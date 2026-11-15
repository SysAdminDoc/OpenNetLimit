using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class SocketOwnersTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.9");

    private sealed class FakeTables
    {
        public List<OpenSocket> Sockets { get; } = [];
        public List<uint> Identified { get; } = [];
        /// <summary>When each process started. One that isn't listed started at 1.</summary>
        public Dictionary<uint, long?> Started { get; } = [];
        public int Reads { get; private set; }
        public long Now { get; set; }

        public SocketOwners Create(FlowTracker tracker) => new(tracker, () =>
        {
            Reads++;
            return Sockets.ToList();
        }, pid =>
        {
            Identified.Add(pid);
            return new ProcessIdentity($"app{pid}", $@"C:\Apps\app{pid}.exe");
        }, pid => Started.GetValueOrDefault(pid, 1), clock: () => Now);
    }

    private const uint SystemPid = SocketOwners.SystemProcessId;
    private static readonly FlowKey Https = new(TransportProtocol.Tcp, Local, 51000, Remote, 443);

    [Fact]
    public void SystemReport_IsCreditedToTheSocketsOwner()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();

        Assert.True(owners.RegisterEstablished(Https, SystemPid, eventTimestamp: -1, out var processId, out var identity));

        Assert.Equal(4242u, processId);
        Assert.Equal("app4242", identity.Name);
        var connection = tracker.LookupConnection(Https);
        Assert.NotNull(connection);
        Assert.Equal(4242u, connection.ProcessId);
        Assert.Equal("app4242", connection.ProcessName);
        Assert.Equal(@"C:\Apps\app4242.exe", connection.ProcessPath);
    }

    [Fact]
    public void SystemReport_ForASocketNewerThanTheTables_ReadsThemAgain()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables { Now = 10 };
        var owners = tables.Create(tracker);
        owners.Refresh();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        tables.Now = 30;

        Assert.True(owners.RegisterEstablished(Https, SystemPid, eventTimestamp: 20, out var processId, out _));

        Assert.Equal(4242u, processId);
        Assert.Equal(2, tables.Reads);
        Assert.Equal(4242u, tracker.LookupProcessId(Https));
    }

    [Fact]
    public void SystemReport_DoesNotReadTablesNewerThanTheEventAgain()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables { Now = 30 };
        var owners = tables.Create(tracker);
        owners.Refresh();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));

        Assert.True(owners.RegisterEstablished(Https, SystemPid, eventTimestamp: 20, out var processId, out _));

        Assert.Equal(SystemPid, processId);
        Assert.Equal(1, tables.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SystemReport_NeverReplacesTheRealOwner(bool realReportFirst)
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        var owners = tables.Create(tracker);
        owners.Refresh();

        if (realReportFirst)
        {
            Assert.True(owners.RegisterEstablished(Https, 4242, eventTimestamp: -1, out _, out _));
            Assert.False(owners.RegisterEstablished(Https, SystemPid, eventTimestamp: -1, out _, out _));
        }
        else
        {
            Assert.True(owners.RegisterEstablished(Https, SystemPid, eventTimestamp: -1, out _, out _));
            Assert.True(owners.RegisterEstablished(Https, 4242, eventTimestamp: -1, out _, out _));
        }

        Assert.Equal(4242u, tracker.LookupProcessId(Https));
        Assert.Equal("app4242", tracker.LookupConnection(Https)!.ProcessName);
    }

    [Fact]
    public void SystemReport_OnTheOtherEndOfALoopbackFlow_IsNotMistakenForTheClient()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, IPAddress.Loopback, 47719, SystemPid));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var client = new FlowKey(TransportProtocol.Tcp, IPAddress.Loopback, 52000, IPAddress.Loopback, 47719);
        var server = new FlowKey(TransportProtocol.Tcp, IPAddress.Loopback, 47719, IPAddress.Loopback, 52000);

        Assert.True(owners.RegisterEstablished(client, 900, eventTimestamp: -1, out _, out _));
        Assert.True(owners.RegisterEstablished(server, SystemPid, eventTimestamp: -1, out var processId, out _));

        Assert.Equal(SystemPid, processId);
        Assert.Equal(900u, tracker.LookupProcessId(client));
        Assert.Equal(SystemPid, tracker.LookupProcessId(server));
    }

    [Fact]
    public void RealProcessReport_IsTakenAsReported()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 99));
        var owners = tables.Create(tracker);
        owners.Refresh();

        Assert.True(owners.RegisterEstablished(Https, 4242, eventTimestamp: 50, out var processId, out var identity));

        Assert.Equal(4242u, processId);
        Assert.Equal("app4242", identity.Name);
        Assert.Equal(1, tables.Reads);
    }

    [Fact]
    public void SystemReportForALimitedApp_GetsTheAppsLimit()
    {
        var tracker = new FlowTracker();
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var reconciler = new RuleReconciler(rules, limiter, tracker);
        rules.OnRulesChanged += reconciler.Reconcile;
        rules.AddRule(new BandwidthRule { ProcessName = "app4242", DownloadBytesPerSecond = 262_144 });
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();

        owners.RegisterEstablished(Https, SystemPid, eventTimestamp: -1, out _, out _);

        Assert.True(limiter.HasLimit(4242));
        Assert.False(limiter.HasLimit(SystemPid));
    }

    [Fact]
    public void ConnectionOpenBeforeStart_IsAttributedToItsApp()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var inbound = new FlowKey(TransportProtocol.Tcp, Remote, 443, Local, 51000);

        Assert.True(owners.TryAdopt(inbound, isOutbound: false));

        Assert.Equal(4242u, tracker.LookupProcessId(inbound));
        var connection = tracker.LookupConnection(inbound);
        Assert.NotNull(connection);
        Assert.Equal("app4242", connection.ProcessName);
        Assert.Equal(@"C:\Apps\app4242.exe", connection.ProcessPath);
        Assert.Equal(new FlowKey(TransportProtocol.Tcp, Local, 51000, Remote, 443), connection.FlowKey);
    }

    [Fact]
    public void UdpSocketBoundToEveryAddress_IsMatched()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, IPAddress.Any, 50500, 77));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var outbound = new FlowKey(TransportProtocol.Udp, Local, 50500, Remote, 443);

        Assert.True(owners.TryAdopt(outbound, isOutbound: true));
        Assert.Equal(77u, tracker.LookupProcessId(outbound));
    }

    [Fact]
    public void DualStackSockets_MatchIPv4Traffic()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, IPAddress.IPv6Any, 6000, 10));
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local.MapToIPv6(), 52000, 11));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var quic = new FlowKey(TransportProtocol.Udp, Local, 6000, Remote, 443);
        var tcp = new FlowKey(TransportProtocol.Tcp, Local, 52000, Remote, 443);

        Assert.True(owners.TryAdopt(quic, isOutbound: true));
        Assert.True(owners.TryAdopt(tcp, isOutbound: true));
        Assert.Equal(10u, tracker.LookupProcessId(quic));
        Assert.Equal(11u, tracker.LookupProcessId(tcp));
    }

    [Fact]
    public void ExactAddress_WinsOverTheWildcard()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, IPAddress.Any, 5353, 1));
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, Local, 5353, 2));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var packet = new FlowKey(TransportProtocol.Udp, Local, 5353, Remote, 5353);

        Assert.True(owners.TryAdopt(packet, isOutbound: true));
        Assert.Equal(2u, tracker.LookupProcessId(packet));
    }

    [Fact]
    public void PacketWithNoOpenSocket_IsLeftAlone()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();

        Assert.False(owners.TryAdopt(new FlowKey(TransportProtocol.Udp, Local, 51000, Remote, 443), isOutbound: true));
        Assert.False(owners.TryAdopt(new FlowKey(TransportProtocol.Tcp, Local, 51001, Remote, 443), isOutbound: true));
        Assert.Empty(tracker.GetActiveConnections());
    }

    [Fact]
    public void ClosedSocket_EndsItsAdoptedFlow()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();
        owners.TryAdopt(new FlowKey(TransportProtocol.Tcp, Local, 51000, Remote, 443), isOutbound: true);

        tables.Sockets.Clear();
        owners.Refresh();

        Assert.Empty(tracker.GetActiveConnections());
        Assert.Equal(0, owners.AdoptedFlowCount);
    }

    [Fact]
    public void PortTakenByAnotherApp_EndsTheOldFlowAndAttributesTheNewOne()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, IPAddress.Any, 50500, 77));
        var owners = tables.Create(tracker);
        owners.Refresh();
        var first = new FlowKey(TransportProtocol.Udp, Local, 50500, Remote, 443);
        owners.TryAdopt(first, isOutbound: true);

        tables.Sockets[0] = new OpenSocket(TransportProtocol.Udp, IPAddress.Any, 50500, 88);
        owners.Refresh();

        Assert.Empty(tracker.GetActiveConnections());
        var next = new FlowKey(TransportProtocol.Udp, Local, 50500, IPAddress.Parse("198.51.100.4"), 443);
        Assert.True(owners.TryAdopt(next, isOutbound: true));
        Assert.Equal(88u, tracker.LookupProcessId(next));
    }

    [Fact]
    public void StillOpenSocket_KeepsItsFlowAcrossRefreshes()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242));
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Tcp, Local, 51001, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();
        owners.TryAdopt(new FlowKey(TransportProtocol.Tcp, Local, 51000, Remote, 443), isOutbound: true);

        owners.Refresh();
        owners.Refresh();

        Assert.Single(tracker.GetActiveConnections());
        Assert.Equal([4242u], tables.Identified);
    }

    [Fact]
    public void AnIdThatWentToAnotherProcess_IsNamedAgain()
    {
        var tracker = new FlowTracker();
        var tables = new FakeTables();
        tables.Sockets.Add(new OpenSocket(TransportProtocol.Udp, Local, 5353, 4242));
        var owners = tables.Create(tracker);
        owners.Refresh();

        // The process exited with only a listening socket, so nothing told the owners to forget
        // it, and Windows gave its ID to another program, which opened a socket of its own.
        tables.Started[4242] = 2;
        tables.Sockets[0] = new OpenSocket(TransportProtocol.Tcp, Local, 51000, 4242);
        owners.Refresh();
        // One that can't be opened can't be told apart, so it's named every time.
        tables.Started[4242] = null;
        owners.Refresh();
        owners.Refresh();

        Assert.Equal([4242u, 4242u, 4242u, 4242u], tables.Identified);
    }

    [Fact]
    public void AProcessThatStartedWhileTheTablesWereRead_IsNamedNoEarlierThanItStarted()
    {
        var readAt = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var started = readAt + TimeSpan.FromMilliseconds(5);
        var tracker = new FlowTracker(() => readAt + TimeSpan.FromSeconds(1));
        var sockets = new List<OpenSocket>
        {
            new(TransportProtocol.Tcp, Local, 51000, 4242),
            new(TransportProtocol.Tcp, Local, 51001, 4243)
        };
        var startedAt = new Dictionary<uint, long?> { [4242] = started.ToFileTimeUtc(), [4243] = null };
        var owners = new SocketOwners(tracker, () => sockets.ToList(),
            pid => new ProcessIdentity($"app{pid}", null), pid => startedAt[pid], utcNow: () => readAt);
        owners.Refresh();

        Assert.True(owners.TryAdopt(Https, isOutbound: true));
        Assert.True(owners.TryAdopt(Https with { LocalPort = 51001 }, isOutbound: true));

        Assert.Equal(started + HeldProcess.StartStampLag, tracker.LookupConnection(Https)!.IdentifiedAt);
        // One whose start can't be read goes by the read.
        Assert.Equal(readAt, tracker.LookupConnection(Https with { LocalPort = 51001 })!.IdentifiedAt);
    }

    [Fact]
    public void AProcessStartedBeforeTheClockWasSetBack_IsNamedAsOfTheClock()
    {
        var started = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        // The clock went back an hour after the process started.
        var now = started - TimeSpan.FromHours(1);
        var tracker = new FlowTracker(() => now);
        var sockets = new List<OpenSocket> { new(TransportProtocol.Tcp, Local, 51000, 4242) };
        var owners = new SocketOwners(tracker, () => sockets.ToList(), _ => new ProcessIdentity("app", null),
            _ => started.ToFileTimeUtc(), utcNow: () => now);
        owners.Refresh();

        Assert.True(owners.TryAdopt(Https, isOutbound: true));
        Assert.Equal(now, tracker.LookupConnection(Https)!.IdentifiedAt);

        // So when it exits, the forget reaches its flow and its socket.
        now += TimeSpan.FromSeconds(1);
        tracker.ForgetProcess(4242, now);
        Assert.Null(tracker.LookupConnection(Https));
        Assert.False(owners.TryAdopt(Https, isOutbound: true));
    }

    [Fact]
    public void OsTables_ListThisProcessesSockets()
    {
        var pid = (uint)Environment.ProcessId;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using var accepted = listener.AcceptTcpClient();
        using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udp6 = Socket.OSSupportsIPv6 ? new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)) : null;

        var sockets = OsSocketTable.Read();

        // A dual-mode client socket reports its IPv4 end as ::ffff:a.b.c.d, which is how
        // SocketOwners compares too.
        static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        void AssertListed(TransportProtocol protocol, IPEndPoint endpoint) =>
            Assert.Contains(sockets, socket => socket.Protocol == protocol &&
                                               Plain(socket.LocalAddress).Equals(Plain(endpoint.Address)) &&
                                               socket.LocalPort == endpoint.Port &&
                                               socket.ProcessId == pid);

        AssertListed(TransportProtocol.Tcp, (IPEndPoint)listener.LocalEndpoint);
        AssertListed(TransportProtocol.Tcp, (IPEndPoint)client.Client.LocalEndPoint!);
        AssertListed(TransportProtocol.Udp, (IPEndPoint)udp4.Client.LocalEndPoint!);
        if (udp6 is not null)
            AssertListed(TransportProtocol.Udp, (IPEndPoint)udp6.Client.LocalEndPoint!);
        Assert.DoesNotContain(sockets, socket => socket.ProcessId == 0);
    }
}
