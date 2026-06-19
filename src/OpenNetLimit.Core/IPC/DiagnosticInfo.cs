namespace OpenNetLimit.Core.IPC;

public class DiagnosticInfo
{
    public int ProtocolVersion { get; set; } = IpcProtocol.ProtocolVersion;
    public bool Running { get; set; }
    public int ActiveFlows { get; set; }
    public int ActiveRules { get; set; }
    public long PacketsDelayed { get; set; }
    public long PacketsDropped { get; set; }
    public long PacketsSent { get; set; }
    public DateTime StartedAt { get; set; }
}
