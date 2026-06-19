using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Core.Interfaces;

public interface IRuleEngine
{
    void AddRule(BandwidthRule rule);
    void RemoveRule(Guid ruleId);
    void UpdateRule(BandwidthRule rule);
    BandwidthRule? GetRule(Guid ruleId);
    IReadOnlyList<BandwidthRule> GetAllRules();
    BandwidthRule? FindMatchingRule(string processName, string? processPath);
    void LoadRules(string filePath);
    void SaveRules(string filePath);
}
