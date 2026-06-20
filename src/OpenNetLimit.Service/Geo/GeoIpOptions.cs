using OpenNetLimit.Core;

namespace OpenNetLimit.Service.Geo;

public sealed class GeoIpOptions
{
    public bool Enabled { get; init; }
    public Uri Endpoint { get; init; } = new("https://free.freeipapi.com/api/json/");
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromHours(24);

    public static GeoIpOptions FromEnvironment()
    {
        var hoursRaw = Environment.GetEnvironmentVariable("OPENNETLIMIT_GEOIP_CACHE_HOURS");
        var cacheHours = double.TryParse(hoursRaw, out var parsedHours)
            ? Math.Clamp(parsedHours, 0.25, 168)
            : 24;

        var endpointRaw = Environment.GetEnvironmentVariable("OPENNETLIMIT_GEOIP_ENDPOINT");
        var endpoint = Uri.TryCreate(endpointRaw, UriKind.Absolute, out var parsedEndpoint)
            ? parsedEndpoint
            : new Uri("https://free.freeipapi.com/api/json/");

        return new GeoIpOptions
        {
            Enabled = EnvHelper.IsEnabled(Environment.GetEnvironmentVariable("OPENNETLIMIT_GEOIP_ENABLED")),
            Endpoint = endpoint,
            CacheDuration = TimeSpan.FromHours(cacheHours)
        };
    }
}
