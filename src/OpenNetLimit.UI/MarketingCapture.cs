using OpenNetLimit.Core;

namespace OpenNetLimit.UI;

internal static class MarketingCapture
{
    private const string EnabledVariable = "OPENNETLIMIT_CAPTURE_MODE";
    private const string ViewVariable = "OPENNETLIMIT_CAPTURE_VIEW";

    public static bool IsEnabled => EnvHelper.IsEnabled(Environment.GetEnvironmentVariable(EnabledVariable));

    public static string View
    {
        get
        {
            var requested = Environment.GetEnvironmentVariable(ViewVariable)?.Trim().ToLowerInvariant();
            return requested is "setup" or "live" or "live-light" or "history" or "limit"
                ? requested
                : "live";
        }
    }
}
