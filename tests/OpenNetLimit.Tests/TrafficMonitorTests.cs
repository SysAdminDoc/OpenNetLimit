using System.Diagnostics;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
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

    private const string SvchostPath = @"C:\Windows\System32\svchost.exe";
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private sealed class FakeProcess : IHeldProcess
    {
        public bool HasExited { get; set; }
        public bool LetGo { get; private set; }
        public void Dispose() => LetGo = true;
    }

    /// <summary>What <see cref="HoldProcess"/> hands out for each ID; an ID with nothing is gone.</summary>
    private readonly Dictionary<uint, IHeldProcess> _running = new();
    private DateTime _now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private TrafficMonitor Held(IServiceHosts? hosts = null) =>
        new(TimeSpan.FromSeconds(1), null, sampleOnTimer: false, hosts,
            (processId, _) => _running.GetValueOrDefault(processId), () => _now);

    [Fact]
    public void AProcessThatExits_LeavesTheList_AndTheNextOneWithItsIdStartsItsOwn()
    {
        var hosts = new FakeServiceHosts();
        hosts.Set(200, "DoSvc");
        var svchost = new FakeProcess();
        _running[200] = svchost;
        using var monitor = Held(hosts);
        monitor.RecordBytes(200, "svchost", 100, isUpload: false, SvchostPath);
        monitor.SampleRates();
        Assert.False(svchost.LetGo);

        svchost.HasExited = true;
        hosts.Remove(200);
        monitor.SampleRates();
        Assert.True(svchost.LetGo);
        Assert.Empty(monitor.TakeSnapshot().Processes);
        Assert.Null(monitor.GetProcessInfo(200));

        // Windows gives the ID to chrome only once the svchost is let go.
        _running[200] = new FakeProcess();
        monitor.RecordBytes(200, "chrome", 50, isUpload: false, ChromePath);
        monitor.SampleRates();

        var chrome = Assert.Single(monitor.TakeSnapshot().Processes);
        Assert.Equal(("chrome", ChromePath, 50L), (chrome.ProcessName, chrome.ProcessPath, chrome.TotalBytesReceived));
        Assert.Null(chrome.Services);

        // A quota on DoSvc keeps the svchost's bytes and never gets chrome's.
        var rules = new RuleEngine();
        rules.AddRule(new BandwidthRule
        {
            ServiceName = "DoSvc",
            Quota = new QuotaConfig { LimitBytes = 1_000_000, Period = QuotaPeriod.Daily }
        });
        var quotas = new QuotaTracker(rules, monitor);
        quotas.Update();
        Assert.Equal(100, quotas.GetQuotaState("DoSvc")!.UsedBytes);
    }

    [Fact]
    public void AProcessGoneBeforeItCouldBeHeld_LeavesTheList_ButKeepsItsBytes()
    {
        using var monitor = Held();
        monitor.RecordBytes(300, "curl", 700, isUpload: true);
        monitor.SampleRates();

        Assert.Empty(monitor.GetAllProcesses());
        var curl = Assert.Single(monitor.GetUsageTotals());
        Assert.Equal(700, curl.TotalBytesSent);
    }

    [Fact]
    public void AProcessThatCantBeHeld_StaysListed()
    {
        _running[4] = HeldProcess.Unheld;
        using var monitor = Held();
        monitor.RecordBytes(4, "System", 100, isUpload: false);
        monitor.SampleRates();
        monitor.SampleRates();

        Assert.Equal(100, Assert.Single(monitor.GetAllProcesses()).TotalBytesReceived);
    }

    [Fact]
    public void ExitedProcesses_GiveTheHistoryTheirLastBytes_ThenJoinTheirProgramsTotal()
    {
        var first = new FakeProcess();
        var second = new FakeProcess();
        _running[300] = first;
        _running[304] = second;
        using var monitor = Held();
        monitor.RecordBytes(300, "curl", 100, isUpload: false);
        monitor.RecordBytes(304, "curl", 200, isUpload: false);
        monitor.SampleRates();
        Assert.Equal(300, monitor.TakeUsage().TotalDownloadBytesPerSecond);

        // In flight when it exited.
        monitor.RecordBytes(300, "curl", 40, isUpload: false);
        first.HasExited = true;
        second.HasExited = true;
        monitor.SampleRates();
        Assert.Equal(40, monitor.TakeUsage().TotalDownloadBytesPerSecond);

        _now += TimeSpan.FromMinutes(10);
        monitor.SampleRates();

        var curl = Assert.Single(monitor.GetUsageTotals());
        Assert.Equal(("curl", 340L), (curl.ProcessName, curl.TotalBytesReceived));
        // Already in the history, so not again.
        Assert.Empty(monitor.TakeUsage().Processes);
    }

    [Fact]
    public void HeldProcess_HoldsARealProcessUntilItExits_AndRefusesOneNewerThanFirstSeen()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        uint processId = (uint)child.Id;

        // Seen before it started, so whatever was seen then was another process.
        Assert.Null(HeldProcess.Hold(processId, child.StartTime.ToUniversalTime().AddSeconds(-5)));

        using var held = HeldProcess.Hold(processId, DateTime.UtcNow);
        Assert.NotNull(held);
        Assert.NotSame(HeldProcess.Unheld, held);
        Assert.False(held.HasExited);

        child.Kill();
        child.WaitForExit();
        Assert.True(held.HasExited);
    }
}
