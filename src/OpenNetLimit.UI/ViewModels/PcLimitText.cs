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

    /// <summary>
    /// The rate in bytes for a KB/s value from the dialog. A value left as it showed keeps the
    /// exact rate it came from, so OK on a limit set in bytes elsewhere (1,000,000 B/s shows as
    /// 976 KB/s) doesn't round it down or start its bucket over.
    /// </summary>
    public static long Bytes(long kilobytes, long current) =>
        kilobytes == Kilobytes(current) ? current : kilobytes * 1024;
}
