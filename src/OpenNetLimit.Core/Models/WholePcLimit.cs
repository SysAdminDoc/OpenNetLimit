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

    internal static readonly JsonSerializerOptions ReadOptions = new()
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
            if (FieldsIn(doc.RootElement, out problem) is not { } seen)
                return null;
            if (!seen.Contains("downloadBytesPerSecond") && !seen.Contains("uploadBytesPerSecond"))
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

    /// <summary>
    /// The fields <paramref name="root"/> gives, by their names here, or null with what's wrong
    /// in <paramref name="problem"/>: it isn't an object, or it has a field this doesn't know or
    /// one field twice.
    /// </summary>
    internal static HashSet<string>? FieldsIn(JsonElement root, out string? problem)
    {
        problem = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = "invalid whole-PC limit: send a JSON object like {\"downloadBytesPerSecond\":1048576}";
            return null;
        }
        var seen = new HashSet<string>();
        foreach (var field in root.EnumerateObject())
        {
            var known = Array.Find(Fields, f => f.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                problem = $"invalid whole-PC limit: there's no field \"{field.Name}\". It takes downloadBytesPerSecond, uploadBytesPerSecond and includeLan";
                return null;
            }
            // The last copy would win, so a second spelling of a rate set to 0 switched the
            // limit off with no word, the same as a misspelled field used to.
            if (!seen.Add(known))
            {
                problem = $"invalid whole-PC limit: {known} is in it more than once";
                return null;
            }
        }
        return seen;
    }
}

/// <summary>
/// A change to part of the whole-PC limit. A field left null keeps what's in effect when the
/// change is applied, so a change made elsewhere since the client last read the limit isn't undone.
/// </summary>
public sealed record WholePcLimitChange
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DownloadBytesPerSecond { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UploadBytesPerSecond { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeLan { get; init; }

    [JsonIgnore]
    public bool IsEmpty => DownloadBytesPerSecond is null && UploadBytesPerSecond is null && IncludeLan is null;

    /// <summary>The limit <paramref name="current"/> becomes with this change.</summary>
    public WholePcLimit ApplyTo(WholePcLimit current) => current with
    {
        DownloadBytesPerSecond = DownloadBytesPerSecond ?? current.DownloadBytesPerSecond,
        UploadBytesPerSecond = UploadBytesPerSecond ?? current.UploadBytesPerSecond,
        IncludeLan = IncludeLan ?? current.IncludeLan
    };

    /// <summary>
    /// Reads a change a client sent, or gives what's wrong with it in <paramref name="problem"/>.
    /// It's checked like a whole limit, except that any one field is enough. A field set to null
    /// is a problem rather than a field left alone, so a client can't mean one and get the other.
    /// </summary>
    public static WholePcLimitChange? Parse(string? json, out string? problem)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            if (WholePcLimit.FieldsIn(doc.RootElement, out problem) is not { } seen)
                return null;
            if (seen.Count == 0)
            {
                problem = "invalid whole-PC limit change: give downloadBytesPerSecond, uploadBytesPerSecond or includeLan";
                return null;
            }
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Null)
                {
                    problem = $"invalid whole-PC limit change: {field.Name} can't be null. Leave it out to keep it as it is";
                    return null;
                }
            }
            var change = doc.RootElement.Deserialize<WholePcLimitChange>(WholePcLimit.ReadOptions)!;
            if (change.DownloadBytesPerSecond < 0 || change.UploadBytesPerSecond < 0)
            {
                problem = "A whole-PC limit can't be negative. Use 0 for no limit.";
                return null;
            }
            return change;
        }
        catch (JsonException ex)
        {
            problem = $"invalid JSON: {ex.Message}";
            return null;
        }
    }
}
