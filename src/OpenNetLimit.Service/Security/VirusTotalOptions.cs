namespace OpenNetLimit.Service.Security;

public sealed class VirusTotalOptions
{
    public bool Enabled { get; init; } = true;
    public string? ApiKey { get; init; }
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromHours(12);

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);

    public static VirusTotalOptions FromEnvironment()
    {
        var hoursRaw = Environment.GetEnvironmentVariable("OPENNETLIMIT_VIRUSTOTAL_CACHE_HOURS");
        var cacheHours = double.TryParse(hoursRaw, out var parsedHours)
            ? Math.Clamp(parsedHours, 0.25, 168)
            : 12;

        return new VirusTotalOptions
        {
            Enabled = !IsEnabled(Environment.GetEnvironmentVariable("OPENNETLIMIT_VIRUSTOTAL_DISABLED")),
            ApiKey = Environment.GetEnvironmentVariable("OPENNETLIMIT_VIRUSTOTAL_API_KEY"),
            CacheDuration = TimeSpan.FromHours(cacheHours)
        };
    }

    private static bool IsEnabled(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
