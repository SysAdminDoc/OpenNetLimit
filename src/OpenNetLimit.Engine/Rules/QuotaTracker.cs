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
        var rules = QuotaRules(_ruleEngine.GetAllRules());
        var processes = _trafficMonitor.GetAllProcesses();
        bool firstUpdate = !_started;
        _started = true;

        var live = rules.Select(r => r.Id).ToHashSet();
        foreach (var ruleId in _quotas.Keys)
        {
            if (!live.Contains(ruleId))
                _quotas.TryRemove(ruleId, out _);
        }

        foreach (var rule in rules)
        {
            long totalBytes = UsageOf(rule, processes);

            // A rule added later, or pointed at a different app, counts from now. The app's
            // earlier traffic can't be split by quota period, so it isn't charged.
            var state = _quotas.AddOrUpdate(rule.Id,
                _ => NewState(rule, firstUpdate ? 0 : totalBytes),
                (_, current) => current.IsFor(rule) ? current : NewState(rule, totalBytes));

            lock (state)
            {
                bool limitChanged = state.LimitBytes != rule.Quota!.LimitBytes;
                state.LimitBytes = rule.Quota.LimitBytes;
                state.UsedBytes = totalBytes - state.BaselineBytes;
                state.Period = rule.Quota.Period;
                state.WarningPercent = rule.Quota.WarningPercent;
                state.Action = rule.Quota.OnExceeded;

                // A raised limit gives the app its traffic back, and nearing or crossing the new
                // one notifies again. Only a limit change does this: usage can't be trusted to only grow.
                if (limitChanged && state.PercentUsed < 100)
                {
                    if (state.IsExceeded)
                    {
                        state.IsExceeded = false;
                        state.ExceededNotified = false;
                        state.WarningNotified = false;
                    }
                    else if (state.PercentUsed < state.WarningPercent)
                    {
                        state.WarningNotified = false;
                    }
                }

                // Each alert carries a copy taken here: the live state can change, or be reset,
                // before a handler (or a plugin on another thread) reads it.
                if (state.PercentUsed >= 100 && !state.ExceededNotified)
                {
                    state.IsExceeded = true;
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

    /// <summary>Rules with a quota and something to match: a process name, a path, or both.</summary>
    private static List<BandwidthRule> QuotaRules(IReadOnlyList<BandwidthRule> rules) =>
        rules.Where(r => r.Quota is not null && (r.ProcessName is not null || r.ProcessPath is not null)).ToList();

    private static long UsageOf(BandwidthRule rule, IReadOnlyList<ProcessTrafficInfo> processes) =>
        processes
            .Where(p => rule.MatchesProcess(p.ProcessName, p.ProcessPath))
            .Sum(p => p.TotalBytesReceived + p.TotalBytesSent);

    private static string DisplayName(BandwidthRule rule) => rule.ProcessName ?? rule.ProcessPath ?? string.Empty;

    private static QuotaState NewState(BandwidthRule rule, long baselineBytes) => new()
    {
        RuleId = rule.Id,
        ProcessName = DisplayName(rule),
        ProcessPath = rule.ProcessPath,
        BaselineBytes = baselineBytes
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
            var processes = _trafficMonitor.GetAllProcesses();
            var rules = _ruleEngine.GetAllRules();

            foreach (var state in _quotas.Values.Where(s => s.Period == period))
            {
                var rule = rules.FirstOrDefault(r => r.Id == state.RuleId);
                long currentTotal = rule is not null ? UsageOf(rule, processes) : 0;

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

    /// <summary>The rule's process name, or its path when it only has a path.</summary>
    public string ProcessName { get; set; } = string.Empty;
    public string? ProcessPath { get; set; }
    public long LimitBytes { get; set; }
    public long UsedBytes { get; set; }
    public long BaselineBytes { get; set; }
    public QuotaPeriod Period { get; set; }
    public QuotaAction Action { get; set; }
    public int WarningPercent { get; set; }
    public bool IsExceeded { get; set; }
    public bool WarningNotified { get; set; }
    public bool ExceededNotified { get; set; }
    public int PercentUsed => LimitBytes > 0 ? (int)(UsedBytes * 100 / LimitBytes) : 0;

    /// <summary>Whether this state still counts the apps <paramref name="rule"/> matches.</summary>
    internal bool IsFor(BandwidthRule rule) =>
        ProcessName.Equals(rule.ProcessName ?? rule.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProcessPath, rule.ProcessPath, StringComparison.OrdinalIgnoreCase);
}
