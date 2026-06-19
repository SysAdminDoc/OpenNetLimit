using OpenNetLimit.Engine.RateLimiting;
using Xunit;

namespace OpenNetLimit.Tests;

public class PacketSchedulerTests
{
    [Fact]
    public void NewScheduler_HasZeroCounters()
    {
        using var scheduler = new PacketScheduler();
        Assert.Equal(0, scheduler.TotalDelayed);
        Assert.Equal(0, scheduler.TotalDropped);
        Assert.Equal(0, scheduler.TotalSent);
    }

    [Fact]
    public void MaxQueuePerProcess_IsPositive()
    {
        Assert.True(PacketScheduler.MaxQueuePerProcess > 0);
    }

    [Fact]
    public void MaxDelay_IsReasonable()
    {
        Assert.True(PacketScheduler.MaxDelay > TimeSpan.Zero);
        Assert.True(PacketScheduler.MaxDelay <= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var scheduler = new PacketScheduler();
        scheduler.Dispose();
        scheduler.Dispose();
    }
}
