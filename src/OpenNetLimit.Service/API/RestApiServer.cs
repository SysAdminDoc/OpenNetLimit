using System.Net;
using System.Text;

namespace OpenNetLimit.Service.API;

public sealed class RestApiServer : BackgroundService
{
    private const long MaxBodyBytes = 1024 * 1024;
    private readonly RestApiRouter _router;
    private readonly RestApiOptions _options;
    private readonly ILogger<RestApiServer> _logger;

    public RestApiServer(RestApiRouter router, RestApiOptions options, ILogger<RestApiServer> logger)
    {
        _router = router;
        _options = options;
        _logger = logger;
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
            var body = await ReadBodyAsync(context.Request, ct);
            var request = new RestApiRequest(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? "/",
                context.Request.Url?.Query ?? string.Empty,
                body,
                IsLoopback(context),
                GetApiKey(context.Request));

            result = _router.Handle(request);
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
}
