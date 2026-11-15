using System.Text.Json.Serialization;

namespace OpenNetLimit.Core.Models;

public class ProcessTrafficInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }
    /// <summary>The first of <see cref="Services"/>, kept for clients that read one name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The Windows services running in the process, null when there are none or they aren't known yet.</summary>
    public string[]? Services { get; set; }

    /// <summary>
    /// Windows' own: no rule blocks or slows it. <see cref="NetworkingServices"/> says which of
    /// its services keep it that way, and is empty for a process like lsass that always is.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>The services in it that Windows networking needs (DNS, DHCP and the like), which no rule slows.</summary>
    public string[]? NetworkingServices { get; set; }

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

    private long _namedSinceTicks = long.MaxValue;

    /// <summary>
    /// When the oldest name behind its bytes was read, null until bytes come with one. The ID
    /// may have been another process's then, so the one that has it now must have started first.
    /// </summary>
    [JsonIgnore]
    public DateTime? NamedSince
    {
        get
        {
            long ticks = Volatile.Read(ref _namedSinceTicks);
            return ticks == long.MaxValue ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Notes bytes that came with a name read at <paramref name="namedAt"/>.</summary>
    public void NamedBy(DateTime namedAt)
    {
        long ticks = namedAt.Ticks;
        long seen = Volatile.Read(ref _namedSinceTicks);
        while (ticks < seen)
        {
            long prior = Interlocked.CompareExchange(ref _namedSinceTicks, ticks, seen);
            if (prior == seen)
                return;
            seen = prior;
        }
    }
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}
