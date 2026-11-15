using Microsoft.Extensions.Logging.Abstractions;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using OpenNetLimit.Engine.Rules;
using OpenNetLimit.Service;
using OpenNetLimit.Service.Control;
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
    private readonly ControlPlaneState _controlPlane = new();
    private readonly TaskCompletionSource _pipeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string RulesPath => Path.Combine(_dataDir, "rules.json");

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

    private EngineWorker CreateWorker(IPacketInterceptor interceptor, bool isAdmin) =>
        CreateWorker(interceptor, () => isAdmin);

    private EngineWorker CreateWorker(IPacketInterceptor interceptor, Func<bool> isAdmin)
    {
        return new EngineWorker(
            interceptor,
            _rules,
            new ProcessRateLimiter(),
            new FlowTracker(),
            _monitor,
            RunPipe,
            new BandwidthAlertTracker(_monitor),
            new PluginManager(new PluginOptions { Enabled = false, PluginDirectory = Path.Combine(_dataDir, "plugins") },
                NullLogger<PluginManager>.Instance),
            _controlPlane,
            NullLogger<EngineWorker>.Instance,
            _dataDir,
            isAdmin);
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
        private readonly Exception? _startFailure;

        public FakeInterceptor(Exception? startFailure = null) => _startFailure = startFailure;

        public bool StartCalled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Like a start waiting on another program's driver load: it ends when the service stops.</summary>
        public bool StartWaitsForStop { get; init; }
        public bool IsRunning { get; set; }
        public long TotalBlocked => 0;
        public long TotalDelayed => 0;
        public long TotalDropped => 0;
        public long TotalSent => 0;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalled = true;
            Started.TrySetResult();
            if (_startFailure is not null)
                throw _startFailure;
            if (StartWaitsForStop)
                return Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public IReadOnlyList<object> GetRecentConnectionLog(int maxCount) => Array.Empty<object>();

        public void Dispose()
        {
        }
    }
}
