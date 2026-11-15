using System.Text.Json.Nodes;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>These resolve text, so they can't run while another test switches the language.</summary>
[Collection(CultureSwitchingCollection.Name)]
public sealed class RulesViewModelTests : IDisposable
{
    private readonly List<string> _sent = [];
    private readonly Dictionary<string, string?> _replies = new()
    {
        ["RULES"] = "[]",
        ["QUOTAS"] = "[]",
        ["ALERT_RULES"] = "[]"
    };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onl-rules-" + Guid.NewGuid().ToString("N"));

    public RulesViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // Replies by verb, like the service: anything not scripted is a plain success.
    private RulesViewModel Create() => new(command =>
    {
        _sent.Add(command);
        var verb = command.Split(' ', 2)[0];
        return Task.FromResult(_replies.TryGetValue(verb, out var reply) ? reply : """{"ok":true}""");
    });

    private IEnumerable<string> SentVerbs => _sent.Select(c => c.Split(' ', 2)[0]);

    [Fact]
    public async Task Refresh_ShowsRulesSortedWithTheirQuotaUse()
    {
        var chromeId = Guid.NewGuid();
        _replies["RULES"] = $$"""
            [{"id":"{{Guid.NewGuid()}}","processName":"steam","action":1,"enabled":false},
             {"id":"{{chromeId}}","processName":"chrome","action":0,"direction":1,"downloadBytesPerSecond":1048576,"uploadBytesPerSecond":4096,
              "quota":{"limitBytes":2147483648,"period":0} }]
            """;
        _replies["QUOTAS"] = $$"""[{"ruleId":"{{chromeId}}","usedBytes":1073741824,"isExceeded":false,"periodStart":"2026-11-15T00:00:00Z"}]""";
        _replies["ALERT_RULES"] = """[{"name":"Big download","direction":"Download","thresholdBytesPerSecond":52428800,"cooldownSeconds":600}]""";
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(new[] { "chrome", "steam" }, vm.Rules.Select(r => r.Target));
        var chrome = vm.Rules[0];
        Assert.Equal("↓ " + ProcessViewModel.FormatBytes(1048576), chrome.LimitDisplay);
        Assert.Equal(LocalizationManager.Format("Rules_QuotaDaily",
            ProcessViewModel.FormatTotalBytes(1073741824), ProcessViewModel.FormatTotalBytes(2147483648)), chrome.QuotaDisplay);
        var steam = vm.Rules[1];
        Assert.Equal(LocalizationManager.Text("Rules_ActionBlock"), steam.ActionDisplay);
        Assert.Equal(string.Empty, steam.LimitDisplay);
        Assert.Equal(LocalizationManager.Text("Rules_Off"), steam.StateDisplay);

        var alert = Assert.Single(vm.Alerts);
        Assert.Equal(RuleDirection.Download, alert.Alert.Direction);
        Assert.Equal(LocalizationManager.Text("Alerts_EveryApp"), alert.Target);
        Assert.Equal(ProcessViewModel.FormatBytes(52428800), alert.ThresholdDisplay);
        Assert.Equal(LocalizationManager.Format("Alerts_CooldownMinutes", "10"), alert.CooldownDisplay);
        Assert.False(vm.HasNoRules);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task UsedUpQuota_SaysSo()
    {
        var id = Guid.NewGuid();
        _replies["RULES"] = $$"""[{"id":"{{id}}","processName":"onedrive","uploadBytesPerSecond":1024,"quota":{"limitBytes":1024,"period":2} }]""";
        _replies["QUOTAS"] = $$"""[{"ruleId":"{{id}}","usedBytes":2048,"isExceeded":true}]""";
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LocalizationManager.Format("Rules_QuotaMonthly",
                ProcessViewModel.FormatTotalBytes(2048), ProcessViewModel.FormatTotalBytes(1024)) +
            " " + LocalizationManager.Text("Rules_QuotaUsedUp"), vm.Rules[0].QuotaDisplay);
    }

    [Fact]
    public void DaysInARow_ShowAsARange()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string Day(DayOfWeek day) => culture.DateTimeFormat.GetAbbreviatedDayName(day);
        var week = RuleRow.WeekOrder(culture).ToArray();
        var schedule = new RuleSchedule { ActiveDays = [week[0], week[1], week[2], week[3], week[5], week[6]] };

        var text = RuleRow.ScheduleOf(schedule);

        Assert.Equal(LocalizationManager.Format("Rules_TimeRange", Day(week[0]), Day(week[3])) +
            $", {Day(week[5])}, {Day(week[6])}", text);
    }

    [Fact]
    public async Task NoService_SaysSoInsteadOfShowingAnEmptyList()
    {
        _replies["RULES"] = null;
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LocalizationManager.Text("Rules_NoService"), vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.False(vm.HasNoRules);
        Assert.DoesNotContain("QUOTAS", SentVerbs);
    }

    [Fact]
    public async Task ServiceRefusal_ShowsItsReason()
    {
        _replies["RULES"] = """{"error":"Access denied"}""";
        var vm = Create();

        await vm.RefreshAsync();

        Assert.Equal(LocalizationManager.Format("Rules_ServiceError", "Access denied"), vm.Message);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public async Task EmptyList_ShowsTheEmptyText()
    {
        var vm = Create();

        await vm.RefreshAsync();

        Assert.True(vm.HasNoRules);
        Assert.True(vm.HasNoAlerts);
    }

    [Fact]
    public async Task SaveRule_SendsEnumsAsNumbersAndReturnsTheServiceError()
    {
        _replies["ADD_RULE"] = """{"error":"Rule must specify at least ProcessName or ProcessPath."}""";
        var vm = Create();

        var error = await vm.SaveRuleAsync(new BandwidthRule { ProcessName = "game", Action = RuleAction.Block }, isNew: true);

        Assert.Equal(LocalizationManager.Format("Rules_ServiceError", "Rule must specify at least ProcessName or ProcessPath."), error);
        var command = Assert.Single(_sent);
        Assert.StartsWith("ADD_RULE {", command);
        Assert.Contains("\"action\":1", command);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public async Task SaveRule_RefreshesAndSaysWhatChanged()
    {
        var vm = Create();
        var rule = new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1024 };

        Assert.Null(await vm.SaveRuleAsync(rule, isNew: false));

        Assert.Equal(new[] { "UPDATE_RULE", "RULES", "QUOTAS", "ALERT_RULES" }, SentVerbs);
        Assert.Equal(LocalizationManager.Format("Rules_Saved", "chrome"), vm.Message);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task UnreadableWriteReply_CountsAsNoService()
    {
        _replies["UPDATE_RULE"] = "[";
        var vm = Create();

        var error = await vm.SaveRuleAsync(new BandwidthRule { ProcessName = "chrome" }, isNew: false);

        Assert.Equal(LocalizationManager.Text("Rules_NoService"), error);
    }

    [Fact]
    public async Task Toggle_SendsTheRuleWithOnlyEnabledChanged()
    {
        var id = Guid.NewGuid();
        _replies["RULES"] = $$"""[{"id":"{{id}}","processName":"steam","downloadBytesPerSecond":2048,"priority":4}]""";
        var vm = Create();
        await vm.RefreshAsync();
        var row = vm.Rules[0];
        _sent.Clear();

        await vm.SetEnabledAsync(row, enabled: false);

        var sent = JsonNode.Parse(_sent[0]["UPDATE_RULE ".Length..])!;
        Assert.Equal(id, sent["id"]!.GetValue<Guid>());
        Assert.False(sent["enabled"]!.GetValue<bool>());
        Assert.Equal(2048, sent["downloadBytesPerSecond"]!.GetValue<long>());
        Assert.Equal(4, sent["priority"]!.GetValue<int>());
        Assert.True(row.Rule.Enabled);
        Assert.Equal(LocalizationManager.Format("Rules_TurnedOff", "steam"), vm.Message);
    }

    [Fact]
    public async Task DeleteThenUndo_PutsTheSameRuleBack()
    {
        var id = Guid.NewGuid();
        _replies["RULES"] = $$"""[{"id":"{{id}}","processName":"steam","downloadBytesPerSecond":2048}]""";
        var vm = Create();
        vm.CanEdit = true;
        await vm.RefreshAsync();
        var row = vm.Rules[0];
        _sent.Clear();

        await vm.DeleteRuleAsync(row);

        Assert.Equal($"REMOVE_RULE {id}", _sent[0]);
        Assert.True(vm.CanUndo);
        Assert.Equal(LocalizationManager.Format("Rules_Deleted", "steam"), vm.Message);
        _sent.Clear();

        await vm.UndoAsync();

        var restored = JsonNode.Parse(_sent[0]["ADD_RULE ".Length..])!;
        Assert.Equal(id, restored["id"]!.GetValue<Guid>());
        Assert.Equal(2048, restored["downloadBytesPerSecond"]!.GetValue<long>());
        Assert.False(vm.CanUndo);
        Assert.Equal(LocalizationManager.Format("Rules_Restored", "steam"), vm.Message);
    }

    [Fact]
    public async Task Delete_WhenTheReloadFails_SaysItWasDeletedAndWhyTheListIsOld()
    {
        _replies["RULES"] = $$"""[{"id":"{{Guid.NewGuid()}}","processName":"steam"}]""";
        var vm = Create();
        vm.CanEdit = true;
        await vm.RefreshAsync();
        _replies["RULES"] = null;

        await vm.DeleteRuleAsync(vm.Rules[0]);

        Assert.Equal(LocalizationManager.Format("Rules_Deleted", "steam") + " " + LocalizationManager.Text("Rules_NoService"), vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public async Task AlertsThatDontLoad_ShowTheErrorNotAnEmptyList()
    {
        _replies["ALERT_RULES"] = null;
        var vm = Create();

        await vm.RefreshAsync();

        Assert.False(vm.HasNoAlerts);
        Assert.True(vm.MessageIsError);
        Assert.Equal(LocalizationManager.Text("Rules_NoService"), vm.Message);
    }

    [Fact]
    public async Task AlertsThatStopLoading_KeepWhatTheyShowed()
    {
        _replies["ALERT_RULES"] = """[{"name":"Big download","thresholdBytesPerSecond":1024}]""";
        var vm = Create();
        await vm.RefreshAsync();
        _replies["ALERT_RULES"] = """{"error":"Access denied"}""";

        await vm.RefreshAsync();

        Assert.Equal("Big download", Assert.Single(vm.Alerts).Name);
        Assert.Equal(LocalizationManager.Format("Rules_ServiceError", "Access denied"), vm.Message);
    }

    [Fact]
    public async Task QuotasThatDontLoad_ShowTheError()
    {
        _replies["QUOTAS"] = """{"error":"Access denied"}""";
        var vm = Create();

        await vm.RefreshAsync();

        Assert.True(vm.MessageIsError);
        Assert.Equal(LocalizationManager.Format("Rules_ServiceError", "Access denied"), vm.Message);
    }

    [Fact]
    public void LoneTime_ShowsAsAlways()
    {
        // The engine only uses a time window with both ends.
        var text = RuleRow.ScheduleOf(new RuleSchedule { StartTime = new TimeOnly(9, 0) });

        Assert.Equal(LocalizationManager.Text("Rules_Always"), text);
    }

    [Fact]
    public async Task Undo_IsOffForAUserWhoCannotEdit()
    {
        _replies["RULES"] = $$"""[{"id":"{{Guid.NewGuid()}}","processName":"steam"}]""";
        var vm = Create();
        await vm.RefreshAsync();

        await vm.DeleteRuleAsync(vm.Rules[0]);

        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task SaveAlert_SendsTheAlertAndRefreshes()
    {
        var vm = Create();
        var alert = new BandwidthAlertRule { Name = "Upload spike", Direction = RuleDirection.Upload, ThresholdBytesPerSecond = 4096 };

        Assert.Null(await vm.SaveAlertAsync(alert, isNew: true));

        var sent = JsonNode.Parse(_sent[0]["ADD_ALERT_RULE ".Length..])!;
        Assert.Equal((int)RuleDirection.Upload, sent["direction"]!.GetValue<int>());
        Assert.Equal(4096, sent["thresholdBytesPerSecond"]!.GetValue<long>());
        Assert.Contains("ALERT_RULES", SentVerbs);
        Assert.Equal(LocalizationManager.Format("Alerts_Added", "Upload spike"), vm.Message);
    }

    [Fact]
    public async Task Export_WritesTheServiceListAsAnIndentedFile()
    {
        _replies["EXPORT_RULES"] = """[{"id":"8c0f8b0e-4a39-4f4e-9d55-4f7f6c7a0c11","processName":"chrome","action":1}]""";
        var vm = Create();
        var path = Path.Combine(_folder, "rules.json");

        await vm.ExportToAsync(path);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains('\n', text);
        var saved = Assert.IsType<JsonArray>(JsonNode.Parse(text));
        Assert.Equal("chrome", saved[0]!["processName"]!.GetValue<string>());
        Assert.Equal(LocalizationManager.Format("Rules_Exported", 1, "rules.json"), vm.Message);
    }

    [Fact]
    public async Task Export_WithoutTheService_WritesNothing()
    {
        _replies["EXPORT_RULES"] = null;
        var vm = Create();
        var path = Path.Combine(_folder, "rules.json");

        await vm.ExportToAsync(path);

        Assert.False(File.Exists(path));
        Assert.Equal(LocalizationManager.Text("Rules_NoService"), vm.Message);
    }

    [Fact]
    public async Task Import_SendsAnIndentedFileOnOneLine()
    {
        var path = Path.Combine(_folder, "rules.json");
        await File.WriteAllTextAsync(path, "[\r\n  {\r\n    \"processName\": \"steam\",\r\n    \"action\": \"Block\"\r\n  },\r\n  { \"processName\": \"chrome\" }\r\n]\r\n");
        var vm = Create();

        await vm.ImportFromAsync(path);

        var command = _sent[0];
        Assert.StartsWith("IMPORT_RULES [", command);
        Assert.DoesNotContain('\n', command);
        Assert.Equal(2, JsonNode.Parse(command["IMPORT_RULES ".Length..])!.AsArray().Count);
        Assert.Equal(LocalizationManager.Format("Rules_Imported", 2, "rules.json"), vm.Message);
    }

    [Fact]
    public async Task Import_TakesTheServiceRulesFileToo()
    {
        var path = Path.Combine(_folder, "saved.json");
        await File.WriteAllTextAsync(path, """{ "version": 1, "rules": [ { "processName": "steam" } ] }""");
        var vm = Create();

        await vm.ImportFromAsync(path);

        Assert.Equal("""IMPORT_RULES [{"processName":"steam"}]""", _sent[0]);
    }

    [Theory]
    [InlineData("""{ "processName": "steam" }""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""[{ "processName": "steam" }, null]""")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "version": 1, "rules": [null] }""")]
    public async Task Import_OfSomethingElse_SendsNothing(string content)
    {
        var path = Path.Combine(_folder, "other.json");
        await File.WriteAllTextAsync(path, content);
        var vm = Create();

        await vm.ImportFromAsync(path);

        Assert.Empty(_sent);
        Assert.Equal(LocalizationManager.Text("Rules_ImportNotList"), vm.Message);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public async Task Import_TooBigForOneRequest_SendsNothing()
    {
        var path = Path.Combine(_folder, "big.json");
        var rules = new JsonArray();
        for (int i = 0; i < 2000; i++)
            rules.Add(new JsonObject { ["processName"] = $"app{i:D4}", ["downloadBytesPerSecond"] = 1024 });
        await File.WriteAllTextAsync(path, rules.ToJsonString());
        var vm = Create();

        await vm.ImportFromAsync(path);

        Assert.Empty(_sent);
        Assert.Equal(LocalizationManager.Text("Rules_ImportTooBig"), vm.Message);
    }

    [Fact]
    public async Task Import_OfAMissingFile_SaysWhy()
    {
        var vm = Create();

        await vm.ImportFromAsync(Path.Combine(_folder, "missing.json"));

        Assert.Empty(_sent);
        Assert.True(vm.MessageIsError);
        Assert.StartsWith(LocalizationManager.Format("Rules_FileError", string.Empty), vm.Message);
    }
}
