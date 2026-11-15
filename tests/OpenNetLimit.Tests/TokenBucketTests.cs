using OpenNetLimit.Engine.RateLimiting;
using Xunit;

namespace OpenNetLimit.Tests;

public class TokenBucketTests
{
    [Fact]
    public void NewBucket_HasFullCapacity()
    {
        var bucket = new TokenBucket(1000);
        Assert.True(bucket.AvailableTokens >= 1000);
    }

    [Fact]
    public void TryConsume_WithinCapacity_ReturnsTrue()
    {
        var bucket = new TokenBucket(100_000);
        Assert.True(bucket.TryConsume(1000));
    }

    [Fact]
    public void TryConsume_ExceedsCapacity_ReturnsFalse()
    {
        var bucket = new TokenBucket(100);
        bucket.TryConsume(65536);
        Assert.False(bucket.TryConsume(65536));
    }

    [Fact]
    public void GetDelay_WhenTokensAvailable_ReturnsZero()
    {
        var bucket = new TokenBucket(100_000);
        Assert.Equal(TimeSpan.Zero, bucket.GetDelay(1000));
    }

    [Fact]
    public void GetDelay_WhenTokensDepleted_ReturnsPositiveDelay()
    {
        var bucket = new TokenBucket(1000);
        bucket.TryConsume(65536);
        var delay = bucket.GetDelay(1000);
        Assert.True(delay > TimeSpan.Zero);
    }

    [Fact]
    public void Capacity_IsAtLeast64KB()
    {
        var bucket = new TokenBucket(100);
        Assert.True(bucket.CapacityBytes >= 65536);
    }

    [Fact]
    public void TokensRefill_AfterWait()
    {
        var bucket = new TokenBucket(1_000_000);
        bucket.TryConsume((int)bucket.CapacityBytes);

        Thread.Sleep(50);

        Assert.True(bucket.AvailableTokens > 0);
    }

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    [Fact]
    public void DelayedPackets_AreCharged_SoABurstLeavesAtTheLimit()
    {
        long now = 0;
        var bucket = new TokenBucket(1000, () => now);
        Assert.True(bucket.TryReserve(65536, Plenty, out var first));
        Assert.Equal(TimeSpan.Zero, first);

        for (int packet = 1; packet <= 5; packet++)
        {
            Assert.True(bucket.TryReserve(1000, Plenty, out var delay));
            Assert.Equal(TimeSpan.FromSeconds(packet), delay);
        }
    }

    [Fact]
    public void PacketThatWouldWaitTooLong_IsNotCharged()
    {
        long now = 0;
        var bucket = new TokenBucket(1000, () => now);
        bucket.TryReserve(65536, Plenty, out _);

        Assert.False(bucket.TryReserve(3000, TimeSpan.FromSeconds(2), out _));
        Assert.True(bucket.TryReserve(1000, TimeSpan.FromSeconds(2), out var delay));
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void Debt_IsPaidOffAtTheRefillRate()
    {
        long now = 0;
        var bucket = new TokenBucket(1000, () => now);
        bucket.TryReserve(65536, Plenty, out _);
        bucket.TryReserve(2000, Plenty, out _);

        now += 1000;
        Assert.True(bucket.TryReserve(1000, Plenty, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(2), delay);

        now += 5000;
        Assert.True(bucket.TryReserve(1000, Plenty, out delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }
}

public class ProcessRateLimiterTests
{
    [Fact]
    public void NoLimit_TryConsume_ReturnsTrue()
    {
        var limiter = new ProcessRateLimiter();
        Assert.True(limiter.TryConsume(1234, 5000, false));
    }

    [Fact]
    public void WithLimit_TryConsume_EnforcesLimit()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);

        limiter.TryConsume(1, 65536, false);
        Assert.False(limiter.TryConsume(1, 65536, false));
    }

    [Fact]
    public void HasLimit_ReturnsCorrectState()
    {
        var limiter = new ProcessRateLimiter();
        Assert.False(limiter.HasLimit(1));

        limiter.SetLimit(1, 1000, 1000);
        Assert.True(limiter.HasLimit(1));

        limiter.RemoveLimit(1);
        Assert.False(limiter.HasLimit(1));
    }

    [Fact]
    public void RemoveAll_ClearsAllLimits()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 1000);
        limiter.SetLimit(2, 2000, 2000);

        limiter.RemoveAll();

        Assert.False(limiter.HasLimit(1));
        Assert.False(limiter.HasLimit(2));
    }

    [Fact]
    public void GetDelay_NoLimit_ReturnsZero()
    {
        var limiter = new ProcessRateLimiter();
        Assert.Equal(TimeSpan.Zero, limiter.GetDelay(999, 5000, false));
    }

    [Fact]
    public void SeparateUploadDownloadLimits()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 500);

        limiter.TryConsume(1, 65536, false);
        var downloadDelay = limiter.GetDelay(1, 1000, false);

        limiter.TryConsume(1, 65536, true);
        var uploadDelay = limiter.GetDelay(1, 1000, true);

        Assert.True(downloadDelay > TimeSpan.Zero);
        Assert.True(uploadDelay > TimeSpan.Zero);
    }

    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(30);

    [Fact]
    public void TryReserve_SpreadsAProcessesBurstOnlyInItsLimitedDirection()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 0);
        limiter.TryReserve(1, 65536, isUpload: false, Plenty, out _);

        limiter.TryReserve(1, 1000, isUpload: false, Plenty, out var firstDelay);
        limiter.TryReserve(1, 1000, isUpload: false, Plenty, out var secondDelay);
        Assert.True(firstDelay > TimeSpan.FromSeconds(0.5), $"first delay {firstDelay}");
        Assert.True(secondDelay > firstDelay + TimeSpan.FromSeconds(0.5), $"second delay {secondDelay}");

        Assert.True(limiter.TryReserve(1, 1_000_000, isUpload: true, Plenty, out var uploadDelay));
        Assert.Equal(TimeSpan.Zero, uploadDelay);
        Assert.True(limiter.TryReserve(2, 1_000_000, isUpload: false, Plenty, out var unlimitedDelay));
        Assert.Equal(TimeSpan.Zero, unlimitedDelay);
    }

    [Fact]
    public void SettingTheSameRateAgain_KeepsTheDebtQueuedPacketsWaitBehind()
    {
        // The reconciler re-applies limits on a timer. A fresh bucket would start full and
        // let the next burst through at full speed.
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 0);
        limiter.TryReserve(1, 65536, isUpload: false, Plenty, out _);

        limiter.SetLimit(1, 1000, 0);

        limiter.TryReserve(1, 1000, isUpload: false, Plenty, out var delay);
        Assert.True(delay > TimeSpan.FromSeconds(0.5), $"delay {delay}");
    }

    [Fact]
    public void SettingANewRate_StartsFreshBuckets()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 1000, 0);
        limiter.TryReserve(1, 65536, isUpload: false, Plenty, out _);

        limiter.SetLimit(1, 2000, 0);

        limiter.TryReserve(1, 1000, isUpload: false, Plenty, out var delay);
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.True(limiter.TryGetLimit(1, out long download, out _));
        Assert.Equal(2000, download);
    }

    [Fact]
    public void BlockedProcess_GetsNoTrafficThrough_UntilALimitReplacesTheBlock()
    {
        // Before Block existed a used-up quota called SetLimit(pid, 0, 0), which means no limit.
        var limiter = new ProcessRateLimiter();
        limiter.Block(1);

        Assert.True(limiter.IsBlocked(1));
        Assert.True(limiter.HasLimit(1));
        Assert.False(limiter.TryReserve(1, 100, isUpload: false, Plenty, out _));
        Assert.False(limiter.TryReserve(1, 100, isUpload: true, Plenty, out _));
        Assert.False(limiter.TryConsume(1, 100, isUpload: false));
        Assert.False(limiter.IsBlocked(2));

        limiter.SetLimit(1, 1000, 0);
        Assert.False(limiter.IsBlocked(1));
        Assert.True(limiter.TryReserve(1, 100, isUpload: false, Plenty, out _));

        limiter.Block(1);
        limiter.RemoveLimit(1);
        Assert.False(limiter.IsBlocked(1));
        Assert.False(limiter.HasLimit(1));

        // A block has no buckets, so it must not pass for an unlimited rate.
        limiter.Block(1);
        limiter.SetLimit(1, 0, 0);
        Assert.False(limiter.IsBlocked(1));
    }

    [Fact]
    public void TryGetLimit_ReportsTheRatesInForce()
    {
        var limiter = new ProcessRateLimiter();
        limiter.SetLimit(1, 262_144, 0);

        Assert.True(limiter.TryGetLimit(1, out var download, out var upload));
        Assert.Equal(262_144, download);
        Assert.Equal(0, upload);
        Assert.False(limiter.TryGetLimit(2, out _, out _));
    }
}
