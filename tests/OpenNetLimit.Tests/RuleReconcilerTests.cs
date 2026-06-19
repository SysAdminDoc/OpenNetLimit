using System.Net;
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
