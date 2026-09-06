using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

public class HistoryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _client;
    private CancellationTokenSource? _loadCts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ObservableCollection<ObservableValue> _receivedPoints = [];
    private readonly ObservableCollection<ObservableValue> _sentPoints = [];
    private readonly ObservableCollection<string> _labels = [];

    public ISeries[] HistorySeries { get; }

    public Axis[] HistoryYAxes { get; } =
    [
        new Axis
        {
            Name = LocalizationManager.Text("History_Bytes"),
            MinLimit = 0,
            Labeler = v => FormatMB(v)
        }
    ];

    public Axis[] HistoryXAxes { get; }

    private ObservableCollection<string> _processNames = ["ALL"];
    public ObservableCollection<string> ProcessNames
    {
        get => _processNames;
        set => SetField(ref _processNames, value);
    }

    private string _selectedProcess = "ALL";
    public string SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
                _ = LoadDataAsync();
        }
    }

    private bool _isHourly = true;
    public bool IsHourly
    {
        get => _isHourly;
        set
        {
            if (SetField(ref _isHourly, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDaily)));
                _ = LoadDataAsync();
            }
        }
    }

    public bool IsDaily
    {
        get => !_isHourly;
        set
        {
            if (value != !_isHourly)
            {
                IsHourly = !value;
            }
        }
    }

    public HistoryViewModel(PipeClient client)
    {
        _client = client;

        HistorySeries =
        [
            new ColumnSeries<ObservableValue>
            {
                Values = _receivedPoints,
                Name = LocalizationManager.Text("History_Received"),
                Fill = new SolidColorPaint(new SKColor(0x2C, 0xCA, 0xF6, 0xE6)),
                MaxBarWidth = 20
            },
            new ColumnSeries<ObservableValue>
            {
                Values = _sentPoints,
                Name = LocalizationManager.Text("History_Sent"),
                Fill = new SolidColorPaint(new SKColor(0x4B, 0xE5, 0xA2, 0xD8)),
                MaxBarWidth = 20
            }
        ];

        HistoryXAxes =
        [
            new Axis
            {
                Labels = _labels,
                LabelsRotation = 45,
                TextSize = 10
            }
        ];

        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyChartTheme();
    }

    internal void ApplyMarketingFixture()
    {
        _labels.Clear();
        _receivedPoints.Clear();
        _sentPoints.Clear();

        string[] labels = ["08:00", "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00"];
        double[] received = [420, 680, 930, 760, 1180, 1440, 1320, 1710, 1580, 1860];
        double[] sent = [110, 160, 240, 180, 310, 380, 340, 460, 410, 520];

        foreach (var label in labels)
            _labels.Add(label);
        foreach (var value in received)
            _receivedPoints.Add(new ObservableValue(value));
        foreach (var value in sent)
            _sentPoints.Add(new ObservableValue(value));

        ProcessNames = new ObservableCollection<string>
        {
            "ALL",
            "msedge.exe",
            "steam.exe",
            "OneDrive.exe",
            "Teams.exe"
        };
    }

    public async Task LoadDataAsync()
    {
        // Cancel and dispose any previous in-flight load to prevent
        // interleaved chart updates and avoid leaking WaitHandles
        var oldCts = _loadCts;
        oldCts?.Cancel();
        oldCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();

        if (_client.State != ConnectionState.Connected)
            return;

        var command = _isHourly ? "STATS_HOURLY" : "STATS_DAILY";
        var processFilter = _selectedProcess == "ALL" ? "" : $" {_selectedProcess}";
        var response = await _client.SendCommandAsync($"{command}{processFilter}");
        if (response is null || cts.Token.IsCancellationRequested) return;

        try
        {
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(response, JsonOptions);
            if (entries is null || cts.Token.IsCancellationRequested) return;

            _receivedPoints.Clear();
            _sentPoints.Clear();
            _labels.Clear();

            foreach (var entry in entries)
            {
                var label = FormatLabel(entry.Period, _isHourly);
                _labels.Add(label);
                _receivedPoints.Add(new ObservableValue(entry.BytesReceived / (1024.0 * 1024)));
                _sentPoints.Add(new ObservableValue(entry.BytesSent / (1024.0 * 1024)));
            }
        }
        catch (JsonException)
        {
            // Invalid response — ignore
        }

        if (!cts.Token.IsCancellationRequested)
            await LoadProcessListAsync();
    }

    private async Task LoadProcessListAsync()
    {
        var response = await _client.SendCommandAsync("STATS_TOP");
        if (response is null) return;

        try
        {
            var entries = JsonSerializer.Deserialize<List<TopEntry>>(response, JsonOptions);
            if (entries is null) return;

            var names = new ObservableCollection<string> { "ALL" };
            foreach (var entry in entries)
                names.Add(entry.ProcessName);

            ProcessNames = names;
        }
        catch (JsonException)
        {
            // Invalid response — ignore
        }
    }

    private static string FormatLabel(string period, bool isHourly)
    {
        if (isHourly && period.Length >= 13)
        {
            // "2024-01-15T14" → "14:00"
            return period[11..] + ":00";
        }
        if (!isHourly && period.Length >= 10)
        {
            // "2024-01-15" → "Jan 15"
            if (DateTime.TryParse(period, out var dt))
                return dt.ToString("MMM dd");
        }
        return period;
    }

    private static string FormatMB(double value)
    {
        if (value >= 1024)
            return $"{value / 1024:F1} GB";
        return $"{value:F0} MB";
    }

    private void OnThemeChanged(AppTheme _)
    {
        ApplyChartTheme();
    }

    private void ApplyChartTheme()
    {
        var colors = ThemeManager.GetChartColors();
        var labelPaint = new SolidColorPaint(new SKColor(colors.LabelR, colors.LabelG, colors.LabelB));
        var gridPaint = new SolidColorPaint(new SKColor(colors.GridR, colors.GridG, colors.GridB)) { StrokeThickness = 1 };

        foreach (var axis in HistoryYAxes)
        {
            axis.LabelsPaint = labelPaint;
            axis.NamePaint = labelPaint;
            axis.SeparatorsPaint = gridPaint;
        }

        foreach (var axis in HistoryXAxes)
        {
            axis.LabelsPaint = labelPaint;
            axis.NamePaint = labelPaint;
        }
    }

    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private class HistoryEntry
    {
        public string ProcessName { get; set; } = string.Empty;
        public string Period { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
    }

    private class TopEntry
    {
        public string ProcessName { get; set; } = string.Empty;
        public long TotalBytesReceived { get; set; }
        public long TotalBytesSent { get; set; }
    }
}
