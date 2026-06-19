using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IFlowTracker
{
    void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath);
    void UnregisterFlow(FlowKey flowKey);
    uint? LookupProcessId(FlowKey flowKey);
    ConnectionInfo? LookupConnection(FlowKey flowKey);
    IReadOnlyList<ConnectionInfo> GetActiveConnections();
    IReadOnlyList<ConnectionInfo> GetConnectionsByProcess(uint processId);
    void PurgeStale(TimeSpan maxAge);
}
