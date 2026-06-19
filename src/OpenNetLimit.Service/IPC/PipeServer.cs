using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.IPC;

public class PipeServer
{
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly IRuleEngine _ruleEngine;
    private readonly ILogger<PipeServer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PipeServer(
        ITrafficMonitor trafficMonitor,
        IRuleEngine ruleEngine,
        ILogger<PipeServer> logger)
    {
        _trafficMonitor = trafficMonitor;
        _ruleEngine = ruleEngine;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecurePipe();

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

    internal static NamedPipeServerStream CreateSecurePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string clientIdentity = "unknown";
        try
        {
            using (pipe)
            {
                clientIdentity = GetClientIdentity(pipe);
                bool isAdmin = IsClientAdmin(pipe);
                _logger.LogInformation("IPC client connected: {Identity} (admin={IsAdmin})", clientIdentity, isAdmin);

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;

                    if (string.IsNullOrWhiteSpace(line) || line.Length > 65536)
                    {
                        await writer.WriteLineAsync(ErrorResponse("invalid request"));
                        continue;
                    }

                    var parts = line.Split(' ', 2);
                    var action = parts[0].ToUpperInvariant();

                    if (!IpcProtocol.IsValidCommand(action))
                    {
                        await writer.WriteLineAsync(ErrorResponse("unknown command"));
                        continue;
                    }

                    if (IpcProtocol.RequiresAdmin(action) && !isAdmin)
                    {
                        _logger.LogWarning("Unauthorized mutation attempt by {Identity}: {Command}", clientIdentity, action);
                        await writer.WriteLineAsync(ErrorResponse("unauthorized: admin required"));
                        continue;
                    }

                    var payload = parts.Length > 1 ? parts[1] : string.Empty;
                    var response = ProcessCommand(action, payload);
                    await writer.WriteLineAsync(response);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IPC client error for {Identity}", clientIdentity);
        }
    }

    private static string GetClientIdentity(NamedPipeServerStream pipe)
    {
        try
        {
            return pipe.GetImpersonationUserName();
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsClientAdmin(NamedPipeServerStream pipe)
    {
        try
        {
            pipe.RunAsClient(() => { });
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private string ProcessCommand(string action, string payload)
    {
        return action switch
        {
            "SNAPSHOT" => JsonSerializer.Serialize(_trafficMonitor.TakeSnapshot(), JsonOptions),
            "RULES" => JsonSerializer.Serialize(_ruleEngine.GetAllRules(), JsonOptions),
            "PROCESSES" => JsonSerializer.Serialize(_trafficMonitor.GetAllProcesses(), JsonOptions),
            "STATUS" => JsonSerializer.Serialize(new
            {
                version = IpcProtocol.ProtocolVersion,
                running = true
            }, JsonOptions),
            "ADD_RULE" => AddRule(payload),
            "REMOVE_RULE" => RemoveRule(payload),
            "UPDATE_RULE" => UpdateRule(payload),
            _ => ErrorResponse("unknown command")
        };
    }

    private string AddRule(string payload)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthRule>(payload, JsonOptions);
            if (rule is null) return ErrorResponse("invalid rule");

            _ruleEngine.AddRule(rule);
            return JsonSerializer.Serialize(new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"invalid JSON: {ex.Message}");
        }
    }

    private string RemoveRule(string payload)
    {
        if (!Guid.TryParse(payload.Trim(), out var id))
            return ErrorResponse("invalid guid");

        _ruleEngine.RemoveRule(id);
        return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
    }

    private string UpdateRule(string payload)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthRule>(payload, JsonOptions);
            if (rule is null) return ErrorResponse("invalid rule");

            _ruleEngine.UpdateRule(rule);
            return JsonSerializer.Serialize(new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"invalid JSON: {ex.Message}");
        }
    }

    private static string ErrorResponse(string message) =>
        JsonSerializer.Serialize(new { error = message }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
}
