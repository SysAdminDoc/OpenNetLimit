using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IFlowTracker
{
    /// <summary>Raised after a flow is registered, with the process it was registered to.</summary>
    event Action<uint, string, string?>? FlowRegistered;

    /// <param name="identifiedAt">
    /// When the process was last known to be the one named, if that was before now (a name read
    /// with the socket tables, say). It's what <see cref="ForgetProcess"/> goes by.
    /// </param>
    void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath,
        DateTime? identifiedAt = null);
    void UnregisterFlow(FlowKey flowKey);
    uint? LookupProcessId(FlowKey flowKey);
    ConnectionInfo? LookupConnection(FlowKey flowKey);
    IReadOnlyList<ConnectionInfo> GetActiveConnections();
    IReadOnlyList<ConnectionInfo> GetConnectionsByProcess(uint processId);
    void PurgeStale(TimeSpan maxAge);

    /// <summary>
    /// Raised first thing in <see cref="ForgetProcess"/>, so whatever else could hand a late
    /// packet back to the process forgets it before its flows go.
    /// </summary>
    event Action<uint>? ForgettingProcess;

    /// <summary>
    /// Drops the flows, open or closed, of a process that exited. Windows can give its ID to
    /// another process once it's let go, and a late packet on one of its flows would be
    /// credited to that process under the old one's name. When the ID may have gone to another
    /// process from <paramref name="identifiedBefore"/> on, only flows named before then go.
    /// </summary>
    void ForgetProcess(uint processId, DateTime? identifiedBefore = null);
}
