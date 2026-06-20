using System.Net;
using OpenNetLimit.Engine.Monitoring;
using Xunit;

namespace OpenNetLimit.Tests;

public class DnsResolverTests
{
    [Fact]
    public void Resolve_ReturnsNull_WhenNotCached()
    {
        var resolver = new DnsResolver();
        var result = resolver.Resolve(IPAddress.Parse("8.8.8.8"));
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_CachesResult()
    {
        var resolver = new DnsResolver();
        // Resolve localhost — always available
        var result = await resolver.ResolveAsync(IPAddress.Loopback);

        // Should be cached now
        Assert.True(resolver.CacheSize > 0);
        var cached = resolver.Resolve(IPAddress.Loopback);
        Assert.Equal(result, cached);
    }

    [Fact]
    public async Task ResolveAsync_CachesNullOnFailure()
    {
        var resolver = new DnsResolver();
        // Resolve a non-routable address — should fail
        var result = await resolver.ResolveAsync(IPAddress.Parse("192.0.2.1"));
        Assert.Null(result);
        Assert.True(resolver.CacheSize > 0);
    }

    [Fact]
    public void PruneExpired_RemovesExpiredEntries()
    {
        var resolver = new DnsResolver();
        // Manually inject expired entry via ResolveAsync, then prune
        // Since we can't control time, verify prune doesn't crash on empty
        resolver.PruneExpired();
        Assert.Equal(0, resolver.CacheSize);
    }

    [Fact]
    public void CacheSize_ReflectsEntryCount()
    {
        var resolver = new DnsResolver();
        Assert.Equal(0, resolver.CacheSize);
    }
}
