using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
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
        public bool TryConsume(uint processId, int byteCount, bool isUpload) => true;
        public TimeSpan GetDelay(uint processId, int byteCount, bool isUpload) => TimeSpan.Zero;
        public bool TryReserve(uint processId, int byteCount, bool isUpload, TimeSpan maxDelay, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            return true;
        }
        public bool TryGetLimit(uint processId, out long downloadBytesPerSecond, out long uploadBytesPerSecond)
        {
            bool limited = Limits.TryGetValue(processId, out var limit);
            (downloadBytesPerSecond, uploadBytesPerSecond) = limit;
            return limited;
        }
        public void SetLimit(uint processId, long downloadBytesPerSecond, long uploadBytesPerSecond) =>
            Limits[processId] = (downloadBytesPerSecond, uploadBytesPerSecond);
        public void RemoveLimit(uint processId) => Limits.Remove(processId);
        public void RemoveAll() => Limits.Clear();
        public bool HasLimit(uint processId) => Limits.ContainsKey(processId);
    }

    /// <summary>The engine's wiring: reconcile whenever the rules change.</summary>
    private static RuleReconciler Wire(RuleEngine rules, IRateLimiter limiter, FlowTracker flows)
    {
        var reconciler = new RuleReconciler(rules, limiter, flows);
        rules.OnRulesChanged += reconciler.Reconcile;
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
