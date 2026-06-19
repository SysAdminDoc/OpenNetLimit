namespace OpenNetLimit.Core.Models;

public class ProcessTrafficInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }

    public long CurrentDownloadBytesPerSecond { get; set; }
    public long CurrentUploadBytesPerSecond { get; set; }

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
