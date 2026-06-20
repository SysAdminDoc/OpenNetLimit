using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Forms = System.Windows.Forms;

namespace OpenNetLimit.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _showTrayItem;
    private Forms.ToolStripMenuItem? _exitTrayItem;

    public MainWindow()
    {
        ApplyFlowDirection();
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.BandwidthAlertRaised += OnBandwidthAlertRaised;
        LocalizationManager.CultureChanged += OnCultureChanged;
        InitializeTrayIcon();
        StateChanged += OnStateChanged;
        Closed += OnClosed;
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _showTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_Show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(_showTrayItem);
        menu.Items.Add("-");
        _exitTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_Exit"), null, (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            System.Windows.Application.Current.Shutdown();
        });
        menu.Items.Add(_exitTrayItem);

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
        LocalizationManager.CultureChanged -= OnCultureChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _viewModel.Dispose();
    }

    private void OnBandwidthAlertRaised(OpenNetLimit.Core.Models.BandwidthAlertEvent alert)
    {
        _trayIcon?.ShowBalloonTip(
            5000,
            LocalizationManager.Text("Alert_BalloonTitle"),
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

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleLanguage();
    }

    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is System.Windows.Controls.TabControl && HistoryTab.IsSelected)
        {
            await _viewModel.HistoryViewModel.LoadDataAsync();
        }
    }

    private async void OnHistoryRefresh(object sender, RoutedEventArgs e)
    {
        await _viewModel.HistoryViewModel.LoadDataAsync();
    }

    private void OnCultureChanged()
    {
        if (_showTrayItem is not null)
            _showTrayItem.Text = LocalizationManager.Text("Tray_Show");
        if (_exitTrayItem is not null)
            _exitTrayItem.Text = LocalizationManager.Text("Tray_Exit");
        ApplyFlowDirection();
    }

    private void ApplyFlowDirection()
    {
        FlowDirection = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
    }
}
