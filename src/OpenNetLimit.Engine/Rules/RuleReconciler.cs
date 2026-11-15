using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleReconciler
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly object _lock = new();

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
            var processesWithLimits = new HashSet<uint>();

            foreach (var conn in connections)
            {
                if (LimitFor(conn.ProcessName, conn.ProcessPath) is { } limit)
                {
                    _rateLimiter.SetLimit(conn.ProcessId, limit.Download, limit.Upload);
                    processesWithLimits.Add(conn.ProcessId);
                }
            }

            foreach (var conn in connections)
            {
                if (!processesWithLimits.Contains(conn.ProcessId) && _rateLimiter.HasLimit(conn.ProcessId))
                {
                    _rateLimiter.RemoveLimit(conn.ProcessId);
                }
            }
        }
    }

    /// <summary>
    /// Applies the matching rule's limit when a process that has none opens a flow.
    /// <see cref="Reconcile"/> only runs when the rules change, so an app started (or restarted,
    /// or reconnecting after a reboot) after the last change was never limited. A limit already
    /// in place is left alone: it is either this rule's or a quota throttle.
    /// </summary>
    public void EnsureLimit(uint processId, string processName, string? processPath)
    {
        lock (_lock)
        {
            if (_rateLimiter.HasLimit(processId))
                return;
            if (LimitFor(processName, processPath) is { } limit)
                _rateLimiter.SetLimit(processId, limit.Download, limit.Upload);
        }
    }

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
