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

    private EngineWorker CreateWorker(IPacketInterceptor interceptor, bool isAdmin)
    {
        var monitor = new TrafficMonitor();
        return new EngineWorker(
            interceptor,
            _rules,
            new ProcessRateLimiter(),
            new FlowTracker(),
            monitor,
            RunPipe,
            new BandwidthAlertTracker(monitor),
            new PluginManager(new PluginOptions { Enabled = false, PluginDirectory = Path.Combine(_dataDir, "plugins") },
                NullLogger<PluginManager>.Instance),
            _controlPlane,
            NullLogger<EngineWorker>.Instance,
            _dataDir,
            () => isAdmin);
    }

    private async Task RunPipe(CancellationToken ct)
    {
        _pipeStarted.TrySetResult();
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
    }

    public void Dispose()
    {
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
        public bool IsRunning { get; set; }
        public long TotalBlocked => 0;
        public long TotalDelayed => 0;
        public long TotalDropped => 0;
        public long TotalSent => 0;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalled = true;
            if (_startFailure is not null)
                throw _startFailure;
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
