using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.Plugins;
using OpenNetLimit.Service.Security;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service.IPC;

public class PipeServer
{
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly IRuleEngine _ruleEngine;
    private readonly BandwidthAlertTracker _alertTracker;
    private readonly ControlPlaneState _controlPlane;
    private readonly IProcessVerifier _processVerifier;
    private readonly IGeoIpResolver _geoIpResolver;
    private readonly PluginManager _pluginManager;
    private readonly ILogger<PipeServer> _logger;

    public Func<DiagnosticInfo>? DiagnosticProvider
    {
        get => _controlPlane.DiagnosticProvider;
        set => _controlPlane.DiagnosticProvider = value;
    }

    public Func<IReadOnlyList<object>>? ConnectionLogProvider
    {
        get => _controlPlane.ConnectionLogProvider;
        set => _controlPlane.ConnectionLogProvider = value;
    }

    public TrafficStatsDb? StatsProvider
    {
        get => _controlPlane.StatsProvider;
        set => _controlPlane.StatsProvider = value;
    }

    public QuotaTracker? QuotaTracker
    {
        get => _controlPlane.QuotaTracker;
        set => _controlPlane.QuotaTracker = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PipeServer(
        ITrafficMonitor trafficMonitor,
        IRuleEngine ruleEngine,
        BandwidthAlertTracker alertTracker,
        ControlPlaneState controlPlane,
        IProcessVerifier processVerifier,
        IGeoIpResolver geoIpResolver,
        PluginManager pluginManager,
        ILogger<PipeServer> logger)
    {
        _trafficMonitor = trafficMonitor;
        _ruleEngine = ruleEngine;
        _alertTracker = alertTracker;
        _controlPlane = controlPlane;
        _processVerifier = processVerifier;
        _geoIpResolver = geoIpResolver;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        bool isFirst = true;
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecurePipe(isFirst);
            isFirst = false;

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

    internal static NamedPipeServerStream CreateSecurePipe(bool firstInstance = false)
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

        var options = PipeOptions.Asynchronous;
        if (firstInstance)
            options |= PipeOptions.FirstPipeInstance;

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
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
                    var response = await ProcessCommandAsync(action, payload, ct);
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
            bool isAdmin = false;
            pipe.RunAsClient(() =>
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            });
            return isAdmin;
        }
        catch
        {
            return false;
        }
    }

    private Task<string> ProcessCommandAsync(string action, string payload, CancellationToken ct)
    {
        return action switch
        {
            "SNAPSHOT" => Task.FromResult(JsonSerializer.Serialize(_trafficMonitor.TakeSnapshot(), JsonOptions)),
            "RULES" => Task.FromResult(JsonSerializer.Serialize(_ruleEngine.GetAllRules(), JsonOptions)),
            "PROCESSES" => Task.FromResult(JsonSerializer.Serialize(_trafficMonitor.GetAllProcesses(), JsonOptions)),
            "STATUS" => Task.FromResult(GetStatusResponse()),
            "CONNECTION_LOG" => Task.FromResult(GetConnectionLog()),
            "EXPORT_RULES" => Task.FromResult(_ruleEngine.ExportRules()),
            "STATS_HOURLY" => Task.FromResult(GetStatsHourly(payload)),
            "STATS_DAILY" => Task.FromResult(GetStatsDaily(payload)),
            "STATS_TOP" => Task.FromResult(GetStatsTop()),
            "QUOTAS" => Task.FromResult(GetQuotas()),
            "ALERT_RULES" => Task.FromResult(JsonSerializer.Serialize(_alertTracker.GetRules(), JsonOptions)),
            "ALERT_EVENTS" => Task.FromResult(JsonSerializer.Serialize(_alertTracker.GetRecentEvents(), JsonOptions)),
            "PLUGINS" => Task.FromResult(JsonSerializer.Serialize(_pluginManager.GetPlugins(), JsonOptions)),
            "GROUPS" => Task.FromResult(JsonSerializer.Serialize(_ruleEngine.GetGroupNames(), JsonOptions)),
            "GROUP_RULES" => Task.FromResult(GetGroupRules(payload)),
            "ADD_RULE" => Task.FromResult(AddRule(payload)),
            "REMOVE_RULE" => Task.FromResult(RemoveRule(payload)),
            "UPDATE_RULE" => Task.FromResult(UpdateRule(payload)),
            "IMPORT_RULES" => Task.FromResult(ImportRules(payload)),
            "ADD_ALERT_RULE" => Task.FromResult(AddAlertRule(payload)),
            "UPDATE_ALERT_RULE" => Task.FromResult(UpdateAlertRule(payload)),
            "REMOVE_ALERT_RULE" => Task.FromResult(RemoveAlertRule(payload)),
            "RELOAD_PLUGINS" => Task.FromResult(JsonSerializer.Serialize(_pluginManager.Reload(), JsonOptions)),
            "VERIFY_PROCESS" => VerifyProcess(payload, ct),
            "GEOIP" => ResolveGeoIp(payload, ct),
            _ => Task.FromResult(ErrorResponse("unknown command"))
        };
    }

    private string AddAlertRule(string payload)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthAlertRule>(payload, JsonOptions);
            if (rule is null) return ErrorResponse("invalid alert rule");

            _alertTracker.AddRule(rule);
            return JsonSerializer.Serialize(new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"invalid JSON: {ex.Message}");
        }
    }

    private string UpdateAlertRule(string payload)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthAlertRule>(payload, JsonOptions);
            if (rule is null) return ErrorResponse("invalid alert rule");

            _alertTracker.UpdateRule(rule);
            return JsonSerializer.Serialize(new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"invalid JSON: {ex.Message}");
        }
    }

    private string RemoveAlertRule(string payload)
    {
        if (!Guid.TryParse(payload.Trim(), out var id))
            return ErrorResponse("invalid guid");

        _alertTracker.RemoveRule(id);
        return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
    }

    private async Task<string> VerifyProcess(string payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ErrorResponse("process path is required");

        var result = await _processVerifier.VerifyFileAsync(payload.Trim(), ct);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private async Task<string> ResolveGeoIp(string payload, CancellationToken ct)
    {
        if (!IPAddress.TryParse(payload.Trim(), out var ipAddress))
            return ErrorResponse("valid IP address is required");

        var result = await _geoIpResolver.ResolveAsync(ipAddress, ct);
        return JsonSerializer.Serialize(result, JsonOptions);
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

    private string ImportRules(string payload)
    {
        try
        {
            _ruleEngine.ImportRules(payload, replace: false);
            return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"invalid JSON: {ex.Message}");
        }
    }

    private string GetStatsHourly(string payload)
    {
        if (_controlPlane.StatsProvider is null) return ErrorResponse("stats unavailable");
        var processName = string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
        var entries = _controlPlane.StatsProvider.GetHourlyStats(processName);
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private string GetStatsDaily(string payload)
    {
        if (_controlPlane.StatsProvider is null) return ErrorResponse("stats unavailable");
        var processName = string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
        var entries = _controlPlane.StatsProvider.GetDailyStats(processName);
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private string GetStatsTop()
    {
        if (_controlPlane.StatsProvider is null) return ErrorResponse("stats unavailable");
        var entries = _controlPlane.StatsProvider.GetTopProcesses();
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private string GetQuotas()
    {
        var states = _controlPlane.GetQuotaStates();
        return JsonSerializer.Serialize(states, JsonOptions);
    }

    private string GetGroupRules(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ErrorResponse("group name is required");
        var rules = _ruleEngine.GetRulesByGroup(payload.Trim());
        return JsonSerializer.Serialize(rules, JsonOptions);
    }

    private string GetConnectionLog()
    {
        var entries = _controlPlane.GetConnectionLog();
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private string GetStatusResponse()
    {
        var diag = _controlPlane.GetDiagnostics();
        return JsonSerializer.Serialize(diag, JsonOptions);
    }

    private static string ErrorResponse(string message) =>
        JsonSerializer.Serialize(new { error = message }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
}
