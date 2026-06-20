using System.Security.Principal;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service;

public class EngineWorker : BackgroundService
{
    private readonly IPacketInterceptor _interceptor;
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PipeServer _pipeServer;
    private readonly BandwidthAlertTracker _alertTracker;
    private readonly ControlPlaneState _controlPlane;
    private readonly ILogger<EngineWorker> _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private RuleReconciler? _reconciler;
    private QuotaTracker? _quotaTracker;
    private TrafficStatsDb? _statsDb;
    private Timer? _statsTimer;
    private Timer? _purgeTimer;
    private Timer? _quotaTimer;
    private Timer? _alertTimer;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit");

    private static readonly string RulesPath = Path.Combine(DataDir, "rules.json");
    private static readonly string AlertsPath = Path.Combine(DataDir, "alerts.json");
    private static readonly string StatsDbPath = Path.Combine(DataDir, "traffic.db");
    private static readonly string LastErrorPath = Path.Combine(DataDir, "last-error.txt");

    public EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        IFlowTracker flowTracker,
        ITrafficMonitor trafficMonitor,
        PipeServer pipeServer,
        BandwidthAlertTracker alertTracker,
        ControlPlaneState controlPlane,
        ILogger<EngineWorker> logger)
    {
        _interceptor = interceptor;
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _trafficMonitor = trafficMonitor;
        _pipeServer = pipeServer;
        _alertTracker = alertTracker;
        _controlPlane = controlPlane;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");
        _controlPlane.DiagnosticProvider = GetDiagnosticInfo;

        if (!ValidatePrerequisites())
        {
            _logger.LogCritical("Prerequisite validation failed — engine will not start");
            return;
        }

        ClearLastError();
        EnsureDataDirectory();

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
            _logger.LogWarning("Quota warning for {Process}: {Percent}% used ({Used}/{Limit} bytes)",
                name, state.PercentUsed, state.UsedBytes, state.LimitBytes);
        _quotaTracker.OnQuotaExceeded += (name, state) =>
            _logger.LogWarning("Quota exceeded for {Process}: {Used}/{Limit} bytes — action: {Action}",
                name, state.UsedBytes, state.LimitBytes, state.Action);
        _alertTracker.OnAlert += alert =>
            _logger.LogWarning("Bandwidth alert: {Message}", alert.Message);

        LoadRules();
        LoadAlerts();
        _reconciler.Reconcile();

        try
        {
            await _interceptor.StartAsync(stoppingToken);
            _logger.LogInformation("Packet interceptor started");
        }
        catch (Exception ex)
        {
            RecordLastError($"Failed to start packet interceptor: {ex.Message}");
            _logger.LogCritical(ex, "Failed to start packet interceptor — is WinDivert installed and accessible?");
            return;
        }

        try
        {
            _statsDb = new TrafficStatsDb(StatsDbPath);
            _statsTimer = new Timer(_ => RecordStats(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _purgeTimer = new Timer(_ => _statsDb.PurgeOlderThan(90), null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
            _controlPlane.StatsProvider = _statsDb;
            _logger.LogInformation("Traffic statistics database initialized at {Path}", StatsDbPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize stats database — statistics will be unavailable");
        }

        _quotaTimer = new Timer(_ => _quotaTracker?.Update(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _alertTimer = new Timer(_ => _alertTracker.Update(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _controlPlane.QuotaTracker = _quotaTracker;
        if (_interceptor is WinDivertInterceptor wdi2)
            _controlPlane.ConnectionLogProvider = () => wdi2.ConnectionLog.GetRecent(100).Cast<object>().ToList();
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

    private bool ValidatePrerequisites()
    {
        bool valid = true;

        if (!IsRunningAsAdmin())
        {
            _logger.LogError("OpenNetLimit requires administrator privileges to load the WinDivert driver");
            RecordLastError("Service not running as administrator");
            valid = false;
        }

        return valid;
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
            Directory.CreateDirectory(DataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data directory {DataDir}", DataDir);
        }
    }

    private void LoadRules()
    {
        try
        {
            _ruleEngine.LoadRules(RulesPath);
            _logger.LogInformation("Loaded {Count} rules from {Path}",
                _ruleEngine.GetAllRules().Count, RulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load rules from {Path} — starting with empty rule set", RulesPath);
            RecordLastError($"Failed to load rules: {ex.Message}");
        }
    }

    private void SaveRules()
    {
        try
        {
            _ruleEngine.SaveRules(RulesPath);
            _logger.LogInformation("Saved rules to {Path}", RulesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save rules to {Path}", RulesPath);
        }
    }

    private void LoadAlerts()
    {
        try
        {
            _alertTracker.LoadRules(AlertsPath);
            _logger.LogInformation("Loaded {Count} alert rules from {Path}",
                _alertTracker.GetRules().Count, AlertsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load alert rules from {Path} — starting with empty alert set", AlertsPath);
            RecordLastError($"Failed to load alert rules: {ex.Message}");
        }
    }

    private void SaveAlerts()
    {
        try
        {
            _alertTracker.SaveRules(AlertsPath);
            _logger.LogInformation("Saved alert rules to {Path}", AlertsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save alert rules to {Path}", AlertsPath);
        }
    }

    private void RecordStats()
    {
        try
        {
            var snapshot = _trafficMonitor.TakeSnapshot();
            _statsDb?.RecordSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record traffic statistics");
        }
    }

    private async Task ShutdownGracefully()
    {
        _quotaTimer?.Dispose();
        _alertTimer?.Dispose();
        _statsTimer?.Dispose();
        _purgeTimer?.Dispose();

        try
        {
            await _interceptor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping packet interceptor during shutdown");
        }

        SaveRules();
        SaveAlerts();
        _statsDb?.Dispose();
        _logger.LogInformation("OpenNetLimit engine stopped");
    }

    private async Task RunPipeServer(CancellationToken ct)
    {
        try
        {
            await _pipeServer.StartAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "IPC pipe server crashed");
            RecordLastError($"IPC pipe server crashed: {ex.Message}");
        }
    }

    private DiagnosticInfo GetDiagnosticInfo()
    {
        var info = new DiagnosticInfo
        {
            Running = _interceptor.IsRunning,
            ActiveFlows = _flowTracker.GetActiveConnections().Count,
            ActiveRules = _ruleEngine.GetAllRules().Count,
            StartedAt = _startedAt
        };

        if (_interceptor is WinDivertInterceptor wdi)
        {
            info.PacketsDelayed = wdi.Scheduler.TotalDelayed;
            info.PacketsDropped = wdi.Scheduler.TotalDropped;
            info.PacketsSent = wdi.Scheduler.TotalSent;
            info.PacketsBlocked = wdi.TotalBlocked;
        }

        return info;
    }

    private void RecordLastError(string message)
    {
        try
        {
            EnsureDataDirectory();
            File.WriteAllText(LastErrorPath, $"{DateTime.UtcNow:O} {message}");
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
            if (File.Exists(LastErrorPath))
                File.Delete(LastErrorPath);
        }
        catch
        {
            // Best-effort
        }
    }
}
