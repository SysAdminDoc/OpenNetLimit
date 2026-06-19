using System.Text.Json;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleEngine : IRuleEngine
{
    private readonly object _lock = new();
    private readonly List<BandwidthRule> _rules = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void AddRule(BandwidthRule rule)
    {
        lock (_lock)
        {
            _rules.Add(rule);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    public void RemoveRule(Guid ruleId)
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
        }
    }

    public void UpdateRule(BandwidthRule rule)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
                _rules[index] = rule;
            else
                _rules.Add(rule);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    public BandwidthRule? GetRule(Guid ruleId)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == ruleId);
        }
    }

    public IReadOnlyList<BandwidthRule> GetAllRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }

    public BandwidthRule? FindMatchingRule(string processName, string? processPath)
    {
        lock (_lock)
        {
            return _rules
                .Where(r => r.IsActiveNow() && r.MatchesProcess(processName, processPath))
                .FirstOrDefault();
        }
    }

    public void LoadRules(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(json, JsonOptions);
        if (rules is null) return;

        lock (_lock)
        {
            _rules.Clear();
            _rules.AddRange(rules);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
    }

    public void SaveRules(string filePath)
    {
        List<BandwidthRule> snapshot;
        lock (_lock)
        {
            snapshot = _rules.ToList();
        }

        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(filePath, json);
    }
}
