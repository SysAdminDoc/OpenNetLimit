using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.API;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.Plugins;
using OpenNetLimit.Service.Security;
using OpenNetLimit.Service.Storage;
using Xunit;

namespace OpenNetLimit.Tests;

public class RestApiRouterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task LocalRead_AllowsStatusWithoutApiKey()
    {
        var (_, _, router) = CreateRouter();

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/status",
            string.Empty,
            string.Empty,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"running\":true", response.Body);
    }

    [Theory]
    [InlineData("stay=true", true)]
    [InlineData("", false)]
    public async Task Pause_PassesTheStayChoiceAndReturnsTheStatus(string query, bool expectedStay)
    {
        bool? stay = null;
        var (_, _, router) = CreateRouter("secret", pause: (stayPaused, _) =>
        {
            stay = stayPaused;
            return Task.FromResult(new DiagnosticInfo { Paused = true, StayPaused = stayPaused, DriverLoadedReason = "held" });
        });

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", "/api/v1/engine/pause", query, string.Empty, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expectedStay, stay);
        Assert.Contains("\"paused\":true", response.Body);
        Assert.Contains("\"driverLoadedReason\":\"held\"", response.Body);
    }

    [Fact]
    public async Task Resume_ReturnsTheStatus()
    {
        var (_, _, router) = CreateRouter("secret",
            resume: _ => Task.FromResult(new DiagnosticInfo { Running = true }));

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", "/api/v1/engine/resume", string.Empty, string.Empty, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"running\":true", response.Body);
        Assert.Contains("\"paused\":false", response.Body);
    }

    [Theory]
    [InlineData("/api/v1/engine/pause")]
    [InlineData("/api/v1/engine/resume")]
    public async Task PauseAndResume_NeedTheApiKeyEvenLocally(string path)
    {
        bool called = false;
        var (_, _, router) = CreateRouter("secret",
            pause: (_, _) => { called = true; return Task.FromResult(new DiagnosticInfo()); },
            resume: _ => { called = true; return Task.FromResult(new DiagnosticInfo()); });

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", path, string.Empty, string.Empty, IsLoopback: true, ApiKey: null));

        Assert.Equal(401, response.StatusCode);
        Assert.False(called);
    }

    [Theory]
    [InlineData("/api/v1/engine/pause")]
    [InlineData("/api/v1/engine/resume")]
    public async Task PauseAndResume_BeforeTheEngineIsUp_Answer503(string path)
    {
        var (_, _, router) = CreateRouter("secret");

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", path, string.Empty, string.Empty, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("engine control unavailable", response.Body);
    }

    [Fact]
    public async Task RemoteRead_RequiresConfiguredApiKey()
    {
        var (_, _, router) = CreateRouter();

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/status",
            string.Empty,
            string.Empty,
            IsLoopback: false,
            ApiKey: null));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("api key not configured", response.Body);
    }

    [Fact]
    public async Task LocalMutation_RequiresApiKey()
    {
        var (_, _, router) = CreateRouter();
        var body = JsonSerializer.Serialize(new BandwidthRule { ProcessName = "chrome" }, JsonOptions);

        var response = await router.HandleAsync(new RestApiRequest(
            "POST",
            "/api/v1/rules",
            string.Empty,
            body,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task MutationWithKey_AddsRule()
    {
        var (_, rules, router) = CreateRouter("secret");
        var body = JsonSerializer.Serialize(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000
        }, JsonOptions);

        var response = await router.HandleAsync(new RestApiRequest(
            "POST",
            "/api/v1/rules",
            string.Empty,
            body,
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(201, response.StatusCode);
        Assert.Single(rules.GetAllRules());
        Assert.Equal("chrome", rules.GetAllRules()[0].ProcessName);
    }

    // A service that didn't load the rules took changes it then lost on the next save. Every
    // change route is refused, edits, deletes and a replacing import as well as adds, for
    // rules and alerts both.
    [Theory]
    [InlineData("POST", "/api/v1/rules", "", """{"processName":"steam"}""")]
    [InlineData("PUT", "/api/v1/rules/{rule}", "", """{"processName":"steam"}""")]
    [InlineData("DELETE", "/api/v1/rules/{rule}", "", "")]
    [InlineData("POST", "/api/v1/rules/import", "", """[{"processName":"steam"}]""")]
    [InlineData("POST", "/api/v1/rules/import", "?replace=true", """[{"processName":"steam"}]""")]
    [InlineData("POST", "/API/V1/ALERTS/RULES", "", """{"processName":"steam","thresholdBytesPerSecond":1024}""")]
    [InlineData("PUT", "/api/v1/alerts/rules/{alert}", "", """{"processName":"steam","thresholdBytesPerSecond":1024}""")]
    [InlineData("DELETE", "/api/v1/alerts/rules/{alert}", "", "")]
    public async Task Change_WhileChangesAreRefused_Answers503AndChangesNothing(string method, string path, string query, string body)
    {
        var alerts = new BandwidthAlertTracker(new TrafficMonitor());
        var (_, rules, router) = CreateRouter("secret", changesRefused: "not running as administrator", alerts: alerts);
        var rule = new BandwidthRule { ProcessName = "chrome" };
        rules.AddRule(rule);
        var alert = new BandwidthAlertRule { Name = "Big download", ThresholdBytesPerSecond = 2048 };
        alerts.AddRule(alert);
        string State() => rules.ExportRules() + string.Join(",",
            alerts.GetRules().Select(a => $"{a.Id}:{a.ProcessName}:{a.ThresholdBytesPerSecond}"));
        var before = State();

        var response = await router.HandleAsync(new RestApiRequest(method,
            path.Replace("{rule}", rule.Id.ToString()).Replace("{alert}", alert.Id.ToString()),
            query, body, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("not running as administrator", response.Body);
        Assert.Equal(before, State());
    }

    [Fact]
    public async Task RuleList_WhileChangesAreRefused_StillAnswers()
    {
        var (_, _, router) = CreateRouter("secret", changesRefused: "not running as administrator");

        var response = await router.HandleAsync(new RestApiRequest("GET", "/api/v1/rules", string.Empty, string.Empty, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/v1/rules", """{"downloadBytesPerSecond":1024}""")]
    [InlineData("PUT", "/api/v1/rules/8c0f8b0e-4a39-4f4e-9d55-4f7f6c7a0c11", """{"processPath":""}""")]
    [InlineData("POST", "/api/v1/rules/import", """[{"action":1}]""")]
    public async Task RuleWithoutAProgram_IsABadRequest(string method, string path, string body)
    {
        var (_, rules, router) = CreateRouter("secret");

        var response = await router.HandleAsync(new RestApiRequest(method, path, string.Empty, body, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("ProcessName or ProcessPath", response.Body);
        Assert.Empty(rules.GetAllRules());
    }

    [Fact]
    public async Task AddRule_NamedWithExe_MatchesTheApp()
    {
        var (_, rules, router) = CreateRouter("secret");

        var response = await router.HandleAsync(new RestApiRequest(
            "POST",
            "/api/v1/rules",
            string.Empty,
            """{"processName":"steam.exe","downloadBytesPerSecond":100000}""",
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(201, response.StatusCode);
        Assert.NotNull(rules.FindMatchingRule("steam", @"C:\Games\Steam\steam.exe"));
    }

    [Fact]
    public async Task HourlyStats_ForANameWithExe_ReturnThatAppsHistory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"onl_test_{Guid.NewGuid()}.db");
        using (var stats = new TrafficStatsDb(dbPath))
        {
            stats.RecordSnapshot(new TrafficSnapshot
            {
                Processes = [new ProcessTrafficInfo { ProcessId = 1, ProcessName = "steam", CurrentDownloadBytesPerSecond = 4096 }]
            });
            var (_, _, router) = CreateRouter(stats: stats);

            var response = await router.HandleAsync(new RestApiRequest(
                "GET",
                "/api/v1/stats/hourly",
                "processName=steam.exe&hours=1",
                string.Empty,
                IsLoopback: true,
                ApiKey: null));

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("\"bytesReceived\":4096", response.Body);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            File.Delete(file);
    }

    [Theory]
    [InlineData("Limit", RuleAction.Limit)]
    [InlineData("block", RuleAction.Block)]
    [InlineData("ALLOW", RuleAction.Allow)]
    public async Task AddRule_AcceptsTheActionNameTheCliSends(string action, RuleAction expected)
    {
        var (_, rules, router) = CreateRouter("secret");
        // The body `onl rules add --process curl --download 256 --action <name>` posts.
        var body = $$"""{"processName":"curl","downloadBytesPerSecond":262144,"action":"{{action}}","name":"curl limit"}""";

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", "/api/v1/rules", string.Empty, body, IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(201, response.StatusCode);
        var rule = Assert.Single(rules.GetAllRules());
        Assert.Equal(expected, rule.Action);
        Assert.Equal(262144, rule.DownloadBytesPerSecond);
    }

    [Fact]
    public async Task AddRule_UnknownActionName_SaysWhichNamesWork()
    {
        var (_, rules, router) = CreateRouter("secret");

        var response = await router.HandleAsync(new RestApiRequest(
            "POST", "/api/v1/rules", string.Empty, """{"processName":"curl","action":"Throttle"}""",
            IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Limit, Block, Allow", response.Body);
        Assert.Empty(rules.GetAllRules());
    }

    [Fact]
    public async Task Rules_StillListActionsAsNumbers()
    {
        var (_, rules, router) = CreateRouter("secret");
        rules.AddRule(new BandwidthRule { ProcessName = "curl", Action = RuleAction.Block });

        var response = await router.HandleAsync(new RestApiRequest(
            "GET", "/api/v1/rules", string.Empty, string.Empty, IsLoopback: true, ApiKey: null));

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"action\":1", response.Body);
    }

    [Fact]
    public async Task PutRule_UsesPathId()
    {
        var (_, rules, router) = CreateRouter("secret");
        var id = Guid.NewGuid();
        rules.AddRule(new BandwidthRule { Id = id, ProcessName = "chrome" });
        var body = JsonSerializer.Serialize(new BandwidthRule
        {
            Id = Guid.NewGuid(),
            ProcessName = "firefox",
            UploadBytesPerSecond = 50_000
        }, JsonOptions);

        var response = await router.HandleAsync(new RestApiRequest(
            "PUT",
            $"/api/v1/rules/{id}",
            string.Empty,
            body,
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        var rule = rules.GetRule(id);
        Assert.NotNull(rule);
        Assert.Equal("firefox", rule.ProcessName);
        Assert.Equal(50_000, rule.UploadBytesPerSecond);
    }

    [Fact]
    public async Task DeleteRule_RemovesRule()
    {
        var (_, rules, router) = CreateRouter("secret");
        var rule = new BandwidthRule { ProcessName = "chrome" };
        rules.AddRule(rule);

        var response = await router.HandleAsync(new RestApiRequest(
            "DELETE",
            $"/api/v1/rules/{rule.Id}",
            string.Empty,
            string.Empty,
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(rules.GetAllRules());
    }

    [Fact]
    public async Task StatsUnavailable_ReturnsServiceUnavailable()
    {
        var (_, _, router) = CreateRouter();

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/stats/top",
            string.Empty,
            string.Empty,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task Verification_RequiresKeyEvenOnLoopback()
    {
        var (_, _, router) = CreateRouter();

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/verification",
            "?path=C%3A%5CTools%5Capp.exe",
            string.Empty,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task Verification_WithKeyReturnsVerifierResult()
    {
        var verifier = new StubVerifier();
        var (_, _, router) = CreateRouter("secret", verifier);

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/verification",
            "?path=C%3A%5CTools%5Capp.exe",
            string.Empty,
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(@"C:\Tools\app.exe", verifier.LastPath);
        Assert.Contains("\"status\":4", response.Body);
    }

    [Fact]
    public async Task GeoIp_RequiresKeyEvenOnLoopback()
    {
        var (_, _, router) = CreateRouter();

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/geoip",
            "?ip=8.8.8.8",
            string.Empty,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task GeoIp_WithKeyReturnsResolverResult()
    {
        var geo = new StubGeoIpResolver();
        var (_, _, router) = CreateRouter("secret", geoIpResolver: geo);

        var response = await router.HandleAsync(new RestApiRequest(
            "GET",
            "/api/v1/geoip",
            "?ip=8.8.8.8",
            string.Empty,
            IsLoopback: true,
            ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("8.8.8.8", geo.LastIp);
        Assert.Contains("\"countryCode\":\"US\"", response.Body);
    }

    [Fact]
    public async Task PcLimit_PutThenGet_GivesTheSameLimit()
    {
        var current = WholePcLimit.Off;
        var (_, _, router) = CreateRouter("secret", pcLimit: () => current, setPcLimit: limit => current = limit);

        var put = await router.HandleAsync(new RestApiRequest("PUT", "/api/v1/pc-limit", string.Empty,
            """{"downloadBytesPerSecond":1048576,"uploadBytesPerSecond":262144}""", IsLoopback: true, ApiKey: "secret"));
        var get = await router.HandleAsync(new RestApiRequest("GET", "/api/v1/pc-limit", string.Empty, string.Empty,
            IsLoopback: true, ApiKey: null));

        Assert.Equal(200, put.StatusCode);
        Assert.Equal(200, get.StatusCode);
        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 1_048_576, UploadBytesPerSecond = 262_144 }, current);
        Assert.Equal(put.Body, get.Body);
        Assert.DoesNotContain("isOff", get.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PcLimit_Delete_TurnsItOffAndKeepsTheLanChoice()
    {
        var current = new WholePcLimit { DownloadBytesPerSecond = 1024, UploadBytesPerSecond = 2048, IncludeLan = true };
        var (_, _, router) = CreateRouter("secret", pcLimit: () => current, setPcLimit: limit => current = limit);

        var response = await router.HandleAsync(new RestApiRequest("DELETE", "/api/v1/pc-limit", string.Empty, string.Empty,
            IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new WholePcLimit { IncludeLan = true }, current);
    }

    [Theory]
    [InlineData("""{"uploadBytesPerSecond":-5}""", "negative")]
    [InlineData("""{"uploadBytesPerSecond":""", "invalid JSON")]
    [InlineData("null", "invalid whole-PC limit")]
    public async Task PcLimit_ThatIsntALimit_IsABadRequest(string body, string why)
    {
        var applied = false;
        var (_, _, router) = CreateRouter("secret", setPcLimit: limit => { applied = true; return limit; });

        var response = await router.HandleAsync(new RestApiRequest("PUT", "/api/v1/pc-limit", string.Empty, body,
            IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains(why, response.Body);
        Assert.False(applied);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task PcLimit_ChangeWithoutTheKey_IsRefusedEvenLocally(string method)
    {
        var applied = false;
        var (_, _, router) = CreateRouter("secret", setPcLimit: limit => { applied = true; return limit; });

        var response = await router.HandleAsync(new RestApiRequest(method, "/api/v1/pc-limit", string.Empty,
            """{"downloadBytesPerSecond":1024}""", IsLoopback: true, ApiKey: null));

        Assert.Equal(401, response.StatusCode);
        Assert.False(applied);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task PcLimit_WhileChangesAreRefused_Answers503ButStillReads(string method)
    {
        var applied = false;
        var (_, _, router) = CreateRouter("secret", changesRefused: "not running as administrator",
            pcLimit: () => new WholePcLimit { DownloadBytesPerSecond = 4096 },
            setPcLimit: limit => { applied = true; return limit; });

        var change = await router.HandleAsync(new RestApiRequest(method, "/api/v1/pc-limit", string.Empty,
            """{"downloadBytesPerSecond":1024}""", IsLoopback: true, ApiKey: "secret"));
        var read = await router.HandleAsync(new RestApiRequest("GET", "/api/v1/pc-limit", string.Empty, string.Empty,
            IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(503, change.StatusCode);
        Assert.Contains("not running as administrator", change.Body);
        Assert.False(applied);
        Assert.Equal(200, read.StatusCode);
        Assert.Contains("\"downloadBytesPerSecond\":4096", read.Body);
    }

    [Fact]
    public async Task PcLimit_BeforeTheEngineIsUp_Answers503()
    {
        var (_, _, router) = CreateRouter("secret");

        var response = await router.HandleAsync(new RestApiRequest("PUT", "/api/v1/pc-limit", string.Empty,
            """{"downloadBytesPerSecond":1024}""", IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("engine control unavailable", response.Body);
    }

    [Fact]
    public void Options_RemoteModeKeepsWildcardListenerPrefix()
    {
        var options = RestApiOptions.Create(
            "http://+:47719/",
            apiKey: "secret",
            remoteRequested: true,
            disabled: false);

        Assert.True(options.RemoteEnabled);
        Assert.Contains("http://+:47719/", options.Urls);
    }

    [Fact]
    public void Options_WithoutRemoteKeyFallsBackToLoopback()
    {
        var options = RestApiOptions.Create(
            "http://+:47719/",
            apiKey: null,
            remoteRequested: true,
            disabled: false);

        Assert.False(options.RemoteEnabled);
        Assert.Equal(RestApiOptions.DefaultUrl, Assert.Single(options.Urls));
    }

    internal static (ITrafficMonitor Monitor, RuleEngine Rules, RestApiRouter Router) CreateRouter(
        string? apiKey = null,
        IProcessVerifier? verifier = null,
        IGeoIpResolver? geoIpResolver = null,
        TrafficStatsDb? stats = null,
        Func<bool, CancellationToken, Task<DiagnosticInfo>>? pause = null,
        Func<CancellationToken, Task<DiagnosticInfo>>? resume = null,
        string? changesRefused = null,
        Func<WholePcLimit>? pcLimit = null,
        Func<WholePcLimit, WholePcLimit>? setPcLimit = null,
        BandwidthAlertTracker? alerts = null)
    {
        var monitor = new TrafficMonitor();
        var rules = new RuleEngine();
        var alertTracker = alerts ?? new BandwidthAlertTracker(monitor);
        var controlPlane = new ControlPlaneState
        {
            ChangesRefused = changesRefused,
            DiagnosticProvider = () => new DiagnosticInfo
            {
                Running = true,
                ActiveRules = rules.GetAllRules().Count,
                StartedAt = DateTime.UtcNow
            },
            StatsProvider = stats,
            PauseHandler = pause,
            ResumeHandler = resume,
            PcLimitProvider = pcLimit,
            PcLimitHandler = setPcLimit
        };
        var options = new RestApiOptions { ApiKey = apiKey };
        return (monitor, rules, new RestApiRouter(
            monitor,
            rules,
            alertTracker,
            controlPlane,
            options,
            verifier ?? new StubVerifier(),
            geoIpResolver ?? new StubGeoIpResolver(),
            new PluginManager(new PluginOptions(), NullLogger<PluginManager>.Instance)));
    }

    internal sealed class StubVerifier : IProcessVerifier
    {
        public string? LastPath { get; private set; }

        public Task<ProcessVerificationInfo> VerifyFileAsync(string processPath, CancellationToken ct = default)
        {
            LastPath = processPath;
            return Task.FromResult(new ProcessVerificationInfo
            {
                ProcessPath = processPath,
                Sha256 = "abc",
                Status = ProcessVerificationStatus.Clean,
                Harmless = 1
            });
        }

        public IReadOnlyList<ProcessVerificationInfo> GetCachedResults() => [];
    }

    internal sealed class StubGeoIpResolver : IGeoIpResolver
    {
        public string? LastIp { get; private set; }

        public Task<GeoIpInfo> ResolveAsync(System.Net.IPAddress ipAddress, CancellationToken ct = default)
        {
            LastIp = ipAddress.ToString();
            return Task.FromResult(new GeoIpInfo
            {
                IpAddress = LastIp,
                Status = GeoIpStatus.Located,
                CountryName = "United States",
                CountryCode = "US",
                CityName = "Mountain View"
            });
        }

        public GeoIpInfo? GetCached(System.Net.IPAddress ipAddress) => null;

        public IReadOnlyList<GeoIpInfo> GetCachedResults() => [];
    }
}
