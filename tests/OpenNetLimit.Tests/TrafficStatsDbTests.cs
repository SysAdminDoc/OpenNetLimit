using OpenNetLimit.Core.Models;
using OpenNetLimit.Service.Storage;
using Xunit;

namespace OpenNetLimit.Tests;

public class TrafficStatsDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TrafficStatsDb _db;

    public TrafficStatsDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"onl_test_{Guid.NewGuid()}.db");
        _db = new TrafficStatsDb(_dbPath);
    }

    [Fact]
    public void RecordSnapshot_StoresHourlyData()
    {
        var snapshot = MakeSnapshot("chrome", 1000, 500);
        _db.RecordSnapshot(snapshot);

        var stats = _db.GetHourlyStats("chrome", 1);
        Assert.Single(stats);
        Assert.Equal(1000, stats[0].BytesReceived);
        Assert.Equal(500, stats[0].BytesSent);
    }

    [Fact]
    public void RecordSnapshot_AccumulatesBytes()
    {
        _db.RecordSnapshot(MakeSnapshot("chrome", 1000, 500));
        _db.RecordSnapshot(MakeSnapshot("chrome", 2000, 1000));

        var stats = _db.GetHourlyStats("chrome", 1);
        Assert.Single(stats);
        Assert.Equal(3000, stats[0].BytesReceived);
        Assert.Equal(1500, stats[0].BytesSent);
    }

    [Fact]
    public void RecordSnapshot_StoresDailyData()
    {
        _db.RecordSnapshot(MakeSnapshot("firefox", 5000, 3000));

        var stats = _db.GetDailyStats("firefox", 1);
        Assert.Single(stats);
        Assert.Equal(5000, stats[0].BytesReceived);
    }

    [Fact]
    public void GetHourlyStats_AllProcesses_AggregatesCorrectly()
    {
        _db.RecordSnapshot(MakeSnapshot("chrome", 1000, 500));
        _db.RecordSnapshot(MakeSnapshot("firefox", 2000, 1000));

        var stats = _db.GetHourlyStats(null, 1);
        Assert.Single(stats);
        Assert.Equal(3000, stats[0].BytesReceived);
    }

    [Fact]
    public void GetTopProcesses_OrdersByTotalBytes()
    {
        _db.RecordSnapshot(MakeSnapshot("chrome", 100, 50));
        _db.RecordSnapshot(MakeSnapshot("firefox", 5000, 3000));

        var top = _db.GetTopProcesses(days: 1);
        Assert.Equal(2, top.Count);
        Assert.Equal("firefox", top[0].ProcessName);
    }

    [Fact]
    public void SkipsZeroTrafficProcesses()
    {
        _db.RecordSnapshot(MakeSnapshot("idle", 0, 0));
        var stats = _db.GetHourlyStats("idle", 1);
        Assert.Empty(stats);
    }

    private static TrafficSnapshot MakeSnapshot(string processName, long down, long up) => new()
    {
        Processes =
        [
            new ProcessTrafficInfo
            {
                ProcessId = 1,
                ProcessName = processName,
                CurrentDownloadBytesPerSecond = down,
                CurrentUploadBytesPerSecond = up
            }
        ]
    };

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }
}
