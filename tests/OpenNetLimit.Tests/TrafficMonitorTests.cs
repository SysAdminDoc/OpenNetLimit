using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using Xunit;

namespace OpenNetLimit.Tests;

public class TrafficMonitorTests
{
    [Fact]
    public void ReadingASnapshot_LeavesTheSpeedForTheNextReader()
    {
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 5000, isUpload: false);
        monitor.RecordBytes(1, "curl", 700, isUpload: true);
        monitor.SampleRates();

        var app = monitor.TakeSnapshot();
        var api = monitor.TakeSnapshot();

        foreach (var snapshot in new[] { app, api })
        {
            var curl = Assert.Single(snapshot.Processes);
            Assert.Equal(5000, curl.CurrentDownloadBytesPerSecond);
            Assert.Equal(700, curl.CurrentUploadBytesPerSecond);
            Assert.Equal(5000, snapshot.TotalDownloadBytesPerSecond);
            Assert.Equal(700, snapshot.TotalUploadBytesPerSecond);
        }
    }

    [Fact]
    public void Snapshot_ShowsTheLimitAProcessIsHeldTo()
    {
        var limiter = new ProcessRateLimiter();
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), limiter, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 5000, isUpload: false);
        monitor.RecordBytes(2, "git", 5000, isUpload: false);
        limiter.SetLimit(1, 262_144, 0);
        monitor.SampleRates();

        var snapshot = monitor.TakeSnapshot();
        var curl = snapshot.Processes.Single(p => p.ProcessId == 1);
        var git = snapshot.Processes.Single(p => p.ProcessId == 2);
        Assert.Equal(262_144, curl.DownloadLimitBytesPerSecond);
        Assert.Null(curl.UploadLimitBytesPerSecond);
        Assert.Null(git.DownloadLimitBytesPerSecond);

        limiter.RemoveLimit(1);
        monitor.SampleRates();
        Assert.Null(monitor.TakeSnapshot().Processes.Single(p => p.ProcessId == 1).DownloadLimitBytesPerSecond);
    }

    [Fact]
    public void Usage_CountsEveryByteOnce_WhoeverElseReadsTheMonitor()
    {
        using var monitor = new TrafficMonitor(TimeSpan.FromSeconds(1), null, sampleOnTimer: false);
        monitor.RecordBytes(1, "curl", 1000, isUpload: false);
        monitor.RecordBytes(1, "curl", 300, isUpload: true);
        monitor.SampleRates();
        monitor.TakeSnapshot();

        var first = Assert.Single(monitor.TakeUsage().Processes);
        Assert.Equal(1000, first.CurrentDownloadBytesPerSecond);
        Assert.Equal(300, first.CurrentUploadBytesPerSecond);

        monitor.RecordBytes(1, "curl", 500, isUpload: false);
        monitor.SampleRates();
        monitor.TakeSnapshot();

        var second = Assert.Single(monitor.TakeUsage().Processes);
        Assert.Equal(500, second.CurrentDownloadBytesPerSecond);
        Assert.Equal(0, second.CurrentUploadBytesPerSecond);

        var idle = monitor.TakeUsage();
        Assert.Equal(0, idle.TotalDownloadBytesPerSecond);
        Assert.Equal(0, idle.TotalUploadBytesPerSecond);
    }
}
