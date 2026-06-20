using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Monitoring;

public class TrafficMonitor : ITrafficMonitor, IDisposable
{
    private readonly ConcurrentDictionary<uint, ProcessTrafficInfo> _processes = new();
    private readonly ConcurrentDictionary<uint, TrafficCounter> _counters = new();
    private readonly Timer _snapshotTimer;

    public event Action<TrafficSnapshot>? OnSnapshot;

    public TrafficMonitor(TimeSpan? snapshotInterval = null)
    {
        var interval = snapshotInterval ?? TimeSpan.FromSeconds(1);
        _snapshotTimer = new Timer(OnSnapshotTick, null, interval, interval);
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

    private static void EnrichProcessInfo(ProcessTrafficInfo info)
    {
        try
        {
            info.ServiceName = ProcessIdentifier.GetServiceName(info.ProcessId);
            info.AppxPackage = ProcessIdentifier.GetAppxPackageName(info.ProcessId);
        }
        catch
        {
            // Best-effort enrichment
        }
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

    public TrafficSnapshot TakeSnapshot()
    {
        long totalDown = 0, totalUp = 0;
        var processes = new List<ProcessTrafficInfo>();

        foreach (var (pid, info) in _processes)
        {
            long downSpeed = 0, upSpeed = 0;
            if (_counters.TryGetValue(pid, out var counter))
            {
                downSpeed = Interlocked.Exchange(ref counter.DownloadBytes, 0);
                upSpeed = Interlocked.Exchange(ref counter.UploadBytes, 0);
                info.CurrentDownloadBytesPerSecond = downSpeed;
                info.CurrentUploadBytesPerSecond = upSpeed;
            }
            totalDown += downSpeed;
            totalUp += upSpeed;

            // Return a snapshot copy to prevent consumers from observing
            // concurrent mutations to LastActivityAt, ProcessPath, etc.
            processes.Add(new ProcessTrafficInfo
            {
                ProcessId = info.ProcessId,
                ProcessName = info.ProcessName,
                ProcessPath = info.ProcessPath,
                ServiceName = info.ServiceName,
                AppxPackage = info.AppxPackage,
                CurrentDownloadBytesPerSecond = downSpeed,
                CurrentUploadBytesPerSecond = upSpeed,
                TotalBytesReceived = info.TotalBytesReceived,
                TotalBytesSent = info.TotalBytesSent,
                DownloadLimitBytesPerSecond = info.DownloadLimitBytesPerSecond,
                UploadLimitBytesPerSecond = info.UploadLimitBytesPerSecond,
                ActiveConnectionCount = info.ActiveConnectionCount,
                FirstSeenAt = info.FirstSeenAt,
                LastActivityAt = info.LastActivityAt
            });
        }

        return new TrafficSnapshot
        {
            Processes = processes,
            TotalDownloadBytesPerSecond = totalDown,
            TotalUploadBytesPerSecond = totalUp
        };
    }

    private void OnSnapshotTick(object? state)
    {
        var snapshot = TakeSnapshot();
        OnSnapshot?.Invoke(snapshot);
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
