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
        ["WindowBackgroundBrush"] = "#080D16",
        ["PanelBackgroundBrush"] = "#0F1726",
        ["SurfaceRaisedBrush"] = "#131E30",
        ["ControlBackgroundBrush"] = "#172337",
        ["ControlHoverBrush"] = "#1F304A",
        ["ControlPressedBrush"] = "#263A58",
        ["InputBackgroundBrush"] = "#0B1220",
        ["InputBorderBrush"] = "#2B3D58",
        ["TextBrush"] = "#F4F7FB",
        ["MutedTextBrush"] = "#93A4BC",
        ["BorderBrush"] = "#22324A",
        ["GridBackgroundBrush"] = "#0B1321",
        ["GridHeaderBrush"] = "#111C2D",
        ["GridAltRowBrush"] = "#0E1929",
        ["GridLineBrush"] = "#1D2B41",
        ["SelectionBrush"] = "#123D55",
        ["SelectionTextBrush"] = "#FFFFFF",
        ["DisabledTextBrush"] = "#627086",
        ["StatusBarBackgroundBrush"] = "#0B1220",
        ["AccentBrush"] = "#27C7F3",
        ["AccentSoftBrush"] = "#153C4B",
        ["DownloadBrush"] = "#2CCAF6",
        ["UploadBrush"] = "#4BE5A2",
        ["SuccessBrush"] = "#4BE5A2",
        ["WarningBrush"] = "#F2C66D",
        ["DangerBrush"] = "#F27887"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["WindowBackgroundBrush"] = "#F3F7FB",
        ["PanelBackgroundBrush"] = "#FFFFFF",
        ["SurfaceRaisedBrush"] = "#F8FBFE",
        ["ControlBackgroundBrush"] = "#EAF1F7",
        ["ControlHoverBrush"] = "#DFEAF3",
        ["ControlPressedBrush"] = "#D2E0EC",
        ["InputBackgroundBrush"] = "#FFFFFF",
        ["InputBorderBrush"] = "#AFC0D0",
        ["TextBrush"] = "#142033",
        ["MutedTextBrush"] = "#5E6F84",
        ["BorderBrush"] = "#D5E0EA",
        ["GridBackgroundBrush"] = "#FFFFFF",
        ["GridHeaderBrush"] = "#F0F5F9",
        ["GridAltRowBrush"] = "#F7FAFC",
        ["GridLineBrush"] = "#E1E9F0",
        ["SelectionBrush"] = "#D8F2FA",
        ["SelectionTextBrush"] = "#142033",
        ["DisabledTextBrush"] = "#8594A6",
        ["StatusBarBackgroundBrush"] = "#EAF1F7",
        ["AccentBrush"] = "#007FA8",
        ["AccentSoftBrush"] = "#DDF3F8",
        ["DownloadBrush"] = "#008FC2",
        ["UploadBrush"] = "#168A5E",
        ["SuccessBrush"] = "#168A5E",
        ["WarningBrush"] = "#A66A05",
        ["DangerBrush"] = "#B53A4A"
    };

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

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
            ? new SkiaThemeColors(0x93, 0xA4, 0xBC, 0x1D, 0x2B, 0x41)
            : new SkiaThemeColors(0x5E, 0x6F, 0x84, 0xE1, 0xE9, 0xF0);
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
