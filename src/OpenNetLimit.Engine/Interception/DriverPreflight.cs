using System.ComponentModel;
using System.Diagnostics;

namespace OpenNetLimit.Engine.Interception;

/// <summary>
/// Checks the shared <c>WinDivert</c> driver service around the engine's first open. Another
/// WinDivert program can be loading the driver at the same moment (a handle opened while the
/// driver is still starting crashes Windows, basil00/WinDivert#408), or can have left a
/// registration that points at a folder it deleted, which fails every open with error 2 or 3.
/// </summary>
public sealed class DriverPreflight
{
    /// <summary>How long a start or stop that's under way is waited on.</summary>
    public static readonly TimeSpan PendingLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The service manager reports a kernel driver as StartPending for as long as its
    /// DriverEntry runs (measured at 4 to 11 ms on a warm load, longer on a cold boot).
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// WinDivert.dll holds this named mutex while it registers and starts the driver (the name
    /// is in the 2.2.2 DLL). It has no <c>Global\</c> prefix, so holding it only keeps programs
    /// in the service's own session from registering mid-check. A program in a user session is
    /// caught by the re-read and the stopped check instead.
    /// </summary>
    private const string InstallMutexName = "WinDivertDriverInstallMutex";
    private static readonly TimeSpan InstallLockLimit = TimeSpan.FromSeconds(5);

    /// <summary>ERROR_SERVICE_MARKED_FOR_DELETE: deleted, but a handle to it is still open.</summary>
    private const int ErrorServiceMarkedForDelete = 1072;

    private readonly IDriverService _service;
    private readonly Action<TimeSpan, CancellationToken> _sleep;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<IDisposable?> _lockInstall;

    public DriverPreflight(IDriverService service,
        Action<TimeSpan, CancellationToken>? sleep = null, Func<string, bool>? fileExists = null,
        Func<IDisposable?>? lockInstall = null)
    {
        _service = service;
        _sleep = sleep ?? Sleep;
        _fileExists = fileExists ?? File.Exists;
        _lockInstall = lockInstall ?? LockInstall;
    }

    /// <summary>
    /// Waits while another program is starting or stopping the driver, so the engine's open
    /// can't reach it halfway through DriverEntry.
    /// </summary>
    /// <returns>What it waited for, or null when it didn't wait.</returns>
    /// <exception cref="InvalidOperationException">The driver is still starting after <see cref="PendingLimit"/>.</exception>
    public string? WaitWhileDriverIsChanging(CancellationToken cancellationToken)
    {
        var state = TryQueryState();
        var waited = TimeSpan.Zero;
        while (state is DriverServiceState.StartPending or DriverServiceState.StopPending && waited < PendingLimit)
        {
            _sleep(PollInterval, cancellationToken);
            waited += PollInterval;
            state = TryQueryState();
        }
        if (state == DriverServiceState.StartPending)
        {
            throw new InvalidOperationException(
                $"Another program has been starting the WinDivert driver for over {PendingLimit.TotalSeconds:0} s. " +
                "Opening it before it finishes can crash Windows, so the engine didn't start. Restart Windows and try again.");
        }
        return waited == TimeSpan.Zero
            ? null
            : $"Waited {waited.TotalMilliseconds:0} ms for another program to finish starting or stopping the WinDivert driver.";
    }

    /// <summary>
    /// Opens the handle that loads the driver. When that fails with error 2 or 3 it finds out
    /// why: a dead registration another program left is removed and the open tried once more,
    /// and otherwise the error says what was found.
    /// </summary>
    public T OpenLoadingDriver<T>(Func<T> open, Func<string> driverFolder)
    {
        try
        {
            return open();
        }
        catch (WinDivertOpenException ex) when (ex.NativeErrorCode is 2 or 3)
        {
            var diagnosis = DiagnoseMissingFile(driverFolder());
            if (!diagnosis.RetryOpen)
                throw ex.WithDetail(diagnosis.Reason);

            Trace.TraceWarning(diagnosis.Reason);
            try
            {
                return open();
            }
            catch (WinDivertOpenException retryFailure)
            {
                throw retryFailure.WithDetail(AfterFailedRetry(diagnosis, retryFailure.NativeErrorCode));
            }
        }
    }

    /// <summary>
    /// Explains an open that failed with error 2 or 3, and removes a registration whose driver
    /// file is gone when that's safe. <paramref name="driverFolder"/> holds the WinDivert.dll
    /// that was loaded, which looks for WinDivert64.sys next to itself.
    /// </summary>
    public MissingFileDiagnosis DiagnoseMissingFile(string driverFolder)
    {
        string ourDriver = Path.Combine(driverFolder, "WinDivert64.sys");
        if (!_fileExists(ourDriver))
        {
            return new MissingFileDiagnosis(false,
                $"WinDivert64.sys is missing from {driverFolder}. Antivirus software often quarantines it. " +
                "Restore it from quarantine and allow it, or reinstall OpenNetLimit.");
        }

        string? imagePath = TryRegisteredImagePath();
        if (imagePath is null)
        {
            return new MissingFileDiagnosis(false,
                $"WinDivert64.sys is in {driverFolder} and no other program's WinDivert driver registration was found, " +
                "so Windows is missing another file the driver needs. Reinstall OpenNetLimit, and check whether antivirus blocked part of it.");
        }

        string? registered = ImagePathToFile(imagePath);
        if (registered is null)
        {
            return new MissingFileDiagnosis(false,
                $"The shared WinDivert driver service points at {imagePath}, a path OpenNetLimit can't check, and Windows couldn't find a file it needs. " +
                "If no other program is using WinDivert, run `sc.exe delete WinDivert` from an administrator prompt, then restart OpenNetLimit.");
        }
        if (_fileExists(registered))
        {
            return new MissingFileDiagnosis(false,
                $"The shared WinDivert driver service points at {registered}, and Windows still couldn't find a file it needs.");
        }

        using var installLock = _lockInstall();
        if (installLock is null)
            return LeftInPlace(registered);

        // Read again under the lock: another program may have replaced the registration since.
        string? registeredNow = ImagePathToFile(TryRegisteredImagePath());
        if (!string.Equals(registeredNow, registered, StringComparison.OrdinalIgnoreCase))
        {
            return new MissingFileDiagnosis(true,
                $"The shared WinDivert driver service pointed at {registered}, which no longer exists, and another program changed it while OpenNetLimit was checking. It was left alone.");
        }
        if (TryQueryState() != DriverServiceState.Stopped)
            return LeftInPlace(registered);

        try
        {
            _service.DeleteRegistration();
            return new MissingFileDiagnosis(true,
                $"Removed a WinDivert driver registration left by another program. It pointed at {registered}, which no longer exists.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceMarkedForDelete)
        {
            return new MissingFileDiagnosis(false,
                $"The shared WinDivert driver service points at {registered}, which no longer exists. Windows is already removing it, " +
                "but a program still has it open, often the Services window. Close that program or restart Windows, then restart OpenNetLimit.");
        }
        catch (Win32Exception ex)
        {
            return new MissingFileDiagnosis(false,
                $"The shared WinDivert driver service points at {registered}, which no longer exists, and removing it failed ({ex.Message}). " +
                "Run `sc.exe delete WinDivert` from an administrator prompt, then restart OpenNetLimit.");
        }
    }

    /// <summary>What to tell the user when the open failed again after a dead registration was dealt with.</summary>
    internal static string AfterFailedRetry(MissingFileDiagnosis diagnosis, int nativeErrorCode) =>
        nativeErrorCode == ErrorServiceMarkedForDelete
            ? $"{diagnosis.Reason} Windows hasn't finished removing it because a program still has it open, often the Services window. " +
              "Close that program or restart Windows, then restart OpenNetLimit."
            : $"{diagnosis.Reason} Opening the driver still failed after that.";

    private static MissingFileDiagnosis LeftInPlace(string registered) => new(false,
        $"The shared WinDivert driver service points at {registered}, which no longer exists. Another program registered it. " +
        "Restart Windows, or run `sc.exe delete WinDivert` from an administrator prompt, then restart OpenNetLimit.");

    /// <summary>
    /// The file a service ImagePath names. Kernel driver paths come as <c>\??\C:\...</c>,
    /// <c>\??\UNC\server\...</c>, <c>\SystemRoot\...</c> or <c>System32\drivers\...</c>.
    /// Null for an empty path or a kernel object path such as <c>\Device\...</c>, which a
    /// user-mode check can't follow.
    /// </summary>
    internal static string? ImagePathToFile(string? imagePath, string? systemRoot = null)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;
        systemRoot ??= Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        string path = imagePath.Trim().Trim('"');
        if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[@"\??\UNC\".Length..];
        if (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(systemRoot, path[@"\SystemRoot\".Length..]);
        if (path.StartsWith('\\') && !path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        if (!Path.IsPathRooted(path))
            return Path.Combine(systemRoot, path);
        return path;
    }

    private DriverServiceState? TryQueryState()
    {
        try
        {
            return _service.QueryState();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private string? TryRegisteredImagePath()
    {
        try
        {
            return _service.RegisteredImagePath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static IDisposable? LockInstall()
    {
        var mutex = new Mutex(false, InstallMutexName);
        try
        {
            if (mutex.WaitOne(InstallLockLimit))
                return new HeldMutex(mutex);
        }
        catch (AbandonedMutexException)
        {
            // A program died holding it. The wait still took ownership.
            return new HeldMutex(mutex);
        }
        mutex.Dispose();
        return null;
    }

    private sealed class HeldMutex(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static void Sleep(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.WaitHandle.WaitOne(delay);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <param name="RetryOpen">A dead registration was dealt with, so opening again can work.</param>
/// <param name="Reason">What was found and what the user can do.</param>
public sealed record MissingFileDiagnosis(bool RetryOpen, string Reason);
