using System.Net;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Geo;

public interface IGeoIpResolver
{
    Task<GeoIpInfo> ResolveAsync(IPAddress ipAddress, CancellationToken ct = default);
    GeoIpInfo? GetCached(IPAddress ipAddress);
    IReadOnlyList<GeoIpInfo> GetCachedResults();
}
