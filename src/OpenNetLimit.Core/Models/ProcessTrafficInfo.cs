namespace OpenNetLimit.Core.Models;

public class ProcessTrafficInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }

    public long CurrentDownloadBytesPerSecond { get; set; }
    public long CurrentUploadBytesPerSecond { get; set; }

    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }

    public long? DownloadLimitBytesPerSecond { get; set; }
    public long? UploadLimitBytesPerSecond { get; set; }

    public int ActiveConnectionCount { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}
