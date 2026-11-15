using System.ComponentModel;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>
/// How the app shows svchost rows and puts limits on the services in them. These resolve text,
/// so they can't run while another test switches the language.
/// </summary>
[Collection(CultureSwitchingCollection.Name)]
public sealed class ServiceRuleUiTests
{
    [Theory]
    [InlineData("svchost.exe", "BITS,DoSvc", "svchost.exe (BITS, DoSvc)")]
    [InlineData("SvcHost", "BITS", "SvcHost (BITS)")]
    [InlineData("svchost", null, "svchost")]
    [InlineData("svchost", "", "svchost")]
    [InlineData("spoolsv", "Spooler", "spoolsv")]
    public void DisplayName_NamesTheServicesASvchostRuns(string process, string? services, string expected)
    {
        Assert.Equal(expected, ProcessRowText.DisplayName(process, List(services)));
    }

    [Fact]
    public void RowFor_KeepsAProcessesRow_ButNotForTheNextProcessWithItsId()
    {
        var seen = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var svchost = new ProcessViewModel { ProcessId = 200, ProcessName = "svchost", FirstSeenAt = seen };
        var rows = new Dictionary<uint, ProcessViewModel> { [200] = svchost };

        Assert.Same(svchost, ProcessRowText.RowFor(rows, new ProcessTrafficInfo { ProcessId = 200, ProcessName = "svchost", FirstSeenAt = seen }));
        Assert.Null(ProcessRowText.RowFor(rows, new ProcessTrafficInfo { ProcessId = 200, ProcessName = "chrome", FirstSeenAt = seen }));
        Assert.Null(ProcessRowText.RowFor(rows, new ProcessTrafficInfo { ProcessId = 200, ProcessName = "svchost", FirstSeenAt = seen.AddSeconds(3) }));
        Assert.Null(ProcessRowText.RowFor(rows, new ProcessTrafficInfo { ProcessId = 204, ProcessName = "svchost", FirstSeenAt = seen }));
    }

    [Fact]
    public void ProtectedReason_IsNullWhenRulesApply()
    {
        Assert.Null(ProcessRowText.ProtectedReason(new ProcessTrafficInfo { ProcessName = "svchost", Services = ["BITS"] }));
    }

    [Fact]
    public void ProtectedReason_NamesTheNetworkingServices()
    {
        var process = new ProcessTrafficInfo
        {
            ProcessName = "svchost", Services = ["Dnscache", "Dhcp", "BITS"],
            IsProtected = true, NetworkingServices = ["Dnscache", "Dhcp"]
        };

        Assert.Equal(LocalizationManager.Format("Process_ProtectedNetworking", "Dnscache, Dhcp"),
            ProcessRowText.ProtectedReason(process));
    }

    [Fact]
    public void ProtectedReason_ASvchostWhoseServicesArentKnownYet_SaysSo()
    {
        var process = new ProcessTrafficInfo { ProcessName = "svchost", IsProtected = true };

        Assert.Equal(LocalizationManager.Text("Process_ProtectedUnknown"), ProcessRowText.ProtectedReason(process));
    }

    [Theory]
    [InlineData("lsass")]
    [InlineData("svchost")]
    public void ProtectedReason_OtherwiseWindowsNeedsIt(string name)
    {
        // A svchost with services known and none of them networking can still be Windows' own.
        var process = new ProcessTrafficInfo
        {
            ProcessName = name, IsProtected = true, Services = name == "svchost" ? ["SamSs"] : null, NetworkingServices = []
        };

        Assert.Equal(LocalizationManager.Text("Process_ProtectedWindows"), ProcessRowText.ProtectedReason(process));
    }

    [Fact]
    public void LimitText_AProtectedProcess_ShowsNoLimitEvenWhenOneWouldMatch()
    {
        var process = new ProcessTrafficInfo { ProcessName = "svchost", IsProtected = true, DownloadLimitBytesPerSecond = 1024 };

        Assert.Equal(LocalizationManager.Text("Limit_Protected"), ProcessRowText.LimitText(process));
    }

    [Theory]
    [InlineData(2048L, null, "↓2")]
    [InlineData(null, 1024L, "↑1")]
    [InlineData(2048L, 1024L, "↓2 ↑1")]
    [InlineData(null, null, null)]
    [InlineData(0L, 0L, null)]
    public void LimitText_ShowsEachDirectionThatIsLimited(long? down, long? up, string? shape)
    {
        var process = new ProcessTrafficInfo { ProcessName = "steam", DownloadLimitBytesPerSecond = down, UploadLimitBytesPerSecond = up };

        var expected = shape is null
            ? LocalizationManager.Text("Limit_None")
            : string.Join(" ", shape.Split(' ').Select(p => p[0] + ProcessViewModel.FormatBytes(long.Parse(p[1..]) * 1024)));
        Assert.Equal(expected, ProcessRowText.LimitText(process));
    }

    [Fact]
    public void SharedNote_IsNullForAServiceRunningAlone()
    {
        Assert.Null(ProcessRowText.SharedNote("BITS", ["BITS"]));
    }

    [Fact]
    public void SharedNote_NamesTheOneServiceSharingTheProcess()
    {
        Assert.Equal(LocalizationManager.Format("SetLimit_SharedOne", "BITS", "DoSvc"),
            ProcessRowText.SharedNote("BITS", ["BITS", "DoSvc"]));
    }

    [Fact]
    public void SharedNote_NamesEveryOtherService_WhateverTheCase()
    {
        Assert.Equal(LocalizationManager.Format("SetLimit_SharedMany", "bits", "DoSvc, wuauserv"),
            ProcessRowText.SharedNote("bits", ["BITS", "DoSvc", "wuauserv"]));
    }

    private static readonly BandwidthRule AppNamedBits = new() { ProcessName = "bits", DownloadBytesPerSecond = 1 };
    private static readonly BandwidthRule BitsService = new() { ServiceName = "BITS", DownloadBytesPerSecond = 2 };
    private static readonly BandwidthRule DoSvcBlocked = new() { ServiceName = "DoSvc", Action = RuleAction.Block };
    private static readonly BandwidthRule DoSvcLimit = new() { ServiceName = "DoSvc", DownloadBytesPerSecond = 3 };
    private static readonly BandwidthRule Steam = new() { ProcessName = "steam", DownloadBytesPerSecond = 4 };
    private static readonly BandwidthRule[] Rules = [AppNamedBits, BitsService, DoSvcBlocked, DoSvcLimit, Steam];

    [Fact]
    public void ExistingLimit_ForAService_IsTheRuleOnThatService()
    {
        Assert.Same(BitsService, LimitMenu.ExistingLimit(Rules, "svchost.exe", "bits"));
        Assert.Same(DoSvcLimit, LimitMenu.ExistingLimit(Rules, "svchost", "DoSvc"));
        Assert.Null(LimitMenu.ExistingLimit(Rules, "svchost", "wuauserv"));
        var off = new BandwidthRule { ServiceName = "BITS", Enabled = false, DownloadBytesPerSecond = 5 };
        Assert.Same(BitsService, LimitMenu.ExistingLimit([off, BitsService], "svchost", "BITS"));
    }

    [Fact]
    public void ExistingLimit_ForAProgram_IsNeverAServiceRule()
    {
        // A program called bits.exe gets its own rule, not the one on the BITS service.
        Assert.Same(AppNamedBits, LimitMenu.ExistingLimit([BitsService, AppNamedBits], "bits.exe", null));
        Assert.Null(LimitMenu.ExistingLimit([BitsService], "bits", null));
        Assert.Same(Steam, LimitMenu.ExistingLimit(Rules, "Steam.exe", null));
    }

    [Fact]
    public void NewLimit_GoesOnTheServiceOrTheProgram()
    {
        var service = LimitMenu.NewLimit("svchost.exe", "BITS", 1024, 512);
        Assert.Equal("BITS", service.ServiceName);
        Assert.Null(service.ProcessName);
        Assert.Equal((1024L, 512L, RuleAction.Limit), (service.DownloadBytesPerSecond, service.UploadBytesPerSecond, service.Action));

        var program = LimitMenu.NewLimit("steam.exe", null, 2048, 0);
        Assert.Equal("steam", program.ProcessName);
        Assert.Null(program.ServiceName);
        Assert.Equal(2048, program.DownloadBytesPerSecond);
    }

    private const string CopyPath = @"C:\Tools\svchost.exe";
    private static readonly string WindowsSvchost =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe");

    [Fact]
    public void NewLimit_ASvchostCopyOutsideWindows_GoesOnItsPath()
    {
        var copy = LimitMenu.NewLimit("svchost.exe", null, 1024, 0, CopyPath);

        Assert.Equal(("svchost", CopyPath), (copy.ProcessName, copy.ProcessPath));
        Assert.False(copy.TargetsServiceHost());
        var rules = new RuleEngine();
        rules.AddRule(copy);
        Assert.Single(rules.GetAllRules());
    }

    [Fact]
    public void NewLimit_AnyOtherProgram_StaysOnItsName()
    {
        Assert.Null(LimitMenu.NewLimit("steam", null, 1024, 0, @"C:\Games\steam.exe").ProcessPath);
        Assert.Null(LimitMenu.NewLimit("svchost", null, 1024, 0, null).ProcessPath);
        Assert.Null(LimitMenu.NewLimit("svchost", null, 1024, 0, WindowsSvchost).ProcessPath);
        Assert.Null(LimitMenu.NewLimit("svchost", "BITS", 1024, 0, CopyPath).ProcessPath);
    }

    [Fact]
    public void ExistingLimit_ForASvchostCopy_IsTheRuleOnItsPath()
    {
        var onCopy = new BandwidthRule { ProcessName = "svchost", ProcessPath = CopyPath, DownloadBytesPerSecond = 2 };
        var onOther = new BandwidthRule { ProcessPath = @"C:\Other\svchost.exe", DownloadBytesPerSecond = 3 };
        var oldByName = new BandwidthRule { ProcessName = "svchost", DownloadBytesPerSecond = 1 };
        BandwidthRule[] rules = [onCopy, onOther, oldByName];

        Assert.Same(onCopy, LimitMenu.ExistingLimit(rules, "svchost", null, @"c:\tools\SVCHOST.exe"));
        Assert.Same(onOther, LimitMenu.ExistingLimit(rules, "svchost", null, @"C:\Other\svchost.exe"));
        // An older rule on the name reaches every copy that has no rule of its own.
        Assert.Same(oldByName, LimitMenu.ExistingLimit(rules, "svchost", null, @"C:\New\svchost.exe"));
        Assert.Null(LimitMenu.ExistingLimit([onCopy, onOther], "svchost", null, @"C:\New\svchost.exe"));
        // The service reaches Windows' own svchost, and one whose path it hasn't read yet, only
        // through a service in it, so no rule on the name or a copy's path is its limit.
        Assert.Null(LimitMenu.ExistingLimit(rules, "svchost", null, WindowsSvchost));
        Assert.Null(LimitMenu.ExistingLimit(rules, "svchost", null, null));
    }

    [Fact]
    public void ExistingLimit_ARuleThatsOffOrOutsideItsHours_IsSkipped()
    {
        var off = new BandwidthRule { ProcessName = "steam", Enabled = false, DownloadBytesPerSecond = 1 };
        var ended = new BandwidthRule { ProcessName = "steam", ActiveUntil = DateTime.UtcNow.AddDays(-1), DownloadBytesPerSecond = 2 };
        var steam = new BandwidthRule { ProcessName = "steam", DownloadBytesPerSecond = 3 };

        // Changing either would leave the app at whatever limit applies now.
        Assert.Same(steam, LimitMenu.ExistingLimit([off, ended, steam], "steam", null, @"C:\Games\steam.exe"));
        Assert.Null(LimitMenu.ExistingLimit([off, ended], "steam", null, null));
    }

    [Fact]
    public void ExistingLimit_ARulesPatternThatMissesTheApp_IsntItsLimit()
    {
        var steamOnD = new BandwidthRule { ProcessName = "steam", ProcessPath = @"D:\Other\*", DownloadBytesPerSecond = 1 };

        Assert.Null(LimitMenu.ExistingLimit([steamOnD], "steam", null, @"C:\Games\steam.exe"));
        Assert.Same(steamOnD, LimitMenu.ExistingLimit([steamOnD], "steam", null, @"D:\Other\steam.exe"));
    }

    [Fact]
    public void ExistingLimit_ARuleWithNoName_CountsOnlyOnTheAppsExactPath()
    {
        var onPath = new BandwidthRule { ProcessPath = @"C:\Games\steam.exe", DownloadBytesPerSecond = 1 };
        var inGames = new BandwidthRule { ProcessPath = @"C:\Games\*", DownloadBytesPerSecond = 2 };

        Assert.Same(onPath, LimitMenu.ExistingLimit([inGames, onPath], "steam", null, @"c:\games\STEAM.EXE"));
        // A folder rule covers other apps too, so Set limit adds one of the app's own.
        Assert.Null(LimitMenu.ExistingLimit([inGames], "steam", null, @"C:\Games\steam.exe"));
        Assert.Null(LimitMenu.ExistingLimit([onPath], "steam", null, null));
    }

    [Fact]
    public void ExistingLimit_ForASvchostCopy_IsTheRuleInForceWhenAnOlderOneComesFirst()
    {
        // The service tries rules in list order, so a rule on the name ahead of the copy's own
        // is the one that limits it, and changing the copy's would change nothing.
        var oldByName = new BandwidthRule { ProcessName = "svchost", DownloadBytesPerSecond = 1 };
        var onCopy = new BandwidthRule { ProcessName = "svchost", ProcessPath = CopyPath, DownloadBytesPerSecond = 2 };
        var inTools = new BandwidthRule { ProcessName = "svchost", ProcessPath = @"C:\Tools\*", DownloadBytesPerSecond = 3 };

        Assert.Same(oldByName, LimitMenu.ExistingLimit([oldByName, onCopy], "svchost", null, CopyPath));
        Assert.Same(inTools, LimitMenu.ExistingLimit([inTools, onCopy], "svchost", null, CopyPath));
        Assert.Same(onCopy, LimitMenu.ExistingLimit([onCopy, oldByName, inTools], "svchost", null, CopyPath));
    }

    [Fact]
    public void ExistingLimit_ARuleOnAnotherExactPath_IsLeftToThatProgram()
    {
        var steamOnD = new BandwidthRule { ProcessName = "steam", ProcessPath = @"D:\steam.exe", DownloadBytesPerSecond = 1 };
        var steamInGames = new BandwidthRule { ProcessName = "steam", ProcessPath = @"C:\Games\*", DownloadBytesPerSecond = 2 };

        Assert.Null(LimitMenu.ExistingLimit([steamOnD], "steam", null, @"C:\Games\steam.exe"));
        Assert.Same(steamInGames, LimitMenu.ExistingLimit([steamOnD, steamInGames], "steam", null, @"C:\Games\steam.exe"));
        Assert.Same(steamOnD, LimitMenu.ExistingLimit([steamOnD, steamInGames], "steam", null, @"d:\STEAM.EXE"));
        // Until the service has read where the app runs from, the name is all there is.
        Assert.Same(steamOnD, LimitMenu.ExistingLimit([steamOnD, steamInGames], "steam", null, null));
    }

    [Fact]
    public void RulesFor_WithNoPathYet_TakesEveryRuleOnTheName()
    {
        var steamOnD = new BandwidthRule { ProcessName = "steam", ProcessPath = @"D:\steam.exe", DownloadBytesPerSecond = 1 };
        var steam = new BandwidthRule { ProcessName = "steam", DownloadBytesPerSecond = 2 };

        Assert.Equal(new[] { steamOnD, steam }, LimitMenu.RulesFor([steamOnD, steam], "steam", null, null));
        Assert.Equal(new[] { steamOnD, steam }, LimitMenu.RulesFor([steamOnD, steam], "steam", null, @"d:\STEAM.exe"));
    }

    [Fact]
    public void RulesFor_ARuleOnACopysPath_StaysOffEveryOtherSvchost()
    {
        var onCopy = new BandwidthRule { ProcessName = "svchost", ProcessPath = CopyPath, DownloadBytesPerSecond = 1024 };
        BandwidthRule[] rules = [onCopy, BitsService];

        Assert.Equal(new[] { BitsService }, LimitMenu.RulesFor(rules, "svchost", ["BITS"], WindowsSvchost));
        Assert.Empty(LimitMenu.RulesFor(rules, "svchost", null, @"C:\Other\svchost.exe"));
        Assert.Equal(new[] { onCopy }, LimitMenu.RulesFor(rules, "svchost", null, CopyPath));
    }

    [Fact]
    public void RulesFor_ANameRuleWithAFolderPattern_IsStillTheProgramsOwn()
    {
        var steamInGames = new BandwidthRule { ProcessName = "steam", ProcessPath = @"C:\Games\*", DownloadBytesPerSecond = 1024 };
        var steamElsewhere = new BandwidthRule { ProcessName = "steam", ProcessPath = @"D:\steam.exe", DownloadBytesPerSecond = 1024 };

        Assert.Equal(new[] { steamInGames }, LimitMenu.RulesFor([steamInGames, steamElsewhere], "steam", null, @"C:\Games\steam.exe"));
        Assert.Empty(LimitMenu.RulesFor([steamInGames], "steam", null, @"D:\Other\steam.exe"));
    }

    [Fact]
    public void RulesFor_ARuleWithNoNameOnTheAppsExactPath_IsItsOwn()
    {
        var onPath = new BandwidthRule { ProcessPath = @"C:\Games\steam.exe", DownloadBytesPerSecond = 1 };
        var inGames = new BandwidthRule { ProcessPath = @"C:\Games\*", DownloadBytesPerSecond = 2 };
        BandwidthRule[] rules = [onPath, inGames];

        Assert.Equal(new[] { onPath }, LimitMenu.RulesFor(rules, "steam", null, @"c:\GAMES\steam.exe"));
        Assert.Empty(LimitMenu.RulesFor(rules, "steam", null, @"D:\steam.exe"));
        Assert.Empty(LimitMenu.RulesFor(rules, "steam", null, null));
        // A blank name from the API or a rules file is no name.
        var blankName = new BandwidthRule { ProcessName = "  ", ProcessPath = @"C:\Games\steam.exe" };
        Assert.Equal(new[] { blankName }, LimitMenu.RulesFor([blankName], "steam", null, @"C:\Games\steam.exe"));
    }

    [Fact]
    public void RulesFor_ARuleThatsOff_IsStillRemoved()
    {
        // It's still the app's, and it would limit the app again once turned back on.
        var off = new BandwidthRule { ProcessName = "steam", Enabled = false, DownloadBytesPerSecond = 1 };

        Assert.Equal(new[] { off }, LimitMenu.RulesFor([off], "steam", null, @"C:\Games\steam.exe"));
    }

    [Fact]
    public void RulesFor_WindowsSvchost_LeavesRulesOnTheNameOrACopy()
    {
        // None of them reach it, so they're left for the copies they were made for.
        BandwidthRule[] rules =
        [
            new() { ProcessName = "svchost", DownloadBytesPerSecond = 1 },
            new() { ProcessName = "svchost", ProcessPath = @"C:\Tools\*", DownloadBytesPerSecond = 2 },
            new() { ProcessName = "svchost", ProcessPath = CopyPath, DownloadBytesPerSecond = 3 },
            BitsService
        ];

        Assert.Equal(new[] { BitsService }, LimitMenu.RulesFor(rules, "svchost", ["BITS"], WindowsSvchost));
        Assert.Equal(new[] { BitsService }, LimitMenu.RulesFor(rules, "svchost", ["BITS"], null));
    }

    [Fact]
    public void RulesFor_ASvchost_AreTheRulesOnItsServices()
    {
        Assert.Equal(new[] { BitsService, DoSvcBlocked, DoSvcLimit }, LimitMenu.RulesFor(Rules, "svchost.exe", ["dosvc", "BITS"]));
        Assert.Empty(LimitMenu.RulesFor(Rules, "svchost", null));
    }

    [Fact]
    public void RulesFor_LeavesAllowRulesInPlace()
    {
        // Deleting one would let the folder rule it exempts steam from slow steam down.
        var folder = new BandwidthRule { ProcessPath = @"C:\Games\*", DownloadBytesPerSecond = 1024 };
        var steamAllowed = new BandwidthRule { ProcessName = "steam", Action = RuleAction.Allow };
        var bitsAllowed = new BandwidthRule { ServiceName = "BITS", Action = RuleAction.Allow };
        var bitsLimit = new BandwidthRule { ServiceName = "BITS", DownloadBytesPerSecond = 1024 };
        BandwidthRule[] rules = [folder, steamAllowed, bitsAllowed, bitsLimit];

        Assert.Empty(LimitMenu.RulesFor(rules, "steam.exe", null));
        Assert.Equal(new[] { bitsLimit }, LimitMenu.RulesFor(rules, "svchost", ["BITS"]));
    }

    [Fact]
    public void RulesFor_AProgram_AreItsOwnRulesByName()
    {
        Assert.Equal(new[] { AppNamedBits }, LimitMenu.RulesFor(Rules, "bits.exe", null));
        Assert.Equal(new[] { Steam }, LimitMenu.RulesFor(Rules, "STEAM", []));
    }

    [Fact]
    public void Draft_AServiceAlone_BuildsAServiceRule()
    {
        var result = new RuleDraft { ServiceName = " BITS ", DownloadKbps = "256" }.Build(null);

        var rule = Assert.IsType<BandwidthRule>(result.Rule);
        Assert.Equal("BITS", rule.ServiceName);
        Assert.Null(rule.ProcessName);
        Assert.Null(rule.ProcessPath);
    }

    [Theory]
    [InlineData("svchost", "")]
    [InlineData("", @"C:\Windows\System32\svchost.exe")]
    public void Draft_AServiceBesideAProgramOrPath_IsRefused(string program, string path)
    {
        var result = new RuleDraft { ProcessName = program, ProcessPath = path, ServiceName = "BITS", DownloadKbps = "256" }.Build(null);

        Assert.Null(result.Rule);
        Assert.Equal(("RuleEditor_ServiceAlone", DraftField.Service), (result.ErrorKey, result.Field));
    }

    [Theory]
    [InlineData("svchost")]
    [InlineData(" SVCHOST.exe ")]
    public void Draft_ANewRuleOnSvchostByName_AsksForTheService(string program)
    {
        var result = new RuleDraft { ProcessName = program, ServiceName = "  ", DownloadKbps = "256" }.Build(null);

        Assert.Null(result.Rule);
        Assert.Equal(("RuleEditor_UseService", DraftField.Service), (result.ErrorKey, result.Field));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\svchost.exe")]
    [InlineData("\"C:\\Windows\\SysWOW64\\SVCHOST.EXE\"")]
    public void Draft_ANewRuleOnSvchostByPath_AsksForTheService(string path)
    {
        var result = new RuleDraft { ProcessPath = path, DownloadKbps = "256" }.Build(null);

        Assert.Null(result.Rule);
        Assert.Equal(("RuleEditor_UseService", DraftField.Service), (result.ErrorKey, result.Field));
    }

    [Fact]
    public void Draft_ASvchostCopyByNameAndPath_IsAnOrdinaryProgram()
    {
        var result = new RuleDraft { ProcessName = "svchost", ProcessPath = CopyPath, DownloadKbps = "256" }.Build(null);

        Assert.Null(result.ErrorKey);
        Assert.Equal(("svchost", CopyPath), (result.Rule!.ProcessName, result.Rule.ProcessPath));

        // Taking the path off, or pointing it at Windows' own, makes it a rule on svchost again.
        var copyRule = result.Rule;
        var noPath = RuleDraft.From(copyRule);
        noPath.ProcessPath = "";
        Assert.Equal("RuleEditor_UseService", noPath.Build(copyRule).ErrorKey);
        var onWindows = RuleDraft.From(copyRule);
        onWindows.ProcessPath = WindowsSvchost;
        Assert.Equal("RuleEditor_UseService", onWindows.Build(copyRule).ErrorKey);
    }

    [Fact]
    public void SetLimit_OnACopyWithAnOlderRuleOnTheName_ChangesTheLimitThatApplies()
    {
        // Saved before service rules existed, so it comes in through the service's own file.
        var file = Path.Combine(Path.GetTempPath(), $"onl-copy-rule-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{"version":1,"rules":[{"processName":"svchost","downloadBytesPerSecond":1024}]}""");
        var rules = new RuleEngine();
        try
        {
            rules.LoadRules(file);
        }
        finally
        {
            File.Delete(file);
        }

        // What Set limit does with the list the service sends back.
        var existing = LimitMenu.ExistingLimit(rules.GetAllRules(), "svchost", null, CopyPath);
        Assert.NotNull(existing);
        existing.DownloadBytesPerSecond = 4096;
        rules.UpdateRule(existing);
        Assert.Equal(4096, rules.FindMatchingRule("svchost", CopyPath)!.DownloadBytesPerSecond);

        // A new rule on the copy's path would sit behind the older one and change nothing.
        rules.AddRule(LimitMenu.NewLimit("svchost", null, 8192, 0, CopyPath));
        Assert.Equal(4096, rules.FindMatchingRule("svchost", CopyPath)!.DownloadBytesPerSecond);
    }

    [Fact]
    public void Draft_AnOldRuleOnSvchost_CanStillBeEditedAndTurnedOff()
    {
        // Saved before service rules existed. It never matches, but it mustn't get stuck on.
        var old = new BandwidthRule { ProcessName = "svchost", DownloadBytesPerSecond = 1024 };
        var draft = RuleDraft.From(old);
        draft.Enabled = false;

        var result = draft.Build(old);

        Assert.Null(result.ErrorKey);
        Assert.False(result.Rule!.Enabled);
        Assert.Equal("svchost", result.Rule.ProcessName);
    }

    [Fact]
    public void Draft_AServiceRuleMovedOntoSvchostByName_AsksForTheService()
    {
        var old = new BandwidthRule { ServiceName = "BITS", DownloadBytesPerSecond = 1024 };
        var draft = RuleDraft.From(old);
        draft.ServiceName = "";
        draft.ProcessName = "svchost";

        Assert.Equal("RuleEditor_UseService", draft.Build(old).ErrorKey);
    }

    [Fact]
    public void Draft_AServiceRule_RoundTrips()
    {
        var old = new BandwidthRule { ServiceName = "DoSvc", DownloadBytesPerSecond = 300, Name = "Delivery Optimization" };

        var draft = RuleDraft.From(old);
        var rule = draft.Build(old).Rule!;

        Assert.Equal("DoSvc", draft.ServiceName);
        Assert.Equal(string.Empty, draft.ProcessName);
        Assert.Equal(("DoSvc", (string?)null, 300L), (rule.ServiceName, rule.ProcessName, rule.DownloadBytesPerSecond));
        Assert.Equal(old.Id, rule.Id);
    }

    [Fact]
    public void Draft_ClearingTheService_TakesItOffTheRule()
    {
        var old = new BandwidthRule { ServiceName = "DoSvc", DownloadBytesPerSecond = 1024 };
        var draft = RuleDraft.From(old);
        draft.ServiceName = "";
        draft.ProcessName = "steam";

        var rule = draft.Build(old).Rule!;

        Assert.Null(rule.ServiceName);
        Assert.Equal("steam", rule.ProcessName);
    }

    [Fact]
    public void RuleRow_NamesAServiceAsOne()
    {
        var rule = new BandwidthRule { ServiceName = "BITS", DownloadBytesPerSecond = 1024 };

        Assert.Equal(LocalizationManager.Format("Rules_ServiceTarget", "BITS"), RuleRow.TargetOf(rule));
        Assert.Equal(@"C:\a.exe", RuleRow.TargetOf(new BandwidthRule { ProcessPath = @"C:\a.exe", ProcessName = "a" }));
        Assert.Equal("a", RuleRow.TargetOf(new BandwidthRule { ProcessName = "a" }));
    }

    [Fact]
    public void ProcessRow_AnEqualServiceListFromThePoll_ChangesNothing()
    {
        var row = new ProcessViewModel { ProcessId = 1532, ProcessName = "svchost" };
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Services = ["BITS", "DoSvc"];
        Assert.Equal(new string?[] { nameof(ProcessViewModel.Services), nameof(ProcessViewModel.DisplayName) }, changed);
        Assert.Equal("svchost (BITS, DoSvc)", row.DisplayName);

        changed.Clear();
        row.Services = new[] { "BITS", "DoSvc" };
        Assert.Empty(changed);

        row.Services = ["BITS"];
        Assert.Equal("svchost (BITS)", row.DisplayName);
        Assert.Equal(2, changed.Count);

        changed.Clear();
        row.Services = null;
        Assert.Equal(2, changed.Count);
        Assert.Equal("svchost", row.DisplayName);

        changed.Clear();
        row.Services = null;
        Assert.Empty(changed);
    }

    private static string[]? List(string? services) =>
        services is null ? null : services.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
