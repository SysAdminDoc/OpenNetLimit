using System.Globalization;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>A rule built from the editor, or the resource key that says what to fix.</summary>
public sealed record RuleDraftResult(BandwidthRule? Rule, string? ErrorKey);

/// <summary>
/// The rule editor's fields as the user typed them. <see cref="Build"/> checks them and turns
/// them into a rule. It starts from a copy of the rule being edited, so what the editor doesn't
/// show (connection filters, active dates, priority, profile) survives an edit, and a number the
/// user left alone keeps its exact byte value instead of being rounded to whole KB or MB.
/// </summary>
public sealed class RuleDraft
{
    public const long BytesPerKilobyte = 1024;
    public const long BytesPerMegabyte = 1024 * 1024;
    private const long DefaultThrottleBytesPerSecond = 10 * BytesPerKilobyte;
    private const int DefaultWarningPercent = 80;
    private static readonly string[] TimeFormats = ["H:mm", "HH:mm"];

    public string Name { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public RuleAction Action { get; set; } = RuleAction.Limit;
    public string DownloadKbps { get; set; } = "0";
    public string UploadKbps { get; set; } = "0";
    public string ScheduleStart { get; set; } = string.Empty;
    public string ScheduleEnd { get; set; } = string.Empty;

    /// <summary>Checked days, indexed by <see cref="DayOfWeek"/>. None checked means every day.</summary>
    public bool[] Days { get; } = new bool[7];

    public string GroupName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool HasQuota { get; set; }
    public string QuotaMegabytes { get; set; } = "1024";
    public QuotaPeriod QuotaPeriod { get; set; } = QuotaPeriod.Daily;
    public QuotaAction QuotaAction { get; set; } = QuotaAction.Throttle;
    public string ThrottleKbps { get; set; } = "10";

    public static RuleDraft From(BandwidthRule rule)
    {
        var draft = new RuleDraft
        {
            Name = rule.Name,
            ProcessName = rule.ProcessName ?? string.Empty,
            ProcessPath = rule.ProcessPath ?? string.Empty,
            Action = rule.Action,
            // Show what the engine applies: a direction the rule leaves out isn't limited.
            DownloadKbps = Units(rule.Direction is RuleDirection.Upload ? 0 : rule.DownloadBytesPerSecond, BytesPerKilobyte),
            UploadKbps = Units(rule.Direction is RuleDirection.Download ? 0 : rule.UploadBytesPerSecond, BytesPerKilobyte),
            ScheduleStart = rule.Schedule?.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
            ScheduleEnd = rule.Schedule?.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
            GroupName = rule.GroupName ?? string.Empty,
            Enabled = rule.Enabled,
            HasQuota = rule.Quota is not null
        };
        foreach (var day in rule.Schedule?.ActiveDays ?? [])
            draft.Days[(int)day] = true;
        if (rule.Quota is { } quota)
        {
            draft.QuotaMegabytes = Units(quota.LimitBytes, BytesPerMegabyte);
            draft.QuotaPeriod = quota.Period;
            draft.QuotaAction = quota.OnExceeded;
            draft.ThrottleKbps = Units(quota.ThrottleBytesPerSecond, BytesPerKilobyte);
        }
        return draft;
    }

    /// <summary>Checks the fields and builds the rule, starting from <paramref name="original"/> when editing.</summary>
    public RuleDraftResult Build(BandwidthRule? original)
    {
        var rule = original?.Clone() ?? new BandwidthRule();
        var processName = ProcessName.Trim();
        // Explorer's "Copy as path" wraps the path in quotes.
        var processPath = ProcessPath.Trim().Trim('"').Trim();
        if (processName.Length == 0 && processPath.Length == 0)
            return Fail("RuleEditor_NeedTarget");

        rule.Name = Name.Trim();
        rule.ProcessName = processName.Length == 0 ? null : processName;
        rule.ProcessPath = processPath.Length == 0 ? null : processPath;
        rule.Action = Action;
        rule.Enabled = Enabled;
        rule.GroupName = GroupName.Trim() is { Length: > 0 } group ? group : null;

        if (Action == RuleAction.Limit)
        {
            var originalDown = original is { Direction: not RuleDirection.Upload } ? original.DownloadBytesPerSecond : 0;
            var originalUp = original is { Direction: not RuleDirection.Download } ? original.UploadBytesPerSecond : 0;
            if (!TryBytes(DownloadKbps, BytesPerKilobyte, originalDown, out var down) ||
                !TryBytes(UploadKbps, BytesPerKilobyte, originalUp, out var up))
                return Fail("RuleEditor_BadNumber");
            if (down == 0 && up == 0)
                return Fail("RuleEditor_NeedLimit");
            rule.Direction = RuleDirection.Both;
            rule.DownloadBytesPerSecond = down;
            rule.UploadBytesPerSecond = up;
        }

        var start = ScheduleStart.Trim();
        var end = ScheduleEnd.Trim();
        TimeOnly? startTime = null, endTime = null;
        if (start.Length > 0 || end.Length > 0)
        {
            if (!TryTime(start, out var parsedStart) || !TryTime(end, out var parsedEnd))
                return Fail("RuleEditor_BadTime");
            startTime = parsedStart;
            endTime = parsedEnd;
        }
        var days = Enumerable.Range(0, 7).Where(i => Days[i]).Select(i => (DayOfWeek)i).ToArray();
        rule.Schedule = startTime is null && days.Length == 0
            ? null
            : new RuleSchedule { StartTime = startTime, EndTime = endTime, ActiveDays = days.Length > 0 ? days : null };

        if (!HasQuota)
        {
            rule.Quota = null;
            return new RuleDraftResult(rule, null);
        }

        var oldQuota = original?.Quota;
        if (!TryBytes(QuotaMegabytes, BytesPerMegabyte, oldQuota?.LimitBytes ?? 0, out var quotaBytes) || quotaBytes == 0)
            return Fail("RuleEditor_NeedQuota");
        var throttle = oldQuota?.ThrottleBytesPerSecond ?? DefaultThrottleBytesPerSecond;
        if (QuotaAction == QuotaAction.Throttle &&
            (!TryBytes(ThrottleKbps, BytesPerKilobyte, throttle, out throttle) || throttle == 0))
            return Fail("RuleEditor_NeedThrottle");
        rule.Quota = new QuotaConfig
        {
            LimitBytes = quotaBytes,
            Period = QuotaPeriod,
            OnExceeded = QuotaAction,
            ThrottleBytesPerSecond = throttle,
            WarningPercent = oldQuota?.WarningPercent ?? DefaultWarningPercent
        };
        return new RuleDraftResult(rule, null);
    }

    private static RuleDraftResult Fail(string key) => new(null, key);

    /// <summary>Whole units, rounded up so a rule set in bytes never shows as 0.</summary>
    internal static string Units(long bytes, long unit) =>
        (bytes <= 0 ? 0 : (bytes + unit - 1) / unit).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a whole number of units. Text that still reads as <paramref name="originalBytes"/>
    /// keeps that exact value.
    /// </summary>
    internal static bool TryBytes(string text, long unit, long originalBytes, out long bytes)
    {
        bytes = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return true;
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value > long.MaxValue / unit)
            return false;
        bytes = trimmed == Units(originalBytes, unit) && originalBytes > 0 ? originalBytes : value * unit;
        return true;
    }

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

/// <summary>An alert built from the editor, or the resource key that says what to fix.</summary>
public sealed record AlertDraftResult(BandwidthAlertRule? Alert, string? ErrorKey);

/// <summary>The alert editor's fields as typed. An alert with no program watches every app.</summary>
public sealed class AlertDraft
{
    public string Name { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public RuleDirection Direction { get; set; } = RuleDirection.Both;
    public string ThresholdKbps { get; set; } = "1024";
    public string CooldownMinutes { get; set; } = "5";
    public bool Enabled { get; set; } = true;

    public static AlertDraft From(BandwidthAlertRule alert) => new()
    {
        Name = alert.Name,
        ProcessName = alert.ProcessName ?? string.Empty,
        ProcessPath = alert.ProcessPath ?? string.Empty,
        Direction = alert.Direction,
        ThresholdKbps = RuleDraft.Units(alert.ThresholdBytesPerSecond, RuleDraft.BytesPerKilobyte),
        CooldownMinutes = RuleDraft.Units(alert.CooldownSeconds, 60),
        Enabled = alert.Enabled
    };

    public AlertDraftResult Build(BandwidthAlertRule? original)
    {
        if (!RuleDraft.TryBytes(ThresholdKbps, RuleDraft.BytesPerKilobyte, original?.ThresholdBytesPerSecond ?? 0, out var threshold) ||
            threshold == 0)
            return new AlertDraftResult(null, "AlertEditor_NeedThreshold");
        if (!RuleDraft.TryBytes(CooldownMinutes, 60, original?.CooldownSeconds ?? 0, out var cooldown) || cooldown > int.MaxValue)
            return new AlertDraftResult(null, "RuleEditor_BadNumber");

        var processName = ProcessName.Trim();
        var processPath = ProcessPath.Trim().Trim('"').Trim();
        return new AlertDraftResult(new BandwidthAlertRule
        {
            Id = original?.Id ?? Guid.NewGuid(),
            Name = Name.Trim(),
            ProcessName = processName.Length == 0 ? null : processName,
            ProcessPath = processPath.Length == 0 ? null : processPath,
            Direction = Direction,
            ThresholdBytesPerSecond = threshold,
            CooldownSeconds = (int)cooldown,
            Enabled = Enabled
        }, null);
    }
}
