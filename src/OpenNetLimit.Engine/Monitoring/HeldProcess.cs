using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>
/// A process held open so Windows can't hand its ID to another one, which can say whether it has exited.
/// </summary>
internal interface IHeldProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>
    /// From when the ID may be another process's, when the one asked for is gone and wasn't held:
    /// no process had the ID then, or the one that has it now started then. A name read for the
    /// ID before that time was the old process's.
    /// </summary>
    DateTime? IdFreeSince => null;
}

/// <summary>
/// Holds a process seen at <paramref name="firstSeenUtc"/>. One that has exited, with
/// <see cref="IHeldProcess.IdFreeSince"/>, when no process has the ID or the one that has it
/// started later. <see cref="HeldProcess.Unheld"/> when it can't be held, so it's taken to be
/// running. Null is taken as exited, with nothing known about the ID since.
/// </summary>
internal delegate IHeldProcess? HoldProcess(uint processId, DateTime firstSeenUtc);

internal sealed class HeldProcess : IHeldProcess
{
    /// <summary>A process that couldn't be held. It never reads as exited.</summary>
    public static readonly IHeldProcess Unheld = new NotHeld();

    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Windows stamps a process's start with the time as of the clock's last tick, up to 15.6 ms
    /// before it really started. A name read in that gap would look newer than the process.
    /// </summary>
    private static readonly TimeSpan StartStampLag = TimeSpan.FromMilliseconds(16);

    private readonly ProcessWait _process;

    private HeldProcess(SafeWaitHandle handle) => _process = new ProcessWait(handle);

    public bool HasExited => _process.WaitOne(0);

    public void Dispose() => _process.Dispose();

    public static IHeldProcess? Hold(uint processId, DateTime firstSeenUtc)
    {
        // The idle process and the kernel never exit, and Windows won't open the idle one.
        if (processId is 0 or 4)
            return Unheld;
        // Anything named for the ID before this was named for this process or an earlier one.
        var lookedAt = DateTime.UtcNow;
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return error == ErrorInvalidParameter ? new Gone(lookedAt) : Unheld;
        }
        if (GetProcessTimes(handle, out long created, out _, out _, out _)
            && StartedSince(DateTime.FromFileTimeUtc(created), firstSeenUtc, DateTime.UtcNow))
        {
            handle.Dispose();
            return new Gone(DateTime.FromFileTimeUtc(created) + StartStampLag);
        }
        return new HeldProcess(handle);
    }

    /// <summary>
    /// When the process that has the ID started, as a FILETIME. Null when it can't be opened or
    /// nothing has the ID. The idle process and the kernel never change, so theirs is zero.
    /// </summary>
    internal static long? StartedAt(uint processId)
    {
        if (processId is 0 or 4)
            return 0;
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;
        return GetProcessTimes(handle, out long created, out _, out _, out _) ? created : null;
    }

    /// <summary>
    /// Whether a process created at <paramref name="createdUtc"/> started after <paramref name="sinceUtc"/>,
    /// so it can't be the one seen then and the ID went to another process. One created after
    /// <paramref name="nowUtc"/> means the clock was set back since, which says nothing either way.
    /// </summary>
    internal static bool StartedSince(DateTime createdUtc, DateTime sinceUtc, DateTime nowUtc) =>
        createdUtc > sinceUtc && createdUtc <= nowUtc;

    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private sealed class NotHeld : IHeldProcess
    {
        public bool HasExited => false;

        public void Dispose()
        {
        }
    }

    /// <summary>A process that exited, whose ID may be another one's from <see cref="IdFreeSince"/> on.</summary>
    private sealed class Gone(DateTime idFreeSince) : IHeldProcess
    {
        public bool HasExited => true;

        public DateTime? IdFreeSince => idFreeSince;

        public void Dispose()
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);
}
