using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenNetLimit.CLI;

/// <summary>The parts of the service the self test talks to.</summary>
internal interface ISelfTestService
{
    /// <summary>Null when the packet engine is running, otherwise the service's reason it isn't.</summary>
    Task<string?> GetEngineProblemAsync(CancellationToken ct);

    Task<Guid> AddUploadLimitAsync(string processName, long bytesPerSecond, CancellationToken ct);

    Task RemoveRuleAsync(Guid ruleId, CancellationToken ct);
}

/// <summary>Measures how fast this process can move data to itself.</summary>
internal interface IThroughputProbe
{
    Task<double> MeasureBytesPerSecondAsync(CancellationToken ct);
}

internal sealed class SelfTestException(string message) : Exception(message);

/// <summary>
/// Sets an upload limit on this process, moves data over loopback and checks the rate held, then
/// removes the rule and checks the rate came back. WinDivert treats every loopback packet as outbound,
/// so the sender and receiver both live in this process and the rule limits upload.
/// </summary>
internal sealed class SelfTest(ISelfTestService service, IThroughputProbe probe, TextWriter output, string processName)
{
    /// <summary>A limited rate may land this far over the limit (TCP bursts, timer jitter) and still pass.</summary>
    internal const double Tolerance = 1.3;

    /// <summary>A limited rate under this share of the limit means the engine is starving the app, not limiting it.</summary>
    internal const double StarvedShare = 0.25;

    /// <summary>Unlimited traffic has to beat the limit by this much, or a pass would prove nothing.</summary>
    internal const double Headroom = 4;

    /// <summary>Time for the service to apply or lift the rule before measuring.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<int> RunAsync(long limitBytesPerSecond, CancellationToken ct)
    {
        output.WriteLine($"OpenNetLimit self test: {Rate(limitBytesPerSecond)} upload limit on {processName}");

        if (await service.GetEngineProblemAsync(ct) is { } problem)
            return Fail($"The packet engine isn't running. {problem}");

        var unlimited = await probe.MeasureBytesPerSecondAsync(ct);
        output.WriteLine($"  unlimited      {Rate(unlimited),12}");
        if (unlimited < limitBytesPerSecond * Headroom)
            return Fail($"Loopback only reached {Rate(unlimited)} with no limit, too close to {Rate(limitBytesPerSecond)} to tell. Run it again with a lower --limit.");

        var ruleId = await service.AddUploadLimitAsync(processName, limitBytesPerSecond, ct);
        double limited;
        try
        {
            await Task.Delay(SettleTime, ct);
            limited = await probe.MeasureBytesPerSecondAsync(ct);
        }
        finally
        {
            await service.RemoveRuleAsync(ruleId, CancellationToken.None);
        }

        var held = limited <= limitBytesPerSecond * Tolerance;
        var starved = limited < limitBytesPerSecond * StarvedShare;
        output.WriteLine($"  limited        {Rate(limited),12}  {(held && !starved ? "ok" : "FAIL")}");

        await Task.Delay(SettleTime, ct);
        var lifted = await probe.MeasureBytesPerSecondAsync(ct);
        var restored = lifted >= limitBytesPerSecond * Headroom;
        output.WriteLine($"  rule removed   {Rate(lifted),12}  {(restored ? "ok" : "FAIL")}");

        if (!held)
            return Fail($"The limit didn't hold. Traffic ran at {Rate(limited)} against a {Rate(limitBytesPerSecond)} limit.");
        if (starved)
            return Fail($"Traffic nearly stopped under the limit ({Rate(limited)} against {Rate(limitBytesPerSecond)}).");
        if (!restored)
            return Fail($"Removing the rule didn't lift the limit. Traffic stayed at {Rate(lifted)}.");

        output.WriteLine("PASS");
        return 0;
    }

    private int Fail(string reason)
    {
        output.WriteLine($"FAIL: {reason}");
        return 1;
    }

    internal static string Rate(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = bytesPerSecond;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>Streams data from one loopback socket to another in this process and times the receiving end.</summary>
internal sealed class LoopbackProbe(TimeSpan warmup, TimeSpan window) : IThroughputProbe
{
    private const int ChunkBytes = 64 * 1024;

    public async Task<double> MeasureBytesPerSecondAsync(CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var sender = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync(ct).AsTask();
        await sender.ConnectAsync(IPAddress.Loopback, port, ct);
        using var receiver = await accepting;
        listener.Stop();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = SendUntilStoppedAsync(sender.GetStream(), stop.Token);
        try
        {
            return await ReceiveAsync(receiver.GetStream(), ct);
        }
        finally
        {
            stop.Cancel();
            sender.Close();
            await sending;
        }
    }

    private async Task<double> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        var end = warmup + window;
        var clock = Stopwatch.StartNew();
        long counted = 0;

        while (clock.Elapsed < end)
        {
            using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readLimit.CancelAfter(end - clock.Elapsed);
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readLimit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            if (read == 0)
                throw new SelfTestException("The loopback connection closed partway through the test.");
            if (clock.Elapsed >= warmup)
                counted += read;
        }

        return counted / window.TotalSeconds;
    }

    private static async Task SendUntilStoppedAsync(NetworkStream stream, CancellationToken ct)
    {
        var chunk = new byte[ChunkBytes];
        try
        {
            while (!ct.IsCancellationRequested)
                await stream.WriteAsync(chunk, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The receiver finished and closed the connection, which is how sending ends.
        }
    }
}

/// <summary>The self test's calls to the REST API.</summary>
internal sealed class RestSelfTestService(HttpClient client, string baseUrl) : ISelfTestService
{
    public async Task<string?> GetEngineProblemAsync(CancellationToken ct)
    {
        using var response = await client.GetAsync(baseUrl + "/api/v1/status", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new SelfTestException($"The service answered the status check with {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True)
            return null;
        return root.TryGetProperty("engineError", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : "The service didn't say why. Check its event log.";
    }

    public async Task<Guid> AddUploadLimitAsync(string processName, long bytesPerSecond, CancellationToken ct)
    {
        var rule = JsonSerializer.Serialize(new
        {
            name = "onl selftest",
            processName,
            action = "Limit",
            direction = "Upload",
            uploadBytesPerSecond = bytesPerSecond
        });
        using var content = new StringContent(rule, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(baseUrl + "/api/v1/rules", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new SelfTestException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? $"The service turned down the test rule ({body}). Set OPENNETLIMIT_API_KEY to the service's API key."
                : $"The service turned down the test rule with {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) && id.TryGetGuid(out var ruleId)
            ? ruleId
            : throw new SelfTestException($"The service added the test rule but didn't return its id: {body}");
    }

    public async Task RemoveRuleAsync(Guid ruleId, CancellationToken ct)
    {
        using var response = await client.DeleteAsync($"{baseUrl}/api/v1/rules/{ruleId}", ct);
        if (!response.IsSuccessStatusCode)
            throw new SelfTestException($"Couldn't remove the test rule {ruleId}. Remove it with: onl rules remove {ruleId}");
    }
}
