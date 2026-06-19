using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface ITrafficMonitor
{
    void RecordBytes(uint processId, string processName, int byteCount, bool isUpload);
    ProcessTrafficInfo? GetProcessInfo(uint processId);
    IReadOnlyList<ProcessTrafficInfo> GetAllProcesses();
    TrafficSnapshot TakeSnapshot();
    event Action<TrafficSnapshot>? OnSnapshot;
}
