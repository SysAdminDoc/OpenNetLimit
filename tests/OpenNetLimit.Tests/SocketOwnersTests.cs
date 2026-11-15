using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
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

        public SocketOwners Create(FlowTracker tracker) => new(tracker, () => Sockets.ToList(), pid =>
        {
            Identified.Add(pid);
            return new ProcessIdentity($"app{pid}", $@"C:\Apps\app{pid}.exe");
        });
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
