using System.Net;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using Xunit;

namespace OpenNetLimit.Tests;

public class DnsDomainTests
{
    [Fact]
    public void MatchesDomain_NullFilter_MatchesAnything()
    {
        var rule = new BandwidthRule { ProcessName = "test", DnsDomainFilter = null };
        Assert.True(rule.MatchesDomain(null));
        Assert.True(rule.MatchesDomain("example.com"));
    }

    [Fact]
    public void MatchesDomain_ExactMatch()
    {
        var rule = new BandwidthRule { ProcessName = "test", DnsDomainFilter = "cdn.example.com" };
        Assert.True(rule.MatchesDomain("cdn.example.com"));
        Assert.True(rule.MatchesDomain("CDN.EXAMPLE.COM"));
        Assert.False(rule.MatchesDomain("api.example.com"));
        Assert.False(rule.MatchesDomain("example.com"));
    }

    [Fact]
    public void MatchesDomain_WildcardSubdomain()
    {
        var rule = new BandwidthRule { ProcessName = "test", DnsDomainFilter = "*.example.com" };
        Assert.True(rule.MatchesDomain("cdn.example.com"));
        Assert.True(rule.MatchesDomain("api.example.com"));
        Assert.True(rule.MatchesDomain("sub.cdn.example.com"));
        Assert.True(rule.MatchesDomain("example.com")); // bare domain also matches
        Assert.False(rule.MatchesDomain("example.org"));
        Assert.False(rule.MatchesDomain("notexample.com"));
    }

    [Fact]
    public void MatchesDomain_FilterSet_NullDomain_NoMatch()
    {
        var rule = new BandwidthRule { ProcessName = "test", DnsDomainFilter = "*.example.com" };
        Assert.False(rule.MatchesDomain(null));
    }

    [Fact]
    public void DnsDomainCache_RecordAndLookup()
    {
        var cache = new DnsDomainCache();
        var ip = IPAddress.Parse("93.184.216.34");
        cache.RecordMapping(ip, "example.com");

        Assert.Equal("example.com", cache.LookupDomain(ip));
        Assert.Null(cache.LookupDomain(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void DnsDomainCache_MultipleIPs_SameDomain()
    {
        var cache = new DnsDomainCache();
        cache.RecordMapping(IPAddress.Parse("1.1.1.1"), "cdn.example.com");
        cache.RecordMapping(IPAddress.Parse("1.1.1.2"), "cdn.example.com");

        Assert.Equal("cdn.example.com", cache.LookupDomain(IPAddress.Parse("1.1.1.1")));
        Assert.Equal("cdn.example.com", cache.LookupDomain(IPAddress.Parse("1.1.1.2")));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void DnsResponseParser_ParsesSimpleARecord()
    {
        // Minimal DNS response: 1 question, 1 A record answer for "test.com" → 1.2.3.4
        var data = BuildDnsResponse("test.com", IPAddress.Parse("1.2.3.4"));
        var records = DnsResponseParser.ParseResponse(data);

        Assert.Single(records);
        Assert.Equal("test.com", records[0].Domain);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), records[0].Address);
    }

    [Fact]
    public void DnsResponseParser_IgnoresQueries()
    {
        // Build a query (QR=0)
        var data = new byte[12];
        // flags: QR=0 (query)
        data[2] = 0x00;
        var records = DnsResponseParser.ParseResponse(data);
        Assert.Empty(records);
    }

    [Fact]
    public void DnsResponseParser_HandlesEmptyPayload()
    {
        var records = DnsResponseParser.ParseResponse(ReadOnlySpan<byte>.Empty);
        Assert.Empty(records);
    }

    [Fact]
    public void Clone_PreservesDnsDomainFilter()
    {
        var rule = new BandwidthRule
        {
            ProcessName = "test",
            DnsDomainFilter = "*.cdn.example.com"
        };
        var clone = rule.Clone();
        Assert.Equal("*.cdn.example.com", clone.DnsDomainFilter);
    }

    [Fact]
    public void HasConnectionFilters_IncludesDnsDomainFilter()
    {
        var rule = new BandwidthRule { ProcessName = "test", DnsDomainFilter = "example.com" };
        Assert.True(rule.HasConnectionFilters);
    }

    [Fact]
    public void DnsResponseParser_ParsesAAAARecord()
    {
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var data = BuildDnsResponseAAAA("test.com", ipv6);
        var records = DnsResponseParser.ParseResponse(data);

        Assert.Single(records);
        Assert.Equal("test.com", records[0].Domain);
        Assert.Equal(ipv6, records[0].Address);
    }

    [Fact]
    public void DnsResponseParser_TruncatedPayload_ReturnsEmpty()
    {
        // Valid header but truncated before answer section
        var data = new byte[12];
        data[2] = 0x81; // QR=1
        data[3] = 0x80; // RA=1
        data[7] = 1;    // ANCOUNT = 1
        var records = DnsResponseParser.ParseResponse(data);
        Assert.Empty(records);
    }

    [Fact]
    public void DnsResponseParser_InvalidPointer_ReturnsNull()
    {
        // Build a response with a pointer to offset 255 (way past payload)
        var data = new byte[20];
        data[2] = 0x81; data[3] = 0x80; // Flags: QR=1
        data[5] = 1; // QDCOUNT=1
        data[7] = 1; // ANCOUNT=1
        data[12] = 0xC0; // Compression pointer
        data[13] = 0xFF; // Points to offset 255 — way past end
        // Parser should handle this gracefully
        var records = DnsResponseParser.ParseResponse(data);
        // Should return empty or partial — not throw
        Assert.NotNull(records);
    }

    [Fact]
    public void DnsResponseParser_OversizedResponse_HandledGracefully()
    {
        // 65KB+ payload should not crash
        var data = new byte[70000];
        data[2] = 0x81; data[3] = 0x80;
        var records = DnsResponseParser.ParseResponse(data);
        Assert.NotNull(records);
    }

    private static byte[] BuildDnsResponseAAAA(string domain, IPAddress ipv6)
    {
        var ms = new System.IO.MemoryStream();
        var writer = new System.IO.BinaryWriter(ms);

        writer.Write((ushort)0x1234);
        writer.Write(SwapBytes((ushort)0x8180));
        writer.Write(SwapBytes((ushort)1)); // QDCOUNT
        writer.Write(SwapBytes((ushort)1)); // ANCOUNT
        writer.Write(SwapBytes((ushort)0));
        writer.Write(SwapBytes((ushort)0));

        WriteDnsName(writer, domain);
        writer.Write(SwapBytes((ushort)28)); // QTYPE AAAA
        writer.Write(SwapBytes((ushort)1));

        WriteDnsName(writer, domain);
        writer.Write(SwapBytes((ushort)28));   // TYPE AAAA
        writer.Write(SwapBytes((ushort)1));    // CLASS IN
        writer.Write(SwapBytes((uint)300));    // TTL
        writer.Write(SwapBytes((ushort)16));   // RDLENGTH
        writer.Write(ipv6.GetAddressBytes());  // RDATA

        return ms.ToArray();
    }

    private static byte[] BuildDnsResponse(string domain, IPAddress ip)
    {
        var ms = new System.IO.MemoryStream();
        var writer = new System.IO.BinaryWriter(ms);

        // Header
        writer.Write((ushort)0x1234); // ID
        writer.Write(SwapBytes((ushort)0x8180)); // Flags: QR=1, RD=1, RA=1
        writer.Write(SwapBytes((ushort)1)); // QDCOUNT
        writer.Write(SwapBytes((ushort)1)); // ANCOUNT
        writer.Write(SwapBytes((ushort)0)); // NSCOUNT
        writer.Write(SwapBytes((ushort)0)); // ARCOUNT

        // Question section
        WriteDnsName(writer, domain);
        writer.Write(SwapBytes((ushort)1)); // QTYPE A
        writer.Write(SwapBytes((ushort)1)); // QCLASS IN

        // Answer section
        WriteDnsName(writer, domain);
        writer.Write(SwapBytes((ushort)1));    // TYPE A
        writer.Write(SwapBytes((ushort)1));    // CLASS IN
        writer.Write(SwapBytes((uint)300));    // TTL
        writer.Write(SwapBytes((ushort)4));    // RDLENGTH
        writer.Write(ip.GetAddressBytes());    // RDATA

        return ms.ToArray();
    }

    private static void WriteDnsName(System.IO.BinaryWriter writer, string name)
    {
        foreach (var part in name.Split('.'))
        {
            writer.Write((byte)part.Length);
            foreach (var c in part)
                writer.Write((byte)c);
        }
        writer.Write((byte)0);
    }

    private static ushort SwapBytes(ushort value) =>
        (ushort)((value >> 8) | (value << 8));

    private static uint SwapBytes(uint value) =>
        ((value >> 24) & 0xFF) |
        ((value >> 8) & 0xFF00) |
        ((value << 8) & 0xFF0000) |
        ((value << 24) & 0xFF000000);
}
