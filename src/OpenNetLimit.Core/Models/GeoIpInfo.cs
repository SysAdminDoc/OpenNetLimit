namespace OpenNetLimit.Core.Models;

public enum GeoIpStatus
{
    Disabled,
    PrivateAddress,
    Unknown,
    Located,
    Error
}

public class GeoIpInfo
{
    public string? IpAddress { get; set; }
    public GeoIpStatus Status { get; set; } = GeoIpStatus.Unknown;
    public string? CountryName { get; set; }
    public string? CountryCode { get; set; }
    public string? RegionName { get; set; }
    public string? CityName { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? TimeZone { get; set; }
    public string? Asn { get; set; }
    public string? Organization { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public bool FromCache { get; set; }
}
