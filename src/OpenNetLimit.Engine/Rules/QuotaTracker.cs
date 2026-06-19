using System.Collections.Concurrent;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class QuotaTracker
{
    private readonly ConcurrentDictionary<string, QuotaState> _quotas = new(StringComparer.OrdinalIgnoreCase);
    private readonly IRuleEngine _ruleEngine;
    private readonly ITrafficMonitor _trafficMonitor;

    public event Action<string, QuotaState>? OnQuotaWarning;
    public event Action<string, QuotaState>? OnQuotaExceeded;

    public QuotaTracker(IRuleEngine ruleEngine, ITrafficMonitor trafficMonitor)
    {
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
    }

    public void Update()
    {
        var rules = _ruleEngine.GetAllRules();
        var processes = _trafficMonitor.GetAllProcesses();

        foreach (var rule in rules)
        {
            if (rule.Quota is null || rule.ProcessName is null) continue;

            var matchingProcesses = processes.Where(p =>
                rule.MatchesProcess(p.ProcessName, p.ProcessPath)).ToList();

            long totalBytes = matchingProcesses.Sum(p => p.TotalBytesReceived + p.TotalBytesSent);

            var state = _quotas.GetOrAdd(rule.ProcessName, _ => new QuotaState());
            state.RuleId = rule.Id;
            state.ProcessName = rule.ProcessName;
            state.LimitBytes = rule.Quota.LimitBytes;
            state.UsedBytes = totalBytes;
            state.Period = rule.Quota.Period;
            state.WarningPercent = rule.Quota.WarningPercent;
            state.Action = rule.Quota.OnExceeded;

            var percentUsed = rule.Quota.LimitBytes > 0
                ? (int)(totalBytes * 100 / rule.Quota.LimitBytes) : 0;

            if (percentUsed >= 100 && !state.ExceededNotified)
            {
                state.IsExceeded = true;
                state.ExceededNotified = true;
                OnQuotaExceeded?.Invoke(rule.ProcessName, state);
            }
            else if (percentUsed >= rule.Quota.WarningPercent && !state.WarningNotified)
            {
                state.WarningNotified = true;
                OnQuotaWarning?.Invoke(rule.ProcessName, state);
            }
        }
    }

    public QuotaState? GetQuotaState(string processName)
    {
        _quotas.TryGetValue(processName, out var state);
        return state;
    }

    public IReadOnlyList<QuotaState> GetAllQuotaStates() =>
        _quotas.Values.ToList();

    public void ResetPeriod(QuotaPeriod period)
    {
        foreach (var state in _quotas.Values.Where(s => s.Period == period))
        {
            state.UsedBytes = 0;
            state.IsExceeded = false;
            state.WarningNotified = false;
            state.ExceededNotified = false;
        }
    }
}

public class QuotaState
{
    public Guid RuleId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public long LimitBytes { get; set; }
    public long UsedBytes { get; set; }
    public QuotaPeriod Period { get; set; }
    public QuotaAction Action { get; set; }
    public int WarningPercent { get; set; }
    public bool IsExceeded { get; set; }
    public bool WarningNotified { get; set; }
    public bool ExceededNotified { get; set; }
    public int PercentUsed => LimitBytes > 0 ? (int)(UsedBytes * 100 / LimitBytes) : 0;
}
