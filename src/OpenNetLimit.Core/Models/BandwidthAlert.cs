using OpenNetLimit.Core;

namespace OpenNetLimit.Core.Models;

public class BandwidthAlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? ProcessName { get; set; }
    public string? ProcessPath { get; set; }
    public RuleDirection Direction { get; set; } = RuleDirection.Both;
    public long ThresholdBytesPerSecond { get; set; }
    public int CooldownSeconds { get; set; } = 300;

    public bool Matches(ProcessTrafficInfo process)
    {
        if (!Enabled || ThresholdBytesPerSecond <= 0)
            return false;

        if (ProcessPath is not null && process.ProcessPath is not null)
        {
            if (ProcessPath.Contains('*') || ProcessPath.Contains('?'))
                return WildcardMatcher.IsMatch(process.ProcessPath, ProcessPath);
            return process.ProcessPath.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        if (ProcessName is not null)
            return process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        return true;
    }

    public long GetObservedBytesPerSecond(ProcessTrafficInfo process) =>
        Direction switch
        {
            RuleDirection.Download => process.CurrentDownloadBytesPerSecond,
            RuleDirection.Upload => process.CurrentUploadBytesPerSecond,
            _ => process.CurrentDownloadBytesPerSecond + process.CurrentUploadBytesPerSecond
        };

}

public class BandwidthAlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }
    public RuleDirection Direction { get; set; }
    public long ObservedBytesPerSecond { get; set; }
    public long ThresholdBytesPerSecond { get; set; }
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;
    public string Message { get; set; } = string.Empty;
}
