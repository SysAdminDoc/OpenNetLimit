using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Interception;

/// <summary>A local socket from the OS connection tables and the process that owns it.</summary>
internal readonly record struct OpenSocket(TransportProtocol Protocol, IPAddress LocalAddress, ushort LocalPort, uint ProcessId);

/// <summary>Reads every TCP and UDP socket with its owning process from the IP Helper tables.</summary>
internal static class OsSocketTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    // Row sizes of MIB_TCPROW_OWNER_PID, MIB_TCP6ROW_OWNER_PID, MIB_UDPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID.
    private const int Tcp4RowSize = 24;
    private const int Tcp6RowSize = 56;
    private const int Udp4RowSize = 12;
    private const int Udp6RowSize = 28;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(byte[]? table, ref int size, bool sort, int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(byte[]? table, ref int size, bool sort, int addressFamily, int tableClass, uint reserved);

    public static IReadOnlyList<OpenSocket> Read()
    {
        var sockets = new List<OpenSocket>();
        ReadTcp(AfInet, sockets);
        ReadTcp(AfInet6, sockets);
        ReadUdp(AfInet, sockets);
        ReadUdp(AfInet6, sockets);
        return sockets;
    }

    private static void ReadTcp(int family, List<OpenSocket> sockets)
    {
        var table = Fetch((byte[]? buffer, ref int size) =>
            GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0));
        if (family == AfInet)
            ParseRows(table, Tcp4RowSize, sockets, static row => new OpenSocket(
                TransportProtocol.Tcp, new IPAddress(row.Slice(4, 4)), Port(row.Slice(8, 4)), Dword(row.Slice(20, 4))));
        else
            ParseRows(table, Tcp6RowSize, sockets, static row => new OpenSocket(
                TransportProtocol.Tcp, Address6(row.Slice(0, 16)), Port(row.Slice(20, 4)), Dword(row.Slice(52, 4))));
    }

    private static void ReadUdp(int family, List<OpenSocket> sockets)
    {
        var table = Fetch((byte[]? buffer, ref int size) =>
            GetExtendedUdpTable(buffer, ref size, false, family, UdpTableOwnerPid, 0));
        if (family == AfInet)
            ParseRows(table, Udp4RowSize, sockets, static row => new OpenSocket(
                TransportProtocol.Udp, new IPAddress(row.Slice(0, 4)), Port(row.Slice(4, 4)), Dword(row.Slice(8, 4))));
        else
            ParseRows(table, Udp6RowSize, sockets, static row => new OpenSocket(
                TransportProtocol.Udp, Address6(row.Slice(0, 16)), Port(row.Slice(20, 4)), Dword(row.Slice(24, 4))));
    }

    private delegate uint TableCall(byte[]? buffer, ref int size);

    private delegate OpenSocket RowReader(ReadOnlySpan<byte> row);

    /// <summary>The table can grow between the size query and the read, so retry a few times.</summary>
    private static byte[] Fetch(TableCall call)
    {
        var size = 0;
        byte[]? buffer = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = call(buffer, ref size);
            if (result == NoError && buffer is not null)
                return buffer;
            if (result != ErrorInsufficientBuffer && result != NoError)
                throw new InvalidOperationException($"Reading the socket table failed (Windows error {result}).");
            buffer = new byte[size + 1024];
            size = buffer.Length;
        }
        throw new InvalidOperationException("The socket table kept growing while it was being read.");
    }

    private static void ParseRows(byte[] table, int rowSize, List<OpenSocket> sockets, RowReader read)
    {
        var count = (int)Dword(table.AsSpan(0, 4));
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(4 + i * rowSize, rowSize);
            var socket = read(row);
            if (socket.ProcessId != 0)
                sockets.Add(socket);
        }
    }

    private static uint Dword(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);

    /// <summary>Ports sit in network byte order in the low two bytes of the DWORD.</summary>
    private static ushort Port(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16BigEndian(value);

    /// <summary>
    /// The scope id is left off on purpose: addresses read from packet headers carry none,
    /// and IPAddress equality compares it.
    /// </summary>
    private static IPAddress Address6(ReadOnlySpan<byte> value) => new(value);
}
