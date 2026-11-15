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
}
