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

        _processes.AddOrUpdate(processId,
            _ =>
            {
                var info = new ProcessTrafficInfo
                {
                    ProcessId = processId,
                    ProcessName = processName,
                    ProcessPath = processPath,
                    TotalBytesReceived = isUpload ? 0 : byteCount,
                    TotalBytesSent = isUpload ? byteCount : 0,
                    LastActivityAt = DateTime.UtcNow
                };
                EnrichProcessInfo(info);
                return info;
            },
            (_, existing) =>
            {
                if (!string.IsNullOrWhiteSpace(processPath))
                    existing.ProcessPath = processPath;
                if (isUpload)
                    existing.AddBytesSent(byteCount);
                else
                    existing.AddBytesReceived(byteCount);
                existing.LastActivityAt = DateTime.UtcNow;
                return existing;
            });
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
            if (_counters.TryGetValue(pid, out var counter))
            {
                info.CurrentDownloadBytesPerSecond = Interlocked.Exchange(ref counter.DownloadBytes, 0);
                info.CurrentUploadBytesPerSecond = Interlocked.Exchange(ref counter.UploadBytes, 0);
            }
            totalDown += info.CurrentDownloadBytesPerSecond;
            totalUp += info.CurrentUploadBytesPerSecond;
            processes.Add(info);
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
