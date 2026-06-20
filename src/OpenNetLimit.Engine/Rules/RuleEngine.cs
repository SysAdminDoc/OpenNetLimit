using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleEngine : IRuleEngine
{
    private readonly object _lock = new();
    private readonly List<BandwidthRule> _rules = [];

    public event Action? OnRulesChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void AddRule(BandwidthRule rule)
    {
        ValidateRule(rule);
        lock (_lock)
        {
            _rules.Add(rule);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
        OnRulesChanged?.Invoke();
    }

    public void RemoveRule(Guid ruleId)
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
        }
        OnRulesChanged?.Invoke();
    }

    public void UpdateRule(BandwidthRule rule)
    {
        ValidateRule(rule);
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
                _rules[index] = rule;
            else
                _rules.Add(rule);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
        OnRulesChanged?.Invoke();
    }

    public BandwidthRule? GetRule(Guid ruleId)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == ruleId)?.Clone();
        }
    }

    public IReadOnlyList<BandwidthRule> GetAllRules()
    {
        lock (_lock)
        {
            return _rules.Select(r => r.Clone()).ToList();
        }
    }

    public BandwidthRule? FindMatchingRule(string processName, string? processPath)
    {
        lock (_lock)
        {
            return _rules
                .Where(r => r.IsActiveNow() && r.MatchesProcess(processName, processPath))
                .FirstOrDefault()?.Clone();
        }
    }

    public BandwidthRule? FindMatchingRule(string processName, string? processPath,
        IPAddress? remoteAddress, int? remotePort, string? protocol, string? countryCode = null, string? resolvedDomain = null)
    {
        lock (_lock)
        {
            return _rules
                .Where(r => r.IsActiveNow() &&
                            r.MatchesProcess(processName, processPath) &&
                            r.MatchesConnection(remoteAddress, remotePort, protocol) &&
                            r.MatchesCountry(countryCode) &&
                            r.MatchesDomain(resolvedDomain))
                .FirstOrDefault()?.Clone();
        }
    }

    public IReadOnlyList<BandwidthRule> GetRulesByGroup(string groupName)
    {
        lock (_lock)
        {
            return _rules
                .Where(r => r.GroupName is not null &&
                            r.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Clone())
                .ToList();
        }
    }

    public IReadOnlyList<string> GetGroupNames()
    {
        lock (_lock)
        {
            return _rules
                .Where(r => r.GroupName is not null)
                .Select(r => r.GroupName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g)
                .ToList();
        }
    }

    public void LoadRules(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);

        List<BandwidthRule>? rules;
        try
        {
            var envelope = JsonSerializer.Deserialize<RuleFileEnvelope>(json, JsonOptions);
            if (envelope?.Rules is not null)
            {
                rules = envelope.Rules;
            }
            else
            {
                rules = JsonSerializer.Deserialize<List<BandwidthRule>>(json, JsonOptions);
            }
        }
        catch (JsonException)
        {
            rules = JsonSerializer.Deserialize<List<BandwidthRule>>(json, JsonOptions);
        }

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

        var envelope = new RuleFileEnvelope
        {
            Version = 1,
            Rules = snapshot
        };

        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public string ExportRules()
    {
        List<BandwidthRule> snapshot;
        lock (_lock)
        {
            snapshot = _rules.ToList();
        }
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public void ImportRules(string json, bool replace = false)
    {
        var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(json, JsonOptions);
        if (rules is null) return;

        lock (_lock)
        {
            if (replace)
                _rules.Clear();
            _rules.AddRange(rules);
            _rules.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }
        OnRulesChanged?.Invoke();
    }

    private static void ValidateRule(BandwidthRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.ProcessPath))
            throw new ArgumentException("Rule must specify at least ProcessName or ProcessPath.");
    }

    private sealed class RuleFileEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("rules")]
        public List<BandwidthRule>? Rules { get; set; }
    }
}
