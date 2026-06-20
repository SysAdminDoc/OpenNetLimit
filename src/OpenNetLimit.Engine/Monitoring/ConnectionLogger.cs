using System.Collections.Concurrent;

namespace OpenNetLimit.Engine.Monitoring;

public class ConnectionLogger
{
    private readonly ConcurrentQueue<ConnectionLogEntry> _entries = new();
    private int _count;

    public const int MaxEntries = 10_000;

    public void Log(ConnectionLogEntry entry)
    {
        _entries.Enqueue(entry);
        var count = Interlocked.Increment(ref _count);

        while (count > MaxEntries && _entries.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _count);
        }
    }

    public IReadOnlyList<ConnectionLogEntry> GetRecent(int maxCount = 100)
    {
        var snapshot = _entries.ToArray();
        var start = Math.Max(0, snapshot.Length - maxCount);
        return new ArraySegment<ConnectionLogEntry>(snapshot, start, snapshot.Length - start);
    }

    public int Count => Volatile.Read(ref _count);
}

public class ConnectionLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string LocalEndpoint { get; init; } = string.Empty;
    public string RemoteEndpoint { get; init; } = string.Empty;
    public string? RuleName { get; init; }
}
