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

    public int Priority { get; set; }

    public bool MatchesProcess(string processName, string? processPath)
    {
        if (ProcessPath is not null && processPath is not null)
        {
            if (ProcessPath.Contains('*') || ProcessPath.Contains('?'))
                return WildcardMatch(processPath, ProcessPath);
            return processPath.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        if (ProcessName is not null)
            return processName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        int i = 0, j = 0;
        int starI = -1, starJ = -1;

        while (i < input.Length)
        {
            if (j < pattern.Length && (char.ToLowerInvariant(pattern[j]) == char.ToLowerInvariant(input[i]) || pattern[j] == '?'))
            {
                i++;
                j++;
            }
            else if (j < pattern.Length && pattern[j] == '*')
            {
                starI = i;
                starJ = j++;
            }
            else if (starJ >= 0)
            {
                i = ++starI;
                j = starJ + 1;
            }
            else
            {
                return false;
            }
        }

        while (j < pattern.Length && pattern[j] == '*') j++;
        return j == pattern.Length;
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
