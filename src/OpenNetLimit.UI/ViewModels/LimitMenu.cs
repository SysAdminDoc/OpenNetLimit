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
    /// The Limit rule Set limit changes: the first one on <paramref name="serviceName"/> when it
    /// names a service, otherwise the first of the program's own that reaches it. The service lists
    /// rules in the order it tries them, so that's the one in force, and a new rule after it would
    /// change nothing. A rule that's turned off or outside its hours isn't in force, so Set limit
    /// adds one rather than change numbers that wouldn't apply.
    /// </summary>
    public static BandwidthRule? ExistingLimit(IEnumerable<BandwidthRule> rules, string processName, string? serviceName,
        string? processPath = null)
    {
        var program = ProcessNames.Normalize(processName) ?? processName;
        return rules.FirstOrDefault(r => r.Action == RuleAction.Limit && r.IsActiveNow() && (serviceName is not null
            ? string.Equals(r.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
            : IsProgramsOwn(r, program, processPath)));
    }

    /// <summary>
    /// A new Limit rule on the service when <paramref name="serviceName"/> names one, otherwise on
    /// the program, and on its path too when it's a svchost copy.
    /// </summary>
    public static BandwidthRule NewLimit(string processName, string? serviceName, long downloadBytesPerSecond,
        long uploadBytesPerSecond, string? processPath = null) => new()
    {
        ProcessName = serviceName is null ? processName : null,
        ProcessPath = serviceName is null ? SvchostCopyPath(processName, processPath) : null,
        ServiceName = serviceName,
        DownloadBytesPerSecond = downloadBytesPerSecond,
        UploadBytesPerSecond = uploadBytesPerSecond,
        Action = RuleAction.Limit
    };

    /// <summary>
    /// The path of a program called svchost that runs outside Windows' folders, which is some
    /// other program's copy. A rule reaches it by that path, since a rule on the name svchost
    /// alone is one on Windows' svchost. Null for Windows' own svchost and every other program.
    /// </summary>
    private static string? SvchostCopyPath(string processName, string? processPath) =>
        !string.IsNullOrWhiteSpace(processPath) && ProcessRowText.IsServiceHost(processName)
            && !WindowsFolders.Contain(processPath)
            ? processPath
            : null;

    /// <summary>
    /// The rules Remove limit deletes for a row: limits and blocks of the program's own that reach
    /// it, and those on any service running in it, which is the only way a rule reaches Windows'
    /// svchost. An Allow rule limits nothing. It keeps a wider rule, like one on the program's
    /// folder, off this program, so deleting it could only add a limit.
    /// </summary>
    public static IEnumerable<BandwidthRule> RulesFor(IEnumerable<BandwidthRule> rules, string processName,
        IReadOnlyList<string>? services, string? processPath = null)
    {
        var program = ProcessNames.Normalize(processName) ?? processName;
        return rules.Where(r => r.Action != RuleAction.Allow && (r.ServiceName is { } service
            ? services?.Contains(service, StringComparer.OrdinalIgnoreCase) == true
            : IsProgramsOwn(r, program, processPath)));
    }

    /// <summary>
    /// The rule names the program, or has no name and sits on its exact path, and the service
    /// would match it to the row: by path once the row's is known, by name until then, and never
    /// to Windows' svchost. A rule on a folder or pattern with no name covers other programs too,
    /// so it isn't this one's to change or delete.
    /// </summary>
    private static bool IsProgramsOwn(BandwidthRule rule, string program, string? processPath) =>
        rule.ServiceName is null
        && (string.Equals(rule.ProcessName, program, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(rule.ProcessName) && processPath is not null
                && string.Equals(rule.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
        && rule.MatchesProcess(program, processPath);
}
