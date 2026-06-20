using OpenNetLimit.Core;

namespace OpenNetLimit.Core.Models;

public enum RuleAction
{
    Limit,
    Block,
    Allow
}

public enum RuleDirection
{
    Both,
    Download,
    Upload
}

public enum BandwidthPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public class BandwidthRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public string? ProcessName { get; set; }
    public string? ProcessPath { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Limit;
    public RuleDirection Direction { get; set; } = RuleDirection.Both;

    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }

    public string? RemoteAddressFilter { get; set; }
    public int? RemotePortFilter { get; set; }

    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveUntil { get; set; }

    public RuleSchedule? Schedule { get; set; }

    public QuotaConfig? Quota { get; set; }

    public BandwidthPriority BandwidthPriority { get; set; } = BandwidthPriority.Normal;

    public string? ProfileName { get; set; }

    public int Priority { get; set; }

    public bool MatchesProcess(string processName, string? processPath)
    {
        if (ProcessPath is not null && processPath is not null)
        {
            if (ProcessPath.Contains('*') || ProcessPath.Contains('?'))
                return WildcardMatcher.IsMatch(processPath, ProcessPath);
            return processPath.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        if (ProcessName is not null)
            return processName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    public bool IsActiveNow()
    {
        if (!Enabled) return false;
        var now = DateTime.UtcNow;
        if (ActiveFrom.HasValue && now < ActiveFrom.Value) return false;
        if (ActiveUntil.HasValue && now > ActiveUntil.Value) return false;
        if (Schedule is not null && !Schedule.IsActiveAt(now)) return false;
        return true;
    }
}

public enum QuotaPeriod
{
    Daily,
    Weekly,
    Monthly
}

public enum QuotaAction
{
    Throttle,
    Block,
    WarnOnly
}

public class QuotaConfig
{
    public long LimitBytes { get; set; }
    public QuotaPeriod Period { get; set; } = QuotaPeriod.Daily;
    public QuotaAction OnExceeded { get; set; } = QuotaAction.Throttle;
    public long ThrottleBytesPerSecond { get; set; } = 10 * 1024;
    public int WarningPercent { get; set; } = 80;
}

public class RuleSchedule
{
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public DayOfWeek[]? ActiveDays { get; set; }

    public bool IsActiveAt(DateTime utcNow)
    {
        if (ActiveDays is { Length: > 0 } && !ActiveDays.Contains(utcNow.DayOfWeek))
            return false;

        if (StartTime.HasValue && EndTime.HasValue)
        {
            var timeNow = TimeOnly.FromDateTime(utcNow);
            if (StartTime.Value <= EndTime.Value)
                return timeNow >= StartTime.Value && timeNow <= EndTime.Value;
            return timeNow >= StartTime.Value || timeNow <= EndTime.Value;
        }

        return true;
    }
}
