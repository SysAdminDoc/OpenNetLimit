using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows.Markup;
using DataBinding = System.Windows.Data.Binding;
using DataBindingMode = System.Windows.Data.BindingMode;

namespace OpenNetLimit.UI.Services;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private const string DefaultCultureCode = "en";
    private static readonly string[] CultureOrder = ["en", "es"];

    private static readonly string CulturePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenNetLimit",
        "culture.txt");

    private static readonly ResourceManager Resources =
        new("OpenNetLimit.UI.Resources.Strings", typeof(LocalizationManager).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultCultureCode);
    private string _cultureCode = DefaultCultureCode;

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public static event Action? CultureChanged
    {
        add => Instance.CultureChangedCore += value;
        remove => Instance.CultureChangedCore -= value;
    }

    private event Action? CultureChangedCore;

    public static string CurrentCultureCode => Instance._cultureCode;

    public static IReadOnlyList<string> SupportedCultureCodes => CultureOrder;

    public string this[string key] => Translate(key);

    public static string Text(string key) => Instance.Translate(key);

    public static string Format(string key, params object[] args)
    {
        return string.Format(Instance._culture, Instance.Translate(key), args);
    }

    public static void ApplySavedCulture()
    {
        Instance.ApplyCulture(LoadSavedCulture(), save: false);
    }

    public static void SetCulture(string cultureName, bool save = true)
    {
        Instance.ApplyCulture(cultureName, save);
    }

    public static void ToggleCulture()
    {
        var currentIndex = Array.IndexOf(CultureOrder, Instance._cultureCode);
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % CultureOrder.Length;
        Instance.ApplyCulture(CultureOrder[nextIndex], save: true);
    }

    private void ApplyCulture(string? cultureName, bool save)
    {
        var normalized = NormalizeCulture(cultureName);
        _cultureCode = normalized;
        _culture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentCulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        Thread.CurrentThread.CurrentCulture = _culture;
        Thread.CurrentThread.CurrentUICulture = _culture;

        if (save)
            SaveCulture(normalized);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(DataBinding.IndexerName));
        CultureChangedCore?.Invoke();
    }

    private string Translate(string key)
    {
        return Resources.GetString(key, _culture) ?? key;
    }

    private static string LoadSavedCulture()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("OPENNETLIMIT_UI_CULTURE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment;

        try
        {
            if (File.Exists(CulturePath))
                return File.ReadAllText(CulturePath).Trim();
        }
        catch
        {
            // Fall back to the OS UI culture.
        }

        return CultureInfo.CurrentUICulture.Name;
    }

    private static void SaveCulture(string cultureCode)
    {
        try
        {
            var dir = Path.GetDirectoryName(CulturePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(CulturePath, cultureCode);
        }
        catch
        {
            // Best-effort user preference persistence.
        }
    }

    private static string NormalizeCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return DefaultCultureCode;

        foreach (var cultureCode in CultureOrder)
        {
            if (cultureName.Equals(cultureCode, StringComparison.OrdinalIgnoreCase) ||
                cultureName.StartsWith(cultureCode + "-", StringComparison.OrdinalIgnoreCase))
                return cultureCode;
        }

        return DefaultCultureCode;
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new DataBinding($"[{Key}]")
        {
            Source = LocalizationManager.Instance,
            Mode = DataBindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
