using System.Collections.Concurrent;
using System.Diagnostics;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class QuotaTracker
{
    /// <summary>
    /// One state per rule. Keyed by process name, two rules on the same app shared a state and
    /// overwrote each other's limit, and a rule edited to another app carried its used-up state over.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, QuotaState> _quotas = new();

    /// <summary>
    /// States of rules removed lately, so a rule put back (the Rules tab's undo, or an export
    /// imported again) carries on with what it had used instead of starting over at zero.
    /// Only read and written under <see cref="_updateLock"/>.
    /// </summary>
    private readonly Dictionary<Guid, QuotaState> _removed = [];
    private const int MaxRemoved = 256;

    private readonly IRuleEngine _ruleEngine;
    private readonly ITrafficMonitor _trafficMonitor;

    /// <summary>
    /// Serializes <see cref="Update"/> and <see cref="ResetPeriod"/>. A rule edit updates quotas
    /// while the 30 s timer may be doing the same; without this the timer could finish last with
    /// the rules it read before the edit and put the old app's used-up state back.
    /// </summary>
    private readonly object _updateLock = new();

    /// <summary>
    /// Whether an update has run. Rules present at the first update count the traffic since the
    /// service started; a rule added or pointed at another app later counts from that update on.
    /// </summary>
    private bool _started;

    public event Action<string, QuotaState>? OnQuotaWarning;
    public event Action<string, QuotaState>? OnQuotaExceeded;

    public QuotaTracker(IRuleEngine ruleEngine, ITrafficMonitor trafficMonitor)
    {
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
    }

    public void Update()
    {
        var notifications = new List<(Action<string, QuotaState>? Event, string Name, QuotaState State)>();
        lock (_updateLock)
        {
            UpdateLocked(notifications);
        }
        foreach (var (handler, name, state) in notifications)
            Notify(handler, name, state);
    }

    /// <summary>
    /// Calls each subscriber on its own. One that throws would otherwise silence the rest, and
    /// the alert can't fire again: it's already marked as sent.
    /// </summary>
    private static void Notify(Action<string, QuotaState>? handler, string name, QuotaState state)
    {
        if (handler is null)
            return;
        foreach (var subscriber in handler.GetInvocationList().Cast<Action<string, QuotaState>>())
        {
            try
            {
                subscriber(name, state);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"A quota alert handler for {name} failed: {ex}");
            }
        }
    }

    private void UpdateLocked(List<(Action<string, QuotaState>? Event, string Name, QuotaState State)> notifications)
    {
        var allRules = _ruleEngine.GetAllRules(out var quotaStops);
        var rules = QuotaRules(allRules);
        var processes = _trafficMonitor.GetUsageTotals();
        bool firstUpdate = !_started;
        _started = true;

        var live = rules.Select(r => r.Id).ToHashSet();
        var existing = allRules.Select(r => r.Id).ToHashSet();
        foreach (var ruleId in _quotas.Keys)
        {
            // Only a deleted rule's use is kept for it coming back. Turning a cap, or its rule,
            // off and on again starts it over, which is how a used-up cap gets reset by hand.
            if (!live.Contains(ruleId) && _quotas.TryRemove(ruleId, out var gone) && !existing.Contains(ruleId))
            {
                if (_removed.Count >= MaxRemoved)
                    _removed.Clear();
                _removed[ruleId] = gone;
            }
        }
        // A deleted rule back in the list with its cap not counting (off, or no cap) was in effect
        // turned off, so turning it on later starts its cap over too.
        foreach (var id in _removed.Keys.Where(id => existing.Contains(id) && !live.Contains(id)).ToList())
            _removed.Remove(id);

        foreach (var rule in rules)
        {
            long totalBytes = UsageOf(rule, processes);
            long stops = quotaStops.GetValueOrDefault(rule.Id);

            if (!_quotas.ContainsKey(rule.Id) && _removed.Remove(rule.Id, out var kept) && kept.IsFor(rule) && kept.Stops == stops)
                _quotas[rule.Id] = kept;

            // A rule added later, pointed at a different app, or turned off and on again since
            // the last update (two clients can do both before one runs) counts from now. The
            // app's earlier traffic can't be split by quota period, so it isn't charged.
            var state = _quotas.AddOrUpdate(rule.Id,
                _ => NewState(rule, firstUpdate ? 0 : totalBytes, stops),
                (_, current) => current.IsFor(rule) && current.Stops == stops ? current : NewState(rule, totalBytes, stops));

            lock (state)
            {
                bool raisedPastUsedUp = rule.Quota!.LimitBytes > state.UsedUpAtLimitBytes;
                bool levelsChanged = rule.Quota.LimitBytes != state.LimitBytes
                    || rule.Quota.WarningPercent != state.WarningPercent;
                state.LimitBytes = rule.Quota.LimitBytes;
                state.UsedBytes = totalBytes - state.BaselineBytes;
                state.Period = rule.Quota.Period;
                state.WarningPercent = rule.Quota.WarningPercent;
                state.Action = rule.Quota.OnExceeded;

                // Only a limit above the one the quota ran out at gives a used-up app its traffic
                // back. Usage can drop (an app's bytes stop counting when its path changes), so
                // being under a lower limit proves nothing, even one raised again since. A new
                // limit or warning level that leaves usage under the warning level warns again on
                // the way up.
                if (state.IsExceeded)
                {
                    if (raisedPastUsedUp && state.PercentUsed < 100)
                    {
                        state.IsExceeded = false;
                        state.ExceededNotified = false;
                        state.WarningNotified = false;
                    }
                }
                else if (levelsChanged && state.PercentUsed < state.WarningPercent)
                {
                    state.WarningNotified = false;
                }

                // Each alert carries a copy taken here: the live state can change, or be reset,
                // before a handler (or a plugin on another thread) reads it.
                if (state.PercentUsed >= 100 && !state.ExceededNotified)
                {
                    state.IsExceeded = true;
                    state.UsedUpAtLimitBytes = state.LimitBytes;
                    state.ExceededNotified = true;
                    notifications.Add((OnQuotaExceeded, DisplayName(rule), CopyState(state)));
                }
                else if (state.PercentUsed >= rule.Quota.WarningPercent && !state.WarningNotified)
                {
                    state.WarningNotified = true;
                    notifications.Add((OnQuotaWarning, DisplayName(rule), CopyState(state)));
                }
            }
        }
    }

    /// <summary>The state of the first quota on <paramref name="processName"/>.</summary>
    public QuotaState? GetQuotaState(string processName)
    {
        var state = _quotas.Values.FirstOrDefault(s =>
            s.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        if (state is null)
            return null;
        lock (state)
            return CopyState(state);
    }

    public QuotaState? GetQuotaState(Guid ruleId)
    {
        if (!_quotas.TryGetValue(ruleId, out var state))
            return null;
        lock (state)
            return CopyState(state);
    }

    /// <summary>
    /// Rules that are on, with a quota and something to match: a service, a process name, a
    /// path, or a name and a path. A rule that's off doesn't count, or it came back on already used up.
    /// </summary>
    private static List<BandwidthRule> QuotaRules(IReadOnlyList<BandwidthRule> rules) =>
        rules.Where(Counts).ToList();

    /// <summary>Whether the rule's quota counts: it's on, with a quota and something to match.</summary>
    internal static bool Counts(BandwidthRule rule) =>
        rule.Enabled && rule.Quota is not null
        && (rule.ServiceName is not null || rule.ProcessName is not null || rule.ProcessPath is not null);

    /// <summary>
    /// What the processes the rule covers sent and received. A service shares its svchost's
    /// traffic with whatever else runs in it, so a quota on one counts all of it.
    /// </summary>
    private static long UsageOf(BandwidthRule rule, IReadOnlyList<ProcessTrafficInfo> processes) =>
        processes
            .Where(p => rule.MatchesProcess(p.ProcessName, p.ProcessPath, p.Services))
            .Sum(p => p.TotalBytesReceived + p.TotalBytesSent);

    private static string DisplayName(BandwidthRule rule) =>
        rule.ServiceName ?? rule.ProcessName ?? rule.ProcessPath ?? string.Empty;

    private static QuotaState NewState(BandwidthRule rule, long baselineBytes, long stops) => new()
    {
        RuleId = rule.Id,
        ProcessName = DisplayName(rule),
        ProcessPath = rule.ProcessPath,
        ServiceName = rule.ServiceName,
        BaselineBytes = baselineBytes,
        Stops = stops
    };

    public IReadOnlyList<QuotaState> GetAllQuotaStates()
    {
        var result = new List<QuotaState>();
        foreach (var state in _quotas.Values)
        {
            lock (state)
                result.Add(CopyState(state));
        }
        return result;
    }

    private static QuotaState CopyState(QuotaState s) => new()
    {
        RuleId = s.RuleId,
        ProcessName = s.ProcessName,
        ProcessPath = s.ProcessPath,
        ServiceName = s.ServiceName,
        LimitBytes = s.LimitBytes,
        UsedBytes = s.UsedBytes,
        BaselineBytes = s.BaselineBytes,
        Period = s.Period,
        Action = s.Action,
        WarningPercent = s.WarningPercent,
        IsExceeded = s.IsExceeded,
        WarningNotified = s.WarningNotified,
        ExceededNotified = s.ExceededNotified
    };

    public void ResetPeriod(QuotaPeriod period)
    {
        lock (_updateLock)
        {
            var processes = _trafficMonitor.GetUsageTotals();
            var rules = _ruleEngine.GetAllRules();

            // A removed rule's use belongs to the period that just ended. Put back later, it
            // counts from then, like any new rule.
            foreach (var id in _removed.Where(p => p.Value.Period == period).Select(p => p.Key).ToList())
                _removed.Remove(id);

            foreach (var state in _quotas.Values.Where(s => s.Period == period))
            {
                var rule = rules.FirstOrDefault(r => r.Id == state.RuleId);
                if (rule is null)
                {
                    // Its rule went after the last update. A baseline of 0 would charge the
                    // apps' whole history if the rule came back, so drop it instead.
                    _quotas.TryRemove(state.RuleId, out _);
                    continue;
                }
                long currentTotal = UsageOf(rule, processes);

                lock (state)
                {
                    state.BaselineBytes = currentTotal;
                    state.UsedBytes = 0;
                    state.IsExceeded = false;
                    state.WarningNotified = false;
                    state.ExceededNotified = false;
                }
            }
        }
    }
}

public class QuotaState
{
    public Guid RuleId { get; set; }

    /// <summary>The rule's service, else its process name, else its path.</summary>
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }

    /// <summary>The service the rule is on, when it's on one.</summary>
    public string? ServiceName { get; set; }
    public long LimitBytes { get; set; }
    public long UsedBytes { get; set; }
    public long BaselineBytes { get; set; }
    public QuotaPeriod Period { get; set; }
    public QuotaAction Action { get; set; }
    public int WarningPercent { get; set; }
    public bool IsExceeded { get; set; }
    public bool WarningNotified { get; set; }
    public bool ExceededNotified { get; set; }

    /// <summary>The limit in force when the quota was used up. Only a higher limit lifts it.</summary>
    internal long UsedUpAtLimitBytes { get; set; }

    /// <summary>How many times the rule had been turned off when this count started.</summary>
    internal long Stops { get; set; }
    public int PercentUsed => LimitBytes > 0 ? (int)(UsedBytes * 100 / LimitBytes) : 0;

    /// <summary>Whether this state still counts the apps <paramref name="rule"/> matches.</summary>
    internal bool IsFor(BandwidthRule rule) =>
        ProcessName.Equals(rule.ServiceName ?? rule.ProcessName ?? rule.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProcessPath, rule.ProcessPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ServiceName, rule.ServiceName, StringComparison.OrdinalIgnoreCase);
}
