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
    public void ImportRules_ReadsEnumNamesAndStillSavesNumbers()
    {
        var engine = new RuleEngine();

        engine.ImportRules("""[{"processName":"game","action":"Block","quota":{"limitBytes":1024,"period":"Weekly"}}]""");

        var rule = Assert.Single(engine.GetAllRules());
        Assert.Equal(RuleAction.Block, rule.Action);
        Assert.Equal(QuotaPeriod.Weekly, rule.Quota!.Period);
        Assert.Contains("\"action\": 1", engine.ExportRules());
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
    public void ImportRules_RejectsRulesWithoutProcessIdentifier()
    {
        var engine = new RuleEngine();

        // Rule with neither ProcessName nor ProcessPath should throw
        var ex = Assert.Throws<ArgumentException>(() =>
            engine.ImportRules("""[{"downloadBytesPerSecond":1000}]"""));
        Assert.Contains("ProcessName", ex.Message);
    }

    [Fact]
    public void AddRule_WithAnIdAlreadyInUse_GetsItsOwnId()
    {
        var engine = new RuleEngine();
        var first = new BandwidthRule { ProcessName = "chrome" };
        engine.AddRule(first);
        var copy = new BandwidthRule { Id = first.Id, ProcessName = "firefox" };

        engine.AddRule(copy);

        Assert.NotEqual(first.Id, copy.Id);
        engine.RemoveRule(copy.Id);
        Assert.Equal("chrome", Assert.Single(engine.GetAllRules()).ProcessName);
    }

    [Fact]
    public void ImportRules_SameRulesTwice_KeepsOneCopyOfEach()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1000 });
        var exported = engine.ExportRules();
        var rule = engine.GetAllRules()[0];
        rule.DownloadBytesPerSecond = 5000;
        engine.UpdateRule(rule);

        engine.ImportRules(exported, replace: false);
        engine.ImportRules(exported, replace: false);

        var kept = Assert.Single(engine.GetAllRules());
        Assert.Equal(rule.Id, kept.Id);
        Assert.Equal(1000, kept.DownloadBytesPerSecond);
    }

    [Fact]
    public void ImportRules_TwoRulesSharingAnId_KeepsBothWithTheirOwnIds()
    {
        var engine = new RuleEngine();
        var id = Guid.NewGuid();

        engine.ImportRules($$"""[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"}]""");

        var rules = engine.GetAllRules();
        Assert.Equal(2, rules.Count);
        Assert.NotEqual(rules[0].Id, rules[1].Id);
    }

    [Fact]
    public void ImportRules_FileWithTwoRulesSharingAnId_ImportedTwice_KeepsTwoRules()
    {
        var engine = new RuleEngine();
        var id = Guid.NewGuid();
        var json = $$"""[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"}]""";

        engine.ImportRules(json);
        engine.ImportRules(json);

        Assert.Equal(["chrome", "firefox"], engine.GetAllRules().Select(r => r.ProcessName).Order());
    }

    [Fact]
    public void ImportRules_TwoFilesWithAZeroId_KeepBothRules()
    {
        var engine = new RuleEngine();
        const string zero = "00000000-0000-0000-0000-000000000000";

        engine.ImportRules($$"""[{"id":"{{zero}}","processName":"chrome"}]""");
        engine.ImportRules($$"""[{"id":"{{zero}}","processName":"firefox"}]""");

        Assert.Equal(["chrome", "firefox"], engine.GetAllRules().Select(r => r.ProcessName).Order());
    }

    [Fact]
    public void ImportRules_ANewIdNeverTakesOneALaterRuleInTheFileHas()
    {
        var id = Guid.NewGuid();
        var earlier = new RuleEngine();
        earlier.ImportRules($$"""[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"}]""");
        var givenToFirefox = earlier.GetAllRules().Single(r => r.ProcessName == "firefox").Id;

        var engine = new RuleEngine();
        engine.ImportRules($$"""[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"},{"id":"{{givenToFirefox}}","processName":"steam"}]""");

        var rules = engine.GetAllRules();
        Assert.Equal(3, rules.Count);
        Assert.Equal(givenToFirefox, rules.Single(r => r.ProcessName == "steam").Id);
        Assert.Equal(3, rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void LoadRules_TwoRulesSharingAnId_GetTheSameIdsEveryTime()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"onl-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{"version":1,"rules":[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"}]}""");
        try
        {
            var first = new RuleEngine();
            first.LoadRules(path);
            var second = new RuleEngine();
            second.LoadRules(path);

            Guid FirefoxId(RuleEngine engine) => engine.GetAllRules().Single(r => r.ProcessName == "firefox").Id;
            Assert.NotEqual(id, FirefoxId(first));
            Assert.Equal(FirefoxId(first), FirefoxId(second));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadRules_TwoRulesSharingAnId_CanBeEditedApart()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"onl-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{"version":1,"rules":[{"id":"{{id}}","processName":"chrome"},{"id":"{{id}}","processName":"firefox"}]}""");
        try
        {
            var engine = new RuleEngine();
            engine.LoadRules(path);

            var chrome = engine.GetAllRules().Single(r => r.ProcessName == "chrome");
            Assert.Equal(id, chrome.Id);
            engine.RemoveRule(chrome.Id);
            Assert.Equal("firefox", Assert.Single(engine.GetAllRules()).ProcessName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("""[{"processName":"chrome"},null]""")]
    public void ImportRules_ListWithANull_IsRefusedAndAddsNothing(string json)
    {
        var engine = new RuleEngine();

        var error = Assert.Throws<ArgumentException>(() => engine.ImportRules(json));

        Assert.Contains("empty", error.Message);
        Assert.Empty(engine.GetAllRules());
    }

    [Fact]
    public void LoadRules_FileWithANullEntry_LoadsTheRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onl-rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"version":1,"rules":[null,{"processName":"chrome"}]}""");
        try
        {
            var engine = new RuleEngine();
            engine.LoadRules(path);

            Assert.Equal("chrome", Assert.Single(engine.GetAllRules()).ProcessName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RulesOfEqualPriority_KeepTheirOrderWhenAnotherIsAdded()
    {
        // The first match wins, so the order decides which rule applies. List.Sort is unstable
        // past 16 items and shuffled equal rules on every add.
        var engine = new RuleEngine();
        for (var i = 0; i < 40; i++)
            engine.AddRule(new BandwidthRule { ProcessName = $"app{i}", DownloadBytesPerSecond = 1024 });
        var before = engine.GetAllRules().Select(r => r.Id).ToList();

        engine.AddRule(new BandwidthRule { ProcessName = "steam", Priority = 5, DownloadBytesPerSecond = 1024 });
        engine.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1024 });

        var after = engine.GetAllRules();
        Assert.Equal("steam", after[0].ProcessName);
        Assert.Equal(before, after.Skip(1).Take(40).Select(r => r.Id));
        Assert.Equal("chrome", after[^1].ProcessName);
    }

    [Fact]
    public void RuleRemovedAndAddedBack_GoesBackWhereItWas()
    {
        var engine = new RuleEngine();
        var rules = new[] { "first", "second", "third" }
            .Select(name => new BandwidthRule { Name = name, ProcessName = "chrome", DownloadBytesPerSecond = 1024 })
            .ToList();
        rules.ForEach(engine.AddRule);
        var second = engine.GetAllRules()[1];

        engine.RemoveRule(second.Id);
        engine.AddRule(second);

        Assert.Equal(["first", "second", "third"], engine.GetAllRules().Select(r => r.Name));
    }

    [Fact]
    public void RuleAddedBack_AfterTheOneItCameBeforeWentToo_GoesBeforeTheNext()
    {
        var engine = new RuleEngine();
        foreach (var name in new[] { "a", "b", "c", "d" })
            engine.AddRule(new BandwidthRule { Name = name, ProcessName = "chrome", DownloadBytesPerSecond = 1024 });
        var all = engine.GetAllRules();

        engine.RemoveRule(all[1].Id);
        engine.RemoveRule(all[2].Id);
        engine.AddRule(all[1]);

        Assert.Equal(["a", "b", "d"], engine.GetAllRules().Select(r => r.Name));
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
