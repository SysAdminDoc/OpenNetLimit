using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace OpenNetLimit.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
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
