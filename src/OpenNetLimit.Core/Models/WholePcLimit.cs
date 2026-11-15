using System.Text.Json.Serialization;

namespace OpenNetLimit.Core.Models;

/// <summary>
/// A limit on the whole computer's traffic, on top of each app's own limit. 0 in a direction
/// means no limit that way. Traffic within this PC never counts, and traffic with the local
/// network counts only when <see cref="IncludeLan"/> is on.
/// </summary>
public sealed record WholePcLimit
{
    public static readonly WholePcLimit Off = new();

    public long DownloadBytesPerSecond { get; init; }
    public long UploadBytesPerSecond { get; init; }
    public bool IncludeLan { get; init; }

    [JsonIgnore]
    public bool IsOff => DownloadBytesPerSecond <= 0 && UploadBytesPerSecond <= 0;

    /// <summary>Why the limit can't be used, or null when it can.</summary>
    public string? Problem() =>
        DownloadBytesPerSecond < 0 || UploadBytesPerSecond < 0
            ? "A whole-PC limit can't be negative. Use 0 for no limit."
            : null;
}
