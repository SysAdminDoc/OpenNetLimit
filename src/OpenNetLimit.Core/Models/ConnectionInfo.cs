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

    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    public bool IsActive => ClosedAt is null;
}
