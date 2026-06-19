using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Interception;

public class FlowTracker : IFlowTracker
{
    private readonly ConcurrentDictionary<FlowKey, ConnectionInfo> _flows = new();

    public void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath)
    {
        var connection = new ConnectionInfo
        {
            FlowKey = flowKey,
            ProcessId = processId,
            ProcessName = processName,
            ProcessPath = processPath
        };
        _flows.AddOrUpdate(flowKey, connection, (_, existing) =>
        {
            existing.ProcessName = processName;
            existing.ProcessPath = processPath;
            return existing;
        });
    }

    public void UnregisterFlow(FlowKey flowKey)
    {
        if (_flows.TryGetValue(flowKey, out var connection))
        {
            connection.ClosedAt = DateTime.UtcNow;
        }
    }

    public uint? LookupProcessId(FlowKey flowKey)
    {
        if (_flows.TryGetValue(flowKey, out var connection))
            return connection.ProcessId;

        var reversed = new FlowKey(
            flowKey.Protocol,
            flowKey.RemoteAddress,
            flowKey.RemotePort,
            flowKey.LocalAddress,
            flowKey.LocalPort);

        if (_flows.TryGetValue(reversed, out connection))
            return connection.ProcessId;

        return null;
    }

    public ConnectionInfo? LookupConnection(FlowKey flowKey)
    {
        if (_flows.TryGetValue(flowKey, out var connection))
            return connection;

        var reversed = new FlowKey(
            flowKey.Protocol,
            flowKey.RemoteAddress,
            flowKey.RemotePort,
            flowKey.LocalAddress,
            flowKey.LocalPort);

        _flows.TryGetValue(reversed, out connection);
        return connection;
    }

    public IReadOnlyList<ConnectionInfo> GetActiveConnections()
    {
        return _flows.Values.Where(c => c.IsActive).ToList();
    }

    public IReadOnlyList<ConnectionInfo> GetConnectionsByProcess(uint processId)
    {
        return _flows.Values.Where(c => c.ProcessId == processId).ToList();
    }

    public void PurgeStale(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleKeys = _flows
            .Where(kvp => kvp.Value.ClosedAt.HasValue && kvp.Value.ClosedAt.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
            _flows.TryRemove(key, out _);
    }
}
