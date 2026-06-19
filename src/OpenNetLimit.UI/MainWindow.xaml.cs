using System.Windows;
using OpenNetLimit.UI.ViewModels;

namespace OpenNetLimit.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void OnSetLimit(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessViewModel process) return;

        var dialog = new SetLimitDialog
        {
            ProcessName = process.ProcessName,
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            var downBytes = dialog.DownloadKBps * 1024;
            var upBytes = dialog.UploadKBps * 1024;
            await _viewModel.SetLimitAsync(process.ProcessName, downBytes, upBytes);
        }
    }

    private async void OnRemoveLimit(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessViewModel process) return;
        await _viewModel.RemoveLimitAsync(process.ProcessName);
    }
}
