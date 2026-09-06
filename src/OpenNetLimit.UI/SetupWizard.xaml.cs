using System.IO;
using System.Windows;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI;

public partial class SetupWizard : Window
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenNetLimit", "setup-complete");

    public static bool IsFirstRun => !File.Exists(MarkerPath);

    public SetupWizard()
    {
        InitializeComponent();
        UpdateStepVisuals();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex < Pages.Items.Count - 1)
        {
            Pages.SelectedIndex++;
            BtnBack.IsEnabled = true;
            if (Pages.SelectedIndex == Pages.Items.Count - 1)
                BtnNext.Content = LocalizationManager.Text("Action_Finish");
            UpdateStepVisuals();
        }
        else
        {
            MarkComplete();
            DialogResult = true;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex > 0)
        {
            Pages.SelectedIndex--;
            BtnNext.Content = LocalizationManager.Text("Action_Next");
            BtnBack.IsEnabled = Pages.SelectedIndex > 0;
            UpdateStepVisuals();
        }
    }

    private void UpdateStepVisuals()
    {
        var active = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
        var inactive = System.Windows.Media.Brushes.Transparent;
        StepOne.Background = Pages.SelectedIndex == 0 ? active : inactive;
        StepTwo.Background = Pages.SelectedIndex == 1 ? active : inactive;
        StepThree.Background = Pages.SelectedIndex == 2 ? active : inactive;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        MarkComplete();
        DialogResult = true;
    }

    private static void MarkComplete()
    {
        try
        {
            var dir = Path.GetDirectoryName(MarkerPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best-effort
        }
    }
}
