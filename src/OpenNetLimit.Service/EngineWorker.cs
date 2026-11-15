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
    private readonly ILogger<EngineWorker> _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private RuleReconciler? _reconciler;
    private QuotaTracker? _quotaTracker;
    private TrafficStatsDb? _statsDb;
    private Timer? _statsTimer;
    private Timer? _purgeTimer;
    private Timer? _flowPurgeTimer;
    private Timer? _quotaTimer;
    private Timer? _quotaResetTimer;
    private Timer? _alertTimer;
    private long _lastQuotaResetCheckTicks = DateTime.Now.Date.Ticks;
    private volatile string? _engineError;
    private volatile bool _engineStarted;
    private volatile bool _stopping;
    private bool _stateLoaded;

    private static readonly string DefaultDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit");

    private readonly string _dataDir;
    private readonly string _rulesPath;
    private readonly string _alertsPath;
    private readonly string _statsDbPath;
    private readonly string _lastErrorPath;

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
        ILogger<EngineWorker> logger)
        : this(interceptor, ruleEngine, rateLimiter, flowTracker, trafficMonitor, pipeServer.StartAsync,
            alertTracker, pluginManager, controlPlane, logger, DefaultDataDir, IsRunningAsAdmin)
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
        Func<bool> isAdmin)
    {
        _interceptor = interceptor;
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");
        _controlPlane.DiagnosticProvider = GetDiagnosticInfo;

        if (ValidatePrerequisites())
        {
            ClearLastError();
            EnsureDataDirectory();
            LoadState(stoppingToken);

            if (await TryStartInterceptorAsync(stoppingToken))
                StartBackgroundWork();
        }
        else
        {
            _logger.LogCritical("Prerequisite validation failed; the packet engine will not start");
        }

        // The control pipe stays up even when the engine can't start. Without it the app
        // could only report "Service not running" and never the reason.
        _controlPlane.QuotaTracker = _quotaTracker;
        _controlPlane.ConnectionLogProvider = () => _interceptor.GetRecentConnectionLog(100);
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
        _reconciler = new RuleReconciler(_ruleEngine, _rateLimiter, _flowTracker);
        if (_ruleEngine is RuleEngine concreteEngine)
        {
            concreteEngine.OnRulesChanged += () =>
            {
                try { _reconciler.Reconcile(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Rule reconciliation failed"); }
            };
        }

        _quotaTracker = new QuotaTracker(_ruleEngine, _trafficMonitor);
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

            // Enforce the configured quota action
            switch (state.Action)
            {
                case OpenNetLimit.Core.Models.QuotaAction.Throttle:
                    var rule = _ruleEngine.GetAllRules()
                        .FirstOrDefault(r => r.ProcessName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true && r.Quota is not null);
                    if (rule?.Quota is not null)
                    {
                        var throttleRate = rule.Quota.ThrottleBytesPerSecond;
                        foreach (var proc in _trafficMonitor.GetAllProcesses()
                            .Where(p => rule.MatchesProcess(p.ProcessName, p.ProcessPath)))
                        {
                            _rateLimiter.SetLimit(proc.ProcessId, throttleRate, throttleRate);
                        }
                        _logger.LogInformation("Quota throttle applied for {Process}: {Rate} B/s", name, throttleRate);
                    }
                    break;

                case OpenNetLimit.Core.Models.QuotaAction.Block:
                    foreach (var proc in _trafficMonitor.GetAllProcesses()
                        .Where(p => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _rateLimiter.SetLimit(proc.ProcessId, 0, 0);
                    }
                    _logger.LogInformation("Quota block applied for {Process}: traffic dropped", name);
                    break;

                case OpenNetLimit.Core.Models.QuotaAction.WarnOnly:
                    break;
            }
        };
        _alertTracker.OnAlert += alert =>
        {
            _logger.LogWarning("Bandwidth alert: {Message}", alert.Message);
            DispatchPluginEvent("alert.triggered", alert, stoppingToken);
        };

        LoadRules();
        LoadAlerts();
        LoadPlugins();
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

    /// <summary>One line the app can show when the packet engine can't start.</summary>
    internal static string DescribeStartFailure(Exception ex) => ex switch
    {
        WinDivertOpenException open => $"{open.Reason} (Windows error {open.NativeErrorCode})",
        DllNotFoundException => "WinDivert.dll is missing next to the service. Reinstall OpenNetLimit.",
        _ => ex.Message
    };

    private void StartBackgroundWork()
    {
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
        _quotaTimer = new Timer(_ => _quotaTracker?.Update(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
                using var baseCert = X509Certificate.CreateFromSignedFile(driverPath);
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

    private void DispatchPluginEvent(string eventType, object payload, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await _pluginManager.DispatchAsync(eventType, payload, ct); }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
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
        _quotaTimer?.Dispose();
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
        var running = _interceptor.IsRunning;
        var engineError = _engineError;
        if (engineError is null && _engineStarted && !running && !_stopping)
            engineError = "The packet engine stopped after repeated errors. Restart the OpenNetLimit service.";

        return new DiagnosticInfo
        {
            Running = running,
            EngineError = engineError,
            ActiveFlows = _flowTracker.GetActiveConnections().Count,
            ActiveRules = _ruleEngine.GetAllRules().Count,
            StartedAt = _startedAt,
            PacketsDelayed = _interceptor.TotalDelayed,
            PacketsDropped = _interceptor.TotalDropped,
            PacketsSent = _interceptor.TotalSent,
            PacketsBlocked = _interceptor.TotalBlocked
        };
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
