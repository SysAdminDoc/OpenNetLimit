namespace OpenNetLimit.Engine.RateLimiting;

public class TokenBucket
{
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefillTicks;

    public long CapacityBytes { get; }
    public long RefillBytesPerSecond { get; }

    public TokenBucket(long bytesPerSecond)
    {
        RefillBytesPerSecond = bytesPerSecond;
        CapacityBytes = Math.Max(bytesPerSecond, 65536);
        _tokens = CapacityBytes;
        _lastRefillTicks = Environment.TickCount64;
    }

    public bool TryConsume(int byteCount)
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= byteCount)
            {
                _tokens -= byteCount;
                return true;
            }
            return false;
        }
    }

    public TimeSpan GetDelay(int byteCount)
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= byteCount)
                return TimeSpan.Zero;

            double deficit = byteCount - _tokens;
            double seconds = deficit / RefillBytesPerSecond;
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public double AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _tokens;
            }
        }
    }

    private void Refill()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastRefillTicks;
        if (elapsed <= 0) return;

        double tokensToAdd = (elapsed / 1000.0) * RefillBytesPerSecond;
        _tokens = Math.Min(_tokens + tokensToAdd, CapacityBytes);
        _lastRefillTicks = now;
    }
}
