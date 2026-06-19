using System.Collections.Concurrent;
using System.Net;

namespace OpenNetLimit.Engine.Monitoring;

public class DnsResolver
{
    private readonly ConcurrentDictionary<IPAddress, string?> _cache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<IPAddress, DateTime> _cacheExpiry = new();

    public string? Resolve(IPAddress address)
    {
        if (_cache.TryGetValue(address, out var cached) &&
            _cacheExpiry.TryGetValue(address, out var expiry) &&
            DateTime.UtcNow < expiry)
        {
            return cached;
        }

        return null;
    }

    public async Task<string?> ResolveAsync(IPAddress address)
    {
        if (_cache.TryGetValue(address, out var cached) &&
            _cacheExpiry.TryGetValue(address, out var expiry) &&
            DateTime.UtcNow < expiry)
        {
            return cached;
        }

        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            var hostname = entry.HostName;
            _cache[address] = hostname;
            _cacheExpiry[address] = DateTime.UtcNow + _cacheDuration;
            return hostname;
        }
        catch
        {
            _cache[address] = null;
            _cacheExpiry[address] = DateTime.UtcNow + _cacheDuration;
            return null;
        }
    }

    public int CacheSize => _cache.Count;
}
