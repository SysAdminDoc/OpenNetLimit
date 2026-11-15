using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleReconciler
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly object _lock = new();

    /// <summary>
    /// The process each limit set here was meant for. A limit outlives the process's open
    /// connections, so without this a removed or changed rule never reached an idle app, and an
    /// app that got a reused process id inherited whatever limit the old process had.
    /// </summary>
    private readonly Dictionary<uint, (string Name, string? Path)> _applied = new();

    public RuleReconciler(IRuleEngine ruleEngine, IRateLimiter rateLimiter, IFlowTracker flowTracker)
    {
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _flowTracker.FlowRegistered += EnsureLimit;
    }

    public void Reconcile()
    {
        lock (_lock)
        {
            var connections = _flowTracker.GetActiveConnections();
            var connected = new HashSet<uint>();
            var processesWithLimits = new HashSet<uint>();

            foreach (var conn in connections)
            {
                connected.Add(conn.ProcessId);
                if (LimitFor(conn.ProcessName, conn.ProcessPath) is { } limit)
                {
                    Apply(conn.ProcessId, conn.ProcessName, conn.ProcessPath, limit);
                    processesWithLimits.Add(conn.ProcessId);
                }
            }

            foreach (var processId in connected)
            {
                if (!processesWithLimits.Contains(processId) && _rateLimiter.HasLimit(processId))
                {
                    _rateLimiter.RemoveLimit(processId);
                    _applied.Remove(processId);
                }
            }

            foreach (var (processId, process) in _applied.ToList())
            {
                if (connected.Contains(processId))
                    continue;
                if (LimitFor(process.Name, process.Path) is { } limit)
                {
                    _rateLimiter.SetLimit(processId, limit.Download, limit.Upload);
                }
                else
                {
                    _rateLimiter.RemoveLimit(processId);
                    _applied.Remove(processId);
                }
            }
        }
    }

    /// <summary>
    /// Applies the matching rule's limit when a process that has none opens a flow.
    /// <see cref="Reconcile"/> only runs when the rules change, so an app started (or restarted,
    /// or reconnecting after a reboot) after the last change was never limited. A limit already
    /// in place is left alone: it is either this rule's or a quota throttle, unless it was set
    /// for a different process that used to have this id.
    /// </summary>
    public void EnsureLimit(uint processId, string processName, string? processPath)
    {
        lock (_lock)
        {
            if (_rateLimiter.HasLimit(processId))
            {
                if (!_applied.TryGetValue(processId, out var holder) || IsSameProcess(holder, processName, processPath))
                    return;
                _rateLimiter.RemoveLimit(processId);
                _applied.Remove(processId);
            }
            if (LimitFor(processName, processPath) is { } limit)
                Apply(processId, processName, processPath, limit);
        }
    }

    private void Apply(uint processId, string processName, string? processPath, (long Download, long Upload) limit)
    {
        _rateLimiter.SetLimit(processId, limit.Download, limit.Upload);
        _applied[processId] = (processName, processPath);
    }

    private static bool IsSameProcess((string Name, string? Path) holder, string processName, string? processPath) =>
        string.Equals(holder.Name, processName, StringComparison.OrdinalIgnoreCase)
        && (holder.Path is null || processPath is null || string.Equals(holder.Path, processPath, StringComparison.OrdinalIgnoreCase));

    private (long Download, long Upload)? LimitFor(string processName, string? processPath)
    {
        var rule = _ruleEngine.FindMatchingRule(processName, processPath);
        if (rule is null || rule.Action != RuleAction.Limit || !rule.IsActiveNow())
            return null;

        long download = rule.Direction is RuleDirection.Both or RuleDirection.Download
            ? rule.DownloadBytesPerSecond : 0;
        long upload = rule.Direction is RuleDirection.Both or RuleDirection.Upload
            ? rule.UploadBytesPerSecond : 0;
        return download > 0 || upload > 0 ? (download, upload) : null;
    }
}
