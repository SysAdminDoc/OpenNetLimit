namespace OpenNetLimit.Core.Interfaces;

public interface IPacketInterceptor : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    bool IsRunning { get; }
}
