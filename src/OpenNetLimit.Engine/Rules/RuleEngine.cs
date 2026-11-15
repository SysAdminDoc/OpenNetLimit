using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenNetLimit.Core;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Rules;

public class RuleEngine : IRuleEngine
{
    private readonly object _lock = new();
    private readonly List<BandwidthRule> _rules = [];

    /// <summary>
    /// Each rule's place among rules of equal priority, lowest first: the order they were added
    /// or loaded in. The first rule that matches wins, so this decides which one applies.
    /// </summary>
    private readonly Dictionary<Guid, long> _order = [];

    /// <summary>
    /// The places of rules removed lately. Adding one back with its id (the Rules tab's undo, or
    /// importing an export) puts it where it was among the rules of equal priority, however
    /// those moved or changed priority since.
    /// </summary>
    private readonly Dictionary<Guid, long> _removedOrder = [];
    private const int MaxRemembered = 64;
    private long _nextOrder;

    public event Action? OnRulesChanged;

    // The converter lets an imported or hand-edited file name its enums ("action": "Block")
    // like the pipe and REST requests can. It still writes numbers, so the file format is unchanged.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new EnumNameOrNumberConverter() }
    };

    public void AddRule(BandwidthRule rule)
    {
        ValidateRule(rule);
        lock (_lock)
        {
            // Edits, removal and quotas find a rule by id. A rule copied from an export keeps
            // its id, so a new rule whose id is taken gets its own.
            if (rule.Id == Guid.Empty || _rules.Any(r => r.Id == rule.Id))
                rule.Id = Guid.NewGuid();
            Insert(rule);
            SortByPriority();
        }
        OnRulesChanged?.Invoke();
    }

    public void RemoveRule(Guid ruleId)
    {
        lock (_lock)
        {
            if (_order.Remove(ruleId, out var place))
            {
                if (_removedOrder.Count >= MaxRemembered)
                    _removedOrder.Clear();
                _removedOrder[ruleId] = place;
            }
            _rules.RemoveAll(r => r.Id == ruleId);
        }
        OnRulesChanged?.Invoke();
    }

    /// <summary>
    /// Adds a rule that isn't in the list: back in its old place when it was removed lately,
    /// or else after every rule of its priority. Caller holds the lock and sorts after.
    /// </summary>
    private void Insert(BandwidthRule rule)
    {
        _order[rule.Id] = _removedOrder.Remove(rule.Id, out var place) ? place : _nextOrder++;
        _rules.Add(rule);
    }

    /// <summary>
    /// Highest priority first, and rules of equal priority in their own order, since the first
    /// one that matches wins. List.Sort is unstable and could swap them on any change.
    /// Caller holds the lock.
    /// </summary>
    private void SortByPriority()
    {
        var sorted = _rules.OrderByDescending(r => r.Priority).ThenBy(r => _order[r.Id]).ToList();
        _rules.Clear();
        _rules.AddRange(sorted);
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
                Insert(rule);
            SortByPriority();
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
        // A hand-edited file can hold a null entry. It has nothing to keep, and the rest still loads.
        rules.RemoveAll(r => r is null);
        GiveEachItsOwnId(rules);

        lock (_lock)
        {
            _rules.Clear();
            _order.Clear();
            _removedOrder.Clear();
            foreach (var rule in rules)
                Insert(rule);
            SortByPriority();
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

        // Validate each imported rule to prevent invalid entries from polluting the rule list
        foreach (var rule in rules)
            ValidateRule(rule);
        GiveEachItsOwnId(rules);

        lock (_lock)
        {
            // A replacing import sets the whole order, so nothing removed earlier takes its old
            // place among the new rules.
            if (replace)
            {
                _rules.Clear();
                _order.Clear();
                _removedOrder.Clear();
            }
            // An imported rule that is already here replaces it, so importing the same file
            // twice doesn't leave two copies sharing one id. One removed lately goes back where it was.
            foreach (var rule in rules)
            {
                var index = _rules.FindIndex(r => r.Id == rule.Id);
                if (index >= 0)
                    _rules[index] = rule;
                else
                    Insert(rule);
            }
            SortByPriority();
        }
        OnRulesChanged?.Invoke();
    }

    private static void ValidateRule(BandwidthRule? rule)
    {
        // An import list with a null in it threw a NullReferenceException, which dropped the
        // client's connection instead of telling it what was wrong.
        if (rule is null)
            throw new ArgumentException("A rule in the list is empty.");
        if (string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.ProcessPath))
            throw new ArgumentException("Rule must specify at least ProcessName or ProcessPath.");
    }

    /// <summary>
    /// Gives a rule a new id when it has none or an earlier rule in <paramref name="rules"/> has
    /// it. Two rules sharing an id can't be edited or removed apart, and their quotas collide.
    /// Both rules are kept, since a hand-edited file may reuse one id for different rules.
    /// The new id comes from the old one and how many times it was seen, so loading or importing
    /// the same file again gives the same ids, and an import replaces those rules instead of
    /// adding them again. It never takes an id another rule in the file already has. An empty id
    /// gets a random one, as in <see cref="AddRule"/>: one derived from the empty id would be the
    /// same in every file, so importing a second file would overwrite the first one's rule.
    /// </summary>
    private static void GiveEachItsOwnId(List<BandwidthRule> rules)
    {
        var taken = rules.Where(r => r.Id != Guid.Empty).Select(r => r.Id).ToHashSet();
        var kept = new HashSet<Guid>();
        var timesSeen = new Dictionary<Guid, int>();
        foreach (var rule in rules)
        {
            if (rule.Id != Guid.Empty && kept.Add(rule.Id))
                continue;
            var original = rule.Id;
            int count = timesSeen.GetValueOrDefault(original);
            Guid id;
            do
            {
                id = original == Guid.Empty ? Guid.NewGuid() : DerivedId(original, ++count);
            } while (!taken.Add(id));
            timesSeen[original] = count;
            rule.Id = id;
        }
    }

    private static Guid DerivedId(Guid original, int occurrence)
    {
        Span<byte> input = stackalloc byte[20];
        original.TryWriteBytes(input);
        BitConverter.TryWriteBytes(input[16..], occurrence);
        return new Guid(SHA256.HashData(input)[..16]);
    }

    private sealed class RuleFileEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("rules")]
        public List<BandwidthRule>? Rules { get; set; }
    }
}
