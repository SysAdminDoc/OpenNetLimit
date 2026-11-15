using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Service.API;
using Xunit;

namespace OpenNetLimit.Tests;

public class RestApiServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private long _nowMs;

    private RestClientLimits ManualLimits() => new(() => _nowMs);

    private static (OpenNetLimit.Engine.Rules.RuleEngine Rules, RestApiRouter Router, RestApiOptions Options) CreateRouter()
    {
        var (_, rules, router) = RestApiRouterTests.CreateRouter(apiKey: "secret");
        return (rules, router, new RestApiOptions { ApiKey = "secret" });
    }

    /// <summary>A server on the test's clock, refilled only by <see cref="RestApiServer.RefillAll"/>.</summary>
    private (OpenNetLimit.Engine.Rules.RuleEngine Rules, RestApiServer Server) CreateServer(Func<RestClientLimits>? newLimits = null)
    {
        var (rules, router, options) = CreateRouter();
        return (rules, new RestApiServer(router, options, NullLogger<RestApiServer>.Instance,
            newLimits ?? ManualLimits, () => _nowMs, refillEvery: null));
    }

    private static Task<RestApiResponse> Send(RestApiServer server, string method, string path,
        string body = "", string? key = "secret", string client = "127.0.0.1", Func<Task<string>>? readBody = null) =>
        server.HandleAsync(client,
            new RestApiRequest(method, path, string.Empty, string.Empty, IPAddress.IsLoopback(IPAddress.Parse(client)), key),
            readBody ?? (() => Task.FromResult(body)), CancellationToken.None);

    private static Task<RestApiResponse> AddRule(RestApiServer server, int n, string? key = "secret",
        string client = "127.0.0.1", Func<Task<string>>? readBody = null) =>
        Send(server, "POST", "/api/v1/rules", $$"""{"processName":"app{{n}}"}""", key, client, readBody);

    private static Task<RestApiResponse> ReadStatus(RestApiServer server, string? key = null) =>
        Send(server, "GET", "/api/v1/status", key: key);

    [Fact]
    public async Task FiftyRuleAddsInARow_AreAllTaken()
    {
        var (rules, router, options) = CreateRouter();
        using var server = new RestApiServer(router, options, NullLogger<RestApiServer>.Instance);

        for (int i = 0; i < 50; i++)
            Assert.Equal(201, (await AddRule(server, i)).StatusCode);

        Assert.Equal(50, rules.GetAllRules().Count);
    }

    [Fact]
    public async Task TheServiceRefillsOnItsOwn_SoRequestsPastTheBurstAllGetThrough()
    {
        var (_, router, options) = CreateRouter();
        using var server = new RestApiServer(router, options, NullLogger<RestApiServer>.Instance);

        // At 50 a second, the 20 past the burst take about 0.4 seconds.
        var reads = Enumerable.Range(0, RestClientLimits.RequestBurst + 20).Select(_ => ReadStatus(server)).ToList();

        var answers = await Task.WhenAll(reads).WaitAsync(Timeout);
        Assert.All(answers, a => Assert.Equal(200, a.StatusCode));
    }

    [Fact]
    public async Task ARequestPastTheBurst_WaitsForItsTurn_InsteadOfBeingRefused()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.RequestBurst; i++)
            Assert.Equal(200, (await ReadStatus(server)).StatusCode);

        var waiting = ReadStatus(server);
        Assert.False(waiting.IsCompleted);
        // A refill adds only what the time since the last one is worth.
        await Task.Delay(250);
        server.RefillAll();

        Assert.Equal(200, (await waiting.WaitAsync(Timeout)).StatusCode);
    }

    [Fact]
    public async Task ARequestPastTheWaitingLine_IsRefused()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.RequestBurst; i++)
            await ReadStatus(server);
        var waiting = Enumerable.Range(0, RestClientLimits.RequestsWaiting).Select(_ => ReadStatus(server)).ToList();

        var refused = await ReadStatus(server).WaitAsync(Timeout);

        Assert.Equal(429, refused.StatusCode);
        Assert.Contains("waiting", refused.Body);
        Assert.Equal(1, refused.RetryAfterSeconds);
        Assert.DoesNotContain(waiting, w => w.IsCompleted);
    }

    [Fact]
    public async Task AChangesBody_IsReadOnlyOnceItHasItsTurn()
    {
        var (rules, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.RequestBurst; i++)
            await ReadStatus(server);
        bool read = false;

        var add = AddRule(server, 1, readBody: () =>
        {
            read = true;
            return Task.FromResult("""{"processName":"late"}""");
        });
        bool readBeforeItsTurn = read;
        await Task.Delay(250);
        server.RefillAll();

        Assert.Equal(201, (await add.WaitAsync(Timeout)).StatusCode);
        Assert.False(readBeforeItsTurn);
        Assert.Equal("late", Assert.Single(rules.GetAllRules()).ProcessName);
    }

    [Fact]
    public async Task AReadsBody_IsNeverWaitedFor()
    {
        var (_, server) = CreateServer();
        var neverSent = new TaskCompletionSource<string>();

        var read = Send(server, "GET", "/api/v1/status", key: null, readBody: () => neverSent.Task);

        Assert.Equal(200, (await read.WaitAsync(Timeout)).StatusCode);
    }

    [Fact]
    public async Task TenWrongKeys_ShutOutEvenTheRightOne_UntilATryComesBack()
    {
        var (rules, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.WrongKeyBurst; i++)
            Assert.Equal(401, (await AddRule(server, i, key: "guess")).StatusCode);

        var shutOut = await AddRule(server, 100);
        _nowMs += (long)RestClientLimits.WrongKeyRefill.TotalMilliseconds;
        var back = await AddRule(server, 101);

        Assert.Equal(429, shutOut.StatusCode);
        Assert.Contains("wrong api keys", shutOut.Body);
        Assert.Equal(6, shutOut.RetryAfterSeconds);
        Assert.Equal(201, back.StatusCode);
        Assert.Equal("app101", Assert.Single(rules.GetAllRules()).ProcessName);
    }

    [Fact]
    public async Task WrongKeysSentAllAtOnceWithTheirBodiesHeldBack_StillGetOnlyTen()
    {
        var (_, server) = CreateServer();
        var neverSent = new TaskCompletionSource<string>();
        bool anyBodyRead = false;

        var guesses = Enumerable.Range(0, 2 * RestClientLimits.WrongKeyBurst)
            .Select(i => AddRule(server, i, key: $"guess{i}", readBody: () =>
            {
                anyBodyRead = true;
                return neverSent.Task;
            }))
            .ToList();
        var answers = await Task.WhenAll(guesses).WaitAsync(Timeout);

        Assert.Equal(RestClientLimits.WrongKeyBurst, answers.Count(a => a.StatusCode == 401));
        Assert.Equal(RestClientLimits.WrongKeyBurst, answers.Count(a => a.StatusCode == 429));
        Assert.False(anyBodyRead);
    }

    [Fact]
    public async Task AClientQuietForAWhile_StillHasOnlyTenTries()
    {
        var (_, server) = CreateServer();
        Assert.Equal(201, (await AddRule(server, 0)).StatusCode);
        _nowMs += (long)RestApiServer.IdleClient.TotalMilliseconds;

        for (int i = 0; i < RestClientLimits.WrongKeyBurst; i++)
            Assert.Equal(401, (await AddRule(server, i, key: "guess")).StatusCode);
        Assert.Equal(429, (await AddRule(server, 0, key: "guess")).StatusCode);
    }

    [Fact]
    public async Task RightKeys_LeaveTheWrongKeyBudgetAlone()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < 30; i++)
            Assert.Equal(201, (await AddRule(server, i)).StatusCode);

        for (int i = 0; i < RestClientLimits.WrongKeyBurst; i++)
            Assert.Equal(401, (await AddRule(server, i, key: "guess")).StatusCode);
        Assert.Equal(429, (await AddRule(server, 0, key: "guess")).StatusCode);
    }

    [Fact]
    public async Task MissingKeysFromAnotherPC_DontCostATry()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < 3 * RestClientLimits.WrongKeyBurst; i++)
            Assert.Equal(401, (await AddRule(server, i, key: null, client: "192.168.1.20")).StatusCode);

        Assert.Equal(201, (await AddRule(server, 0, client: "192.168.1.20")).StatusCode);
    }

    [Fact]
    public async Task AClientShutOutForWrongKeys_CanStillReadWithoutOne()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.WrongKeyBurst; i++)
            await AddRule(server, i, key: "guess");

        Assert.Equal(200, (await ReadStatus(server)).StatusCode);
        Assert.Equal(429, (await ReadStatus(server, key: "secret")).StatusCode);
    }

    [Fact]
    public async Task WrongKeysFromOneAddress_DontShutOutAnother()
    {
        var (_, server) = CreateServer();
        for (int i = 0; i < RestClientLimits.WrongKeyBurst; i++)
            await AddRule(server, i, key: "guess", client: "192.168.1.20");

        Assert.Equal(201, (await AddRule(server, 0, client: "192.168.1.30")).StatusCode);
        Assert.Equal(429, (await AddRule(server, 1, client: "192.168.1.20")).StatusCode);
    }

    [Fact]
    public async Task AClientQuietForTenMinutes_IsForgotten_AndABusyOneIsKept()
    {
        var made = new List<RestClientLimits>();
        var (_, server) = CreateServer(() =>
        {
            var limits = ManualLimits();
            made.Add(limits);
            return limits;
        });
        await AddRule(server, 0, client: "192.168.1.20");
        await AddRule(server, 1, client: "192.168.1.30");

        _nowMs += (long)RestApiServer.IdleClient.TotalMilliseconds - 1000;
        await AddRule(server, 2, client: "192.168.1.30");
        _nowMs += 1000;
        server.RefillAll();
        await AddRule(server, 3, client: "192.168.1.20");
        await AddRule(server, 4, client: "192.168.1.30");

        Assert.Equal(3, made.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await made[0].WaitTurnAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.0.0.2", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    [InlineData("::ffff:192.168.1.5", "192.168.1.5")]
    [InlineData("192.168.1.5", "192.168.1.5")]
    [InlineData("2001:db8:1:2:aaaa::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:bbbb:cccc:dddd:2", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void ClientKey_GroupsTheAddressesOneMachineCanSendFrom(string address, string client)
    {
        Assert.Equal(client, RestApiServer.ClientKey(IPAddress.Parse(address)));
    }
}
