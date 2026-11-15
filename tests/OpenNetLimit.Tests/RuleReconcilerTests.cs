using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using Xunit;

namespace OpenNetLimit.Tests;

public class RuleReconcilerTests
{
    private static FlowKey MakeFlowKey(ushort localPort = 12345) =>
        new(TransportProtocol.Tcp,
            IPAddress.Parse("192.168.1.100"), localPort,
            IPAddress.Parse("93.184.216.34"), 80);

    [Fact]
    public void Reconcile_AppliesLimitForMatchingRule()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(), 100, "chrome", @"C:\chrome.exe");
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 50_000,
            UploadBytesPerSecond = 25_000
        });

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();

        Assert.True(limiter.HasLimit(100));
    }

    [Theory]
    [InlineData("curl.exe")]
    [InlineData("CURL.EXE")]
    [InlineData(" curl.exe ")]
    public void RuleNamedTheWayExplorerShowsIt_LimitsTheApp(string typed)
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var reconciler = new RuleReconciler(rules, limiter, new FlowTracker());
        rules.AddRule(new BandwidthRule { ProcessName = typed, DownloadBytesPerSecond = 262_144 });

        reconciler.EnsureLimit(100, "curl", @"C:\Windows\System32\curl.exe");

        Assert.True(limiter.HasLimit(100));
        Assert.Equal("curl", Assert.Single(rules.GetAllRules()).ProcessName, ignoreCase: true);
    }

    [Fact]
    public void Reconcile_RemovesLimitWhenRuleDeleted()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(), 100, "chrome", null);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 50_000
        };
        rules.AddRule(rule);

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();
        Assert.True(limiter.HasLimit(100));

        rules.RemoveRule(rule.Id);
        reconciler.Reconcile();
        Assert.False(limiter.HasLimit(100));
    }

    [Fact]
    public void Reconcile_IgnoresDisabledRule()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(), 100, "chrome", null);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 50_000,
            Enabled = false
        });

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();

        Assert.False(limiter.HasLimit(100));
    }

    [Fact]
    public void Reconcile_RespectsDirection_DownloadOnly()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(), 100, "chrome", null);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            Direction = RuleDirection.Download,
            DownloadBytesPerSecond = 50_000,
            UploadBytesPerSecond = 25_000
        });

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();

        Assert.True(limiter.HasLimit(100));
        Assert.Equal(TimeSpan.Zero, limiter.GetDelay(100, 1000, true));
    }

    [Fact]
    public void Reconcile_HigherPriorityRuleWins()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(), 100, "chrome", null);

        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000,
            Priority = 1
        });
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 10_000,
            Priority = 10
        });

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();

        Assert.True(limiter.HasLimit(100));
    }

    [Fact]
    public void Reconcile_DoesNotAffectUnmatchedProcesses()
    {
        var rules = new RuleEngine();
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();

        flows.RegisterFlow(MakeFlowKey(100), 1, "chrome", null);
        flows.RegisterFlow(MakeFlowKey(101), 2, "firefox", null);

        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 50_000
        });

        var reconciler = new RuleReconciler(rules, limiter, flows);
        reconciler.Reconcile();

        Assert.True(limiter.HasLimit(1));
        Assert.False(limiter.HasLimit(2));
    }

    /// <summary>Keeps the exact rates set, so a test can tell one limit from another.</summary>
    private sealed class RecordingLimiter : IRateLimiter
    {
        public Dictionary<uint, (long Download, long Upload)> Limits { get; } = new();
        public HashSet<uint> Blocked { get; } = new();
        public bool TryConsume(uint processId, int byteCount, bool isUpload) => true;
        public TimeSpan GetDelay(uint processId, int byteCount, bool isUpload) => TimeSpan.Zero;
        public bool TryReserve(uint processId, int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            return true;
        }
        public bool TryReserve(uint processId, int byteCount, bool isUpload, bool wholePc, TimeSpan maxDelay, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            return true;
        }
        public WholePcLimit? PcLimit { get; private set; }
        public void SetPcLimit(WholePcLimit? limit) => PcLimit = limit;
        public bool TryGetLimit(uint processId, out long downloadBytesPerSecond, out long uploadBytesPerSecond)
        {
            bool limited = Limits.TryGetValue(processId, out var limit);
            (downloadBytesPerSecond, uploadBytesPerSecond) = limit;
            return limited;
        }
        public void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond)
        {
            Blocked.Remove(processId);
            Limits[processId] = (downloadBytesPerSecond, uploadBytesPerSecond);
        }
        public void Block(uint processId)
        {
            Limits.Remove(processId);
            Blocked.Add(processId);
        }
        public bool IsBlocked(uint processId) => Blocked.Contains(processId);
        public void RemoveLimit(uint processId)
        {
            Limits.Remove(processId);
            Blocked.Remove(processId);
        }
        public void RemoveAll()
        {
            Limits.Clear();
            Blocked.Clear();
        }
        public bool HasLimit(uint processId) => Limits.ContainsKey(processId) || Blocked.Contains(processId);
    }

    /// <summary>The engine's wiring: reconcile whenever the rules change.</summary>
    private static RuleReconciler Wire(RuleEngine rules, IRateLimiter limiter, FlowTracker flows,
        QuotaTracker? quotas = null)
    {
        var reconciler = new RuleReconciler(rules, limiter, flows, quotas);
        rules.OnRulesChanged += reconciler.ReconcileAfterRuleChange;
        return reconciler;
    }

    [Fact]
    public void AppStartedAfterTheRule_IsLimitedOnItsFirstFlow()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 });
        Assert.Empty(limiter.Limits);

        flows.RegisterFlow(MakeFlowKey(), 500, "curl", @"C:\Windows\System32\curl.exe");

        Assert.Equal((262_144L, 0L), limiter.Limits[500]);
    }

    [Fact]
    public void RestartedApp_IsLimitedUnderItsNewProcessId()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        flows.RegisterFlow(MakeFlowKey(100), 500, "curl", null);
        rules.AddRule(new BandwidthRule { ProcessName = "curl", UploadBytesPerSecond = 65_536, Direction = RuleDirection.Upload });
        flows.UnregisterFlow(MakeFlowKey(100));

        flows.RegisterFlow(MakeFlowKey(101), 640, "curl", null);

        Assert.Equal((0L, 65_536L), limiter.Limits[500]);
        Assert.Equal((0L, 65_536L), limiter.Limits[640]);
    }

    [Fact]
    public void NewFlow_LeavesAQuotaThrottleInPlace()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 });
        limiter.SetLimit(500, 1_000, 1_000);

        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);

        Assert.Equal((1_000L, 1_000L), limiter.Limits[500]);
    }

    [Fact]
    public void NewFlow_GetsNoLimitWithoutAnActiveLimitRule()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        rules.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 50_000 });
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 50_000, Action = RuleAction.Allow });
        rules.AddRule(new BandwidthRule { ProcessName = "git", DownloadBytesPerSecond = 50_000, Enabled = false });

        flows.RegisterFlow(MakeFlowKey(100), 1, "firefox", null);
        flows.RegisterFlow(MakeFlowKey(101), 2, "curl", null);
        flows.RegisterFlow(MakeFlowKey(102), 3, "git", null);

        Assert.Empty(limiter.Limits);
    }

    [Fact]
    public void RemovedRule_LiftsTheLimitFromAnAppWithNoOpenConnection()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        var rule = new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        flows.UnregisterFlow(MakeFlowKey());

        rules.RemoveRule(rule.Id);

        Assert.Empty(limiter.Limits);
    }

    [Fact]
    public void ChangedRule_ReachesAnAppWithNoOpenConnection()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        var rule = new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(100), 500, "curl", null);
        flows.UnregisterFlow(MakeFlowKey(100));

        rule.DownloadBytesPerSecond = 131_072;
        rules.UpdateRule(rule);
        flows.RegisterFlow(MakeFlowKey(101), 500, "curl", null);

        Assert.Equal((131_072L, 0L), limiter.Limits[500]);
    }

    [Fact]
    public void ReusedProcessId_DoesNotKeepTheLimitOfTheAppThatHadIt()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 });
        rules.AddRule(new BandwidthRule { ProcessName = "git", UploadBytesPerSecond = 65_536, Direction = RuleDirection.Upload });
        flows.RegisterFlow(MakeFlowKey(100), 500, "curl", null);
        flows.UnregisterFlow(MakeFlowKey(100));

        flows.RegisterFlow(MakeFlowKey(101), 500, "brave", null);
        Assert.False(limiter.Limits.ContainsKey(500));

        flows.UnregisterFlow(MakeFlowKey(101));
        flows.RegisterFlow(MakeFlowKey(102), 500, "git", null);
        Assert.Equal((0L, 65_536L), limiter.Limits[500]);
    }

    [Fact]
    public void QuotaThrottle_OnALimitedApp_SurvivesItsNextFlow()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows);
        rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 });
        flows.RegisterFlow(MakeFlowKey(100), 500, "CURL", @"C:\Windows\System32\curl.exe");
        limiter.SetLimit(500, 1_000, 1_000);

        flows.RegisterFlow(MakeFlowKey(101), 500, "curl", null);

        Assert.Equal((1_000L, 1_000L), limiter.Limits[500]);
    }

    private const string SteamFolder = @"C:\Games\Steam\";

    [Fact]
    public void UsedUpBlockQuota_StopsTrafficForEveryMatchingProcess()
    {
        // The old handler set a 0/0 limit, which means no limit, and matched only processes
        // named exactly like the rule, so steamwebhelper under a folder rule was never touched.
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "steam",
            ProcessPath = SteamFolder + "*",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        });
        flows.RegisterFlow(MakeFlowKey(100), 1, "steam", SteamFolder + "steam.exe");
        flows.RegisterFlow(MakeFlowKey(101), 2, "steamwebhelper", SteamFolder + "steamwebhelper.exe");
        flows.RegisterFlow(MakeFlowKey(102), 3, "firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe");
        monitor.RecordBytes(1, "steam", 600, false, SteamFolder + "steam.exe");
        monitor.RecordBytes(2, "steamwebhelper", 600, true, SteamFolder + "steamwebhelper.exe");

        quotas.Update();
        Assert.True(reconciler.ReconcileIfChanged());
        flows.RegisterFlow(MakeFlowKey(103), 4, "steam", SteamFolder + "steam.exe");

        foreach (uint pid in new uint[] { 1, 2, 4 })
        {
            Assert.True(limiter.IsBlocked(pid), $"pid {pid}");
            Assert.False(limiter.TryReserve(pid, 100, isUpload: false, TimeSpan.FromSeconds(2), out _));
        }
        Assert.False(limiter.HasLimit(3));
    }

    [Fact]
    public void UsedUpQuota_PointedAtAnotherApp_DoesNotBlockTheNewApp()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(100), 1, "chrome", null);
        flows.RegisterFlow(MakeFlowKey(101), 2, "msedge", null);
        monitor.RecordBytes(1, "chrome", 1_500, false);
        quotas.Update();
        reconciler.ReconcileIfChanged();
        Assert.True(limiter.IsBlocked(1));

        rule.ProcessName = "msedge";
        rules.UpdateRule(rule);

        Assert.False(limiter.IsBlocked(2));
        Assert.False(limiter.IsBlocked(1));
    }

    [Fact]
    public void RaisingAUsedUpQuota_UnblocksTheAppWithTheEdit()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(100), 1, "chrome", null);
        monitor.RecordBytes(1, "chrome", 1_500, false);
        quotas.Update();
        reconciler.ReconcileIfChanged();
        Assert.True(limiter.IsBlocked(1));

        rule.Quota!.LimitBytes = 10_000;
        rules.UpdateRule(rule);

        Assert.False(limiter.IsBlocked(1));
    }

    [Fact]
    public void TwoQuotasOnOneAppName_BlockOnlyTheFolderThatUsedItsQuota()
    {
        const string folderA = @"C:\A\";
        const string folderB = @"C:\B\";
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            ProcessPath = folderA + "*",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        });
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            ProcessPath = folderB + "*",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 5_000, OnExceeded = QuotaAction.Block }
        });
        flows.RegisterFlow(MakeFlowKey(100), 1, "chrome", folderA + "chrome.exe");
        flows.RegisterFlow(MakeFlowKey(101), 2, "chrome", folderB + "chrome.exe");
        monitor.RecordBytes(1, "chrome", 2_000, false, folderA + "chrome.exe");
        monitor.RecordBytes(2, "chrome", 2_000, false, folderB + "chrome.exe");
        int exceeded = 0;
        quotas.OnQuotaExceeded += (_, _) => exceeded++;

        for (int i = 0; i < 4; i++)
        {
            quotas.Update();
            reconciler.ReconcileIfChanged();
        }

        Assert.Equal(1, exceeded);
        Assert.True(limiter.IsBlocked(1));
        Assert.False(limiter.IsBlocked(2));
    }

    [Fact]
    public void UsedUpQuotaOnAFolderRuleWithoutAName_BlocksTheFolder()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessPath = SteamFolder + "*",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        });
        flows.RegisterFlow(MakeFlowKey(100), 1, "steam", SteamFolder + "steam.exe");
        monitor.RecordBytes(1, "steam", 5_000, false, SteamFolder + "steam.exe");

        quotas.Update();
        reconciler.ReconcileIfChanged();

        Assert.True(limiter.IsBlocked(1));
    }

    [Fact]
    public void UsedUpBlockQuota_IsLiftedWhenItsPeriodResets()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "curl",
            DownloadBytesPerSecond = 262_144,
            Quota = new QuotaConfig { LimitBytes = 1_000, Period = QuotaPeriod.Daily, OnExceeded = QuotaAction.Block }
        });
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        monitor.RecordBytes(500, "curl", 2_000, false);
        quotas.Update();
        reconciler.ReconcileIfChanged();
        Assert.True(limiter.IsBlocked(500));

        quotas.ResetPeriod(QuotaPeriod.Daily);

        Assert.True(reconciler.ReconcileIfChanged());
        Assert.False(limiter.IsBlocked(500));
        Assert.Equal((262_144L, 0L), limiter.Limits[500]);
    }

    [Fact]
    public void UsedUpThrottleQuota_SurvivesAnEditToAnotherRule()
    {
        // The quota handler used to set the throttle itself, and the next reconcile, run by
        // any rule edit, put the rule's own limit back.
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "curl",
            DownloadBytesPerSecond = 262_144,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Throttle, ThrottleBytesPerSecond = 10_240 }
        });
        var other = new BandwidthRule { ProcessName = "git", DownloadBytesPerSecond = 50_000 };
        rules.AddRule(other);
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        monitor.RecordBytes(500, "curl", 2_000, false);
        quotas.Update();
        reconciler.ReconcileIfChanged();
        Assert.Equal((10_240L, 10_240L), limiter.Limits[500]);

        other.DownloadBytesPerSecond = 40_000;
        rules.UpdateRule(other);

        Assert.Equal((10_240L, 10_240L), limiter.Limits[500]);
    }

    [Fact]
    public void QuotaThrottle_NeverLoosensATighterRuleLimit()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "curl",
            DownloadBytesPerSecond = 4_096,
            Direction = RuleDirection.Download,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Throttle, ThrottleBytesPerSecond = 10_240 }
        });
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        monitor.RecordBytes(500, "curl", 2_000, false);

        quotas.Update();
        reconciler.ReconcileIfChanged();

        Assert.Equal((4_096L, 10_240L), limiter.Limits[500]);
    }

    [Fact]
    public void WarnOnlyQuota_LeavesTheLimitAlone()
    {
        var rules = new RuleEngine();
        var monitor = new TrafficMonitor();
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        rules.AddRule(new BandwidthRule
        {
            ProcessName = "curl",
            DownloadBytesPerSecond = 262_144,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.WarnOnly }
        });
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        monitor.RecordBytes(500, "curl", 2_000, false);

        quotas.Update();

        Assert.False(reconciler.ReconcileIfChanged());
        Assert.Equal((262_144L, 0L), limiter.Limits[500]);
    }

    [Fact]
    public void ScheduledRule_FollowsItsWindowWithoutAnEdit()
    {
        // Moving the window stands in for the clock: no rule event fires either way, which is
        // what happens when a schedule opens or closes on its own.
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows);
        var rule = new BandwidthRule
        {
            ProcessName = "curl",
            DownloadBytesPerSecond = 262_144,
            ActiveFrom = DateTime.UtcNow.AddHours(1)
        };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);
        Assert.Empty(limiter.Limits);
        Assert.False(reconciler.ReconcileIfChanged());

        rule.ActiveFrom = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(reconciler.ReconcileIfChanged());
        Assert.Equal((262_144L, 0L), limiter.Limits[500]);
        Assert.False(reconciler.ReconcileIfChanged());

        rule.ActiveUntil = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(reconciler.ReconcileIfChanged());
        Assert.Empty(limiter.Limits);
    }

    /// <summary>Traffic stats that fail to list apps while <see cref="Failing"/> is set.</summary>
    private sealed class FailingTrafficMonitor : ITrafficMonitor
    {
        private readonly TrafficMonitor _stats = new();
        public bool Failing { get; set; } = true;
        public void RecordBytes(uint processId, string processName, int byteCount, bool isUpload, string? processPath = null) =>
            _stats.RecordBytes(processId, processName, byteCount, isUpload, processPath);
        public ProcessTrafficInfo? GetProcessInfo(uint processId) => _stats.GetProcessInfo(processId);
        public IReadOnlyList<ProcessTrafficInfo> GetAllProcesses() =>
            Failing ? throw new InvalidOperationException("stats unavailable") : _stats.GetAllProcesses();
        public TrafficSnapshot TakeSnapshot() => new();
        public TrafficSnapshot TakeUsage() => new();
        public event Action<TrafficSnapshot>? OnSnapshot { add { } remove { } }
    }

    [Fact]
    public void UsedUpQuota_PointedAtAnotherAppWhileItsUpdateFails_DoesNotBlockTheNewApp()
    {
        var rules = new RuleEngine();
        var monitor = new FailingTrafficMonitor { Failing = false };
        var quotas = new QuotaTracker(rules, monitor);
        var limiter = new ProcessRateLimiter();
        var flows = new FlowTracker();
        var reconciler = Wire(rules, limiter, flows, quotas);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Action = RuleAction.Allow,
            Quota = new QuotaConfig { LimitBytes = 1_000, OnExceeded = QuotaAction.Block }
        };
        rules.AddRule(rule);
        flows.RegisterFlow(MakeFlowKey(100), 1, "chrome", null);
        flows.RegisterFlow(MakeFlowKey(101), 2, "msedge", null);
        monitor.RecordBytes(1, "chrome", 1_500, false);
        quotas.Update();
        reconciler.ReconcileIfChanged();
        Assert.True(limiter.IsBlocked(1));

        monitor.Failing = true;
        rule.ProcessName = "msedge";
        Assert.Throws<InvalidOperationException>(() => rules.UpdateRule(rule));

        Assert.False(limiter.IsBlocked(2));
        Assert.False(limiter.IsBlocked(1));
    }

    [Fact]
    public void QuotaUpdateThatFails_StillAppliesTheEditedRule()
    {
        var rules = new RuleEngine();
        var limiter = new RecordingLimiter();
        var flows = new FlowTracker();
        Wire(rules, limiter, flows, new QuotaTracker(rules, new FailingTrafficMonitor()));
        flows.RegisterFlow(MakeFlowKey(), 500, "curl", null);

        Assert.Throws<InvalidOperationException>(() =>
            rules.AddRule(new BandwidthRule { ProcessName = "curl", DownloadBytesPerSecond = 262_144 }));

        Assert.Equal((262_144L, 0L), limiter.Limits[500]);
    }
}

public class RuleFileVersionTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var engine = new RuleEngine();
        engine.AddRule(new BandwidthRule
        {
            ProcessName = "chrome",
            DownloadBytesPerSecond = 100_000
        });

        var path = Path.Combine(Path.GetTempPath(), $"onl_test_{Guid.NewGuid()}.json");
        try
        {
            engine.SaveRules(path);

            var engine2 = new RuleEngine();
            engine2.LoadRules(path);

            var rules = engine2.GetAllRules();
            Assert.Single(rules);
            Assert.Equal("chrome", rules[0].ProcessName);
            Assert.Equal(100_000, rules[0].DownloadBytesPerSecond);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadRules_HandlesLegacyArrayFormat()
    {
        var json = """[{"processName":"chrome","downloadBytesPerSecond":50000}]""";
        var path = Path.Combine(Path.GetTempPath(), $"onl_test_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, json);

            var engine = new RuleEngine();
            engine.LoadRules(path);

            var rules = engine.GetAllRules();
            Assert.Single(rules);
            Assert.Equal("chrome", rules[0].ProcessName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnRulesChanged_FiresOnAddUpdateRemove()
    {
        var engine = new RuleEngine();
        int changeCount = 0;
        engine.OnRulesChanged += () => changeCount++;

        var rule = new BandwidthRule { ProcessName = "test" };
        engine.AddRule(rule);
        Assert.Equal(1, changeCount);

        rule.DownloadBytesPerSecond = 100;
        engine.UpdateRule(rule);
        Assert.Equal(2, changeCount);

        engine.RemoveRule(rule.Id);
        Assert.Equal(3, changeCount);
    }
}
