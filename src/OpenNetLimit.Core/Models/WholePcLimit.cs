using System.Text.Json;
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

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] Fields = ["downloadBytesPerSecond", "uploadBytesPerSecond", "includeLan"];

    /// <summary>
    /// Reads a limit a client sent or a file holds, or gives what's wrong with it in
    /// <paramref name="problem"/>. Field names can be in any case. A field it doesn't know, or no
    /// rate at all, is a problem: a misspelled rate used to turn the limit off without a word.
    /// </summary>
    public static WholePcLimit? Parse(string? json, out string? problem)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "invalid whole-PC limit: send a JSON object like {\"downloadBytesPerSecond\":1048576}";
                return null;
            }
            var hasRate = false;
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                var known = Array.Find(Fields, f => f.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                if (known is null)
                {
                    problem = $"invalid whole-PC limit: there's no field \"{field.Name}\". It takes downloadBytesPerSecond, uploadBytesPerSecond and includeLan";
                    return null;
                }
                hasRate |= known != "includeLan";
            }
            if (!hasRate)
            {
                problem = "invalid whole-PC limit: give downloadBytesPerSecond, uploadBytesPerSecond or both, with 0 for no limit that way";
                return null;
            }
            var limit = doc.RootElement.Deserialize<WholePcLimit>(ReadOptions)!;
            problem = limit.Problem();
            return problem is null ? limit : null;
        }
        catch (JsonException ex)
        {
            problem = $"invalid JSON: {ex.Message}";
            return null;
        }
    }
}
