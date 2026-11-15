using System.Globalization;
using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace OpenNetLimit.UI;

public partial class SetLimitDialog : Window, INotifyPropertyChanged
{
    private string _processName = string.Empty;
    private static readonly WpfBrush ErrorBorderBrush = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(0xE5, 0x39, 0x35));
    private WpfBrush? _defaultBorderBrush;

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (_processName == value) return;
            _processName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProcessLabel));
        }
    }

    private string? _label;

    public string ProcessLabel => _label ?? LocalizationManager.Format("SetLimit_ProcessLabel", ProcessName);

    public long DownloadKBps { get; private set; }
    public long UploadKBps { get; private set; }

    /// <summary>The whole-PC limit should count the local network too.</summary>
    public bool IncludeLan => IncludeLanBox.IsChecked == true;

    // Whether each box was typed in once the dialog showed its values. Retyping the number it
    // showed still counts: 1 over a 500 B/s limit's "1" means 1 KB/s.
    private bool _trackEdits;
    private bool _shownIncludeLan;

    /// <summary>The download box was typed in.</summary>
    public bool DownloadEdited { get; private set; }

    /// <summary>The upload box was typed in.</summary>
    public bool UploadEdited { get; private set; }

    /// <summary>The local network box ended up different from how it showed.</summary>
    public bool IncludeLanChanged => IncludeLan != _shownIncludeLan;

    /// <summary>The dialog for the limit every app shares, filled in with the one in effect.</summary>
    public static SetLimitDialog ForWholePc(WholePcLimit current)
    {
        var dialog = new SetLimitDialog { _label = LocalizationManager.Text("PcLimit_Scope") };
        dialog.Title = dialog.HeaderTitle.Text = LocalizationManager.Text("PcLimit_Title");
        dialog.HeaderSubtitle.Text = LocalizationManager.Text("PcLimit_Subtitle");
        dialog.ZeroHintText.Text = LocalizationManager.Text("PcLimit_ZeroHint");
        dialog.DownloadBox.Text = PcLimitText.Kilobytes(current.DownloadBytesPerSecond).ToString(CultureInfo.InvariantCulture);
        dialog.UploadBox.Text = PcLimitText.Kilobytes(current.UploadBytesPerSecond).ToString(CultureInfo.InvariantCulture);
        dialog.IncludeLanBox.IsChecked = current.IncludeLan;
        dialog.IncludeLanBox.Visibility = Visibility.Visible;
        dialog.Height += 34;
        dialog._shownIncludeLan = current.IncludeLan;
        dialog._trackEdits = true;
        return dialog;
    }

    /// <summary>A whole number of KB/s, 0 or more, that still fits once it's turned into bytes.</summary>
    internal static bool TryParseKBps(string text, out long kilobytes) =>
        long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out kilobytes)
        && kilobytes <= long.MaxValue / 1024;

    public SetLimitDialog()
    {
        InitializeComponent();
        DataContext = this;
        ErrorBorderBrush.Freeze();
        if (MarketingCapture.IsEnabled)
        {
            DownloadBox.Text = "8192";
            UploadBox.Text = "1024";
        }
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not WpfTextBox box) return;
        _defaultBorderBrush ??= box.BorderBrush;
        if (_trackEdits && ReferenceEquals(box, DownloadBox))
            DownloadEdited = true;
        else if (_trackEdits && ReferenceEquals(box, UploadBox))
            UploadEdited = true;

        box.BorderBrush = TryParseKBps(box.Text, out _) ? _defaultBorderBrush : ErrorBorderBrush;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryParseKBps(DownloadBox.Text, out var down) && TryParseKBps(UploadBox.Text, out var up))
        {
            DownloadKBps = down;
            UploadKBps = up;
            DialogResult = true;
        }
        else
        {
            // Show inline error instead of a blocking MessageBox
            ErrorText.Visibility = Visibility.Visible;

            if (!TryParseKBps(DownloadBox.Text, out _))
                DownloadBox.BorderBrush = ErrorBorderBrush;
            if (!TryParseKBps(UploadBox.Text, out _))
                UploadBox.BorderBrush = ErrorBorderBrush;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
