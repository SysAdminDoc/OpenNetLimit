using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.ViewModels;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class RuleDraftTests
{
    private const long KB = RuleDraft.BytesPerKilobyte;
    private const long MB = RuleDraft.BytesPerMegabyte;

    [Fact]
    public void NewLimit_BuildsBothDirectionsInKilobytes()
    {
        var result = new RuleDraft { ProcessName = " chrome.exe ", DownloadKbps = "512", UploadKbps = "64" }.Build(null);

        var rule = Assert.IsType<BandwidthRule>(result.Rule);
        Assert.Null(result.ErrorKey);
        Assert.Equal("chrome", rule.ProcessName);
        Assert.Null(rule.ProcessPath);
        Assert.Equal(RuleAction.Limit, rule.Action);
        Assert.Equal(RuleDirection.Both, rule.Direction);
        Assert.Equal(512 * KB, rule.DownloadBytesPerSecond);
        Assert.Equal(64 * KB, rule.UploadBytesPerSecond);
        Assert.Null(rule.Schedule);
        Assert.Null(rule.Quota);
        Assert.True(rule.Enabled);
    }

    [Fact]
    public void PathCopiedFromExplorer_LosesItsQuotes()
    {
        var rule = new RuleDraft { ProcessPath = "  \"C:\\Games\\game.exe\" ", Action = RuleAction.Block }.Build(null).Rule;

        Assert.Equal(@"C:\Games\game.exe", rule!.ProcessPath);
        Assert.Null(rule.ProcessName);
    }

    [Theory]
    [InlineData("", "", "RuleEditor_NeedTarget")]
    [InlineData("chrome", "0", "RuleEditor_NeedLimit")]
    [InlineData("chrome", "12a", "RuleEditor_BadNumber")]
    [InlineData("chrome", "-5", "RuleEditor_BadNumber")]
    [InlineData("chrome", "99999999999999999999", "RuleEditor_BadNumber")]
    public void BadFields_SayWhatToFix(string program, string download, string errorKey)
    {
        var result = new RuleDraft { ProcessName = program, DownloadKbps = download, UploadKbps = "" }.Build(null);

        Assert.Null(result.Rule);
        Assert.Equal(errorKey, result.ErrorKey);
    }

    [Fact]
    public void BlockRule_IgnoresTheSpeedBoxes()
    {
        var result = new RuleDraft { ProcessName = "game", Action = RuleAction.Block, DownloadKbps = "not a number" }.Build(null);

        Assert.Equal(RuleAction.Block, result.Rule!.Action);
    }

    [Theory]
    [InlineData("9:00", "17:30")]
    [InlineData("22:00", "06:00")]
    public void Hours_BecomeASchedule(string start, string end)
    {
        var rule = new RuleDraft { ProcessName = "steam", DownloadKbps = "100", ScheduleStart = start, ScheduleEnd = end }
            .Build(null).Rule!;

        Assert.Equal(TimeOnly.Parse(start), rule.Schedule!.StartTime);
        Assert.Equal(TimeOnly.Parse(end), rule.Schedule.EndTime);
        Assert.Null(rule.Schedule.ActiveDays);
    }

    [Theory]
    [InlineData("9:00", "")]
    [InlineData("", "17:00")]
    [InlineData("25:00", "17:00")]
    [InlineData("9am", "5pm")]
    public void HalfOrUnreadableHours_AreRefused(string start, string end)
    {
        var result = new RuleDraft { ProcessName = "steam", DownloadKbps = "100", ScheduleStart = start, ScheduleEnd = end }.Build(null);

        Assert.Equal("RuleEditor_BadTime", result.ErrorKey);
    }

    [Fact]
    public void DaysWithoutHours_ApplyAllDayOnThoseDays()
    {
        var draft = new RuleDraft { ProcessName = "steam", DownloadKbps = "100" };
        draft.Days[(int)DayOfWeek.Saturday] = true;
        draft.Days[(int)DayOfWeek.Sunday] = true;

        var schedule = draft.Build(null).Rule!.Schedule!;

        Assert.Null(schedule.StartTime);
        Assert.Equal(new[] { DayOfWeek.Sunday, DayOfWeek.Saturday }, schedule.ActiveDays);
    }

    [Fact]
    public void Edit_KeepsWhatTheEditorDoesNotShowAndExactByteValues()
    {
        var original = new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 1000,
            UploadBytesPerSecond = 3 * KB,
            Priority = 7,
            ProfileName = "Work",
            RemotePortFilter = 443,
            ActiveUntil = new DateTime(2030, 1, 1)
        };

        var draft = RuleDraft.From(original);
        draft.UploadKbps = "4";
        var rule = draft.Build(original).Rule!;

        Assert.Equal("1", draft.DownloadKbps);
        Assert.Equal(original.Id, rule.Id);
        Assert.Equal(1000, rule.DownloadBytesPerSecond);
        Assert.Equal(4 * KB, rule.UploadBytesPerSecond);
        Assert.Equal(7, rule.Priority);
        Assert.Equal("Work", rule.ProfileName);
        Assert.Equal(443, rule.RemotePortFilter);
        Assert.Equal(new DateTime(2030, 1, 1), rule.ActiveUntil);
        Assert.Equal(1000, original.DownloadBytesPerSecond);
        Assert.Equal(3 * KB, original.UploadBytesPerSecond);
    }

    [Fact]
    public void Edit_OfAnUploadOnlyRule_ShowsDownloadAsUnlimited()
    {
        var original = new BandwidthRule
        {
            ProcessName = "obs64",
            Direction = RuleDirection.Upload,
            DownloadBytesPerSecond = 9 * KB,
            UploadBytesPerSecond = 2 * KB
        };

        var draft = RuleDraft.From(original);
        var rule = draft.Build(original).Rule!;

        Assert.Equal("0", draft.DownloadKbps);
        Assert.Equal(RuleDirection.Both, rule.Direction);
        Assert.Equal(0, rule.DownloadBytesPerSecond);
        Assert.Equal(2 * KB, rule.UploadBytesPerSecond);
    }

    [Fact]
    public void Edit_RoundTripsScheduleAndQuota()
    {
        var original = new BandwidthRule
        {
            ProcessName = "onedrive",
            UploadBytesPerSecond = 512 * KB,
            Schedule = new RuleSchedule { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), ActiveDays = [DayOfWeek.Monday] },
            Quota = new QuotaConfig
            {
                LimitBytes = 2 * 1024 * MB, Period = QuotaPeriod.Weekly, OnExceeded = QuotaAction.Block, WarningPercent = 90
            }
        };

        var draft = RuleDraft.From(original);
        var rule = draft.Build(original).Rule!;

        Assert.Equal("09:00", draft.ScheduleStart);
        Assert.True(draft.Days[(int)DayOfWeek.Monday]);
        Assert.Equal("2048", draft.QuotaMegabytes);
        Assert.Equal(new TimeOnly(9, 0), rule.Schedule!.StartTime);
        Assert.Equal(new[] { DayOfWeek.Monday }, rule.Schedule.ActiveDays);
        Assert.Equal(2 * 1024 * MB, rule.Quota!.LimitBytes);
        Assert.Equal(QuotaPeriod.Weekly, rule.Quota.Period);
        Assert.Equal(QuotaAction.Block, rule.Quota.OnExceeded);
        Assert.Equal(90, rule.Quota.WarningPercent);
    }

    [Fact]
    public void ClearingTheHoursAndDays_RemovesTheSchedule()
    {
        var original = new BandwidthRule
        {
            ProcessName = "steam",
            DownloadBytesPerSecond = KB,
            Schedule = new RuleSchedule { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) }
        };
        var draft = RuleDraft.From(original);
        draft.ScheduleStart = draft.ScheduleEnd = " ";

        Assert.Null(draft.Build(original).Rule!.Schedule);
    }

    [Fact]
    public void UncheckingTheCap_RemovesTheQuota()
    {
        var original = new BandwidthRule { ProcessName = "steam", DownloadBytesPerSecond = KB, Quota = new QuotaConfig { LimitBytes = MB } };
        var draft = RuleDraft.From(original);
        draft.HasQuota = false;

        Assert.Null(draft.Build(original).Rule!.Quota);
    }

    [Theory]
    [InlineData("0", QuotaAction.Throttle, "10", "RuleEditor_NeedQuota")]
    [InlineData("", QuotaAction.Block, "10", "RuleEditor_NeedQuota")]
    [InlineData("100", QuotaAction.Throttle, "0", "RuleEditor_NeedThrottle")]
    [InlineData("100", QuotaAction.Throttle, "", "RuleEditor_NeedThrottle")]
    public void BadQuotaFields_SayWhatToFix(string megabytes, QuotaAction action, string throttle, string errorKey)
    {
        var result = new RuleDraft
        {
            ProcessName = "steam", DownloadKbps = "100", HasQuota = true,
            QuotaMegabytes = megabytes, QuotaAction = action, ThrottleKbps = throttle
        }.Build(null);

        Assert.Equal(errorKey, result.ErrorKey);
    }

    [Fact]
    public void WarnOnlyCap_IgnoresTheThrottleBox()
    {
        var quota = new RuleDraft
        {
            ProcessName = "steam", DownloadKbps = "100", HasQuota = true,
            QuotaMegabytes = "500", QuotaAction = QuotaAction.WarnOnly, ThrottleKbps = "nope"
        }.Build(null).Rule!.Quota!;

        Assert.Equal(500 * MB, quota.LimitBytes);
        Assert.Equal(QuotaAction.WarnOnly, quota.OnExceeded);
        Assert.Equal(80, quota.WarningPercent);
    }
}

public sealed class AlertDraftTests
{
    [Fact]
    public void NewAlert_DefaultsToOneMegabyteAndFiveMinutes()
    {
        var alert = new AlertDraft { Name = " Big download ", Direction = RuleDirection.Download }.Build(null).Alert!;

        Assert.Equal("Big download", alert.Name);
        Assert.Null(alert.ProcessName);
        Assert.Equal(1024 * RuleDraft.BytesPerKilobyte, alert.ThresholdBytesPerSecond);
        Assert.Equal(300, alert.CooldownSeconds);
        Assert.Equal(RuleDirection.Download, alert.Direction);
        Assert.NotEqual(Guid.Empty, alert.Id);
    }

    [Theory]
    [InlineData("0", "5", "AlertEditor_NeedThreshold")]
    [InlineData("fast", "5", "AlertEditor_NeedThreshold")]
    [InlineData("100", "soon", "RuleEditor_BadNumber")]
    [InlineData("100", "99999999999", "RuleEditor_BadNumber")]
    public void BadFields_SayWhatToFix(string threshold, string cooldown, string errorKey)
    {
        var result = new AlertDraft { ThresholdKbps = threshold, CooldownMinutes = cooldown }.Build(null);

        Assert.Null(result.Alert);
        Assert.Equal(errorKey, result.ErrorKey);
    }

    [Fact]
    public void Edit_KeepsTheIdAndExactValues()
    {
        var original = new BandwidthAlertRule
        {
            ProcessPath = @"C:\Tools\obs64.exe", ThresholdBytesPerSecond = 1500, CooldownSeconds = 90, Enabled = false
        };

        var draft = AlertDraft.From(original);
        var alert = draft.Build(original).Alert!;

        Assert.Equal("2", draft.ThresholdKbps);
        Assert.Equal("2", draft.CooldownMinutes);
        Assert.Equal(original.Id, alert.Id);
        Assert.Equal(1500, alert.ThresholdBytesPerSecond);
        Assert.Equal(90, alert.CooldownSeconds);
        Assert.Equal(@"C:\Tools\obs64.exe", alert.ProcessPath);
        Assert.False(alert.Enabled);
    }
}
