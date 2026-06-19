using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.IPC;

public class PipeServer
{
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly ILogger<PipeServer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PipeServer(
        ITrafficMonitor trafficMonitor,
        IRuleEngine ruleEngine,
        IRateLimiter rateLimiter,
        ILogger<PipeServer> logger)
    {
        _trafficMonitor = trafficMonitor;
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                "OpenNetLimit",
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Message);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleClient(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
        }
    }

    private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    var response = ProcessCommand(line);
                    await writer.WriteLineAsync(response);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IPC client error");
        }
    }

    private string ProcessCommand(string command)
    {
        var parts = command.Split(' ', 2);
        var action = parts[0].ToUpperInvariant();
        var payload = parts.Length > 1 ? parts[1] : string.Empty;

        return action switch
        {
            "SNAPSHOT" => JsonSerializer.Serialize(_trafficMonitor.TakeSnapshot(), JsonOptions),
            "RULES" => JsonSerializer.Serialize(_ruleEngine.GetAllRules(), JsonOptions),
            "ADD_RULE" => AddRule(payload),
            "REMOVE_RULE" => RemoveRule(payload),
            "PROCESSES" => JsonSerializer.Serialize(_trafficMonitor.GetAllProcesses(), JsonOptions),
            _ => JsonSerializer.Serialize(new { error = "unknown command" }, JsonOptions)
        };
    }

    private string AddRule(string payload)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthRule>(payload, JsonOptions);
            if (rule is null) return """{"error":"invalid rule"}""";

            _ruleEngine.AddRule(rule);
            return JsonSerializer.Serialize(new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }
    }

    private string RemoveRule(string payload)
    {
        if (!Guid.TryParse(payload, out var id))
            return """{"error":"invalid guid"}""";

        _ruleEngine.RemoveRule(id);
        _rateLimiter.RemoveAll();
        return """{"ok":true}""";
    }
}
