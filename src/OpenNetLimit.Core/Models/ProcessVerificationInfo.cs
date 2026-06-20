namespace OpenNetLimit.Core.Models;

public enum ProcessVerificationStatus
{
    NotConfigured,
    FileNotFound,
    AccessDenied,
    Unknown,
    Clean,
    Suspicious,
    Malicious,
    Error
}

public class ProcessVerificationInfo
{
    public string Source { get; set; } = "VirusTotal";
    public string? ProcessPath { get; set; }
    public string? Sha256 { get; set; }
    public ProcessVerificationStatus Status { get; set; } = ProcessVerificationStatus.Unknown;
    public int Harmless { get; set; }
    public int Malicious { get; set; }
    public int Suspicious { get; set; }
    public int Undetected { get; set; }
    public int Timeout { get; set; }
    public string? Summary { get; set; }
    public string? Permalink { get; set; }
    public string? Error { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public bool FromCache { get; set; }
}
