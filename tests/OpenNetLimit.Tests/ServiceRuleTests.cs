using System.Net;
using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.API;
using Xunit;
using Cli = OpenNetLimit.CLI.Program;

namespace OpenNetLimit.Tests;

/// <summary>Rules on a Windows service, which cover the svchost the service runs in.</summary>
public class ServiceRuleTests
{
    private const string SvchostPath = @"C:\Windows\System32\svchost.exe";

    private static FlowKey Key(ushort localPort) =>
        new(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.100"), localPort, IPAddress.Parse("93.184.216.34"), 443);

    [Fact]
    public void ServiceRule_MatchesTheProcessTheServiceRunsIn_AmongOthers()
    {
        var rule = new BandwidthRule { ServiceName = " bits " };

        Assert.Equal("bits", rule.ServiceName);
        Assert.True(rule.MatchesProcess("svchost", SvchostPath, ["BITS", "wuauserv"]));
        Assert.False(rule.MatchesProcess("svchost", SvchostPath, ["DoSvc"]));
        Assert.False(rule.MatchesProcess("svchost", SvchostPath, null));
        // An app named like the service isn't the service.
        Assert.False(rule.MatchesProcess("bits", null, null));
        Assert.Null(new BandwidthRule { ServiceName = "  " }.ServiceName);
        Assert.Equal("bits", rule.Clone().ServiceName);
    }

    [Theory]
    [InlineData("svchost", null)]
    [InlineData(null, @"C:\Windows\System32\*")]
    [InlineData(null, SvchostPath)]
    public void NameAndPathRules_NeverMatchASvchost(string? name, string? path)
    {
        // Every service in it would slow down with the one the rule was meant for.
        var rule = new BandwidthRule { ProcessName = name, ProcessPath = path };

        Assert.False(rule.MatchesProcess("svchost", SvchostPath, ["BITS"]));
    }

    [Fact]
    public void AFolderRule_StillMatchesTheOtherAppsInTheFolder()
    {
        var rule = new BandwidthRule { ProcessPath = @"C:\Windows\System32\*" };

        Assert.True(rule.MatchesProcess("curl", @"C:\Windows\System32\curl.exe", null));
    }

    [Fact]
    public void AddRule_OnAService_IsKeptAndSaved()
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule { ServiceName = "DoSvc", DownloadBytesPerSecond = 1024 });
        var path = Path.Combine(Path.GetTempPath(), $"onl-service-rule-{Guid.NewGuid():N}.json");
        try
        {
            rules.SaveRules(path);
            var loaded = new RuleEngine();
            loaded.LoadRules(path);

            var rule = Assert.Single(loaded.GetAllRules());
            Assert.Equal("DoSvc", rule.ServiceName);
            Assert.Null(rule.ProcessName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("BITS", "chrome", null, "can't also name a process or a path")]
    [InlineData("BITS", null, @"C:\Tools\*", "can't also name a process or a path")]
    [InlineData(@"..\BITS", null, null, "isn't a Windows service name")]
    [InlineData("Dnscache", null, null, "part of Windows networking")]
    [InlineData("rpcss", null, null, "part of Windows networking")]
    [InlineData("SharedAccess", null, null, "part of Windows networking")]
    public void AddRule_ServiceRuleThatCanNotWork_IsRefused(string service, string? name, string? path, string reason)
    {
        var rules = new RuleEngine();

        var ex = Assert.Throws<ArgumentException>(() => rules.AddRule(
            new BandwidthRule { ServiceName = service, ProcessName = name, ProcessPath = path }));

        Assert.Contains(reason, ex.Message);
        Assert.Empty(rules.GetAllRules());
    }

    [Fact]
    public void AddRule_ServiceNameLongerThanWindowsAllows_IsRefused()
    {
        var rules = new RuleEngine();

        rules.AddRule(new BandwidthRule { ServiceName = new string('a', 256) });
        Assert.Throws<ArgumentException>(() => rules.AddRule(new BandwidthRule { ServiceName = new string('a', 257) }));
        Assert.Single(rules.GetAllRules());
    }

    [Theory]
    [InlineData("svchost")]
    [InlineData("SvcHost.exe")]
    public void AddRule_OnSvchostItself_PointsAtServiceRules(string name)
    {
        var rules = new RuleEngine();

        var ex = Assert.Throws<ArgumentException>(() => rules.AddRule(
            new BandwidthRule { ProcessName = name, DownloadBytesPerSecond = 1024 }));

        Assert.Contains("by its service name", ex.Message);
        Assert.Empty(rules.GetAllRules());
    }

    [Theory]
    [InlineData(SvchostPath)]
    [InlineData("\"C:\\Windows\\SysWOW64\\SVCHOST.EXE\"")]
    [InlineData(@"C:\*\svchost.exe")]
    public void AddRule_OnSvchostByPath_PointsAtServiceRules(string path)
    {
        // It never matches either, so it would sit in the list limiting nothing.
        var rules = new RuleEngine();

        var ex = Assert.Throws<ArgumentException>(() => rules.AddRule(
            new BandwidthRule { ProcessPath = path, DownloadBytesPerSecond = 1024 }));

        Assert.Contains("by its service name", ex.Message);
        Assert.Empty(rules.GetAllRules());
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\*")]
    [InlineData(@"C:\Windows\System32\svc*.exe")]
    [InlineData(@"C:\Tools\svchostmonitor.exe")]
    public void AddRule_APathThatOnlyTakesSvchostIn_IsKept(string path)
    {
        var rules = new RuleEngine();

        rules.AddRule(new BandwidthRule { ProcessPath = path, DownloadBytesPerSecond = 1024 });

        Assert.Single(rules.GetAllRules());
    }

    [Fact]
    public void AddRule_ACopyOfSvchostByNameAndPath_IsTakenAndLimitsOnlyTheCopy()
    {
        var rules = new RuleEngine();
        var copy = new BandwidthRule { ProcessName = "svchost", ProcessPath = @"C:\Tools\svchost.exe", DownloadBytesPerSecond = 1024 };

        rules.AddRule(copy);

        Assert.Equal(copy.Id, Assert.Single(rules.GetAllRules()).Id);
        Assert.True(copy.MatchesProcess("svchost", @"C:\Tools\svchost.exe"));
        Assert.False(copy.MatchesProcess("svchost", SvchostPath));
        Assert.False(copy.MatchesProcess("svchost", @"C:\Other\svchost.exe"));
        Assert.Equal(copy.Id, rules.FindMatchingRule("svchost", @"C:\Tools\svchost.exe")?.Id);
        // A svchost whose path isn't read yet is taken for Windows' own.
        Assert.Null(rules.FindMatchingRule("svchost", null));
        Assert.Null(rules.FindMatchingRule("svchost", WindowsSvchost));
    }

    private static readonly string WindowsSvchost =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe");

    [Fact]
    public void UpdateRule_CantMoveACopysRuleOntoSvchost()
    {
        var rules = new RuleEngine();
        var copy = new BandwidthRule { ProcessName = "svchost", ProcessPath = @"C:\Tools\svchost.exe", DownloadBytesPerSecond = 1024 };
        rules.AddRule(copy);

        var onWindows = copy.Clone();
        onWindows.ProcessPath = WindowsSvchost;
        Assert.Throws<ArgumentException>(() => rules.UpdateRule(onWindows));
        var noPath = copy.Clone();
        noPath.ProcessPath = null;
        Assert.Throws<ArgumentException>(() => rules.UpdateRule(noPath));

        Assert.Equal(@"C:\Tools\svchost.exe", Assert.Single(rules.GetAllRules()).ProcessPath);
    }

    [Fact]
    public void UpdateRule_CantAddOrMakeARuleOnSvchost()
    {
        // Update adds a rule whose id isn't in the list, so it went around AddRule's check.
        var rules = new RuleEngine();
        var curl = new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 1024 };
        rules.AddRule(curl);

        Assert.Throws<ArgumentException>(() => rules.UpdateRule(
            new BandwidthRule { ProcessName = "svchost", DownloadBytesPerSecond = 1024 }));
        var moved = curl.Clone();
        moved.ProcessName = null;
        moved.ProcessPath = SvchostPath;
        Assert.Throws<ArgumentException>(() => rules.UpdateRule(moved));

        var kept = Assert.Single(rules.GetAllRules());
        Assert.Equal(("curl", (string?)null), (kept.ProcessName, kept.ProcessPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportRules_CantBringInARuleOnSvchost(bool replace)
    {
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 1024 });

        var ex = Assert.Throws<ArgumentException>(() => rules.ImportRules(
            $$"""[{"processName":"steam","downloadBytesPerSecond":1024},{"processPath":{{JsonSerializer.Serialize(SvchostPath)}},"downloadBytesPerSecond":1024}]""",
            replace));

        Assert.Contains("by its service name", ex.Message);
        Assert.Equal("curl", Assert.Single(rules.GetAllRules()).ProcessName);
    }

    [Fact]
    public void ASvchostRuleFromBefore_StillLoadsAndCanBeTurnedOff_ButLimitsNothing()
    {
        // Saved before service rules existed, so it comes in through the service's own file.
        var file = Path.Combine(Path.GetTempPath(), $"onl-svchost-rule-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{"version":1,"rules":[{"processName":"svchost","downloadBytesPerSecond":1024}]}""");
        var rules = new RuleEngine();
        try
        {
            rules.LoadRules(file);
        }
        finally
        {
            File.Delete(file);
        }
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "BITS");
        flows.RegisterFlow(Key(100), 200, "svchost", SvchostPath);

        new RuleReconciler(rules, limiter, flows, serviceHosts: hosts).Reconcile();
        Assert.False(limiter.HasLimit(200));

        var rule = Assert.Single(rules.GetAllRules());
        rule.Enabled = false;
        rules.UpdateRule(rule);
        Assert.False(rules.GetRule(rule.Id)!.Enabled);

        // An export of the list carries it back over itself, with or without replacing the rest.
        var exported = rules.ExportRules();
        rules.ImportRules(exported);
        rules.ImportRules(exported, replace: true);
        Assert.Equal(rule.Id, Assert.Single(rules.GetAllRules()).Id);
    }

    [Fact]
    public void ServiceRule_LimitsTheSvchostTheServiceRunsIn()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "BITS");
        hosts.Set(201, "DoSvc");
        flows.RegisterFlow(Key(100), 200, "svchost", SvchostPath);
        flows.RegisterFlow(Key(101), 201, "svchost", SvchostPath);
        rules.AddRule(new BandwidthRule { ServiceName = "BITS", DownloadBytesPerSecond = 65_536 });
        var reconciler = new RuleReconciler(rules, limiter, flows, serviceHosts: hosts);

        reconciler.Reconcile();
        Assert.True(limiter.HasLimit(200));
        Assert.False(limiter.HasLimit(201));

        // A svchost that opens its first connection after the rule was set.
        hosts.Set(202, "BITS");
        reconciler.EnsureLimit(202, "svchost", SvchostPath);
        Assert.True(limiter.HasLimit(202));
    }

    [Theory]
    [InlineData("BITS", "Dnscache")]
    [InlineData("BITS", "RpcSs")]
    public void ServiceRule_LeavesAloneASvchostWindowsNetworkingRunsIn(string service, string networking)
    {
        // On a PC short of memory Windows puts many services in one svchost. Slowing BITS there
        // would slow every DNS lookup with it.
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var hosts = new FakeServiceHosts();
        hosts.Set(200, service, networking);
        flows.RegisterFlow(Key(100), 200, "svchost", SvchostPath);
        rules.AddRule(new BandwidthRule { ServiceName = service, DownloadBytesPerSecond = 65_536 });
        var reconciler = new RuleReconciler(rules, limiter, flows, serviceHosts: hosts);

        reconciler.Reconcile();
        reconciler.EnsureLimit(200, "svchost", SvchostPath);

        Assert.False(limiter.HasLimit(200));
    }

    [Fact]
    public void BlockOnAService_DropsWhatItsSvchostSends_ButNeverOneThatAlsoRunsDns()
    {
        var rules = new RuleEngine();
        var hosts = new FakeServiceHosts();
        hosts.Set(201, "DoSvc");
        hosts.Set(202, "DoSvc", "Dnscache");
        var block = new BandwidthRule { ServiceName = "DoSvc", Action = RuleAction.Block };
        rules.AddRule(block);

        (BandwidthRule? Rule, bool Dropped) Packet(uint processId, bool quotaBlocked = false)
        {
            var (rule, protection) = WinDivertInterceptor.Attribute(rules, hosts, processId, "svchost", SvchostPath,
                IPAddress.Parse("93.184.216.34"), 443, "Tcp", resolvedDomain: null);
            return (rule, WinDivertInterceptor.Drops(protection, rule, quotaBlocked));
        }

        Assert.Equal(block.Id, Packet(201).Rule?.Id);
        Assert.True(Packet(201).Dropped);
        // Blocking DoSvc there would cut DNS for the whole PC.
        Assert.Equal(block.Id, Packet(202).Rule?.Id);
        Assert.False(Packet(202).Dropped);
        // Nothing reaches a svchost the last read hasn't seen, so it's kept safe until a read names its services.
        Assert.Null(Packet(203).Rule);
        Assert.False(Packet(203).Dropped);
        // A used-up quota that blocks stops a plain svchost but never one Windows needs.
        Assert.True(Packet(201, quotaBlocked: true).Dropped);
        Assert.False(Packet(202, quotaBlocked: true).Dropped);
        Assert.False(Packet(203, quotaBlocked: true).Dropped);
    }

    [Fact]
    public void QuotaOnAService_CountsTheSvchostItRunsIn_AndStopsItWhenUsedUp()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "BITS");
        hosts.Set(201, "DoSvc");
        var rules = new RuleEngine();
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts);
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = new RuleReconciler(rules, limiter, flows, quotas, hosts);
        rules.AddRule(new BandwidthRule
        {
            ServiceName = "BITS",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        });
        flows.RegisterFlow(Key(100), 200, "svchost", SvchostPath);
        flows.RegisterFlow(Key(101), 201, "svchost", SvchostPath);
        monitor.RecordBytes(200, "svchost", 600, isUpload: false, SvchostPath);
        monitor.RecordBytes(201, "svchost", 50_000, isUpload: false, SvchostPath);

        quotas.Update();
        var state = quotas.GetQuotaState("BITS");
        Assert.NotNull(state);
        Assert.Equal(600, state.UsedBytes);
        Assert.Equal("BITS", state.ServiceName);

        monitor.RecordBytes(200, "svchost", 600, isUpload: true, SvchostPath);
        quotas.Update();
        Assert.True(reconciler.ReconcileIfChanged());
        Assert.True(limiter.IsBlocked(200));
        Assert.False(limiter.IsBlocked(201));
    }

    [Fact]
    public void QuotaMovedFromAServiceToAnAppOfTheSameName_StartsOver()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "BITS");
        var rules = new RuleEngine();
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts);
        var quotas = new QuotaTracker(rules, monitor);
        var rule = new BandwidthRule
        {
            ServiceName = "BITS",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        };
        rules.AddRule(rule);
        monitor.RecordBytes(200, "svchost", 1_200, isUpload: false, SvchostPath);
        quotas.Update();
        Assert.True(quotas.GetQuotaState(rule.Id)!.IsExceeded);

        rule.ServiceName = null;
        rule.ProcessName = "bits";
        rules.UpdateRule(rule);
        quotas.Update();

        var state = quotas.GetQuotaState(rule.Id)!;
        Assert.False(state.IsExceeded);
        Assert.Equal(0, state.UsedBytes);
        Assert.Null(state.ServiceName);
    }

    [Fact]
    public void ServiceRule_ASvchostStartedSinceTheLastRead_IsLimitedWhenItConnects()
    {
        var running = new Dictionary<uint, string[]> { [200] = ["DoSvc"] };
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var queued = new List<Action>();
        using var hosts = new ServiceHosts(() => new Dictionary<uint, string[]>(running), () => now, queue: queued.Add);
        hosts.Refresh();
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = new RuleReconciler(rules, limiter, flows, serviceHosts: hosts);
        hosts.Changed += reconciler.Reconcile;
        rules.AddRule(new BandwidthRule { ServiceName = "BITS", DownloadBytesPerSecond = 65_536 });

        running[201] = ["BITS"];
        now += ServiceHosts.MinReadGap;
        flows.RegisterFlow(Key(12345), 201, "svchost", SvchostPath);

        // The packet loop doesn't wait for the service list. The read it asked for limits the
        // svchost once it's done.
        Assert.False(limiter.HasLimit(201));
        Assert.Single(queued)();
        Assert.True(limiter.HasLimit(201));
        Assert.Equal(new[] { "BITS" }, hosts.ServicesIn(201));
    }

    private sealed class OnDispose(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public void ServiceRule_ASvchostThatExits_LosesItsLimitBeforeItsIdIsFree()
    {
        // Its id used to keep the limit until the next read, 30 s on, so chrome or a DHCP
        // svchost that got the id was limited or blocked in its place.
        var running = new Dictionary<uint, string[]> { [200] = ["DoSvc"] };
        var limiter = new ProcessRateLimiter();
        Action? exit = null;
        bool? limitedWhenLetGo = null;
        using var hosts = new ServiceHosts(() => new Dictionary<uint, string[]>(running), () => DateTime.UtcNow,
            (processId, _, exited) =>
            {
                exit = exited;
                return new OnDispose(() => limitedWhenLetGo = limiter.HasLimit(processId));
            }, queue: _ => { });
        hosts.Refresh();
        var rules = new RuleEngine();
        var flows = new FlowTracker();
        var reconciler = new RuleReconciler(rules, limiter, flows, serviceHosts: hosts);
        hosts.Changed += reconciler.Reconcile;
        rules.AddRule(new BandwidthRule { ServiceName = "DoSvc", DownloadBytesPerSecond = 65_536 });
        flows.RegisterFlow(Key(12345), 200, "svchost", SvchostPath);
        Assert.True(limiter.HasLimit(200));

        running.Remove(200);
        exit!();

        Assert.False(limiter.HasLimit(200));
        Assert.False(limitedWhenLetGo);
    }

    [Fact]
    public void Snapshot_ASvchostThatExited_KeepsItsServices_SoTheirQuotasKeepItsBytes()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "DoSvc");
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();

        hosts.Remove(200);
        monitor.SampleRates();

        var info = monitor.TakeSnapshot().Processes.Single();
        Assert.Equal(new[] { "DoSvc" }, info.Services);
        Assert.False(info.IsProtected);
        Assert.Equal(100, info.TotalBytesReceived);
    }

    [Fact]
    public void Snapshot_ShowsTheServicesInEachSvchost_AndWhichOnesRulesLeaveAlone()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "BITS", "wuauserv");
        hosts.Set(201, "Dnscache");
        hosts.Set(202, "BITS", "Dnscache");
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts);
        foreach (uint pid in new uint[] { 200, 201, 202, 203 })
            monitor.RecordBytes(pid, "svchost", 100, isUpload: false, SvchostPath);
        monitor.RecordBytes(1, "curl", 100, isUpload: false);
        monitor.SampleRates();

        var p = monitor.TakeSnapshot().Processes.ToDictionary(x => x.ProcessId);
        Assert.Equal(new[] { "BITS", "wuauserv" }, p[200].Services);
        Assert.Equal("BITS", p[200].ServiceName);
        Assert.False(p[200].IsProtected);
        Assert.Empty(p[200].NetworkingServices!);
        Assert.True(p[201].IsProtected);
        Assert.Equal(new[] { "Dnscache" }, p[201].NetworkingServices);
        Assert.True(p[202].IsProtected);
        Assert.Equal(new[] { "Dnscache" }, p[202].NetworkingServices);
        // Started since the last read of the service list.
        Assert.True(p[203].IsProtected);
        Assert.Null(p[203].Services);
        Assert.Empty(p[203].NetworkingServices!);
        Assert.False(p[1].IsProtected);
        Assert.Null(p[1].Services);

        hosts.Set(203, "DoSvc");
        monitor.SampleRates();
        var later = monitor.TakeSnapshot().Processes.Single(x => x.ProcessId == 203);
        Assert.Equal(new[] { "DoSvc" }, later.Services);
        Assert.False(later.IsProtected);
    }

    [Fact]
    public async Task RestApi_AddsARuleOnAService_AndRefusesOneOnSvchost()
    {
        var (_, rules, router) = RestApiRouterTests.CreateRouter("secret");

        var added = await router.HandleAsync(new RestApiRequest("POST", "/api/v1/rules", string.Empty,
            """{"serviceName":"DoSvc","downloadBytesPerSecond":65536}""", IsLoopback: true, ApiKey: "secret"));
        var refused = await router.HandleAsync(new RestApiRequest("POST", "/api/v1/rules", string.Empty,
            """{"processName":"svchost","downloadBytesPerSecond":65536}""", IsLoopback: true, ApiKey: "secret"));

        Assert.Equal(201, added.StatusCode);
        Assert.Equal("DoSvc", Assert.Single(rules.GetAllRules()).ServiceName);
        Assert.Equal(400, refused.StatusCode);
        Assert.Contains("by its service name", refused.Body);
    }

    [Theory]
    [InlineData(new[] { "--service", "DoSvc", "--download", "64" }, "serviceName", "DoSvc")]
    [InlineData(new[] { "--process", "steam" }, "processName", "steam")]
    public void Cli_RuleTarget_IsTheServiceOrTheApp(string[] args, string key, string value)
    {
        var error = new StringWriter();

        var body = Cli.RuleTarget(args, error);

        Assert.NotNull(body);
        Assert.Equal(value, Assert.Single(body).Value);
        Assert.Equal(key, Assert.Single(body).Key);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("--download 64")]
    [InlineData("--process steam --service DoSvc")]
    public void Cli_RuleTarget_NeedsExactlyOneOfThem(string commandLine)
    {
        var error = new StringWriter();

        Assert.Null(Cli.RuleTarget(commandLine.Split(' '), error));
        Assert.Contains("--process or --service", error.ToString());
    }
}
