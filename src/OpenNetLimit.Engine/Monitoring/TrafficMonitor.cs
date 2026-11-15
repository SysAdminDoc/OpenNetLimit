using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Monitoring;

public class TrafficMonitor : ITrafficMonitor, IDisposable
{
    private readonly ConcurrentDictionary<uint, ProcessTrafficInfo> _processes = new();
    private readonly ConcurrentDictionary<uint, TrafficCounter> _counters = new();
    private readonly Timer _snapshotTimer;
    private readonly IRateLimiter? _rateLimiter;
    private readonly IServiceHosts? _serviceHosts;
    private readonly double _intervalSeconds;
    private readonly object _usageLock = new();
    private readonly Dictionary<ProcessTrafficInfo, (long Received, long Sent)> _usageMarks =
        new(ReferenceEqualityComparer.Instance);

    private readonly HoldProcess? _holdProcess;
    private readonly IFlowTracker? _flows;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sampleLock = new();

    /// <summary>The process behind each entry, held until it exits. Only used under <see cref="_sampleLock"/>.</summary>
    private readonly Dictionary<uint, Held> _held = new();

    /// <summary>Set by <see cref="Dispose"/>. Only used under <see cref="_sampleLock"/>.</summary>
    private bool _disposed;

    /// <summary>
    /// Guards <see cref="_exited"/> and <see cref="_retired"/>, and an entry's move from
    /// <see cref="_processes"/> to <see cref="_exited"/>, so quotas never count it twice or not at all.
    /// </summary>
    private readonly object _exitedLock = new();

    /// <summary>Processes that exited lately. The usage history still takes their last bytes.</summary>
    private readonly List<(ProcessTrafficInfo Info, DateTime ExitedAt)> _exited = [];

    /// <summary>What exited processes moved, per program, path and services, so quotas keep counting it.</summary>
    private readonly Dictionary<string, ProcessTrafficInfo> _retired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long an exited process is kept on its own before its bytes join its program's total.
    /// The usage history takes what's new every 10 seconds, so this is well past its last take.
    /// </summary>
    internal TimeSpan ExitedKeptFor { get; init; } = TimeSpan.FromMinutes(10);

    public event Action<TrafficSnapshot>? OnSnapshot;

    /// <param name="rateLimiter">Where the limit each process is held to is read from.</param>
    /// <param name="serviceHosts">Where the services in each process are read from.</param>
    public TrafficMonitor(TimeSpan? snapshotInterval = null, IRateLimiter? rateLimiter = null,
        IServiceHosts? serviceHosts = null)
        : this(snapshotInterval ?? TimeSpan.FromSeconds(1), rateLimiter, sampleOnTimer: true, serviceHosts)
    {
    }

    /// <summary>
    /// The service's monitor. It holds each process it counts, so Windows can't give the ID to
    /// another process unseen: one that exits leaves the list, and the next process with its ID
    /// starts its own entry instead of adding to the old one.
    /// </summary>
    public static TrafficMonitor ForService(IRateLimiter rateLimiter, IServiceHosts serviceHosts, IFlowTracker flows) =>
        new(TimeSpan.FromSeconds(1), rateLimiter, sampleOnTimer: true, serviceHosts, HeldProcess.Hold, flows: flows);

    /// <summary>
    /// With <paramref name="sampleOnTimer"/> off, rates are only sampled by calling
    /// <see cref="SampleRates"/>, so a test can't lose its bytes to a timer tick mid-test.
    /// Without <paramref name="holdProcess"/> no process is held and none leaves the list.
    /// A process that leaves it takes its flows in <paramref name="flows"/> with it.
    /// </summary>
    internal TrafficMonitor(TimeSpan snapshotInterval, IRateLimiter? rateLimiter, bool sampleOnTimer,
        IServiceHosts? serviceHosts = null, HoldProcess? holdProcess = null, Func<DateTime>? utcNow = null,
        IFlowTracker? flows = null)
    {
        _intervalSeconds = snapshotInterval.TotalSeconds;
        _rateLimiter = rateLimiter;
        _serviceHosts = serviceHosts;
        _holdProcess = holdProcess;
        _flows = flows;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _snapshotTimer = sampleOnTimer
            ? new Timer(OnSnapshotTick, null, snapshotInterval, snapshotInterval)
            : new Timer(OnSnapshotTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null,
        DateTime? namedAt = null)
    {
        var counter = _counters.GetOrAdd(processId, _ => new TrafficCounter());
        if (isUpload)
            Interlocked.Add(ref counter.UploadBytes, byteCount);
        else
            Interlocked.Add(ref counter.DownloadBytes, byteCount);

        var info = _processes.GetOrAdd(processId, _ =>
        {
            var p = new ProcessTrafficInfo
            {
                ProcessId = processId,
                ProcessName = processName,
                ProcessPath = processPath,
                FirstSeenAt = _utcNow(),
                LastActivityAt = DateTime.UtcNow
            };
            // Before the entry is out: the next sample holds the process against it.
            if (namedAt is { } at)
                p.NamedBy(at);
            EnrichProcessInfo(p);
            return p;
        });
        if (namedAt is { } named)
            info.NamedBy(named);

        // Always use atomic AddBytes to prevent first-packet race —
        // two threads can both call GetOrAdd's factory, the loser's
        // factory result is discarded but AddBytes still runs correctly
        // on the winning instance.
        if (isUpload)
            info.AddBytesSent(byteCount);
        else
            info.AddBytesReceived(byteCount);

        if (!string.IsNullOrWhiteSpace(processPath))
            info.ProcessPath = processPath;
        info.LastActivityAt = DateTime.UtcNow;
    }

    private void EnrichProcessInfo(ProcessTrafficInfo info)
    {
        ReadServices(info);
        try
        {
            info.AppxPackage = ProcessIdentifier.GetAppxPackageName(info.ProcessId);
        }
        catch
        {
            // Best-effort enrichment
        }
    }

    /// <summary>
    /// The services in the process and whether that keeps it out of rules, from the last read of
    /// the service list. A svchost started since that read shows as Windows' own until the next.
    /// </summary>
    private void ReadServices(ProcessTrafficInfo info)
    {
        var services = _serviceHosts?.ServicesIn(info.ProcessId);
        // Gone from the list means it exited. What it sent and received still belongs to the
        // services it ran, so their quotas don't drop when a svchost stops for being idle.
        if (services is null && info.Services is not null)
            return;
        var protection = ProtectedProcesses.Of(info.ProcessName, services, info.ProcessPath);
        bool isProtected = protection != ProcessProtection.None;
        if (ReferenceEquals(services, info.Services) && isProtected == info.IsProtected)
            return;
        var names = services as string[] ?? services?.ToArray();
        info.Services = names;
        info.ServiceName = names?.FirstOrDefault();
        info.NetworkingServices = ProtectedProcesses.NetworkingIn(names);
        info.IsProtected = isProtected;
    }

    public ProcessTrafficInfo? GetProcessInfo(uint processId)
    {
        _processes.TryGetValue(processId, out var info);
        return info;
    }

    public IReadOnlyList<ProcessTrafficInfo> GetAllProcesses()
    {
        return _processes.Values.ToList();
    }

    public IReadOnlyList<ProcessTrafficInfo> GetUsageTotals()
    {
        lock (_exitedLock)
        {
            var all = new List<ProcessTrafficInfo>(_processes.Count + _exited.Count + _retired.Count);
            all.AddRange(_processes.Values);
            foreach (var (info, _) in _exited)
                all.Add(info);
            all.AddRange(_retired.Values);
            return all;
        }
    }

    /// <summary>
    /// Closes the current window: each process's speed becomes what it moved during it, and its
    /// limit is read from the rate limiter. The monitor's timer calls this once per interval.
    /// Snapshots used to do this themselves, so the timer, the stats recorder, the app and the
    /// API each zeroed the counters for the others and every one of them read short speeds.
    /// A process that exited leaves the list here.
    /// </summary>
    public void SampleRates()
    {
        List<(uint ProcessId, IHeldProcess? Process)>? retired = null;
        lock (_sampleLock)
        {
            // A tick already on its way when the monitor was disposed would hold processes again.
            if (_disposed)
                return;
            foreach (var (pid, info) in _processes)
            {
                long down = 0, up = 0;
                if (_counters.TryGetValue(pid, out var counter))
                {
                    down = (long)Math.Round(Interlocked.Exchange(ref counter.DownloadBytes, 0) / _intervalSeconds);
                    up = (long)Math.Round(Interlocked.Exchange(ref counter.UploadBytes, 0) / _intervalSeconds);
                }
                if (HasExited(pid, info))
                {
                    (retired ??= []).Add((pid, Retire(pid, info)));
                    continue;
                }
                info.CurrentDownloadBytesPerSecond = down;
                info.CurrentUploadBytesPerSecond = up;
                ReadServices(info);

                if (_rateLimiter is not null)
                {
                    bool limited = _rateLimiter.TryGetLimit(pid, out var downLimit, out var upLimit);
                    info.DownloadLimitBytesPerSecond = limited && downLimit > 0 ? downLimit : null;
                    info.UploadLimitBytesPerSecond = limited && upLimit > 0 ? upLimit : null;
                }
            }
            FoldExited();
        }
        // Out of the lock: the socket owners may be mid-read of the socket tables, and the next
        // tick shouldn't wait for that.
        if (retired is not null)
            foreach (var (pid, process) in retired)
                LetGo(pid, process);
    }

    /// <summary>Whether the process behind the entry is gone. It's held from its first window on.</summary>
    private bool HasExited(uint processId, ProcessTrafficInfo info)
    {
        if (_holdProcess is null)
            return false;
        if (!_held.TryGetValue(processId, out var held) || !ReferenceEquals(held.Info, info))
        {
            held?.Process.Dispose();
            _held.Remove(processId);
            // Bytes named before the first packet (a socket read's name, say) can be from a
            // process that exited unseen. The one that has the ID must have started before them.
            var since = info.NamedSince is { } named && named < info.FirstSeenAt ? named : info.FirstSeenAt;
            var process = _holdProcess(processId, since);
            if (process is null)
                return true;
            _held[processId] = held = new Held(info, process);
        }
        return held.Process.HasExited;
    }

    /// <summary>
    /// Takes an exited process off the list, and hands back what holds it, for <see cref="LetGo"/>.
    /// Its ID can't go to another process before that, so the next one with it starts its own entry.
    /// </summary>
    private IHeldProcess? Retire(uint processId, ProcessTrafficInfo info)
    {
        info.CurrentDownloadBytesPerSecond = 0;
        info.CurrentUploadBytesPerSecond = 0;
        info.DownloadLimitBytesPerSecond = null;
        info.UploadLimitBytesPerSecond = null;
        lock (_exitedLock)
        {
            _processes.TryRemove(KeyValuePair.Create(processId, info));
            _exited.Add((info, _utcNow()));
        }
        _counters.TryRemove(processId, out _);
        _held.Remove(processId, out var held);
        return held?.Process;
    }

    /// <summary>Drops an exited process's flows, then lets go of it.</summary>
    private void LetGo(uint processId, IHeldProcess? process)
    {
        // While it's held the ID can't be reused, so every flow named for it was named for the
        // process that exited, and so is a name read before now that comes in late. When it
        // wasn't held all along, flows named since the ID was free may be the next process's, and
        // stay. With no hold at all, nothing is known about who has the ID now: its flows go, and
        // nothing stays behind to turn a later one away.
        if (process is null)
            _flows?.ForgetProcess(processId);
        else if (process.IdFreeSince is { } freeSince)
            _flows?.ForgetProcess(processId, freeSince);
        else
            _flows?.ForgetHeldProcess(processId);
        process?.Dispose();
    }

    /// <summary>Adds processes that exited a while ago to their program's total, so memory doesn't grow.</summary>
    private void FoldExited()
    {
        var cutoff = _utcNow() - ExitedKeptFor;
        lock (_exitedLock)
        {
            _exited.RemoveAll(exited =>
            {
                if (exited.ExitedAt > cutoff)
                    return false;
                var info = exited.Info;
                var key = string.Join('\n', info.ProcessName, info.ProcessPath,
                    string.Join(',', info.Services ?? []));
                if (!_retired.TryGetValue(key, out var total))
                {
                    _retired[key] = total = new ProcessTrafficInfo
                    {
                        ProcessName = info.ProcessName,
                        ProcessPath = info.ProcessPath,
                        Services = info.Services,
                        ServiceName = info.ServiceName,
                        IsProtected = info.IsProtected,
                        NetworkingServices = info.NetworkingServices,
                        FirstSeenAt = info.FirstSeenAt
                    };
                }
                total.AddBytesReceived(info.TotalBytesReceived);
                total.AddBytesSent(info.TotalBytesSent);
                total.LastActivityAt = info.LastActivityAt;
                return true;
            });
        }
    }

    private sealed record Held(ProcessTrafficInfo Info, IHeldProcess Process);

    /// <summary>The speeds from the last closed window. Reading it changes nothing.</summary>
    public TrafficSnapshot TakeSnapshot()
    {
        long totalDown = 0, totalUp = 0;
        var processes = new List<ProcessTrafficInfo>();

        foreach (var info in _processes.Values)
        {
            long down = info.CurrentDownloadBytesPerSecond;
            long up = info.CurrentUploadBytesPerSecond;
            totalDown += down;
            totalUp += up;
            processes.Add(Copy(info, down, up));
        }

        return new TrafficSnapshot
        {
            Processes = processes,
            TotalDownloadBytesPerSecond = totalDown,
            TotalUploadBytesPerSecond = totalUp
        };
    }

    /// <summary>
    /// The bytes each process moved since the previous call, in the Current* fields, which is
    /// how the usage history stores a record. Only the history recorder calls this.
    /// </summary>
    public TrafficSnapshot TakeUsage()
    {
        lock (_usageLock)
        {
            long totalDown = 0, totalUp = 0;
            var processes = new List<ProcessTrafficInfo>();
            var seen = new HashSet<ProcessTrafficInfo>(ReferenceEqualityComparer.Instance);

            // Processes that exited lately too: what they moved before exiting is still new here.
            List<ProcessTrafficInfo> counted;
            lock (_exitedLock)
            {
                counted = [.. _processes.Values];
                foreach (var (info, _) in _exited)
                    counted.Add(info);
            }

            foreach (var info in counted)
            {
                seen.Add(info);
                long received = info.TotalBytesReceived;
                long sent = info.TotalBytesSent;
                var mark = _usageMarks.GetValueOrDefault(info);
                long down = received >= mark.Received ? received - mark.Received : received;
                long up = sent >= mark.Sent ? sent - mark.Sent : sent;
                _usageMarks[info] = (received, sent);
                totalDown += down;
                totalUp += up;
                processes.Add(Copy(info, down, up));
            }

            foreach (var info in _usageMarks.Keys.Where(info => !seen.Contains(info)).ToList())
                _usageMarks.Remove(info);

            return new TrafficSnapshot
            {
                Processes = processes,
                TotalDownloadBytesPerSecond = totalDown,
                TotalUploadBytesPerSecond = totalUp
            };
        }
    }

    /// <summary>
    /// A copy, so consumers never see concurrent changes to LastActivityAt, ProcessPath and the rest.
    /// </summary>
    private static ProcessTrafficInfo Copy(ProcessTrafficInfo info, long down, long up) => new()
    {
        ProcessId = info.ProcessId,
        ProcessName = info.ProcessName,
        ProcessPath = info.ProcessPath,
        ServiceName = info.ServiceName,
        Services = info.Services,
        IsProtected = info.IsProtected,
        NetworkingServices = info.NetworkingServices,
        AppxPackage = info.AppxPackage,
        CurrentDownloadBytesPerSecond = down,
        CurrentUploadBytesPerSecond = up,
        TotalBytesReceived = info.TotalBytesReceived,
        TotalBytesSent = info.TotalBytesSent,
        DownloadLimitBytesPerSecond = info.DownloadLimitBytesPerSecond,
        UploadLimitBytesPerSecond = info.UploadLimitBytesPerSecond,
        ActiveConnectionCount = info.ActiveConnectionCount,
        FirstSeenAt = info.FirstSeenAt,
        LastActivityAt = info.LastActivityAt
    };

    private void OnSnapshotTick(object? state)
    {
        SampleRates();
        OnSnapshot?.Invoke(TakeSnapshot());
    }

    public void Dispose()
    {
        _snapshotTimer.Dispose();
        lock (_sampleLock)
        {
            _disposed = true;
            foreach (var held in _held.Values)
                held.Process.Dispose();
            _held.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private class TrafficCounter
    {
        public long DownloadBytes;
        public long UploadBytes;
    }
}
