using System.IO;

namespace OpenNetLimit.UI.Services;

/// <summary>
/// Where the app keeps per-user settings (theme, language, first-run marker).
/// OPENNETLIMIT_UI_SETTINGS_DIR moves them, which keeps automated UI runs away from
/// the real profile.
/// </summary>
public static class UiPaths
{
    public const string SettingsDirectoryVariable = "OPENNETLIMIT_UI_SETTINGS_DIR";

    public static string SettingsDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(SettingsDirectoryVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenNetLimit")
                : overridden;
        }
    }

    public static string SettingsFile(string name) => Path.Combine(SettingsDirectory, name);
}
