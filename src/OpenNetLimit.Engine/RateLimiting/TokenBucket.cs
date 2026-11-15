namespace OpenNetLimit.Engine.RateLimiting;

public class TokenBucket
{
    private readonly object _lock = new();
    private readonly Func<long> _clockMs;
    private double _tokens;
    private long _lastRefillTicks;

    public long CapacityBytes { get; }
    public long RefillBytesPerSecond { get; }

    public TokenBucket(long bytesPerSecond) : this(bytesPerSecond, () => Environment.TickCount64)
    {
    }

    /// <param name="clockMs">Milliseconds on a monotonic clock; tests pass their own.</param>
    internal TokenBucket(long bytesPerSecond, Func<long> clockMs)
    {
        _clockMs = clockMs;
        RefillBytesPerSecond = bytesPerSecond;
        CapacityBytes = Math.Max(bytesPerSecond, 65536);
        _tokens = CapacityBytes;
        _lastRefillTicks = clockMs();
    }

    /// <summary>
    /// Charges a packet and says how long it must wait so traffic leaves at the refill rate.
    /// The balance can go below zero: that debt is what the packets after it wait behind, so a
    /// burst is spread out instead of every packet waiting for the same single packet's worth
    /// of tokens. A packet that would wait longer than <paramref name="maxDelay"/> is not
    /// charged and returns false; the caller drops it and TCP slows down.
    /// </summary>
    public bool TryReserve(int byteCount, TimeSpan maxDelay, out TimeSpan delay)
    {
        lock (_lock)
        {
            Refill();
            double balance = _tokens - byteCount;
            delay = balance >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-balance / RefillBytesPerSecond);
            if (delay > maxDelay)
                return false;
            _tokens = balance;
            return true;
        }
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
        long now = _clockMs();
        long elapsed = now - _lastRefillTicks;
        if (elapsed <= 0) return;

        double tokensToAdd = (elapsed / 1000.0) * RefillBytesPerSecond;
        _tokens = Math.Min(_tokens + tokensToAdd, CapacityBytes);
        _lastRefillTicks = now;
    }
}
