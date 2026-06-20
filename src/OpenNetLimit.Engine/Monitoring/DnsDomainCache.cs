using System.Collections.Concurrent;
using System.Net;

namespace OpenNetLimit.Engine.Monitoring;

public class DnsDomainCache
{
    private readonly ConcurrentDictionary<IPAddress, DomainEntry> _ipToDomain = new();
    private const int MaxEntries = 50_000;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    public void RecordMapping(IPAddress ip, string domain, TimeSpan? ttl = null)
    {
        var expiry = DateTime.UtcNow + (ttl ?? DefaultTtl);
        _ipToDomain[ip] = new DomainEntry(domain, expiry);

        if (_ipToDomain.Count > MaxEntries)
            PruneExpired();
    }

    public string? LookupDomain(IPAddress ip)
    {
        if (_ipToDomain.TryGetValue(ip, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            return entry.Domain;
        return null;
    }

    public void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _ipToDomain)
        {
            if (now >= kvp.Value.ExpiresAt)
                _ipToDomain.TryRemove(kvp.Key, out _);
        }
    }

    public int Count => _ipToDomain.Count;

    private readonly record struct DomainEntry(string Domain, DateTime ExpiresAt);
}
