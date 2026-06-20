using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Service.Security;
using Xunit;

namespace OpenNetLimit.Tests;

public class VirusTotalVerifierTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"onl_vt_{Guid.NewGuid()}.bin");

    [Fact]
    public async Task VerifyFileAsync_WithoutApiKey_ReturnsNotConfigured()
    {
        var verifier = new VirusTotalVerifier(
            new VirusTotalOptions { ApiKey = null },
            NullLogger<VirusTotalVerifier>.Instance,
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not call"))));

        var result = await verifier.VerifyFileAsync(_tempFile);

        Assert.Equal(ProcessVerificationStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task VerifyFileAsync_WithMissingFile_ReturnsFileNotFound()
    {
        var verifier = new VirusTotalVerifier(
            new VirusTotalOptions { ApiKey = "key" },
            NullLogger<VirusTotalVerifier>.Instance,
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not call"))));

        var result = await verifier.VerifyFileAsync(_tempFile);

        Assert.Equal(ProcessVerificationStatus.FileNotFound, result.Status);
    }

    [Fact]
    public async Task VerifyFileAsync_QueriesVirusTotalBySha256()
    {
        await File.WriteAllTextAsync(_tempFile, "known content");
        HttpRequestMessage? request = null;
        var httpClient = new HttpClient(new StubHandler(req =>
        {
            request = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": {
                        "attributes": {
                          "last_analysis_stats": {
                            "harmless": 70,
                            "malicious": 1,
                            "suspicious": 2,
                            "undetected": 5,
                            "timeout": 0
                          }
                        }
                      }
                    }
                    """, Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://www.virustotal.com/api/v3/")
        };
        var verifier = new VirusTotalVerifier(
            new VirusTotalOptions { ApiKey = "key" },
            NullLogger<VirusTotalVerifier>.Instance,
            httpClient);

        var result = await verifier.VerifyFileAsync(_tempFile);

        Assert.Equal(ProcessVerificationStatus.Malicious, result.Status);
        Assert.Equal(1, result.Malicious);
        Assert.Equal(2, result.Suspicious);
        Assert.Equal(70, result.Harmless);
        Assert.NotNull(result.Sha256);
        Assert.EndsWith($"/files/{result.Sha256}", request!.RequestUri!.ToString());
        Assert.Equal("key", request.Headers.GetValues("x-apikey").Single());
    }

    [Fact]
    public async Task VerifyFileAsync_UsesCacheForRepeatedHash()
    {
        await File.WriteAllTextAsync(_tempFile, "cache me");
        var calls = 0;
        var httpClient = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("https://www.virustotal.com/api/v3/")
        };
        var verifier = new VirusTotalVerifier(
            new VirusTotalOptions { ApiKey = "key", CacheDuration = TimeSpan.FromHours(1) },
            NullLogger<VirusTotalVerifier>.Instance,
            httpClient);

        var first = await verifier.VerifyFileAsync(_tempFile);
        var second = await verifier.VerifyFileAsync(_tempFile);

        Assert.Equal(ProcessVerificationStatus.Unknown, first.Status);
        Assert.Equal(ProcessVerificationStatus.Unknown, second.Status);
        Assert.True(second.FromCache);
        Assert.Equal(1, calls);
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
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
