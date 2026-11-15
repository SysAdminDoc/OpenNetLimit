using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleReconciler
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IRateLimiter _rateLimiter;
    private readonly IFlowTracker _flowTracker;
    private readonly QuotaTracker? _quotaTracker;
    private readonly object _lock = new();

    /// <summary>
    /// The process each limit set here was meant for. A limit outlives the process's open
    /// connections, so without this a removed or changed rule never reached an idle app, and an
    /// app that got a reused process id inherited whatever limit the old process had.
    /// </summary>
    private readonly Dictionary<uint, (string Name, string? Path)> _applied = new();

    /// <summary>Active rules whose quota is used up and blocks or throttles, as of the last reconcile.</summary>
    private IReadOnlyList<BandwidthRule> _usedUpQuotas = [];

    /// <summary>The active rules at the last reconcile, and whether each one's quota was used up.</summary>
    private HashSet<(Guid RuleId, bool QuotaUsedUp)> _inputs = [];

    public RuleReconciler(IRuleEngine ruleEngine, IRateLimiter rateLimiter, IFlowTracker flowTracker,
        QuotaTracker? quotaTracker = null)
    {
        _ruleEngine = ruleEngine;
        _rateLimiter = rateLimiter;
        _flowTracker = flowTracker;
        _quotaTracker = quotaTracker;
        _flowTracker.FlowRegistered += EnsureLimit;
    }

    public void Reconcile()
    {
        lock (_lock)
        {
            var rules = _ruleEngine.GetAllRules();
            _usedUpQuotas = UsedUpQuotas(rules);
            _inputs = Inputs(rules, _usedUpQuotas);

            var connections = _flowTracker.GetActiveConnections();
            var connected = new HashSet<uint>();
            var processesWithLimits = new HashSet<uint>();

            foreach (var conn in connections)
            {
                connected.Add(conn.ProcessId);
                if (PolicyFor(conn.ProcessName, conn.ProcessPath) is { } policy)
                {
                    Apply(conn.ProcessId, conn.ProcessName, conn.ProcessPath, policy);
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
                if (PolicyFor(process.Name, process.Path) is { } policy)
                {
                    Enforce(processId, policy);
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
    /// Runs after a rule is added, edited or removed. Quota state is brought up to date first,
    /// so a used-up quota that was pointed at another app or given a higher limit stops
    /// blocking with the edit instead of at the next quota update.
    /// </summary>
    public void ReconcileAfterRuleChange()
    {
        try
        {
            _quotaTracker?.Update();
        }
        finally
        {
            Reconcile();
        }
    }

    /// <summary>
    /// Reconciles when a rule's schedule or active dates switched it on or off, or a quota ran
    /// out or was reset, since the last pass. Editing a rule reconciles on its own; these change
    /// with the clock and the traffic, so the engine calls this on a timer.
    /// </summary>
    /// <returns>True when it reconciled.</returns>
    public bool ReconcileIfChanged()
    {
        lock (_lock)
        {
            var rules = _ruleEngine.GetAllRules();
            if (Inputs(rules, UsedUpQuotas(rules)).SetEquals(_inputs))
                return false;
            Reconcile();
            return true;
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
            if (PolicyFor(processName, processPath) is { } policy)
                Apply(processId, processName, processPath, policy);
        }
    }

    private void Apply(uint processId, string processName, string? processPath, Policy policy)
    {
        Enforce(processId, policy);
        _applied[processId] = (processName, processPath);
    }

    private void Enforce(uint processId, Policy policy)
    {
        if (policy.Drop)
            _rateLimiter.Block(processId);
        else
            _rateLimiter.SetLimit(processId, policy.Download, policy.Upload);
    }

    private static bool IsSameProcess((string Name, string? Path) holder, string processName, string? processPath) =>
        string.Equals(holder.Name, processName, StringComparison.OrdinalIgnoreCase)
        && (holder.Path is null || processPath is null || string.Equals(holder.Path, processPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the process gets: a used-up quota that blocks wins, then the tighter of its limit
    /// rule and any used-up quota that throttles. Quotas stay in force until their period resets.
    /// </summary>
    private Policy? PolicyFor(string processName, string? processPath)
    {
        var policy = RuleLimitFor(processName, processPath);
        foreach (var rule in _usedUpQuotas)
        {
            if (!rule.MatchesProcess(processName, processPath))
                continue;
            if (rule.Quota!.OnExceeded == QuotaAction.Block)
                return Policy.Blocked;
            policy = Throttle(policy, rule.Quota.ThrottleBytesPerSecond);
        }
        return policy;
    }

    private Policy? RuleLimitFor(string processName, string? processPath)
    {
        var rule = _ruleEngine.FindMatchingRule(processName, processPath);
        if (rule is null || rule.Action != RuleAction.Limit || !rule.IsActiveNow())
            return null;

        long download = rule.Direction is RuleDirection.Both or RuleDirection.Download
            ? rule.DownloadBytesPerSecond : 0;
        long upload = rule.Direction is RuleDirection.Both or RuleDirection.Upload
            ? rule.UploadBytesPerSecond : 0;
        return download > 0 || upload > 0 ? new Policy(download, upload, Drop: false) : null;
    }

    /// <summary>A quota throttle caps both directions, unless the rule's own limit is lower.</summary>
    private static Policy? Throttle(Policy? policy, long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return policy;
        if (policy is not { } current)
            return new Policy(bytesPerSecond, bytesPerSecond, Drop: false);
        return new Policy(Tighter(current.Download, bytesPerSecond), Tighter(current.Upload, bytesPerSecond), Drop: false);
    }

    private static long Tighter(long limit, long throttle) => limit > 0 ? Math.Min(limit, throttle) : throttle;

    private IReadOnlyList<BandwidthRule> UsedUpQuotas(IReadOnlyList<BandwidthRule> rules)
    {
        if (_quotaTracker is null)
            return [];
        // A state only counts for the apps it was measured on. A rule just pointed at another app
        // keeps the old app's used-up state until the next quota update replaces it, and a
        // reconcile in between (a schedule tick, or an update that failed) would block the new app.
        var usedUp = _quotaTracker.GetAllQuotaStates().Where(s => s.IsExceeded).ToDictionary(s => s.RuleId);
        return rules
            .Where(r => usedUp.TryGetValue(r.Id, out var state) && state.IsFor(r)
                && r.Quota is { OnExceeded: QuotaAction.Block or QuotaAction.Throttle }
                && r.IsActiveNow())
            .ToList();
    }

    private static HashSet<(Guid, bool)> Inputs(IReadOnlyList<BandwidthRule> rules, IReadOnlyList<BandwidthRule> usedUpQuotas)
    {
        var usedUp = usedUpQuotas.Select(r => r.Id).ToHashSet();
        return rules.Where(r => r.IsActiveNow()).Select(r => (r.Id, usedUp.Contains(r.Id))).ToHashSet();
    }

    private readonly record struct Policy(long Download, long Upload, bool Drop)
    {
        public static Policy Blocked => new(0, 0, Drop: true);
    }
}
