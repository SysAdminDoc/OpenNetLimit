using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly HashSet<Guid> _seenAlertIds = [];
    private bool _alertEventsInitialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int ChartMaxPoints = 60;

    private readonly ObservableCollection<ObservableValue> _downloadPoints = [];
    private readonly ObservableCollection<ObservableValue> _uploadPoints = [];

    public ObservableCollection<ProcessViewModel> Processes { get; } = [];

    public ISeries[] ChartSeries { get; }

    public Axis[] ChartYAxes { get; } =
    [
        new Axis
        {
            MinLimit = 0,
            Labeler = FormatRateAxis
        }
    ];

    public Axis[] ChartXAxes { get; } =
    [
        new Axis
        {
            Labels = null,
            IsVisible = false
        }
    ];

    private string _statusKey = "Status_Disconnected";
    private string? _statusFallbackText;

    private string _statusText = LocalizationManager.Text("Status_Disconnected");
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private Brush _statusColor = Brushes.Gray;
    public Brush StatusColor
    {
        get => _statusColor;
        set => SetField(ref _statusColor, value);
    }

    private string _totalDownload = "0 B/s";
    public string TotalDownload
    {
        get => _totalDownload;
        set => SetField(ref _totalDownload, value);
    }

    private string _totalUpload = "0 B/s";
    public string TotalUpload
    {
        get => _totalUpload;
        set => SetField(ref _totalUpload, value);
    }

    private int _activeConnectionCount;
    public int ActiveConnectionCount
    {
        get => _activeConnectionCount;
        set
        {
            if (SetField(ref _activeConnectionCount, value))
                OnPropertyChanged(nameof(ConnectionsDisplay));
        }
    }

    public string ConnectionsDisplay => LocalizationManager.Format("StatusBar_Connections", ActiveConnectionCount);

    private int _activeRuleCount;
    public int ActiveRuleCount
    {
        get => _activeRuleCount;
        set
        {
            if (SetField(ref _activeRuleCount, value))
                OnPropertyChanged(nameof(ActiveRulesDisplay));
        }
    }

    public string ActiveRulesDisplay => LocalizationManager.Format("StatusBar_ActiveRules", ActiveRuleCount);

    private int _recentAlertCount;
    public int RecentAlertCount
    {
        get => _recentAlertCount;
        set
        {
            if (SetField(ref _recentAlertCount, value))
                OnPropertyChanged(nameof(RecentAlertsDisplay));
        }
    }

    public string RecentAlertsDisplay => LocalizationManager.Format("StatusBar_RecentAlerts", RecentAlertCount);

    private bool _isAdmin;
    public bool IsAdmin
    {
        get => _isAdmin;
        set => SetField(ref _isAdmin, value);
    }

    private string _permissionDisplay = "Read-only";
    public string PermissionDisplay
    {
        get => _permissionDisplay;
        set
        {
            if (SetField(ref _permissionDisplay, value))
                OnPropertyChanged(nameof(PermissionModeDisplay));
        }
    }

    public string PermissionModeDisplay => LocalizationManager.Format("StatusBar_Mode", PermissionDisplay);

    public string ThemeDisplay => LocalizationManager.Format(
        "ThemeDisplay",
        LocalizationManager.Text(ThemeManager.CurrentTheme == AppTheme.Dark ? "Theme_Dark" : "Theme_Light"));

    public string LanguageDisplay => LocalizationManager.Format(
        "LanguageDisplay",
        LocalizationManager.CurrentCultureCode.ToUpperInvariant());

    public HistoryViewModel HistoryViewModel { get; }

    public MainViewModel()
    {
        ChartSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _downloadPoints,
                Name = LocalizationManager.Text("Chart_Download"),
                GeometrySize = 0,
                Stroke = new SolidColorPaint(new SKColor(0x2C, 0xCA, 0xF6)) { StrokeThickness = 3 },
                Fill = new SolidColorPaint(new SKColor(0x2C, 0xCA, 0xF6, 0x28)),
                LineSmoothness = 0.3
            },
            new LineSeries<ObservableValue>
            {
                Values = _uploadPoints,
                Name = LocalizationManager.Text("Chart_Upload"),
                GeometrySize = 0,
                Stroke = new SolidColorPaint(new SKColor(0x4B, 0xE5, 0xA2)) { StrokeThickness = 3 },
                Fill = new SolidColorPaint(new SKColor(0x4B, 0xE5, 0xA2, 0x24)),
                LineSmoothness = 0.3
            }
        ];

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollServiceAsync();

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += async (_, _) => await TryConnectAsync();

        HistoryViewModel = new HistoryViewModel(_client);

        ThemeManager.ThemeChanged += OnThemeChanged;
        LocalizationManager.CultureChanged += OnCultureChanged;
        ApplyChartTheme();
        DetectAdmin();
        RefreshLocalizedStrings();
        if (MarketingCapture.IsEnabled)
            ApplyMarketingFixture();
        else
            _ = TryConnectAsync();
    }

    private void ApplyMarketingFixture()
    {
        IsAdmin = true;
        PermissionDisplay = LocalizationManager.Text("Mode_Administrator");
        TotalDownload = "18.6 MB/s";
        TotalUpload = "2.4 MB/s";
        ActiveConnectionCount = 5;
        ActiveRuleCount = 3;
        RecentAlertCount = 1;
        UpdateStatus(ConnectionState.Connected, "Status_Connected");

        double[] downloadMbps =
        [
            6.2, 7.1, 7.5, 8.8, 9.4, 8.9, 10.2, 11.6, 10.8, 12.4,
            13.1, 12.7, 14.5, 15.2, 14.8, 16.1, 15.6, 17.3, 16.8, 18.6,
            17.9, 18.2, 16.7, 17.4, 18.1, 17.6, 19.2, 18.8, 17.5, 18.6
        ];
        double[] uploadMbps =
        [
            0.8, 1.0, 0.9, 1.2, 1.1, 1.3, 1.4, 1.2, 1.6, 1.5,
            1.7, 1.9, 1.6, 1.8, 2.0, 1.9, 2.2, 2.1, 2.3, 2.4,
            2.2, 2.5, 2.3, 2.1, 2.4, 2.6, 2.5, 2.3, 2.4, 2.4
        ];
        foreach (var value in downloadMbps)
            _downloadPoints.Add(new ObservableValue(value * 1024 * 1024));
        foreach (var value in uploadMbps)
            _uploadPoints.Add(new ObservableValue(value * 1024 * 1024));

        Processes.Add(new ProcessViewModel
        {
            ProcessId = 18432,
            ProcessName = "msedge.exe",
            DownloadDisplay = "8.4 MB/s",
            UploadDisplay = "620.0 KB/s",
            TotalDownDisplay = "5.8 GB",
            TotalUpDisplay = "320.4 MB",
            LimitDisplay = "↓ 12.0 MB/s  ↑ 1.5 MB/s"
        });
        Processes.Add(new ProcessViewModel
        {
            ProcessId = 10288,
            ProcessName = "steam.exe",
            DownloadDisplay = "6.1 MB/s",
            UploadDisplay = "1.2 MB/s",
            TotalDownDisplay = "18.4 GB",
            TotalUpDisplay = "2.1 GB",
            LimitDisplay = "↓ 8.0 MB/s"
        });
        Processes.Add(new ProcessViewModel
        {
            ProcessId = 22576,
            ProcessName = "OneDrive.exe",
            DownloadDisplay = "2.7 MB/s",
            UploadDisplay = "480.0 KB/s",
            TotalDownDisplay = "3.2 GB",
            TotalUpDisplay = "1.8 GB",
            LimitDisplay = "↓ 3.0 MB/s  ↑ 768 KB/s"
        });
        Processes.Add(new ProcessViewModel
        {
            ProcessId = 30844,
            ProcessName = "Teams.exe",
            DownloadDisplay = "640.0 KB/s",
            UploadDisplay = "190.0 KB/s",
            TotalDownDisplay = "1.6 GB",
            TotalUpDisplay = "740.0 MB",
            LimitDisplay = LocalizationManager.Text("Limit_None")
        });
        Processes.Add(new ProcessViewModel
        {
            ProcessId = 1532,
            ProcessName = "svchost.exe",
            DownloadDisplay = "210.0 KB/s",
            UploadDisplay = "42.0 KB/s",
            TotalDownDisplay = "890.0 MB",
            TotalUpDisplay = "120.0 MB",
            LimitDisplay = LocalizationManager.Text("Limit_None")
        });

        HistoryViewModel.ApplyMarketingFixture();
    }

    private void DetectAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        PermissionDisplay = IsAdmin
            ? LocalizationManager.Text("Mode_Administrator")
            : LocalizationManager.Text("Mode_ReadOnly");
    }

    private async Task TryConnectAsync()
    {
        UpdateStatus(ConnectionState.Connecting, "Status_Connecting");
        var connected = await _client.ConnectAsync();

        if (connected)
        {
            _reconnectTimer.Stop();
            _pollTimer.Start();
            UpdateStatus(ConnectionState.Connected, "Status_Connected");
            await PollServiceAsync();
        }
        else
        {
            _pollTimer.Stop();
            if (!_reconnectTimer.IsEnabled)
                _reconnectTimer.Start();

            if (string.IsNullOrWhiteSpace(_client.LastError) ||
                string.Equals(_client.LastError, "Service not running", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus(_client.State, "Status_ServiceNotRunning");
            }
            else
            {
                UpdateStatus(_client.State, "Status_ServiceNotRunning", _client.LastError);
            }
        }
    }

    private async Task PollServiceAsync()
    {
        var snapshotJson = await _client.SendCommandAsync("SNAPSHOT");
        if (snapshotJson is null)
        {
            OnDisconnected();
            return;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<TrafficSnapshot>(snapshotJson, JsonOptions);
            if (snapshot is null) return;

            TotalDownload = ProcessViewModel.FormatBytes(snapshot.TotalDownloadBytesPerSecond);
            TotalUpload = ProcessViewModel.FormatBytes(snapshot.TotalUploadBytesPerSecond);

            _downloadPoints.Add(new ObservableValue(snapshot.TotalDownloadBytesPerSecond));
            _uploadPoints.Add(new ObservableValue(snapshot.TotalUploadBytesPerSecond));
            while (_downloadPoints.Count > ChartMaxPoints) _downloadPoints.RemoveAt(0);
            while (_uploadPoints.Count > ChartMaxPoints) _uploadPoints.RemoveAt(0);

            UpdateProcessList(snapshot.Processes);
        }
        catch
        {
            // Malformed response — ignore this tick
        }

        var rulesJson = await _client.SendCommandAsync("RULES");
        if (rulesJson is not null)
        {
            try
            {
                var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(rulesJson, JsonOptions);
                ActiveRuleCount = rules?.Count ?? 0;
            }
            catch { }
        }

        await PollAlertEventsAsync();
    }

    private async Task PollAlertEventsAsync()
    {
        var alertsJson = await _client.SendCommandAsync("ALERT_EVENTS");
        if (alertsJson is null) return;

        try
        {
            var alerts = JsonSerializer.Deserialize<List<BandwidthAlertEvent>>(alertsJson, JsonOptions);
            if (alerts is null) return;

            RecentAlertCount = alerts.Count;
            foreach (var alert in alerts.OrderBy(a => a.TriggeredAt))
            {
                if (!_seenAlertIds.Add(alert.Id))
                    continue;

                if (_alertEventsInitialized)
                    BandwidthAlertRaised?.Invoke(alert);
            }

            if (_seenAlertIds.Count > 1000)
            {
                var recentIds = alerts.Select(a => a.Id).ToHashSet();
                _seenAlertIds.IntersectWith(recentIds);
            }

            _alertEventsInitialized = true;
        }
        catch
        {
            // Malformed alert response — ignore this tick
        }
    }

    private void UpdateProcessList(IReadOnlyList<ProcessTrafficInfo> processes)
    {
        var existingByPid = new Dictionary<uint, ProcessViewModel>();
        foreach (var p in Processes)
            existingByPid[p.ProcessId] = p;

        var activePids = new HashSet<uint>();
        foreach (var proc in processes)
        {
            activePids.Add(proc.ProcessId);

            if (existingByPid.TryGetValue(proc.ProcessId, out var existing))
            {
                existing.DownloadDisplay = ProcessViewModel.FormatBytes(proc.CurrentDownloadBytesPerSecond);
                existing.UploadDisplay = ProcessViewModel.FormatBytes(proc.CurrentUploadBytesPerSecond);
                existing.TotalDownDisplay = ProcessViewModel.FormatTotalBytes(proc.TotalBytesReceived);
                existing.TotalUpDisplay = ProcessViewModel.FormatTotalBytes(proc.TotalBytesSent);
                existing.LimitDisplay = FormatLimit(proc);
            }
            else
            {
                Processes.Add(new ProcessViewModel
                {
                    ProcessId = proc.ProcessId,
                    ProcessName = proc.ProcessName,
                    DownloadDisplay = ProcessViewModel.FormatBytes(proc.CurrentDownloadBytesPerSecond),
                    UploadDisplay = ProcessViewModel.FormatBytes(proc.CurrentUploadBytesPerSecond),
                    TotalDownDisplay = ProcessViewModel.FormatTotalBytes(proc.TotalBytesReceived),
                    TotalUpDisplay = ProcessViewModel.FormatTotalBytes(proc.TotalBytesSent),
                    LimitDisplay = FormatLimit(proc)
                });
            }
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!activePids.Contains(Processes[i].ProcessId))
                Processes.RemoveAt(i);
        }

        ActiveConnectionCount = processes.Count;
    }

    private static string FormatLimit(ProcessTrafficInfo proc)
    {
        if (proc.DownloadLimitBytesPerSecond is null && proc.UploadLimitBytesPerSecond is null)
            return LocalizationManager.Text("Limit_None");

        var parts = new List<string>();
        if (proc.DownloadLimitBytesPerSecond is > 0)
            parts.Add($"↓{ProcessViewModel.FormatBytes(proc.DownloadLimitBytesPerSecond.Value)}");
        if (proc.UploadLimitBytesPerSecond is > 0)
            parts.Add($"↑{ProcessViewModel.FormatBytes(proc.UploadLimitBytesPerSecond.Value)}");
        return parts.Count > 0 ? string.Join(" ", parts) : LocalizationManager.Text("Limit_None");
    }

    private static string FormatRateAxis(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
            return $"{bytesPerSecond / (1024 * 1024):F0} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F0} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    public async Task SetLimitAsync(string processName, long downloadBytesPerSec, long uploadBytesPerSec)
    {
        if (_client.State != ConnectionState.Connected) return;

        var rulesJson = await _client.SendCommandAsync("RULES");
        BandwidthRule? existingRule = null;
        if (rulesJson is not null)
        {
            try
            {
                var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(rulesJson, JsonOptions);
                existingRule = rules?.FirstOrDefault(r =>
                    r.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) == true &&
                    r.Action == RuleAction.Limit);
            }
            catch { }
        }

        if (existingRule is not null)
        {
            existingRule.DownloadBytesPerSecond = downloadBytesPerSec;
            existingRule.UploadBytesPerSecond = uploadBytesPerSec;
            var json = JsonSerializer.Serialize(existingRule, JsonOptions);
            await _client.SendCommandAsync($"UPDATE_RULE {json}");
        }
        else
        {
            var rule = new BandwidthRule
            {
                ProcessName = processName,
                DownloadBytesPerSecond = downloadBytesPerSec,
                UploadBytesPerSecond = uploadBytesPerSec,
                Action = RuleAction.Limit
            };
            var json = JsonSerializer.Serialize(rule, JsonOptions);
            await _client.SendCommandAsync($"ADD_RULE {json}");
        }
    }

    public async Task RemoveLimitAsync(string processName)
    {
        if (_client.State != ConnectionState.Connected) return;

        var rulesJson = await _client.SendCommandAsync("RULES");
        if (rulesJson is null) return;

        try
        {
            var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(rulesJson, JsonOptions);
            if (rules is null) return;

            foreach (var rule in rules.Where(r =>
                r.ProcessName?.Equals(processName, StringComparison.OrdinalIgnoreCase) == true))
            {
                await _client.SendCommandAsync($"REMOVE_RULE {rule.Id}");
            }
        }
        catch { }
    }

    public void ToggleTheme()
    {
        ThemeManager.ToggleTheme();
    }

    public void ToggleLanguage()
    {
        LocalizationManager.ToggleCulture();
    }

    private void OnThemeChanged(AppTheme _)
    {
        OnPropertyChanged(nameof(ThemeDisplay));
        ApplyChartTheme();
    }

    private void OnCultureChanged()
    {
        RefreshLocalizedStrings();
    }

    private void RefreshLocalizedStrings()
    {
        StatusText = _statusFallbackText ?? LocalizationManager.Text(_statusKey);
        PermissionDisplay = IsAdmin
            ? LocalizationManager.Text("Mode_Administrator")
            : LocalizationManager.Text("Mode_ReadOnly");

        if (ChartSeries[0] is LineSeries<ObservableValue> downloadSeries)
            downloadSeries.Name = LocalizationManager.Text("Chart_Download");
        if (ChartSeries[1] is LineSeries<ObservableValue> uploadSeries)
            uploadSeries.Name = LocalizationManager.Text("Chart_Upload");

        OnPropertyChanged(nameof(ConnectionsDisplay));
        OnPropertyChanged(nameof(ActiveRulesDisplay));
        OnPropertyChanged(nameof(RecentAlertsDisplay));
        OnPropertyChanged(nameof(PermissionModeDisplay));
        OnPropertyChanged(nameof(ThemeDisplay));
        OnPropertyChanged(nameof(LanguageDisplay));
    }

    private void ApplyChartTheme()
    {
        var colors = ThemeManager.GetChartColors();
        var labelPaint = new SolidColorPaint(new SKColor(colors.LabelR, colors.LabelG, colors.LabelB));
        var gridPaint = new SolidColorPaint(new SKColor(colors.GridR, colors.GridG, colors.GridB)) { StrokeThickness = 1 };

        foreach (var axis in ChartYAxes)
        {
            axis.LabelsPaint = labelPaint;
            axis.NamePaint = labelPaint;
            axis.SeparatorsPaint = gridPaint;
        }

        foreach (var axis in ChartXAxes)
            axis.LabelsPaint = labelPaint;
    }

    private void OnDisconnected()
    {
        _pollTimer.Stop();
        UpdateStatus(ConnectionState.Disconnected, "Status_ServiceDisconnected");
        if (!_reconnectTimer.IsEnabled)
            _reconnectTimer.Start();
    }

    private void UpdateStatus(ConnectionState state, string textKey, string? fallbackText = null)
    {
        _statusKey = textKey;
        _statusFallbackText = fallbackText;
        StatusText = fallbackText ?? LocalizationManager.Text(textKey);
        StatusColor = state switch
        {
            ConnectionState.Connected => Brushes.LimeGreen,
            ConnectionState.Connecting => Brushes.Orange,
            ConnectionState.Error => Brushes.Red,
            _ => Brushes.Gray
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<BandwidthAlertEvent>? BandwidthAlertRaised;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        LocalizationManager.CultureChanged -= OnCultureChanged;
        _pollTimer.Stop();
        _reconnectTimer.Stop();
        HistoryViewModel.Dispose();
        _client.Dispose();
    }
}

public class ProcessViewModel : INotifyPropertyChanged
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    private string _downloadDisplay = "0 B/s";
    public string DownloadDisplay
    {
        get => _downloadDisplay;
        set => SetField(ref _downloadDisplay, value);
    }

    private string _uploadDisplay = "0 B/s";
    public string UploadDisplay
    {
        get => _uploadDisplay;
        set => SetField(ref _uploadDisplay, value);
    }

    private string _totalDownDisplay = "0 B";
    public string TotalDownDisplay
    {
        get => _totalDownDisplay;
        set => SetField(ref _totalDownDisplay, value);
    }

    private string _totalUpDisplay = "0 B";
    public string TotalUpDisplay
    {
        get => _totalUpDisplay;
        set => SetField(ref _totalUpDisplay, value);
    }

    private string _limitDisplay = LocalizationManager.Text("Limit_None");
    public string LimitDisplay
    {
        get => _limitDisplay;
        set => SetField(ref _limitDisplay, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B/s",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB/s",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB/s",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB/s"
        };
    }

    public static string FormatTotalBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
