using System.Drawing;
using System.Windows;
using OpenNetLimit.UI.ViewModels;
using Forms = System.Windows.Forms;

namespace OpenNetLimit.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.BandwidthAlertRaised += OnBandwidthAlertRaised;
        InitializeTrayIcon();
        StateChanged += OnStateChanged;
        Closed += OnClosed;
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            System.Windows.Application.Current.Shutdown();
        });

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "OpenNetLimit",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (_trayIcon is not null)
                _trayIcon.Visible = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.BandwidthAlertRaised -= OnBandwidthAlertRaised;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _viewModel.Dispose();
    }

    private void OnBandwidthAlertRaised(OpenNetLimit.Core.Models.BandwidthAlertEvent alert)
    {
        _trayIcon?.ShowBalloonTip(
            5000,
            "OpenNetLimit bandwidth alert",
            alert.Message,
            Forms.ToolTipIcon.Warning);
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

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleTheme();
    }
}
