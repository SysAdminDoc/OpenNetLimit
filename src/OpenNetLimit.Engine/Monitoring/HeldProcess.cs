using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>
/// A process held open so Windows can't hand its ID to another one, which can say whether it has exited.
/// </summary>
internal interface IHeldProcess : IDisposable
{
    bool HasExited { get; }
}

/// <summary>
/// Holds a process seen at <paramref name="firstSeenUtc"/>. Null when it's gone: no process has the
/// ID, or the one that does started later. <see cref="HeldProcess.Unheld"/> when it can't be held,
/// so it's taken to be running.
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
        // Created after it was first seen means the ID went to another process. One created
        // "after now" means the clock was set back since, and says nothing.
        if (GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            var createdUtc = DateTime.FromFileTimeUtc(created);
            if (createdUtc > firstSeenUtc && createdUtc <= DateTime.UtcNow)
            {
                handle.Dispose();
                return null;
            }
        }
        return new HeldProcess(handle);
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);
}
