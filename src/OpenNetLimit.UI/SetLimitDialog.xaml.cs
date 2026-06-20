using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenNetLimit.UI.Services;
using MessageBox = System.Windows.MessageBox;

namespace OpenNetLimit.UI;

public partial class SetLimitDialog : Window, INotifyPropertyChanged
{
    private string _processName = string.Empty;

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
            MessageBox.Show(
                LocalizationManager.Text("SetLimit_InvalidMessage"),
                LocalizationManager.Text("SetLimit_InvalidTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
