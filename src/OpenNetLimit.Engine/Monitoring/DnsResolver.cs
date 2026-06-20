using System.Collections.Concurrent;
using System.Net;

namespace OpenNetLimit.Engine.Monitoring;

public class DnsResolver
{
    private readonly ConcurrentDictionary<IPAddress, CacheEntry> _cache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(10);
    private const int MaxCacheSize = 10_000;

    public string? Resolve(IPAddress address)
    {
        if (_cache.TryGetValue(address, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            return entry.Hostname;

        return null;
    }

    public async Task<string?> ResolveAsync(IPAddress address)
    {
        if (_cache.TryGetValue(address, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
            return entry.Hostname;

        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(address);
            var hostname = hostEntry.HostName;
            _cache[address] = new CacheEntry(hostname, DateTime.UtcNow + _cacheDuration);
            EvictIfNeeded();
            return hostname;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _cache[address] = new CacheEntry(null, DateTime.UtcNow + _cacheDuration);
            return null;
        }
    }

    public void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (now >= kvp.Value.ExpiresAt)
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    private void EvictIfNeeded()
    {
        if (_cache.Count <= MaxCacheSize)
            return;

        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (_cache.Count <= MaxCacheSize * 3 / 4)
                break;
            if (now >= kvp.Value.ExpiresAt)
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    public int CacheSize => _cache.Count;

    internal readonly record struct CacheEntry(string? Hostname, DateTime ExpiresAt);
}
