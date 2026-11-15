using System.Text.Json;
using OpenNetLimit.Core.IPC;

namespace OpenNetLimit.UI.Services;

/// <summary>
/// A tray balloon. Body is the service's own wording, or a resource key when it gave none.
/// ExtraKey is a sentence added after it.
/// </summary>
public sealed record TrayNotice(string TitleKey, string? Body = null, string? BodyKey = null, bool IsWarning = false,
    string? ExtraKey = null)
{
    public string ResolveTitle() => LocalizationManager.Text(TitleKey);

    public string ResolveBody()
    {
        var body = Body ?? LocalizationManager.Text(BodyKey ?? "Engine_NoAnswer");
        return ExtraKey is null ? body : body + " " + LocalizationManager.Text(ExtraKey);
    }
}

/// <summary>Builds the pause and resume requests and decides what the tray says about their answers.</summary>
public static class EnginePause
{
    public static string PauseCommand(bool stayPaused) =>
        "PAUSE " + JsonSerializer.Serialize(new { stayPaused });

    public const string ResumeCommand = "RESUME";

    /// <summary>
    /// The connection dropped before the answer came, so whether it worked isn't known. The
    /// status in the window catches up on the next poll.
    /// </summary>
    private static readonly TrayNotice NoReply = new("Engine_NoReply_Title", BodyKey: "Engine_ConnectionLost", IsWarning: true);

    /// <summary>
    /// What to say once the service answers a pause. Status is null when the reply wasn't one.
    /// <paramref name="stayRequested"/> is whether the pause asked to stay paused after a restart.
    /// </summary>
    public static TrayNotice AfterPause(string? reply, DiagnosticInfo? status, bool stayRequested = false)
    {
        if (reply is null)
            return NoReply;
        if (status is not { Paused: true })
            return new TrayNotice("Pause_Failed_Title", Body: Reason(reply, status), IsWarning: true);
        string? stayWarning = status.StayPaused == stayRequested ? null
            : stayRequested ? "Pause_StayNotSaved_Body" : "Pause_StayNotCleared_Body";
        if (!string.IsNullOrWhiteSpace(status.DriverLoadedReason))
            return new TrayNotice("Pause_StillLoaded_Title", Body: status.DriverLoadedReason, IsWarning: true,
                ExtraKey: stayWarning);
        return new TrayNotice("Status_Paused", BodyKey: "Pause_Unloaded_Body", IsWarning: stayWarning is not null,
            ExtraKey: stayWarning);
    }

    /// <summary>What to say once the service answers a resume.</summary>
    public static TrayNotice AfterResume(string? reply, DiagnosticInfo? status) =>
        reply is null ? NoReply
        : status is not { Running: true, Paused: false }
            ? new TrayNotice("Resume_Failed_Title", Body: Reason(reply, status), IsWarning: true)
        : status.StayPaused
            ? new TrayNotice("Resume_Done_Title", BodyKey: "Resume_Done_Body", IsWarning: true,
                ExtraKey: "Resume_StayNotCleared_Body")
            : new TrayNotice("Resume_Done_Title", BodyKey: "Resume_Done_Body");

    /// <summary>
    /// A pause can leave the driver loaded because another program still has it open. It unloads by itself
    /// when that program closes, and this says so on the first poll that sees it. After a refused or failed
    /// resume it says Resume works now instead.
    /// </summary>
    public static TrayNotice? AfterPoll(DiagnosticInfo? previous, DiagnosticInfo current) =>
        previous is { Paused: true } && !string.IsNullOrWhiteSpace(previous.DriverLoadedReason) &&
        current.Paused && string.IsNullOrWhiteSpace(current.DriverLoadedReason)
            ? new TrayNotice("Pause_Unloaded_Title",
                BodyKey: string.IsNullOrWhiteSpace(previous.EngineError) ? "Pause_Unloaded_Body" : "Pause_CanResume_Body")
            : null;

    private static string? Reason(string? reply, DiagnosticInfo? status)
    {
        if (!string.IsNullOrWhiteSpace(status?.EngineError))
            return status.EngineError;
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(error.GetString())
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
