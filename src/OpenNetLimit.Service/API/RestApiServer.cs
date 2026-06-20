using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

namespace OpenNetLimit.Service.API;

public sealed class RestApiServer : BackgroundService
{
    private const long MaxBodyBytes = 1024 * 1024;
    private readonly RestApiRouter _router;
    private readonly RestApiOptions _options;
    private readonly ILogger<RestApiServer> _logger;
    private readonly ConcurrentDictionary<string, RateLimiterEntry> _rateLimiters = new();
    private Timer? _rateLimiterCleanupTimer;

    public RestApiServer(RestApiRouter router, RestApiOptions options, ILogger<RestApiServer> logger)
    {
        _router = router;
        _options = options;
        _logger = logger;
    }

    private sealed class RateLimiterEntry
    {
        public RateLimiter Limiter { get; }
        public DateTime LastAccess { get; set; }

        public RateLimiterEntry(RateLimiter limiter)
        {
            Limiter = limiter;
            LastAccess = DateTime.UtcNow;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("REST API disabled by OPENNETLIMIT_API_DISABLED");
            return;
        }

        _rateLimiterCleanupTimer = new Timer(_ => PruneIdleRateLimiters(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        using var listener = new HttpListener();
        foreach (var url in _options.Urls)
            listener.Prefixes.Add(url);

        try
        {
            listener.Start();
            _logger.LogInformation("REST API listening on {Urls}", string.Join(", ", _options.Urls));
            if (_options.RemoteEnabled)
                _logger.LogWarning("Remote REST API is enabled; requests must include X-OpenNetLimit-Key");

            using var stopRegistration = stoppingToken.Register(() =>
            {
                try { listener.Stop(); } catch { }
            });

            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (stoppingToken.IsCancellationRequested
                    && (ex is HttpListenerException or ObjectDisposedException))
                {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context, stoppingToken), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "REST API server failed to start");
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        RestApiResponse result;
        try
        {
            var clientKey = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            var entry = _rateLimiters.GetOrAdd(clientKey, _ => new RateLimiterEntry(
                new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 10,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    TokensPerPeriod = 10,
                    QueueLimit = 0
                })));
            entry.LastAccess = DateTime.UtcNow;

            using var lease = await entry.Limiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                result = RestApiResponse.Error(429, "rate limit exceeded", RestApiRouter.JsonOptions);
                await WriteResponseAsync(context.Response, result, ct);
                return;
            }

            var body = await ReadBodyAsync(context.Request, ct);
            var request = new RestApiRequest(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? "/",
                context.Request.Url?.Query ?? string.Empty,
                body,
                IsLoopback(context),
                GetApiKey(context.Request));

            result = await _router.HandleAsync(request, ct);
        }
        catch (InvalidOperationException ex)
        {
            result = RestApiResponse.Error(413, ex.Message, RestApiRouter.JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "REST API request failed");
            result = RestApiResponse.Error(500, "internal server error", RestApiRouter.JsonOptions);
        }

        await WriteResponseAsync(context.Response, result, ct);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!request.HasEntityBody)
            return string.Empty;

        if (request.ContentLength64 > MaxBodyBytes)
            throw new InvalidOperationException("request body too large");

        using var reader = new StreamReader(
            request.InputStream,
            request.ContentEncoding ?? Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        var body = await reader.ReadToEndAsync(ct);
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            throw new InvalidOperationException("request body too large");
        return body;
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, RestApiResponse result, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(result.Body);
        response.StatusCode = result.StatusCode;
        response.ContentType = result.ContentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
        response.Close();
    }

    private static bool IsLoopback(HttpListenerContext context)
    {
        var address = context.Request.RemoteEndPoint?.Address;
        return address is null || IPAddress.IsLoopback(address);
    }

    private static string? GetApiKey(HttpListenerRequest request)
    {
        var header = request.Headers["X-OpenNetLimit-Key"];
        if (!string.IsNullOrWhiteSpace(header))
            return header;

        var authorization = request.Headers["Authorization"];
        const string bearer = "Bearer ";
        if (authorization?.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) == true)
            return authorization[bearer.Length..];

        return null;
    }

    private void PruneIdleRateLimiters()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var kvp in _rateLimiters)
        {
            if (kvp.Value.LastAccess < cutoff && _rateLimiters.TryRemove(kvp.Key, out var removed))
            {
                removed.Limiter.Dispose();
            }
        }
    }
}
