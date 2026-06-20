using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class QuotaTrackerTests
{
    [Fact]
    public void Update_TracksQuotaUsage()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig
            {
                LimitBytes = 1_000_000,
                Period = QuotaPeriod.Daily
            }
        });

        monitor.RecordBytes(1, "chrome", 500_000, false);

        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        var state = tracker.GetQuotaState("chrome");
        Assert.NotNull(state);
        Assert.Equal(500_000, state.UsedBytes);
        Assert.Equal(50, state.PercentUsed);
        Assert.False(state.IsExceeded);
    }

    [Fact]
    public void Update_FiresWarningAtThreshold()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig
            {
                LimitBytes = 1000,
                Period = QuotaPeriod.Daily,
                WarningPercent = 80
            }
        });

        monitor.RecordBytes(1, "chrome", 900, false);

        string? warned = null;
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.OnQuotaWarning += (name, _) => warned = name;
        tracker.Update();

        Assert.Equal("chrome", warned);
    }

    [Fact]
    public void Update_FiresExceededEvent()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "firefox",
            Quota = new QuotaConfig
            {
                LimitBytes = 100,
                Period = QuotaPeriod.Daily
            }
        });

        monitor.RecordBytes(2, "firefox", 200, false);

        string? exceeded = null;
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.OnQuotaExceeded += (name, _) => exceeded = name;
        tracker.Update();

        Assert.Equal("firefox", exceeded);
        var state = tracker.GetQuotaState("firefox");
        Assert.True(state!.IsExceeded);
    }

    [Fact]
    public void ResetPeriod_ClearsState()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig
            {
                LimitBytes = 100,
                Period = QuotaPeriod.Daily
            }
        });

        monitor.RecordBytes(1, "chrome", 200, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);

        tracker.ResetPeriod(QuotaPeriod.Daily);
        Assert.False(tracker.GetQuotaState("chrome")!.IsExceeded);
    }

    [Fact]
    public void ResetPeriod_UsageCountsOnlyBytesSinceReset()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig
            {
                LimitBytes = 1000,
                Period = QuotaPeriod.Daily
            }
        });

        monitor.RecordBytes(1, "chrome", 800, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        Assert.Equal(800, tracker.GetQuotaState("chrome")!.UsedBytes);

        tracker.ResetPeriod(QuotaPeriod.Daily);
        tracker.Update();
        Assert.Equal(0, tracker.GetQuotaState("chrome")!.UsedBytes);

        monitor.RecordBytes(1, "chrome", 200, false);
        tracker.Update();
        Assert.Equal(200, tracker.GetQuotaState("chrome")!.UsedBytes);
    }

    [Fact]
    public void GetAllQuotaStates_ReturnsAll()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();

        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000 }
        });
        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "firefox",
            Quota = new QuotaConfig { LimitBytes = 2000 }
        });

        monitor.RecordBytes(1, "chrome", 100, false);
        monitor.RecordBytes(2, "firefox", 200, false);

        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        Assert.Equal(2, tracker.GetAllQuotaStates().Count);
    }
}
