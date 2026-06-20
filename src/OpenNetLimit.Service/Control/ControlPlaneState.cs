using OpenNetLimit.Core.IPC;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.Storage;

namespace OpenNetLimit.Service.Control;

public sealed class ControlPlaneState
{
    public Func<DiagnosticInfo>? DiagnosticProvider { get; set; }
    public Func<IReadOnlyList<object>>? ConnectionLogProvider { get; set; }
    public TrafficStatsDb? StatsProvider { get; set; }
    public QuotaTracker? QuotaTracker { get; set; }

    public DiagnosticInfo GetDiagnostics() =>
        DiagnosticProvider?.Invoke() ?? new DiagnosticInfo { Running = false };

    public IReadOnlyList<object> GetConnectionLog() =>
        ConnectionLogProvider?.Invoke() ?? [];

    public IReadOnlyList<QuotaState> GetQuotaStates() =>
        QuotaTracker?.GetAllQuotaStates() ?? [];
}
