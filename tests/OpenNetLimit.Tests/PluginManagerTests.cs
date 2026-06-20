using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Service.Plugins;
using Xunit;

namespace OpenNetLimit.Tests;

public class PluginManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"onl_plugins_{Guid.NewGuid()}");

    [Fact]
    public void Reload_WhenDisabled_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), ValidManifest("hook"));
        var manager = new PluginManager(
            new PluginOptions { Enabled = false, PluginDirectory = _dir },
            NullLogger<PluginManager>.Instance);

        var plugins = manager.Reload();

        Assert.Empty(plugins);
    }

    [Fact]
    public void Reload_LoadsValidManifest()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), ValidManifest("hook"));
        var manager = new PluginManager(
            new PluginOptions { Enabled = true, PluginDirectory = _dir },
            NullLogger<PluginManager>.Instance);

        var plugin = Assert.Single(manager.Reload());

        Assert.Equal("hook", plugin.Id);
        Assert.Equal("Alert Hook", plugin.Name);
        Assert.Contains("alert.triggered", plugin.EventSubscriptions);
    }

    [Fact]
    public void Reload_IgnoresInvalidManifest()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.json"), """{"id":"bad plugin","eventSubscriptions":[],"webhookUrl":"file:///tmp/x"}""");
        var manager = new PluginManager(
            new PluginOptions { Enabled = true, PluginDirectory = _dir },
            NullLogger<PluginManager>.Instance);

        Assert.Empty(manager.Reload());
    }

    [Fact]
    public async Task DispatchAsync_PostsSubscribedEvent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), ValidManifest("hook"));
        string? posted = null;
        var http = new HttpClient(new StubHandler(request =>
        {
            posted = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var manager = new PluginManager(
            new PluginOptions { Enabled = true, PluginDirectory = _dir },
            NullLogger<PluginManager>.Instance,
            http);
        manager.Reload();

        await manager.DispatchAsync("alert.triggered", new BandwidthAlertEvent { ProcessName = "chrome" });

        Assert.NotNull(posted);
        using var doc = System.Text.Json.JsonDocument.Parse(posted);
        Assert.Equal("hook", doc.RootElement.GetProperty("pluginId").GetString());
        Assert.Equal("alert.triggered", doc.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("chrome", doc.RootElement.GetProperty("payload").GetProperty("processName").GetString());
    }

    [Fact]
    public async Task DispatchAsync_SkipsUnsubscribedEvent()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), ValidManifest("hook"));
        var calls = 0;
        var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var manager = new PluginManager(
            new PluginOptions { Enabled = true, PluginDirectory = _dir },
            NullLogger<PluginManager>.Instance,
            http);
        manager.Reload();

        await manager.DispatchAsync("geoip.resolved", new { ip = "8.8.8.8" });

        Assert.Equal(0, calls);
    }

    private static string ValidManifest(string id) => $$"""
        {
          "id": "{{id}}",
          "name": "Alert Hook",
          "version": "1.0.0",
          "enabled": true,
          "eventSubscriptions": [ "alert.triggered" ],
          "webhookUrl": "https://example.test/hook"
        }
        """;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
