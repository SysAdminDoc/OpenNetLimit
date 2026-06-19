using System.IO;
using System.Windows;

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
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex < Pages.Items.Count - 1)
        {
            Pages.SelectedIndex++;
            BtnBack.IsEnabled = true;
            if (Pages.SelectedIndex == Pages.Items.Count - 1)
                BtnNext.Content = "Finish";
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
            BtnNext.Content = "Next";
            BtnBack.IsEnabled = Pages.SelectedIndex > 0;
        }
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
