using System.Security.Cryptography;
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

namespace OpenNetLimit.Service.API;

public sealed class RestApiRouter
{
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly IRuleEngine _ruleEngine;
    private readonly BandwidthAlertTracker _alertTracker;
    private readonly ControlPlaneState _controlPlane;
    private readonly RestApiOptions _options;
    private readonly IProcessVerifier _processVerifier;
    private readonly IGeoIpResolver _geoIpResolver;
    private readonly PluginManager _pluginManager;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RestApiRouter(
        ITrafficMonitor trafficMonitor,
        IRuleEngine ruleEngine,
        BandwidthAlertTracker alertTracker,
        ControlPlaneState controlPlane,
        RestApiOptions options,
        IProcessVerifier processVerifier,
        IGeoIpResolver geoIpResolver,
        PluginManager pluginManager)
    {
        _trafficMonitor = trafficMonitor;
        _ruleEngine = ruleEngine;
        _alertTracker = alertTracker;
        _controlPlane = controlPlane;
        _options = options;
        _processVerifier = processVerifier;
        _geoIpResolver = geoIpResolver;
        _pluginManager = pluginManager;
    }

    public async Task<RestApiResponse> HandleAsync(RestApiRequest request, CancellationToken ct = default)
    {
        var method = request.Method.ToUpperInvariant();
        var path = NormalizePath(request.Path);
        var authFailure = Authorize(request, method, path);
        if (authFailure is not null)
            return authFailure;

        if (path == "/health" && method == "GET")
            return RestApiResponse.Json(200, new { ok = true }, JsonOptions);

        if (!path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            return RestApiResponse.Error(404, "not found", JsonOptions);

        var query = ParseQuery(request.QueryString);

        return path switch
        {
            "/api/v1/status" when method == "GET" =>
                RestApiResponse.Json(200, _controlPlane.GetDiagnostics(), JsonOptions),
            "/api/v1/snapshot" when method == "GET" =>
                RestApiResponse.Json(200, _trafficMonitor.TakeSnapshot(), JsonOptions),
            "/api/v1/processes" when method == "GET" =>
                RestApiResponse.Json(200, _trafficMonitor.GetAllProcesses(), JsonOptions),
            "/api/v1/rules" when method == "GET" =>
                RestApiResponse.Json(200, _ruleEngine.GetAllRules(), JsonOptions),
            "/api/v1/rules" when method == "POST" =>
                AddRule(request.Body),
            "/api/v1/rules/import" when method == "POST" =>
                ImportRules(request.Body, GetBool(query, "replace")),
            "/api/v1/stats/hourly" when method == "GET" =>
                GetHourlyStats(query),
            "/api/v1/stats/daily" when method == "GET" =>
                GetDailyStats(query),
            "/api/v1/stats/top" when method == "GET" =>
                GetTopProcesses(query),
            "/api/v1/quotas" when method == "GET" =>
                RestApiResponse.Json(200, _controlPlane.GetQuotaStates(), JsonOptions),
            "/api/v1/connections" when method == "GET" =>
                RestApiResponse.Json(200, _controlPlane.GetConnectionLog(), JsonOptions),
            "/api/v1/verification" when method == "GET" =>
                await VerifyProcessAsync(query, ct),
            "/api/v1/verification/cache" when method == "GET" =>
                RestApiResponse.Json(200, _processVerifier.GetCachedResults(), JsonOptions),
            "/api/v1/geoip" when method == "GET" =>
                await ResolveGeoIpAsync(query, ct),
            "/api/v1/geoip/cache" when method == "GET" =>
                RestApiResponse.Json(200, _geoIpResolver.GetCachedResults(), JsonOptions),
            "/api/v1/alerts/rules" when method == "GET" =>
                RestApiResponse.Json(200, _alertTracker.GetRules(), JsonOptions),
            "/api/v1/alerts/rules" when method == "POST" =>
                AddAlertRule(request.Body),
            "/api/v1/alerts/events" when method == "GET" =>
                RestApiResponse.Json(200, _alertTracker.GetRecentEvents(GetInt(query, "limit", 100, 1, 500)), JsonOptions),
            "/api/v1/groups" when method == "GET" =>
                RestApiResponse.Json(200, _ruleEngine.GetGroupNames(), JsonOptions),
            "/api/v1/plugins" when method == "GET" =>
                RestApiResponse.Json(200, _pluginManager.GetPlugins(), JsonOptions),
            "/api/v1/plugins/reload" when method == "POST" =>
                RestApiResponse.Json(200, _pluginManager.Reload(), JsonOptions),
            _ => path.StartsWith("/api/v1/groups/", StringComparison.OrdinalIgnoreCase) && method == "GET"
                ? HandleGroupByName(path)
                : path.StartsWith("/api/v1/alerts/rules/", StringComparison.OrdinalIgnoreCase)
                    ? HandleAlertRuleById(method, path, request.Body)
                    : HandleRuleById(method, path, request.Body)
        };
    }

    private RestApiResponse? Authorize(RestApiRequest request, string method, string path)
    {
        if (request.IsLoopback && !RequiresApiKey(method, path))
            return null;

        if (!_options.HasApiKey)
            return RestApiResponse.Error(403, "api key not configured", JsonOptions);

        if (!HasValidApiKey(request.ApiKey))
            return RestApiResponse.Error(401, "api key required", JsonOptions);

        return null;
    }

    private bool HasValidApiKey(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrWhiteSpace(_options.ApiKey))
            return false;

        var expected = Encoding.UTF8.GetBytes(_options.ApiKey);
        var actual = Encoding.UTF8.GetBytes(supplied.Trim());
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool IsMutation(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    private static bool RequiresApiKey(string method, string path) =>
        IsMutation(method)
        || path.Equals("/api/v1/verification", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/v1/verification/cache", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/v1/geoip", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/v1/geoip/cache", StringComparison.OrdinalIgnoreCase);

    private RestApiResponse AddRule(string body)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthRule>(body, JsonOptions);
            if (rule is null)
                return RestApiResponse.Error(400, "invalid rule", JsonOptions);

            _ruleEngine.AddRule(rule);
            return RestApiResponse.Json(201, new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return RestApiResponse.Error(400, $"invalid JSON: {ex.Message}", JsonOptions);
        }
    }

    private RestApiResponse AddAlertRule(string body)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthAlertRule>(body, JsonOptions);
            if (rule is null)
                return RestApiResponse.Error(400, "invalid alert rule", JsonOptions);

            _alertTracker.AddRule(rule);
            return RestApiResponse.Json(201, new { ok = true, id = rule.Id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return RestApiResponse.Error(400, $"invalid JSON: {ex.Message}", JsonOptions);
        }
    }

    private RestApiResponse UpdateRule(Guid id, string body)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthRule>(body, JsonOptions);
            if (rule is null)
                return RestApiResponse.Error(400, "invalid rule", JsonOptions);

            rule.Id = id;
            _ruleEngine.UpdateRule(rule);
            return RestApiResponse.Json(200, new { ok = true, id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return RestApiResponse.Error(400, $"invalid JSON: {ex.Message}", JsonOptions);
        }
    }

    private RestApiResponse DeleteRule(Guid id)
    {
        _ruleEngine.RemoveRule(id);
        return RestApiResponse.Json(200, new { ok = true }, JsonOptions);
    }

    private RestApiResponse ImportRules(string body, bool replace)
    {
        try
        {
            _ruleEngine.ImportRules(body, replace);
            return RestApiResponse.Json(200, new { ok = true, replace }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return RestApiResponse.Error(400, $"invalid JSON: {ex.Message}", JsonOptions);
        }
    }

    private RestApiResponse HandleRuleById(string method, string path, string body)
    {
        const string prefix = "/api/v1/rules/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return RestApiResponse.Error(404, "not found", JsonOptions);

        var rawId = path[prefix.Length..];
        if (!Guid.TryParse(rawId, out var id))
            return RestApiResponse.Error(400, "invalid rule id", JsonOptions);

        return method switch
        {
            "GET" => GetRule(id),
            "PUT" => UpdateRule(id, body),
            "DELETE" => DeleteRule(id),
            _ => RestApiResponse.Error(405, "method not allowed", JsonOptions)
        };
    }

    private RestApiResponse HandleGroupByName(string path)
    {
        const string prefix = "/api/v1/groups/";
        var name = Uri.UnescapeDataString(path[prefix.Length..].Trim('/'));
        if (string.IsNullOrEmpty(name))
            return RestApiResponse.Error(400, "group name required", JsonOptions);
        return RestApiResponse.Json(200, _ruleEngine.GetRulesByGroup(name), JsonOptions);
    }

    private RestApiResponse HandleAlertRuleById(string method, string path, string body)
    {
        const string prefix = "/api/v1/alerts/rules/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return RestApiResponse.Error(404, "not found", JsonOptions);

        var rawId = path[prefix.Length..];
        if (!Guid.TryParse(rawId, out var id))
            return RestApiResponse.Error(400, "invalid alert rule id", JsonOptions);

        return method switch
        {
            "GET" => _alertTracker.GetRule(id) is { } rule
                ? RestApiResponse.Json(200, rule, JsonOptions)
                : RestApiResponse.Error(404, "alert rule not found", JsonOptions),
            "PUT" => UpdateAlertRule(id, body),
            "DELETE" => DeleteAlertRule(id),
            _ => RestApiResponse.Error(405, "method not allowed", JsonOptions)
        };
    }

    private RestApiResponse UpdateAlertRule(Guid id, string body)
    {
        try
        {
            var rule = JsonSerializer.Deserialize<BandwidthAlertRule>(body, JsonOptions);
            if (rule is null)
                return RestApiResponse.Error(400, "invalid alert rule", JsonOptions);

            rule.Id = id;
            _alertTracker.UpdateRule(rule);
            return RestApiResponse.Json(200, new { ok = true, id }, JsonOptions);
        }
        catch (JsonException ex)
        {
            return RestApiResponse.Error(400, $"invalid JSON: {ex.Message}", JsonOptions);
        }
    }

    private RestApiResponse DeleteAlertRule(Guid id)
    {
        _alertTracker.RemoveRule(id);
        return RestApiResponse.Json(200, new { ok = true }, JsonOptions);
    }

    private RestApiResponse GetRule(Guid id)
    {
        var rule = _ruleEngine.GetRule(id);
        return rule is null
            ? RestApiResponse.Error(404, "rule not found", JsonOptions)
            : RestApiResponse.Json(200, rule, JsonOptions);
    }

    private RestApiResponse GetHourlyStats(IReadOnlyDictionary<string, string> query)
    {
        if (_controlPlane.StatsProvider is null)
            return RestApiResponse.Error(503, "stats unavailable", JsonOptions);

        var hours = GetInt(query, "hours", 24, 1, 24 * 365);
        query.TryGetValue("processName", out var processName);
        return RestApiResponse.Json(200, _controlPlane.StatsProvider.GetHourlyStats(BlankToNull(processName), hours), JsonOptions);
    }

    private RestApiResponse GetDailyStats(IReadOnlyDictionary<string, string> query)
    {
        if (_controlPlane.StatsProvider is null)
            return RestApiResponse.Error(503, "stats unavailable", JsonOptions);

        var days = GetInt(query, "days", 30, 1, 3650);
        query.TryGetValue("processName", out var processName);
        return RestApiResponse.Json(200, _controlPlane.StatsProvider.GetDailyStats(BlankToNull(processName), days), JsonOptions);
    }

    private RestApiResponse GetTopProcesses(IReadOnlyDictionary<string, string> query)
    {
        if (_controlPlane.StatsProvider is null)
            return RestApiResponse.Error(503, "stats unavailable", JsonOptions);

        var days = GetInt(query, "days", 7, 1, 3650);
        var limit = GetInt(query, "limit", 20, 1, 200);
        return RestApiResponse.Json(200, _controlPlane.StatsProvider.GetTopProcesses(days, limit), JsonOptions);
    }

    private async Task<RestApiResponse> VerifyProcessAsync(IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        if (!query.TryGetValue("path", out var processPath) || string.IsNullOrWhiteSpace(processPath))
            return RestApiResponse.Error(400, "path query parameter is required", JsonOptions);

        var result = await _processVerifier.VerifyFileAsync(processPath, ct).ConfigureAwait(false);
        return RestApiResponse.Json(200, result, JsonOptions);
    }

    private async Task<RestApiResponse> ResolveGeoIpAsync(IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        if (!query.TryGetValue("ip", out var rawIp) || !System.Net.IPAddress.TryParse(rawIp, out var ipAddress))
            return RestApiResponse.Error(400, "valid ip query parameter is required", JsonOptions);

        var result = await _geoIpResolver.ResolveAsync(ipAddress, ct).ConfigureAwait(false);
        return RestApiResponse.Json(200, result, JsonOptions);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var trimmed = path.Split('?', 2)[0].TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed.ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = queryString.TrimStart('?');
        if (query.Length == 0)
            return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> query, string key, int fallback, int min, int max)
    {
        if (!query.TryGetValue(key, out var raw) || !int.TryParse(raw, out var value))
            return fallback;

        return Math.Clamp(value, min, max);
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var raw)
        && (raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
