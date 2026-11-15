using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Plugins;
using OpenNetLimit.Service.Security;
using Xunit;

namespace OpenNetLimit.Tests;

public class PipeServerTests
{
    [Fact]
    public async Task Client_IsIdentifiedAsTheUserItRunsAs()
    {
        using var self = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(self).IsInRole(WindowsBuiltInRole.Administrator);
        var logger = new RecordingLogger();
        var rules = new RuleEngine();
        var pipeName = $"OpenNetLimit-test-{Guid.NewGuid():N}";
        var server = CreateServer(rules, logger, pipeName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = server.StartAsync(cts.Token);

        // Connected the same way the UI's PipeClient connects.
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            await client.ConnectAsync(5000, cts.Token);
            using var reader = new StreamReader(client, Encoding.UTF8);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };

            await writer.WriteLineAsync("""ADD_RULE {"processName":"curl","downloadBytesPerSecond":262144}""");
            var reply = await reader.ReadLineAsync(cts.Token);

            var connected = Assert.Single(logger.Messages, message => message.StartsWith("IPC client connected:"));
            Assert.DoesNotContain("unknown", connected);
            Assert.Contains(Environment.UserName, connected, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith($"(admin={elevated})", connected);

            // An elevated client gets its rule; anyone else is told why not.
            if (elevated)
            {
                Assert.Contains("\"ok\":true", reply);
                Assert.Equal("curl", Assert.Single(rules.GetAllRules()).ProcessName);
            }
            else
            {
                Assert.Contains("admin required", reply);
                Assert.Empty(rules.GetAllRules());
            }
        }

        cts.Cancel();
        await serving;
    }

    private static PipeServer CreateServer(RuleEngine rules, RecordingLogger logger, string pipeName)
    {
        var monitor = new TrafficMonitor();
        return new PipeServer(
            monitor,
            rules,
            new BandwidthAlertTracker(monitor),
            new ControlPlaneState(),
            new UnusedVerifier(),
            new UnusedGeoIpResolver(),
            new PluginManager(new PluginOptions(), NullLogger<PluginManager>.Instance),
            logger)
        {
            PipeName = pipeName
        };
    }

    private sealed class RecordingLogger : ILogger<PipeServer>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class UnusedVerifier : IProcessVerifier
    {
        public Task<ProcessVerificationInfo> VerifyFileAsync(string processPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<ProcessVerificationInfo> GetCachedResults() => [];
    }

    private sealed class UnusedGeoIpResolver : IGeoIpResolver
    {
        public Task<GeoIpInfo> ResolveAsync(IPAddress ipAddress, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public GeoIpInfo? GetCached(IPAddress ipAddress) => null;

        public IReadOnlyList<GeoIpInfo> GetCachedResults() => [];
    }
}
