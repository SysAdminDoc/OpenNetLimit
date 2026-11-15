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
        if (buckets.Blocked)
            return false;

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

    public bool TryReserve(uint processId, int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!_buckets.TryGetValue(processId, out var buckets))
            return true;
        if (buckets.Blocked)
            return false;

        var bucket = isUpload ? buckets.Upload : buckets.Download;
        return bucket?.TryReserve(byteCount, maxDelay, out delay) ?? true;
    }

    public bool TryGetLimit(uint processId, out long downloadBytesPerSecond, out long uploadBytesPerSecond)
    {
        downloadBytesPerSecond = uploadBytesPerSecond = 0;
        if (!_buckets.TryGetValue(processId, out var buckets))
            return false;

        downloadBytesPerSecond = buckets.Download?.RefillBytesPerSecond ?? 0;
        uploadBytesPerSecond = buckets.Upload?.RefillBytesPerSecond ?? 0;
        return true;
    }

    public void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond)
    {
        long download = Math.Max(downloadBytesPerSecond, 0);
        long upload = Math.Max(uploadBytesPerSecond, 0);
        // Setting the rate a process already has keeps its buckets. A new bucket starts full and
        // forgets the debt queued packets wait behind, so each reconcile would let a burst out.
        _buckets.AddOrUpdate(processId,
            _ => ProcessBuckets.For(download, upload),
            (_, current) => current.Has(download, upload) ? current : ProcessBuckets.For(download, upload));
    }

    public void Block(uint processId)
    {
        _buckets[processId] = ProcessBuckets.BlockAll;
    }

    public bool IsBlocked(uint processId)
    {
        return _buckets.TryGetValue(processId, out var buckets) && buckets.Blocked;
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

    private sealed record ProcessBuckets(TokenBucket? Download, TokenBucket? Upload, bool Blocked = false)
    {
        public static readonly ProcessBuckets BlockAll = new(null, null, Blocked: true);

        public static ProcessBuckets For(long download, long upload) => new(
            download > 0 ? new TokenBucket(download) : null,
            upload > 0 ? new TokenBucket(upload) : null);

        public bool Has(long download, long upload) =>
            !Blocked
            && (Download?.RefillBytesPerSecond ?? 0) == download
            && (Upload?.RefillBytesPerSecond ?? 0) == upload;
    }
}
