using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>
/// The services in each process, read from the Service Control Manager. Readers get the last
/// read without waiting, and a new read replaces it whole.
/// </summary>
public sealed class ServiceHosts : IServiceHosts
{
    /// <summary>How soon a svchost the last read didn't have can cause another read.</summary>
    internal static readonly TimeSpan MinReadGap = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyDictionary<uint, string[]>> _read;
    private readonly Func<DateTime> _utcNow;
    private readonly object _readLock = new();
    private volatile FrozenDictionary<uint, string[]> _hosts = FrozenDictionary<uint, string[]>.Empty;
    private DateTime _lastRead = DateTime.MinValue;

    /// <summary>A read found a change that <see cref="Changed"/> hasn't reported yet.</summary>
    private bool _unreported;

    public ServiceHosts() : this(ReadServiceManager, () => DateTime.UtcNow)
    {
    }

    /// <param name="read">Running services by process id. Throws <see cref="Win32Exception"/> when the list can't be read.</param>
    internal ServiceHosts(Func<IReadOnlyDictionary<uint, string[]>> read, Func<DateTime> utcNow)
    {
        _read = read;
        _utcNow = utcNow;
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
        if (!IsServiceHost(processName))
            return null;
        lock (_readLock)
        {
            if (_utcNow() - _lastRead >= MinReadGap && ReadLocked())
                _unreported = true;
        }
        return ServicesIn(processId);
    }

    public void Refresh()
    {
        bool changed;
        lock (_readLock)
        {
            changed = ReadLocked() | _unreported;
            _unreported = false;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <returns>True when a process's services changed.</returns>
    private bool ReadLocked()
    {
        _lastRead = _utcNow();
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
        var next = read.ToFrozenDictionary(
            entry => entry.Key,
            entry => entry.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        bool changed = !SameHosts(_hosts, next);
        _hosts = next;
        return changed;
    }

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
