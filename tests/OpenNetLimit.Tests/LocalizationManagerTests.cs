using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using OpenNetLimit.UI.Services;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>
/// Tests that switch the app language. Many tests compare against the current language's text,
/// so these run on their own instead of beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureSwitchingCollection
{
    public const string Name = "Switches the app language";
}

[Collection(CultureSwitchingCollection.Name)]
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

    [Fact]
    public void EveryString_HasASpanishVersionWithTheSamePlaceholders()
    {
        var resources = new ResourceManager("OpenNetLimit.UI.Resources.Strings", typeof(LocalizationManager).Assembly);
        var english = Strings(resources, CultureInfo.InvariantCulture);
        var spanish = Strings(resources, CultureInfo.GetCultureInfo("es"));

        Assert.Empty(english.Keys.Except(spanish.Keys).Order());
        Assert.Empty(spanish.Keys.Except(english.Keys).Order());
        Assert.Empty(english.Keys
            .Where(key => !Placeholders(english[key]).SequenceEqual(Placeholders(spanish[key])))
            .Order());
    }

    private static Dictionary<string, string> Strings(ResourceManager resources, CultureInfo culture) =>
        resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => m.Value).Distinct().Order();
}
