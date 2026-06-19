namespace OpenNetLimit.Core.Interfaces;

public interface IRateLimiter
{
    bool TryConsume(uint processId, int byteCount, bool isUpload);
    TimeSpan GetDelay(uint processId, int byteCount, bool isUpload);
    void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond);
    void RemoveLimit(uint processId);
    void RemoveAll();
    bool HasLimit(uint processId);
}
