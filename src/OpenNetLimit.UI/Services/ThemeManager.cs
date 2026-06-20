using System.IO;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace OpenNetLimit.UI.Services;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeManager
{
    private static readonly string ThemePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenNetLimit",
        "theme.txt");

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["WindowBackgroundBrush"] = "#121417",
        ["PanelBackgroundBrush"] = "#1B1F24",
        ["ControlBackgroundBrush"] = "#252B33",
        ["ControlHoverBrush"] = "#303843",
        ["ControlPressedBrush"] = "#3A444F",
        ["InputBackgroundBrush"] = "#15191F",
        ["InputBorderBrush"] = "#4A535F",
        ["TextBrush"] = "#F0F3F6",
        ["MutedTextBrush"] = "#A7B0BA",
        ["BorderBrush"] = "#3A414B",
        ["GridBackgroundBrush"] = "#171A1F",
        ["GridHeaderBrush"] = "#232931",
        ["GridAltRowBrush"] = "#20262D",
        ["GridLineBrush"] = "#343B45",
        ["SelectionBrush"] = "#0E4C73",
        ["SelectionTextBrush"] = "#FFFFFF",
        ["DisabledTextBrush"] = "#707984",
        ["StatusBarBackgroundBrush"] = "#181C21",
        ["AccentBrush"] = "#2D8FD5"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["WindowBackgroundBrush"] = "#FAFAFA",
        ["PanelBackgroundBrush"] = "#FFFFFF",
        ["ControlBackgroundBrush"] = "#F1F3F5",
        ["ControlHoverBrush"] = "#E6EAEE",
        ["ControlPressedBrush"] = "#D8DEE5",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["InputBorderBrush"] = "#AEB7C2",
        ["TextBrush"] = "#17202A",
        ["MutedTextBrush"] = "#606B78",
        ["BorderBrush"] = "#D0D7DE",
        ["GridBackgroundBrush"] = "#FFFFFF",
        ["GridHeaderBrush"] = "#EEF2F5",
        ["GridAltRowBrush"] = "#F7F9FB",
        ["GridLineBrush"] = "#D8DEE5",
        ["SelectionBrush"] = "#CFE7F8",
        ["SelectionTextBrush"] = "#17202A",
        ["DisabledTextBrush"] = "#8A949F",
        ["StatusBarBackgroundBrush"] = "#F1F3F5",
        ["AccentBrush"] = "#0B73B8"
    };

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static string ThemeDisplayName => $"Theme: {CurrentTheme}";

    public static event Action<AppTheme>? ThemeChanged;

    public static void ApplySavedTheme()
    {
        ApplyTheme(LoadSavedTheme(), save: false);
    }

    public static void ToggleTheme()
    {
        ApplyTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark, save: true);
    }

    public static void ApplyTheme(AppTheme theme)
    {
        ApplyTheme(theme, save: true);
    }

    public static SkiaThemeColors GetChartColors()
    {
        return CurrentTheme == AppTheme.Dark
            ? new SkiaThemeColors(0xF0, 0xF3, 0xF6, 0x34, 0x3B, 0x45)
            : new SkiaThemeColors(0x17, 0x20, 0x2A, 0xD8, 0xDE, 0xE5);
    }

    private static void ApplyTheme(AppTheme theme, bool save)
    {
        CurrentTheme = theme;
        var palette = theme == AppTheme.Dark ? DarkPalette : LightPalette;
        var resources = System.Windows.Application.Current.Resources;

        foreach (var (key, value) in palette)
            resources[key] = CreateBrush(value);

        if (save)
            SaveTheme(theme);

        ThemeChanged?.Invoke(theme);
    }

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(ThemePath))
                return AppTheme.Dark;

            var value = File.ReadAllText(ThemePath).Trim();
            return Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme)
                ? theme
                : AppTheme.Dark;
        }
        catch
        {
            return AppTheme.Dark;
        }
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            var dir = Path.GetDirectoryName(ThemePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(ThemePath, theme.ToString());
        }
        catch
        {
            // Best-effort user preference persistence.
        }
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public readonly record struct SkiaThemeColors(byte LabelR, byte LabelG, byte LabelB, byte GridR, byte GridG, byte GridB);
