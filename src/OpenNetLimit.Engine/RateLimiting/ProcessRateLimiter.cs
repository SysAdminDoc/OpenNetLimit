using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.RateLimiting;

public class ProcessRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<uint, ProcessBuckets> _buckets = new();
    private readonly Func<long> _clockMs;
    private volatile MachineBuckets? _machine;

    public ProcessRateLimiter() : this(() => Environment.TickCount64)
    {
    }

    /// <param name="clockMs">Milliseconds on a monotonic clock; tests pass their own.</param>
    internal ProcessRateLimiter(Func<long> clockMs)
    {
        _clockMs = clockMs;
    }

    public WholePcLimit? PcLimit => _machine?.Limit;

    public void SetPcLimit(WholePcLimit? limit)
    {
        if (limit is null || limit.IsOff)
        {
            _machine = null;
            return;
        }
        // A direction whose rate didn't change keeps its bucket, and with it the debt that
        // queued packets wait behind, so changing the other direction lets no burst out.
        var current = _machine;
        _machine = new MachineBuckets(limit,
            Keep(current?.Download, limit.DownloadBytesPerSecond),
            Keep(current?.Upload, limit.UploadBytesPerSecond));
    }

    private TokenBucket? Keep(TokenBucket? bucket, long bytesPerSecond) =>
        bytesPerSecond <= 0 ? null
        : bucket?.RefillBytesPerSecond == bytesPerSecond ? bucket
        : new TokenBucket(bytesPerSecond, _clockMs);

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

    public bool TryReserve(uint processId, int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay) =>
        TryReserve(processId, byteCount, isUpload, wholePc: false, maxDelay, out delay);

    public bool TryReserve(uint processId, int byteCount, bool isUpload, bool wholePc, TimeSpan maxDelay, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        TokenBucket? app = null;
        if (_buckets.TryGetValue(processId, out var buckets))
        {
            if (buckets.Blocked)
                return false;
            app = isUpload ? buckets.Upload : buckets.Download;
        }

        if (app is not null && !app.TryReserve(byteCount, maxDelay, out delay))
            return false;
        var machine = wholePc ? _machine : null;
        var shared = isUpload ? machine?.Upload : machine?.Download;
        if (shared is null)
            return true;
        if (!shared.TryReserve(byteCount, maxDelay, out var sharedDelay))
        {
            // Dropped after all, so the app's limit gets its share back.
            app?.Refund(byteCount);
            delay = TimeSpan.Zero;
            return false;
        }
        // It leaves once both limits have room for it.
        if (sharedDelay > delay)
            delay = sharedDelay;
        return true;
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
            _ => ForRates(download, upload),
            (_, current) => current.Has(download, upload) ? current : ForRates(download, upload));
    }

    private ProcessBuckets ForRates(long download, long upload) => new(
        download > 0 ? new TokenBucket(download, _clockMs) : null,
        upload > 0 ? new TokenBucket(upload, _clockMs) : null);

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

    /// <summary>Removes every app's limit. The whole-PC limit stays.</summary>
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

        public bool Has(long download, long upload) =>
            !Blocked
            && (Download?.RefillBytesPerSecond ?? 0) == download
            && (Upload?.RefillBytesPerSecond ?? 0) == upload;
    }

    private sealed record MachineBuckets(WholePcLimit Limit, TokenBucket? Download, TokenBucket? Upload);
}
