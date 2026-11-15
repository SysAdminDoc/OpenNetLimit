using OpenNetLimit.Core.Interfaces;
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
    public void RaisingTheLimitOfAUsedUpQuota_GivesTheTrafficBack()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        int exceeded = 0;
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.OnQuotaExceeded += (_, _) => exceeded++;
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);

        rule.Quota!.LimitBytes = 5000;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        Assert.False(tracker.GetQuotaState("chrome")!.IsExceeded);

        monitor.RecordBytes(1, "chrome", 4000, false);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);
        Assert.Equal(2, exceeded);
    }

    /// <summary>Lets a test take an app away, the way pruning an exited app will.</summary>
    private sealed class ListedProcesses : ITrafficMonitor
    {
        public List<ProcessTrafficInfo> Processes { get; } = [];

        public ProcessTrafficInfo Add(string name, long bytes, string? path = null)
        {
            var info = new ProcessTrafficInfo { ProcessId = (uint)Processes.Count + 1, ProcessName = name, ProcessPath = path };
            info.AddBytesReceived(bytes);
            Processes.Add(info);
            return info;
        }

        /// <summary>Runs once, inside the next <see cref="GetAllProcesses"/> call.</summary>
        public Action? OnNextList;

        public void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null) =>
            throw new NotSupportedException();
        public ProcessTrafficInfo? GetProcessInfo(uint processId) => null;
        public IReadOnlyList<ProcessTrafficInfo> GetAllProcesses()
        {
            Interlocked.Exchange(ref OnNextList, null)?.Invoke();
            return Processes.ToList();
        }
        public TrafficSnapshot TakeSnapshot() => new();
        public TrafficSnapshot TakeUsage() => new();
        public event Action<TrafficSnapshot>? OnSnapshot { add { } remove { } }
    }

    [Fact]
    public void UsedUpQuota_StaysUsedUpWhenUsageDropsWithTheLimitUnchanged()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        });
        var exited = monitor.Add("chrome", 1500);
        monitor.Add("chrome", 100);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);

        monitor.Processes.Remove(exited);
        tracker.Update();

        var state = tracker.GetQuotaState("chrome")!;
        Assert.Equal(100, state.UsedBytes);
        Assert.True(state.IsExceeded);
    }

    [Fact]
    public void RuleEditedToAnotherApp_StartsItsQuotaOver()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        monitor.RecordBytes(2, "msedge", 200, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        rule.ProcessName = "msedge";
        ruleEngine.UpdateRule(rule);
        tracker.Update();

        var state = Assert.Single(tracker.GetAllQuotaStates());
        Assert.Equal("msedge", state.ProcessName);
        Assert.Equal(0, state.UsedBytes);
        Assert.False(state.IsExceeded);

        monitor.RecordBytes(2, "msedge", 300, false);
        tracker.Update();
        Assert.Equal(300, tracker.GetQuotaState(rule.Id)!.UsedBytes);
    }

    [Fact]
    public void RuleNarrowedToAFolderAfterAReset_DoesNotCountTrafficFromBeforeTheReset()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, Period = QuotaPeriod.Daily, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false, @"C:\Chrome\chrome.exe");
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        tracker.ResetPeriod(QuotaPeriod.Daily);
        monitor.RecordBytes(1, "chrome", 100, false, @"C:\Chrome\chrome.exe");
        tracker.Update();

        rule.ProcessPath = @"C:\Chrome\*";
        ruleEngine.UpdateRule(rule);
        tracker.Update();

        var state = tracker.GetQuotaState(rule.Id)!;
        Assert.Equal(0, state.UsedBytes);
        Assert.False(state.IsExceeded);
    }

    [Fact]
    public void QuotaAddedWhileRunning_CountsFromWhenItWasAdded()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        monitor.RecordBytes(1, "chrome", 5000, false);

        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        tracker.Update();
        Assert.Equal(0, tracker.GetQuotaState(rule.Id)!.UsedBytes);

        monitor.RecordBytes(1, "chrome", 300, false);
        tracker.Update();
        var state = tracker.GetQuotaState(rule.Id)!;
        Assert.Equal(300, state.UsedBytes);
        Assert.False(state.IsExceeded);
    }

    [Fact]
    public async Task TimerUpdateRunningDuringARuleEdit_DoesNotPutTheOldAppBack()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.Add("chrome", 1500);
        monitor.Add("msedge", 200);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        int exceeded = 0;
        tracker.OnQuotaExceeded += (_, _) => Interlocked.Increment(ref exceeded);

        // The timer's update has read the rules and stalls before it applies them.
        var timerHasReadTheRules = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var letTheTimerFinish = new ManualResetEventSlim();
        monitor.OnNextList = () =>
        {
            timerHasReadTheRules.SetResult();
            letTheTimerFinish.Wait(TimeSpan.FromSeconds(10));
        };
        var timer = Task.Run(tracker.Update);
        await timerHasReadTheRules.Task.WaitAsync(TimeSpan.FromSeconds(5));

        rule.ProcessName = "msedge";
        ruleEngine.UpdateRule(rule);
        var edit = Task.Run(tracker.Update);
        // Only an edit that didn't wait for the timer's update gets done in this time.
        await Task.WhenAny(edit, Task.Delay(200));
        bool editRanDuringTheTimerUpdate = edit.IsCompleted;
        letTheTimerFinish.Set();
        await Task.WhenAll(timer, edit).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(editRanDuringTheTimerUpdate);
        var state = Assert.Single(tracker.GetAllQuotaStates());
        Assert.Equal("msedge", state.ProcessName);
        Assert.False(state.IsExceeded);
        Assert.Equal(1, exceeded);
    }

    [Fact]
    public void AlertHandlerThatThrows_DoesNotStopTheOtherAlerts()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        ruleEngine.AddRule(new BandwidthRule { ProcessName = "chrome", Quota = new QuotaConfig { LimitBytes = 1000 } });
        ruleEngine.AddRule(new BandwidthRule { ProcessName = "msedge", Quota = new QuotaConfig { LimitBytes = 1000 } });
        monitor.Add("chrome", 1500);
        monitor.Add("msedge", 900);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        var exceeded = new List<string>();
        var warned = new List<string>();
        tracker.OnQuotaExceeded += (_, _) => throw new InvalidOperationException("plugin down");
        tracker.OnQuotaExceeded += (name, _) => exceeded.Add(name);
        tracker.OnQuotaWarning += (name, _) => warned.Add(name);

        tracker.Update();

        Assert.Equal(["chrome"], exceeded);
        Assert.Equal(["msedge"], warned);
    }

    [Fact]
    public void Alert_CarriesTheUsageItFiredWith()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        ruleEngine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, Period = QuotaPeriod.Daily }
        });
        monitor.Add("chrome", 1500);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        QuotaState? alerted = null;
        tracker.OnQuotaExceeded += (_, state) => alerted = state;
        tracker.Update();

        tracker.ResetPeriod(QuotaPeriod.Daily);

        Assert.NotNull(alerted);
        Assert.Equal(1500, alerted.UsedBytes);
        Assert.True(alerted.IsExceeded);
    }

    [Fact]
    public void RaisedLimit_WarnsAgainWhenUsageNearsTheNewOne()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule { ProcessName = "chrome", Quota = new QuotaConfig { LimitBytes = 1000, WarningPercent = 80 } };
        ruleEngine.AddRule(rule);
        var chrome = monitor.Add("chrome", 850);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        int warnings = 0;
        tracker.OnQuotaWarning += (_, _) => warnings++;
        tracker.Update();

        rule.Quota!.LimitBytes = 2000;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        chrome.AddBytesReceived(900);
        tracker.Update();

        Assert.Equal(2, warnings);
    }

    [Fact]
    public void LoweredLimit_KeepsAUsedUpQuotaUsedUpAfterUsageDropped()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        var counted = monitor.Add("chrome", 1100);
        monitor.Add("chrome", 800);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);

        monitor.Processes.Remove(counted);
        rule.Quota!.LimitBytes = 900;
        ruleEngine.UpdateRule(rule);
        tracker.Update();

        var state = tracker.GetQuotaState("chrome")!;
        Assert.Equal(800, state.UsedBytes);
        Assert.True(state.IsExceeded);
    }

    [Fact]
    public void LimitLoweredThenRaised_StaysUsedUpUntilAboveTheLimitItRanOutAt()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        var counted = monitor.Add("chrome", 1100);
        monitor.Add("chrome", 600);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);
        monitor.Processes.Remove(counted);

        rule.Quota!.LimitBytes = 500;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        rule.Quota.LimitBytes = 700;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        bool usedUpAt700 = tracker.GetQuotaState("chrome")!.IsExceeded;
        rule.Quota.LimitBytes = 1200;
        ruleEngine.UpdateRule(rule);
        tracker.Update();

        Assert.True(usedUpAt700);
        Assert.False(tracker.GetQuotaState("chrome")!.IsExceeded);
    }

    [Fact]
    public void UsedUpQuotaThatWarnedFirst_WarnsAgainAfterTheLimitIsRaised()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule { ProcessName = "chrome", Quota = new QuotaConfig { LimitBytes = 1000, WarningPercent = 80 } };
        ruleEngine.AddRule(rule);
        var chrome = monitor.Add("chrome", 850);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        int warnings = 0;
        tracker.OnQuotaWarning += (_, _) => warnings++;
        tracker.Update();
        chrome.AddBytesReceived(250);
        tracker.Update();
        Assert.True(tracker.GetQuotaState("chrome")!.IsExceeded);

        rule.Quota!.LimitBytes = 2000;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        chrome.AddBytesReceived(600);
        tracker.Update();

        Assert.Equal(2, warnings);
    }

    [Fact]
    public void RaisedWarningLevel_WarnsAgainAtTheNewLevel()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var rule = new BandwidthRule { ProcessName = "chrome", Quota = new QuotaConfig { LimitBytes = 1000, WarningPercent = 50 } };
        ruleEngine.AddRule(rule);
        var chrome = monitor.Add("chrome", 600);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        int warnings = 0;
        tracker.OnQuotaWarning += (_, _) => warnings++;
        tracker.Update();

        rule.Quota!.WarningPercent = 90;
        ruleEngine.UpdateRule(rule);
        tracker.Update();
        Assert.Equal(1, warnings);
        chrome.AddBytesReceived(350);
        tracker.Update();

        Assert.Equal(2, warnings);
    }

    [Fact]
    public void TwoQuotasOnOneAppName_KeepTheirOwnLimits()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new ListedProcesses();
        var small = new BandwidthRule
        {
            ProcessName = "chrome",
            ProcessPath = @"C:\A\*",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        var large = new BandwidthRule
        {
            ProcessName = "chrome",
            ProcessPath = @"C:\B\*",
            Quota = new QuotaConfig { LimitBytes = 5000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(small);
        ruleEngine.AddRule(large);
        monitor.Add("chrome", 2000, @"C:\A\chrome.exe");
        monitor.Add("chrome", 2000, @"C:\B\chrome.exe");
        int exceeded = 0;
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.OnQuotaExceeded += (_, _) => exceeded++;

        for (int i = 0; i < 4; i++)
            tracker.Update();

        Assert.Equal(1, exceeded);
        Assert.True(tracker.GetQuotaState(small.Id)!.IsExceeded);
        Assert.False(tracker.GetQuotaState(large.Id)!.IsExceeded);
    }

    [Fact]
    public void QuotaOnAFolderRuleWithoutAName_IsTrackedAndResets()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessPath = @"C:\Games\Steam\*",
            Quota = new QuotaConfig { LimitBytes = 1000, Period = QuotaPeriod.Daily }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "steam", 5000, false, @"C:\Games\Steam\steam.exe");
        var tracker = new QuotaTracker(ruleEngine, monitor);

        tracker.Update();
        Assert.True(tracker.GetQuotaState(rule.Id)!.IsExceeded);

        tracker.ResetPeriod(QuotaPeriod.Daily);
        tracker.Update();
        var state = tracker.GetQuotaState(rule.Id)!;
        Assert.False(state.IsExceeded);
        Assert.Equal(0, state.UsedBytes);
    }

    [Fact]
    public void RemovedQuotaRule_IsForgotten()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        ruleEngine.RemoveRule(rule.Id);
        tracker.Update();

        Assert.Empty(tracker.GetAllQuotaStates());
    }

    [Fact]
    public void UsedUpRule_RemovedAndPutBack_StaysUsedUp()
    {
        // Delete then Undo on the Rules tab started the cap over at zero.
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        var exceeded = 0;
        tracker.OnQuotaExceeded += (_, _) => exceeded++;
        tracker.Update();

        ruleEngine.RemoveRule(rule.Id);
        tracker.Update();
        monitor.RecordBytes(1, "chrome", 100, false);
        ruleEngine.AddRule(rule.Clone());
        tracker.Update();

        var state = tracker.GetQuotaState(rule.Id);
        Assert.NotNull(state);
        Assert.Equal(1600, state.UsedBytes);
        Assert.True(state.IsExceeded);
        Assert.Equal(1, exceeded);
    }

    [Fact]
    public void RemovedRule_PutBackAfterItsPeriodReset_CountsFromThen()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, Period = QuotaPeriod.Daily, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        ruleEngine.RemoveRule(rule.Id);
        tracker.Update();
        tracker.ResetPeriod(QuotaPeriod.Daily);
        ruleEngine.AddRule(rule.Clone());
        tracker.Update();

        var state = tracker.GetQuotaState(rule.Id);
        Assert.NotNull(state);
        Assert.Equal(0, state.UsedBytes);
        Assert.False(state.IsExceeded);
    }

    [Fact]
    public void RuleRemovedRightBeforeAReset_PutBackLater_DoesNotChargeTheAppsWholeHistory()
    {
        var ruleEngine = new RuleEngine();
        var monitor = new TrafficMonitor();
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, Period = QuotaPeriod.Daily, OnExceeded = QuotaAction.Block }
        };
        ruleEngine.AddRule(rule);
        monitor.RecordBytes(1, "chrome", 1500, false);
        var tracker = new QuotaTracker(ruleEngine, monitor);
        tracker.Update();

        ruleEngine.RemoveRule(rule.Id);
        tracker.ResetPeriod(QuotaPeriod.Daily);
        tracker.Update();
        ruleEngine.AddRule(rule.Clone());
        tracker.Update();

        Assert.Equal(0, tracker.GetQuotaState(rule.Id)!.UsedBytes);
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
