using System.Windows;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI;

public partial class App : System.Windows.Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        LocalizationManager.ApplySavedCulture();
        ThemeManager.ApplySavedTheme();

        if (SetupWizard.IsFirstRun)
        {
            var wizard = new SetupWizard();
            wizard.ShowDialog();
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
