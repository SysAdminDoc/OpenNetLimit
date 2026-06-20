using System.Net;
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

    [Fact]
    public void Schedule_TimeWindow_InsideIsActive()
    {
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Schedule = new RuleSchedule
            {
                StartTime = new TimeOnly(0, 0),
                EndTime = new TimeOnly(23, 59)
            }
        };
        Assert.True(rule.IsActiveNow());
    }

    [Fact]
    public void Schedule_DayOfWeek_TodayIsActive()
    {
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Schedule = new RuleSchedule
            {
                ActiveDays = [DateTime.Now.DayOfWeek]
            }
        };
        Assert.True(rule.IsActiveNow());
    }

    [Fact]
    public void Schedule_WrongDay_IsInactive()
    {
        var wrongDay = (DayOfWeek)(((int)DateTime.Now.DayOfWeek + 1) % 7);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Schedule = new RuleSchedule
            {
                ActiveDays = [wrongDay]
            }
        };
        Assert.False(rule.IsActiveNow());
    }

    [Fact]
    public void ExportImport_RoundTrips()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 100_000 });
        engine.AddRule(new BandwidthRule { ProcessName = "firefox", DownloadBytesPerSecond = 50_000 });

        var exported = engine.ExportRules();

        var engine2 = new RuleEngine();
        engine2.ImportRules(exported);
        Assert.Equal(2, engine2.GetAllRules().Count);
    }

    [Fact]
    public void ImportRules_Merge_AddsToExisting()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "chrome" });

        engine.ImportRules("""[{"processName":"firefox"}]""", replace: false);
        Assert.Equal(2, engine.GetAllRules().Count);
    }

    [Fact]
    public void ImportRules_Replace_ClearsExisting()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "chrome" });

        engine.ImportRules("""[{"processName":"firefox"}]""", replace: true);
        Assert.Single(engine.GetAllRules());
        Assert.Equal("firefox", engine.GetAllRules()[0].ProcessName);
    }

    [Fact]
    public void FindMatchingRule_WithRemotePort_FiltersCorrectly()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            RemotePortFilter = 443,
            DownloadBytesPerSecond = 50_000
        });

        var match = engine.FindMatchingRule("chrome", null, IPAddress.Parse("8.8.8.8"), 443, "Tcp");
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("chrome", null, IPAddress.Parse("8.8.8.8"), 80, "Tcp");
        Assert.Null(noMatch);
    }

    [Fact]
    public void FindMatchingRule_WithProtocol_FiltersCorrectly()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "app",
            ProtocolFilter = "Tcp",
            DownloadBytesPerSecond = 10_000
        });

        var match = engine.FindMatchingRule("app", null, null, null, "Tcp");
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("app", null, null, null, "Udp");
        Assert.Null(noMatch);
    }

    [Fact]
    public void FindMatchingRule_WithCidr_FiltersCorrectly()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "app",
            RemoteAddressFilter = "10.0.0.0/8",
            DownloadBytesPerSecond = 10_000
        });

        var match = engine.FindMatchingRule("app", null, IPAddress.Parse("10.1.2.3"), null, null);
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("app", null, IPAddress.Parse("192.168.1.1"), null, null);
        Assert.Null(noMatch);
    }

    [Fact]
    public void FindMatchingRule_WithExactIp_FiltersCorrectly()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "app",
            RemoteAddressFilter = "8.8.8.8",
            DownloadBytesPerSecond = 10_000
        });

        var match = engine.FindMatchingRule("app", null, IPAddress.Parse("8.8.8.8"), null, null);
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("app", null, IPAddress.Parse("8.8.4.4"), null, null);
        Assert.Null(noMatch);
    }

    [Fact]
    public void MatchesConnection_NoFilters_MatchesAll()
    {
        var rule = new BandwidthRule { ProcessName = "app" };
        Assert.True(rule.MatchesConnection(IPAddress.Parse("1.2.3.4"), 443, "Tcp"));
        Assert.False(rule.HasConnectionFilters);
    }

    [Fact]
    public void FindMatchingRule_WithIPv6Cidr64_FiltersCorrectly()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "app",
            RemoteAddressFilter = "2001:db8::/64",
            DownloadBytesPerSecond = 10_000
        });

        var match = engine.FindMatchingRule("app", null, IPAddress.Parse("2001:db8::1"), null, null);
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("app", null, IPAddress.Parse("2001:db9::1"), null, null);
        Assert.Null(noMatch);
    }

    [Fact]
    public void FindMatchingRule_WithIPv6Cidr128_MatchesExact()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "app",
            RemoteAddressFilter = "::1/128",
            DownloadBytesPerSecond = 10_000
        });

        var match = engine.FindMatchingRule("app", null, IPAddress.Parse("::1"), null, null);
        Assert.NotNull(match);

        var noMatch = engine.FindMatchingRule("app", null, IPAddress.Parse("::2"), null, null);
        Assert.Null(noMatch);
    }
}
