using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _reconnectTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ObservableCollection<ProcessViewModel> Processes { get; } = [];

    private string _statusText = "Disconnected";
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
        set => SetField(ref _activeConnectionCount, value);
    }

    private int _activeRuleCount;
    public int ActiveRuleCount
    {
        get => _activeRuleCount;
        set => SetField(ref _activeRuleCount, value);
    }

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
        set => SetField(ref _permissionDisplay, value);
    }

    public MainViewModel()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollServiceAsync();

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += async (_, _) => await TryConnectAsync();

        DetectAdmin();
        _ = TryConnectAsync();
    }

    private void DetectAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        PermissionDisplay = IsAdmin ? "Administrator" : "Read-only";
    }

    private async Task TryConnectAsync()
    {
        UpdateStatus(ConnectionState.Connecting, "Connecting...");
        var connected = await _client.ConnectAsync();

        if (connected)
        {
            _reconnectTimer.Stop();
            _pollTimer.Start();
            UpdateStatus(ConnectionState.Connected, "Connected");
            await PollServiceAsync();
        }
        else
        {
            _pollTimer.Stop();
            if (!_reconnectTimer.IsEnabled)
                _reconnectTimer.Start();
            UpdateStatus(_client.State, _client.LastError ?? "Service not running");
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
            return "None";

        var parts = new List<string>();
        if (proc.DownloadLimitBytesPerSecond is > 0)
            parts.Add($"↓{ProcessViewModel.FormatBytes(proc.DownloadLimitBytesPerSecond.Value)}");
        if (proc.UploadLimitBytesPerSecond is > 0)
            parts.Add($"↑{ProcessViewModel.FormatBytes(proc.UploadLimitBytesPerSecond.Value)}");
        return parts.Count > 0 ? string.Join(" ", parts) : "None";
    }

    public async Task SetLimitAsync(string processName, long downloadBytesPerSec, long uploadBytesPerSec)
    {
        if (_client.State != ConnectionState.Connected) return;

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

    private void OnDisconnected()
    {
        _pollTimer.Stop();
        UpdateStatus(ConnectionState.Disconnected, "Service disconnected");
        if (!_reconnectTimer.IsEnabled)
            _reconnectTimer.Start();
    }

    private void UpdateStatus(ConnectionState state, string text)
    {
        StatusText = text;
        StatusColor = state switch
        {
            ConnectionState.Connected => Brushes.LimeGreen,
            ConnectionState.Connecting => Brushes.Orange,
            ConnectionState.Error => Brushes.Red,
            _ => Brushes.Gray
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _reconnectTimer.Stop();
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

    private string _limitDisplay = "None";
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
