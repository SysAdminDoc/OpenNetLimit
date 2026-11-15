using System.Net;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
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
        Assert.Equal(counts, PcLimitScope.Applies(Down(1000), isUpload: false, IPAddress.Parse(remote)));
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

        Assert.Equal(counts, PcLimitScope.Applies(limit, isUpload: false, IPAddress.Parse(remote)));
    }

    [Fact]
    public void DirectionWithoutACap_CountsNothing()
    {
        Assert.False(PcLimitScope.Applies(Down(1000), isUpload: true, IPAddress.Parse("8.8.8.8")));
        Assert.False(PcLimitScope.Applies(null, isUpload: false, IPAddress.Parse("8.8.8.8")));
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
