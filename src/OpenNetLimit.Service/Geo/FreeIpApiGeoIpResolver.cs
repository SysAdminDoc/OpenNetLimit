using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Geo;

public sealed class FreeIpApiGeoIpResolver : IGeoIpResolver, IDisposable
{
    private readonly GeoIpOptions _options;
    private readonly ILogger<FreeIpApiGeoIpResolver> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, GeoIpInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FreeIpApiGeoIpResolver(GeoIpOptions options, ILogger<FreeIpApiGeoIpResolver> logger)
        : this(options, logger, null)
    {
    }

    public FreeIpApiGeoIpResolver(GeoIpOptions options, ILogger<FreeIpApiGeoIpResolver> logger, HttpClient? httpClient)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { BaseAddress = options.Endpoint };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<GeoIpInfo> ResolveAsync(IPAddress ipAddress, CancellationToken ct = default)
    {
        var ip = Normalize(ipAddress);

        if (!_options.Enabled)
            return new GeoIpInfo { IpAddress = ip, Status = GeoIpStatus.Disabled, Error = "GeoIP lookup is disabled" };

        if (IsNonPublicAddress(ipAddress))
            return new GeoIpInfo { IpAddress = ip, Status = GeoIpStatus.PrivateAddress, Error = "Non-public address" };

        if (_cache.TryGetValue(ip, out var cached) && DateTime.UtcNow - cached.CheckedAt < _options.CacheDuration)
        {
            var copy = Copy(cached);
            copy.FromCache = true;
            return copy;
        }

        try
        {
            using var response = await _httpClient.GetAsync(Uri.EscapeDataString(ip), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Error(ip, $"GeoIP provider returned {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = Parse(ip, json);
            _cache[ip] = Copy(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GeoIP lookup failed for {IpAddress}", ip);
            return Error(ip, ex.Message);
        }
    }

    public GeoIpInfo? GetCached(IPAddress ipAddress)
    {
        var ip = Normalize(ipAddress);
        return _cache.TryGetValue(ip, out var cached) ? Copy(cached) : null;
    }

    public IReadOnlyList<GeoIpInfo> GetCachedResults() =>
        _cache.Values.Select(Copy).OrderByDescending(g => g.CheckedAt).ToList();

    internal static bool IsNonPublicAddress(IPAddress ipAddress)
    {
        if (IPAddress.IsLoopback(ipAddress))
            return true;

        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            return ipAddress.IsIPv6LinkLocal
                || ipAddress.IsIPv6Multicast
                || ipAddress.IsIPv6SiteLocal
                || ipAddress.Equals(IPAddress.IPv6None)
                || ipAddress.Equals(IPAddress.IPv6Any)
                || ipAddress.GetAddressBytes()[0] == 0xfd
                || ipAddress.GetAddressBytes()[0] == 0xfc;

        var bytes = ipAddress.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] >= 224
            || ipAddress.Equals(IPAddress.Any);
    }

    private static GeoIpInfo Parse(string ip, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var success = !root.TryGetProperty("success", out var successElement)
            || successElement.ValueKind != JsonValueKind.False;

        if (!success)
            return Error(ip, GetString(root, "message") ?? "GeoIP lookup failed");

        return new GeoIpInfo
        {
            IpAddress = ip,
            Status = GeoIpStatus.Located,
            CountryName = GetString(root, "countryName", "country"),
            CountryCode = GetString(root, "countryCode"),
            RegionName = GetString(root, "regionName", "region"),
            CityName = GetString(root, "cityName", "city"),
            Latitude = GetDouble(root, "latitude", "lat"),
            Longitude = GetDouble(root, "longitude", "lon"),
            TimeZone = GetString(root, "timeZone", "timezone"),
            Asn = GetString(root, "asn"),
            Organization = GetString(root, "asnOrganization", "org", "isp")
        };
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value))
                return value;
        }

        return null;
    }

    private static GeoIpInfo Error(string ip, string message) =>
        new()
        {
            IpAddress = ip,
            Status = GeoIpStatus.Error,
            Error = message
        };

    private static string Normalize(IPAddress ipAddress) =>
        ipAddress.MapToIPv6().IsIPv4MappedToIPv6
            ? ipAddress.MapToIPv4().ToString()
            : ipAddress.ToString();

    private static GeoIpInfo Copy(GeoIpInfo source) =>
        new()
        {
            IpAddress = source.IpAddress,
            Status = source.Status,
            CountryName = source.CountryName,
            CountryCode = source.CountryCode,
            RegionName = source.RegionName,
            CityName = source.CityName,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            TimeZone = source.TimeZone,
            Asn = source.Asn,
            Organization = source.Organization,
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
