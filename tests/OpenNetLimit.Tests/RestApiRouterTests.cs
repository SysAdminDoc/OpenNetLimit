using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.API;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.Security;
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

    private static (ITrafficMonitor Monitor, RuleEngine Rules, RestApiRouter Router) CreateRouter(
        string? apiKey = null,
        IProcessVerifier? verifier = null,
        IGeoIpResolver? geoIpResolver = null)
    {
        var monitor = new TrafficMonitor();
        var rules = new RuleEngine();
        var controlPlane = new ControlPlaneState
        {
            DiagnosticProvider = () => new DiagnosticInfo
            {
                Running = true,
                ActiveRules = rules.GetAllRules().Count,
                StartedAt = DateTime.UtcNow
            }
        };
        var options = new RestApiOptions { ApiKey = apiKey };
        return (monitor, rules, new RestApiRouter(
            monitor,
            rules,
            controlPlane,
            options,
            verifier ?? new StubVerifier(),
            geoIpResolver ?? new StubGeoIpResolver()));
    }

    private sealed class StubVerifier : IProcessVerifier
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

    private sealed class StubGeoIpResolver : IGeoIpResolver
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
