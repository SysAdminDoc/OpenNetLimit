namespace OpenNetLimit.Core.Models;

public class ProcessTrafficInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }
    public string? ServiceName { get; set; }
    public string? AppxPackage { get; set; }

    private long _currentDownloadBytesPerSecond;
    public long CurrentDownloadBytesPerSecond
    {
        get => Volatile.Read(ref _currentDownloadBytesPerSecond);
        set => Volatile.Write(ref _currentDownloadBytesPerSecond, value);
    }

    private long _currentUploadBytesPerSecond;
    public long CurrentUploadBytesPerSecond
    {
        get => Volatile.Read(ref _currentUploadBytesPerSecond);
        set => Volatile.Write(ref _currentUploadBytesPerSecond, value);
    }

    private long _totalBytesReceived;
    public long TotalBytesReceived
    {
        get => Volatile.Read(ref _totalBytesReceived);
        set => Volatile.Write(ref _totalBytesReceived, value);
    }

    private long _totalBytesSent;
    public long TotalBytesSent
    {
        get => Volatile.Read(ref _totalBytesSent);
        set => Volatile.Write(ref _totalBytesSent, value);
    }

    public void AddBytesReceived(long count) => Interlocked.Add(ref _totalBytesReceived, count);
    public void AddBytesSent(long count) => Interlocked.Add(ref _totalBytesSent, count);

    public long? DownloadLimitBytesPerSecond { get; set; }
    public long? UploadLimitBytesPerSecond { get; set; }

    public int ActiveConnectionCount { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}
