using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service.Control;

public sealed class ControlPlaneState
{
    public Func<DiagnosticInfo>? DiagnosticProvider { get; set; }
    public Func<IReadOnlyList<object>>? ConnectionLogProvider { get; set; }
    public TrafficStatsDb? StatsProvider { get; set; }
    public QuotaTracker? QuotaTracker { get; set; }

    /// <summary>
    /// Why changes to rules, alerts and the whole-PC limit are refused, or null when the service
    /// keeps them. A service that couldn't load the rules, or couldn't keep a copy of a file
    /// that didn't load, can't save a change without losing them.
    /// </summary>
    public string? ChangesRefused { get; set; }

    /// <summary>Pauses the engine (the flag keeps it paused across restarts) and returns the new status.</summary>
    public Func<bool, CancellationToken, Task<DiagnosticInfo>>? PauseHandler { get; set; }

    /// <summary>Starts the engine again and returns the new status.</summary>
    public Func<CancellationToken, Task<DiagnosticInfo>>? ResumeHandler { get; set; }

    /// <summary>The whole-PC limit as last set, on or off.</summary>
    public Func<WholePcLimit>? PcLimitProvider { get; set; }

    /// <summary>Sets the whole-PC limit, keeps it for the next start and returns it.</summary>
    public Func<WholePcLimit, WholePcLimit>? PcLimitHandler { get; set; }

    /// <summary>
    /// Changes part of the whole-PC limit, applied to the limit in effect at that moment, then
    /// keeps it like <see cref="PcLimitHandler"/> and returns the result.
    /// </summary>
    public Func<WholePcLimitChange, WholePcLimit>? PcLimitChangeHandler { get; set; }

    public WholePcLimit GetPcLimit() => PcLimitProvider?.Invoke() ?? WholePcLimit.Off;

    public DiagnosticInfo GetDiagnostics() =>
        DiagnosticProvider?.Invoke() ?? new DiagnosticInfo { Running = false };

    public IReadOnlyList<object> GetConnectionLog() =>
        ConnectionLogProvider?.Invoke() ?? [];

    public IReadOnlyList<QuotaState> GetQuotaStates() =>
        QuotaTracker?.GetAllQuotaStates() ?? [];
}
