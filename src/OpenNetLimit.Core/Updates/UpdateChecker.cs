using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenNetLimit.Core.Updates;

/// <summary>A release newer than the one running.</summary>
public sealed record UpdateInfo(Version Version);

/// <summary>
/// Asks GitHub for the latest OpenNetLimit release at most once a day. The answer is kept in a
/// small state file, so later starts can show it without asking again, along with the switch
/// that turns checking off. Nothing is downloaded or installed.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/SysAdminDoc/OpenNetLimit/releases/latest";
    public const string ReleasesPage = "https://github.com/SysAdminDoc/OpenNetLimit/releases/latest";

    /// <summary>Set to 1 (or true, yes) to turn update checks off on a managed PC.</summary>
    public const string DisableVariable = "OPENNETLIMIT_NO_UPDATE_CHECK";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly string _stateFile;
    private readonly Func<DateTimeOffset> _now;

    public UpdateChecker(HttpClient http, string stateFile, bool disabledByPolicy, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _stateFile = stateFile;
        DisabledByPolicy = disabledByPolicy;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool PolicyDisablesChecks =>
        Environment.GetEnvironmentVariable(DisableVariable)?.Trim().ToLowerInvariant() is "1" or "true" or "yes";

    public bool DisabledByPolicy { get; }

    public bool Enabled => !DisabledByPolicy && Load().Enabled;

    public void SetEnabled(bool enabled)
    {
        var state = Load();
        state.Enabled = enabled;
        Save(state);
    }

    /// <summary>
    /// Returns the newer release when there is one. Asks GitHub only when the last answer is a
    /// day old; offline, rate limited or otherwise unanswered, it falls back to the last answer.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        if (DisabledByPolicy) return null;
        var state = Load();
        if (!state.Enabled) return null;

        var now = _now();
        var due = state.CheckedAt is not { } checkedAt || now - checkedAt >= Interval || now < checkedAt;
        if (due)
        {
            try
            {
                var latest = await FetchLatestTagAsync(_http, cancellationToken);
                state.CheckedAt = now;
                state.LatestTag = latest;
                Save(state);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Trace.TraceWarning($"The update check didn't get an answer: {ex.Message}");
            }
        }

        return Newer(state.LatestTag, current);
    }

    /// <summary>Asks GitHub for the latest release's tag. Throws when GitHub doesn't answer with one.</summary>
    public static async Task<string> FetchLatestTagAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenNetLimit-UpdateCheck", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.", null, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var tag = document.RootElement.GetProperty("tag_name").GetString();
        return string.IsNullOrWhiteSpace(tag) ? throw new JsonException("The release has no tag.") : tag;
    }

    /// <summary>Reads "v1.2.3", "1.2" or "v2.0.0-beta.1" as major.minor.patch.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+']);
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out var parsed) ? Normalize(parsed) : null;
    }

    public static UpdateInfo? Newer(string? latestTag, Version current) =>
        ParseVersion(latestTag) is { } latest && latest > Normalize(current) ? new UpdateInfo(latest) : null;

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private UpdateState Load()
    {
        try
        {
            if (File.Exists(_stateFile))
                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_stateFile)) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"The update check state couldn't be read, starting over: {ex.Message}");
        }
        return new UpdateState();
    }

    private void Save(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, StateJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The update check state couldn't be saved: {ex.Message}");
        }
    }

    private sealed class UpdateState
    {
        public bool Enabled { get; set; } = true;
        public DateTimeOffset? CheckedAt { get; set; }
        public string? LatestTag { get; set; }
    }
}
