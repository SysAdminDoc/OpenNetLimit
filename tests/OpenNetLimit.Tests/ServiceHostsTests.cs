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

    public IReadOnlyList<string>? Resolve(uint processId, string processName) => ServicesIn(processId);

    public void Refresh() => Changed?.Invoke();
}

public class ServiceHostsTests
{
    private DateTime _now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<uint, string[]> _running = new();
    private int _reads;
    private Exception? _readFails;

    private ServiceHosts Create() => new(() =>
    {
        _reads++;
        if (_readFails is { } failure)
            throw failure;
        return new Dictionary<uint, string[]>(_running);
    }, () => _now);

    [Fact]
    public void Resolve_ASvchostTheLastReadDidNotHave_ReadsTheListAgainAtMostOnceASecond()
    {
        var hosts = Create();
        _running[200] = ["BITS"];

        Assert.Equal(new[] { "BITS" }, hosts.Resolve(200, "svchost"));
        Assert.Equal(1, _reads);

        // A svchost started since: the read a moment ago stands until a second has passed.
        _running[201] = ["DoSvc"];
        Assert.Null(hosts.Resolve(201, "svchost"));
        Assert.Equal(1, _reads);

        _now += ServiceHosts.MinReadGap;
        Assert.Equal(new[] { "DoSvc" }, hosts.Resolve(201, "SVCHOST"));
        Assert.Equal(2, _reads);
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
        Assert.Equal(1, _reads);
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
    public void Refresh_ReportsWhatAReadForANewSvchostFound()
    {
        // The reconciler reads inside its own pass, so hearing about it there would start
        // another pass inside the first. It hears at the next refresh instead.
        var hosts = Create();
        int changes = 0;
        hosts.Changed += () => changes++;
        _running[200] = ["BITS"];

        hosts.Resolve(200, "svchost");
        Assert.Equal(0, changes);

        hosts.Refresh();
        Assert.Equal(1, changes);
        hosts.Refresh();
        Assert.Equal(1, changes);
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
        var hosts = new ServiceHosts();
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
    [InlineData("chrome", null, ProcessProtection.None)]
    [InlineData("spoolsv", "Spooler", ProcessProtection.None)]
    // A svchost whose services aren't known yet is kept safe until they are.
    [InlineData("svchost", null, ProcessProtection.Windows)]
    [InlineData("svchost", "", ProcessProtection.Windows)]
    [InlineData("svchost", "BITS", ProcessProtection.None)]
    [InlineData("svchost", "BITS,wuauserv", ProcessProtection.None)]
    [InlineData("svchost", "Dnscache", ProcessProtection.Networking)]
    [InlineData("SVCHOST", "dnscache,NlaSvc", ProcessProtection.Networking)]
    [InlineData("svchost", "BITS,Dnscache", ProcessProtection.Windows)]
    [InlineData("svchost", "DcomLaunch,BrokerInfrastructure,Power", ProcessProtection.Windows)]
    public void Of_KeepsWindowsAndItsNetworkingOutOfRules(string name, string? services, ProcessProtection expected)
    {
        var list = services?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, ProtectedProcesses.Of(name, list));
    }

    [Fact]
    public void NetworkingIn_NamesTheServicesThatKeepASvchostOutOfRules()
    {
        Assert.Equal(new[] { "Dnscache" }, ProtectedProcesses.NetworkingIn(["BITS", "Dnscache"]));
        Assert.Empty(ProtectedProcesses.NetworkingIn(["BITS"]));
        Assert.Empty(ProtectedProcesses.NetworkingIn(null));
    }
}
