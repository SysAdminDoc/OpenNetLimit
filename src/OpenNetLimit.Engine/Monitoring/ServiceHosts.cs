using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>
/// The services in each process, read from the Service Control Manager. Readers get the last
/// read without waiting, and a new read replaces it whole.
/// </summary>
public sealed class ServiceHosts : IServiceHosts, IDisposable
{
    /// <summary>How soon a svchost the last read didn't have can cause another read.</summary>
    internal static readonly TimeSpan MinReadGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Starts watching a process and calls <c>exited</c> when it goes. Returns null when the
    /// process is gone already or started after <c>readStartedUtc</c>, which means its id went to
    /// another process since the read, and <see cref="Unwatched"/> when it can't be watched but
    /// the read still stands.
    /// </summary>
    internal delegate IDisposable? WatchExit(uint processId, DateTime readStartedUtc, Action exited);

    /// <summary>A process kept as read, with nothing watching it for its exit.</summary>
    internal static readonly IDisposable Unwatched = new NoWatch();

    private readonly Func<IReadOnlyDictionary<uint, string[]>> _read;
    private readonly Func<DateTime> _utcNow;
    private readonly WatchExit _watchExit;
    private readonly Action<Action> _queue;
    private readonly object _readLock = new();
    private volatile FrozenDictionary<uint, string[]> _hosts = FrozenDictionary<uint, string[]>.Empty;

    /// <summary>
    /// The watch on each process in <see cref="_hosts"/>. A process held open can't give its id
    /// to another, so an entry always means the process it was read for: it goes when that
    /// process exits, before the id is free. Only touched under <see cref="_readLock"/>.
    /// </summary>
    private Dictionary<uint, Watch> _watches = [];
    private long _lastReadTicks = DateTime.MinValue.Ticks;
    private int _readQueued;
    private bool _disposed;

    public ServiceHosts() : this(ReadServiceManager, () => DateTime.UtcNow, WatchProcess, work => ThreadPool.QueueUserWorkItem(_ => work()))
    {
    }

    /// <param name="read">Running services by process id. Throws <see cref="Win32Exception"/> when the list can't be read.</param>
    /// <param name="watchExit">Watches each process read. Leaving it out keeps every process unwatched.</param>
    /// <param name="queue">Runs a read asked for by <see cref="Resolve"/> off the caller's thread.</param>
    internal ServiceHosts(Func<IReadOnlyDictionary<uint, string[]>> read, Func<DateTime> utcNow,
        WatchExit? watchExit = null, Action<Action>? queue = null)
    {
        _read = read;
        _utcNow = utcNow;
        _watchExit = watchExit ?? ((_, _, _) => Unwatched);
        _queue = queue ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    public event Action? Changed;

    /// <summary>The process Windows runs shared services in.</summary>
    public static bool IsServiceHost(string processName) =>
        processName.Equals(BandwidthRule.ServiceHostName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string>? ServicesIn(uint processId) =>
        _hosts.TryGetValue(processId, out var services) ? services : null;

    public IReadOnlyList<string>? Resolve(uint processId, string processName)
    {
        if (ServicesIn(processId) is { } known)
            return known;
        if (IsServiceHost(processName))
            RequestRead();
        return null;
    }

    /// <summary>
    /// Reads the list on another thread, at most about once a second. The packet loop can end
    /// up here through a new flow, and a read there held up every packet.
    /// </summary>
    private void RequestRead()
    {
        if (_utcNow().Ticks - Volatile.Read(ref _lastReadTicks) < MinReadGap.Ticks
            || Interlocked.Exchange(ref _readQueued, 1) == 1)
            return;
        _queue(() =>
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                // Nothing above a pool thread would see it, and the next refresh reads again.
                System.Diagnostics.Trace.TraceWarning($"Reading the services of a new svchost failed: {ex}");
            }
            finally
            {
                Volatile.Write(ref _readQueued, 0);
            }
        });
    }

    public void Refresh()
    {
        bool changed;
        List<Watch> released;
        lock (_readLock)
        {
            if (_disposed)
                return;
            changed = ReadLocked(out released);
        }
        try
        {
            if (changed)
                Changed?.Invoke();
        }
        finally
        {
            // Let go only once the rules heard: one of these may have exited, and its id is
            // free the moment its last handle closes.
            foreach (var watch in released)
                watch.Handle?.Dispose();
        }
    }

    /// <param name="released">Watches on processes the read no longer has, for the caller to let go of.</param>
    /// <returns>True when a process's services changed.</returns>
    private bool ReadLocked(out List<Watch> released)
    {
        released = [];
        var started = _utcNow();
        Volatile.Write(ref _lastReadTicks, started.Ticks);
        IReadOnlyDictionary<uint, string[]> read;
        try
        {
            read = _read();
        }
        catch (Win32Exception ex)
        {
            // The last read stays. A svchost it doesn't have is treated as Windows' own anyway.
            System.Diagnostics.Trace.TraceWarning($"Reading the running services failed: {ex.Message}");
            return false;
        }
        var hosts = new Dictionary<uint, string[]>();
        var watches = new Dictionary<uint, Watch>();
        try
        {
            foreach (var (processId, services) in read)
            {
                // A process watched since an earlier read is still that process: its id can't have
                // been reused while it's held open.
                if (!_watches.Remove(processId, out var watch))
                {
                    var token = watch = new Watch();
                    watch.Handle = _watchExit(processId, started, () => OnExited(processId, token));
                    if (watch.Handle is null)
                        continue;
                }
                hosts[processId] = services.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                watches[processId] = watch;
            }
        }
        catch
        {
            // Put back what this read took over, so their exits still drop them and let go.
            foreach (var (processId, watch) in watches)
                _watches[processId] = watch;
            throw;
        }
        released.AddRange(_watches.Values);
        _watches = watches;
        var next = hosts.ToFrozenDictionary();
        bool changed = !SameHosts(_hosts, next);
        _hosts = next;
        return changed;
    }

    /// <summary>
    /// Drops a process that exited and lets the rules catch up before letting go of it, so
    /// whatever takes its id next never gets its services, or its limit.
    /// </summary>
    private void OnExited(uint processId, Watch token)
    {
        lock (_readLock)
        {
            // A read since dropped it, or a newer watch has the id.
            if (_disposed || !_watches.TryGetValue(processId, out var current) || current != token)
                return;
            _watches.Remove(processId);
            _hosts = _hosts.Where(entry => entry.Key != processId).ToFrozenDictionary();
        }
        try
        {
            Changed?.Invoke();
        }
        finally
        {
            token.Handle?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_readLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var watch in _watches.Values)
                watch.Handle?.Dispose();
            _watches.Clear();
        }
    }

    private sealed class Watch
    {
        public IDisposable? Handle;
    }

    private sealed class NoWatch : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;

    /// <summary>Holds the process open and waits on a pool thread for it to exit.</summary>
    internal static IDisposable? WatchProcess(uint processId, DateTime readStartedUtc, Action exited)
    {
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            // No such process any more. Anything else (no access, when not running as the
            // service) leaves the read as it was, like before processes were watched.
            return error == ErrorInvalidParameter ? null : Unwatched;
        }
        if (GetProcessTimes(handle, out long created, out _, out _, out _)
            && HeldProcess.StartedSince(DateTime.FromFileTimeUtc(created), readStartedUtc, DateTime.UtcNow))
        {
            handle.Dispose();
            return null;
        }
        var process = new ProcessWait(handle);
        var registration = ThreadPool.RegisterWaitForSingleObject(process, (_, _) => exited(), null,
            Timeout.Infinite, executeOnlyOnce: true);
        return new ExitWatch(registration, process);
    }

    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private sealed class ExitWatch(RegisteredWaitHandle registration, WaitHandle process) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            process.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);

    private static bool SameHosts(FrozenDictionary<uint, string[]> before, FrozenDictionary<uint, string[]> after)
    {
        if (before.Count != after.Count)
            return false;
        foreach (var (processId, services) in after)
        {
            if (!before.TryGetValue(processId, out var old)
                || !old.SequenceEqual(services, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 0x1;
    private const int ErrorMoreData = 234;

    /// <summary>Every running Win32 service with a process, grouped by that process.</summary>
    private static IReadOnlyDictionary<uint, string[]> ReadServiceManager()
    {
        var manager = OpenSCManager(null, null, ScManagerEnumerateService);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var hosts = new Dictionary<uint, List<string>>();
        try
        {
            uint resume = 0;
            uint size = 0;
            bool more = true;
            while (more)
            {
                var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
                try
                {
                    bool done = EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive,
                        buffer, size, out uint needed, out uint count, ref resume, null);
                    int error = done ? 0 : Marshal.GetLastWin32Error();
                    if (!done && error != ErrorMoreData)
                        throw new Win32Exception(error);
                    // ERROR_MORE_DATA with entries in the buffer means the rest comes from the
                    // resume point. With none, the buffer was too small for even one.
                    var entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (uint i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (int)(i * entrySize));
                        uint processId = entry.Status.ProcessId;
                        if (processId == 0 || string.IsNullOrEmpty(entry.ServiceName))
                            continue;
                        if (!hosts.TryGetValue(processId, out var names))
                            hosts[processId] = names = [];
                        names.Add(entry.ServiceName);
                    }
                    more = !done;
                    if (more && count == 0)
                    {
                        // Nothing fit, so the next try needs a bigger buffer than this one.
                        if (needed <= size)
                            throw new Win32Exception(error);
                        size = needed;
                    }
                    else if (more)
                    {
                        size = Math.Max(size, needed);
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
        return hosts.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    private static extern bool EnumServicesStatusEx(IntPtr manager, int infoLevel, uint serviceType,
        uint serviceState, IntPtr services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ServiceName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }
}
