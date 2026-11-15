using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Interception;

public class FlowTracker : IFlowTracker
{
    private readonly ConcurrentDictionary<FlowKey, ConnectionInfo> _flows = new();
    private readonly Func<DateTime> _utcNow;

    public FlowTracker()
        : this(() => DateTime.UtcNow)
    {
    }

    internal FlowTracker(Func<DateTime> utcNow) => _utcNow = utcNow;

    public event Action<uint, string, string?>? FlowRegistered;

    public event Action<uint>? ForgettingProcess;

    public void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath)
    {
        var registeredAt = _utcNow();
        var connection = new ConnectionInfo
        {
            FlowKey = flowKey,
            ProcessId = processId,
            ProcessName = processName,
            ProcessPath = processPath,
            EstablishedAt = registeredAt
        };
        _flows.AddOrUpdate(flowKey, connection, (_, existing) =>
        {
            // Create a new ConnectionInfo to avoid mutating the shared object
            // that NetworkLoop may be concurrently reading.
            // Preserve accumulated byte counters from the existing instance.
            var updated = new ConnectionInfo
            {
                FlowKey = flowKey,
                ProcessId = processId,
                ProcessName = processName,
                ProcessPath = processPath,
                EstablishedAt = registeredAt,
                ClosedAt = existing.ClosedAt
            };
            updated.AddBytesSent(existing.BytesSent);
            updated.AddBytesReceived(existing.BytesReceived);
            return updated;
        });
        FlowRegistered?.Invoke(processId, processName, processPath);
    }

    public void UnregisterFlow(FlowKey flowKey)
    {
        // Replace with a new ConnectionInfo that has ClosedAt set,
        // avoiding torn DateTime? reads by PurgeStale on the timer thread.
        // A flow that isn't here (never registered, or its process exited) stays gone: adding
        // it with no process would credit its last packets to process 0.
        while (_flows.TryGetValue(flowKey, out var existing))
        {
            var closed = new ConnectionInfo
            {
                FlowKey = existing.FlowKey,
                ProcessId = existing.ProcessId,
                ProcessName = existing.ProcessName,
                ProcessPath = existing.ProcessPath,
                EstablishedAt = existing.EstablishedAt,
                ClosedAt = _utcNow()
            };
            if (_flows.TryUpdate(flowKey, closed, existing))
                return;
        }
    }

    public void ForgetProcess(uint processId, DateTime? establishedBefore = null)
    {
        ForgettingProcess?.Invoke(processId);
        foreach (var (flowKey, connection) in _flows)
        {
            // Only the exact entry seen: a flow registered again meanwhile is someone else's.
            if (connection.ProcessId == processId
                && (establishedBefore is null || connection.EstablishedAt < establishedBefore))
                _flows.TryRemove(KeyValuePair.Create(flowKey, connection));
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
        var cutoff = _utcNow() - maxAge;
        var staleKeys = _flows
            .Where(kvp => kvp.Value.ClosedAt.HasValue && kvp.Value.ClosedAt.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
            _flows.TryRemove(key, out _);
    }
}
