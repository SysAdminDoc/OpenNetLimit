using OpenNetLimit.Core;

namespace OpenNetLimit.Core.Models;

public class BandwidthAlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    private string? _processName;
    public string? ProcessName
    {
        get => _processName;
        set => _processName = ProcessNames.Normalize(value);
    }

    private string? _processPath;
    /// <summary>A blank path (REST, the pipe or an imported file can send one) means no path.</summary>
    public string? ProcessPath
    {
        get => _processPath;
        set => _processPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
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

        // An alert on a path alone says nothing about a process whose path can't be read
        // (protected and system processes). One on neither watches every app.
        return ProcessPath is null;
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
