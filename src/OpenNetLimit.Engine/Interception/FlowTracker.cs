using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;

namespace OpenNetLimit.Engine.Interception;

public class FlowTracker : IFlowTracker
{
    private readonly ConcurrentDictionary<FlowKey, ConnectionInfo> _flows = new();
    private readonly Func<DateTime> _utcNow;

    private readonly Func<long> _tickMs;

    /// <summary>
    /// The cutoff each exited process was forgotten with. A name read before it can still be on
    /// its way in (a socket owner looked up just before the forget), and must not bring it back.
    /// </summary>
    private readonly ConcurrentDictionary<uint, ForgetMark> _forgottenBefore = new();

    /// <summary>
    /// A forget's cutoff, and when it was made by the clock and by the tick count, which setting
    /// the clock doesn't move.
    /// </summary>
    private readonly record struct ForgetMark(DateTime Before, DateTime MadeAt, long MadeAtMs);

    /// <summary>A moment by the clock and by the tick count, read together.</summary>
    private readonly record struct Instant(DateTime Utc, long TickMs);

    /// <summary>How long a cutoff is kept, by the tick count. Names on their way in are microseconds old.</summary>
    private static readonly TimeSpan ForgottenKeptFor = TimeSpan.FromMinutes(1);

    public FlowTracker()
        : this(() => DateTime.UtcNow, () => Environment.TickCount64)
    {
    }

    internal FlowTracker(Func<DateTime> utcNow, Func<long> tickMs)
    {
        _utcNow = utcNow;
        _tickMs = tickMs;
    }

    /// <summary>
    /// The tick count, then the clock, for a check. A thread held up between the two reads makes
    /// the clock look ahead, which no check minds. The other way round it would look like the clock
    /// set back. A cutoff is made the other way round (<see cref="MarkForgotten"/>).
    /// </summary>
    private Instant Now()
    {
        long tickMs = _tickMs();
        return new Instant(_utcNow(), tickMs);
    }

    public event Action<uint, string, string?>? FlowRegistered;

    public event Action<uint, DateTime?>? ForgettingProcess;

    public void RegisterFlow(FlowKey flowKey, uint processId, string processName, string? processPath,
        DateTime? identifiedAt = null)
    {
        var now = Now();
        var registeredAt = now.Utc;
        var namedAt = identifiedAt ?? registeredAt;
        if (Forgotten(processId, namedAt, now))
            return;
        var connection = new ConnectionInfo
        {
            FlowKey = flowKey,
            ProcessId = processId,
            ProcessName = processName,
            ProcessPath = processPath,
            EstablishedAt = registeredAt,
            IdentifiedAt = identifiedAt ?? registeredAt
        };
        var stored = _flows.AddOrUpdate(flowKey, connection, (_, existing) =>
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
                // Named again, it's still the connection it was.
                EstablishedAt = existing.ProcessId == processId ? existing.EstablishedAt : registeredAt,
                IdentifiedAt = identifiedAt ?? registeredAt,
                ClosedAt = existing.ClosedAt
            };
            updated.AddBytesSent(existing.BytesSent);
            updated.AddBytesReceived(existing.BytesReceived);
            return updated;
        });
        // Forgotten while this went in, and missed by the forget's sweep of the flows. The clock
        // is read again, since a forget made while this thread was held up is newer than the first read.
        if (Forgotten(processId, namedAt, Now()))
        {
            _flows.TryRemove(KeyValuePair.Create(flowKey, stored));
            return;
        }
        FlowRegistered?.Invoke(processId, processName, processPath);
    }

    /// <summary>
    /// Whether a flow named at <paramref name="namedAt"/> was named for a process that's been
    /// forgotten since. A name stamped well past <paramref name="now"/> was made before the clock
    /// was set back, and the clock hasn't gone back since the forget, so it's older than that too.
    /// A cutoff found void is dropped, so putting the clock right again can't bring it back to
    /// turn away the names it let in meanwhile.
    /// </summary>
    private bool Forgotten(uint processId, DateTime namedAt, Instant now)
    {
        if (!_forgottenBefore.TryGetValue(processId, out var mark))
            return false;
        if (!StillHolds(mark, now))
        {
            _forgottenBefore.TryRemove(KeyValuePair.Create(processId, mark));
            return false;
        }
        return namedAt < mark.Before || namedAt - now.Utc > HeldProcess.ClockSlack;
    }

    /// <summary>
    /// Whether a cutoff still turns names away: it's under a minute old by the tick count, and the
    /// clock hasn't fallen back more than <see cref="HeldProcess.ClockSlack"/> against the tick
    /// count since it was made. One made before the clock went back would turn away every
    /// process that got the ID until the clock caught up.
    /// </summary>
    private static bool StillHolds(ForgetMark mark, Instant now)
    {
        var age = TimeSpan.FromMilliseconds(now.TickMs - mark.MadeAtMs);
        return age < ForgottenKeptFor && now.Utc - mark.MadeAt >= age - HeldProcess.ClockSlack;
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
                IdentifiedAt = existing.IdentifiedAt,
                ClosedAt = _utcNow()
            };
            if (_flows.TryUpdate(flowKey, closed, existing))
                return;
        }
    }

    /// <summary>
    /// Drops the process's flows named before <paramref name="identifiedBefore"/>, or all of them
    /// when it's null, and turns away names from before it that come in late. The sweep and
    /// <see cref="ForgettingProcess"/> get the cutoff as given, even one well past the clock: they
    /// act once, on what's there, which was all named before now. Held to the clock, they'd only
    /// spare the old process's names from before the clock went back.
    /// </summary>
    public void ForgetProcess(uint processId, DateTime? identifiedBefore = null)
    {
        if (identifiedBefore is { } cutoff)
            MarkForgotten(processId, cutoff);
        ForgettingProcess?.Invoke(processId, identifiedBefore);
        Sweep(processId, identifiedBefore);
    }

    public void ForgetHeldProcess(uint processId)
    {
        MarkForgotten(processId, null);
        ForgettingProcess?.Invoke(processId, null);
        Sweep(processId, null);
    }

    /// <summary>
    /// Turns away names for <paramref name="processId"/> from before <paramref name="before"/>, or
    /// from before now when it's null, for the next minute.
    /// </summary>
    private void MarkForgotten(uint processId, DateTime? before)
    {
        // The clock, then the tick count. A thread held up between the two reads makes the clock
        // look ahead of the mark from then on. The other way round, the mark would look made after
        // the clock was set back, and the first check once the hold passed the slack would void it.
        var madeAt = _utcNow();
        long madeAtMs = _tickMs();
        var cutoff = before ?? madeAt;
        // A cutoff well past the clock was taken before the clock went back. Held to it, whatever
        // gets the ID next would be turned away for the minute. Names from before the clock went
        // back read as ahead of it and are turned away all the same.
        if (cutoff - madeAt > HeldProcess.ClockSlack)
            cutoff = madeAt;
        var mark = new ForgetMark(cutoff, madeAt, madeAtMs);
        var now = Now();
        foreach (var (otherId, other) in _forgottenBefore)
            if (!StillHolds(other, now))
                _forgottenBefore.TryRemove(KeyValuePair.Create(otherId, other));
        // Forgotten again, the later cutoff stays, kept for a minute from this forget.
        _forgottenBefore.AddOrUpdate(processId, mark,
            (_, earlier) => StillHolds(earlier, now) && earlier.Before > cutoff ? mark with { Before = earlier.Before } : mark);
    }

    private void Sweep(uint processId, DateTime? identifiedBefore)
    {
        foreach (var (flowKey, connection) in _flows)
        {
            // Only the exact entry seen: a flow registered again meanwhile is someone else's.
            if (connection.ProcessId == processId
                && (identifiedBefore is null || connection.IdentifiedAt < identifiedBefore))
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
