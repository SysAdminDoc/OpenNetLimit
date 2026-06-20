using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Security;

public sealed class VirusTotalVerifier : IProcessVerifier, IDisposable
{
    private readonly VirusTotalOptions _options;
    private readonly ILogger<VirusTotalVerifier> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, ProcessVerificationInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public VirusTotalVerifier(VirusTotalOptions options, ILogger<VirusTotalVerifier> logger)
        : this(options, logger, null)
    {
    }

    public VirusTotalVerifier(VirusTotalOptions options, ILogger<VirusTotalVerifier> logger, HttpClient? httpClient)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri("https://www.virustotal.com/api/v3/") };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<ProcessVerificationInfo> VerifyFileAsync(string processPath, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            return new ProcessVerificationInfo
            {
                ProcessPath = processPath,
                Status = ProcessVerificationStatus.NotConfigured,
                Error = "OPENNETLIMIT_VIRUSTOTAL_API_KEY is not configured"
            };
        }

        if (string.IsNullOrWhiteSpace(processPath))
            return Error(processPath, ProcessVerificationStatus.FileNotFound, "process path is required");

        if (!File.Exists(processPath))
            return Error(processPath, ProcessVerificationStatus.FileNotFound, "file not found");

        string sha256;
        try
        {
            sha256 = await ComputeSha256Async(processPath, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error(processPath, ProcessVerificationStatus.AccessDenied, ex.Message);
        }
        catch (IOException ex)
        {
            return Error(processPath, ProcessVerificationStatus.Error, ex.Message);
        }

        if (_cache.TryGetValue(sha256, out var cached) &&
            DateTime.UtcNow - cached.CheckedAt < _options.CacheDuration)
        {
            var copy = Copy(cached);
            copy.FromCache = true;
            copy.ProcessPath = processPath;
            return copy;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{sha256}");
            request.Headers.Add("x-apikey", _options.ApiKey);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var unknown = new ProcessVerificationInfo
                {
                    ProcessPath = processPath,
                    Sha256 = sha256,
                    Status = ProcessVerificationStatus.Unknown,
                    Summary = "No VirusTotal report for this hash",
                    Permalink = BuildPermalink(sha256)
                };
                _cache[sha256] = Copy(unknown);
                return unknown;
            }

            if ((int)response.StatusCode == 429)
                return Error(processPath, ProcessVerificationStatus.Error, "VirusTotal rate limit exceeded", sha256);

            if (!response.IsSuccessStatusCode)
                return Error(processPath, ProcessVerificationStatus.Error, $"VirusTotal returned {(int)response.StatusCode}", sha256);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseReport(processPath, sha256, json);
            _cache[sha256] = Copy(parsed);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VirusTotal verification failed for {ProcessPath}", processPath);
            return Error(processPath, ProcessVerificationStatus.Error, ex.Message, sha256);
        }
    }

    public IReadOnlyList<ProcessVerificationInfo> GetCachedResults() =>
        _cache.Values.Select(Copy).OrderByDescending(v => v.CheckedAt).ToList();

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 64,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ProcessVerificationInfo ParseReport(string processPath, string sha256, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
        var stats = attributes.GetProperty("last_analysis_stats");

        var result = new ProcessVerificationInfo
        {
            ProcessPath = processPath,
            Sha256 = sha256,
            Harmless = GetInt(stats, "harmless"),
            Malicious = GetInt(stats, "malicious"),
            Suspicious = GetInt(stats, "suspicious"),
            Undetected = GetInt(stats, "undetected"),
            Timeout = GetInt(stats, "timeout"),
            Permalink = BuildPermalink(sha256)
        };

        result.Status = result.Malicious > 0
            ? ProcessVerificationStatus.Malicious
            : result.Suspicious > 0
                ? ProcessVerificationStatus.Suspicious
                : result.Harmless > 0 || result.Undetected > 0
                    ? ProcessVerificationStatus.Clean
                    : ProcessVerificationStatus.Unknown;

        result.Summary = $"{result.Malicious} malicious, {result.Suspicious} suspicious, {result.Harmless} harmless";
        return result;
    }

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static ProcessVerificationInfo Error(
        string? processPath,
        ProcessVerificationStatus status,
        string message,
        string? sha256 = null) =>
        new()
        {
            ProcessPath = processPath,
            Sha256 = sha256,
            Status = status,
            Error = message,
            Permalink = sha256 is null ? null : BuildPermalink(sha256)
        };

    private static string BuildPermalink(string sha256) =>
        $"https://www.virustotal.com/gui/file/{sha256}";

    private static ProcessVerificationInfo Copy(ProcessVerificationInfo source) =>
        new()
        {
            Source = source.Source,
            ProcessPath = source.ProcessPath,
            Sha256 = source.Sha256,
            Status = source.Status,
            Harmless = source.Harmless,
            Malicious = source.Malicious,
            Suspicious = source.Suspicious,
            Undetected = source.Undetected,
            Timeout = source.Timeout,
            Summary = source.Summary,
            Permalink = source.Permalink,
            Error = source.Error,
            CheckedAt = source.CheckedAt,
            FromCache = source.FromCache
        };

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
