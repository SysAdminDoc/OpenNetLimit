using System.Security.Principal;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.IPC;

namespace OpenNetLimit.Service;

public class EngineWorker : BackgroundService
{
    private readonly IPacketInterceptor _interceptor;
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PipeServer _pipeServer;
    private readonly ILogger<EngineWorker> _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private RuleReconciler? _reconciler;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit");

    private static readonly string RulesPath = Path.Combine(DataDir, "rules.json");
    private static readonly string LastErrorPath = Path.Combine(DataDir, "last-error.txt");

    public EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        IFlowTracker flowTracker,
        ITrafficMonitor trafficMonitor,
        PipeServer pipeServer,
        ILogger<EngineWorker> logger)
    {
        _interceptor = interceptor;
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _trafficMonitor = trafficMonitor;
        _pipeServer = pipeServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");

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

        LoadRules();
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

        _pipeServer.DiagnosticProvider = GetDiagnosticInfo;
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

    private async Task ShutdownGracefully()
    {
        try
        {
            await _interceptor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping packet interceptor during shutdown");
        }

        SaveRules();
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
