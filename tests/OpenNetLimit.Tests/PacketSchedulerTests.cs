using OpenNetLimit.Engine.RateLimiting;
using SharpDivert;
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

    [Fact]
    public void Enqueue_ExceedingMaxDelay_IncrementsTotalDropped()
    {
        using var scheduler = new PacketScheduler();
        var packetData = new byte[64];
        var addr = new WinDivertAddress[1];

        // Delay exceeding MaxDelay should be dropped
        scheduler.Enqueue(1, packetData, addr, PacketScheduler.MaxDelay + TimeSpan.FromSeconds(1));

        Assert.Equal(1, scheduler.TotalDropped);
        Assert.Equal(0, scheduler.TotalDelayed);
    }

    [Fact]
    public void Enqueue_WithinMaxDelay_IncrementsTotalDelayed()
    {
        using var scheduler = new PacketScheduler();
        var packetData = new byte[64];
        var addr = new WinDivertAddress[1];

        scheduler.Enqueue(1, packetData, addr, TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, scheduler.TotalDropped);
        Assert.Equal(1, scheduler.TotalDelayed);
    }

    [Fact]
    public void Enqueue_ExceedingQueueCapacity_Drops()
    {
        using var scheduler = new PacketScheduler();
        var packetData = new byte[64];
        var addr = new WinDivertAddress[1];

        // Fill the queue to capacity
        for (int i = 0; i < PacketScheduler.MaxQueuePerProcess; i++)
            scheduler.Enqueue(42, packetData, addr, TimeSpan.FromMilliseconds(500));

        Assert.Equal(PacketScheduler.MaxQueuePerProcess, scheduler.TotalDelayed);
        Assert.Equal(0, scheduler.TotalDropped);

        // Next enqueue should be dropped (queue full)
        scheduler.Enqueue(42, packetData, addr, TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, scheduler.TotalDropped);
    }

    [Fact]
    public void Enqueue_MultipleProcesses_SeparateQueues()
    {
        using var scheduler = new PacketScheduler();
        var packetData = new byte[64];
        var addr = new WinDivertAddress[1];

        // Each process has its own queue
        scheduler.Enqueue(1, packetData, addr, TimeSpan.FromMilliseconds(100));
        scheduler.Enqueue(2, packetData, addr, TimeSpan.FromMilliseconds(100));
        scheduler.Enqueue(3, packetData, addr, TimeSpan.FromMilliseconds(100));

        Assert.Equal(3, scheduler.TotalDelayed);
        Assert.Equal(0, scheduler.TotalDropped);
    }

    [Fact]
    public void RemoveProcess_RemovesQueue()
    {
        using var scheduler = new PacketScheduler();
        var packetData = new byte[64];
        var addr = new WinDivertAddress[1];

        scheduler.Enqueue(99, packetData, addr, TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, scheduler.TotalDelayed);

        scheduler.RemoveProcess(99);

        // After removal, a new enqueue creates a fresh queue
        scheduler.Enqueue(99, packetData, addr, TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, scheduler.TotalDelayed);
    }
}
