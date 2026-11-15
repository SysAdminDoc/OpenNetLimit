using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>How the whole-PC limit reads on the Realtime tab.</summary>
internal static class PcLimitText
{
    /// <summary>The rates it caps, or "Off".</summary>
    public static string Rates(WholePcLimit limit)
    {
        if (limit.IsOff)
            return LocalizationManager.Text("PcLimit_Off");
        var parts = new List<string>(2);
        if (limit.DownloadBytesPerSecond > 0)
            parts.Add($"↓ {ProcessViewModel.FormatBytes(limit.DownloadBytesPerSecond)}");
        if (limit.UploadBytesPerSecond > 0)
            parts.Add($"↑ {ProcessViewModel.FormatBytes(limit.UploadBytesPerSecond)}");
        return string.Join("  ", parts);
    }

    /// <summary>Which traffic it counts.</summary>
    public static string Scope(WholePcLimit limit) => LocalizationManager.Text(
        limit.IsOff ? "PcLimit_OffHint"
        : limit.IncludeLan ? "PcLimit_WithLan"
        : "PcLimit_InternetOnly");

    /// <summary>A rate in the dialog's KB/s. Anything above 0 shows as at least 1.</summary>
    public static long Kilobytes(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? 0 : Math.Max(1, bytesPerSecond / 1024);
}
