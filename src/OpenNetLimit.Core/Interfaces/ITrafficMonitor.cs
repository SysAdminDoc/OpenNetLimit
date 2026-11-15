using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface ITrafficMonitor
{
    /// <param name="namedAt">
    /// When <paramref name="processName"/> was read for the ID, when that was before now (a name
    /// read with the socket tables, say). The process may have exited since and left the ID to another.
    /// </param>
    void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null,
        DateTime? namedAt = null);
    ProcessTrafficInfo? GetProcessInfo(uint processId);
    IReadOnlyList<ProcessTrafficInfo> GetAllProcesses();

    /// <summary>
    /// What every process moved since the service started, for counting quotas. Unlike
    /// <see cref="GetAllProcesses"/> it keeps processes that exited, under their name, path and services.
    /// </summary>
    IReadOnlyList<ProcessTrafficInfo> GetUsageTotals() => GetAllProcesses();

    TrafficSnapshot TakeSnapshot();

    /// <summary>The bytes each process moved since the previous call, for the usage history.</summary>
    TrafficSnapshot TakeUsage();
    event Action<TrafficSnapshot>? OnSnapshot;
}
