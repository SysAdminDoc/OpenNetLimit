using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface ITrafficMonitor
{
    void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null);
    ProcessTrafficInfo? GetProcessInfo(uint processId);
    IReadOnlyList<ProcessTrafficInfo> GetAllProcesses();
    TrafficSnapshot TakeSnapshot();

    /// <summary>The bytes each process moved since the previous call, for the usage history.</summary>
    TrafficSnapshot TakeUsage();
    event Action<TrafficSnapshot>? OnSnapshot;
}
