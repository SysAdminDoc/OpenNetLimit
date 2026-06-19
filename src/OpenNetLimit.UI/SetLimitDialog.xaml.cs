using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace OpenNetLimit.UI;

public partial class SetLimitDialog : Window
{
    public string ProcessName { get; set; } = string.Empty;
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
            MessageBox.Show("Please enter valid non-negative numbers.", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
