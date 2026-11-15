using System.Diagnostics;
using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class TrafficMonitorTests
{
    [Fact]
    public void ReadingASnapshot_LeavesTheSpeedForTheNextReader()
    {
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 5000, isUpload: false);
        monitor.RecordBytes(1, "curl", 700, isUpload: true);
        monitor.SampleRates();

        var app = monitor.TakeSnapshot();
        var api = monitor.TakeSnapshot();

        foreach (var snapshot in new[] { app, api })
        {
            var curl = Assert.Single(snapshot.Processes);
            Assert.Equal(5000, curl.CurrentDownloadBytesPerSecond);
            Assert.Equal(700, curl.CurrentUploadBytesPerSecond);
            Assert.Equal(5000, snapshot.TotalDownloadBytesPerSecond);
            Assert.Equal(700, snapshot.TotalUploadBytesPerSecond);
        }
    }

    [Fact]
    public void Snapshot_ShowsTheLimitAProcessIsHeldTo()
    {
        var limiter = new ProcessRateLimiter();
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), limiter, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 5000, isUpload: false);
        monitor.RecordBytes(2, "git", 5000, isUpload: false);
        limiter.SetLimit(1, 262_144, 0);
        monitor.SampleRates();

        var snapshot = monitor.TakeSnapshot();
        var curl = snapshot.Processes.Single(p => p.ProcessId == 1);
        var git = snapshot.Processes.Single(p => p.ProcessId == 2);
        Assert.Equal(262_144, curl.DownloadLimitBytesPerSecond);
        Assert.Null(curl.UploadLimitBytesPerSecond);
        Assert.Null(git.DownloadLimitBytesPerSecond);

        limiter.RemoveLimit(1);
        monitor.SampleRates();
        Assert.Null(monitor.TakeSnapshot().Processes.Single(p => p.ProcessId == 1).DownloadLimitBytesPerSecond);
    }

    [Fact]
    public void Usage_CountsEveryByteOnce_WhoeverElseReadsTheMonitor()
    {
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 1000, isUpload: false);
        monitor.RecordBytes(1, "curl", 300, isUpload: true);
        monitor.SampleRates();
        monitor.TakeSnapshot();

        var first = Assert.Single(monitor.TakeUsage().Processes);
        Assert.Equal(1000, first.CurrentDownloadBytesPerSecond);
        Assert.Equal(300, first.CurrentUploadBytesPerSecond);

        monitor.RecordBytes(1, "curl", 500, isUpload: false);
        monitor.SampleRates();
        monitor.TakeSnapshot();

        var second = Assert.Single(monitor.TakeUsage().Processes);
        Assert.Equal(500, second.CurrentDownloadBytesPerSecond);
        Assert.Equal(0, second.CurrentUploadBytesPerSecond);

        var idle = monitor.TakeUsage();
        Assert.Equal(0, idle.TotalDownloadBytesPerSecond);
        Assert.Equal(0, idle.TotalUploadBytesPerSecond);
    }

    private const string SvchostPath = @"C:\Windows\System32\svchost.exe";
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private sealed class FakeProcess : IHeldProcess
    {
        public bool HasExited { get; set; }
        public DateTime? IdFreeSince { get; init; }
        public bool LetGo { get; private set; }
        public Action? OnLetGo { get; init; }

        public void Dispose()
        {
            LetGo = true;
            OnLetGo?.Invoke();
        }
    }

    /// <summary>What <see cref="HoldProcess"/> hands out for each ID; an ID with nothing is gone.</summary>
    private readonly Dictionary<uint, IHeldProcess> _running = new();

    /// <summary>When the process with each ID started, for IDs a test gives one.</summary>
    private readonly Dictionary<uint, DateTime> _startedAt = new();
    private DateTime _now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private TrafficMonitor Held(IServiceHosts? hosts = null, IFlowTracker? flows = null) =>
        new(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts, Hold, () => _now, flows);

    /// <summary>Like <see cref="HeldProcess.Hold"/>: a process that started after the entry was seen isn't the one seen.</summary>
    private IHeldProcess? Hold(uint processId, DateTime firstSeenUtc)
    {
        if (_startedAt.TryGetValue(processId, out var started) && HeldProcess.StartedSince(started, firstSeenUtc, _now))
            return new FakeProcess { HasExited = true, IdFreeSince = started + TimeSpan.FromMilliseconds(16) };
        return _running.GetValueOrDefault(processId);
    }

    private static FlowKey Flow(ushort localPort) =>
        new(TransportProtocol.Tcp, IPAddress.Parse("192.168.1.100"), localPort, IPAddress.Parse("93.184.216.34"), 443);

    [Fact]
    public void AProcessThatExits_TakesItsFlowsWithIt_SoALatePacketCantNameTheNextOneWithItsId()
    {
        var flows = new FlowTracker(() => _now);
        flows.RegisterFlow(Flow(1), 200, "svchost", SvchostPath);
        flows.RegisterFlow(Flow(2), 200, "svchost", SvchostPath);
        flows.UnregisterFlow(Flow(2));
        flows.RegisterFlow(Flow(3), 300, "curl", null);
        uint? ownerWhenLetGo = 0;
        var svchost = new FakeProcess { OnLetGo = () => ownerWhenLetGo = flows.LookupProcessId(Flow(1)) };
        _running[200] = svchost;
        using var monitor = Held(flows: flows);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();

        _now += TimeSpan.FromSeconds(1);
        svchost.HasExited = true;
        monitor.SampleRates();

        // Gone before the ID is free: a late packet on either flow has no owner, and the flow's
        // own deleted event finds nothing to close.
        Assert.True(svchost.LetGo);
        Assert.Null(ownerWhenLetGo);
        Assert.Null(flows.LookupProcessId(Flow(2)));
        flows.UnregisterFlow(Flow(1));
        Assert.Null(flows.LookupConnection(Flow(1)));
        Assert.Equal(300u, flows.LookupProcessId(Flow(3)));
    }

    [Fact]
    public void AProcessWhoseIdAlreadyWentToANewerOne_LeavesThatOnesFlowsAlone()
    {
        var clock = _now;
        var flows = new FlowTracker(() => clock);
        flows.RegisterFlow(Flow(1), 200, "svchost", SvchostPath);
        clock += TimeSpan.FromSeconds(1);
        // Chrome got the ID and connected before the next sample.
        flows.RegisterFlow(Flow(2), 200, "chrome", ChromePath);
        _running[200] = new FakeProcess { HasExited = true, IdFreeSince = clock };
        using var monitor = Held(flows: flows);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();

        Assert.Empty(monitor.GetAllProcesses());
        Assert.Null(flows.LookupConnection(Flow(1)));
        Assert.Equal("chrome", flows.LookupConnection(Flow(2))?.ProcessName);
    }

    [Fact]
    public void AnExitedProcessesSocket_CantAdoptALatePacketBackToIt_AndItsIdIsNamedAfresh()
    {
        var flows = new FlowTracker(() => _now);
        var names = new Dictionary<uint, string> { [200] = "svchost" };
        var local = Flow(1).LocalAddress;
        var sockets = new List<OpenSocket> { new(TransportProtocol.Tcp, local, 1, 200) };
        var owners = new SocketOwners(flows, () => sockets.ToList(), processId => new ProcessIdentity(names[processId], null),
            _ => 1, utcNow: () => _now);
        owners.Refresh();
        Assert.True(owners.TryAdopt(Flow(1), isOutbound: true));
        var svchost = new FakeProcess();
        _running[200] = svchost;
        using var monitor = Held(flows: flows);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();

        _now += TimeSpan.FromSeconds(1);
        svchost.HasExited = true;
        monitor.SampleRates();

        // A late packet on its connection, before the socket tables are read again.
        Assert.False(owners.TryAdopt(Flow(1), isOutbound: true));
        Assert.Null(flows.LookupProcessId(Flow(1)));

        // Windows gives the ID to chrome, which opens a socket of its own.
        names[200] = "chrome";
        sockets[0] = new OpenSocket(TransportProtocol.Tcp, local, 2, 200);
        owners.Refresh();
        Assert.True(owners.TryAdopt(Flow(2), isOutbound: true));
        Assert.Equal("chrome", flows.LookupConnection(Flow(2))?.ProcessName);
    }

    [Fact]
    public void AProcessThatStartedWhileTheSocketTablesWereRead_IsKept()
    {
        var flows = new FlowTracker(() => _now);
        var local = Flow(1).LocalAddress;
        var readAt = _now;
        // It started just after the read did, and its socket made it into the tables.
        var started = readAt + TimeSpan.FromMilliseconds(5);
        _startedAt[200] = started;
        _running[200] = new FakeProcess();
        var sockets = new List<OpenSocket> { new(TransportProtocol.Tcp, local, 1, 200) };
        var owners = new SocketOwners(flows, () => sockets.ToList(), _ => new ProcessIdentity("chrome", null),
            _ => started.ToFileTimeUtc(), utcNow: () => readAt);
        owners.Refresh();
        using var monitor = Held(flows: flows);

        _now += TimeSpan.FromSeconds(1);
        Assert.True(owners.TryAdopt(Flow(1), isOutbound: true));
        var flow = flows.LookupConnection(Flow(1))!;
        monitor.RecordBytes(200, flow.ProcessName, 100, isUpload: false, flow.ProcessPath, flow.IdentifiedAt);
        monitor.SampleRates();

        Assert.Equal("chrome", Assert.Single(monitor.GetAllProcesses()).ProcessName);
        Assert.NotNull(flows.LookupConnection(Flow(1)));
    }

    [Fact]
    public void ALatePacketNamedBeforeTheIdWentToAnotherProcess_GoesWithTheOldOne()
    {
        var flows = new FlowTracker(() => _now);
        var local = Flow(1).LocalAddress;
        var names = new Dictionary<uint, string> { [200] = "svchost" };
        var started = new Dictionary<uint, long?> { [200] = 1 };
        var sockets = new List<OpenSocket> { new(TransportProtocol.Tcp, local, 1, 200) };
        var owners = new SocketOwners(flows, () => sockets.ToList(), processId => new ProcessIdentity(names[processId], null),
            processId => started[processId], utcNow: () => _now);
        owners.Refresh();
        using var monitor = Held(flows: flows);

        // svchost exits before the monitor ever sees it, and Windows gives its ID to chrome.
        _now += TimeSpan.FromSeconds(1);
        names[200] = "chrome";
        started[200] = 2;
        _startedAt[200] = _now;
        _running[200] = new FakeProcess();
        _now += TimeSpan.FromSeconds(1);
        // A late packet on svchost's connection, before the socket tables are read again. It's
        // the first the monitor hears of the ID, after chrome started.
        Assert.True(owners.TryAdopt(Flow(1), isOutbound: true));
        var late = flows.LookupConnection(Flow(1))!;
        monitor.RecordBytes(200, late.ProcessName, 100, isUpload: false, late.ProcessPath, late.IdentifiedAt);
        // Chrome opens a connection of its own, found by the next read.
        sockets[0] = new OpenSocket(TransportProtocol.Tcp, local, 2, 200);
        owners.Refresh();
        Assert.True(owners.TryAdopt(Flow(2), isOutbound: true));
        monitor.SampleRates();

        // svchost's entry and flow go. Chrome's flow and socket stay.
        Assert.Empty(monitor.GetAllProcesses());
        Assert.Null(flows.LookupConnection(Flow(1)));
        var chrome = flows.LookupConnection(Flow(2))!;
        Assert.Equal("chrome", chrome.ProcessName);
        Assert.True(owners.TryAdopt(new FlowKey(TransportProtocol.Tcp, local, 2, IPAddress.Parse("1.1.1.1"), 443), isOutbound: true));

        // Chrome's own bytes start an entry of its own, which is held.
        monitor.RecordBytes(200, chrome.ProcessName, 100, isUpload: false, chrome.ProcessPath, chrome.IdentifiedAt);
        monitor.SampleRates();
        Assert.Equal("chrome", Assert.Single(monitor.GetAllProcesses()).ProcessName);
    }

    [Fact]
    public void AFlowNamedBeforeAHeldProcessExited_IsTurnedAwayWhenItArrivesLate()
    {
        var flows = new FlowTracker(() => _now);
        var svchost = new FakeProcess();
        _running[200] = svchost;
        using var monitor = Held(flows: flows);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();
        var namedEarlier = _now;

        // It's held until it's let go, so the ID is its own until then.
        _now += TimeSpan.FromSeconds(1);
        svchost.HasExited = true;
        monitor.SampleRates();
        // A socket owner looked up before the exit, registering after it.
        flows.RegisterFlow(Flow(1), 200, "svchost", SvchostPath, namedEarlier);
        // The next process with the ID is named after it was let go.
        _now += TimeSpan.FromSeconds(1);
        flows.RegisterFlow(Flow(2), 200, "chrome", ChromePath);

        Assert.Null(flows.LookupConnection(Flow(1)));
        Assert.Equal("chrome", flows.LookupConnection(Flow(2))?.ProcessName);
    }

    [Fact]
    public void AProcessGoneBeforeItCouldBeHeld_LeavesNoCutoffBehind()
    {
        var flows = new FlowTracker(() => _now);
        using var monitor = Held(flows: flows);
        var namedEarlier = _now;
        monitor.RecordBytes(300, "curl", 700, isUpload: true);
        _now += TimeSpan.FromSeconds(1);
        monitor.SampleRates();
        Assert.Empty(monitor.GetAllProcesses());

        // Nothing is known about who has the ID since, so a flow named before may be the next one's.
        flows.RegisterFlow(Flow(1), 300, "chrome", ChromePath, namedEarlier);

        Assert.Equal("chrome", flows.LookupConnection(Flow(1))?.ProcessName);
    }

    [Fact]
    public void ForgettingAnExitedProcess_DoesntHoldUpTheNextSample()
    {
        var flows = new FlowTracker();
        var svchost = new FakeProcess();
        _running[200] = svchost;
        using var monitor = Held(flows: flows);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();
        svchost.HasExited = true;
        bool? sampledMeanwhile = null;
        // The socket owners can be in the middle of reading the socket tables when told to forget.
        flows.ForgettingProcess += (_, _) =>
        {
            var other = new Thread(monitor.SampleRates);
            other.Start();
            sampledMeanwhile = other.Join(TimeSpan.FromSeconds(5));
        };

        monitor.SampleRates();

        Assert.True(sampledMeanwhile);
        Assert.True(svchost.LetGo);
    }

    [Fact]
    public void SamplingAfterDispose_HoldsNothing()
    {
        int holds = 0;
        var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, null,
            (_, _) => { holds++; return new FakeProcess(); });
        monitor.RecordBytes(200, "curl", 100, isUpload: false);

        // A timer tick that was already on its way when the service stopped.
        monitor.Dispose();
        monitor.SampleRates();

        Assert.Equal(0, holds);
    }

    [Fact]
    public void AProcessThatExits_LeavesTheList_AndTheNextOneWithItsIdStartsItsOwn()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "DoSvc");
        var svchost = new FakeProcess();
        _running[200] = svchost;
        using var monitor = Held(hosts);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();
        Assert.False(svchost.LetGo);

        svchost.HasExited = true;
        hosts.Remove(200);
        monitor.SampleRates();
        Assert.True(svchost.LetGo);
        Assert.Empty(monitor.TakeSnapshot().Processes);
        Assert.Null(monitor.GetProcessInfo(200));

        // Windows gives the ID to chrome only once the svchost is let go.
        _running[200] = new FakeProcess();
        monitor.RecordBytes(200, "chrome", 50, isUpload: false, ChromePath);
        monitor.SampleRates();

        var chrome = Assert.Single(monitor.TakeSnapshot().Processes);
        Assert.Equal(("chrome", ChromePath, 50L), (chrome.ProcessName, chrome.ProcessPath, chrome.TotalBytesReceived));
        Assert.Null(chrome.Services);

        // A quota on DoSvc keeps the svchost's bytes and never gets chrome's.
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ServiceName = "DoSvc",
            Quota = new QuotaConfig { LimitBytes = 1_000_000, Period = QuotaPeriod.Daily }
        });
        var quotas = new QuotaTracker(rules, monitor);
        quotas.Update();
        Assert.Equal(100, quotas.GetQuotaState("DoSvc")!.UsedBytes);
    }

    [Fact]
    public void AProcessGoneBeforeItCouldBeHeld_LeavesTheList_ButKeepsItsBytes()
    {
        using var monitor = Held();
        monitor.RecordBytes(300, "curl", 700, isUpload: true);
        monitor.SampleRates();

        Assert.Empty(monitor.GetAllProcesses());
        var curl = Assert.Single(monitor.GetUsageTotals());
        Assert.Equal(700, curl.TotalBytesSent);
    }

    [Fact]
    public void AProcessThatCantBeHeld_StaysListed()
    {
        _running[4] = HeldProcess.Unheld;
        using var monitor = Held();
        monitor.RecordBytes(4, "System", 100, isUpload: false);
        monitor.SampleRates();
        monitor.SampleRates();

        Assert.Equal(100, Assert.Single(monitor.GetAllProcesses()).TotalBytesReceived);
    }

    [Fact]
    public void ExitedProcesses_GiveTheHistoryTheirLastBytes_ThenJoinTheirProgramsTotal()
    {
        var first = new FakeProcess();
        var second = new FakeProcess();
        _running[300] = first;
        _running[304] = second;
        using var monitor = Held();
        monitor.RecordBytes(300, "curl", 100, isUpload: false);
        monitor.RecordBytes(304, "curl", 200, isUpload: false);
        monitor.SampleRates();
        Assert.Equal(300, monitor.TakeUsage().TotalDownloadBytesPerSecond);

        // In flight when it exited.
        monitor.RecordBytes(300, "curl", 40, isUpload: false);
        first.HasExited = true;
        second.HasExited = true;
        monitor.SampleRates();
        Assert.Equal(40, monitor.TakeUsage().TotalDownloadBytesPerSecond);

        _now += TimeSpan.FromMinutes(10);
        monitor.SampleRates();

        var curl = Assert.Single(monitor.GetUsageTotals());
        Assert.Equal(("curl", 340L), (curl.ProcessName, curl.TotalBytesReceived));
        // Already in the history, so not again.
        Assert.Empty(monitor.TakeUsage().Processes);
    }

    [Fact]
    public void HeldProcess_HoldsARealProcessUntilItExits_AndRefusesOneNewerThanFirstSeen()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        uint processId = (uint)child.Id;

        // Seen before it started, so whatever was seen then was another process, and it's gone.
        using var replaced = HeldProcess.Hold(processId, child.StartTime.ToUniversalTime().AddSeconds(-5));
        Assert.NotNull(replaced);
        Assert.True(replaced.HasExited);
        // A tick past its start stamp, which can be up to 15.6 ms behind when it really started.
        Assert.InRange(replaced.IdFreeSince!.Value - child.StartTime.ToUniversalTime(),
            TimeSpan.FromMilliseconds(15.625), TimeSpan.FromMilliseconds(20));

        using var held = HeldProcess.Hold(processId, DateTime.UtcNow);
        Assert.NotNull(held);
        Assert.NotSame(HeldProcess.Unheld, held);
        Assert.False(held.HasExited);

        child.Kill();
        child.WaitForExit();
        Assert.True(held.HasExited);
    }

    [Fact]
    public void HeldProcess_ForAnIdNoProcessHas_SaysSinceWhenItWasFree()
    {
        var before = DateTime.UtcNow;
        using var gone = HeldProcess.Hold(uint.MaxValue - 3, before.AddMinutes(-1));
        var after = DateTime.UtcNow;

        Assert.NotNull(gone);
        Assert.True(gone.HasExited);
        Assert.InRange(gone.IdFreeSince!.Value, before, after);
    }

    [Fact]
    public void HeldProcess_StartedAt_ReadsWhenARealProcessStarted()
    {
        using var self = Process.GetCurrentProcess();

        Assert.Equal(self.StartTime.ToUniversalTime().ToFileTimeUtc(), HeldProcess.StartedAt((uint)self.Id));
        Assert.Equal(0, HeldProcess.StartedAt(4));
        Assert.Null(HeldProcess.StartedAt(uint.MaxValue - 3));
    }
}
