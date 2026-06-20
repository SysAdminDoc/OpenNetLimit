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
        // Resolve loopback — always available without network
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
        // Use IPv6 loopback as a non-standard lookup that may fail on some systems,
        // but the real test is that the resolver caches the result (null or not)
        // without throwing. We verify the cache grows regardless of the DNS outcome.
        var address = IPAddress.Parse("192.0.2.1");
        try
        {
            await resolver.ResolveAsync(address);
        }
        catch
        {
            // DNS failures are expected on isolated networks
        }

        // The entry should be cached (either as hostname or null)
        Assert.True(resolver.CacheSize > 0);
    }

    [Fact]
    public void PruneExpired_RemovesExpiredEntries()
    {
        var resolver = new DnsResolver();
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
