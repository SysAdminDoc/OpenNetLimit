using System.ComponentModel;
using System.Diagnostics;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Engine.Monitoring;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>Services by process, set by the test.</summary>
internal sealed class FakeServiceHosts : IServiceHosts
{
    private readonly Dictionary<uint, string[]> _hosts = new();

    public event Action? Changed;

    public void Set(uint processId, params string[] services)
    {
        lock (_hosts)
            _hosts[processId] = services;
    }

    public IReadOnlyList<string>? ServicesIn(uint processId)
    {
        lock (_hosts)
            return _hosts.TryGetValue(processId, out var services) ? services : null;
    }

    /// <summary>The process exited.</summary>
    public void Remove(uint processId)
    {
        lock (_hosts)
            _hosts.Remove(processId);
    }

    public IReadOnlyList<string>? Resolve(uint processId, string processName) => ServicesIn(processId);

    public void Refresh() => Changed?.Invoke();
}

public class ServiceHostsTests
{
    private DateTime _now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<uint, string[]> _running = new();
    private int _reads;
    private Exception? _readFails;
    private readonly List<Action> _queued = [];

    /// <summary>Watches the test can end, by process id. Null leaves the process out, as if its id were reused.</summary>
    private readonly Dictionary<uint, FakeWatch> _watches = new();
    private readonly HashSet<uint> _newerThanTheRead = [];
    private readonly HashSet<uint> _unwatchable = [];
    private readonly HashSet<uint> _watchThrows = [];

    private sealed class FakeWatch(Action exited) : IDisposable
    {
        public Action Exit { get; } = exited;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private ServiceHosts Create() => new(() =>
    {
        _reads++;
        if (_readFails is { } failure)
            throw failure;
        return new Dictionary<uint, string[]>(_running);
    }, () => _now, Watch, _queued.Add);

    private IDisposable? Watch(uint processId, DateTime readStartedUtc, Action exited)
    {
        Assert.Equal(_now, readStartedUtc);
        if (_newerThanTheRead.Contains(processId))
            return null;
        if (_unwatchable.Contains(processId))
            return ServiceHosts.Unwatched;
        if (_watchThrows.Contains(processId))
            throw new InvalidOperationException("watch failed");
        return _watches[processId] = new FakeWatch(exited);
    }

    private void RunQueued()
    {
        var queued = _queued.ToList();
        _queued.Clear();
        foreach (var work in queued)
            work();
    }

    [Fact]
    public void Resolve_ASvchostTheLastReadDidNotHave_ReadsTheListAgainOffThreadAtMostOnceASecond()
    {
        var hosts = Create();
        int changes = 0;
        hosts.Changed += () => changes++;
        _running[200] = ["BITS"];

        // Never read on the caller's thread: the packet loop can get here through a new flow.
        Assert.Null(hosts.Resolve(200, "svchost"));
        Assert.Equal(0, _reads);
        Assert.Null(hosts.Resolve(200, "svchost"));
        Assert.Single(_queued);
        RunQueued();
        Assert.Equal(1, _reads);
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "BITS" }, hosts.Resolve(200, "svchost"));

        // A svchost started since: the read a moment ago stands until a second has passed.
        _running[201] = ["DoSvc"];
        Assert.Null(hosts.Resolve(201, "svchost"));
        Assert.Empty(_queued);

        _now += ServiceHosts.MinReadGap;
        Assert.Null(hosts.Resolve(201, "SVCHOST"));
        RunQueued();
        Assert.Equal(new[] { "DoSvc" }, hosts.ServicesIn(201));
        Assert.Equal(2, _reads);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Resolve_AKnownSvchostOrAnotherApp_NeverReadsTheList()
    {
        _running[200] = ["BITS"];
        var hosts = Create();
        hosts.Refresh();
        _now += TimeSpan.FromMinutes(1);

        Assert.Equal(new[] { "BITS" }, hosts.Resolve(200, "svchost"));
        Assert.Null(hosts.Resolve(300, "chrome"));
        Assert.Empty(_queued);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void AProcessThatExits_LeavesTheListBeforeItsIdIsFree()
    {
        // A svchost that stopped for being idle kept its services until the next read, 30 s
        // later, so chrome or a DHCP svchost taking its id got its rule and its limit.
        _running[200] = ["DoSvc"];
        _running[201] = ["BITS"];
        var hosts = Create();
        hosts.Refresh();
        var watch = _watches[200];
        bool heldDuringChange = false;
        hosts.Changed += () => heldDuringChange = !watch.Disposed && hosts.ServicesIn(200) is null;

        watch.Exit();

        Assert.Null(hosts.ServicesIn(200));
        Assert.Equal(new[] { "BITS" }, hosts.ServicesIn(201));
        Assert.True(heldDuringChange);
        Assert.True(watch.Disposed);
        Assert.False(_watches[201].Disposed);
    }

    [Fact]
    public void AProcessStartedAfterTheReadBegan_IsLeftOut()
    {
        // Its id was taken between the read and the watch, so the services are another process's.
        _running[200] = ["DoSvc"];
        _running[201] = ["BITS"];
        _newerThanTheRead.Add(200);
        var hosts = Create();

        hosts.Refresh();

        Assert.Null(hosts.ServicesIn(200));
        Assert.Equal(new[] { "BITS" }, hosts.ServicesIn(201));
    }

    [Fact]
    public void AProcessThatCantBeWatched_KeepsWhatTheReadFound()
    {
        _running[200] = ["DoSvc"];
        _unwatchable.Add(200);
        var hosts = Create();

        hosts.Refresh();

        Assert.Equal(new[] { "DoSvc" }, hosts.ServicesIn(200));
    }

    [Fact]
    public void AProcessStillListed_IsWatchedOnce_AndOneThatLeftTheListIsLetGo()
    {
        _running[200] = ["DoSvc"];
        var hosts = Create();
        hosts.Refresh();
        var first = _watches[200];

        _running[200] = ["DoSvc", "BITS"];
        hosts.Refresh();
        Assert.Same(first, _watches[200]);
        Assert.False(first.Disposed);
        Assert.Equal(new[] { "BITS", "DoSvc" }, hosts.ServicesIn(200));

        // It may have exited without the exit being heard yet, so the rules hear first.
        bool heldDuringChange = false;
        hosts.Changed += () => heldDuringChange = !first.Disposed;
        _running.Remove(200);
        hosts.Refresh();
        Assert.True(heldDuringChange);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void AnExitHeardAfterAReadDroppedIt_ChangesNothing()
    {
        _running[200] = ["DoSvc"];
        var hosts = Create();
        hosts.Refresh();
        var old = _watches[200];
        _running.Remove(200);
        hosts.Refresh();
        // A new svchost took the id, and the old one's exit is only heard now.
        _running[200] = ["Dhcp"];
        hosts.Refresh();
        int changes = 0;
        hosts.Changed += () => changes++;

        old.Exit();

        Assert.Equal(new[] { "Dhcp" }, hosts.ServicesIn(200));
        Assert.Equal(0, changes);
        Assert.False(_watches[200].Disposed);
    }

    [Fact]
    public void Dispose_LetsGoOfEveryProcess()
    {
        _running[200] = ["DoSvc"];
        _running[201] = ["BITS"];
        var hosts = Create();
        hosts.Refresh();

        hosts.Dispose();
        hosts.Refresh();

        Assert.All(_watches.Values, watch => Assert.True(watch.Disposed));
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void AWatchThatThrows_KeepsTheProcessesTheReadHadAlreadyTakenOver()
    {
        _running[200] = ["DoSvc"];
        _running[201] = ["BITS"];
        var hosts = Create();
        hosts.Refresh();
        var doSvc = _watches[200];
        var bits = _watches[201];

        // The read takes over 200 and 201 from the last one, then fails on 202.
        _running[202] = ["wuauserv"];
        _watchThrows.Add(202);
        Assert.Throws<InvalidOperationException>(hosts.Refresh);

        Assert.Equal(new[] { "DoSvc" }, hosts.ServicesIn(200));
        doSvc.Exit();
        Assert.Null(hosts.ServicesIn(200));
        Assert.True(doSvc.Disposed);

        hosts.Dispose();
        Assert.True(bits.Disposed);
    }

    [Theory]
    [InlineData(-60, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(7200, false)]
    public void StartedSince_IsTrueOnlyForAProcessCreatedBetweenThenAndNow(int createdSecondsAfter, bool startedSince)
    {
        var seen = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var now = seen.AddHours(1);

        // 7200: created "after now", so the clock was set back since. That's no sign the id was reused.
        Assert.Equal(startedSince, HeldProcess.StartedSince(seen.AddSeconds(createdSecondsAfter), seen, now));
    }

    [Fact]
    public void WatchProcess_HearsARealProcessExit_AndRefusesOneNewerThanTheRead()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        uint processId = (uint)child.Id;

        Assert.Null(ServiceHosts.WatchProcess(processId, child.StartTime.ToUniversalTime().AddSeconds(-5), () => { }));

        using var exited = new ManualResetEventSlim();
        using var watch = ServiceHosts.WatchProcess(processId, DateTime.UtcNow, exited.Set);
        Assert.NotNull(watch);
        Assert.NotSame(ServiceHosts.Unwatched, watch);
        Assert.False(exited.Wait(200));

        child.Kill();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WatchProcess_AProcessAlreadyGone_IsLeftOut()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        uint processId = (uint)child.Id;
        child.WaitForExit();
        child.Dispose();

        // Once the last handle closes, the id names no process (or a newer one).
        Assert.Null(ServiceHosts.WatchProcess(processId, DateTime.UtcNow.AddSeconds(-30), () => { }));
    }

    [Fact]
    public void Refresh_SaysSoOnlyWhenAProcessServicesChanged()
    {
        var hosts = Create();
        int changes = 0;
        hosts.Changed += () => changes++;

        _running[200] = ["wuauserv", "BITS"];
        hosts.Refresh();
        Assert.Equal(1, changes);

        // Windows lists them in its own order, which isn't a change.
        _running[200] = ["bits", "WUAUSERV"];
        hosts.Refresh();
        Assert.Equal(1, changes);

        _running[200] = ["BITS"];
        hosts.Refresh();
        Assert.Equal(2, changes);

        // The service restarted in another svchost.
        _running.Remove(200);
        _running[201] = ["BITS"];
        hosts.Refresh();
        Assert.Equal(3, changes);
    }

    [Fact]
    public void AFailedRead_KeepsTheLastList()
    {
        _running[200] = ["BITS"];
        var hosts = Create();
        int changes = 0;
        hosts.Changed += () => changes++;
        hosts.Refresh();

        _readFails = new Win32Exception(5);
        hosts.Refresh();
        _now += ServiceHosts.MinReadGap;

        Assert.Equal(new[] { "BITS" }, hosts.ServicesIn(200));
        Assert.Null(hosts.Resolve(201, "svchost"));
        RunQueued();
        Assert.Equal(new[] { "BITS" }, hosts.ServicesIn(200));
        Assert.Equal(1, changes);
        Assert.Equal(3, _reads);
    }

    [Fact]
    public void ServicesIn_ListsThemByName()
    {
        _running[200] = ["wuauserv", "BITS", "Appinfo"];
        var hosts = Create();
        hosts.Refresh();

        Assert.Equal(new[] { "Appinfo", "BITS", "wuauserv" }, hosts.ServicesIn(200));
    }

    [Fact]
    public void TheServiceManager_ListsTheServicesInTheSvchostsRunningNow()
    {
        using var hosts = new ServiceHosts();
        hosts.Refresh();

        var services = Process.GetProcessesByName("svchost").SelectMany(process =>
        {
            using (process)
                return hosts.ServicesIn((uint)process.Id) ?? [];
        }).ToList();

        // RPC runs on every Windows, and a PC runs dozens of services in svchosts.
        Assert.Contains("RpcSs", services, StringComparer.OrdinalIgnoreCase);
        Assert.True(services.Count > 20, $"{services.Count} services");
    }

    [Theory]
    [InlineData("lsass", null, ProcessProtection.Windows)]
    [InlineData("services", null, ProcessProtection.Windows)]
    [InlineData("System", null, ProcessProtection.Windows)]
    [InlineData("csrss", null, ProcessProtection.Windows)]
    [InlineData("wininit", null, ProcessProtection.Windows)]
    [InlineData("smss", null, ProcessProtection.Windows)]
    [InlineData("ntoskrnl", null, ProcessProtection.Windows)]
    // Networking only from System32, so with no path these stay as protected as they always were.
    [InlineData("dns", null, ProcessProtection.Windows)]
    [InlineData("dnscache", null, ProcessProtection.Windows)]
    [InlineData("chrome", null, ProcessProtection.None)]
    [InlineData("spoolsv", "Spooler", ProcessProtection.None)]
    // A svchost whose services aren't known yet is kept safe until they are.
    [InlineData("svchost", null, ProcessProtection.Windows)]
    [InlineData("svchost", "", ProcessProtection.Windows)]
    [InlineData("svchost", "BITS", ProcessProtection.None)]
    [InlineData("svchost", "BITS,wuauserv", ProcessProtection.None)]
    [InlineData("svchost", "Dnscache", ProcessProtection.Networking)]
    [InlineData("SVCHOST", "dnscache,NlaSvc", ProcessProtection.Networking)]
    [InlineData("svchost", "SharedAccess", ProcessProtection.Networking)]
    [InlineData("svchost", "icssvc", ProcessProtection.Networking)]
    [InlineData("svchost", "hns", ProcessProtection.Networking)]
    [InlineData("svchost", "BITS,Dnscache", ProcessProtection.Windows)]
    [InlineData("svchost", "DcomLaunch,BrokerInfrastructure,Power", ProcessProtection.Windows)]
    public void Of_KeepsWindowsAndItsNetworkingOutOfRules(string name, string? services, ProcessProtection expected)
    {
        var list = services?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, ProtectedProcesses.Of(name, list));
    }

    [Theory]
    // The Windows DNS Server only ever sends DNS, its replies from local port 53 included.
    [InlineData("dns", true, ProcessProtection.Networking)]
    [InlineData("DNS", true, ProcessProtection.Networking)]
    [InlineData("dhcp", true, ProcessProtection.Networking)]
    [InlineData("dnscache", true, ProcessProtection.Networking)]
    // Anyone can copy a program to dns.exe somewhere else. It keeps Windows' protection, no more.
    [InlineData("dns", false, ProcessProtection.Windows)]
    [InlineData("dnscache", false, ProcessProtection.Windows)]
    // Only the networking names go by where they run.
    [InlineData("lsass", true, ProcessProtection.Windows)]
    [InlineData("chrome", true, ProcessProtection.None)]
    public void Of_TrustsNetworkingNamesOnlyFromSystem32(string name, bool inSystem32, ProcessProtection expected)
    {
        var folder = inSystem32 ? Environment.SystemDirectory : Path.Combine(Path.GetTempPath(), "x");
        var path = Path.Combine(folder, name + ".exe");

        Assert.Equal(expected, ProtectedProcesses.Of(name, null, path));
    }

    [Fact]
    public void NetworkingIn_NamesTheServicesThatKeepASvchostOutOfRules()
    {
        Assert.Equal(new[] { "Dnscache" }, ProtectedProcesses.NetworkingIn(["BITS", "Dnscache"]));
        Assert.Empty(ProtectedProcesses.NetworkingIn(["BITS"]));
        Assert.Empty(ProtectedProcesses.NetworkingIn(null));
    }
}
