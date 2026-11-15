using OpenNetLimit.Core;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>How a row of the app list names its process and says why rules leave it alone.</summary>
public static class ProcessRowText
{
    /// <summary>The process Windows runs shared services in. Rules reach it only through a service.</summary>
    public static bool IsServiceHost(string? processName) =>
        string.Equals(ProcessNames.Normalize(processName), BandwidthRule.ServiceHostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The row already showing <paramref name="process"/>, or null when it needs its own. Windows
    /// gives an exited process's ID to a later one, which the service counts from its own first
    /// sighting, so a row with the ID but another first sighting or name was the earlier process.
    /// </summary>
    public static ProcessViewModel? RowFor(IReadOnlyDictionary<uint, ProcessViewModel> rows, ProcessTrafficInfo process) =>
        rows.TryGetValue(process.ProcessId, out var row) && row.FirstSeenAt == process.FirstSeenAt
            && string.Equals(row.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase)
            ? row
            : null;

    /// <summary>The name, followed by the services a svchost runs, since every svchost looks the same otherwise.</summary>
    public static string DisplayName(string processName, IReadOnlyList<string>? services) =>
        IsServiceHost(processName) && services is { Count: > 0 }
            ? $"{processName} ({string.Join(", ", services)})"
            : processName;

    /// <summary>Why no rule slows the process, or null when rules apply to it.</summary>
    public static string? ProtectedReason(ProcessTrafficInfo process)
    {
        if (!process.IsProtected)
            return null;
        if (process.NetworkingServices is { Length: > 0 } networking)
            return LocalizationManager.Format("Process_ProtectedNetworking", string.Join(", ", networking));
        if (IsServiceHost(process.ProcessName) && process.Services is not { Length: > 0 })
            return LocalizationManager.Text("Process_ProtectedUnknown");
        return LocalizationManager.Text("Process_ProtectedWindows");
    }

    /// <summary>The Limit column: the limit in force, or that rules leave the process alone.</summary>
    public static string LimitText(ProcessTrafficInfo process)
    {
        if (process.IsProtected)
            return LocalizationManager.Text("Limit_Protected");
        var parts = new List<string>(2);
        if (process.DownloadLimitBytesPerSecond is long down and > 0)
            parts.Add($"↓{ProcessViewModel.FormatBytes(down)}");
        if (process.UploadLimitBytesPerSecond is long up and > 0)
            parts.Add($"↑{ProcessViewModel.FormatBytes(up)}");
        return parts.Count > 0 ? string.Join(" ", parts) : LocalizationManager.Text("Limit_None");
    }

    /// <summary>
    /// Which other services a limit on <paramref name="service"/> slows: every service in the
    /// same svchost shares its limit. Null when it runs alone.
    /// </summary>
    public static string? SharedNote(string service, IReadOnlyList<string> services)
    {
        var others = services.Where(s => !s.Equals(service, StringComparison.OrdinalIgnoreCase)).ToList();
        return others.Count switch
        {
            0 => null,
            1 => LocalizationManager.Format("SetLimit_SharedOne", service, others[0]),
            _ => LocalizationManager.Format("SetLimit_SharedMany", service, string.Join(", ", others))
        };
    }
}
