using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Service.IPC;

namespace OpenNetLimit.Service;

public class EngineWorker : BackgroundService
{
    private readonly IPacketInterceptor _interceptor;
    private readonly IRuleEngine _ruleEngine;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PipeServer _pipeServer;
    private readonly ILogger<EngineWorker> _logger;

    private static readonly string RulesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit", "rules.json");

    public EngineWorker(
        IPacketInterceptor interceptor,
        IRuleEngine ruleEngine,
        ITrafficMonitor trafficMonitor,
        PipeServer pipeServer,
        ILogger<EngineWorker> logger)
    {
        _interceptor = interceptor;
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
        _pipeServer = pipeServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenNetLimit engine starting");

        _ruleEngine.LoadRules(RulesPath);
        _logger.LogInformation("Loaded {Count} rules from {Path}",
            _ruleEngine.GetAllRules().Count, RulesPath);

        await _interceptor.StartAsync(stoppingToken);
        _logger.LogInformation("Packet interceptor started");

        _ = Task.Run(() => _pipeServer.StartAsync(stoppingToken), stoppingToken);
        _logger.LogInformation("IPC pipe server started");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OpenNetLimit engine stopping");
        }

        await _interceptor.StopAsync();
        _ruleEngine.SaveRules(RulesPath);
        _logger.LogInformation("OpenNetLimit engine stopped");
    }
}
