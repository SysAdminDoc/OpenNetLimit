using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class RuleEngineTests
{
    [Fact]
    public void AddRule_FindsMatchingRule()
    {
        var engine = new RuleEngine();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000
        };

        engine.AddRule(rule);
        var match = engine.FindMatchingRule("chrome", null);

        Assert.NotNull(match);
        Assert.Equal(100_000, match.DownloadBytesPerSecond);
    }

    [Fact]
    public void FindMatchingRule_ByPath_Matches()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            DownloadBytesPerSecond = 50_000
        });

        var match = engine.FindMatchingRule("chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        Assert.NotNull(match);
    }

    [Fact]
    public void FindMatchingRule_CaseInsensitive()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "Chrome" });

        Assert.NotNull(engine.FindMatchingRule("chrome", null));
    }

    [Fact]
    public void DisabledRule_NotMatched()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "chrome", Enabled = false });

        Assert.Null(engine.FindMatchingRule("chrome", null));
    }

    [Fact]
    public void RemoveRule_NoLongerMatches()
    {
        var engine = new RuleEngine();
        var rule = new BandwidthRule { ProcessName = "chrome" };
        engine.AddRule(rule);

        engine.RemoveRule(rule.Id);

        Assert.Null(engine.FindMatchingRule("chrome", null));
    }

    [Fact]
    public void HigherPriority_MatchedFirst()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000,
            Priority = 1
        });
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 50_000,
            Priority = 10
        });

        var match = engine.FindMatchingRule("chrome", null);
        Assert.Equal(50_000, match?.DownloadBytesPerSecond);
    }

    [Fact]
    public void GetAllRules_ReturnsAll()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "a" });
        engine.AddRule(new BandwidthRule { ProcessName = "b" });

        Assert.Equal(2, engine.GetAllRules().Count);
    }

    [Fact]
    public void WildcardPath_MatchesAnyDirectory()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessPath = @"*\chrome.exe",
            DownloadBytesPerSecond = 50_000
        });

        var match = engine.FindMatchingRule("chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        Assert.NotNull(match);
    }

    [Fact]
    public void WildcardPath_DoesNotMatchWrongExe()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessPath = @"*\chrome.exe"
        });

        Assert.Null(engine.FindMatchingRule("firefox",
            @"C:\Program Files\Mozilla Firefox\firefox.exe"));
    }

    [Fact]
    public void QuestionMarkWildcard_MatchesSingleChar()
    {
        var rule = new BandwidthRule { ProcessPath = @"C:\app?.exe" };
        Assert.True(rule.MatchesProcess("app", @"C:\appX.exe"));
        Assert.False(rule.MatchesProcess("app", @"C:\appXY.exe"));
    }
}
