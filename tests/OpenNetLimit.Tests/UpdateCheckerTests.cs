using System.Net;
using System.Text;
using OpenNetLimit.Core.Updates;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly Version Running = new(1, 0, 2, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"onl-update-{Guid.NewGuid():N}");
    private readonly FakeGitHub _github = new();
    private readonly HttpClient _http;
    private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public UpdateCheckerTests()
    {
        _http = new HttpClient(_github);
    }

    private string StateFile => Path.Combine(_dir, "update-check.json");

    private UpdateChecker Checker(bool disabledByPolicy = false) => new(_http, StateFile, disabledByPolicy, () => _now);

    [Fact]
    public async Task NewerRelease_IsReported()
    {
        _github.Tag = "v1.1.0";

        var update = await Checker().CheckAsync(Running, CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), update?.Version);
        Assert.Equal(UpdateChecker.LatestReleaseApi, _github.LastUrl);
        Assert.Contains("OpenNetLimit", _github.LastUserAgent);
    }

    [Theory]
    [InlineData("v1.0.2")]
    [InlineData("1.0.2")]
    [InlineData("v1.0.1")]
    [InlineData("v0.9.0")]
    [InlineData("v1.0")]
    public async Task SameOrOlderRelease_IsNotReported(string tag)
    {
        _github.Tag = tag;

        Assert.Null(await Checker().CheckAsync(Running, CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_IsAskedAtMostOnceADay()
    {
        _github.Tag = "v1.1.0";
        var checker = Checker();

        await checker.CheckAsync(Running, CancellationToken.None);
        _now += TimeSpan.FromHours(23);
        var cached = await checker.CheckAsync(Running, CancellationToken.None);
        var nextStart = await Checker().CheckAsync(Running, CancellationToken.None);

        Assert.Equal(1, _github.Requests);
        Assert.Equal(new Version(1, 1, 0), cached?.Version);
        Assert.Equal(new Version(1, 1, 0), nextStart?.Version);

        _now += TimeSpan.FromHours(1);
        await checker.CheckAsync(Running, CancellationToken.None);
        Assert.Equal(2, _github.Requests);
    }

    [Fact]
    public async Task ClockMovedBack_AsksAgain()
    {
        var checker = Checker();
        await checker.CheckAsync(Running, CancellationToken.None);

        _now -= TimeSpan.FromDays(3);
        await checker.CheckAsync(Running, CancellationToken.None);

        Assert.Equal(2, _github.Requests);
    }

    [Fact]
    public async Task TurnedOff_NeverAsksAndStaysOff()
    {
        _github.Tag = "v1.1.0";
        Checker().SetEnabled(false);

        var reopened = Checker();
        Assert.False(reopened.Enabled);
        Assert.Null(await reopened.CheckAsync(Running, CancellationToken.None));
        Assert.Equal(0, _github.Requests);

        reopened.SetEnabled(true);
        Assert.True(Checker().Enabled);
        Assert.NotNull(await Checker().CheckAsync(Running, CancellationToken.None));
    }

    [Fact]
    public async Task TurnedOffByPolicy_NeverAsks()
    {
        _github.Tag = "v1.1.0";
        var checker = Checker(disabledByPolicy: true);

        Assert.False(checker.Enabled);
        Assert.Null(await checker.CheckAsync(Running, CancellationToken.None));
        Assert.Equal(0, _github.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NoAnswer_KeepsTheLastAnswerAndTriesAgainNextTime(HttpStatusCode failure)
    {
        _github.Tag = "v1.1.0";
        var checker = Checker();
        await checker.CheckAsync(Running, CancellationToken.None);

        _now += TimeSpan.FromDays(1);
        _github.Status = failure;
        var update = await checker.CheckAsync(Running, CancellationToken.None);
        Assert.Equal(new Version(1, 1, 0), update?.Version);

        _now += TimeSpan.FromMinutes(5);
        _github.Status = HttpStatusCode.OK;
        _github.Tag = "v1.2.0";
        update = await checker.CheckAsync(Running, CancellationToken.None);
        Assert.Equal(new Version(1, 2, 0), update?.Version);
        Assert.Equal(3, _github.Requests);
    }

    [Fact]
    public async Task Offline_ReportsNothingAndDoesNotThrow()
    {
        _github.Offline = true;

        Assert.Null(await Checker().CheckAsync(Running, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("{\"tag_name\":\"\"}")]
    [InlineData("<html>busy</html>")]
    public async Task UnexpectedAnswer_ReportsNothingAndDoesNotThrow(string body)
    {
        _github.Body = body;

        Assert.Null(await Checker().CheckAsync(Running, CancellationToken.None));
    }

    [Fact]
    public async Task DamagedStateFile_StartsOver()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(StateFile, "{ not json");
        _github.Tag = "v1.1.0";

        var update = await Checker().CheckAsync(Running, CancellationToken.None);

        Assert.Equal(new Version(1, 1, 0), update?.Version);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("v1.4.0+build.7", "1.4.0")]
    [InlineData(" v1.0.2 ", "1.0.2")]
    public void Tags_ReadAsMajorMinorPatch(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("vnext")]
    public void UnreadableTags_AreIgnored(string? tag)
    {
        Assert.Null(UpdateChecker.ParseVersion(tag));
        Assert.Null(UpdateChecker.Newer(tag, Running));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" YES ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PolicyVariable_TurnsChecksOff(string? value, bool disabled)
    {
        var before = Environment.GetEnvironmentVariable(UpdateChecker.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateChecker.DisableVariable, value);
            Assert.Equal(disabled, UpdateChecker.PolicyDisablesChecks);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateChecker.DisableVariable, before);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastUrl { get; private set; }
        public string LastUserAgent { get; private set; } = string.Empty;
        public string Tag { get; set; } = "v1.0.2";
        public string? Body { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.UserAgent.ToString();
            if (Offline) throw new HttpRequestException("No such host is known.");

            var body = Body ?? $"{{\"tag_name\":\"{Tag}\",\"html_url\":\"https://example.invalid/not-used\"}}";
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
