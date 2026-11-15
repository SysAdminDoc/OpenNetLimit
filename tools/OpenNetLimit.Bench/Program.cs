using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenNetLimit.Bench;

/// <summary>
/// Measures loopback traffic on this PC: TCP throughput over IPv4 and IPv6, TCP and UDP round
/// trips, and UDP delivery at a steady rate. Run it with the service stopped and again with it
/// running to see what the packet engine costs. Every loopback packet passes through WinDivert
/// while the service runs, so the difference is the engine's overhead.
/// </summary>
internal static class Program
{
    private static readonly string[] AllTests = ["tcp4", "tcp6", "tcprtt", "udprtt", "udprate", "udpburst"];

    private static int Main(string[] args)
    {
        var seconds = 5;
        var json = false;
        var only = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0)
            {
                seconds = s;
                i++;
            }
            else if (args[i] == "--only" && i + 1 < args.Length)
            {
                only.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (args[i] == "--json")
            {
                json = true;
            }
            else
            {
                Console.Error.WriteLine($"Usage: onl-bench [--seconds N] [--only {string.Join(',', AllTests)}] [--json]");
                return 2;
            }
        }

        var unknown = only.Where(name => !AllTests.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown test: {string.Join(", ", unknown)}. Tests: {string.Join(", ", AllTests)}");
            return 2;
        }

        var tests = only.Count == 0 ? AllTests : AllTests.Where(t => only.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
        var window = TimeSpan.FromSeconds(seconds);
        var results = new Dictionary<string, object>();
        foreach (var test in tests)
        {
            try
            {
                object result = test switch
                {
                    "tcp4" => LoopbackBench.TcpThroughput(IPAddress.Loopback, window),
                    "tcp6" => LoopbackBench.TcpThroughput(IPAddress.IPv6Loopback, window),
                    "tcprtt" => LoopbackBench.TcpRoundTrip(IPAddress.Loopback, window),
                    "udprtt" => LoopbackBench.UdpRoundTrip(IPAddress.Loopback, window),
                    "udprate" => LoopbackBench.UdpDelivery(IPAddress.Loopback, window, datagramsPerSecond: 20_000),
                    _ => LoopbackBench.UdpBurst(IPAddress.Loopback, datagrams: 12_000),
                };
                results[test] = result;
                if (!json) Console.WriteLine($"{test,-8} {result}");
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                results[test] = new { error = ex.Message };
                if (!json) Console.WriteLine($"{test,-8} failed: {ex.Message}");
            }
        }

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return results.Values.Any(r => r.GetType().GetProperty("error") is not null) ? 1 : 0;
    }
}

internal sealed record Throughput(double MegabytesPerSecond)
{
    public override string ToString() => $"{MegabytesPerSecond:F1} MB/s";
}

internal sealed record RoundTrips(int Samples, int Lost, double P50Microseconds, double P99Microseconds, double MaxMicroseconds)
{
    public override string ToString() =>
        $"p50 {P50Microseconds:F0} us, p99 {P99Microseconds:F0} us, max {MaxMicroseconds:F0} us ({Samples} round trips{(Lost > 0 ? $", {Lost} lost" : "")})";
}

internal sealed record Delivery(long Sent, long Received, double LossPercent)
{
    public override string ToString() => $"{Received}/{Sent} datagrams arrived ({LossPercent:F2}% lost)";
}

internal static class LoopbackBench
{
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1);
    private const int IoTimeoutMs = 5000;

    public static Throughput TcpThroughput(IPAddress loopback, TimeSpan window)
    {
        var listener = new TcpListener(loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient(loopback.AddressFamily);
            client.Connect(loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = listener.AcceptTcpClient();
            server.ReceiveTimeout = IoTimeoutMs;
            var stop = false;
            var sender = new Thread(() =>
            {
                var chunk = new byte[64 * 1024];
                try
                {
                    var stream = client.GetStream();
                    while (!Volatile.Read(ref stop)) stream.Write(chunk);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }) { IsBackground = true };
            sender.Start();

            var buffer = new byte[256 * 1024];
            var input = server.GetStream();
            var clock = Stopwatch.StartNew();
            long counted = 0;
            while (clock.Elapsed < Warmup + window)
            {
                var read = input.Read(buffer);
                if (read == 0) throw new IOException("The connection closed during the measurement.");
                if (clock.Elapsed >= Warmup) counted += read;
            }

            Volatile.Write(ref stop, true);
            client.Close();
            sender.Join(IoTimeoutMs);
            return new Throughput(counted / window.TotalSeconds / (1024 * 1024));
        }
        finally
        {
            listener.Stop();
        }
    }

    public static RoundTrips TcpRoundTrip(IPAddress loopback, TimeSpan window)
    {
        var listener = new TcpListener(loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient(loopback.AddressFamily) { NoDelay = true, ReceiveTimeout = IoTimeoutMs };
            client.Connect(loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = listener.AcceptTcpClient();
            server.NoDelay = true;
            var echo = new Thread(() =>
            {
                var one = new byte[1];
                try
                {
                    var stream = server.GetStream();
                    while (stream.Read(one) == 1) stream.Write(one);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }) { IsBackground = true };
            echo.Start();

            var stream = client.GetStream();
            var ping = new byte[1];
            var samples = Measure(window, () =>
            {
                stream.Write(ping);
                if (stream.Read(ping) != 1) throw new IOException("The echo connection closed.");
                return true;
            });
            client.Close();
            echo.Join(IoTimeoutMs);
            return Summarize(samples, lost: 0);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static RoundTrips UdpRoundTrip(IPAddress loopback, TimeSpan window)
    {
        using var server = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(loopback, 0));
        using var client = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 1000 };
        client.Connect(server.LocalEndPoint!);
        var echo = new Thread(() =>
        {
            var buffer = new byte[64];
            EndPoint from = new IPEndPoint(loopback, 0);
            try
            {
                while (true)
                {
                    var read = server.ReceiveFrom(buffer, ref from);
                    server.SendTo(buffer.AsSpan(0, read), from);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }) { IsBackground = true };
        echo.Start();

        var ping = new byte[32];
        var lost = 0;
        var samples = Measure(window, () =>
        {
            client.Send(ping);
            try
            {
                client.Receive(ping);
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                lost++;
                return false;
            }
        });
        server.Close();
        echo.Join(IoTimeoutMs);
        return Summarize(samples, lost);
    }

    /// <summary>Sends datagrams at a steady rate for the window and counts how many arrive.</summary>
    public static Delivery UdpDelivery(IPAddress loopback, TimeSpan window, int datagramsPerSecond) =>
        CountDelivered(loopback, 1200, send =>
        {
            long sent = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < window)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * datagramsPerSecond);
                while (sent < due)
                {
                    send();
                    sent++;
                }
                Thread.Sleep(1);
            }
            return sent;
        });

    /// <summary>
    /// Sends datagrams back to back, the way a fast download's first round arrives, and counts
    /// how many arrive. Anything a packet filter can't queue while it catches up is lost.
    /// </summary>
    public static Delivery UdpBurst(IPAddress loopback, int datagrams) =>
        CountDelivered(loopback, 512, send =>
        {
            for (var i = 0; i < datagrams; i++) send();
            return datagrams;
        });

    private static Delivery CountDelivered(IPAddress loopback, int datagramSize, Func<Action, long> sendAll)
    {
        using var receiver = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 8 * 1024 * 1024 };
        receiver.Bind(new IPEndPoint(loopback, 0));
        using var sender = new Socket(loopback.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        sender.Connect(receiver.LocalEndPoint!);
        long received = 0;
        var counter = new Thread(() =>
        {
            var buffer = new byte[2048];
            try
            {
                while (true)
                {
                    receiver.Receive(buffer);
                    Interlocked.Increment(ref received);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }) { IsBackground = true };
        counter.Start();

        var datagram = new byte[datagramSize];
        var sent = sendAll(() => sender.Send(datagram));

        // Datagrams still in flight get a moment to land before the count is read.
        Thread.Sleep(1000);
        var arrived = Interlocked.Read(ref received);
        receiver.Close();
        counter.Join(IoTimeoutMs);
        return new Delivery(sent, arrived, sent == 0 ? 0 : 100.0 * (sent - arrived) / sent);
    }

    /// <summary>Runs one round trip after another for the window, after a warmup that isn't counted.</summary>
    private static List<long> Measure(TimeSpan window, Func<bool> roundTrip)
    {
        var samples = new List<long>(200_000);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Warmup + window)
        {
            var start = Stopwatch.GetTimestamp();
            if (!roundTrip()) continue;
            var ticks = Stopwatch.GetTimestamp() - start;
            if (clock.Elapsed >= Warmup) samples.Add(ticks);
        }
        return samples;
    }

    private static RoundTrips Summarize(List<long> ticks, int lost)
    {
        if (ticks.Count == 0) throw new TimeoutException("No round trip completed.");
        ticks.Sort();
        double Micro(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        long At(double share) => ticks[Math.Min(ticks.Count - 1, (int)(share * ticks.Count))];
        return new RoundTrips(ticks.Count, lost, Micro(At(0.50)), Micro(At(0.99)), Micro(ticks[^1]));
    }
}
