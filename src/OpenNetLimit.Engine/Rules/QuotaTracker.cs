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
    /// while the quota timer may be doing the same; without this the timer could finish last with
    /// the rules it read before the edit and put the old app's used-up state back.
    /// </summary>
    private readonly object _updateLock = new();

    /// <summary>
    /// Whether an update has run. Rules present at the first update count the traffic since the
    /// service started; a rule added or pointed at another app later counts from that update on.
    /// </summary>
    private bool _started;

    private readonly Func<long> _nowMs;

    /// <summary>The local time, which says when a quota's day, week or month started.</summary>
    private readonly Func<DateTime> _localNow;

    /// <summary>
    /// Quota states saved before the service restarted, waiting for the first update.
    /// Only read and written under <see cref="_updateLock"/>.
    /// </summary>
    private readonly Dictionary<Guid, SavedQuota> _saved = [];

    /// <summary>
    /// Saved quotas whose rule wasn't there at all at the first update, as when rules.json didn't
    /// load. They're saved again until their period ends or their rule turns up, so fixing the
    /// rules and restarting gets them back. Only read and written under <see cref="_updateLock"/>.
    /// </summary>
    private readonly Dictionary<Guid, SavedQuota> _unclaimed = [];

    /// <summary>
    /// When the last update ran, by the tick count, or null when the next check is due at once.
    /// Only read and written under <see cref="_updateLock"/>.
    /// </summary>
    private long? _lastUpdateMs;

    /// <summary>
    /// Whether a Block or Throttle quota still had traffic left at the last update. One can run
    /// out between two checks, and everything it lets through until the next is past its cap.
    /// Only read and written under <see cref="_updateLock"/>.
    /// </summary>
    private bool _canRunOut;

    /// <summary>How often <see cref="UpdateIfDue"/> checks while a Block or Throttle quota has traffic left.</summary>
    internal static readonly TimeSpan CheckEveryNearACap = TimeSpan.FromSeconds(1);

    /// <summary>How often it checks otherwise, for warnings and usage on the Rules tab.</summary>
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    public event Action<string, QuotaState>? OnQuotaWarning;
    public event Action<string, QuotaState>? OnQuotaExceeded;

    public QuotaTracker(IRuleEngine ruleEngine, ITrafficMonitor trafficMonitor)
        : this(ruleEngine, trafficMonitor, () => Environment.TickCount64)
    {
    }

    internal QuotaTracker(IRuleEngine ruleEngine, ITrafficMonitor trafficMonitor, Func<long> nowMs)
        : this(ruleEngine, trafficMonitor, nowMs, () => DateTime.Now)
    {
    }

    internal QuotaTracker(IRuleEngine ruleEngine, ITrafficMonitor trafficMonitor, Func<long> nowMs, Func<DateTime> localNow)
    {
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
        _nowMs = nowMs;
        _localNow = localNow;
    }

    /// <summary>
    /// Takes the quota states saved before the service restarted. The first update carries each
    /// one on for its rule, when the rule still counts the same apps over the same period and that
    /// period hasn't ended. Any other quota counts from the restart, and one whose rule is missing
    /// altogether is saved again until its period ends. Ignored once an update has run.
    /// </summary>
    public void Restore(IEnumerable<SavedQuota?> saved)
    {
        lock (_updateLock)
        {
            if (_started)
                return;
            foreach (var quota in saved)
            {
                if (quota is not null)
                    _saved[quota.RuleId] = quota;
            }
        }
    }

    /// <summary>What each quota has used in its period, for <see cref="Restore"/> after a restart.</summary>
    public IReadOnlyList<SavedQuota> SaveStates()
    {
        var result = new List<SavedQuota>();
        lock (_updateLock)
        {
            var now = _localNow();
            foreach (var ended in _unclaimed.Values.Where(q => q.PeriodStart != PeriodStartOf(q.Period, now)).ToList())
                _unclaimed.Remove(ended.RuleId);
            result.AddRange(_unclaimed.Values);
        }
        foreach (var state in _quotas.Values)
        {
            lock (state)
            {
                result.Add(new SavedQuota
                {
                    RuleId = state.RuleId,
                    ProcessName = state.ProcessName,
                    ProcessPath = state.ProcessPath,
                    ServiceName = state.ServiceName,
                    Period = state.Period,
                    PeriodStart = state.PeriodStart,
                    UsedBytes = state.UsedBytes,
                    IsExceeded = state.IsExceeded,
                    UsedUpAtLimitBytes = state.UsedUpAtLimitBytes,
                    WarningNotified = state.WarningNotified,
                    ExceededNotified = state.ExceededNotified
                });
            }
        }
        result.Sort((a, b) => a.RuleId.CompareTo(b.RuleId));
        return result;
    }

    /// <summary>
    /// The first day of the quota period <paramref name="now"/> falls in: that day, that week's
    /// Monday, or the first of that month. Periods reset at midnight on those days.
    /// </summary>
    internal static DateOnly PeriodStartOf(QuotaPeriod period, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        return period switch
        {
            QuotaPeriod.Weekly => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            QuotaPeriod.Monthly => new DateOnly(today.Year, today.Month, 1),
            _ => today
        };
    }

    /// <summary>
    /// Runs <see cref="Update"/> when it's due: every second while a Block or Throttle quota has
    /// traffic left, so a fast download can't run far past it, and every 30 seconds otherwise.
    /// Returns whether it ran.
    /// </summary>
    public bool UpdateIfDue()
    {
        lock (_updateLock)
        {
            var every = _canRunOut ? CheckEveryNearACap : CheckEvery;
            if (_lastUpdateMs is { } last && _nowMs() - last < (long)every.TotalMilliseconds)
                return false;
        }
        Update();
        return true;
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
        var now = _localNow();
        bool firstUpdate = !_started;
        _started = true;

        _lastUpdateMs = _nowMs();
        _canRunOut = false;

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
            // At the first update, a quota saved before the restart carries on from what it had used.
            var state = _quotas.AddOrUpdate(rule.Id,
                _ => (firstUpdate ? Restored(rule, stops, now) : null) ?? NewState(rule, firstUpdate ? 0 : totalBytes, stops, now),
                (_, current) => current.IsFor(rule) && current.Stops == stops ? current : NewState(rule, totalBytes, stops, now));

            lock (state)
            {
                bool raisedPastUsedUp = rule.Quota!.LimitBytes > state.UsedUpAtLimitBytes;
                bool levelsChanged = rule.Quota.LimitBytes != state.LimitBytes
                    || rule.Quota.WarningPercent != state.WarningPercent;
                state.LimitBytes = rule.Quota.LimitBytes;
                state.UsedBytes = totalBytes - state.BaselineBytes;
                // A quota moved to another period keeps its use, now as the new period's.
                if (state.Period != rule.Quota.Period)
                    state.PeriodStart = PeriodStartOf(rule.Quota.Period, now);
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
                    // A warning that it's nearly used up would only come after this one.
                    state.WarningNotified = true;
                    notifications.Add((OnQuotaExceeded, DisplayName(rule), CopyState(state)));
                }
                else if (state.PercentUsed >= rule.Quota.WarningPercent && !state.WarningNotified)
                {
                    state.WarningNotified = true;
                    notifications.Add((OnQuotaWarning, DisplayName(rule), CopyState(state)));
                }

                if (!state.IsExceeded && state.Action != QuotaAction.WarnOnly)
                    _canRunOut = true;
            }
        }

        // A quota whose rule doesn't count at the first update, like one turned off, starts over.
        // One whose rule isn't there at all is kept for the rule coming back at a restart. Added
        // back while the service runs, the rule counts from then, like any rule added later.
        if (firstUpdate)
        {
            foreach (var saved in _saved.Values)
                _unclaimed[saved.RuleId] = saved;
            _saved.Clear();
        }
        foreach (var id in _unclaimed.Keys.Where(existing.Contains).ToList())
            _unclaimed.Remove(id);
    }

    /// <summary>
    /// The state <paramref name="rule"/>'s quota had before the service restarted, or null when
    /// none was saved, the rule now counts other apps or another period, or that period has ended.
    /// </summary>
    private QuotaState? Restored(BandwidthRule rule, long stops, DateTime now)
    {
        if (!_saved.TryGetValue(rule.Id, out var saved) || saved.ProcessName is null || saved.UsedBytes < 0
            || saved.Period != rule.Quota!.Period || saved.PeriodStart != PeriodStartOf(saved.Period, now))
            return null;

        // Its use so far goes below zero in the baseline, so it adds to what's counted from the restart.
        var state = new QuotaState
        {
            RuleId = rule.Id,
            ProcessName = saved.ProcessName,
            ProcessPath = saved.ProcessPath,
            ServiceName = saved.ServiceName,
            BaselineBytes = -saved.UsedBytes,
            Period = saved.Period,
            PeriodStart = saved.PeriodStart,
            IsExceeded = saved.IsExceeded,
            UsedUpAtLimitBytes = saved.UsedUpAtLimitBytes,
            WarningNotified = saved.WarningNotified,
            ExceededNotified = saved.ExceededNotified,
            Stops = stops
        };
        return state.IsFor(rule) ? state : null;
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

    private static QuotaState NewState(BandwidthRule rule, long baselineBytes, long stops, DateTime now) => new()
    {
        RuleId = rule.Id,
        ProcessName = DisplayName(rule),
        ProcessPath = rule.ProcessPath,
        ServiceName = rule.ServiceName,
        BaselineBytes = baselineBytes,
        Period = rule.Quota!.Period,
        PeriodStart = PeriodStartOf(rule.Quota.Period, now),
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
            // Quotas that ran out have their traffic back, so the next check is due at once.
            _lastUpdateMs = null;
            var processes = _trafficMonitor.GetUsageTotals();
            var rules = _ruleEngine.GetAllRules();
            var now = _localNow();

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
                    state.PeriodStart = PeriodStartOf(period, now);
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

    /// <summary>The first day of the period this use belongs to.</summary>
    internal DateOnly PeriodStart { get; set; }
    public int PercentUsed => LimitBytes > 0 ? (int)(UsedBytes * 100 / LimitBytes) : 0;

    /// <summary>Whether this state still counts the apps <paramref name="rule"/> matches.</summary>
    internal bool IsFor(BandwidthRule rule) =>
        ProcessName.Equals(rule.ServiceName ?? rule.ProcessName ?? rule.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProcessPath, rule.ProcessPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ServiceName, rule.ServiceName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A quota's use in its period, kept in <c>quotas.json</c> across a restart of the service.</summary>
public sealed record SavedQuota
{
    public Guid RuleId { get; init; }

    /// <summary>The rule's service, else its process name, else its path, as in <see cref="QuotaState"/>.</summary>
    public string? ProcessName { get; init; }
    public string? ProcessPath { get; init; }
    public string? ServiceName { get; init; }
    public QuotaPeriod Period { get; init; }

    /// <summary>The first day of the period <see cref="UsedBytes"/> was used in.</summary>
    public DateOnly PeriodStart { get; init; }
    public long UsedBytes { get; init; }
    public bool IsExceeded { get; init; }

    /// <summary>The limit in force when the quota was used up. Only a higher limit lifts it.</summary>
    public long UsedUpAtLimitBytes { get; init; }
    public bool WarningNotified { get; init; }
    public bool ExceededNotified { get; init; }
}
