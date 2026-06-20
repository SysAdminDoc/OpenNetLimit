using OpenNetLimit.UI.Services;
using Xunit;

namespace OpenNetLimit.Tests;

public class LocalizationManagerTests
{
    [Fact]
    public void SetCulture_LocalizesKnownKeysAndFallsBackToEnglish()
    {
        var originalCulture = LocalizationManager.CurrentCultureCode;

        try
        {
            LocalizationManager.SetCulture("es-MX", save: false);

            Assert.Equal("es", LocalizationManager.CurrentCultureCode);
            Assert.Equal("Descarga", LocalizationManager.Text("Chart_Download"));
            Assert.Equal("Tema: Oscuro", LocalizationManager.Format("ThemeDisplay", LocalizationManager.Text("Theme_Dark")));

            LocalizationManager.SetCulture("fr-FR", save: false);

            Assert.Equal("en", LocalizationManager.CurrentCultureCode);
            Assert.Equal("Download", LocalizationManager.Text("Chart_Download"));
            Assert.Equal("Unknown_Key", LocalizationManager.Text("Unknown_Key"));
        }
        finally
        {
            LocalizationManager.SetCulture(originalCulture, save: false);
        }
    }
}
