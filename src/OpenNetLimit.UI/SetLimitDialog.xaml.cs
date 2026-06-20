using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenNetLimit.UI.Services;
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

    public string ProcessLabel => LocalizationManager.Format("SetLimit_ProcessLabel", ProcessName);

    public long DownloadKBps { get; private set; }
    public long UploadKBps { get; private set; }

    public SetLimitDialog()
    {
        InitializeComponent();
        DataContext = this;
        ErrorBorderBrush.Freeze();
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not WpfTextBox box) return;
        _defaultBorderBrush ??= box.BorderBrush;

        var isValid = long.TryParse(box.Text.Trim(), out var val) && val >= 0;
        box.BorderBrush = isValid ? _defaultBorderBrush : ErrorBorderBrush;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (long.TryParse(DownloadBox.Text.Trim(), out var down) && down >= 0 &&
            long.TryParse(UploadBox.Text.Trim(), out var up) && up >= 0)
        {
            DownloadKBps = down;
            UploadKBps = up;
            DialogResult = true;
        }
        else
        {
            // Show inline error instead of a blocking MessageBox
            ErrorText.Visibility = Visibility.Visible;

            if (!(long.TryParse(DownloadBox.Text.Trim(), out var d) && d >= 0))
                DownloadBox.BorderBrush = ErrorBorderBrush;
            if (!(long.TryParse(UploadBox.Text.Trim(), out var u) && u >= 0))
                UploadBox.BorderBrush = ErrorBorderBrush;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
