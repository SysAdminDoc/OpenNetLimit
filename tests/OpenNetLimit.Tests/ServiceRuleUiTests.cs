using System.ComponentModel;
using OpenNetLimit.Core.Models;
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
