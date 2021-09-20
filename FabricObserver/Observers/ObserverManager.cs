﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.TelemetryLib;
using HealthReport = FabricObserver.Observers.Utilities.HealthReport;
using System.Fabric.Description;
using System.Runtime;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private readonly string nodeName;
        private readonly List<ObserverBase> observers;
        private volatile bool shutdownSignaled;
        private readonly CancellationToken token;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private bool disposed;
        private readonly IEnumerable<ObserverBase> serviceCollection;
        private bool isConfigurationUpdateInProgress;
        private DateTime StartDateTime;
        private readonly TimeSpan OperationalTelemetryRunInterval = TimeSpan.FromHours(8);

        // Folks often use their own version numbers. This is for internal diagnostic telemetry.
        private const string InternalVersionNumber = "3.1.17";

        private bool TaskCancelled =>
            linkedSFRuntimeObserverTokenSource?.Token.IsCancellationRequested ?? token.IsCancellationRequested;

        public static FabricClient FabricClientInstance
        {
            get; set;
        }

        private static int ObserverExecutionLoopSleepSeconds
        {
            get; set;
        } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static StatelessServiceContext FabricServiceContext
        {
            get; set;
        }

        private static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        public static bool TelemetryEnabled
        {
            get; set;
        }

        private TimeSpan ObserverExecutionTimeout
        {
            get; set;
        } = TimeSpan.FromMinutes(30);

        private static bool FabricObserverOperationalTelemetryEnabled
        {
            get; set;
        }

        public static bool ObserverWebAppDeployed
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get; set;
        }

        public string ApplicationName
        {
            get; set;
        }

        public static HealthState ObserverFailureHealthStateLevel
        {
            get;
            set;
        } = HealthState.Unknown;

        /// <summary>
        /// This is for observers that support parallelized monitor loops. 
        /// AppObserver, ContainerObserver, FabricSystemObserver.
        /// </summary>
        public static ParallelOptions ParallelOptions
        {
            get; set;
        }

        public static bool EnableConcurrentExecution
        {
            get; set;
        }

        private ObserverHealthReporter HealthReporter
        {
            get;
        }

        private string Fqdn
        {
            get; set;
        }

        private Logger Logger
        {
            get;
        }

        private int MaxArchivedLogFileLifetimeDays
        {
            get;
        }

        private DateTime LastForcedGCDateTime
        {
            get; set;
        }

        private TimeSpan ForcedGCInterval
        {
            get; set;
        } = TimeSpan.FromMinutes(15);

        private DateTime LastTelemetrySendDate
        {
            get; set;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// This is only used by unit tests.
        /// </summary>
        /// <param name="observer">Observer instance.</param>
        /// <param name="fabricClient">FabricClient instance</param>
        public ObserverManager(ObserverBase observer, FabricClient fabricClient)
        {
            cts = new CancellationTokenSource();
            token = cts.Token;
            Logger = new Logger("ObserverManagerSingleObserverRun");
            FabricClientInstance ??= fabricClient;
            HealthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);

            // The unit tests expect file output from some observers.
            ObserverWebAppDeployed = true;

            observers = new List<ObserverBase>(new[]
            {
                observer
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        /// <param name="serviceProvider">IServiceProvider for retrieving service instance.</param>
        /// <param name="fabricClient">FabricClient instance.</param>
        /// <param name="token">Cancellation token.</param>
        public ObserverManager(IServiceProvider serviceProvider, FabricClient fabricClient, CancellationToken token)
        {
            this.token = token;
            cts = new CancellationTokenSource();
            linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, this.token);
            FabricClientInstance = fabricClient;
            FabricServiceContext = serviceProvider.GetRequiredService<StatelessServiceContext>();
            nodeName = FabricServiceContext.NodeContext.NodeName;
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;

            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(ObserverConstants.ObserverLogPathParameter, null);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.MaxArchivedLogFileLifetimeDays, null), out int maxArchivedLogFileLifetimeDays))
            {
                MaxArchivedLogFileLifetimeDays = maxArchivedLogFileLifetimeDays;
            }

            // this logs error/warning/info messages for ObserverManager.
            Logger = new Logger("ObserverManager", logFolderBasePath, MaxArchivedLogFileLifetimeDays > 0 ? MaxArchivedLogFileLifetimeDays : 7);
            SetPropertiesFromConfigurationParameters();
            serviceCollection = serviceProvider.GetServices<ObserverBase>();

            // Populate the Observer list for the sequential run loop.
            int capacity = serviceCollection.Count(o => o.IsEnabled);

            if (capacity > 0)
            {
                observers = new List<ObserverBase>(capacity);
                observers.AddRange(serviceCollection.Where(o => o.IsEnabled));
            }
            else
            {
                Logger.LogWarning("There are no observers enabled. Aborting..");
                return;
            }

            HealthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);

            ParallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = EnableConcurrentExecution && Environment.ProcessorCount >= 4 ? -1 : 1,
                CancellationToken = linkedSFRuntimeObserverTokenSource?.Token ?? token,
                TaskScheduler = TaskScheduler.Default
            };
        }

        public async Task StartObserversAsync()
        {
            try
            {
                StartDateTime = DateTime.UtcNow;

                // Nothing to do here.
                if (observers == null || observers.Count == 0)
                {
                    return;
                }

                // Continue running until a shutdown signal is sent
                Logger.LogInfo("Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
                    {
                        await ShutDownAsync().ConfigureAwait(false);
                        break;
                    }

                    _ = await RunObserversAsync().ConfigureAwait(false);

                    // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
                    if (FabricObserverOperationalTelemetryEnabled && DateTime.UtcNow.Subtract(LastTelemetrySendDate) >= OperationalTelemetryRunInterval)
                    {
                        try
                        {
                            using var telemetryEvents = new TelemetryEvents(
                                                                FabricClientInstance,
                                                                FabricServiceContext,
                                                                ServiceEventSource.Current,
                                                                token,
                                                                EtwEnabled);

                            var foData = GetFabricObserverInternalTelemetryData();

                            if (foData != null)
                            {
                                string filepath = Path.Combine(Logger.LogFolderBasePath, $"fo_operational_telemetry.log");

                                if (telemetryEvents.EmitFabricObserverOperationalEvent(foData, OperationalTelemetryRunInterval, filepath))
                                {
                                    LastTelemetrySendDate = DateTime.UtcNow;
                                    ResetInternalDataCounters();
                                }
                            }
                        }
                        catch
                        {
                            // Telemetry is non-critical and should not take down FO.
                        }
                    }

                    // Force Gen0-Gen2 collection with compaction, including LOH. This runs every 15 minutes.
                    if (DateTime.UtcNow.Subtract(LastForcedGCDateTime) >= ForcedGCInterval)
                    {
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        LastForcedGCDateTime = DateTime.UtcNow;
                    }

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds), token);
                    }
                    else if (observers.Count == 1) // This protects against loop spinning when you run FO with one observer enabled and no sleep time set.
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                    }
                }
            }
            catch (Exception e) when (e is TaskCanceledException || e is OperationCanceledException)
            {
                if (!isConfigurationUpdateInProgress && (shutdownSignaled || token.IsCancellationRequested))
                {
                    await ShutDownAsync().ConfigureAwait(true);
                }
            }
            catch (Exception e)
            {
                var message =
                    $"Unhandled Exception in {ObserverConstants.ObserverManagerName} on node " +
                    $"{nodeName}. Taking down FO process. " +
                    $"Error info:{Environment.NewLine}{e}";

                Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    var telemetryData = new TelemetryData(FabricClientInstance, token)
                    {
                        Description = message,
                        HealthState = "Error",
                        Metric = $"{ObserverConstants.FabricObserverName}_ServiceHealth",
                        NodeName = nodeName,
                        ObserverName = ObserverConstants.ObserverManagerName,
                        Source = ObserverConstants.FabricObserverName
                    };

                    await TelemetryClient.ReportHealthAsync(telemetryData, token);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.LogEtw(
                            ObserverConstants.FabricObserverETWEventName,
                            new
                            {
                                Description = message,
                                HealthState = "Error",
                                Metric = $"{ObserverConstants.FabricObserverName}_ServiceHealth",
                                NodeName = nodeName,
                                ObserverName = ObserverConstants.ObserverManagerName,
                                Source = ObserverConstants.FabricObserverName
                            });
                }

                // Operational telemetry sent to FO developer for use in understanding generic behavior of FO in the real world (no PII)
                if (FabricObserverOperationalTelemetryEnabled)
                {
                    try
                    {
                        using var telemetryEvents = new TelemetryEvents(
                                                            FabricClientInstance,
                                                            FabricServiceContext,
                                                            ServiceEventSource.Current,
                                                            token,
                                                            EtwEnabled);

                        var foData = new FabricObserverCriticalErrorEventData
                        {
                            Source = ObserverConstants.ObserverManagerName,
                            ErrorMessage = e.Message,
                            ErrorStack = e.StackTrace,
                            CrashTime = DateTime.UtcNow.ToString("o"),
                            Version = InternalVersionNumber
                        };

                        string filepath = Path.Combine(Logger.LogFolderBasePath, $"fo_critical_error_telemetry.log");
                        _ = telemetryEvents.EmitFabricObserverCriticalErrorEvent(foData, filepath);
                    }
                    catch
                    {
                        // Telemetry is non-critical and should not take down FO.
                    }
                }

                // Don't swallow the exception.
                // Take down FO process. Fix the bug(s).
                throw;
            }
        }

        private void ResetInternalDataCounters()
        {
            // These props are only set for telemetry purposes. This does not remove err/warn state on an observer.
            foreach (var obs in observers)
            {
                obs.CurrentErrorCount = 0;
                obs.CurrentWarningCount = 0;
            }
        }

        // Clear all existing FO health events during shutdown or update event.
        public async Task StopObserversAsync(bool isShutdownSignaled = true, bool isConfigurationUpdateLinux = false)
        {
            string configUpdateLinux = string.Empty;

            if (isConfigurationUpdateLinux)
            {
                configUpdateLinux =
                    $" Note: This is due to a configuration update which requires an FO process restart on Linux (with UD walk (one by one) and safety checks).{Environment.NewLine}" +
                    "The reason FO needs to be restarted as part of a parameter-only upgrade is due to the Linux Capabilities set FO employs not persisting across application upgrades (by design) " +
                    "even when the upgrade is just a configuration parameter update. In order to re-create the Capabilities set, FO's setup script must be re-run by SF. Restarting FO is therefore required here.";
            }

            // If the node goes down, for example, or the app is gracefully closed, then clear all existing error or health reports supplied by FO.
            foreach (var obs in observers)
            {
                var healthReport = new HealthReport
                {
                    Code = FOErrorWarningCodes.Ok,
                    HealthMessage = $"Clearing existing FabricObserver Health Reports as the service is stopping or updating.{configUpdateLinux}.",
                    State = HealthState.Ok,
                    ReportType = HealthReportType.Application,
                    NodeName = obs.NodeName
                };

                if (obs.AppNames.Count(a => !string.IsNullOrWhiteSpace(a) && a.Contains("fabric:/")) > 0)
                {
                    foreach (var app in obs.AppNames)
                    {
                        try
                        {
                            Uri appName = new Uri(app);
                            var appHealth = await FabricClientInstance.HealthManager.GetApplicationHealthAsync(appName).ConfigureAwait(true);
                            var fabricObserverAppHealthEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                            if (isConfigurationUpdateInProgress)
                            {
                                fabricObserverAppHealthEvents = appHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                                                                                        && s.HealthInformation.HealthState == HealthState.Warning
                                                                                        || s.HealthInformation.HealthState == HealthState.Error);
                            }

                            foreach (var evt in fabricObserverAppHealthEvents)
                            {
                                healthReport.AppName = appName;
                                healthReport.Property = evt.HealthInformation.Property;
                                healthReport.SourceId = evt.HealthInformation.SourceId;

                                var healthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);
                                healthReporter.ReportHealthToServiceFabric(healthReport);

                                await Task.Delay(250).ConfigureAwait(true);
                            }
                        }
                        catch (FabricException)
                        {

                        }

                        await Task.Delay(250).ConfigureAwait(true);
                    }
                }
                else
                {
                    try
                    {
                        var nodeHealth = await FabricClientInstance.HealthManager.GetNodeHealthAsync(obs.NodeName).ConfigureAwait(true);
                        var fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName));

                        if (isConfigurationUpdateInProgress)
                        {
                            fabricObserverNodeHealthEvents = nodeHealth.HealthEvents?.Where(s => s.HealthInformation.SourceId.Contains(obs.ObserverName)
                                                                                      && s.HealthInformation.HealthState == HealthState.Warning
                                                                                      || s.HealthInformation.HealthState == HealthState.Error);
                        }

                        healthReport.ReportType = HealthReportType.Node;

                        foreach (var evt in fabricObserverNodeHealthEvents)
                        {
                            healthReport.Property = evt.HealthInformation.Property;
                            healthReport.SourceId = evt.HealthInformation.SourceId;

                            var healthReporter = new ObserverHealthReporter(Logger, FabricClientInstance);
                            healthReporter.ReportHealthToServiceFabric(healthReport);

                            await Task.Delay(250).ConfigureAwait(true);
                        }

                    }
                    catch (FabricException)
                    {

                    }

                    await Task.Delay(250).ConfigureAwait(true);
                }

                obs.HasActiveFabricErrorOrWarning = false;
            }

            shutdownSignaled = isShutdownSignaled;

            if (!isConfigurationUpdateInProgress)
            {
                SignalAbortToRunningObserver();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                FabricClientInstance?.Dispose();
                FabricClientInstance = null;
                linkedSFRuntimeObserverTokenSource?.Dispose();
                cts?.Dispose();
                FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            }

            disposed = true;
        }

        private static bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps = FabricClientInstance.QueryManager.GetApplicationListAsync(new Uri("fabric:/FabricObserverWebApi")).GetAwaiter().GetResult();
                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {

            }

            return false;
        }

        private static string GetConfigSettingValue(string parameterName, ConfigurationSettings settings)
        {
            try
            {
                ConfigurationSettings configSettings = null;

                if (settings != null)
                {
                    configSettings = settings;
                }
                else
                {
                    configSettings = FabricServiceContext.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;
                }

                var section = configSettings?.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];
                var parameter = section?.Parameters[parameterName];

                return parameter?.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {

            }

            return null;
        }

        private async Task ShutDownAsync()
        {
            await StopObserversAsync().ConfigureAwait(true);

            if (cts != null)
            {
                cts.Dispose();
                cts = null;
            }

            // Flush and Dispose all NLog targets. No more logging.
            Logger.Flush();
            DataTableFileLogger.Flush();
            Logger.ShutDown();
            DataTableFileLogger.ShutDown();
        }

        /// <summary>
        /// This function gets FabricObserver's internal observer operational data for telemetry sent to Microsoft (no PII).
        /// Any data sent to Microsoft is also stored in a file in the observer_logs directory so you can see exactly what gets transmitted.
        /// You can enable/disable this at any time by setting EnableFabricObserverDiagnosticTelemetry to true/false in Settings.xml, ObserverManagerConfiguration section.
        /// </summary>
        private FabricObserverOperationalEventData GetFabricObserverInternalTelemetryData()
        {
            FabricObserverOperationalEventData telemetryData = null;

            try
            {
                // plugins
                bool hasPlugins = false;
                string pluginsDir = Path.Combine(FabricServiceContext.CodePackageActivationContext.GetDataPackageObject("Data").Path, "Plugins");

                if (!Directory.Exists(pluginsDir))
                {
                    hasPlugins = false;
                }
                else
                {
                    try
                    {
                        string[] pluginDlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories);
                        hasPlugins = pluginDlls.Length > 0;
                    }
                    catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is PathTooLongException)
                    {

                    }
                }

                telemetryData = new FabricObserverOperationalEventData
                {
                    UpTime = DateTime.UtcNow.Subtract(StartDateTime).ToString(),
                    Version = InternalVersionNumber,
                    EnabledObserverCount = observers.Count(obs => obs.IsEnabled),
                    HasPlugins = hasPlugins,
                    ParallelExecutionEnabled = EnableConcurrentExecution,
                    ObserverData = GetObserverData(),
                };
            }
            catch (Exception e) when (e is ArgumentException)
            {

            }

            return telemetryData;
        }

        private List<ObserverData> GetObserverData()
        {
            var observerData = new List<ObserverData>();
            var enabledObs = observers.Where(o => o.IsEnabled);
            string[] builtInObservers = new string[]
            {
                ObserverConstants.AppObserverName,
                ObserverConstants.AzureStorageUploadObserverName,
                ObserverConstants.CertificateObserverName,
                ObserverConstants.ContainerObserverName,
                ObserverConstants.DiskObserverName,
                ObserverConstants.FabricSystemObserverName,
                ObserverConstants.NetworkObserverName,
                ObserverConstants.NodeObserverName,
                ObserverConstants.OSObserverName,
                ObserverConstants.SFConfigurationObserverName
            };

            foreach (var obs in enabledObs)
            {
                // We don't need to have any information about plugins besides whether or not there are any.
                if (!builtInObservers.Any(o => o == obs.ObserverName))
                {
                    continue;
                }

                // These built-in (non-plugin) observers monitor apps and/or services.
                if (obs.ObserverName == ObserverConstants.AppObserverName ||
                    obs.ObserverName == ObserverConstants.ContainerObserverName ||
                    obs.ObserverName == ObserverConstants.NetworkObserverName ||
                    obs.ObserverName == ObserverConstants.FabricSystemObserverName)
                {
                    observerData.Add(
                        new AppServiceObserverData
                        {
                            ObserverName = obs.ObserverName,
                            MonitoredAppCount = obs.MonitoredAppCount,
                            MonitoredServiceProcessCount = obs.MonitoredServiceProcessCount,
                            ErrorCount = obs.CurrentErrorCount,
                            WarningCount = obs.CurrentWarningCount
                        });
                }
                else
                {
                    observerData.Add(
                        new ObserverData
                        {
                            ObserverName = obs.ObserverName,
                            ErrorCount = obs.CurrentErrorCount,
                            WarningCount = obs.CurrentWarningCount
                        });
                }
            }

            return observerData;
        }

        /// <summary>
        /// Event handler for application parameter updates (Un-versioned application parameter-only Application Upgrades).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Contains the information necessary for setting new config params from updated package.</param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            Logger.LogWarning("Application Parameter upgrade started...");

            try
            {
                // For Linux, we need to restart the FO process due to the Linux Capabilities impl that enables us to run docker and netstat commands as elevated user (FO Linux should always be run as standard user on Linux).
                // During an upgrade event, SF touches the cap binaries which removes the cap settings so we need to run the FO app setup script again to reset them.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Graceful stop.
                    await StopObserversAsync(true, true).ConfigureAwait(true);

                    // Bye.
                    Environment.Exit(42);
                }

                isConfigurationUpdateInProgress = true;
                await StopObserversAsync(false).ConfigureAwait(true);
                observers.Clear();

                // ObserverManager settings?
                this.SetPropertiesFromConfigurationParameters(e.NewPackage.Settings);

                foreach (var observer in serviceCollection)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    observer.ConfigurationSettings = new ConfigSettings(e.NewPackage.Settings, $"{observer.ObserverName}Configuration");

                    if (!observer.ConfigurationSettings.IsEnabled)
                    {
                        continue;
                    }

                    // The ObserverLogger instance (member of each observer type) checks its EnableVerboseLogging setting before writing Info events (it won't write if this setting is false, thus non-verbose).
                    // So, we set it here in case the parameter update includes a change to this config setting. 
                    if (e.NewPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters.Contains(ObserverConstants.EnableVerboseLoggingParameter)
                        && e.OldPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters.Contains(ObserverConstants.EnableVerboseLoggingParameter))
                    {
                        string newLoggingSetting = e.NewPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter].Value.ToLower();
                        string oldLoggingSetting = e.OldPackage.Settings.Sections[$"{observer.ObserverName}Configuration"].Parameters[ObserverConstants.EnableVerboseLoggingParameter].Value.ToLower();

                        if (newLoggingSetting != oldLoggingSetting)
                        {
                            observer.ObserverLogger.EnableVerboseLogging = observer.ConfigurationSettings.EnableVerboseLogging;
                        }
                    }

                    observers.Add(observer);
                }

                cts ??= new CancellationTokenSource();
                linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, token);
            }
            catch (Exception err)
            {
                var healthReport = new HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FOErrorWarningCodes.Ok,
                    ReportType = HealthReportType.Application,
                    HealthMessage = $"Error updating FabricObserver with new configuration settings:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = "Configuration_Upate_Error",
                    EmitLogEvent = true
                };

                HealthReporter.ReportHealthToServiceFabric(healthReport);
            }

            isConfigurationUpdateInProgress = false;
            Logger.LogWarning("Application Parameter upgrade completed...");
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml or ApplicationManifest.xml (Overrides)
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertiesFromConfigurationParameters(ConfigurationSettings settings = null)
        {
            ApplicationName = FabricServiceContext.CodePackageActivationContext.ApplicationName;
            
            // Parallelization settings for capable hardware. \\

            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableConcurrentExecution, settings), out bool enableConcurrency))
            {
                EnableConcurrentExecution = enableConcurrency;
            }

            ParallelOptions = new ParallelOptions
            {
                // Parallelism only makes sense for capable CPU configurations. The minimum requirement is 4 logical processors; which would map to more than 1 available core.
                MaxDegreeOfParallelism = EnableConcurrentExecution && Environment.ProcessorCount >= 4 ? -1 : 1,
                CancellationToken = linkedSFRuntimeObserverTokenSource?.Token ?? token,
                TaskScheduler = TaskScheduler.Default
            };

            // ETW - Overridable
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableETWProvider, settings), out bool etwEnabled))
            {
                EtwEnabled = etwEnabled;
            }

            // Maximum time, in seconds, that an observer can run - Override.
            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout, settings), out int timeoutSeconds))
            {
                ObserverExecutionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            // ObserverManager verbose logging - Override.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter, settings), out bool enableVerboseLogging))
            {
                Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds, settings), out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;
            }

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed.
            string fqdn = GetConfigSettingValue(ObserverConstants.Fqdn, settings);

            if (!string.IsNullOrEmpty(fqdn))
            {
                Fqdn = fqdn;
            }

            // FabricObserver runtime telemetry (No PII) - Override
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableFabricObserverOperationalTelemetry, settings), out bool foTelemEnabled))
            {
                FabricObserverOperationalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverWebApi.
            ObserverWebAppDeployed = bool.TryParse(GetConfigSettingValue(ObserverConstants.ObserverWebApiEnabled, settings), out bool obsWeb) && obsWeb && IsObserverWebApiAppInstalled();

            // ObserverFailure HealthState Level - Override \\

            string state = GetConfigSettingValue(ObserverConstants.ObserverFailureHealthStateLevelParameter, settings);

            if (string.IsNullOrWhiteSpace(state) || state?.ToLower() == "none")
            {
                ObserverFailureHealthStateLevel = HealthState.Unknown;
            }
            else if (Enum.TryParse(state, out HealthState healthState))
            {
                ObserverFailureHealthStateLevel = healthState;
            }

            // Telemetry (AppInsights, LogAnalytics, etc) - Override
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled, settings), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (!TelemetryEnabled)
            {
                return;
            }

            string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType, settings);

            if (string.IsNullOrEmpty(telemetryProviderType))
            {
                TelemetryEnabled = false;

                return;
            }

            if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
            {
                TelemetryEnabled = false;

                return;
            }

            switch (telemetryProvider)
            {
                case TelemetryProviderType.AzureLogAnalytics:
                    {
                        string logAnalyticsLogType = GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter, settings);
                        string logAnalyticsSharedKey = GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter, settings);
                        string logAnalyticsWorkspaceId = GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter, settings);

                        if (string.IsNullOrEmpty(logAnalyticsWorkspaceId) || string.IsNullOrEmpty(logAnalyticsSharedKey))
                        {
                            TelemetryEnabled = false;
                            return;
                        }

                        TelemetryClient = new LogAnalyticsTelemetry(
                                                logAnalyticsWorkspaceId,
                                                logAnalyticsSharedKey,
                                                logAnalyticsLogType,
                                                FabricClientInstance,
                                                token);

                        break;
                    }

                case TelemetryProviderType.AzureApplicationInsights:
                    {
                        string aiKey = GetConfigSettingValue(ObserverConstants.AiKey, settings);

                        if (string.IsNullOrEmpty(aiKey))
                        {
                            TelemetryEnabled = false;
                            return;
                        }

                        TelemetryClient = new AppInsightsTelemetry(aiKey);
                        break;
                    }

                default:
                    TelemetryEnabled = false;
                    break;
            }
        }


        /// <summary>
        /// This function will signal cancellation on the token passed to an observer's ObserveAsync. 
        /// This will eventually cause the observer to stop processing as this will throw an OperationCancelledException 
        /// in one of the observer's executing code paths.
        /// </summary>
        private void SignalAbortToRunningObserver()
        {
            Logger.LogInfo("Signalling task cancellation to currently running Observer.");

            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {

            }

            Logger.LogInfo("Successfully signaled cancellation to currently running Observer.");
        }

        /// <summary>
        /// Runs all observers in a sequential loop.
        /// </summary>
        /// <returns>A boolean value indicating success of a complete observer loop run.</returns>
        private async Task<bool> RunObserversAsync()
        {
            var exceptionBuilder = new StringBuilder();
            bool allExecuted = true;

            for (int i = 0; i < observers.Count; ++i)
            {
                var observer = observers[i];

                if (isConfigurationUpdateInProgress)
                {
                    return true;
                }

                try
                {
                    if (TaskCancelled || shutdownSignaled)
                    {
                        return true;
                    }

                    // Is it healthy?
                    if (observer.IsUnhealthy)
                    {
                        continue;
                    }

                    Logger.LogInfo($"Starting {observer.ObserverName}");

                    // Synchronous call.
                    bool isCompleted = observer.ObserveAsync(linkedSFRuntimeObserverTokenSource?.Token ?? token).Wait(ObserverExecutionTimeout);

                    // The observer is taking too long (hung?), move on to next observer.
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    if (!isCompleted && !(TaskCancelled || shutdownSignaled))
                    {
                        string observerHealthWarning = $"{observer.ObserverName} on node {nodeName} has exceeded its specified Maximum run time of {ObserverExecutionTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Please look into it.";

                        Logger.LogError(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        // Telemetry.
                        if (TelemetryEnabled)
                        {
                            var telemetryData = new TelemetryData(FabricClientInstance, token)
                            {
                                Description = observerHealthWarning,
                                HealthState = "Error",
                                Metric = $"{observer.ObserverName}_HealthState",
                                NodeName = nodeName,
                                ObserverName = ObserverConstants.ObserverManagerName,
                                Source = ObserverConstants.FabricObserverName
                            };

                            await TelemetryClient?.ReportHealthAsync(telemetryData, token);
                        }

                        // ETW.
                        if (EtwEnabled)
                        {
                            Logger.LogEtw(
                                    ObserverConstants.FabricObserverETWEventName,
                                    new
                                    {
                                        Description = observerHealthWarning,
                                        HealthState = "Error",
                                        Metric = $"{observer.ObserverName}_HealthState",
                                        NodeName = nodeName,
                                        ObserverName = ObserverConstants.ObserverManagerName,
                                        Source = ObserverConstants.FabricObserverName
                                    });
                        }

                        // Put FO into Warning or Error (health state is configurable in Settings.xml)
                        if (ObserverFailureHealthStateLevel != HealthState.Unknown)
                        {
                            var healthReport = new HealthReport
                            {
                                AppName = new Uri($"fabric:/{ObserverConstants.FabricObserverName}"),
                                EmitLogEvent = false,
                                HealthMessage = observerHealthWarning,
                                HealthReportTimeToLive = TimeSpan.MaxValue,
                                Property = $"{observer.ObserverName}_HealthState",
                                ReportType = HealthReportType.Application,
                                State = ObserverFailureHealthStateLevel,
                                NodeName = this.nodeName,
                                Observer = ObserverConstants.ObserverManagerName,
                            };

                            // Generate a Service Fabric Health Report.
                            HealthReporter.ReportHealthToServiceFabric(healthReport);
                        }

                        continue;
                    }

                    Logger.LogInfo($"Successfully ran {observer.ObserverName}.");

                    if (!ObserverWebAppDeployed)
                    {
                        continue;
                    }

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        var errWarnMsg = !string.IsNullOrEmpty(Fqdn) ? $"<a style=\"font-weight: bold; color: red;\" href=\"http://{Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>." : $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";
                        Logger.LogWarning($"{observer.ObserverName}: " + errWarnMsg);
                    }
                    else
                    {
                        // Delete the observer's instance log (local file with Warn/Error details per run)..
                        _ = observer.ObserverLogger.TryDeleteInstanceLogFile();

                        try
                        {
                            if (File.Exists(Logger.FilePath))
                            {
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s).
                                await File.WriteAllLinesAsync(
                                            Logger.FilePath,
                                            File.ReadLines(Logger.FilePath)
                                                .Where(line => !line.Contains(observer.ObserverName)).ToList(), token);
                            }
                        }
                        catch (IOException)
                        {

                        }
                    }
                }
                catch (AggregateException ex)
                {
                    if (ex.InnerException is FabricException ||
                        ex.InnerException is OperationCanceledException ||
                        ex.InnerException is TaskCanceledException)
                    {
                        if (isConfigurationUpdateInProgress)
                        {
                            return true;
                        }

                        continue;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled AggregateException from {observer.ObserverName}:{Environment.NewLine}{ex.InnerException}");
                    allExecuted = false;
                }
                catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TaskCanceledException || e is TimeoutException)
                {
                    if (isConfigurationUpdateInProgress)
                    {
                        return true;
                    }

                    _ = exceptionBuilder.AppendLine($"Handled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    allExecuted = false;
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Unhandled Exception from {observer.ObserverName}:{Environment.NewLine}{e}");
                    allExecuted = false;
                }
            }

            if (allExecuted)
            {
                Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                Logger.LogWarning(exceptionBuilder.ToString());
                _ = exceptionBuilder.Clear();
            }

            return allExecuted;
        }
    }
}
