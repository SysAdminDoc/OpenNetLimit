using OpenNetLimit.Core.Models;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>
/// The whole-PC limit's card on the Realtime tab: the limit the service last reported, and why
/// the last change from the app didn't take.
/// </summary>
internal sealed class PcLimitCard
{
    /// <summary>The limit every app shares, as the service last reported it.</summary>
    public WholePcLimit Limit { get; private set; } = WholePcLimit.Off;

    /// <summary>Why the last change didn't take, or null.</summary>
    public string? Error { get; private set; }

    public string Display => PcLimitText.Rates(Limit);

    /// <summary>Which traffic it counts, or why the last change didn't take.</summary>
    public string Hint => Error ?? PcLimitText.Scope(Limit);

    /// <summary>
    /// The service reports the limit in effect. One changed from anywhere (the CLI, REST, another
    /// copy of the app) answers whatever went wrong with the last try. The same limit again
    /// leaves the error up. True when the card changed.
    /// </summary>
    public bool Report(WholePcLimit limit)
    {
        if (Limit == limit)
            return false;
        Limit = limit;
        Error = null;
        return true;
    }

    /// <summary>
    /// The poll's answer to PC_LIMIT. A service from before the whole-PC limit answers with an
    /// error, which changes nothing. True when the card changed.
    /// </summary>
    public bool ReportReply(string? reply) =>
        RulesViewModel.ErrorOf(reply) is null && Parse(reply) is { } limit && Report(limit);

    /// <summary>
    /// The service's answer to a change the app sent, null when none came. An error stays on the
    /// card until the limit changes or the app reconnects.
    /// </summary>
    public void Answered(string? reply, WholePcLimit sent)
    {
        var error = RulesViewModel.ErrorOf(reply);
        if (error is null)
            Report(Parse(reply) ?? sent);
        Error = error;
    }

    /// <summary>
    /// Whatever kept the last change from taking was about a service that's gone since. True
    /// when the card changed.
    /// </summary>
    public bool Reconnected()
    {
        if (Error is null)
            return false;
        Error = null;
        return true;
    }

    /// <summary>
    /// A reply that's a limit, read as strictly as the service reads one. Any JSON object used to
    /// pass, with what it lacked read as 0, so {"ok":true} showed the limit as off.
    /// </summary>
    private static WholePcLimit? Parse(string? json) =>
        json is null ? null : WholePcLimit.Parse(json, out _);
}
