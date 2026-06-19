using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;

namespace OpenNetLimit.Engine.RateLimiting;

public class ProcessRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<uint, ProcessBuckets> _buckets = new();

    public bool TryConsume(uint processId, int byteCount, bool isUpload)
    {
        if (!_buckets.TryGetValue(processId, out var buckets))
            return true;

        var bucket = isUpload ? buckets.Upload : buckets.Download;
        return bucket?.TryConsume(byteCount) ?? true;
    }

    public TimeSpan GetDelay(uint processId, int byteCount, bool isUpload)
    {
        if (!_buckets.TryGetValue(processId, out var buckets))
            return TimeSpan.Zero;

        var bucket = isUpload ? buckets.Upload : buckets.Download;
        return bucket?.GetDelay(byteCount) ?? TimeSpan.Zero;
    }

    public void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond)
    {
        var buckets = new ProcessBuckets(
            downloadBytesPerSecond > 0 ? new TokenBucket(downloadBytesPerSecond) : null,
            uploadBytesPerSecond > 0 ? new TokenBucket(uploadBytesPerSecond) : null);

        _buckets.AddOrUpdate(processId, buckets, (_, _) => buckets);
    }

    public void RemoveLimit(uint processId)
    {
        _buckets.TryRemove(processId, out _);
    }

    public void RemoveAll()
    {
        _buckets.Clear();
    }

    public bool HasLimit(uint processId)
    {
        return _buckets.ContainsKey(processId);
    }

    private sealed record ProcessBuckets(TokenBucket? Download, TokenBucket? Upload);
}
