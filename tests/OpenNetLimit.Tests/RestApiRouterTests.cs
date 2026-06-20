using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.API;
using OpenNetLimit.Service.Control;
using Xunit;

namespace OpenNetLimit.Tests;

public class RestApiRouterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void LocalRead_AllowsStatusWithoutApiKey()
    {
        var (_, _, router) = CreateRouter();

        var response = router.Handle(new RestApiRequest(
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
    public void RemoteRead_RequiresConfiguredApiKey()
    {
        var (_, _, router) = CreateRouter();

        var response = router.Handle(new RestApiRequest(
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
    public void LocalMutation_RequiresApiKey()
    {
        var (_, _, router) = CreateRouter();
        var body = JsonSerializer.Serialize(new BandwidthRule { ProcessName = "chrome" }, JsonOptions);

        var response = router.Handle(new RestApiRequest(
            "POST",
            "/api/v1/rules",
            string.Empty,
            body,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public void MutationWithKey_AddsRule()
    {
        var (_, rules, router) = CreateRouter("secret");
        var body = JsonSerializer.Serialize(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000
        }, JsonOptions);

        var response = router.Handle(new RestApiRequest(
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
    public void PutRule_UsesPathId()
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

        var response = router.Handle(new RestApiRequest(
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
    public void DeleteRule_RemovesRule()
    {
        var (_, rules, router) = CreateRouter("secret");
        var rule = new BandwidthRule { ProcessName = "chrome" };
        rules.AddRule(rule);

        var response = router.Handle(new RestApiRequest(
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
    public void StatsUnavailable_ReturnsServiceUnavailable()
    {
        var (_, _, router) = CreateRouter();

        var response = router.Handle(new RestApiRequest(
            "GET",
            "/api/v1/stats/top",
            string.Empty,
            string.Empty,
            IsLoopback: true,
            ApiKey: null));

        Assert.Equal(503, response.StatusCode);
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

    private static (ITrafficMonitor Monitor, RuleEngine Rules, RestApiRouter Router) CreateRouter(string? apiKey = null)
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
        return (monitor, rules, new RestApiRouter(monitor, rules, controlPlane, options));
    }
}
