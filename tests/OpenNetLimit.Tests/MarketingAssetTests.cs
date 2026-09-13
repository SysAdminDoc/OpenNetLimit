using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class MarketingAssetTests
{
    private const string HeroPath = "assets/marketing/readme-hero.png";
    private const string HeroMarkdown = "![OpenNetLimit dashboard and per-app bandwidth controls](assets/marketing/readme-hero.png)";

    [Fact]
    public void ReadmeStartsWithExactlyOneHeroReference()
    {
        var repoRoot = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md")).TrimStart('\uFEFF');

        Assert.StartsWith(HeroMarkdown, readme, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(readme, Regex.Escape(HeroPath), RegexOptions.CultureInvariant).Cast<Match>());
        Assert.DoesNotContain("assets/screenshots/02-live-traffic.png", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionHeroMatchesTheSelectedArchivedFinal()
    {
        var repoRoot = FindRepoRoot();
        var production = Path.Combine(repoRoot, "assets", "marketing", "readme-hero.png");
        var socialPreview = Path.Combine(repoRoot, "assets", "marketing", "social-preview.png");
        var selected = Path.Combine(repoRoot, "assets", "concepts", "2026-09-12-readme-hero", "readme-hero-final.png");

        Assert.Equal(Sha256(selected), Sha256(production));
        Assert.Equal(Sha256(selected), Sha256(socialPreview));

        var header = File.ReadAllBytes(production);
        Assert.True(header.Length > 26);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8]);
        Assert.Equal(1280, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(640, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
        Assert.Equal(2, header[25]);
    }

    [Fact]
    public void HeroCopyContainsNoReleaseNumber()
    {
        var repoRoot = FindRepoRoot();
        var copy = File.ReadAllText(Path.Combine(repoRoot, "assets", "marketing", "readme-hero-copy.txt"));

        Assert.DoesNotMatch(new Regex(@"\bv?\d+\.\d+\.\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), copy);
        Assert.DoesNotContain("version", copy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeroDecisionArchivePreservesSourcesAndBothCandidates()
    {
        var archive = Path.Combine(FindRepoRoot(), "assets", "concepts", "2026-09-12-readme-hero");

        Assert.True(File.Exists(Path.Combine(archive, "README-before.md")));
        Assert.True(File.Exists(Path.Combine(archive, "source", "previous-social-preview.png")));
        Assert.True(File.Exists(Path.Combine(archive, "source", "approved-logo-master.png")));
        Assert.True(File.Exists(Path.Combine(archive, "readme-hero-candidate-01-continuity.png")));
        Assert.True(File.Exists(Path.Combine(archive, "readme-hero-candidate-02-product-led.png")));
        Assert.True(File.Exists(Path.Combine(archive, "review", "reference-and-candidates.png")));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OpenNetLimit.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the OpenNetLimit repository root.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
