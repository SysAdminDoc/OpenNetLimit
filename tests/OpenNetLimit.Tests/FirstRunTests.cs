using OpenNetLimit.UI.Services;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>
/// Starts the real app on a hidden desktop with a throwaway settings folder.
/// </summary>
public sealed class FirstRunTests : IDisposable
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(45);
    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), $"onl-ui-{Guid.NewGuid():N}");
    private static string AppPath => Path.Combine(AppContext.BaseDirectory, "OpenNetLimit.UI.exe");

    private Dictionary<string, string> AppEnvironment => new()
    {
        [UiPaths.SettingsDirectoryVariable] = _settingsDir,
        ["OPENNETLIMIT_UI_CULTURE"] = "en-US"
    };

    private static bool IsWizard(string title) => title.Contains("First-Run", StringComparison.Ordinal);
    private static bool IsMainWindow(string title) => title == "OpenNetLimit";

    [Fact]
    public void ClosingTheWizard_OpensTheMainWindow()
    {
        using var app = PrivateDesktopApp.Start(AppPath, AppEnvironment);

        var wizard = app.WaitForWindow(IsWizard, WindowTimeout);
        Assert.True(wizard.HasValue, "The first-run wizard never appeared.");
        app.Close(wizard.Value.Handle);

        var main = app.WaitForWindow(IsMainWindow, WindowTimeout);
        Assert.False(app.HasExited, "Closing the first-run wizard shut the app down.");
        Assert.True(main.HasValue, "The main window never appeared after the wizard closed.");
    }

    [Fact]
    public void CompletedSetup_GoesStraightToTheMainWindow()
    {
        Directory.CreateDirectory(_settingsDir);
        File.WriteAllText(Path.Combine(_settingsDir, "setup-complete"), DateTime.UtcNow.ToString("O"));
        using var app = PrivateDesktopApp.Start(AppPath, AppEnvironment);

        var main = app.WaitForWindow(IsMainWindow, WindowTimeout);

        Assert.True(main.HasValue, "The main window never appeared.");
        Assert.DoesNotContain(app.Windows(), window => IsWizard(window.Title));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_settingsDir))
                Directory.Delete(_settingsDir, recursive: true);
        }
        catch (IOException)
        {
            // The app may still hold a settings file for a moment after it's terminated.
        }
    }
}
