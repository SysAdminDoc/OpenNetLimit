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

    [Theory]
    [InlineData("""{"stayPaused":true}""", true)]
    [InlineData("""{"stayPaused":false}""", false)]
    [InlineData("", false)]
    public async Task Pause_ReadsTheStayChoiceAndAnswersWithTheStatus(string payload, bool expectedStay)
    {
        bool? stay = null;
        var controlPlane = new ControlPlaneState
        {
            PauseHandler = (stayPaused, _) =>
            {
                stay = stayPaused;
                return Task.FromResult(new OpenNetLimit.Core.IPC.DiagnosticInfo { Paused = true, StayPaused = stayPaused });
            }
        };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("PAUSE", payload, CancellationToken.None);

        Assert.Equal(expectedStay, stay);
        Assert.Contains("\"paused\":true", reply);
    }

    [Fact]
    public async Task Pause_BadPayload_IsRefusedWithoutPausing()
    {
        bool called = false;
        var controlPlane = new ControlPlaneState
        {
            PauseHandler = (_, _) => { called = true; return Task.FromResult(new OpenNetLimit.Core.IPC.DiagnosticInfo()); }
        };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("PAUSE", "{not json", CancellationToken.None);

        Assert.Contains("invalid JSON", reply);
        Assert.False(called);
    }

    [Theory]
    [InlineData("PAUSE")]
    [InlineData("RESUME")]
    public async Task PauseAndResume_BeforeTheEngineIsUp_SayItIsUnavailable(string command)
    {
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused");

        var reply = await server.ProcessCommandAsync(command, string.Empty, CancellationToken.None);

        Assert.Contains("engine control unavailable", reply);
    }

    [Fact]
    public async Task ExportRules_FitsOnTheOneLineAClientReads()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1024 });
        rules.AddRule(new BandwidthRule { ProcessName = "steam", Action = RuleAction.Block });
        var server = CreateServer(rules, new RecordingLogger(), "unused");

        var reply = await server.ProcessCommandAsync("EXPORT_RULES", string.Empty, CancellationToken.None);

        Assert.DoesNotContain('\n', reply);
        var other = new RuleEngine();
        other.ImportRules(reply);
        Assert.Equal(new[] { "chrome", "steam" }, other.GetAllRules().Select(r => r.ProcessName).Order());
    }

    // An uncaught refusal from the rule engine used to drop the client's connection.
    [Theory]
    [InlineData("ADD_RULE", """{"downloadBytesPerSecond":1024}""")]
    [InlineData("UPDATE_RULE", """{"id":"8c0f8b0e-4a39-4f4e-9d55-4f7f6c7a0c11","processName":" "}""")]
    [InlineData("IMPORT_RULES", """[{"processName":"chrome"},{"action":1}]""")]
    public async Task RuleWithoutAProgram_IsRefusedWithAReason(string command, string payload)
    {
        var rules = new RuleEngine();
        var server = CreateServer(rules, new RecordingLogger(), "unused");

        var reply = await server.ProcessCommandAsync(command, payload, CancellationToken.None);

        Assert.Contains("\"error\"", reply);
        Assert.Contains("ProcessName or ProcessPath", reply);
        Assert.Empty(rules.GetAllRules());
    }

    [Fact]
    public async Task ImportWithANullRule_IsRefusedWithAReason()
    {
        // It threw a NullReferenceException, which dropped the client's connection.
        var rules = new RuleEngine();
        var server = CreateServer(rules, new RecordingLogger(), "unused");

        var reply = await server.ProcessCommandAsync("IMPORT_RULES", """[{"processName":"chrome"},null]""", CancellationToken.None);

        Assert.Contains("\"error\"", reply);
        Assert.Contains("empty", reply);
        Assert.Empty(rules.GetAllRules());
    }

    private static PipeServer CreateServer(RuleEngine rules, RecordingLogger logger, string pipeName,
        ControlPlaneState? controlPlane = null)
    {
        var monitor = new TrafficMonitor();
        return new PipeServer(
            monitor,
            rules,
            new BandwidthAlertTracker(monitor),
            controlPlane ?? new ControlPlaneState(),
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
