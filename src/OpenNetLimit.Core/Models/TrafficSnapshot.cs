namespace OpenNetLimit.Core.Models;

public class TrafficSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<ProcessTrafficInfo> Processes { get; init; } = [];
    public long TotalDownloadBytesPerSecond { get; init; }
    public long TotalUploadBytesPerSecond { get; init; }
}
