using System.Net;

namespace OpenNetLimit.Core.Models;

public enum TransportProtocol
{
    Tcp,
    Udp,
    Other
}

public readonly record struct FlowKey(
    TransportProtocol Protocol,
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort);

public class ConnectionInfo
{
    public FlowKey FlowKey { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }
    public DateTime EstablishedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    private long _bytesSent;
    public long BytesSent
    {
        get => Volatile.Read(ref _bytesSent);
        set => Volatile.Write(ref _bytesSent, value);
    }

    private long _bytesReceived;
    public long BytesReceived
    {
        get => Volatile.Read(ref _bytesReceived);
        set => Volatile.Write(ref _bytesReceived, value);
    }

    public void AddBytesSent(long count) => Interlocked.Add(ref _bytesSent, count);
    public void AddBytesReceived(long count) => Interlocked.Add(ref _bytesReceived, count);

    public bool IsIPv6 => FlowKey.LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    public bool IsActive => ClosedAt is null;
}
