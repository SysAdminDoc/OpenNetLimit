using System.Net;
using System.Text;
using OpenNetLimit.CLI;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service.API;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>
/// Tests that time real loopback traffic. Beside the parallel tests the probe's reads can wait
/// a whole window for a thread-pool thread and measure one chunk, so these run on their own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThroughputTimingCollection
{
    public const string Name = "Times real loopback throughput";
}

[Collection(ThroughputTimingCollection.Name)]
public class SelfTestTests
{
    private const long Limit = 256 * 1024;
    private const double Unlimited = 50 * 1024 * 1024;

    [Fact]
    public async Task LimitThatHolds_Passes_AndRemovesTheRule()
    {
        var service = new FakeService();
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 270 * 1024), output);

        Assert.Equal(0, exit);
        Assert.Equal(["status", $"add onl {Limit}", "remove"], service.Calls);
        Assert.Null(service.ActiveLimit);
        Assert.EndsWith("PASS" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task LimitThatDoesNotHold_Fails()
    {
        var service = new FakeService();
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 5 * 1024 * 1024), output);

        Assert.Equal(1, exit);
        Assert.Contains("The limit didn't hold", output.ToString());
        Assert.Null(service.ActiveLimit);
    }

    [Fact]
    public async Task TrafficThatNearlyStopsUnderTheLimit_Fails()
    {
        var service = new FakeService();
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 10 * 1024), output);

        Assert.Equal(1, exit);
        Assert.Contains("nearly stopped", output.ToString());
    }

    [Fact]
    public async Task LimitThatOutlivesItsRule_Fails()
    {
        var service = new FakeService { RemoveLeavesLimit = true };
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 260 * 1024), output);

        Assert.Equal(1, exit);
        Assert.Contains("didn't lift the limit", output.ToString());
    }

    [Fact]
    public async Task MeasurementThatThrows_StillRemovesTheRule()
    {
        var service = new FakeService();
        var probe = new EngineProbe(service, limitedRate: 0) { ThrowWhileLimited = true };

        await Assert.ThrowsAsync<SelfTestException>(() => Run(service, probe, new StringWriter()));

        Assert.Equal("remove", service.Calls[^1]);
        Assert.Null(service.ActiveLimit);
    }

    [Fact]
    public async Task EngineThatIsDown_FailsWithTheServicesReason_AndAddsNoRule()
    {
        var service = new FakeService { EngineProblem = "WinDivert couldn't open (error 5)." };
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 0), output);

        Assert.Equal(1, exit);
        Assert.Contains("WinDivert couldn't open (error 5).", output.ToString());
        Assert.Equal(["status"], service.Calls);
    }

    [Fact]
    public async Task LoopbackTooSlowToTell_FailsBeforeAddingARule()
    {
        var service = new FakeService();
        var output = new StringWriter();

        var exit = await Run(service, new EngineProbe(service, limitedRate: 0, unlimitedRate: Limit * 2), output);

        Assert.Equal(1, exit);
        Assert.Contains("lower --limit", output.ToString());
        Assert.Equal(["status"], service.Calls);
    }

    [Fact]
    public async Task LoopbackProbe_MeasuresTheDataItMoves()
    {
        var probe = new LoopbackProbe(warmup: TimeSpan.FromMilliseconds(200), window: TimeSpan.FromMilliseconds(500));

        var rate = await probe.MeasureBytesPerSecondAsync(CancellationToken.None);

        Assert.True(rate > 1024 * 1024, $"loopback measured {SelfTest.Rate(rate)}");
    }

    [Fact]
    public async Task RuleTheSelfTestPosts_LimitsOnlyThisProcessesUpload_UntilItIsRemoved()
    {
        var (_, rules, router) = RestApiRouterTests.CreateRouter("secret");
        var limiter = new ProcessRateLimiter();
        var reconciler = new RuleReconciler(rules, limiter, new FlowTracker());
        rules.OnRulesChanged += reconciler.Reconcile;
        using var client = new HttpClient(new RouterHandler(router));
        client.DefaultRequestHeaders.Add("X-OpenNetLimit-Key", "secret");
        var service = new RestSelfTestService(client, "http://127.0.0.1:47719");

        Assert.Null(await service.GetEngineProblemAsync(CancellationToken.None));
        var ruleId = await service.AddUploadLimitAsync("onl", Limit, CancellationToken.None);
        reconciler.EnsureLimit(4242, "onl", @"C:\Program Files\OpenNetLimit\onl.exe");
        reconciler.EnsureLimit(4343, "curl", @"C:\Windows\System32\curl.exe");

        Assert.True(limiter.TryGetLimit(4242, out var download, out var upload));
        Assert.Equal(0, download);
        Assert.Equal(Limit, upload);
        Assert.False(limiter.HasLimit(4343));
        Assert.Equal("onl selftest", Assert.Single(rules.GetAllRules()).Name);

        await service.RemoveRuleAsync(ruleId, CancellationToken.None);

        Assert.Empty(rules.GetAllRules());
        Assert.False(limiter.HasLimit(4242));
    }

    [Fact]
    public async Task ServiceWithoutTheApiKey_SaysWhichVariableToSet()
    {
        var (_, _, router) = RestApiRouterTests.CreateRouter("secret");
        using var client = new HttpClient(new RouterHandler(router));
        var service = new RestSelfTestService(client, "http://127.0.0.1:47719");

        var error = await Assert.ThrowsAsync<SelfTestException>(
            () => service.AddUploadLimitAsync("onl", Limit, CancellationToken.None));

        Assert.Contains("OPENNETLIMIT_API_KEY", error.Message);
    }

    private static Task<int> Run(FakeService service, IThroughputProbe probe, TextWriter output) =>
        new SelfTest(service, probe, output, "onl") { SettleTime = TimeSpan.Zero }
            .RunAsync(Limit, CancellationToken.None);

    private sealed class FakeService : ISelfTestService
    {
        private readonly Guid _ruleId = Guid.NewGuid();

        public string? EngineProblem { get; init; }
        public bool RemoveLeavesLimit { get; init; }
        public long? ActiveLimit { get; private set; }
        public List<string> Calls { get; } = [];

        public Task<string?> GetEngineProblemAsync(CancellationToken ct)
        {
            Calls.Add("status");
            return Task.FromResult(EngineProblem);
        }

        public Task<Guid> AddUploadLimitAsync(string processName, long bytesPerSecond, CancellationToken ct)
        {
            Calls.Add($"add {processName} {bytesPerSecond}");
            ActiveLimit = bytesPerSecond;
            return Task.FromResult(_ruleId);
        }

        public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct)
        {
            Assert.Equal(_ruleId, ruleId);
            Calls.Add("remove");
            if (!RemoveLeavesLimit)
                ActiveLimit = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>Reports the limited rate while the service has the rule in force, the unlimited rate otherwise.</summary>
    private sealed class EngineProbe(FakeService service, double limitedRate, double unlimitedRate = Unlimited) : IThroughputProbe
    {
        public bool ThrowWhileLimited { get; init; }

        public Task<double> MeasureBytesPerSecondAsync(CancellationToken ct)
        {
            if (service.ActiveLimit is null)
                return Task.FromResult(unlimitedRate);
            if (ThrowWhileLimited)
                throw new SelfTestException("The loopback connection closed partway through the test.");
            return Task.FromResult(limitedRate);
        }
    }

    /// <summary>Sends the client's requests straight to the service's REST router, as a loopback caller.</summary>
    private sealed class RouterHandler(RestApiRouter router) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var key = request.Headers.TryGetValues("X-OpenNetLimit-Key", out var keys) ? keys.First() : null;
            var response = await router.HandleAsync(new RestApiRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query.TrimStart('?'),
                body,
                IsLoopback: true,
                ApiKey: key), ct);
            return new HttpResponseMessage((HttpStatusCode)response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            };
        }
    }
}
