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
    /// When the process that has the ID now started, if that isn't the one asked for. The one
    /// asked for has exited, and anything from that time on may be the new one's.
    /// </summary>
    DateTime? IdReusedAt => null;
}

/// <summary>
/// Holds a process seen at <paramref name="firstSeenUtc"/>. Null when no process has the ID.
/// One that has exited, with <see cref="IHeldProcess.IdReusedAt"/>, when the process that has
/// the ID started later. <see cref="HeldProcess.Unheld"/> when it can't be held, so it's taken
/// to be running.
/// </summary>
internal delegate IHeldProcess? HoldProcess(uint processId, DateTime firstSeenUtc);

internal sealed class HeldProcess : IHeldProcess
{
    /// <summary>A process that couldn't be held. It never reads as exited.</summary>
    public static readonly IHeldProcess Unheld = new NotHeld();

    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;

    private readonly ProcessWait _process;

    private HeldProcess(SafeWaitHandle handle) => _process = new ProcessWait(handle);

    public bool HasExited => _process.WaitOne(0);

    public void Dispose() => _process.Dispose();

    public static IHeldProcess? Hold(uint processId, DateTime firstSeenUtc)
    {
        // The idle process and the kernel never exit, and Windows won't open the idle one.
        if (processId is 0 or 4)
            return Unheld;
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return error == ErrorInvalidParameter ? null : Unheld;
        }
        if (GetProcessTimes(handle, out long created, out _, out _, out _)
            && StartedSince(DateTime.FromFileTimeUtc(created), firstSeenUtc, DateTime.UtcNow))
        {
            handle.Dispose();
            return new Replaced(DateTime.FromFileTimeUtc(created));
        }
        return new HeldProcess(handle);
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

    /// <summary>A process that exited, whose ID went to one that started at <see cref="IdReusedAt"/>.</summary>
    private sealed class Replaced(DateTime idReusedAt) : IHeldProcess
    {
        public bool HasExited => true;

        public DateTime? IdReusedAt => idReusedAt;

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
