using System.ComponentModel;
using OpenNetLimit.Engine.Interception;
using SharpDivert;
using Xunit;

namespace OpenNetLimit.Tests;

public class DriverPreflightTests
{
    private const string OurFolder = @"C:\Program Files\OpenNetLimit\runtimes\win-x64\native";
    private const string OurDriver = OurFolder + @"\WinDivert64.sys";
    private const string GoneDriver = @"C:\Tools\OldVpn\WinDivert64.sys";

    /// <summary>Answers each state query with the next of the given states, then repeats the last.</summary>
    internal sealed class FakeDriverService(params DriverServiceState[] states) : IDriverService
    {
        private readonly Queue<DriverServiceState> _states = new(states);
        private DriverServiceState _last = DriverServiceState.NotInstalled;

        public string? ImagePath { get; set; }
        public Exception? QueryFailure { get; set; }
        public Exception? ImagePathFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public Exception? StopFailure { get; set; }
        public int StopRequests { get; private set; }

        /// <summary>The states the next queries answer with, once the current ones are used up.</summary>
        public void Then(params DriverServiceState[] states)
        {
            foreach (var state in states)
                _states.Enqueue(state);
        }

        /// <summary>Runs after each ImagePath read, so a test can change the registration mid-check.</summary>
        public Action<FakeDriverService>? AfterImagePathRead { get; set; }

        public bool Deleted { get; private set; }
        public int DeleteAttempts { get; private set; }
        public int Queries { get; private set; }

        public DriverServiceState QueryState()
        {
            Queries++;
            if (QueryFailure is not null)
                throw QueryFailure;
            if (_states.Count > 0)
                _last = _states.Dequeue();
            return _last;
        }

        public string? RegisteredImagePath()
        {
            if (ImagePathFailure is not null)
                throw ImagePathFailure;
            var path = ImagePath;
            AfterImagePathRead?.Invoke(this);
            return path;
        }

        public void DeleteRegistration()
        {
            DeleteAttempts++;
            if (DeleteFailure is not null)
                throw DeleteFailure;
            Deleted = true;
        }

        public void Stop()
        {
            StopRequests++;
            if (StopFailure is not null)
                throw StopFailure;
        }
    }

    private sealed class FakeLock : IDisposable
    {
        public void Dispose() { }
    }

    private static DriverPreflight Preflight(FakeDriverService service, List<TimeSpan>? sleeps = null, params string[] existingFiles) =>
        Preflight(service, () => new FakeLock(), sleeps, existingFiles);

    private static DriverPreflight Preflight(FakeDriverService service, Func<IDisposable?> lockInstall,
        List<TimeSpan>? sleeps = null, params string[] existingFiles)
    {
        var files = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        return new DriverPreflight(service, (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            sleeps?.Add(delay);
        }, files.Contains, lockInstall);
    }

    private static WinDivertOpenException OpenError(int code) =>
        new(WinDivert.Layer.Flow, WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly, code, "native text");

    [Theory]
    [InlineData(DriverServiceState.NotInstalled)]
    [InlineData(DriverServiceState.Stopped)]
    [InlineData(DriverServiceState.Running)]
    public void SettledDriver_IsOpenedRightAway(DriverServiceState state)
    {
        var sleeps = new List<TimeSpan>();
        var note = Preflight(new FakeDriverService(state), sleeps).WaitWhileDriverIsChanging(CancellationToken.None);

        Assert.Null(note);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void DriverAnotherProgramIsStarting_IsWaitedOnUntilItRuns()
    {
        var sleeps = new List<TimeSpan>();
        var service = new FakeDriverService(DriverServiceState.StartPending, DriverServiceState.StartPending, DriverServiceState.Running);

        var note = Preflight(service, sleeps).WaitWhileDriverIsChanging(CancellationToken.None);

        Assert.Equal(2, sleeps.Count);
        Assert.Equal(3, service.Queries);
        Assert.Contains("Waited", note);
    }

    [Fact]
    public void DriverStillStartingAfterTheLimit_StopsTheEngineInsteadOfOpening()
    {
        var sleeps = new List<TimeSpan>();
        var service = new FakeDriverService(DriverServiceState.StartPending);

        var error = Assert.Throws<InvalidOperationException>(
            () => Preflight(service, sleeps).WaitWhileDriverIsChanging(CancellationToken.None));

        Assert.Contains("Restart Windows", error.Message);
        Assert.Equal(DriverPreflight.PendingLimit, TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks)));
    }

    [Fact]
    public void DriverStillStoppingAfterTheLimit_IsOpenedAnyway()
    {
        var sleeps = new List<TimeSpan>();
        var service = new FakeDriverService(DriverServiceState.StopPending);

        var note = Preflight(service, sleeps).WaitWhileDriverIsChanging(CancellationToken.None);

        Assert.NotNull(note);
        Assert.Equal(DriverPreflight.PendingLimit, TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks)));
    }

    [Fact]
    public void StateThatCantBeRead_IsNotWaitedOn()
    {
        var sleeps = new List<TimeSpan>();
        var service = new FakeDriverService { QueryFailure = new Win32Exception(5) };

        var note = Preflight(service, sleeps).WaitWhileDriverIsChanging(CancellationToken.None);

        Assert.Null(note);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void StopDuringTheWait_EndsTheWait()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var service = new FakeDriverService(DriverServiceState.StartPending);

        Assert.Throws<OperationCanceledException>(() => Preflight(service).WaitWhileDriverIsChanging(stopping.Token));
        Assert.Equal(1, service.Queries);
    }

    [Fact]
    public void QuarantinedDriverFile_IsNamedAsTheCause()
    {
        var service = new FakeDriverService(DriverServiceState.NotInstalled);

        var diagnosis = Preflight(service).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Contains("WinDivert64.sys is missing from " + OurFolder, diagnosis.Reason);
        Assert.Contains("quarantine", diagnosis.Reason);
        Assert.False(service.Deleted);
    }

    [Fact]
    public void StoppedRegistrationForADeletedFolder_IsRemovedSoTheOpenCanRegisterOurs()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = @"\??\" + GoneDriver };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.True(diagnosis.RetryOpen);
        Assert.True(service.Deleted);
        Assert.Contains("It pointed at " + GoneDriver + ",", diagnosis.Reason);
        Assert.DoesNotContain(@"\??\", diagnosis.Reason);
    }

    [Fact]
    public void RunningRegistrationForADeletedFolder_IsLeftAloneAndNamed()
    {
        var service = new FakeDriverService(DriverServiceState.Running) { ImagePath = GoneDriver };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains(GoneDriver, diagnosis.Reason);
        Assert.Contains("sc.exe delete WinDivert", diagnosis.Reason);
    }

    [Fact]
    public void RegistrationTheServiceManagerDoesntList_IsLeftAlone()
    {
        var service = new FakeDriverService(DriverServiceState.NotInstalled) { ImagePath = GoneDriver };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
    }

    [Fact]
    public void RegistrationWhoseStateCantBeRead_IsLeftAlone()
    {
        var service = new FakeDriverService { ImagePath = GoneDriver, QueryFailure = new Win32Exception(5) };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
    }

    [Fact]
    public void StaleRegistrationThatCantBeRemoved_SaysHowToRemoveIt()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped)
        {
            ImagePath = GoneDriver,
            DeleteFailure = new Win32Exception(5)
        };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(1, service.DeleteAttempts);
        Assert.Contains("removing it failed", diagnosis.Reason);
        Assert.Contains("sc.exe delete WinDivert", diagnosis.Reason);
    }

    [Fact]
    public void StaleRegistrationAlreadyBeingRemoved_SaysWhatKeepsItAround()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped)
        {
            ImagePath = GoneDriver,
            DeleteFailure = new Win32Exception(1072)
        };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Contains("Services window", diagnosis.Reason);
        Assert.DoesNotContain("sc.exe delete", diagnosis.Reason);
    }

    [Fact]
    public void RegistrationChangedWhileItWasChecked_IsLeftAloneAndOpenedAgain()
    {
        RecordingLock? installLock = null;
        var lockHeldAtEachRead = new List<bool>();
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };
        service.AfterImagePathRead = s =>
        {
            lockHeldAtEachRead.Add(installLock?.Held == true);
            s.ImagePath = @"C:\Tools\NewVpn\WinDivert64.sys";
        };

        var diagnosis = Preflight(service, () => installLock = new RecordingLock(), null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.True(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains("changed it", diagnosis.Reason);
        // The second read is the one that counts, so it has to happen under the lock.
        Assert.Equal(new[] { false, true }, lockHeldAtEachRead);
        Assert.False(installLock!.Held);
    }

    [Fact]
    public void RegistrationOnAVolumePath_IsNeverRemoved()
    {
        // Taken as a relative path this never exists, which used to get a live driver removed.
        const string volumePath = @"\??\GLOBALROOT\Device\HarddiskVolume3\Tools\WinDivert64.sys";
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = volumePath };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains(volumePath, diagnosis.Reason);
    }

    [Fact]
    public void InstallLock_KeepsOtherThreadsOutUntilItIsReleased()
    {
        var installLock = DriverPreflight.LockInstall();
        Assert.NotNull(installLock);
        try
        {
            Assert.False(InstallMutexIsFreeForAnotherThread());
        }
        finally
        {
            installLock.Dispose();
        }
        Assert.True(InstallMutexIsFreeForAnotherThread());
    }

    [Fact]
    public void RealSleep_EndsWhenTheServiceStops()
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => DriverPreflight.Sleep(TimeSpan.FromSeconds(30), stopping.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void RealSleep_WaitsWhenNotStopped()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        DriverPreflight.Sleep(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(40), $"Took {watch.Elapsed}");
    }

    /// <summary>The mutex is reentrant on the thread that holds it, so another thread tries.</summary>
    private static bool InstallMutexIsFreeForAnotherThread()
    {
        bool taken = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, DriverPreflight.InstallMutexName);
            try
            {
                taken = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                taken = true;
            }
            if (taken)
                mutex.ReleaseMutex();
        });
        thread.Start();
        thread.Join();
        return taken;
    }

    private sealed class RecordingLock : IDisposable
    {
        public bool Held { get; private set; } = true;
        public void Dispose() => Held = false;
    }

    [Fact]
    public void InstallLockHeldByAnotherProgram_LeavesTheRegistrationAlone()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };

        var diagnosis = Preflight(service, () => null, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains(GoneDriver, diagnosis.Reason);
    }

    [Fact]
    public void RegistrationWhoseFileExists_IsNeverRemoved()
    {
        const string otherDriver = @"C:\Tools\Vpn\WinDivert64.sys";
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = otherDriver };

        var diagnosis = Preflight(service, null, OurDriver, otherDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains(otherDriver, diagnosis.Reason);
    }

    [Fact]
    public void RegistrationOnAKernelPath_IsNeverRemoved()
    {
        const string kernelPath = @"\Device\HarddiskVolume3\Tools\WinDivert64.sys";
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = kernelPath };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
        Assert.Contains(kernelPath, diagnosis.Reason);
    }

    [Fact]
    public void NoRegistration_SaysOurDriverFileIsThere()
    {
        var service = new FakeDriverService(DriverServiceState.NotInstalled);

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Contains("WinDivert64.sys is in " + OurFolder, diagnosis.Reason);
        Assert.DoesNotContain("quarantined", diagnosis.Reason);
    }

    [Fact]
    public void RegistrationThatCantBeRead_IsTreatedAsNone()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePathFailure = new UnauthorizedAccessException() };

        var diagnosis = Preflight(service, null, OurDriver).DiagnoseMissingFile(OurFolder);

        Assert.False(diagnosis.RetryOpen);
        Assert.Equal(0, service.DeleteAttempts);
    }

    [Theory]
    [InlineData(@"\??\C:\Tools\WinDivert64.sys", @"C:\Tools\WinDivert64.sys")]
    [InlineData(@"\\?\C:\Tools\WinDivert64.sys", @"C:\Tools\WinDivert64.sys")]
    [InlineData(@"\??\UNC\server\share\WinDivert64.sys", @"\\server\share\WinDivert64.sys")]
    [InlineData(@"""C:\Program Files\X\WinDivert64.sys""", @"C:\Program Files\X\WinDivert64.sys")]
    [InlineData(@"\SystemRoot\System32\drivers\WinDivert64.sys", @"D:\Win\System32\drivers\WinDivert64.sys")]
    [InlineData(@"System32\drivers\WinDivert64.sys", @"D:\Win\System32\drivers\WinDivert64.sys")]
    [InlineData(@"C:\Tools\WinDivert64.sys", @"C:\Tools\WinDivert64.sys")]
    [InlineData(@"\Device\HarddiskVolume3\Tools\WinDivert64.sys", null)]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume3\Tools\WinDivert64.sys", null)]
    [InlineData(@"\??\Volume{1b3c5e7a-0000-0000-0000-100000000000}\Tools\WinDivert64.sys", null)]
    [InlineData(@"\\?\Volume{1b3c5e7a-0000-0000-0000-100000000000}\Tools\WinDivert64.sys", null)]
    [InlineData("", null)]
    public void ImagePath_IsTurnedIntoTheFileItNames(string imagePath, string? expected)
    {
        Assert.Equal(expected, DriverPreflight.ImagePathToFile(imagePath, @"D:\Win"));
    }

    [Fact]
    public void OpenThatWorks_IsNotChecked()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };

        var result = Preflight(service, null, OurDriver).OpenLoadingDriver(() => "handle", () => OurFolder);

        Assert.Equal("handle", result);
        Assert.Equal(0, service.Queries);
    }

    [Fact]
    public void OpenFailingForAnotherReason_KeepsItsOwnError()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };
        var original = OpenError(5);

        var thrown = Assert.Throws<WinDivertOpenException>(() =>
            Preflight(service, null, OurDriver).OpenLoadingDriver<string>(() => throw original, () => OurFolder));

        Assert.Same(original, thrown);
        Assert.Equal(0, service.DeleteAttempts);
    }

    [Fact]
    public void OpenFailingOnADeadRegistration_RemovesItAndOpensAgain()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };
        int opens = 0;

        var result = Preflight(service, null, OurDriver).OpenLoadingDriver(
            () => ++opens == 1 ? throw OpenError(3) : "handle", () => OurFolder);

        Assert.Equal("handle", result);
        Assert.Equal(2, opens);
        Assert.True(service.Deleted);
    }

    [Fact]
    public void OpenFailingAgainAfterTheRemoval_SaysWhatWasRemovedAndWhatHoldsIt()
    {
        var service = new FakeDriverService(DriverServiceState.Stopped) { ImagePath = GoneDriver };
        int opens = 0;

        var thrown = Assert.Throws<WinDivertOpenException>(() => Preflight(service, null, OurDriver).OpenLoadingDriver<string>(
            () => throw OpenError(++opens == 1 ? 2 : 1072), () => OurFolder));

        Assert.Equal(2, opens);
        Assert.Equal(1072, thrown.NativeErrorCode);
        Assert.Contains(GoneDriver, thrown.Reason);
        Assert.Contains("Services window", thrown.Reason);
    }

    [Fact]
    public void OpenFailingOnAQuarantinedFile_IsNotRetriedAndSaysWhy()
    {
        var service = new FakeDriverService(DriverServiceState.NotInstalled);
        int opens = 0;

        var thrown = Assert.Throws<WinDivertOpenException>(() => Preflight(service).OpenLoadingDriver<string>(
            () => { opens++; throw OpenError(2); }, () => OurFolder));

        Assert.Equal(1, opens);
        Assert.Equal(2, thrown.NativeErrorCode);
        Assert.Contains("quarantine", thrown.Reason);
        Assert.Contains("quarantine", thrown.Message);
    }
}
