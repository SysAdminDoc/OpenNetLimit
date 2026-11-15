using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenNetLimit.Service.API;

public sealed class RestApiServer : BackgroundService
{
    private const long MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// A client quiet this long is forgotten. Its wrong-key budget has filled back up long before,
    /// so forgetting it gives back nothing it hadn't earned.
    /// </summary>
    internal static readonly TimeSpan IdleClient = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly RestApiRouter _router;
    private readonly RestApiOptions _options;
    private readonly ILogger<RestApiServer> _logger;
    private readonly Func<RestClientLimits> _newClientLimits;
    private readonly Func<long> _tickMs;
    private readonly TimeSpan? _refillEvery;
    private readonly object _clientsLock = new();
    private readonly Dictionary<string, RestClientLimits> _clients = [];
    private Timer? _refillTimer;
    private long _lastPruneMs;
    private bool _disposed;

    public RestApiServer(RestApiRouter router, RestApiOptions options, ILogger<RestApiServer> logger)
        : this(router, options, logger, () => new RestClientLimits(), () => Environment.TickCount64,
            RestClientLimits.RefillPeriod)
    {
    }

    /// <param name="refillEvery">How often a timer refills every client, null for none (tests call <see cref="RefillAll"/>).</param>
    internal RestApiServer(RestApiRouter router, RestApiOptions options, ILogger<RestApiServer> logger,
        Func<RestClientLimits> newClientLimits, Func<long> tickMs, TimeSpan? refillEvery)
    {
        _router = router;
        _options = options;
        _logger = logger;
        _newClientLimits = newClientLimits;
        _tickMs = tickMs;
        _refillEvery = refillEvery;
        _lastPruneMs = tickMs();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("REST API disabled by OPENNETLIMIT_API_DISABLED");
            return;
        }

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
            var request = context.Request;
            var head = new RestApiRequest(
                request.HttpMethod,
                request.Url?.AbsolutePath ?? "/",
                request.Url?.Query ?? string.Empty,
                string.Empty,
                IsLoopback(context),
                GetApiKey(request));
            result = await HandleAsync(ClientKey(request.RemoteEndPoint?.Address), head,
                () => ReadBodyAsync(request, ct), ct);
        }
        catch (BodyTooLargeException)
        {
            result = RestApiResponse.Error(413, "request body too large", RestApiRouter.JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The service is stopping with this request still waiting. Let the caller go now
            // rather than leave it hanging until the listener closes.
            context.Response.Abort();
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "REST API request failed");
            result = RestApiResponse.Error(500, "internal server error", RestApiRouter.JsonOptions);
        }

        await WriteResponseAsync(context.Response, result, ct);
    }

    /// <summary>
    /// Answers one request from <paramref name="client"/> within its limits. The key is checked from
    /// the headers first, so a wrong one costs its try before the request waits or sends anything
    /// more. The body is read last, and only for a change, which needs the right key to get there.
    /// </summary>
    internal async Task<RestApiResponse> HandleAsync(string client, RestApiRequest head,
        Func<Task<string>> readBody, CancellationToken ct)
    {
        var limits = LimitsFor(client);
        var refused = _router.Authorize(head);
        if (!string.IsNullOrWhiteSpace(head.ApiKey)
            && !(refused is { StatusCode: 401 } ? limits.TrySpendWrongKey() : limits.CanTryAKey))
        {
            var seconds = (int)Math.Max(1, Math.Ceiling(limits.UntilNextTry.TotalSeconds));
            return RestApiResponse.Error(429,
                $"too many wrong api keys, try again in {seconds} seconds", RestApiRouter.JsonOptions)
                with { RetryAfterSeconds = seconds };
        }

        using var turn = await limits.WaitTurnAsync(ct);
        if (turn is null)
            return RestApiResponse.Error(429, "too many requests waiting, wait for some to finish",
                RestApiRouter.JsonOptions) with { RetryAfterSeconds = 1 };
        if (refused is not null)
            return refused;

        var body = RestApiRouter.CarriesBody(head.Method) ? await readBody() : string.Empty;
        return await _router.HandleAsync(head with { Body = body }, ct);
    }

    /// <summary>
    /// Who a request counts against. Every loopback address is one client, since a program on the
    /// PC can send from 127.0.0.2 as easily as from 127.0.0.1, and an IPv6 address counts by its
    /// /64, which one machine usually has to itself.
    /// </summary>
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return "loopback";
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private RestClientLimits LimitsFor(string client)
    {
        lock (_clientsLock)
        {
            if (!_clients.TryGetValue(client, out var limits))
                _clients[client] = limits = _newClientLimits();
            limits.LastRequestMs = _tickMs();
            if (_refillEvery is { } every && !_disposed)
                _refillTimer ??= new Timer(_ => RefillAll(), null, every, every);
            return limits;
        }
    }

    /// <summary>
    /// Refills every client's requests, and forgets the ones that have been quiet a while. Both
    /// happen under the lock that hands clients out, so a request never gets one being forgotten.
    /// </summary>
    internal void RefillAll()
    {
        RestClientLimits[] clients;
        lock (_clientsLock)
        {
            long now = _tickMs();
            if (now - _lastPruneMs >= PruneEvery.TotalMilliseconds)
            {
                _lastPruneMs = now;
                foreach (var (client, idle) in _clients.Where(c => now - c.Value.LastRequestMs >= IdleClient.TotalMilliseconds).ToList())
                {
                    _clients.Remove(client);
                    idle.Dispose();
                }
            }
            clients = [.. _clients.Values];
        }
        foreach (var limits in clients)
            limits.RefillRequests();
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (!request.HasEntityBody)
            return string.Empty;

        if (request.ContentLength64 > MaxBodyBytes)
            throw new BodyTooLargeException();

        // Read with a hard byte cap to prevent DoS via chunked transfer-encoding
        // bodies that bypass the Content-Length check above
        var buffer = new byte[MaxBodyBytes + 1];
        int totalRead = 0;
        int bytesRead;
        while (totalRead < buffer.Length &&
               (bytesRead = await request.InputStream.ReadAsync(
                   buffer, totalRead, buffer.Length - totalRead, ct)) > 0)
        {
            totalRead += bytesRead;
        }

        if (totalRead > MaxBodyBytes)
            throw new BodyTooLargeException();

        var encoding = request.ContentEncoding ?? Encoding.UTF8;
        return encoding.GetString(buffer, 0, totalRead);
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, RestApiResponse result, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(result.Body);
        response.StatusCode = result.StatusCode;
        response.ContentType = result.ContentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        if (result.RetryAfterSeconds is int retryAfter)
            response.AddHeader("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
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

    public override void Dispose()
    {
        lock (_clientsLock)
        {
            _disposed = true;
            _refillTimer?.Dispose();
            _refillTimer = null;
            foreach (var limits in _clients.Values)
                limits.Dispose();
            _clients.Clear();
        }
        base.Dispose();
    }

    private sealed class BodyTooLargeException : Exception;
}
