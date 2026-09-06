using System.Windows;
using System.Windows.Interop;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI;

public partial class App : System.Windows.Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (MarketingCapture.IsEnabled)
            System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        LocalizationManager.ApplySavedCulture();
        if (MarketingCapture.IsEnabled)
            ThemeManager.ApplyTheme(MarketingCapture.View == "live-light" ? AppTheme.Light : AppTheme.Dark);
        else
            ThemeManager.ApplySavedTheme();

        if (MarketingCapture.IsEnabled)
        {
            if (MarketingCapture.View == "setup")
            {
                new SetupWizard().Show();
                return;
            }

            if (MarketingCapture.View == "limit")
            {
                new SetLimitDialog { ProcessName = "steam.exe" }.Show();
                return;
            }
        }

        if (!MarketingCapture.IsEnabled && SetupWizard.IsFirstRun)
        {
            var wizard = new SetupWizard();
            wizard.ShowDialog();
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
