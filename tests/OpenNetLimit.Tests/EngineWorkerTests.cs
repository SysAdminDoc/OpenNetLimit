using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service;
using OpenNetLimit.Service.Control;
using OpenNetLimit.Service.IPC;
using OpenNetLimit.Service.Plugins;
using SharpDivert;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class EngineWorkerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"onl-engine-{Guid.NewGuid():N}");
    private readonly RuleEngine _rules = new();
    private readonly TrafficMonitor _monitor = new();
    private readonly BandwidthAlertTracker _alerts;
    private readonly ControlPlaneState _controlPlane = new();
    private readonly DriverPreflightTests.FakeDriverService _driver = new();
    private string[] _programsUsingWinDivert = [];

    /// <summary>Runs each time the driver unloader lists the programs that have WinDivert open.</summary>
    private Action? _whenListingPrograms;

    /// <summary>Runs on each wait while a pause watches the driver unload.</summary>
    private Action? _duringUnloadWait;

    /// <summary>Set once the worker's boot start is done and it serves the control pipe.</summary>
    private TaskCompletionSource _pipeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string RulesPath => Path.Combine(_dataDir, "rules.json");
    private string AlertsPath => Path.Combine(_dataDir, "alerts.json");
    private string StayPausedPath => Path.Combine(_dataDir, "paused");
    private string PcLimitPath => Path.Combine(_dataDir, "pc-limit.json");

    /// <summary>The limiter the newest worker was given.</summary>
    private ProcessRateLimiter _limiter = new();

    public EngineWorkerTests() => _alerts = new BandwidthAlertTracker(_monitor);

    [Fact]
    public async Task InterceptorFailure_StillServesTheControlPipeWithTheReason()
    {
        var interceptor = new FakeInterceptor(new WinDivertOpenException(
            WinDivert.Layer.Flow, WinDivert.Flag.Sniff, 87, "The parameter is incorrect."));
        using var worker = CreateWorker(interceptor, isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.False(status.Running);
        Assert.NotNull(status.EngineError);
        Assert.Contains(WinDivertOpenException.DescribeError(87), status.EngineError);
        Assert.Contains("87", status.EngineError);
    }

    [Fact]
    public async Task NotAdmin_StillServesTheControlPipeWithTheReason()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: false);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.False(interceptor.StartCalled);
        Assert.False(status.Running);
        Assert.Equal(WinDivertOpenException.DescribeError(5), status.EngineError);
    }

    [Fact]
    public async Task NotAdmin_LeavesSavedRulesAlone()
    {
        Directory.CreateDirectory(_dataDir);
        const string saved = """{"Version":1,"Rules":[{"ProcessName":"steam.exe","DownloadBytesPerSecond":500000}]}""";
        File.WriteAllText(RulesPath, saved);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: false);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(saved, File.ReadAllText(RulesPath));
    }

    [Fact]
    public async Task HealthyEngine_ReportsRunningWithNoError_AndSavesRulesOnStop()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        _rules.AddRule(new BandwidthRule { ProcessName = "steam.exe", DownloadBytesPerSecond = 500_000 });
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(status.Running);
        Assert.Null(status.EngineError);
        Assert.Contains("\"steam\"", File.ReadAllText(RulesPath));
    }

    // A crash or power cut used to lose every change made since the service started.
    [Fact]
    public async Task RuleAdded_IsSavedRightAway()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);

        _rules.AddRule(new BandwidthRule { ProcessName = "steam.exe", DownloadBytesPerSecond = 500_000 });
        var saved = File.ReadAllText(RulesPath);
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains("\"steam\"", saved);
    }

    [Fact]
    public async Task AlertAdded_IsSavedRightAway()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);

        _alerts.AddRule(new BandwidthAlertRule { Name = "Big download", ThresholdBytesPerSecond = 1024 });
        var saved = File.ReadAllText(AlertsPath);
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains("Big download", saved);
    }

    [Fact]
    public async Task UnreadableRulesFile_IsKeptBeforeAChangeSavesOverIt()
    {
        Directory.CreateDirectory(_dataDir);
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);

        _rules.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1024 });
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(broken, File.ReadAllText(RulesPath + ".unreadable"));
        Assert.Contains("\"chrome\"", File.ReadAllText(RulesPath));
    }

    [Fact]
    public async Task UnreadableFiles_WithNoChange_AreLeftAsTheyWereAtStop()
    {
        Directory.CreateDirectory(_dataDir);
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath, broken);
        File.WriteAllText(AlertsPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(broken, File.ReadAllText(RulesPath));
        Assert.Equal(broken, File.ReadAllText(AlertsPath));
    }

    [Fact]
    public async Task UnreadableRulesFile_ThatCantBeCopied_IsNotSavedOverUntilACopyIsKept()
    {
        Directory.CreateDirectory(_dataDir);
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        worker.CopyFile = (_, _) => throw new IOException("The process cannot access the file.");
        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);

        _rules.AddRule(new BandwidthRule { ProcessName = "chrome", DownloadBytesPerSecond = 1024 });
        var afterRefusedCopy = File.ReadAllText(RulesPath);
        worker.CopyFile = File.Copy;
        _rules.AddRule(new BandwidthRule { ProcessName = "steam", DownloadBytesPerSecond = 1024 });
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(broken, afterRefusedCopy);
        Assert.Equal(broken, File.ReadAllText(RulesPath + ".unreadable"));
        Assert.Contains("\"chrome\"", File.ReadAllText(RulesPath));
    }

    [Fact]
    public async Task AnEarlierUnreadableCopy_IsNeverWrittenOver()
    {
        Directory.CreateDirectory(_dataDir);
        const string kept = """{"version":1,"rules":[{"processName":"discord"}""";
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath + ".unreadable", kept);
        File.WriteAllText(RulesPath, broken);

        // Twice, like two boots with the same bad file: it's kept once.
        for (var start = 0; start < 2; start++)
        {
            _pipeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
            await worker.StartAsync(CancellationToken.None);
            await _pipeStarted.Task.WaitAsync(Timeout);
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(kept, File.ReadAllText(RulesPath + ".unreadable"));
        Assert.Equal(broken, File.ReadAllText(RulesPath + ".unreadable-2"));
        Assert.False(File.Exists(RulesPath + ".unreadable-3"));
    }

    [Fact]
    public async Task AKeptCopy_PastAGapInTheNumbers_IsntCopiedAgain()
    {
        // The search for an earlier copy stopped at the first missing number, so a copy deleted
        // by hand had the same bad file copied again on every start.
        Directory.CreateDirectory(_dataDir);
        const string kept = """{"version":1,"rules":[{"processName":"discord"}""";
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath + ".unreadable", kept);
        File.WriteAllText(RulesPath + ".unreadable-3", broken);
        File.WriteAllText(RulesPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(RulesPath + ".unreadable-2"));
        Assert.Equal(kept, File.ReadAllText(RulesPath + ".unreadable"));
        Assert.Null(_controlPlane.ChangesRefused);
    }

    // It took changes it couldn't save, and said so only in the log.
    [Fact]
    public async Task UnreadableRulesFile_ThatCantBeCopied_RefusesChangesAndSaysWhy()
    {
        Directory.CreateDirectory(_dataDir);
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        worker.CopyFile = (_, _) => throw new IOException("The process cannot access the file.");

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var refused = _controlPlane.ChangesRefused;
        var status = _controlPlane.GetDiagnostics();
        var reply = await CreatePipe().ProcessCommandAsync("ADD_RULE", """{"processName":"chrome"}""", CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Contains(RulesPath, refused);
        Assert.Contains("restart", refused);
        Assert.Equal(refused, status.ChangesRefused);
        Assert.Contains("\"error\"", reply);
        Assert.Empty(_rules.GetAllRules());
        Assert.Equal(broken, File.ReadAllText(RulesPath));
    }

    [Fact]
    public async Task UnreadableRulesAndAlerts_ThatCantBeCopied_NameBothFiles()
    {
        Directory.CreateDirectory(_dataDir);
        const string broken = """{"version":1,"rules":[{"processName":"steam.exe",""";
        File.WriteAllText(RulesPath, broken);
        File.WriteAllText(AlertsPath, broken);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        worker.CopyFile = (_, _) => throw new IOException("The process cannot access the file.");

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var refused = _controlPlane.ChangesRefused;
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(RulesPath, refused);
        Assert.Contains(AlertsPath, refused);
    }

    [Fact]
    public async Task NotAdmin_RefusesRuleChangesItCouldNotKeep()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: false);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var refused = _controlPlane.ChangesRefused;
        var status = _controlPlane.GetDiagnostics();
        var reply = await CreatePipe().ProcessCommandAsync("ADD_RULE", """{"processName":"chrome"}""", CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains("administrator", refused);
        Assert.Equal(refused, status.ChangesRefused);
        Assert.Contains("administrator", reply);
        Assert.Empty(_rules.GetAllRules());
        Assert.False(File.Exists(RulesPath));
    }

    [Fact]
    public async Task Admin_TakesRuleChanges()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        var refused = _controlPlane.ChangesRefused;
        var status = _controlPlane.GetDiagnostics();
        var reply = await CreatePipe().ProcessCommandAsync("ADD_RULE", """{"processName":"chrome"}""", CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(refused);
        Assert.Null(status.ChangesRefused);
        Assert.DoesNotContain("\"error\"", reply);
        Assert.Single(_rules.GetAllRules());
    }

    [Fact]
    public async Task EngineThatStopsOnItsOwn_ReportsWhy()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
        interceptor.IsRunning = false;
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.False(status.Running);
        Assert.Contains("stopped", status.EngineError);
    }

    [Fact]
    public async Task SavedRules_AreLoadedBeforeStartAsyncReturns()
    {
        // The host starts the REST API as soon as this worker's StartAsync returns. On .NET 10
        // ExecuteAsync runs on its own thread, so a slow startup used to leave the API serving
        // an empty rule set.
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(RulesPath, """{"version":1,"rules":[{"processName":"steam.exe","downloadBytesPerSecond":500000}]}""");
        using var worker = CreateWorker(new FakeInterceptor(), () => { Thread.Sleep(200); return true; });

        await worker.StartAsync(CancellationToken.None);
        var rulesWhenStarted = _rules.GetAllRules().Count;
        await _pipeStarted.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, rulesWhenStarted);
    }

    [Fact]
    public async Task QuotaAddedBeforeTheFirstQuotaCheck_CountsFromWhenItWasAdded()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await worker.StartAsync(CancellationToken.None);
        _monitor.RecordBytes(1, "chrome", 5000, false);
        var rule = new BandwidthRule
        {
            ProcessName = "chrome",
            Quota = new QuotaConfig { LimitBytes = 1000, OnExceeded = QuotaAction.Block }
        };

        _rules.AddRule(rule);
        var state = _controlPlane.QuotaTracker!.GetQuotaState(rule.Id);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(0, state.UsedBytes);
        Assert.False(state.IsExceeded);
    }

    [Fact]
    public async Task StopWhileTheEngineIsStillStarting_IsNotAnEngineError()
    {
        var interceptor = new FakeInterceptor { StartWaitsForStop = true };
        using var worker = CreateWorker(interceptor, isAdmin: true);

        await worker.StartAsync(CancellationToken.None);
        await interceptor.Started.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);
        var status = _controlPlane.GetDiagnostics();

        Assert.Null(status.EngineError);
        Assert.False(File.Exists(Path.Combine(_dataDir, "last-error.txt")));
    }

    [Fact]
    public async Task Pause_StopsTheEngineAndUnloadsTheDriver_AndResumeStartsItAgain()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.Stopped);
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);

        var paused = await _controlPlane.PauseHandler!(false, CancellationToken.None);
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(paused.Paused);
        Assert.False(paused.Running);
        Assert.False(paused.StayPaused);
        Assert.Null(paused.EngineError);
        Assert.Null(paused.DriverLoadedReason);
        Assert.Equal(1, _driver.StopRequests);
        Assert.False(File.Exists(StayPausedPath));
        Assert.True(resumed.Running);
        Assert.False(resumed.Paused);
        Assert.Equal(2, interceptor.StartCount);
    }

    [Fact]
    public async Task Pause_DriverHeldByAnotherProgram_StaysPausedAndSaysWhyUntilItUnloads()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);

        var paused = await _controlPlane.PauseHandler!(false, CancellationToken.None);
        var stillHeld = _controlPlane.GetDiagnostics();
        _driver.Then(DriverServiceState.Stopped);
        var released = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(paused.Paused);
        Assert.Contains("wireguard.exe", paused.DriverLoadedReason);
        Assert.NotNull(stillHeld.DriverLoadedReason);
        Assert.True(released.Paused);
        Assert.Null(released.DriverLoadedReason);
        Assert.Null(released.EngineError);
    }

    [Fact]
    public async Task StayPaused_KeepsTheEngineStoppedAfterARestart_UntilResumed()
    {
        var first = new FakeInterceptor();
        using (var worker = CreateWorker(first, isAdmin: true))
        {
            await StartAndWaitForBootAsync(worker);
            var paused = await _controlPlane.PauseHandler!(true, CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
            Assert.True(paused.StayPaused);
        }
        bool keptAcrossTheRestart = File.Exists(StayPausedPath);

        var second = new FakeInterceptor();
        using var restarted = CreateWorker(second, isAdmin: true);
        await StartAndWaitForBootAsync(restarted);
        var afterRestart = _controlPlane.GetDiagnostics();
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await restarted.StopAsync(CancellationToken.None);

        Assert.True(keptAcrossTheRestart);
        Assert.True(afterRestart.Paused);
        Assert.True(afterRestart.StayPaused);
        Assert.False(afterRestart.Running);
        Assert.Null(afterRestart.EngineError);
        Assert.True(resumed.Running);
        Assert.Equal(1, second.StartCount);
        Assert.False(File.Exists(StayPausedPath));
    }

    [Fact]
    public async Task PauseNotKept_EngineStartsAgainAfterARestart()
    {
        using (var worker = CreateWorker(new FakeInterceptor(), isAdmin: true))
        {
            await StartAndWaitForBootAsync(worker);
            await _controlPlane.PauseHandler!(true, CancellationToken.None);
            // Pausing again without asking to stay paused takes the earlier choice back.
            await _controlPlane.PauseHandler!(false, CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }

        var second = new FakeInterceptor();
        using var restarted = CreateWorker(second, isAdmin: true);
        await StartAndWaitForBootAsync(restarted);
        var status = _controlPlane.GetDiagnostics();
        await restarted.StopAsync(CancellationToken.None);

        Assert.True(status.Running);
        Assert.False(status.Paused);
        Assert.Equal(1, second.StartCount);
    }

    [Fact]
    public async Task Resume_RetriesAnEngineThatFailedToStart()
    {
        var interceptor = new FakeInterceptor(new WinDivertOpenException(
            WinDivert.Layer.Flow, WinDivert.Flag.Sniff, 433, "A device which does not exist was specified."));
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        var failed = _controlPlane.GetDiagnostics();
        var statsBeforeResume = _controlPlane.StatsProvider;

        interceptor.StartFailure = null;
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        var statsAfterResume = _controlPlane.StatsProvider;
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(WinDivertOpenException.DescribeError(433), failed.EngineError);
        Assert.True(resumed.Running);
        Assert.Null(resumed.EngineError);
        Assert.Null(statsBeforeResume);
        Assert.NotNull(statsAfterResume);
    }

    [Fact]
    public async Task Resume_WhileAnotherProgramStillHoldsTheDriver_StaysPausedAndSaysWhy()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);

        await _controlPlane.PauseHandler!(true, CancellationToken.None);
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Paused);
        Assert.True(resumed.StayPaused);
        Assert.False(resumed.Running);
        Assert.Contains("wireguard.exe", resumed.EngineError);
        Assert.Contains("wireguard.exe", resumed.DriverLoadedReason);
        Assert.True(File.Exists(StayPausedPath));
        // Opening while the stop is pending only waits out the preflight, then fails with 433.
        Assert.Equal(1, interceptor.StartCount);
    }

    [Fact]
    public async Task Resume_ThatFailsToStart_StaysPausedAndKeepsStayPaused()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(true, CancellationToken.None);

        interceptor.StartFailure = new WinDivertOpenException(
            WinDivert.Layer.Flow, WinDivert.Flag.Sniff, 433, "A device which does not exist was specified.");
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Paused);
        Assert.True(resumed.StayPaused);
        Assert.False(resumed.Running);
        Assert.Contains(WinDivertOpenException.DescribeError(433), resumed.EngineError);
        Assert.True(File.Exists(StayPausedPath));
    }

    [Fact]
    public async Task RestartIntoStayPaused_WithTheDriverStillHeld_SaysWhy()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StayPausedPath, "");
        _driver.Then(DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);

        await StartAndWaitForBootAsync(worker);
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(status.Paused);
        Assert.Contains("wireguard.exe", status.DriverLoadedReason);
    }

    [Fact]
    public async Task WhilePaused_AnotherProgramLoadingTheDriver_ShowsInTheStatus()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        var paused = await _controlPlane.PauseHandler!(false, CancellationToken.None);

        _programsUsingWinDivert = ["wireguard.exe"];
        _driver.Then(DriverServiceState.Running);
        var loadedAgain = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(paused.DriverLoadedReason);
        Assert.Contains("wireguard.exe", loadedAgain.DriverLoadedReason);
    }

    [Fact]
    public async Task StatusRead_WhileAPauseIsUnloadingTheDriver_NeverSaysItIsUnloaded()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        var readsDuringUnload = new List<string?>();
        _duringUnloadWait = () => readsDuringUnload.Add(_controlPlane.GetDiagnostics().DriverLoadedReason);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);

        // The first pause has no reason yet; the second already knows one.
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotEmpty(readsDuringUnload);
        Assert.All(readsDuringUnload, Assert.NotNull);
    }

    [Fact]
    public async Task ResumeBeforeTheBootStart_StartsTheEngineOnce()
    {
        // The REST API answers before the boot start runs. A resume that gets in first used to
        // be followed by a second start and a second set of background timers.
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);

        var resumed = await ResumeBeforeTheBootStartAsync(worker);
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Running);
        Assert.True(status.Running);
        Assert.Equal(1, interceptor.StartCount);
    }

    [Fact]
    public async Task ResumeBeforeTheBootStart_WithAStayPausedMarkerThatWontGo_KeepsRunning()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StayPausedPath, "");
        using var markerInUse = new FileStream(StayPausedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Loaded, so an unload the boot start shouldn't do would show up as a stop request.
        _driver.Then(DriverServiceState.Running);
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);

        await ResumeBeforeTheBootStartAsync(worker);
        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(status.Running);
        Assert.False(status.Paused);
        Assert.True(status.StayPaused);
        Assert.Equal(1, interceptor.StartCount);
        Assert.Equal(0, _driver.StopRequests);
    }

    [Fact]
    public async Task Resume_ThatCantRemoveTheStayPausedMarker_SaysItComesUpPausedAfterARestart()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(true, CancellationToken.None);

        DiagnosticInfo resumed;
        using (new FileStream(StayPausedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Running);
        Assert.False(resumed.Paused);
        Assert.True(resumed.StayPaused);
    }

    [Fact]
    public async Task Resume_ThatFailsAfterLoadingTheDriver_UnloadsItAgain()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);

        // The flow handle opened, which loads the driver, then the network handle failed.
        interceptor.WhenStarting = () => _driver.Then(DriverServiceState.Running, DriverServiceState.Stopped);
        interceptor.StartFailure = new WinDivertOpenException(
            WinDivert.Layer.Network, WinDivert.Flag.Sniff, 87, "The parameter is incorrect.");
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Paused);
        Assert.False(resumed.Running);
        Assert.Null(resumed.DriverLoadedReason);
        Assert.Equal(1, _driver.StopRequests);
    }

    [Fact]
    public async Task RefusedResume_ItsErrorGoesOnceTheDriverUnloads()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);

        var refused = await _controlPlane.ResumeHandler!(CancellationToken.None);
        _driver.Then(DriverServiceState.Stopped);
        var released = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains("wireguard.exe", refused.EngineError);
        Assert.True(released.Paused);
        Assert.Null(released.DriverLoadedReason);
        Assert.Null(released.EngineError);
    }

    [Fact]
    public async Task StatusRead_WhileAPauseClosesOurOwnHandles_SaysUnloadingWithoutBlamingAnotherProgram()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.Running, DriverServiceState.Stopped);
        _programsUsingWinDivert = ["wireguard.exe"];
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        DiagnosticInfo? duringStop = null;
        interceptor.WhenStopping = () => duringStop ??= _controlPlane.GetDiagnostics();

        var paused = await _controlPlane.PauseHandler!(false, CancellationToken.None);
        interceptor.WhenStopping = null;
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(duringStop);
        Assert.True(duringStop.Paused);
        Assert.Equal(EngineWorker.UnloadingReason, duringStop.DriverLoadedReason);
        Assert.Null(paused.DriverLoadedReason);
    }

    [Fact]
    public async Task StatusRead_ThatAPauseOvertakes_IsNotAnEngineError()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        // The read has seen "not paused" already. The whole pause runs before it reads "not running".
        interceptor.WhenRunningRead = () =>
            Task.Run(() => _controlPlane.PauseHandler!(false, CancellationToken.None)).GetAwaiter().GetResult();

        var status = _controlPlane.GetDiagnostics();
        var after = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.False(status.Running);
        Assert.Null(status.EngineError);
        // Not paused, not running and no error is what the app shows as Failed to start.
        Assert.True(status.Paused);
        Assert.True(after.Paused);
    }

    [Fact]
    public async Task StatusRead_ThatAResumeOvertakes_KeepsSayingTheDriverIsLoading()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        using var starting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        interceptor.WhenStarting = () => { starting.Set(); release.Wait(Timeout); };
        Task<DiagnosticInfo>? resuming = null;
        // The read has decided to recheck the driver. The resume marks it as loading first, and
        // the recheck used to see it still unloaded and clear that.
        worker.WhenRecheckingDriver = () =>
        {
            resuming = Task.Run(() => _controlPlane.ResumeHandler!(CancellationToken.None));
            starting.Wait(Timeout);
        };

        _controlPlane.GetDiagnostics();
        var whileLoading = _controlPlane.GetDiagnostics();
        release.Set();
        var resumed = await resuming!.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(whileLoading.Paused);
        Assert.Equal(EngineWorker.LoadingReason, whileLoading.DriverLoadedReason);
        Assert.True(resumed.Running);
    }

    [Fact]
    public async Task FailedResume_SaysUnloadingWhileItUnloadsTheDriverAgain()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        interceptor.WhenStarting = () =>
            _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending, DriverServiceState.Stopped);
        interceptor.StartFailure = new WinDivertOpenException(
            WinDivert.Layer.Network, WinDivert.Flag.Sniff, 87, "The parameter is incorrect.");
        DiagnosticInfo? duringUnload = null;
        _duringUnloadWait = () => duringUnload ??= _controlPlane.GetDiagnostics();

        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(duringUnload);
        Assert.Equal(EngineWorker.UnloadingReason, duringUnload.DriverLoadedReason);
        Assert.True(resumed.Paused);
        Assert.Null(resumed.DriverLoadedReason);
    }

    [Fact]
    public async Task Resume_CutShortByAServiceStop_LeavesTheDriverAlone()
    {
        // An unload there would be cancelled by the same stop and only slow it down.
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StayPausedPath, "");
        var interceptor = new FakeInterceptor { StartWaitsForStop = true };
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        interceptor.WhenStarting = () => _driver.Then(DriverServiceState.Running);

        var resuming = _controlPlane.ResumeHandler!(CancellationToken.None);
        await interceptor.Started.Task.WaitAsync(Timeout);
        await worker.StopAsync(CancellationToken.None);
        var resumed = await resuming.WaitAsync(Timeout);

        Assert.True(resumed.Paused);
        Assert.Equal(0, _driver.StopRequests);
    }

    [Fact]
    public async Task RefusedResume_AsTheDriverUnloads_LeavesNoErrorBehind()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        // The other program lets go while the resume lists it, and a poll sees that before the
        // refusal is written.
        _whenListingPrograms = () =>
        {
            _whenListingPrograms = null;
            _driver.Then(DriverServiceState.Stopped);
            _controlPlane.GetDiagnostics();
        };

        var refused = await _controlPlane.ResumeHandler!(CancellationToken.None);
        var after = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(after.Paused);
        Assert.Null(after.EngineError);
        Assert.Null(after.DriverLoadedReason);
        Assert.Null(refused.EngineError);
    }

    [Fact]
    public async Task StatusRead_WhileAResumeLoadsTheDriver_NeverSaysItIsUnloaded()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        DiagnosticInfo? duringStart = null;
        interceptor.WhenStarting = () => duringStart ??= _controlPlane.GetDiagnostics();

        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(duringStart);
        Assert.True(duringStart.Paused);
        Assert.Equal(EngineWorker.LoadingReason, duringStart.DriverLoadedReason);
        Assert.Null(duringStart.EngineError);
        Assert.True(resumed.Running);
    }

    [Fact]
    public async Task Resume_AfterTheProgramHoldingTheDriverCloses_SaysLoadingNotItsName()
    {
        _driver.Then(DriverServiceState.Running, DriverServiceState.StopPending);
        _programsUsingWinDivert = ["wireguard.exe"];
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        var paused = await _controlPlane.PauseHandler!(false, CancellationToken.None);
        // The user closes wireguard, as the balloon said, and resumes before a poll sees it.
        _driver.Then(DriverServiceState.Stopped);
        _programsUsingWinDivert = [];
        DiagnosticInfo? duringStart = null;
        interceptor.WhenStarting = () => duringStart ??= _controlPlane.GetDiagnostics();

        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains("wireguard.exe", paused.DriverLoadedReason);
        Assert.NotNull(duringStart);
        Assert.Equal(EngineWorker.LoadingReason, duringStart.DriverLoadedReason);
        Assert.True(resumed.Running);
    }

    [Fact]
    public async Task StatusRead_ThatAResumeFinishesUnder_BlamesNoOtherProgram()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        // Our own start loads the driver, and no other program has it.
        interceptor.WhenStarting = () => _driver.Then(DriverServiceState.Running);
        worker.WhenRecheckingDriver = () =>
            Task.Run(() => _controlPlane.ResumeHandler!(CancellationToken.None)).GetAwaiter().GetResult();

        var racing = _controlPlane.GetDiagnostics();
        _driver.Then(DriverServiceState.StopPending, DriverServiceState.Stopped);
        DiagnosticInfo? duringNextPause = null;
        _duringUnloadWait = () => duringNextPause ??= _controlPlane.GetDiagnostics();
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(racing.DriverLoadedReason);
        Assert.NotNull(duringNextPause);
        Assert.Equal(EngineWorker.UnloadingReason, duringNextPause.DriverLoadedReason);
    }

    [Fact]
    public async Task StatusRead_ThatAWholePauseAndResumeOvertake_SaysRunning()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        // The read sees the engine stopped by a pause, then the pause and a resume both finish
        // before it looks at the pause flag.
        interceptor.WhenRunningRead = () =>
        {
            Task.Run(() => _controlPlane.PauseHandler!(false, CancellationToken.None)).GetAwaiter().GetResult();
            interceptor.AfterRunningRead = () =>
                Task.Run(() => _controlPlane.ResumeHandler!(CancellationToken.None)).GetAwaiter().GetResult();
        };

        var status = _controlPlane.GetDiagnostics();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(status.Running);
        Assert.False(status.Paused);
        Assert.Null(status.EngineError);
    }

    [Fact]
    public async Task FailedResume_LeavesAnotherProgramsDriverLoaded()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        await _controlPlane.PauseHandler!(false, CancellationToken.None);
        int stopsAfterPause = _driver.StopRequests;

        // Another program loaded the driver while we were paused, then our start failed.
        _driver.Then(DriverServiceState.Running);
        _programsUsingWinDivert = ["wireguard.exe"];
        interceptor.StartFailure = new WinDivertOpenException(
            WinDivert.Layer.Network, WinDivert.Flag.Sniff, 87, "The parameter is incorrect.");
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(resumed.Paused);
        Assert.Equal(stopsAfterPause, _driver.StopRequests);
        Assert.Contains("wireguard.exe", resumed.DriverLoadedReason);
        Assert.NotNull(resumed.EngineError);
    }

    [Fact]
    public async Task NotAdmin_PauseAndResumeChangeNothing()
    {
        var interceptor = new FakeInterceptor();
        using var worker = CreateWorker(interceptor, isAdmin: false);
        await StartAndWaitForBootAsync(worker);

        var paused = await _controlPlane.PauseHandler!(true, CancellationToken.None);
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.False(paused.Paused);
        Assert.False(interceptor.StartCalled);
        Assert.Equal(0, _driver.StopRequests);
        Assert.False(File.Exists(StayPausedPath));
        Assert.Equal(WinDivertOpenException.DescribeError(5), resumed.EngineError);
    }

    [Fact]
    public void StartFailure_MissingDll_SaysWhatIsMissing()
    {
        var message = EngineWorker.DescribeStartFailure(new DllNotFoundException("Unable to load DLL 'WinDivert.dll'"));

        Assert.Contains("WinDivert.dll", message);
    }

    [Fact]
    public void StartFailure_UnknownException_KeepsItsMessage()
    {
        var message = EngineWorker.DescribeStartFailure(new InvalidOperationException("boom"));

        Assert.Equal("boom", message);
    }

    [Theory]
    [InlineData("2026-11-15T09:59:59.900", 350)]
    [InlineData("2026-11-15T10:00:00.000", 60_250)]
    [InlineData("2026-11-15T23:59:30.000", 30_250)]
    public void ScheduleTick_LandsJustAfterTheNextMinuteStarts(string now, int expectedMs)
    {
        // Schedules open and close on the minute, so that's when the rules get rechecked.
        var wait = EngineWorker.UntilNextMinute(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), wait);
    }

    // A whole-PC limit set before a reboot still holds after it.
    [Fact]
    public async Task PcLimit_IsSavedAndAppliedAgainAfterARestart()
    {
        var limit = new WholePcLimit { DownloadBytesPerSecond = 1_048_576, IncludeLan = true };
        using (var first = CreateWorker(new FakeInterceptor(), isAdmin: true))
        {
            await StartAndWaitForBootAsync(first);
            _controlPlane.PcLimitHandler!(limit);
            await first.StopAsync(CancellationToken.None);
        }

        using var second = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(second);
        var applied = _limiter.PcLimit;
        var reported = _controlPlane.GetPcLimit();
        await second.StopAsync(CancellationToken.None);

        Assert.Equal(limit, applied);
        Assert.Equal(limit, reported);
    }

    [Fact]
    public async Task PcLimit_TurnedOff_StaysOffAfterARestart()
    {
        using (var first = CreateWorker(new FakeInterceptor(), isAdmin: true))
        {
            await StartAndWaitForBootAsync(first);
            _controlPlane.PcLimitHandler!(new WholePcLimit { UploadBytesPerSecond = 65_536 });
            _controlPlane.PcLimitHandler!(new WholePcLimit { IncludeLan = true });
            await first.StopAsync(CancellationToken.None);
        }

        using var second = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(second);
        var applied = _limiter.PcLimit;
        var reported = _controlPlane.GetPcLimit();
        await second.StopAsync(CancellationToken.None);

        Assert.Null(applied);
        Assert.Equal(new WholePcLimit { IncludeLan = true }, reported);
    }

    [Theory]
    [InlineData("""{"downloadBytesPerSecond":""")]
    [InlineData("""{"downloadBytesPerSecond":-1}""")]
    // These loaded as off and said nothing.
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"download":1048576}""")]
    public async Task PcLimitFile_ThatCantBeUsed_StartsOffAndSaysWhy(string contents)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(PcLimitPath, contents);
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);
        var applied = _limiter.PcLimit;
        var reported = _controlPlane.GetPcLimit();
        var lastError = File.ReadAllText(Path.Combine(_dataDir, "last-error.txt"));
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(applied);
        Assert.True(reported.IsOff);
        Assert.Contains("whole-PC limit", lastError);
        Assert.Equal(contents, File.ReadAllText(PcLimitPath));
    }

    [Fact]
    public async Task PcLimit_Negative_IsRefusedAndNotSaved()
    {
        using var worker = CreateWorker(new FakeInterceptor(), isAdmin: true);
        await StartAndWaitForBootAsync(worker);

        Assert.Throws<ArgumentException>(() => _controlPlane.PcLimitHandler!(new WholePcLimit { UploadBytesPerSecond = -1 }));
        var reported = _controlPlane.GetPcLimit();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(reported.IsOff);
        Assert.False(File.Exists(PcLimitPath));
    }

    private EngineWorker CreateWorker(IPacketInterceptor interceptor, bool isAdmin) =>
        CreateWorker(interceptor, () => isAdmin);

    /// <summary>A control pipe on the worker's state, like the service's.</summary>
    private PipeServer CreatePipe() => new(
        _monitor,
        _rules,
        _alerts,
        _controlPlane,
        new RestApiRouterTests.StubVerifier(),
        new RestApiRouterTests.StubGeoIpResolver(),
        new PluginManager(new PluginOptions { Enabled = false, PluginDirectory = Path.Combine(_dataDir, "plugins") },
            NullLogger<PluginManager>.Instance),
        NullLogger<PipeServer>.Instance);

    private EngineWorker CreateWorker(IPacketInterceptor interceptor, Func<bool> isAdmin)
    {
        return new EngineWorker(
            interceptor,
            _rules,
            _limiter = new ProcessRateLimiter(),
            new FlowTracker(),
            _monitor,
            RunPipe,
            _alerts,
            new PluginManager(new PluginOptions { Enabled = false, PluginDirectory = Path.Combine(_dataDir, "plugins") },
                NullLogger<PluginManager>.Instance),
            _controlPlane,
            NullLogger<EngineWorker>.Instance,
            _dataDir,
            isAdmin,
            new DriverUnloader(_driver, (_, _) => _duringUnloadWait?.Invoke(), () =>
            {
                _whenListingPrograms?.Invoke();
                return _programsUsingWinDivert;
            }));
    }

    private async Task StartAndWaitForBootAsync(EngineWorker worker)
    {
        _pipeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await worker.StartAsync(CancellationToken.None);
        await _pipeStarted.Task.WaitAsync(Timeout);
    }

    /// <summary>Holds the boot start until a resume has finished, then lets it run.</summary>
    private async Task<DiagnosticInfo> ResumeBeforeTheBootStartAsync(EngineWorker worker)
    {
        var bootStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.BootStartHold = bootStart.Task;
        _pipeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await worker.StartAsync(CancellationToken.None);
        var resumed = await _controlPlane.ResumeHandler!(CancellationToken.None).WaitAsync(Timeout);
        bootStart.SetResult();
        await _pipeStarted.Task.WaitAsync(Timeout);
        return resumed;
    }

    private async Task RunPipe(CancellationToken ct)
    {
        _pipeStarted.TrySetResult();
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
    }

    public void Dispose()
    {
        _monitor.Dispose();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite can hold traffic.db for a moment after the worker stops.
        }
    }

    private sealed class FakeInterceptor : IPacketInterceptor
    {
        public FakeInterceptor(Exception? startFailure = null) => StartFailure = startFailure;

        public Exception? StartFailure { get; set; }
        public bool StartCalled => StartCount > 0;
        public int StartCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Like a start waiting on another program's driver load: it ends when the service stops.</summary>
        public bool StartWaitsForStop { get; init; }

        /// <summary>Runs as a start begins, before it can fail.</summary>
        public Action? WhenStarting { get; set; }

        /// <summary>Runs as a stop begins, while the handles are still open.</summary>
        public Action? WhenStopping { get; set; }

        /// <summary>Runs once, on the next read of IsRunning, before it answers.</summary>
        public Action? WhenRunningRead { get; set; }

        /// <summary>Runs once, on the next read of IsRunning, after it has its answer.</summary>
        public Action? AfterRunningRead { get; set; }

        private bool _isRunning;

        public bool IsRunning
        {
            get
            {
                var hook = WhenRunningRead;
                WhenRunningRead = null;
                hook?.Invoke();
                var answer = _isRunning;
                var after = AfterRunningRead;
                AfterRunningRead = null;
                after?.Invoke();
                return answer;
            }
            set => _isRunning = value;
        }
        public long TotalBlocked => 0;
        public long TotalDelayed => 0;
        public long TotalDropped => 0;
        public long TotalSent => 0;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            Started.TrySetResult();
            WhenStarting?.Invoke();
            if (StartFailure is not null)
                throw StartFailure;
            if (StartWaitsForStop)
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            IsRunning = true;
        }

        public Task StopAsync()
        {
            WhenStopping?.Invoke();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public IReadOnlyList<object> GetRecentConnectionLog(int maxCount) => Array.Empty<object>();

        public void Dispose()
        {
        }
    }
}
