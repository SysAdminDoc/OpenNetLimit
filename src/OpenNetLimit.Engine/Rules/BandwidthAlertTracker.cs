using System.Collections.Concurrent;
using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class BandwidthAlertTracker
{
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly List<BandwidthAlertRule> _rules = [];
    private readonly ConcurrentQueue<BandwidthAlertEvent> _events = new();
    private readonly Dictionary<string, DateTime> _lastTriggered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly object _trimLock = new();

    public const int MaxEvents = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public event Action<BandwidthAlertEvent>? OnAlert;

    public BandwidthAlertTracker(ITrafficMonitor trafficMonitor)
    {
        _trafficMonitor = trafficMonitor;
    }

    public IReadOnlyList<BandwidthAlertRule> GetRules()
    {
        lock (_lock)
            return _rules.Select(CopyRule).ToList();
    }

    public BandwidthAlertRule? GetRule(Guid id)
    {
        lock (_lock)
            return _rules.FirstOrDefault(r => r.Id == id) is { } rule ? CopyRule(rule) : null;
    }

    public void AddRule(BandwidthAlertRule rule)
    {
        lock (_lock)
        {
            if (rule.Id == Guid.Empty)
                rule.Id = Guid.NewGuid();
            _rules.RemoveAll(r => r.Id == rule.Id);
            _rules.Add(CopyRule(rule));
        }
    }

    public void UpdateRule(BandwidthAlertRule rule) => AddRule(rule);

    public void RemoveRule(Guid id)
    {
        lock (_lock)
            _rules.RemoveAll(r => r.Id == id);
    }

    public IReadOnlyList<BandwidthAlertEvent> GetRecentEvents(int maxCount = 100)
    {
        var snapshot = _events.ToArray();
        var start = Math.Max(0, snapshot.Length - maxCount);
        return snapshot[start..].Select(CopyEvent).ToList();
    }

    public void Update()
    {
        // Prune stale _lastTriggered entries to prevent unbounded growth
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-300);
            var staleKeys = _lastTriggered
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleKeys)
                _lastTriggered.Remove(key);
        }

        var processes = _trafficMonitor.GetAllProcesses();
        List<BandwidthAlertRule> rules;
        lock (_lock)
            rules = _rules.Select(CopyRule).ToList();

        foreach (var rule in rules)
        {
            foreach (var process in processes)
            {
                if (!rule.Matches(process))
                    continue;

                var observed = rule.GetObservedBytesPerSecond(process);
                if (observed < rule.ThresholdBytesPerSecond)
                    continue;

                var key = $"{rule.Id}:{process.ProcessId}";
                var now = DateTime.UtcNow;
                lock (_lock)
                {
                    if (_lastTriggered.TryGetValue(key, out var last) &&
                        now - last < TimeSpan.FromSeconds(Math.Max(1, rule.CooldownSeconds)))
                    {
                        continue;
                    }

                    _lastTriggered[key] = now;
                }

                var alert = new BandwidthAlertEvent
                {
                    RuleId = rule.Id,
                    RuleName = string.IsNullOrWhiteSpace(rule.Name) ? "Bandwidth alert" : rule.Name,
                    ProcessId = process.ProcessId,
                    ProcessName = process.ProcessName,
                    ProcessPath = process.ProcessPath,
                    Direction = rule.Direction,
                    ObservedBytesPerSecond = observed,
                    ThresholdBytesPerSecond = rule.ThresholdBytesPerSecond,
                    Message = BuildMessage(rule, process, observed)
                };

                _events.Enqueue(alert);
                lock (_trimLock)
                {
                    while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }
                }
                OnAlert?.Invoke(alert);
            }
        }
    }

    public void LoadRules(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        var json = File.ReadAllText(filePath);
        var rules = JsonSerializer.Deserialize<List<BandwidthAlertRule>>(json, JsonOptions) ?? [];
        lock (_lock)
        {
            _rules.Clear();
            _rules.AddRange(rules.Select(CopyRule));
        }
    }

    public void SaveRules(string filePath)
    {
        List<BandwidthAlertRule> snapshot;
        lock (_lock)
            snapshot = _rules.Select(CopyRule).ToList();

        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        if (File.Exists(filePath))
            File.Replace(tempPath, filePath, null);
        else
            File.Move(tempPath, filePath);
    }

    private static string BuildMessage(BandwidthAlertRule rule, ProcessTrafficInfo process, long observed)
    {
        var name = string.IsNullOrWhiteSpace(rule.Name) ? "Bandwidth alert" : rule.Name;
        return $"{name}: {process.ProcessName} reached {FormatBytes(observed)} (threshold {FormatBytes(rule.ThresholdBytesPerSecond)})";
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B/s",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB/s",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB/s",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB/s"
        };
    }

    private static BandwidthAlertRule CopyRule(BandwidthAlertRule rule) =>
        new()
        {
            Id = rule.Id,
            Name = rule.Name,
            Enabled = rule.Enabled,
            ProcessName = rule.ProcessName,
            ProcessPath = rule.ProcessPath,
            Direction = rule.Direction,
            ThresholdBytesPerSecond = rule.ThresholdBytesPerSecond,
            CooldownSeconds = rule.CooldownSeconds
        };

    private static BandwidthAlertEvent CopyEvent(BandwidthAlertEvent alert) =>
        new()
        {
            Id = alert.Id,
            RuleId = alert.RuleId,
            RuleName = alert.RuleName,
            ProcessId = alert.ProcessId,
            ProcessName = alert.ProcessName,
            ProcessPath = alert.ProcessPath,
            Direction = alert.Direction,
            ObservedBytesPerSecond = alert.ObservedBytesPerSecond,
            ThresholdBytesPerSecond = alert.ThresholdBytesPerSecond,
            TriggeredAt = alert.TriggeredAt,
            Message = alert.Message
        };
}
