using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Plugins;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service;

public class EngineWorker : BackgroundService
{
    private readonly IPacketInterceptor _interceptor;
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly Func<CancellationToken, Task> _runControlPipe;
    private readonly Func<bool> _isAdmin;
    private readonly BandwidthAlertTracker _alertTracker;
    private readonly PluginManager _pluginManager;
    private readonly ControlPlaneState _controlPlane;
    private readonly DriverUnloader _driverUnloader;
    private readonly ILogger<EngineWorker> _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>One engine start, pause, resume or shutdown at a time.</summary>
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private volatile bool _paused;
    private volatile bool _stayPaused;
    private volatile string? _driverLoadedReason;

    /// <summary>
    /// Guards writes to <see cref="_driverLoadedReason"/>. Each write bumps the version, so a
    /// status read that rechecked the driver can't put back a reason a pause or resume replaced.
    /// </summary>
    private readonly object _driverReasonLock = new();
    private int _driverReasonVersion;

    /// <summary>Set while a pause unloads the driver or a resume reopens it, so status reads don't recheck it mid-change.</summary>
    private volatile bool _changingDriver;

    /// <summary>The engine error a refused resume left. It goes once the driver unloads.</summary>
    private volatile string? _resumeRefusal;

    internal const string UnloadingReason = "The WinDivert driver is unloading.";

    /// <summary>Tests hold the boot start here so a pause or resume can get in first.</summary>
    internal Task? BootStartHold { get; set; }
    private bool _backgroundStarted;
    private RuleReconciler? _reconciler;
    private QuotaTracker? _quotaTracker;
    private TrafficStatsDb? _statsDb;
    private Timer? _statsTimer;
    private Timer? _purgeTimer;
    private Timer? _flowPurgeTimer;
    private Timer? _quotaTimer;
    private Timer? _quotaResetTimer;
    private Timer? _alertTimer;
    private Timer? _scheduleTimer;
    private long _lastQuotaResetCheckTicks = DateTime.Now.Date.Ticks;
    private volatile string? _engineError;
    private volatile bool _engineStarted;
    private volatile bool _stopping;
    private bool _stateLoaded;
    private bool _prerequisitesMet;
    private readonly CancellationTokenSource _shutdown = new();

    private static readonly string DefaultDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit");

    private readonly string _dataDir;
    private readonly string _rulesPath;
    private readonly string _alertsPath;
    private readonly string _statsDbPath;
    private readonly string _lastErrorPath;

    /// <summary>There while the user wants the engine to stay paused after a restart.</summary>
    private readonly string _stayPausedPath;

    public EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        IFlowTracker flowTracker,
        ITrafficMonitor trafficMonitor,
        PipeServer pipeServer,
        BandwidthAlertTracker alertTracker,
        PluginManager pluginManager,
        ControlPlaneState controlPlane,
        DriverUnloader driverUnloader,
        ILogger<EngineWorker> logger)
        : this(interceptor, ruleEngine, rateLimiter, flowTracker, trafficMonitor, pipeServer.StartAsync,
            alertTracker, pluginManager, controlPlane, logger, DefaultDataDir, IsRunningAsAdmin, driverUnloader)
    {
    }

    internal EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        IFlowTracker flowTracker,
        ITrafficMonitor trafficMonitor,
        Func<CancellationToken, Task> runControlPipe,
        BandwidthAlertTracker alertTracker,
        PluginManager pluginManager,
        ControlPlaneState controlPlane,
        ILogger<EngineWorker> logger,
        string dataDir,
        Func<bool> isAdmin,
        DriverUnloader driverUnloader)
    {
        _interceptor = interceptor;
        _driverUnloader = driverUnloader;
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _trafficMonitor = trafficMonitor;
        _runControlPipe = runControlPipe;
        _alertTracker = alertTracker;
        _pluginManager = pluginManager;
        _controlPlane = controlPlane;
        _logger = logger;
        _isAdmin = isAdmin;
        _dataDir = dataDir;
        _rulesPath = Path.Combine(dataDir, "rules.json");
        _alertsPath = Path.Combine(dataDir, "alerts.json");
        _statsDbPath = Path.Combine(dataDir, "traffic.db");
        _lastErrorPath = Path.Combine(dataDir, "last-error.txt");
        _stayPausedPath = Path.Combine(dataDir, "paused");
    }

    // Since .NET 10 the host runs ExecuteAsync on a thread-pool thread and starts the next
    // hosted service (the REST API) right away, so the saved rules load here, before
    // base.StartAsync returns, or the API and pipe could answer with an empty rule set.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");
        _controlPlane.DiagnosticProvider = GetDiagnosticInfo;

        _prerequisitesMet = ValidatePrerequisites();
        if (_prerequisitesMet)
        {
            ClearLastError();
            EnsureDataDirectory();
            LoadState(_shutdown.Token);
        }
        else
        {
            _logger.LogCritical("Prerequisite validation failed; the packet engine will not start");
        }

        _controlPlane.QuotaTracker = _quotaTracker;
        _controlPlane.ConnectionLogProvider = () => _interceptor.GetRecentConnectionLog(100);
        _controlPlane.PauseHandler = PauseAsync;
        _controlPlane.ResumeHandler = ResumeAsync;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        // Safe on a second Dispose: a disposed source still reports the cancellation it had.
        if (!_shutdown.IsCancellationRequested)
            _shutdown.Cancel();
        base.Dispose();
        _shutdown.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_prerequisitesMet)
        {
            if (BootStartHold is { } hold)
                await hold;
            await StartEngineAsync(stoppingToken);
        }

        // The control pipe stays up even when the engine can't start. Without it the app
        // could only report "Service not running" and never the reason.
        _ = Task.Run(() => RunPipeServer(stoppingToken), stoppingToken);
        _logger.LogInformation("IPC pipe server started");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OpenNetLimit engine stopping");
        }

        await ShutdownGracefully();
    }

    private void LoadState(CancellationToken stoppingToken)
    {
        _quotaTracker = new QuotaTracker(_ruleEngine, _trafficMonitor);
        _reconciler = new RuleReconciler(_ruleEngine, _rateLimiter, _flowTracker, _quotaTracker);
        if (_ruleEngine is RuleEngine concreteEngine)
        {
            concreteEngine.OnRulesChanged += () =>
            {
                try { _reconciler.ReconcileAfterRuleChange(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Rule reconciliation failed"); }
            };
        }

        _quotaTracker.OnQuotaWarning += (name, state) =>
        {
            _logger.LogWarning("Quota warning for {Process}: {Percent}% used ({Used}/{Limit} bytes)",
                name, state.PercentUsed, state.UsedBytes, state.LimitBytes);
            DispatchPluginEvent("quota.warning", state, stoppingToken);
        };
        _quotaTracker.OnQuotaExceeded += (name, state) =>
        {
            _logger.LogWarning("Quota exceeded for {Process}: {Used}/{Limit} bytes — action: {Action}",
                name, state.UsedBytes, state.LimitBytes, state.Action);
            DispatchPluginEvent("quota.exceeded", state, stoppingToken);
            // The reconciler enforces the Block or Throttle action right after this Update, for
            // every matching process and until the period resets (UpdateQuotas).
        };
        _alertTracker.OnAlert += alert =>
        {
            _logger.LogWarning("Bandwidth alert: {Message}", alert.Message);
            DispatchPluginEvent("alert.triggered", alert, stoppingToken);
        };

        LoadRules();
        LoadAlerts();
        LoadPlugins();
        StartQuotaCounts();
        _reconciler.Reconcile();
        _stateLoaded = true;
    }

    private async Task<bool> TryStartInterceptorAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _interceptor.StartAsync(stoppingToken);
            _engineStarted = true;
            _logger.LogInformation("Packet interceptor started");
            CheckWinDivertDriverSignature();
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped while the start waited on another program's driver load. Not a failure.
            _logger.LogInformation("The service stopped before the packet engine finished starting");
            return false;
        }
        catch (Exception ex)
        {
            _engineError = DescribeStartFailure(ex);
            var hint = $"Failed to start packet interceptor: {_engineError}\n" +
                       "Other things to check:\n" +
                       "  - WinDivert driver not found or inaccessible\n" +
                       "  - HVCI (Memory Integrity) is enabled and blocking the driver\n" +
                       "  - Antivirus/EDR is blocking WinDivert64.sys\n" +
                       "  - Windows kernel driver signing policy may reject cross-signed drivers (Windows 11 24H2+)\n" +
                       "  - Service is not running with administrator privileges\n" +
                       "See: https://github.com/basil00/WinDivert/issues/397\n" +
                       $"Error: {ex.Message}";
            RecordLastError(hint);
            _logger.LogCritical(ex, "Failed to start packet interceptor; check {ErrorFile} for troubleshooting steps", _lastErrorPath);
            return false;
        }
    }

    /// <summary>
    /// Starts the engine at boot, unless the user paused it and asked it to stay paused. A
    /// pause asked for before this runs (the REST API is already up) also keeps it stopped.
    /// </summary>
    private async Task StartEngineAsync(CancellationToken stoppingToken)
    {
        await _engineGate.WaitAsync(CancellationToken.None);
        try
        {
            // A resume that got in first has started everything already, even if it couldn't
            // remove the stay-paused marker.
            if (_backgroundStarted)
                return;
            if (File.Exists(_stayPausedPath))
            {
                MarkDriverChanging();
                _paused = _stayPaused = true;
                _logger.LogInformation("The packet engine stays paused, as asked before the restart");
                // A stop from before the restart can still be waiting on another program, or
                // something loaded the driver since.
                UnloadDriver();
            }
            if (_paused || await TryStartInterceptorAsync(stoppingToken))
                StartBackgroundWork();
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>
    /// Stops filtering packets and unloads the WinDivert driver, so a kernel anti-cheat that
    /// refuses to run beside a packet driver lets the game start.
    /// </summary>
    /// <param name="stayPaused">Keep it paused after the service or Windows restarts.</param>
    internal async Task<DiagnosticInfo> PauseAsync(bool stayPaused, CancellationToken cancellationToken)
    {
        if (!_prerequisitesMet)
            return GetDiagnosticInfo();
        await _engineGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
                return GetDiagnosticInfo();

            // Before it counts as paused: a status read while our own handles close mustn't
            // look for the program holding the driver and blame another one.
            MarkDriverChanging();
            try
            {
                _paused = true;
                _engineError = null;
                try
                {
                    await _interceptor.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping the packet interceptor for a pause");
                }
                _stayPaused = SetStayPausedMarker(stayPaused);
                _logger.LogInformation("Packet engine paused");
                UnloadDriver();
            }
            finally
            {
                _changingDriver = false;
            }
        }
        finally
        {
            _engineGate.Release();
        }
        return GetDiagnosticInfo();
    }

    /// <summary>
    /// Until the driver change finishes, status reads say it's unloading, or keep the reason they
    /// had, and never recheck it. Call it holding the engine gate.
    /// </summary>
    private void MarkDriverChanging()
    {
        lock (_driverReasonLock)
        {
            _driverLoadedReason ??= UnloadingReason;
            _driverReasonVersion++;
        }
        _changingDriver = true;
    }

    private void SetDriverReason(string? reason)
    {
        lock (_driverReasonLock)
        {
            _driverLoadedReason = reason;
            _driverReasonVersion++;
        }
    }

    /// <summary>
    /// Stops the driver and records why it's still loaded, if it is. Until it knows, a status
    /// read keeps the reason it had, or says the driver is unloading, never that it's unloaded.
    /// Call it holding the engine gate.
    /// </summary>
    private void UnloadDriver()
    {
        MarkDriverChanging();
        try
        {
            // Bounded by DriverUnloader.UnloadLimit. Only a service stop cuts it short, so a
            // client that hangs up mid-pause doesn't leave the reason unknown.
            var reason = _driverUnloader.Unload(_shutdown.Token);
            SetDriverReason(reason);
            if (reason is null)
                _logger.LogInformation("The WinDivert driver is unloaded");
            else
                _logger.LogWarning("The WinDivert driver is still loaded: {Reason}", reason);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            _logger.LogInformation("The service stopped before the WinDivert driver finished unloading");
        }
        finally
        {
            _changingDriver = false;
        }
    }

    /// <summary>
    /// Starts the engine again after a pause. It also retries an engine that failed to start
    /// or stopped after errors, which is what the error messages ask the user to do.
    /// </summary>
    internal async Task<DiagnosticInfo> ResumeAsync(CancellationToken cancellationToken)
    {
        if (!_prerequisitesMet)
            return GetDiagnosticInfo();
        await _engineGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping || (!_paused && _interceptor.IsRunning))
                return GetDiagnosticInfo();

            bool wasPaused = _paused;
            // One state query first; only a pending stop is worth listing the programs for.
            if (wasPaused && _driverUnloader.IsStopPending() &&
                _driverUnloader.WhyLoaded(out bool stopPending) is { } held && stopPending)
            {
                // Opening now would wait out the preflight's pending limit, then fail with error 433.
                SetDriverReason(held);
                var refusal = $"It can't resume until the driver finishes unloading. {held}";
                _resumeRefusal = refusal;
                _engineError = refusal;
                _logger.LogWarning("Resume refused while the WinDivert driver is still unloading: {Reason}", held);
                return GetDiagnosticInfo();
            }

            _engineError = null;
            _resumeRefusal = null;
            _changingDriver = true;
            try
            {
                try
                {
                    // Closes whatever an engine that stopped after errors left open.
                    await _interceptor.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing the packet interceptor before a resume");
                }

                // Still paused while it starts, so a status read meanwhile isn't an error.
                if (await TryStartInterceptorAsync(_shutdown.Token))
                {
                    _paused = false;
                    SetDriverReason(null);
                    _stayPaused = SetStayPausedMarker(false);
                    if (_stayPaused)
                        _logger.LogError("The engine resumed, but {Path} couldn't be removed, so it comes up paused after a restart", _stayPausedPath);
                    ClearLastError();
                    if (!_backgroundStarted)
                        StartBackgroundWork();
                    _logger.LogInformation("Packet engine resumed");
                }
                else
                {
                    // A paused engine stays paused, stay-paused choice and all, so the tray still
                    // offers Resume. The status says why it didn't start.
                    _paused = wasPaused;
                    // A start that got as far as opening a handle loaded the driver, and closing
                    // the handle doesn't unload it.
                    if (wasPaused)
                        UnloadDriver();
                    else
                        SetDriverReason(null);
                }
            }
            finally
            {
                _changingDriver = false;
            }
        }
        finally
        {
            _engineGate.Release();
        }
        return GetDiagnosticInfo();
    }

    /// <summary>
    /// Writes or removes the stay-paused marker. Returns whether the marker is there afterwards,
    /// so the status shows what the next start will do even when the change failed.
    /// </summary>
    private bool SetStayPausedMarker(bool stayPaused)
    {
        try
        {
            if (stayPaused)
            {
                EnsureDataDirectory();
                File.WriteAllText(_stayPausedPath, DateTime.UtcNow.ToString("O"));
            }
            else if (File.Exists(_stayPausedPath))
            {
                // A read-only marker would otherwise refuse to go.
                File.SetAttributes(_stayPausedPath, FileAttributes.Normal);
                File.Delete(_stayPausedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not change whether the engine stays paused at {Path}", _stayPausedPath);
        }
        return File.Exists(_stayPausedPath);
    }

    /// <summary>One line the app can show when the packet engine can't start.</summary>
    internal static string DescribeStartFailure(Exception ex) => ex switch
    {
        WinDivertOpenException open => $"{open.Reason} (Windows error {open.NativeErrorCode})",
        DllNotFoundException => "WinDivert.dll is missing next to the service. Reinstall OpenNetLimit.",
        _ => ex.Message
    };

    private void StartBackgroundWork()
    {
        _backgroundStarted = true;
        try
        {
            _statsDb = new TrafficStatsDb(_statsDbPath);
            _statsTimer = new Timer(_ => RecordStats(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _purgeTimer = new Timer(_ => _statsDb.PurgeOlderThan(90), null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
            _controlPlane.StatsProvider = _statsDb;
            _logger.LogInformation("Traffic statistics database initialized at {Path}", _statsDbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize stats database; statistics will be unavailable");
        }

        _flowPurgeTimer = new Timer(_ => PurgeStaleFlows(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _quotaTimer = new Timer(_ => UpdateQuotas(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _scheduleTimer = new Timer(_ => OnScheduleTick(), null, UntilNextMinute(DateTime.Now), Timeout.InfiniteTimeSpan);
        _quotaResetTimer = new Timer(_ => CheckQuotaResets(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _alertTimer = new Timer(_ => _alertTracker.Update(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private bool ValidatePrerequisites()
    {
        if (_isAdmin())
            return true;

        _engineError = WinDivertOpenException.DescribeError(5);
        _logger.LogError("OpenNetLimit requires administrator privileges to load the WinDivert driver");
        RecordLastError("Service not running as administrator");
        return false;
    }

    private void CheckWinDivertDriverSignature()
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(typeof(SharpDivert.WinDivert).Assembly.Location);
            if (assemblyDir is null) return;

            var driverPath = Path.Combine(assemblyDir, "WinDivert64.sys");
            if (!File.Exists(driverPath))
                driverPath = Path.Combine(assemblyDir, "WinDivert.sys");
            if (!File.Exists(driverPath))
            {
                _logger.LogWarning("WinDivert driver binary not found for signature check");
                return;
            }

            try
            {
                // X509CertificateLoader replaces the byte and file loaders, but nothing in the BCL
                // reads the Authenticode signer out of a .sys file, so this obsolete call stays.
#pragma warning disable SYSLIB0057
                using var baseCert = X509Certificate.CreateFromSignedFile(driverPath);
#pragma warning restore SYSLIB0057
                using var cert = new X509Certificate2(baseCert);
                if (cert.NotAfter < DateTime.Now)
                {
                    _logger.LogWarning(
                        "WinDivert driver signature EXPIRED on {ExpiryDate}. " +
                        "Windows 11 24H2+ may reject this driver under the cross-signed driver deprecation policy. " +
                        "See: https://github.com/basil00/WinDivert/issues/397",
                        cert.NotAfter);
                }
                else
                {
                    _logger.LogInformation("WinDivert driver signature valid until {ExpiryDate}", cert.NotAfter);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not verify WinDivert driver signature. " +
                    "If the driver is unsigned or has an expired certificate, " +
                    "it may be blocked on Windows 11 24H2+. " +
                    "See: https://github.com/basil00/WinDivert/issues/397");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WinDivert signature check skipped");
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data directory {DataDir}", _dataDir);
        }
    }

    private void LoadRules()
    {
        try
        {
            _ruleEngine.LoadRules(_rulesPath);
            _logger.LogInformation("Loaded {Count} rules from {Path}",
                _ruleEngine.GetAllRules().Count, _rulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load rules from {Path}; starting with empty rule set", _rulesPath);
            RecordLastError($"Failed to load rules: {ex.Message}");
        }
    }

    private void SaveRules()
    {
        try
        {
            _ruleEngine.SaveRules(_rulesPath);
            _logger.LogInformation("Saved rules to {Path}", _rulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save rules to {Path}", _rulesPath);
        }
    }

    private void LoadAlerts()
    {
        try
        {
            _alertTracker.LoadRules(_alertsPath);
            _logger.LogInformation("Loaded {Count} alert rules from {Path}",
                _alertTracker.GetRules().Count, _alertsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load alert rules from {Path}; starting with empty alert set", _alertsPath);
            RecordLastError($"Failed to load alert rules: {ex.Message}");
        }
    }

    private void SaveAlerts()
    {
        try
        {
            _alertTracker.SaveRules(_alertsPath);
            _logger.LogInformation("Saved alert rules to {Path}", _alertsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save alert rules to {Path}", _alertsPath);
        }
    }

    private void LoadPlugins()
    {
        try
        {
            var plugins = _pluginManager.Reload();
            _logger.LogInformation("Loaded {Count} plugins", plugins.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugins");
            RecordLastError($"Failed to load plugins: {ex.Message}");
        }
    }

    /// <summary>
    /// The first quota update sets where the saved quotas start counting. Run at load, a quota
    /// added before the 30 s timer's first tick counts from when it was added, like any later
    /// one. It runs after the plugins load so an alert it raises reaches them.
    /// </summary>
    private void StartQuotaCounts()
    {
        try
        {
            _quotaTracker?.Update();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The first quota update failed; quotas count from the next one");
            RecordLastError($"Failed to start quota counts: {ex.Message}");
        }
    }

    private void DispatchPluginEvent(string eventType, object payload, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await _pluginManager.DispatchAsync(eventType, payload, ct); }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Reads quota usage, then enforces a quota that just ran out (or lifts one whose limit was
    /// raised) without waiting for a rule edit.
    /// </summary>
    private void UpdateQuotas()
    {
        try
        {
            _quotaTracker?.Update();
            _reconciler?.ReconcileIfChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota update failed");
        }
    }

    /// <summary>
    /// Runs just after each minute starts, which is when schedule windows open and close, so a
    /// scheduled rule switches on or off on time instead of at the next rule edit.
    /// </summary>
    private void OnScheduleTick()
    {
        try
        {
            if (_reconciler?.ReconcileIfChanged() == true)
                _logger.LogInformation("Rules reapplied: a schedule or quota changed which rules are in force");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled rule reconciliation failed");
        }

        try
        {
            _scheduleTimer?.Change(UntilNextMinute(DateTime.Now), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The engine is stopping.
        }
    }

    /// <summary>How long until a quarter second past the start of the next minute.</summary>
    internal static TimeSpan UntilNextMinute(DateTime now)
    {
        var thisMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        return thisMinute.AddMinutes(1) - now + TimeSpan.FromMilliseconds(250);
    }

    private void CheckQuotaResets()
    {
        try
        {
            var now = DateTime.Now;
            var today = now.Date;

            var lastCheck = new DateTime(Interlocked.Read(ref _lastQuotaResetCheckTicks));
            if (today <= lastCheck)
                return;

            _quotaTracker?.ResetPeriod(OpenNetLimit.Core.Models.QuotaPeriod.Daily);
            _logger.LogInformation("Daily quota period reset");

            if (today.DayOfWeek == DayOfWeek.Monday)
            {
                _quotaTracker?.ResetPeriod(OpenNetLimit.Core.Models.QuotaPeriod.Weekly);
                _logger.LogInformation("Weekly quota period reset");
            }

            if (today.Day == 1)
            {
                _quotaTracker?.ResetPeriod(OpenNetLimit.Core.Models.QuotaPeriod.Monthly);
                _logger.LogInformation("Monthly quota period reset");
            }

            // Revert any quota-imposed rate limits by reconciling with base rules
            _reconciler?.Reconcile();

            Interlocked.Exchange(ref _lastQuotaResetCheckTicks, today.Ticks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check quota resets");
        }
    }

    private void PurgeStaleFlows()
    {
        try
        {
            _flowTracker.PurgeStale(TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to purge stale flows");
        }
    }

    private void RecordStats()
    {
        try
        {
            var usage = _trafficMonitor.TakeUsage();
            _statsDb?.RecordSnapshot(usage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record traffic statistics");
        }
    }

    private async Task ShutdownGracefully()
    {
        _stopping = true;
        // Lets a pause or resume in progress finish first. Waiting ones see _stopping and return.
        await _engineGate.WaitAsync(CancellationToken.None);
        _quotaTimer?.Dispose();
        _scheduleTimer?.Dispose();
        _quotaResetTimer?.Dispose();
        _alertTimer?.Dispose();
        _statsTimer?.Dispose();
        _purgeTimer?.Dispose();
        _flowPurgeTimer?.Dispose();

        try
        {
            await _interceptor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping packet interceptor during shutdown");
        }

        // Without a loaded state there is nothing to save, and saving the empty rule set
        // would overwrite the user's rules.json.
        if (_stateLoaded)
        {
            SaveRules();
            SaveAlerts();
        }
        _statsDb?.Dispose();
        _engineGate.Release();
        _logger.LogInformation("OpenNetLimit engine stopped");
    }

    private async Task RunPipeServer(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _runControlPipe(ct);
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay = Math.Min(1000 * (1 << Math.Min(consecutiveFailures - 1, 5)), 30_000);
                _logger.LogError(ex, "IPC pipe server crashed (attempt {Attempt}), restarting in {Delay}ms",
                    consecutiveFailures, delay);
                RecordLastError($"IPC pipe server crashed: {ex.Message}");
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal DiagnosticInfo GetDiagnosticInfo()
    {
        // Read first. A driver change marks itself before it touches the reason or the pause,
        // so a read that sees no change in progress here rechecks the real driver state.
        bool changingDriver = _changingDriver;
        bool paused = _paused;
        string? driverLoadedReason = paused && !changingDriver ? RecheckDriver() : _driverLoadedReason;

        var running = _interceptor.IsRunning;
        var engineError = _engineError;
        if (engineError is null && _engineStarted && !running && !_stopping && !paused)
            engineError = "The packet engine stopped after repeated errors. Restart the OpenNetLimit service.";

        return new DiagnosticInfo
        {
            Running = running,
            EngineError = engineError,
            Paused = paused,
            // Not only while paused: a resume that couldn't clear the marker comes up paused
            // after the next restart, and the app says so.
            StayPaused = _stayPaused,
            DriverLoadedReason = paused ? driverLoadedReason : null,
            ActiveFlows = _flowTracker.GetActiveConnections().Count,
            ActiveRules = _ruleEngine.GetAllRules().Count,
            StartedAt = _startedAt,
            PacketsDelayed = _interceptor.TotalDelayed,
            PacketsDropped = _interceptor.TotalDropped,
            PacketsSent = _interceptor.TotalSent,
            PacketsBlocked = _interceptor.TotalBlocked
        };
    }

    /// <summary>
    /// While paused the driver can unload late (a stop left pending finishes once the other
    /// program closes it) or load again (another program opened it), so a status read checks.
    /// Only a change lists the programs; otherwise it's one service-state query.
    /// </summary>
    private string? RecheckDriver()
    {
        string? known;
        int version;
        lock (_driverReasonLock)
        {
            known = _driverLoadedReason;
            version = _driverReasonVersion;
        }

        string? now;
        if (known is not null)
        {
            if (!_driverUnloader.IsUnloaded())
                return known;
            now = null;
        }
        else
        {
            if (_driverUnloader.IsUnloaded())
                return null;
            now = _driverUnloader.WhyLoaded(out _);
            if (now is null)
                return null;
        }

        lock (_driverReasonLock)
        {
            // A pause or resume wrote a newer reason while this one looked, and it knows better.
            if (version != _driverReasonVersion)
                return _driverLoadedReason;
            _driverLoadedReason = now;
            _driverReasonVersion++;
        }
        // A resume refused while the driver was unloading can go ahead now.
        if (now is null && _resumeRefusal is { } refusal)
            Interlocked.CompareExchange(ref _engineError, null, refusal);
        return now;
    }

    private void RecordLastError(string message)
    {
        try
        {
            EnsureDataDirectory();
            File.WriteAllText(_lastErrorPath, $"{DateTime.UtcNow:O} {message}");
        }
        catch
        {
            // Best-effort; don't fail the service over error recording
        }
    }

    private void ClearLastError()
    {
        try
        {
            if (File.Exists(_lastErrorPath))
                File.Delete(_lastErrorPath);
        }
        catch
        {
            // Best-effort
        }
    }
}
