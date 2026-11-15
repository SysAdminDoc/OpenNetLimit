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
    private readonly Dictionary<uint, (long Received, long Sent)> _usageMarks = new();

    public event Action<TrafficSnapshot>? OnSnapshot;

    /// <param name="rateLimiter">Where the limit each process is held to is read from.</param>
    /// <param name="serviceHosts">Where the services in each process are read from.</param>
    public TrafficMonitor(TimeSpan? snapshotInterval = null, IRateLimiter? rateLimiter = null,
        IServiceHosts? serviceHosts = null)
        : this(snapshotInterval ?? TimeSpan.FromSeconds(1), rateLimiter, sampleOnTimer: true, serviceHosts)
    {
    }

    /// <summary>
    /// With <paramref name="sampleOnTimer"/> off, rates are only sampled by calling
    /// <see cref="SampleRates"/>, so a test can't lose its bytes to a timer tick mid-test.
    /// </summary>
    internal TrafficMonitor(TimeSpan snapshotInterval, IRateLimiter? rateLimiter, bool sampleOnTimer,
        IServiceHosts? serviceHosts = null)
    {
        _intervalSeconds = snapshotInterval.TotalSeconds;
        _rateLimiter = rateLimiter;
        _serviceHosts = serviceHosts;
        _snapshotTimer = sampleOnTimer
            ? new Timer(OnSnapshotTick, null, snapshotInterval, snapshotInterval)
            : new Timer(OnSnapshotTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null)
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
                LastActivityAt = DateTime.UtcNow
            };
            EnrichProcessInfo(p);
            return p;
        });

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
        var protection = ProtectedProcesses.Of(info.ProcessName, services);
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

    /// <summary>
    /// Closes the current window: each process's speed becomes what it moved during it, and its
    /// limit is read from the rate limiter. The monitor's timer calls this once per interval.
    /// Snapshots used to do this themselves, so the timer, the stats recorder, the app and the
    /// API each zeroed the counters for the others and every one of them read short speeds.
    /// </summary>
    public void SampleRates()
    {
        foreach (var (pid, info) in _processes)
        {
            long down = 0, up = 0;
            if (_counters.TryGetValue(pid, out var counter))
            {
                down = (long)Math.Round(Interlocked.Exchange(ref counter.DownloadBytes, 0) / _intervalSeconds);
                up = (long)Math.Round(Interlocked.Exchange(ref counter.UploadBytes, 0) / _intervalSeconds);
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
    }

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
            var seen = new HashSet<uint>();

            foreach (var (pid, info) in _processes)
            {
                seen.Add(pid);
                long received = info.TotalBytesReceived;
                long sent = info.TotalBytesSent;
                var mark = _usageMarks.GetValueOrDefault(pid);
                long down = received >= mark.Received ? received - mark.Received : received;
                long up = sent >= mark.Sent ? sent - mark.Sent : sent;
                _usageMarks[pid] = (received, sent);
                totalDown += down;
                totalUp += up;
                processes.Add(Copy(info, down, up));
            }

            foreach (var pid in _usageMarks.Keys.Where(pid => !seen.Contains(pid)).ToList())
                _usageMarks.Remove(pid);

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
        GC.SuppressFinalize(this);
    }

    private class TrafficCounter
    {
        public long DownloadBytes;
        public long UploadBytes;
    }
}
