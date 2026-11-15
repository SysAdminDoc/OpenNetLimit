using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface ITrafficMonitor
{
    void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null);
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
