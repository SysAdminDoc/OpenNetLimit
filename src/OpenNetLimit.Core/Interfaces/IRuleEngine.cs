using System.Net;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IRuleEngine
{
    void AddRule(BandwidthRule rule);
    void RemoveRule(Guid ruleId);
    void UpdateRule(BandwidthRule rule);
    BandwidthRule? GetRule(Guid ruleId);
    IReadOnlyList<BandwidthRule> GetAllRules();

    /// <summary>
    /// The rules, read together with how many times a change left each rule's quota not counting
    /// (the rule off, or its quota gone). A quota that never got checked while its rule was off
    /// still sees it was turned off, and starts over. Ids that were never turned off are missing.
    /// </summary>
    IReadOnlyList<BandwidthRule> GetAllRules(out IReadOnlyDictionary<Guid, long> quotaStops);
    /// <param name="services">The services running in the process, which service rules match on.</param>
    BandwidthRule? FindMatchingRule(string processName, string? processPath, IReadOnlyList<string>? services = null);
    BandwidthRule? FindMatchingRule(string processName, string? processPath, IPAddress? remoteAddress, int? remotePort, string? protocol, string? countryCode = null, string? resolvedDomain = null, IReadOnlyList<string>? services = null);
    IReadOnlyList<BandwidthRule> GetRulesByGroup(string groupName);
    IReadOnlyList<string> GetGroupNames();
    void LoadRules(string filePath);
    void SaveRules(string filePath);
    string ExportRules();
    void ImportRules(string json, bool replace = false);
}
