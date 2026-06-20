using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class BandwidthAlertTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"onl_alerts_{Guid.NewGuid()}.json");

    [Fact]
    public void Update_FiresAlertWhenThresholdExceeded()
    {
        var monitor = new TrafficMonitor();
        var tracker = new BandwidthAlertTracker(monitor);
        tracker.AddRule(new BandwidthAlertRule
        {
            Name = "Chrome high usage",
            ProcessName = "chrome",
            Direction = RuleDirection.Download,
            ThresholdBytesPerSecond = 1000
        });
        BandwidthAlertEvent? alert = null;
        tracker.OnAlert += e => alert = e;

        monitor.RecordBytes(1, "chrome", 1500, isUpload: false);
        monitor.TakeSnapshot();
        tracker.Update();

        Assert.NotNull(alert);
        Assert.Equal("chrome", alert.ProcessName);
        Assert.Equal(1500, alert.ObservedBytesPerSecond);
    }

    [Fact]
    public void Update_RespectsCooldown()
    {
        var monitor = new TrafficMonitor();
        var tracker = new BandwidthAlertTracker(monitor);
        tracker.AddRule(new BandwidthAlertRule
        {
            ProcessName = "chrome",
            ThresholdBytesPerSecond = 1000,
            CooldownSeconds = 60
        });
        var count = 0;
        tracker.OnAlert += _ => count++;

        monitor.RecordBytes(1, "chrome", 1500, isUpload: false);
        monitor.TakeSnapshot();
        tracker.Update();
        monitor.RecordBytes(1, "chrome", 1600, isUpload: false);
        monitor.TakeSnapshot();
        tracker.Update();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Rule_MatchesWildcardPath()
    {
        var rule = new BandwidthAlertRule
        {
            ProcessPath = @"*\chrome.exe",
            ThresholdBytesPerSecond = 1000
        };
        var process = new ProcessTrafficInfo
        {
            ProcessName = "chrome",
            ProcessPath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            CurrentDownloadBytesPerSecond = 2000
        };

        Assert.True(rule.Matches(process));
    }

    [Fact]
    public void SaveAndLoad_RoundTripsRules()
    {
        var monitor = new TrafficMonitor();
        var tracker = new BandwidthAlertTracker(monitor);
        tracker.AddRule(new BandwidthAlertRule
        {
            Name = "Firefox",
            ProcessName = "firefox",
            ThresholdBytesPerSecond = 2048
        });

        tracker.SaveRules(_path);

        var loaded = new BandwidthAlertTracker(monitor);
        loaded.LoadRules(_path);

        var rule = Assert.Single(loaded.GetRules());
        Assert.Equal("Firefox", rule.Name);
        Assert.Equal("firefox", rule.ProcessName);
        Assert.Equal(2048, rule.ThresholdBytesPerSecond);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + ".tmp"); } catch { }
    }
}
