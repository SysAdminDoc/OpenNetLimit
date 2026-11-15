namespace OpenNetLimit.Core.IPC;

public class DiagnosticInfo
{
    public int ProtocolVersion { get; set; } = IpcProtocol.ProtocolVersion;
    public bool Running { get; set; }

    /// <summary>Why the packet engine isn't running, when the service knows. Null when it's healthy.</summary>
    public string? EngineError { get; set; }

    /// <summary>Paused on request: no packets are filtered and the driver is unloaded if it could be.</summary>
    public bool Paused { get; set; }

    /// <summary>Whether the pause stays on when the service or Windows restarts.</summary>
    public bool StayPaused { get; set; }

    /// <summary>While paused, why the WinDivert driver is still loaded. Null once it's unloaded.</summary>
    public string? DriverLoadedReason { get; set; }

    /// <summary>
    /// Why the service turns away changes to rules, alerts and the whole-PC limit, so the app can
    /// say so before one is tried. Null when it takes them.
    /// </summary>
    public string? ChangesRefused { get; set; }

    public int ActiveFlows { get; set; }
    public int ActiveRules { get; set; }
    public long PacketsDelayed { get; set; }
    public long PacketsDropped { get; set; }
    public long PacketsSent { get; set; }
    public long PacketsBlocked { get; set; }
    public DateTime StartedAt { get; set; }
}
