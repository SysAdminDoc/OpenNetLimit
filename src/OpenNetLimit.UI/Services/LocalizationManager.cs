using System.ComponentModel;
using System.Globalization;
using System.IO;
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

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = new Dictionary<string, string>
            {
                ["Action_Back"] = "Back",
                ["Action_Cancel"] = "Cancel",
                ["Action_Finish"] = "Finish",
                ["Action_Next"] = "Next",
                ["Action_Ok"] = "OK",
                ["Action_Skip"] = "Skip",
                ["Alert_BalloonTitle"] = "OpenNetLimit bandwidth alert",
                ["Chart_Automation"] = "Real-time traffic chart",
                ["Chart_Download"] = "Download",
                ["Chart_Kbps"] = "KB/s",
                ["Chart_Upload"] = "Upload",
                ["Column_DownloadPerSecond"] = "Download/s",
                ["Column_Limit"] = "Limit",
                ["Column_PID"] = "PID",
                ["Column_Process"] = "Process",
                ["Column_TotalDown"] = "Total Down",
                ["Column_TotalUp"] = "Total Up",
                ["Column_UploadPerSecond"] = "Upload/s",
                ["Label_Download"] = "Download: ",
                ["Label_Upload"] = "Upload: ",
                ["LanguageDisplay"] = "Language: {0}",
                ["Limit_None"] = "None",
                ["Main_Title"] = "OpenNetLimit",
                ["Main_WindowTitle"] = "OpenNetLimit",
                ["Menu_RemoveLimit"] = "Remove Limit",
                ["Menu_SetBandwidthLimit"] = "Set Bandwidth Limit...",
                ["Mode_Administrator"] = "Administrator",
                ["Mode_ReadOnly"] = "Read-only",
                ["ProcessGrid_Automation"] = "Process traffic list",
                ["RateUnit_Kbps"] = "KB/s",
                ["ServiceStatus_Automation"] = "Service status",
                ["SetLimit_DownloadAutomation"] = "Download limit in KB/s",
                ["SetLimit_DownloadLabel"] = "Download:",
                ["SetLimit_InvalidMessage"] = "Please enter valid non-negative numbers.",
                ["SetLimit_InvalidTitle"] = "Invalid Input",
                ["SetLimit_ProcessLabel"] = "Limit bandwidth for: {0}",
                ["SetLimit_Title"] = "Set Bandwidth Limit",
                ["SetLimit_UploadAutomation"] = "Upload limit in KB/s",
                ["SetLimit_UploadLabel"] = "Upload:",
                ["SetLimit_ZeroHint"] = "Set to 0 to remove limit.",
                ["Setup_ReadyBodyPrimary"] = "OpenNetLimit will connect to the background service and start showing your network activity.",
                ["Setup_ReadyBodySecondary"] = "Right-click any process in the list to set bandwidth limits. Use the system tray icon for quick access.",
                ["Setup_ReadyHint"] = "Click Finish to open the main window.",
                ["Setup_ReadyTitle"] = "You're all set!",
                ["Setup_RequirementsAntivirus"] = "Some antivirus or EDR software may flag the WinDivert driver. Add an exception if needed.",
                ["Setup_RequirementsPrivileges"] = "Administrator privileges are required to load the WinDivert network driver.",
                ["Setup_RequirementsService"] = "The OpenNetLimit service must be running for monitoring and limiting to work.",
                ["Setup_RequirementsStorage"] = "Traffic rules, statistics, and logs are stored in %ProgramData%\\OpenNetLimit.",
                ["Setup_RequirementsTitle"] = "Requirements",
                ["Setup_Title"] = "OpenNetLimit - First-Run Setup",
                ["Setup_WelcomeBody"] = "OpenNetLimit monitors and controls per-application network bandwidth on your Windows PC.",
                ["Setup_WelcomeHint"] = "This wizard will help you configure the essential settings. You can change these later at any time.",
                ["Setup_WelcomeTitle"] = "Welcome to OpenNetLimit",
                ["Status_Connected"] = "Connected",
                ["Status_Connecting"] = "Connecting...",
                ["Status_Disconnected"] = "Disconnected",
                ["Status_ServiceDisconnected"] = "Service disconnected",
                ["Status_ServiceNotRunning"] = "Service not running",
                ["StatusBar_ActiveRules"] = "Active Rules: {0}",
                ["StatusBar_Automation"] = "Status bar",
                ["StatusBar_Connections"] = "Connections: {0}",
                ["StatusBar_Mode"] = "Mode: {0}",
                ["StatusBar_RecentAlerts"] = "Recent Alerts: {0}",
                ["Theme_Dark"] = "Dark",
                ["Theme_Light"] = "Light",
                ["ThemeDisplay"] = "Theme: {0}",
                ["TrafficSummary_Automation"] = "Traffic summary",
                ["Tray_Exit"] = "Exit",
                ["Tray_Show"] = "Show"
            },
            ["es"] = new Dictionary<string, string>
            {
                ["Action_Back"] = "Atras",
                ["Action_Cancel"] = "Cancelar",
                ["Action_Finish"] = "Finalizar",
                ["Action_Next"] = "Siguiente",
                ["Action_Ok"] = "Aceptar",
                ["Action_Skip"] = "Omitir",
                ["Alert_BalloonTitle"] = "Alerta de ancho de banda de OpenNetLimit",
                ["Chart_Automation"] = "Grafico de trafico en tiempo real",
                ["Chart_Download"] = "Descarga",
                ["Chart_Kbps"] = "KB/s",
                ["Chart_Upload"] = "Subida",
                ["Column_DownloadPerSecond"] = "Descarga/s",
                ["Column_Limit"] = "Limite",
                ["Column_PID"] = "PID",
                ["Column_Process"] = "Proceso",
                ["Column_TotalDown"] = "Total descargado",
                ["Column_TotalUp"] = "Total subido",
                ["Column_UploadPerSecond"] = "Subida/s",
                ["Label_Download"] = "Descarga: ",
                ["Label_Upload"] = "Subida: ",
                ["LanguageDisplay"] = "Idioma: {0}",
                ["Limit_None"] = "Ninguno",
                ["Main_Title"] = "OpenNetLimit",
                ["Main_WindowTitle"] = "OpenNetLimit",
                ["Menu_RemoveLimit"] = "Quitar limite",
                ["Menu_SetBandwidthLimit"] = "Establecer limite de ancho de banda...",
                ["Mode_Administrator"] = "Administrador",
                ["Mode_ReadOnly"] = "Solo lectura",
                ["ProcessGrid_Automation"] = "Lista de trafico por proceso",
                ["RateUnit_Kbps"] = "KB/s",
                ["ServiceStatus_Automation"] = "Estado del servicio",
                ["SetLimit_DownloadAutomation"] = "Limite de descarga en KB/s",
                ["SetLimit_DownloadLabel"] = "Descarga:",
                ["SetLimit_InvalidMessage"] = "Escriba numeros no negativos validos.",
                ["SetLimit_InvalidTitle"] = "Entrada no valida",
                ["SetLimit_ProcessLabel"] = "Limitar ancho de banda para: {0}",
                ["SetLimit_Title"] = "Establecer limite de ancho de banda",
                ["SetLimit_UploadAutomation"] = "Limite de subida en KB/s",
                ["SetLimit_UploadLabel"] = "Subida:",
                ["SetLimit_ZeroHint"] = "Use 0 para quitar el limite.",
                ["Setup_ReadyBodyPrimary"] = "OpenNetLimit se conectara al servicio en segundo plano y empezara a mostrar la actividad de red.",
                ["Setup_ReadyBodySecondary"] = "Haga clic derecho en cualquier proceso de la lista para establecer limites. Use el icono de la bandeja para acceso rapido.",
                ["Setup_ReadyHint"] = "Haga clic en Finalizar para abrir la ventana principal.",
                ["Setup_ReadyTitle"] = "Todo listo.",
                ["Setup_RequirementsAntivirus"] = "Algunos antivirus o EDR pueden marcar el controlador WinDivert. Agregue una excepcion si hace falta.",
                ["Setup_RequirementsPrivileges"] = "Se requieren privilegios de administrador para cargar el controlador de red WinDivert.",
                ["Setup_RequirementsService"] = "El servicio de OpenNetLimit debe estar en ejecucion para supervisar y limitar trafico.",
                ["Setup_RequirementsStorage"] = "Las reglas, estadisticas y registros se guardan en %ProgramData%\\OpenNetLimit.",
                ["Setup_RequirementsTitle"] = "Requisitos",
                ["Setup_Title"] = "OpenNetLimit - Configuracion inicial",
                ["Setup_WelcomeBody"] = "OpenNetLimit supervisa y controla el ancho de banda de red por aplicacion en su PC Windows.",
                ["Setup_WelcomeHint"] = "Este asistente le ayuda a configurar los ajustes esenciales. Puede cambiarlos mas tarde.",
                ["Setup_WelcomeTitle"] = "Bienvenido a OpenNetLimit",
                ["Status_Connected"] = "Conectado",
                ["Status_Connecting"] = "Conectando...",
                ["Status_Disconnected"] = "Desconectado",
                ["Status_ServiceDisconnected"] = "Servicio desconectado",
                ["Status_ServiceNotRunning"] = "El servicio no se esta ejecutando",
                ["StatusBar_ActiveRules"] = "Reglas activas: {0}",
                ["StatusBar_Automation"] = "Barra de estado",
                ["StatusBar_Connections"] = "Conexiones: {0}",
                ["StatusBar_Mode"] = "Modo: {0}",
                ["StatusBar_RecentAlerts"] = "Alertas recientes: {0}",
                ["Theme_Dark"] = "Oscuro",
                ["Theme_Light"] = "Claro",
                ["ThemeDisplay"] = "Tema: {0}",
                ["TrafficSummary_Automation"] = "Resumen de trafico",
                ["Tray_Exit"] = "Salir",
                ["Tray_Show"] = "Mostrar"
            }
        };

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
        if (Catalogs.TryGetValue(_cultureCode, out var catalog) && catalog.TryGetValue(key, out var value))
            return value;

        if (Catalogs[DefaultCultureCode].TryGetValue(key, out var fallback))
            return fallback;

        return key;
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
