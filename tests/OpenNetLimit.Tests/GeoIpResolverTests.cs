using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Service.Geo;
using Xunit;

namespace OpenNetLimit.Tests;

public class GeoIpResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenDisabled_ReturnsDisabled()
    {
        var resolver = new FreeIpApiGeoIpResolver(
            new GeoIpOptions { Enabled = false },
            NullLogger<FreeIpApiGeoIpResolver>.Instance,
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not call"))));

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(GeoIpStatus.Disabled, result.Status);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.5")]
    [InlineData("fd00::1")]
    public async Task ResolveAsync_PrivateAddress_DoesNotCallProvider(string ip)
    {
        var resolver = new FreeIpApiGeoIpResolver(
            new GeoIpOptions { Enabled = true },
            NullLogger<FreeIpApiGeoIpResolver>.Instance,
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not call"))));

        var result = await resolver.ResolveAsync(IPAddress.Parse(ip));

        Assert.Equal(GeoIpStatus.PrivateAddress, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_ParsesFreeIpApiResponse()
    {
        HttpRequestMessage? request = null;
        var httpClient = new HttpClient(new StubHandler(req =>
        {
            request = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "ipAddress": "8.8.8.8",
                      "countryName": "United States",
                      "countryCode": "US",
                      "regionName": "California",
                      "cityName": "Mountain View",
                      "latitude": 37.386,
                      "longitude": -122.0838,
                      "timeZone": "America/Los_Angeles",
                      "asn": "AS15169",
                      "asnOrganization": "Google LLC"
                    }
                    """, Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://free.freeipapi.com/api/json/")
        };
        var resolver = new FreeIpApiGeoIpResolver(
            new GeoIpOptions { Enabled = true },
            NullLogger<FreeIpApiGeoIpResolver>.Instance,
            httpClient);

        var result = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(GeoIpStatus.Located, result.Status);
        Assert.Equal("United States", result.CountryName);
        Assert.Equal("US", result.CountryCode);
        Assert.Equal("Mountain View", result.CityName);
        Assert.Equal("AS15169", result.Asn);
        Assert.EndsWith("/8.8.8.8", request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ResolveAsync_UsesCacheForRepeatedAddress()
    {
        var calls = 0;
        var httpClient = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"countryName":"United States","countryCode":"US"}""")
            };
        }))
        {
            BaseAddress = new Uri("https://free.freeipapi.com/api/json/")
        };
        var resolver = new FreeIpApiGeoIpResolver(
            new GeoIpOptions { Enabled = true, CacheDuration = TimeSpan.FromHours(1) },
            NullLogger<FreeIpApiGeoIpResolver>.Instance,
            httpClient);

        var first = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));
        var second = await resolver.ResolveAsync(IPAddress.Parse("8.8.8.8"));

        Assert.Equal(GeoIpStatus.Located, first.Status);
        Assert.Equal(GeoIpStatus.Located, second.Status);
        Assert.True(second.FromCache);
        Assert.Equal(1, calls);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
