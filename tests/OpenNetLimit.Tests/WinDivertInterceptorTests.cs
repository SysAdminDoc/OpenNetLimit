using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using SharpDivert;
using Xunit;

namespace OpenNetLimit.Tests;

public class WinDivertInterceptorTests
{
    [Fact]
    public void FlowLayer_IsOpenedAsPassiveObserver()
    {
        // WinDivertOpen returns ERROR_INVALID_PARAMETER (87) for the Flow layer unless
        // both Sniff and RecvOnly are set, which kept the engine from ever starting.
        Assert.Equal(WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly, WinDivertInterceptor.FlowLayerFlags);
    }

    [Fact]
    public void NetworkLayer_DivertsPacketsSoTheyCanBeShaped()
    {
        Assert.False(WinDivertInterceptor.NetworkLayerFlags.HasFlag(WinDivert.Flag.Sniff));
        Assert.False(WinDivertInterceptor.NetworkLayerFlags.HasFlag(WinDivert.Flag.RecvOnly));
    }

    [Fact]
    public void NetworkLayer_QueuesAsManyPacketsAsTheDriverAllows()
    {
        // With the default 4096-packet, 4 MB queue the driver dropped packets whenever a burst
        // arrived faster than the packet loop drained it: about half of a 12,000-datagram
        // loopback burst was lost and fast downloads stalled. At the maximum none were lost.
        Assert.Equal(WinDivert.QueueLengthMax, WinDivertInterceptor.NetworkQueueLength);
        Assert.Equal(WinDivert.QueueSizeMax, WinDivertInterceptor.NetworkQueueBytes);
    }

    [Theory]
    [InlineData("::ffff:192.168.1.27", "192.168.1.27")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    [InlineData("::ffff:104.20.23.154", "104.20.23.154")]
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("fe80::1234:5678:9abc:def0", "fe80::1234:5678:9abc:def0")]
    public void FlowAddress_MatchesWinDivertsOwnParser(string native, string expected)
    {
        // IPv6Addr.Parse runs WinDivert's native parser, so it produces exactly the
        // host-byte-order layout the Flow layer reports.
        var addr = IPv6Addr.Parse(native);

        Assert.Equal(IPAddress.Parse(expected), FlowAddress.ToIPAddress(addr));
    }

    [Fact]
    public void FlowAddress_UnmapsIPv4SoItEqualsPacketHeaderAddress()
    {
        var ip = FlowAddress.ToIPAddress(IPv6Addr.Parse("::ffff:10.0.0.5"));

        Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
    }

    [Theory]
    [InlineData((byte)6, TransportProtocol.Tcp)]
    [InlineData((byte)17, TransportProtocol.Udp)]
    [InlineData((byte)1, TransportProtocol.Other)]
    public void FlowAddress_MapsProtocolNumbers(byte number, TransportProtocol expected)
    {
        Assert.Equal(expected, FlowAddress.ToProtocol(number));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ipv4Packets_AreAttributedToTheFlowOwner(bool outbound)
    {
        var tracker = new FlowTracker();
        var flow = new WinDivertDataFlow
        {
            ProcessId = 4242,
            Protocol = 6,
            LocalAddr = IPv6Addr.Parse("::ffff:192.168.1.27"),
            LocalPort = 50123,
            RemoteAddr = IPv6Addr.Parse("::ffff:104.20.23.154"),
            RemotePort = 443
        };
        tracker.RegisterFlow(FlowAddress.ToFlowKey(flow), flow.ProcessId, "browser", null);

        var local = IPAddress.Parse("192.168.1.27");
        var remote = IPAddress.Parse("104.20.23.154");
        var packet = outbound
            ? Ipv4Tcp(local, 50123, remote, 443, 100)
            : Ipv4Tcp(remote, 443, local, 50123, 100);
        var parsed = WinDivertInterceptor.ParsePacket(packet);

        Assert.NotNull(parsed);
        Assert.Equal(100, parsed.Value.payloadLength);
        Assert.Equal((uint)4242, tracker.LookupProcessId(parsed.Value.flowKey));
    }

    [Fact]
    public void Ipv6Packets_AreAttributedToTheFlowOwner()
    {
        var tracker = new FlowTracker();
        var flow = new WinDivertDataFlow
        {
            ProcessId = 777,
            Protocol = 6,
            LocalAddr = IPv6Addr.Parse("2001:db8::10"),
            LocalPort = 61000,
            RemoteAddr = IPv6Addr.Parse("2001:db8:1::20"),
            RemotePort = 80
        };
        tracker.RegisterFlow(FlowAddress.ToFlowKey(flow), flow.ProcessId, "curl", null);

        var packet = Ipv6Tcp(IPAddress.Parse("2001:db8:1::20"), 80, IPAddress.Parse("2001:db8::10"), 61000, 32);
        var parsed = WinDivertInterceptor.ParsePacket(packet);

        Assert.NotNull(parsed);
        Assert.Equal((uint)777, tracker.LookupProcessId(parsed.Value.flowKey));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(577)]
    [InlineData(1275)]
    public void OpenFailure_ExplainsKnownWindowsErrors(int code)
    {
        Assert.NotEqual(WinDivertOpenException.DescribeError(-1), WinDivertOpenException.DescribeError(code));
    }

    [Fact]
    public void OpenFailure_MessageNamesLayerFlagsAndCode()
    {
        var ex = new WinDivertOpenException(WinDivert.Layer.Flow, WinDivert.Flag.Sniff, 87, "The parameter is incorrect.");

        Assert.Contains("Flow", ex.Message);
        Assert.Contains("Sniff", ex.Message);
        Assert.Contains("87", ex.Message);
        Assert.Equal(87, ex.NativeErrorCode);
    }

    [Fact]
    public void OpenFailureWithAFoundCause_GivesThatCauseInsteadOfTheGeneralOne()
    {
        const string found = "The shared WinDivert driver service points at C:\\Old\\WinDivert64.sys, which no longer exists.";
        var inner = new InvalidOperationException("from the driver");
        var ex = new WinDivertOpenException(WinDivert.Layer.Flow, WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly, 2,
            "The system cannot find the file specified.", inner);

        var detailed = ex.WithDetail(found);

        Assert.Equal(found, detailed.Reason);
        Assert.Contains(found, detailed.Message);
        Assert.DoesNotContain(WinDivertOpenException.DescribeError(2), detailed.Message);
        Assert.Equal(2, detailed.NativeErrorCode);
        Assert.Equal(WinDivert.Layer.Flow, detailed.Layer);
        Assert.Equal(WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly, detailed.Flags);
        Assert.Equal("The system cannot find the file specified.", detailed.NativeMessage);
        Assert.Same(inner, detailed.InnerException);
    }

    [Fact]
    public void DriverFolder_IsWhereTheLoadedWinDivertDllIs()
    {
        const string native = @"C:\Program Files\OpenNetLimit\runtimes\win-x64\native";
        string[] modules =
        [
            @"C:\Program Files\OpenNetLimit\OpenNetLimit.Service.exe",
            @"C:\Windows\System32\ntdll.dll",
            native + @"\WINDIVERT.DLL"
        ];

        Assert.Equal(native, WinDivertInterceptor.DriverFolder(modules));
    }

    [Fact]
    public void DriverFolder_WithoutWinDivertLoaded_IsTheAppFolder()
    {
        Assert.Equal(AppContext.BaseDirectory, WinDivertInterceptor.DriverFolder([@"C:\Windows\System32\ntdll.dll"]));
    }

    [Fact]
    public void InboundDnsResponse_IsCached()
    {
        var cache = new DnsDomainCache();
        var answer = IPAddress.Parse("93.184.216.34");
        var packet = Ipv4Udp(IPAddress.Parse("1.1.1.1"), 53, IPAddress.Parse("192.168.1.27"), 51000,
            DnsDomainTests.BuildDnsResponse("example.com", answer));
        var parsed = WinDivertInterceptor.ParsePacket(packet)!.Value;

        Assert.True(WinDivertInterceptor.TryCacheDnsResponse(cache, parsed.flowKey, isOutbound: false, parsed.payloadData.Span));
        Assert.Equal("example.com", cache.LookupDomain(answer));
    }

    [Fact]
    public void OutboundDnsQuery_IsNotParsedAsAResponse()
    {
        var cache = new DnsDomainCache();
        var packet = Ipv4Udp(IPAddress.Parse("192.168.1.27"), 51000, IPAddress.Parse("1.1.1.1"), 53,
            DnsDomainTests.BuildDnsResponse("example.com", IPAddress.Parse("93.184.216.34")));
        var parsed = WinDivertInterceptor.ParsePacket(packet)!.Value;

        Assert.False(WinDivertInterceptor.TryCacheDnsResponse(cache, parsed.flowKey, isOutbound: true, parsed.payloadData.Span));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void InboundUdpFromOtherPorts_IsNotParsedAsDns()
    {
        var cache = new DnsDomainCache();
        var packet = Ipv4Udp(IPAddress.Parse("1.1.1.1"), 5353, IPAddress.Parse("192.168.1.27"), 53,
            DnsDomainTests.BuildDnsResponse("example.com", IPAddress.Parse("93.184.216.34")));
        var parsed = WinDivertInterceptor.ParsePacket(packet)!.Value;

        Assert.False(WinDivertInterceptor.TryCacheDnsResponse(cache, parsed.flowKey, isOutbound: false, parsed.payloadData.Span));
    }

    [Fact]
    public void PacketEndpoints_PutTheRemoteSideFirstForInboundPackets()
    {
        var local = IPAddress.Parse("192.168.1.27");
        var remote = IPAddress.Parse("104.20.23.154");
        var inbound = WinDivertInterceptor.ParsePacket(Ipv4Tcp(remote, 443, local, 50123, 10))!.Value.flowKey;
        var outbound = WinDivertInterceptor.ParsePacket(Ipv4Tcp(local, 50123, remote, 443, 10))!.Value.flowKey;

        var fromInbound = WinDivertInterceptor.PacketEndpoints.From(inbound, isOutbound: false);
        var fromOutbound = WinDivertInterceptor.PacketEndpoints.From(outbound, isOutbound: true);

        Assert.Equal(fromOutbound, fromInbound);
        Assert.Equal(remote, fromInbound.RemoteAddress);
        Assert.Equal(443, fromInbound.RemotePort);
        Assert.Equal(local, fromInbound.LocalAddress);
        Assert.Equal(50123, fromInbound.LocalPort);
    }

    internal static byte[] Ipv4Tcp(IPAddress src, ushort srcPort, IPAddress dst, ushort dstPort, int payload) =>
        Ipv4(src, dst, 6, Tcp(srcPort, dstPort, payload));

    internal static byte[] Ipv4Udp(IPAddress src, ushort srcPort, IPAddress dst, ushort dstPort, byte[] payload) =>
        Ipv4(src, dst, 17, Udp(srcPort, dstPort, payload));

    private static byte[] Ipv6Tcp(IPAddress src, ushort srcPort, IPAddress dst, ushort dstPort, int payload)
    {
        var transport = Tcp(srcPort, dstPort, payload);
        var packet = new byte[40 + transport.Length];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)transport.Length);
        packet[6] = 6;
        packet[7] = 64;
        src.GetAddressBytes().CopyTo(packet, 8);
        dst.GetAddressBytes().CopyTo(packet, 24);
        transport.CopyTo(packet, 40);
        return packet;
    }

    private static byte[] Ipv4(IPAddress src, IPAddress dst, byte protocol, byte[] transport)
    {
        var packet = new byte[20 + transport.Length];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = protocol;
        src.GetAddressBytes().CopyTo(packet, 12);
        dst.GetAddressBytes().CopyTo(packet, 16);
        transport.CopyTo(packet, 20);
        return packet;
    }

    private static byte[] Tcp(ushort srcPort, ushort dstPort, int payload)
    {
        var segment = new byte[20 + payload];
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(0), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), dstPort);
        segment[12] = 0x50;
        segment[13] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(14), 65535);
        return segment;
    }

    private static byte[] Udp(ushort srcPort, ushort dstPort, byte[] payload)
    {
        var datagram = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(0), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)datagram.Length);
        payload.CopyTo(datagram, 8);
        return datagram;
    }
}
