using System.Net;
using OpenNetLimit.Core.Models;
using SharpDivert;

namespace OpenNetLimit.Engine.Interception;

/// <summary>
/// Turns WinDivert flow-layer endpoints into the same <see cref="FlowKey"/> shape the
/// network layer builds from packet headers, so packets can be matched to the process
/// that owns the flow.
/// </summary>
internal static class FlowAddress
{
    /// <summary>
    /// <see cref="IPv6Addr"/> holds the address in host byte order (four little-endian
    /// uints), so its 16 bytes are the network-order bytes reversed. IPv4 flows arrive as
    /// IPv4-mapped IPv6 (::ffff:a.b.c.d) and are unmapped so they equal the addresses
    /// read from IPv4 packet headers.
    /// </summary>
    public static unsafe IPAddress ToIPAddress(IPv6Addr addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        byte* ptr = (byte*)&addr;
        for (int i = 0; i < 16; i++)
            bytes[i] = ptr[15 - i];
        var ip = new IPAddress(bytes);
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    public static TransportProtocol ToProtocol(byte protocol) => protocol switch
    {
        6 => TransportProtocol.Tcp,
        17 => TransportProtocol.Udp,
        _ => TransportProtocol.Other
    };

    public static FlowKey ToFlowKey(in WinDivertDataFlow flow) => new(
        ToProtocol(flow.Protocol),
        ToIPAddress(flow.LocalAddr),
        flow.LocalPort,
        ToIPAddress(flow.RemoteAddr),
        flow.RemotePort);
}
