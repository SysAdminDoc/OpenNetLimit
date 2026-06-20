using OpenNetLimit.Core;

namespace OpenNetLimit.Service.API;

public sealed class RestApiOptions
{
    public const string DefaultUrl = "http://127.0.0.1:47719/";

    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Urls { get; init; } = [DefaultUrl];
    public bool RemoteEnabled { get; init; }
    public string? ApiKey { get; init; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static RestApiOptions FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENNETLIMIT_API_KEY")
                     ?? ProtectedKeyStore.LoadKey();

        return Create(
            Environment.GetEnvironmentVariable("OPENNETLIMIT_API_URLS"),
            apiKey,
            EnvHelper.IsEnabled(Environment.GetEnvironmentVariable("OPENNETLIMIT_ENABLE_REMOTE_API")),
            EnvHelper.IsEnabled(Environment.GetEnvironmentVariable("OPENNETLIMIT_API_DISABLED")));
    }

    public static RestApiOptions Create(string? rawUrls, string? apiKey, bool remoteRequested, bool disabled)
    {
        var remoteEnabled = remoteRequested && !string.IsNullOrWhiteSpace(apiKey);

        var urls = ParseUrls(rawUrls);
        if (!remoteEnabled)
            urls = urls.Where(IsLoopbackPrefix).ToArray();
        if (urls.Length == 0)
            urls = [DefaultUrl];

        return new RestApiOptions
        {
            Enabled = !disabled,
            Urls = urls,
            RemoteEnabled = remoteEnabled,
            ApiKey = apiKey
        };
    }

    internal static bool IsLoopbackPrefix(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ParseUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [DefaultUrl];

        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EnsureTrailingSlash)
            .Where(IsSupportedPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSupportedPrefix(string url) =>
        IsStandardPrefix(url) || IsWildcardHttpListenerPrefix(url);

    private static bool IsStandardPrefix(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsWildcardHttpListenerPrefix(string url)
    {
        var normalized = url
            .Replace("://+:", "://localhost:", StringComparison.Ordinal)
            .Replace("://*:", "://localhost:", StringComparison.Ordinal);

        return !normalized.Equals(url, StringComparison.Ordinal)
            && IsStandardPrefix(normalized);
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";

}
