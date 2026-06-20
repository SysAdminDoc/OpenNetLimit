using OpenNetLimit.Engine.Monitoring;
using Xunit;

namespace OpenNetLimit.Tests;

public class ConnectionLoggerTests
{
    [Fact]
    public void Log_AddsEntry()
    {
        var logger = new ConnectionLogger();
        logger.Log(new ConnectionLogEntry { ProcessName = "test" });
        Assert.Equal(1, logger.Count);
    }

    [Fact]
    public void GetRecent_ReturnsLatestEntries()
    {
        var logger = new ConnectionLogger();
        for (int i = 0; i < 10; i++)
            logger.Log(new ConnectionLogEntry { ProcessName = $"proc-{i}" });

        var recent = logger.GetRecent(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("proc-7", recent[0].ProcessName);
        Assert.Equal("proc-8", recent[1].ProcessName);
        Assert.Equal("proc-9", recent[2].ProcessName);
    }

    [Fact]
    public void GetRecent_ReturnsAll_WhenFewerThanMax()
    {
        var logger = new ConnectionLogger();
        logger.Log(new ConnectionLogEntry { ProcessName = "a" });
        logger.Log(new ConnectionLogEntry { ProcessName = "b" });

        var recent = logger.GetRecent(100);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void Log_TrimsOldEntries_WhenOverMax()
    {
        var logger = new ConnectionLogger();
        for (int i = 0; i < ConnectionLogger.MaxEntries + 100; i++)
            logger.Log(new ConnectionLogEntry { ProcessName = $"proc-{i}" });

        // Count should be around MaxEntries (may be slightly over under contention)
        Assert.True(logger.Count <= ConnectionLogger.MaxEntries + 10);
    }

    [Fact]
    public void Count_ReflectsLoggedEntries()
    {
        var logger = new ConnectionLogger();
        Assert.Equal(0, logger.Count);
        logger.Log(new ConnectionLogEntry { ProcessName = "test" });
        Assert.Equal(1, logger.Count);
    }
}
