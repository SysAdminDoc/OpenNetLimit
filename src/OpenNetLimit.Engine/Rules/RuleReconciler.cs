using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleReconciler
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;

    public RuleReconciler(IRuleEngine ruleEngine, IRateLimiter rateLimiter, IFlowTracker flowTracker)
    {
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
    }

    public void Reconcile()
    {
        var rules = _ruleEngine.GetAllRules();
        var connections = _flowTracker.GetActiveConnections();

        var processesWithLimits = new HashSet<uint>();

        foreach (var conn in connections)
        {
            var matchingRule = _ruleEngine.FindMatchingRule(conn.ProcessName, conn.ProcessPath);

            if (matchingRule is not null && matchingRule.Action == RuleAction.Limit && matchingRule.IsActiveNow())
            {
                long downLimit = matchingRule.Direction is RuleDirection.Both or RuleDirection.Download
                    ? matchingRule.DownloadBytesPerSecond : 0;
                long upLimit = matchingRule.Direction is RuleDirection.Both or RuleDirection.Upload
                    ? matchingRule.UploadBytesPerSecond : 0;

                if (downLimit > 0 || upLimit > 0)
                {
                    _rateLimiter.SetLimit(conn.ProcessId, downLimit, upLimit);
                    processesWithLimits.Add(conn.ProcessId);
                }
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
