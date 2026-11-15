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
    private System.Drawing.Icon? _trayIconImage;
    private Forms.ToolStripMenuItem? _showTrayItem;
    private Forms.ToolStripMenuItem? _exitTrayItem;
    private Forms.ToolStripMenuItem? _pauseTrayItem;
    private Forms.ToolStripMenuItem? _stayPausedTrayItem;

    public MainWindow()
    {
        ApplyFlowDirection();
        InitializeComponent();
        DataContext = _viewModel;
        if (MarketingCapture.IsEnabled && MarketingCapture.View == "history")
            MainTabs.SelectedIndex = 1;
        _viewModel.BandwidthAlertRaised += OnBandwidthAlertRaised;
        _viewModel.TrayNoticeRaised += OnTrayNotice;
        _viewModel.EngineStateChanged += UpdateTrayPauseState;
        LocalizationManager.CultureChanged += OnCultureChanged;
        if (!MarketingCapture.IsEnabled)
            InitializeTrayIcon();
        StateChanged += OnStateChanged;
        Closed += OnClosed;
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        _showTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_Show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(_showTrayItem);
        menu.Items.Add("-");
        _pauseTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_Pause"), null, async (_, _) =>
        {
            if (_viewModel.IsPaused)
                await _viewModel.ResumeAsync();
            else
                await _viewModel.PauseAsync(stayPaused: false);
        });
        menu.Items.Add(_pauseTrayItem);
        _stayPausedTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_StayPaused"), null,
            async (_, _) => await _viewModel.PauseAsync(!_viewModel.StayPaused));
        menu.Items.Add(_stayPausedTrayItem);
        menu.Items.Add("-");
        // Connection and admin state aren't observable, so the items are brought up to date as the menu opens.
        menu.Opening += (_, _) => UpdateTrayPauseState();
        _exitTrayItem = new Forms.ToolStripMenuItem(LocalizationManager.Text("Tray_Exit"), null, (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayIconImage?.Dispose();
            _trayIconImage = null;
            System.Windows.Application.Current.Shutdown();
        });
        menu.Items.Add(_exitTrayItem);

        var executablePath = Environment.ProcessPath;
        _trayIconImage = executablePath is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            : null;

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "OpenNetLimit",
            Icon = _trayIconImage ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        UpdateTrayPauseState();
    }

    private void UpdateTrayPauseState()
    {
        if (_trayIcon is null || _pauseTrayItem is null || _stayPausedTrayItem is null)
            return;

        var paused = _viewModel.IsPaused;
        var canControl = _viewModel.CanControlEngine;
        var hint = _viewModel.IsAdmin ? null : LocalizationManager.Text("Menu_AdminRequired");

        _pauseTrayItem.Text = LocalizationManager.Text(paused ? "Tray_Resume" : "Tray_Pause");
        _pauseTrayItem.Enabled = canControl;
        _pauseTrayItem.ToolTipText = hint;
        _stayPausedTrayItem.Text = LocalizationManager.Text("Tray_StayPaused");
        _stayPausedTrayItem.Checked = _viewModel.StayPaused;
        _stayPausedTrayItem.Enabled = canControl && paused;
        _stayPausedTrayItem.ToolTipText = hint;
        _trayIcon.Text = paused ? LocalizationManager.Text("Tray_TooltipPaused") : "OpenNetLimit";
    }

    private void OnTrayNotice(TrayNotice notice) =>
        _trayIcon?.ShowBalloonTip(
            5000,
            notice.ResolveTitle(),
            notice.ResolveBody(),
            notice.IsWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

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
        _viewModel.TrayNoticeRaised -= OnTrayNotice;
        _viewModel.EngineStateChanged -= UpdateTrayPauseState;
        LocalizationManager.CultureChanged -= OnCultureChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
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

    private void OnToggleUpdates(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleUpdates();
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenReleasePage();
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
        UpdateTrayPauseState();
        ApplyFlowDirection();
    }

    private void ApplyFlowDirection()
    {
        FlowDirection = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? System.Windows.FlowDirection.RightToLeft
            : System.Windows.FlowDirection.LeftToRight;
    }
}
