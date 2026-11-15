using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IFlowTracker
{
    /// <summary>Raised after a flow is registered, with the process it was registered to.</summary>
    event Action<uint, string, string?>? FlowRegistered;

    void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath);
    void UnregisterFlow(FlowKey flowKey);
    uint? LookupProcessId(FlowKey flowKey);
    ConnectionInfo? LookupConnection(FlowKey flowKey);
    IReadOnlyList<ConnectionInfo> GetActiveConnections();
    IReadOnlyList<ConnectionInfo> GetConnectionsByProcess(uint processId);
    void PurgeStale(TimeSpan maxAge);

    /// <summary>
    /// Drops every flow, open or closed, of a process that exited. Windows can give its ID to
    /// another process once it's let go, and a late packet on one of its flows would be
    /// credited to that process under the old one's name.
    /// </summary>
    void ForgetProcess(uint processId);
}
