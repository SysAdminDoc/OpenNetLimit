using System.Threading.RateLimiting;

namespace OpenNetLimit.Service.API;

/// <summary>
/// How fast one client may call the REST API. Its requests share a bucket big enough for a script
/// that adds rules one at a time, and a client that runs the bucket dry waits for its turn instead
/// of being refused. Wrong keys have a small budget of their own that comes back slowly, so a key
/// can't be guessed at speed: once it's spent, the client's requests that carry a key are refused.
/// The bucket fills only through <see cref="RefillRequests"/>, which the server runs for every
/// client from one timer.
/// </summary>
internal sealed class RestClientLimits : IDisposable
{
    internal const int RequestBurst = 100;
    internal const int RequestsPerSecond = 50;
    internal const int RequestsWaiting = 100;
    internal const int WrongKeyBurst = 10;

    /// <summary>How often the server refills every client's requests.</summary>
    internal static readonly TimeSpan RefillPeriod = TimeSpan.FromMilliseconds(100);

    /// <summary>One wrong key comes back to the budget this often.</summary>
    internal static readonly TimeSpan WrongKeyRefill = TimeSpan.FromSeconds(6);

    private readonly TokenBucketRateLimiter _requests;
    private readonly Func<long> _tickMs;
    private readonly object _lock = new();
    private double _wrongKeysLeft = WrongKeyBurst;
    private long _wrongKeysAtMs;

    public RestClientLimits() : this(() => Environment.TickCount64)
    {
    }

    internal RestClientLimits(Func<long> tickMs)
    {
        _requests = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = RequestBurst,
            // A refill's worth at a time, so a client that ran dry waits about as long as its
            // request costs.
            TokensPerPeriod = (int)(RequestsPerSecond * RefillPeriod.TotalSeconds),
            ReplenishmentPeriod = RefillPeriod,
            QueueLimit = RequestsWaiting,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = false
        });
        _tickMs = tickMs;
        _wrongKeysAtMs = tickMs();
    }

    /// <summary>When the client last sent a request, on the server's clock.</summary>
    public long LastRequestMs { get; set; }

    /// <summary>
    /// Waits for the client's turn. Null when too many of its requests are already waiting.
    /// </summary>
    public async ValueTask<RateLimitLease?> WaitTurnAsync(CancellationToken ct)
    {
        var lease = await _requests.AcquireAsync(1, ct);
        if (lease.IsAcquired)
            return lease;
        lease.Dispose();
        return null;
    }

    /// <summary>Whether a request that carries a key may have it checked.</summary>
    public bool CanTryAKey
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _wrongKeysLeft >= 1;
            }
        }
    }

    /// <summary>
    /// Takes a try for a wrong key, false when none is left. Taken as the key is found wrong, before
    /// the request waits or sends anything more, so guesses sent all at once or held open get no
    /// more tries than guesses sent one at a time.
    /// </summary>
    public bool TrySpendWrongKey()
    {
        lock (_lock)
        {
            Refill();
            if (_wrongKeysLeft < 1)
                return false;
            _wrongKeysLeft--;
            return true;
        }
    }

    /// <summary>How long until a try comes back, zero when one is left.</summary>
    public TimeSpan UntilNextTry
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _wrongKeysLeft >= 1 ? TimeSpan.Zero : (1 - _wrongKeysLeft) * WrongKeyRefill;
            }
        }
    }

    /// <summary>
    /// Adds the requests the time since the last refill is worth, and lets waiting ones through.
    /// A refill right after another adds nothing.
    /// </summary>
    public void RefillRequests() => _requests.TryReplenish();

    private void Refill()
    {
        long now = _tickMs();
        if (now <= _wrongKeysAtMs)
            return;
        _wrongKeysLeft = Math.Min(WrongKeyBurst,
            _wrongKeysLeft + (now - _wrongKeysAtMs) / WrongKeyRefill.TotalMilliseconds);
        _wrongKeysAtMs = now;
    }

    public void Dispose() => _requests.Dispose();
}
