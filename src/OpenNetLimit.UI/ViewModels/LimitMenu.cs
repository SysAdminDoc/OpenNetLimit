using OpenNetLimit.Core;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>Decides whether the Set/Remove limit menu items work, and what to say when they don't.</summary>
public static class LimitMenu
{
    /// <summary>Returns the resource key explaining why the items are disabled, or null when they're usable.</summary>
    public static string? HintKey(bool isAdmin, bool hasSelection) =>
        !isAdmin ? "Menu_AdminRequired"
        : !hasSelection ? "Menu_SelectAppFirst"
        : null;

    /// <summary>
    /// Why no change can be made: the service turns changes away for
    /// <paramref name="changesRefused"/>, or the app isn't running as administrator. The refusal
    /// comes first since running as administrator wouldn't get past it. Null when changes go through.
    /// </summary>
    public static string? EditHint(bool isAdmin, string? changesRefused) =>
        changesRefused ?? (!isAdmin ? LocalizationManager.Text("Menu_AdminRequired") : null);

    /// <summary>
    /// The Limit rule Set limit changes: the one on <paramref name="serviceName"/> when it names a
    /// service, otherwise the one on the program.
    /// </summary>
    public static BandwidthRule? ExistingLimit(IEnumerable<BandwidthRule> rules, string processName, string? serviceName)
    {
        var program = ProcessNames.Normalize(processName);
        return rules.FirstOrDefault(r => r.Action == RuleAction.Limit && (serviceName is not null
            ? string.Equals(r.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
            : r.ServiceName is null && string.Equals(r.ProcessName, program, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A new Limit rule on the service when <paramref name="serviceName"/> names one, otherwise on the program.</summary>
    public static BandwidthRule NewLimit(string processName, string? serviceName, long downloadBytesPerSecond,
        long uploadBytesPerSecond) => new()
    {
        ProcessName = serviceName is null ? processName : null,
        ServiceName = serviceName,
        DownloadBytesPerSecond = downloadBytesPerSecond,
        UploadBytesPerSecond = uploadBytesPerSecond,
        Action = RuleAction.Limit
    };

    /// <summary>
    /// The rules Remove limit deletes for a row: those on the program by name, and those on any
    /// service running in it, which is the only way a rule reaches a svchost.
    /// </summary>
    public static IEnumerable<BandwidthRule> RulesFor(IEnumerable<BandwidthRule> rules, string processName,
        IReadOnlyList<string>? services)
    {
        var program = ProcessNames.Normalize(processName);
        return rules.Where(r => r.ServiceName is { } service
            ? services?.Contains(service, StringComparer.OrdinalIgnoreCase) == true
            : string.Equals(r.ProcessName, program, StringComparison.OrdinalIgnoreCase));
    }
}
