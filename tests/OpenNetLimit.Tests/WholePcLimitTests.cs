using System.Net;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.UI;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Xunit;
using Cli = OpenNetLimit.CLI.Program;

namespace OpenNetLimit.Tests;

public sealed class WholePcLimitTests
{
    private static readonly TimeSpan MaxDelay = PacketScheduler.MaxDelay;
    private long _now;

    private ProcessRateLimiter NewLimiter() => new(() => _now);

    private static WholePcLimit Down(long bytesPerSecond) => new() { DownloadBytesPerSecond = bytesPerSecond };

    [Fact]
    public void SharedLimit_SpreadsEveryAppsTrafficOverOneBudget()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));

        Assert.True(limiter.TryReserve(7, 100_000, isUpload: false, wholePc: true, MaxDelay, out var first));
        Assert.True(limiter.TryReserve(8, 50_000, isUpload: false, wholePc: true, MaxDelay, out var second));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(TimeSpan.FromSeconds(0.5), second);
    }

    [Fact]
    public void SharedLimit_RefillsAtItsRate()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.TryReserve(7, 100_000, isUpload: false, wholePc: true, MaxDelay, out _);

        _now += 500;

        Assert.True(limiter.TryReserve(8, 50_000, isUpload: false, wholePc: true, MaxDelay, out var halfASecondLater));
        Assert.True(limiter.TryReserve(8, 50_000, isUpload: false, wholePc: true, MaxDelay, out var overThat));
        Assert.Equal(TimeSpan.Zero, halfASecondLater);
        Assert.Equal(TimeSpan.FromSeconds(0.5), overThat);
    }

    [Fact]
    public void PacketTheLimitDoesntCover_WaitsOnlyForItsApp()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.TryReserve(7, 150_000, isUpload: false, wholePc: true, MaxDelay, out _);

        Assert.True(limiter.TryReserve(8, 50_000, isUpload: false, wholePc: false, MaxDelay, out var local));
        Assert.True(limiter.TryReserve(8, 500_000, isUpload: true, wholePc: true, MaxDelay, out var upload));

        Assert.Equal(TimeSpan.Zero, local);
        Assert.Equal(TimeSpan.Zero, upload);
    }

    [Fact]
    public void AppWithItsOwnLimit_WaitsForWhicheverLimitNeedsLonger()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.SetLimit(1, 50_000, 0);

        // The app's bucket holds 64 KiB and the PC's 100,000 bytes, so the app's is the one short.
        Assert.True(limiter.TryReserve(1, 80_000, isUpload: false, wholePc: true, MaxDelay, out var appLonger));
        // The PC has 20,000 bytes left and this app has no limit of its own.
        Assert.True(limiter.TryReserve(2, 70_000, isUpload: false, wholePc: true, MaxDelay, out var pcLonger));

        Assert.Equal(TimeSpan.FromSeconds((80_000 - 65_536) / 50_000.0), appLonger);
        Assert.Equal(TimeSpan.FromSeconds(0.5), pcLonger);
    }

    [Fact]
    public void PacketThePcLimitDrops_GivesTheAppItsShareBack()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(10_000));
        limiter.SetLimit(1, 50_000, 0);
        limiter.TryReserve(1, 60_000, isUpload: false, wholePc: true, MaxDelay, out _);

        // The PC would need 5.4 s for it, past the 2 s anything may wait.
        Assert.False(limiter.TryReserve(1, 60_000, isUpload: false, wholePc: true, MaxDelay, out var dropped));
        // So the app still has the 5,536 bytes it had before it.
        Assert.True(limiter.TryReserve(1, 5_536, isUpload: false, wholePc: false, MaxDelay, out var next));

        Assert.Equal(TimeSpan.Zero, dropped);
        Assert.Equal(TimeSpan.Zero, next);
    }

    [Fact]
    public void PacketItsAppDrops_IsNotChargedToThePc()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(1_000_000));
        limiter.SetLimit(1, 10_000, 0);
        limiter.TryReserve(1, 65_536, isUpload: false, wholePc: true, MaxDelay, out _);

        Assert.False(limiter.TryReserve(1, 65_536, isUpload: false, wholePc: true, MaxDelay, out _));
        Assert.True(limiter.TryReserve(2, 1_000_000 - 65_536, isUpload: false, wholePc: true, MaxDelay, out var rest));

        Assert.Equal(TimeSpan.Zero, rest);
    }

    [Fact]
    public void BlockedApp_IsDroppedWithoutUsingThePcsBudget()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.Block(1);

        Assert.False(limiter.TryReserve(1, 50_000, isUpload: false, wholePc: true, MaxDelay, out _));
        Assert.True(limiter.TryReserve(2, 100_000, isUpload: false, wholePc: true, MaxDelay, out var rest));

        Assert.Equal(TimeSpan.Zero, rest);
    }

    [Fact]
    public void ChangingOneDirection_KeepsTheOthersQueue()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.TryReserve(1, 150_000, isUpload: false, wholePc: true, MaxDelay, out _);

        limiter.SetPcLimit(Down(100_000) with { UploadBytesPerSecond = 5_000 });
        limiter.TryReserve(1, 1, isUpload: false, wholePc: true, MaxDelay, out var unchanged);
        limiter.SetPcLimit(Down(200_000));
        limiter.TryReserve(1, 1, isUpload: false, wholePc: true, MaxDelay, out var changed);

        Assert.True(unchanged > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, changed);
    }

    [Fact]
    public void RemovingEveryAppLimit_KeepsThePcLimit()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));
        limiter.SetLimit(1, 1000, 0);

        limiter.RemoveAll();

        Assert.Equal(Down(100_000), limiter.PcLimit);
        Assert.False(limiter.HasLimit(1));
    }

    [Fact]
    public void ZeroBothWays_TurnsItOff()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000));

        limiter.SetPcLimit(new WholePcLimit { IncludeLan = true });

        Assert.Null(limiter.PcLimit);
        Assert.True(limiter.TryReserve(1, 1_000_000, isUpload: false, wholePc: true, MaxDelay, out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Refund_NeverFillsTheBucketPastItsSize()
    {
        var bucket = new TokenBucket(100_000, () => _now);
        bucket.TryReserve(150_000, MaxDelay, out _);
        _now += 1_500;

        bucket.Refund(150_000);

        Assert.Equal(100_000, bucket.AvailableTokens);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:127.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::fb", false)]
    public void InternetOnly_CountsTrafficThatLeavesTheLocalNetwork(string remote, bool counts)
    {
        Assert.Equal(counts, PcLimitScope.Applies(Down(1000), isUpload: false, IPAddress.Parse(remote), loopback: false));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.1.2.3", false)]
    public void WithLan_CountsEverythingButThisPc(string remote, bool counts)
    {
        var limit = Down(1000) with { IncludeLan = true };

        Assert.Equal(counts, PcLimitScope.Applies(limit, isUpload: false, IPAddress.Parse(remote), loopback: false));
    }

    [Theory]
    [InlineData("192.168.1.27")]
    [InlineData("203.0.113.9")]
    public void PacketThatNeverLeavesThePc_NeverCounts(string ownAddress)
    {
        // Traffic to the PC's own LAN or public address is loopback to Windows, not just 127/8.
        var limit = Down(1000) with { IncludeLan = true };

        Assert.False(PcLimitScope.Applies(limit, isUpload: false, IPAddress.Parse(ownAddress), loopback: true));
        Assert.True(PcLimitScope.Applies(limit, isUpload: false, IPAddress.Parse(ownAddress), loopback: false));
    }

    [Theory]
    // Windows Update, BITS, Delivery Optimization and SMB copies run in svchost or System over TCP.
    [InlineData(true, TransportProtocol.Tcp, 443, true)]
    [InlineData(true, TransportProtocol.Tcp, 445, true)]
    // DNS, DHCP and NTP stay free, DNS that falls back to TCP for a big answer too.
    [InlineData(true, TransportProtocol.Udp, 53, false)]
    [InlineData(true, TransportProtocol.Udp, 123, false)]
    [InlineData(true, TransportProtocol.Tcp, 53, false)]
    [InlineData(false, TransportProtocol.Tcp, 443, true)]
    [InlineData(false, TransportProtocol.Tcp, 53, true)]
    [InlineData(false, TransportProtocol.Udp, 443, true)]
    public void WindowsServices_CountTowardTheWholePcLimitOverTcpOnly(bool isProtected, TransportProtocol protocol,
        int remotePort, bool counts)
    {
        var limits = WinDivertInterceptor.LimitsFor(isProtected, protocol, remotePort, payloadLength: 1400,
            closesFlow: false, pcLimitCounts: true, hasAppLimit: false);

        Assert.Equal(counts, limits.WholePc);
        Assert.False(limits.App);
    }

    [Fact]
    public void WindowsServices_NeverGetAnAppLimit()
    {
        var service = WinDivertInterceptor.LimitsFor(isProtected: true, TransportProtocol.Tcp, 443, 1400, closesFlow: false,
            pcLimitCounts: false, hasAppLimit: true);
        var app = WinDivertInterceptor.LimitsFor(isProtected: false, TransportProtocol.Tcp, 443, 1400, closesFlow: false,
            pcLimitCounts: false, hasAppLimit: true);

        Assert.False(service.Any);
        Assert.True(app.App);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    public void PacketWithNoData_WaitsOnlyWhenItClosesTheFlow(int payloadLength, bool closesFlow, bool waits)
    {
        // An upload's ACKs come in on the download side. Holding them behind a download cap's
        // queue slowed the upload too.
        var limits = WinDivertInterceptor.LimitsFor(isProtected: false, TransportProtocol.Tcp, 443, payloadLength, closesFlow,
            pcLimitCounts: true, hasAppLimit: true);

        Assert.Equal(waits, limits.WholePc);
        Assert.Equal(waits, limits.App);
    }

    // The packet loop's own step, from a packet's facts to whether it goes, waits or is dropped.
    // The parts above were tested, but how the loop joined them wasn't: dropping WinDivert's
    // loopback flag or charging the wrong bucket there failed no test.
    private const uint ServicePid = 4;
    private const uint AppPid = 1;

    private WinDivertInterceptor.PacketFate ChargeDownload(ProcessRateLimiter limiter, uint processId, bool isProtected,
        string remote, bool loopback = false, int remotePort = 443, TransportProtocol protocol = TransportProtocol.Tcp)
    {
        var fate = WinDivertInterceptor.Charge(limiter, new PacketScheduler(), processId, isProtected, protocol,
            remotePort, payloadLength: 1000, closesFlow: false, isOutbound: false, IPAddress.Parse(remote), loopback,
            out var delay);
        Assert.Equal(fate == WinDivertInterceptor.PacketFate.Delay, delay > TimeSpan.Zero);
        return fate;
    }

    private ProcessRateLimiter LimiterWithItsPcLimitUsedUp(WholePcLimit limit)
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(limit);
        // A new bucket starts full at 64 KB.
        Assert.True(limiter.TryReserveWholePc(65_536, isUpload: false, MaxDelay, out _));
        return limiter;
    }

    [Fact]
    public void Charge_AWindowsServiceDownload_WaitsForTheWholePcLimit()
    {
        var limiter = LimiterWithItsPcLimitUsedUp(Down(10_000));

        Assert.Equal(WinDivertInterceptor.PacketFate.Delay, ChargeDownload(limiter, ServicePid, isProtected: true, "203.0.113.9"));
        Assert.Equal(WinDivertInterceptor.PacketFate.Send,
            ChargeDownload(limiter, ServicePid, isProtected: true, "203.0.113.9", protocol: TransportProtocol.Udp));
        Assert.Equal(WinDivertInterceptor.PacketFate.Send,
            ChargeDownload(limiter, ServicePid, isProtected: true, "203.0.113.9", remotePort: 53));
    }

    [Theory]
    [InlineData("192.168.1.27")]
    [InlineData("203.0.113.9")]
    public void Charge_TrafficThatNeverLeavesThePc_GoesStraightThrough(string ownAddress)
    {
        var limiter = LimiterWithItsPcLimitUsedUp(Down(10_000) with { IncludeLan = true });

        Assert.Equal(WinDivertInterceptor.PacketFate.Send,
            ChargeDownload(limiter, AppPid, isProtected: false, ownAddress, loopback: true));
        Assert.Equal(WinDivertInterceptor.PacketFate.Delay,
            ChargeDownload(limiter, AppPid, isProtected: false, ownAddress, loopback: false));
    }

    [Fact]
    public void Charge_AnAppWithItsOwnLimit_WaitsForItsOwnLimitToo()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(100_000_000));
        limiter.SetLimit(AppPid, downloadBytesPerSecond: 10_000, uploadBytesPerSecond: 0);
        Assert.True(limiter.TryReserve(AppPid, 65_536, isUpload: false, wholePc: false, MaxDelay, out _));

        Assert.Equal(WinDivertInterceptor.PacketFate.Delay, ChargeDownload(limiter, AppPid, isProtected: false, "203.0.113.9"));
    }

    [Fact]
    public void Charge_AnUploadUnderADownloadCap_GoesStraightOut()
    {
        var limiter = LimiterWithItsPcLimitUsedUp(Down(10_000));

        var fate = WinDivertInterceptor.Charge(limiter, new PacketScheduler(), AppPid, isProtected: false,
            TransportProtocol.Tcp, 443, payloadLength: 1000, closesFlow: false, isOutbound: true,
            IPAddress.Parse("203.0.113.9"), loopback: false, out var delay);

        Assert.Equal(WinDivertInterceptor.PacketFate.Send, fate);
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Charge_PastTheLongestWait_IsDropped()
    {
        var limiter = LimiterWithItsPcLimitUsedUp(Down(1_000));

        var fate = WinDivertInterceptor.Charge(limiter, new PacketScheduler(), AppPid, isProtected: false,
            TransportProtocol.Tcp, 443, payloadLength: 60_000, closesFlow: false, isOutbound: false,
            IPAddress.Parse("203.0.113.9"), loopback: false, out _);

        Assert.Equal(WinDivertInterceptor.PacketFate.Drop, fate);
    }

    [Fact]
    public void WholePcOnly_ChargesTheSharedLimitAndNotTheApps()
    {
        var limiter = NewLimiter();
        limiter.SetLimit(1, downloadBytesPerSecond: 1000, uploadBytesPerSecond: 0);
        limiter.SetPcLimit(Down(100_000));

        Assert.True(limiter.TryReserveWholePc(100_000, isUpload: false, MaxDelay, out var first));
        Assert.True(limiter.TryReserveWholePc(50_000, isUpload: false, MaxDelay, out var second));
        Assert.True(limiter.TryReserve(1, 60_000, isUpload: false, wholePc: false, MaxDelay, out var appsOwn));

        Assert.Equal(TimeSpan.Zero, first);
        Assert.Equal(TimeSpan.FromSeconds(0.5), second);
        Assert.Equal(TimeSpan.Zero, appsOwn);
    }

    [Fact]
    public void WholePcOnly_PastTheLongestWait_IsDroppedAndChargesNothing()
    {
        var limiter = NewLimiter();
        limiter.SetPcLimit(Down(10_000));
        // A new bucket starts full at 64 KB.
        limiter.TryReserveWholePc(65_536, isUpload: false, MaxDelay, out _);

        Assert.False(limiter.TryReserveWholePc(1_000_000, isUpload: false, MaxDelay, out var dropped));
        Assert.True(limiter.TryReserveWholePc(1_000, isUpload: false, MaxDelay, out var next));

        Assert.Equal(TimeSpan.Zero, dropped);
        Assert.Equal(TimeSpan.FromSeconds(0.1), next);
    }

    [Fact]
    public void WholePcOnly_WithNoLimitThatWay_NeverWaits()
    {
        var limiter = NewLimiter();
        Assert.True(limiter.TryReserveWholePc(1_000_000, isUpload: false, MaxDelay, out var none));
        limiter.SetPcLimit(Down(1000));
        Assert.True(limiter.TryReserveWholePc(1_000_000, isUpload: true, MaxDelay, out var otherWay));

        Assert.Equal(TimeSpan.Zero, none);
        Assert.Equal(TimeSpan.Zero, otherWay);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"includeLan":true}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1024")]
    [InlineData("")]
    public void Parse_WithNoRate_SaysWhatsMissing(string json)
    {
        Assert.Null(WholePcLimit.Parse(json, out var problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("DownloadBytes")]
    [InlineData("download")]
    [InlineData("uploadKBps")]
    public void Parse_WithAMisspelledField_NamesIt(string field)
    {
        var json = $"{{\"{field}\":1024,\"uploadBytesPerSecond\":0}}";

        Assert.Null(WholePcLimit.Parse(json, out var problem));
        Assert.Contains($"\"{field}\"", problem);
    }

    [Fact]
    public void Parse_TakesFieldNamesInAnyCase()
    {
        var limit = WholePcLimit.Parse("""{"DownloadBytesPerSecond":2048,"INCLUDELAN":true}""", out var problem);

        Assert.Null(problem);
        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 2048, IncludeLan = true }, limit);
    }

    [Theory]
    [InlineData("""{"downloadBytesPerSecond":"fast"}""", "invalid JSON")]
    [InlineData("""{"downloadBytesPerSecond":null}""", "invalid JSON")]
    [InlineData("""{"downloadBytesPerSecond":-1}""", "negative")]
    [InlineData("""{"downloadBytesPerSecond":""", "invalid JSON")]
    public void Parse_WithABadValue_SaysWhy(string json, string why)
    {
        Assert.Null(WholePcLimit.Parse(json, out var problem));
        Assert.Contains(why, problem);
    }

    [Theory]
    // The last copy won, so a second spelling set to 0 switched the limit off.
    [InlineData("""{"downloadBytesPerSecond":1048576,"DownloadBytesPerSecond":0}""", "downloadBytesPerSecond")]
    [InlineData("""{"uploadBytesPerSecond":1024,"uploadBytesPerSecond":0}""", "uploadBytesPerSecond")]
    [InlineData("""{"downloadBytesPerSecond":1024,"includeLan":true,"INCLUDELAN":false}""", "includeLan")]
    public void Parse_WithAFieldGivenTwice_SaysWhich(string json, string field)
    {
        Assert.Null(WholePcLimit.Parse(json, out var problem));
        Assert.Contains($"{field} is in it more than once", problem);
    }

    [Fact]
    public void Parse_ReadsWhatTheServiceSaves()
    {
        var saved = new WholePcLimit { DownloadBytesPerSecond = 1_000_000, UploadBytesPerSecond = 65_536, IncludeLan = true };
        var json = System.Text.Json.JsonSerializer.Serialize(saved,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true });

        Assert.Equal(saved, WholePcLimit.Parse(json, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void DirectionWithoutACap_CountsNothing()
    {
        Assert.False(PcLimitScope.Applies(Down(1000), isUpload: true, IPAddress.Parse("8.8.8.8"), loopback: false));
        Assert.False(PcLimitScope.Applies(null, isUpload: false, IPAddress.Parse("8.8.8.8"), loopback: false));
    }

    [Fact]
    public void NegativeRate_IsAProblem()
    {
        Assert.NotNull(Down(-1).Problem());
        Assert.NotNull(new WholePcLimit { UploadBytesPerSecond = -1 }.Problem());
        Assert.Null(Down(0).Problem());
    }

    [Fact]
    public void ReadingTheLimit_IsOpenToEveryone_SettingItNeedsAdmin()
    {
        Assert.True(IpcProtocol.IsValidCommand("PC_LIMIT"));
        Assert.False(IpcProtocol.RequiresAdmin("PC_LIMIT"));
        Assert.True(IpcProtocol.RequiresAdmin("SET_PC_LIMIT"));
    }

    [Fact]
    public void Cli_Set_TurnsKilobytesIntoBytes()
    {
        var error = new StringWriter();

        var down = Cli.PcLimitBody(["--download", "1024"], error);
        var up = Cli.PcLimitBody(["--upload", "64", "--include-lan"], error);

        Assert.NotNull(down);
        Assert.Equal(1_048_576L, down["downloadBytesPerSecond"]);
        Assert.Equal(0L, down["uploadBytesPerSecond"]);
        Assert.False((bool)down["includeLan"]);
        Assert.NotNull(up);
        Assert.Equal(0L, up["downloadBytesPerSecond"]);
        Assert.Equal(65_536L, up["uploadBytesPerSecond"]);
        Assert.True((bool)up["includeLan"]);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void Cli_SetWithNoRate_PointsAtOff()
    {
        var error = new StringWriter();

        Assert.Null(Cli.PcLimitBody(["--include-lan"], error));
        Assert.Contains("onl pc-limit off", error.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("9007199254740993")]
    public void Cli_SetWithABadRate_SaysWhichOne(string value)
    {
        var error = new StringWriter();

        Assert.Null(Cli.PcLimitBody(["--download", value], error));
        Assert.Contains($"--download value '{value}'", error.ToString());
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData(" 12 ", true)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("abc", false)]
    [InlineData("9007199254740993", false)]
    public void Dialog_TakesWholeKilobytesThatFitInBytes(string text, bool valid)
    {
        Assert.Equal(valid, SetLimitDialog.TryParseKBps(text, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1500, 1)]
    [InlineData(2048, 2)]
    public void Dialog_ShowsTheLimitInKilobytes(long bytesPerSecond, long kilobytes)
    {
        Assert.Equal(kilobytes, PcLimitText.Kilobytes(bytesPerSecond));
    }

    [Theory]
    // Left alone, a rate set in bytes elsewhere stays exact.
    [InlineData(null, 1_000_000, 1_000_000)]
    [InlineData(null, 500, 500)]
    [InlineData(null, 0, 0)]
    // Typed in, it's what was typed.
    [InlineData(977L, 1_000_000, 1_000_448)]
    [InlineData(2L, 500, 2048)]
    [InlineData(0L, 1_000_000, 0)]
    [InlineData(64L, 0, 65_536)]
    // Even the number it showed. Matching what it showed used to keep the old rate, so these
    // two rates couldn't be set over these limits at all.
    [InlineData(976L, 1_000_000, 999_424)]
    [InlineData(1L, 500, 1024)]
    public void Dialog_KeepsTheExactRateUnlessItWasTypedIn(long? kilobytes, long now, long bytes)
    {
        var limit = PcLimitText.FromDialog(new WholePcLimit { DownloadBytesPerSecond = now, UploadBytesPerSecond = now },
            kilobytes, kilobytes, includeLan: null);

        Assert.Equal(bytes, limit.DownloadBytesPerSecond);
        Assert.Equal(bytes, limit.UploadBytesPerSecond);
    }

    [Fact]
    public void Dialog_LeftAlone_KeepsAChangeMadeWhileItWasOpen()
    {
        // The dialog showed 1 MB/s, then the CLI set 2 MB/s and counted the local network.
        // OK after typing only an upload rate put the old download rate back.
        var now = new WholePcLimit { DownloadBytesPerSecond = 2_097_152, IncludeLan = true };

        var sent = PcLimitText.FromDialog(now, downloadKBps: null, uploadKBps: 512, includeLan: null);

        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 2_097_152, UploadBytesPerSecond = 524_288, IncludeLan = true }, sent);
        Assert.False(PcLimitText.FromDialog(now, null, null, includeLan: false).IncludeLan);
    }
}

[Collection(CultureSwitchingCollection.Name)]
public sealed class PcLimitTextTests
{
    [Fact]
    public void Off_SaysSo()
    {
        Assert.Equal(LocalizationManager.Text("PcLimit_Off"), PcLimitText.Rates(WholePcLimit.Off));
        Assert.Equal(LocalizationManager.Text("PcLimit_OffHint"), PcLimitText.Scope(WholePcLimit.Off with { IncludeLan = true }));
    }

    [Fact]
    public void On_ShowsEachCappedDirection()
    {
        var both = new WholePcLimit { DownloadBytesPerSecond = 2 * 1024 * 1024, UploadBytesPerSecond = 512 * 1024 };
        var upOnly = new WholePcLimit { UploadBytesPerSecond = 512 * 1024, IncludeLan = true };

        Assert.Equal($"↓ {ProcessViewModel.FormatBytes(2 * 1024 * 1024)}  ↑ {ProcessViewModel.FormatBytes(512 * 1024)}",
            PcLimitText.Rates(both));
        Assert.Equal($"↑ {ProcessViewModel.FormatBytes(512 * 1024)}", PcLimitText.Rates(upOnly));
        Assert.Equal(LocalizationManager.Text("PcLimit_InternetOnly"), PcLimitText.Scope(both));
        Assert.Equal(LocalizationManager.Text("PcLimit_WithLan"), PcLimitText.Scope(upOnly));
    }
}

// The card's state used to live in the main window's view model, which tests can't build, so
// nothing caught a regression in what it shows after a change fails.
[Collection(CultureSwitchingCollection.Name)]
public sealed class PcLimitCardTests
{
    private static readonly WholePcLimit OneMeg = new() { DownloadBytesPerSecond = 1_048_576 };

    [Fact]
    public void AChangeWithNoService_SaysSoAndKeepsTheLimit()
    {
        var card = new PcLimitCard();

        card.Answered(null, OneMeg);

        Assert.Equal(WholePcLimit.Off, card.Limit);
        Assert.Equal(LocalizationManager.Text("Rules_NoService"), card.Error);
        Assert.Equal(card.Error, card.Hint);
    }

    [Fact]
    public void AChangeTheServiceTurnsDown_GivesItsReason()
    {
        var card = new PcLimitCard();

        card.Answered("""{"error":"no rate in it"}""", OneMeg);

        Assert.Equal(LocalizationManager.Format("Rules_ServiceError", "no rate in it"), card.Hint);
        Assert.Equal(WholePcLimit.Off, card.Limit);
    }

    [Fact]
    public void AChangeThatTakes_ShowsWhatTheServiceSaysItSet()
    {
        var card = new PcLimitCard();
        card.Answered(null, OneMeg);

        card.Answered("""{"downloadBytesPerSecond":1000000,"uploadBytesPerSecond":0,"includeLan":true}""", OneMeg);

        Assert.Null(card.Error);
        Assert.Equal(new WholePcLimit { DownloadBytesPerSecond = 1_000_000, IncludeLan = true }, card.Limit);
        Assert.Equal(PcLimitText.Scope(card.Limit), card.Hint);
    }

    [Fact]
    public void AnAnswerThatIsntALimit_ShowsTheOneSent()
    {
        var card = new PcLimitCard();

        card.Answered("OK", OneMeg);

        Assert.Null(card.Error);
        Assert.Equal(OneMeg, card.Limit);
    }

    [Fact]
    public void TheSameLimitPolledAgain_LeavesTheErrorUp()
    {
        var card = new PcLimitCard();
        card.Answered(null, OneMeg);

        Assert.False(card.Report(WholePcLimit.Off));
        Assert.False(card.ReportReply("""{"downloadBytesPerSecond":0,"uploadBytesPerSecond":0,"includeLan":false}"""));
        Assert.NotNull(card.Error);
    }

    [Fact]
    public void ALimitChangedFromElsewhere_ClearsTheError()
    {
        var card = new PcLimitCard();
        card.Answered(null, OneMeg);

        Assert.True(card.ReportReply("""{"downloadBytesPerSecond":2097152,"uploadBytesPerSecond":0,"includeLan":true}"""));

        Assert.Null(card.Error);
        Assert.Equal(2_097_152, card.Limit.DownloadBytesPerSecond);
    }

    [Fact]
    public void Reconnecting_ClearsTheErrorOnce()
    {
        var card = new PcLimitCard();
        card.Answered(null, OneMeg);

        Assert.True(card.Reconnected());
        Assert.False(card.Reconnected());
        Assert.Null(card.Error);
    }

    [Theory]
    // A service from before the whole-PC limit doesn't know the command.
    [InlineData("""{"error":"unknown command"}""")]
    [InlineData(null)]
    [InlineData("not json")]
    public void APollWithNoLimitInIt_ChangesNothing(string? reply)
    {
        var card = new PcLimitCard();
        card.Report(OneMeg);

        Assert.False(card.ReportReply(reply));
        Assert.Equal(OneMeg, card.Limit);
        Assert.Null(card.Error);
    }
}
