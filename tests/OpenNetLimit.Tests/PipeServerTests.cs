using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.Geo;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Storage;
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

    // A service that didn't load the rules took changes it then lost on the next save. Every
    // change is refused, edits and deletes of rules and alerts as well as adds.
    [Theory]
    [InlineData("ADD_RULE", """{"processName":"steam"}""")]
    [InlineData("UPDATE_RULE", """{"id":"{rule}","processName":"steam"}""")]
    [InlineData("REMOVE_RULE", "{rule}")]
    [InlineData("IMPORT_RULES", """[{"processName":"steam"}]""")]
    [InlineData("import_rules", """[{"processName":"steam"}]""")]
    [InlineData("ADD_ALERT_RULE", """{"processName":"steam","thresholdBytesPerSecond":1024}""")]
    [InlineData("UPDATE_ALERT_RULE", """{"id":"{alert}","processName":"steam","thresholdBytesPerSecond":1024}""")]
    [InlineData("REMOVE_ALERT_RULE", "{alert}")]
    public async Task Change_WhileChangesAreRefused_SaysWhyAndChangesNothing(string command, string payload)
    {
        var rules = new RuleEngine();
        var rule = new BandwidthRule { ProcessName = "chrome" };
        rules.AddRule(rule);
        var alerts = new BandwidthAlertTracker(new TrafficMonitor());
        var alert = new BandwidthAlertRule { Name = "Big download", ThresholdBytesPerSecond = 2048 };
        alerts.AddRule(alert);
        string State() => rules.ExportRules() + string.Join(",",
            alerts.GetRules().Select(a => $"{a.Id}:{a.ProcessName}:{a.ThresholdBytesPerSecond}"));
        var before = State();
        var controlPlane = new ControlPlaneState { ChangesRefused = "not running as administrator" };
        var server = CreateServer(rules, new RecordingLogger(), "unused", controlPlane, alerts);

        var reply = await server.ProcessCommandAsync(command,
            payload.Replace("{rule}", rule.Id.ToString()).Replace("{alert}", alert.Id.ToString()), CancellationToken.None);

        Assert.Contains("\"error\"", reply);
        Assert.Contains("not running as administrator", reply);
        Assert.Equal(before, State());
    }

    [Fact]
    public async Task RuleList_WhileChangesAreRefused_StillAnswers()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule { ProcessName = "chrome" });
        var controlPlane = new ControlPlaneState { ChangesRefused = "not running as administrator" };
        var server = CreateServer(rules, new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("RULES", string.Empty, CancellationToken.None);

        Assert.Contains("chrome", reply);
        Assert.DoesNotContain("\"error\"", reply);
    }

    [Fact]
    public async Task PcLimit_SetThenRead_GivesTheSameLimit()
    {
        var current = WholePcLimit.Off;
        var controlPlane = new ControlPlaneState
        {
            PcLimitProvider = () => current,
            PcLimitHandler = limit => current = limit
        };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var set = await server.ProcessCommandAsync("SET_PC_LIMIT",
            """{"downloadBytesPerSecond":1048576,"uploadBytesPerSecond":0,"includeLan":true}""", CancellationToken.None);
        var read = await server.ProcessCommandAsync("PC_LIMIT", string.Empty, CancellationToken.None);

        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 1_048_576, IncludeLan = true }, current);
        Assert.Equal(set, read);
        Assert.Contains("\"downloadBytesPerSecond\":1048576", read);
        Assert.DoesNotContain("isOff", read, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("STATS_HOURLY")]
    [InlineData("STATS_DAILY")]
    public async Task Stats_ForAnAppTypedWithExeOrOtherCase_FindItsHistory(string command)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"onl_test_{Guid.NewGuid()}.db");
        try
        {
            using var stats = new TrafficStatsDb(dbPath);
            stats.RecordSnapshot(new TrafficSnapshot
            {
                Processes = [new ProcessTrafficInfo { ProcessId = 1, ProcessName = "steam", CurrentDownloadBytesPerSecond = 4096 }]
            });
            var controlPlane = new ControlPlaneState { StatsProvider = stats };
            var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

            var reply = await server.ProcessCommandAsync(command, " Steam.exe ", CancellationToken.None);

            Assert.Contains("4096", reply);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("""{"downloadBytesPerSecond":-1}""", "negative")]
    [InlineData("""{"downloadBytesPerSecond":""", "invalid JSON")]
    [InlineData("null", "invalid whole-PC limit")]
    [InlineData("{}", "give downloadBytesPerSecond")]
    [InlineData("""{"includeLan":true}""", "give downloadBytesPerSecond")]
    [InlineData("""{"DownloadBytes":1048576}""", "DownloadBytes")]
    [InlineData("""{"downloadBytesPerSecond":"fast"}""", "invalid JSON")]
    public async Task PcLimit_ThatIsntALimit_IsRefused(string payload, string why)
    {
        var applied = false;
        var controlPlane = new ControlPlaneState { PcLimitHandler = limit => { applied = true; return limit; } };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("SET_PC_LIMIT", payload, CancellationToken.None);

        Assert.Contains("\"error\"", reply);
        Assert.Contains(why, reply);
        Assert.False(applied);
    }

    [Fact]
    public async Task PcLimit_TakesFieldNamesInAnyCase()
    {
        WholePcLimit? applied = null;
        var controlPlane = new ControlPlaneState { PcLimitHandler = limit => applied = limit };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("SET_PC_LIMIT", """{"DownloadBytesPerSecond":2048}""", CancellationToken.None);

        Assert.DoesNotContain("\"error\"", reply);
        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 2048 }, applied);
    }

    [Fact]
    public async Task PcLimit_BeforeTheEngineIsUp_SaysSo()
    {
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused");

        var reply = await server.ProcessCommandAsync("SET_PC_LIMIT", """{"uploadBytesPerSecond":1024}""", CancellationToken.None);
        var read = await server.ProcessCommandAsync("PC_LIMIT", string.Empty, CancellationToken.None);

        Assert.Contains("engine control unavailable", reply);
        Assert.Contains("\"downloadBytesPerSecond\":0", read);
    }

    [Fact]
    public async Task PcLimitChange_SetsOnlyWhatItGives()
    {
        // The app's dialog sends what was typed in. The rest is whatever is in effect when it
        // arrives, not what the dialog showed.
        var current = new WholePcLimit { DownloadBytesPerSecond = 2_097_152, IncludeLan = true };
        var controlPlane = new ControlPlaneState { PcLimitChangeHandler = change => current = change.ApplyTo(current) };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("CHANGE_PC_LIMIT", """{"uploadBytesPerSecond":524288}""", CancellationToken.None);

        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 2_097_152, UploadBytesPerSecond = 524_288, IncludeLan = true }, current);
        Assert.Contains("\"downloadBytesPerSecond\":2097152", reply);
        Assert.True(IpcProtocol.RequiresAdmin("CHANGE_PC_LIMIT"));
    }

    [Theory]
    [InlineData("{}", "give downloadBytesPerSecond, uploadBytesPerSecond or includeLan")]
    [InlineData("""{"includeLan":null}""", "be null. Leave it out")]
    [InlineData("""{"uploadBytesPerSecond":-1}""", "negative")]
    [InlineData("""{"upload":1}""", "upload")]
    [InlineData("""{"includeLan":true,"IncludeLan":false}""", "more than once")]
    [InlineData("[]", "invalid whole-PC limit")]
    [InlineData("""{"includeLan":""", "invalid JSON")]
    public async Task PcLimitChange_ThatIsntOne_IsRefused(string payload, string why)
    {
        var applied = false;
        var controlPlane = new ControlPlaneState { PcLimitChangeHandler = change => { applied = true; return WholePcLimit.Off; } };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync("CHANGE_PC_LIMIT", payload, CancellationToken.None);

        Assert.Contains("\"error\"", reply);
        Assert.Contains(why, reply);
        Assert.False(applied);
    }

    // The limit is saved state too, so a service that couldn't load its state can't take it.
    [Theory]
    [InlineData("SET_PC_LIMIT")]
    [InlineData("CHANGE_PC_LIMIT")]
    public async Task PcLimit_WhileChangesAreRefused_SaysWhyButStillReads(string command)
    {
        var applied = false;
        var controlPlane = new ControlPlaneState
        {
            ChangesRefused = "not running as administrator",
            PcLimitProvider = () => new WholePcLimit { UploadBytesPerSecond = 2048 },
            PcLimitHandler = limit => { applied = true; return limit; },
            PcLimitChangeHandler = change => { applied = true; return WholePcLimit.Off; }
        };
        var server = CreateServer(new RuleEngine(), new RecordingLogger(), "unused", controlPlane);

        var reply = await server.ProcessCommandAsync(command, """{"uploadBytesPerSecond":1024}""", CancellationToken.None);
        var read = await server.ProcessCommandAsync("PC_LIMIT", string.Empty, CancellationToken.None);

        Assert.Contains("not running as administrator", reply);
        Assert.False(applied);
        Assert.Contains("\"uploadBytesPerSecond\":2048", read);
    }

    private static PipeServer CreateServer(RuleEngine rules, RecordingLogger logger, string pipeName,
        ControlPlaneState? controlPlane = null, BandwidthAlertTracker? alerts = null)
    {
        var monitor = new TrafficMonitor();
        return new PipeServer(
            monitor,
            rules,
            alerts ?? new BandwidthAlertTracker(monitor),
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
