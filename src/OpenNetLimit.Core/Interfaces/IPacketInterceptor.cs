namespace OpenNetLimit.Core.Interfaces;

public interface IPacketInterceptor : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    bool IsRunning { get; }
    long TotalBlocked { get; }
    long TotalDelayed { get; }
    long TotalDropped { get; }
    long TotalSent { get; }
    IReadOnlyList<object> GetRecentConnectionLog(int maxCount);
}
