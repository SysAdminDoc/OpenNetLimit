using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenNetLimit.Engine.Interception;

/// <summary>What the service manager says about the shared <c>WinDivert</c> driver service.</summary>
public enum DriverServiceState
{
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Other
}

/// <summary>
/// The <c>WinDivert</c> kernel driver service. Every WinDivert 2.x program on the PC shares it,
/// so it can be registered, started or left behind by some other program.
/// </summary>
public interface IDriverService
{
    DriverServiceState QueryState();

    /// <summary>
    /// The registration's ImagePath, with environment variables expanded when Windows would
    /// expand them, or null when there's none.
    /// </summary>
    string? RegisteredImagePath();

    /// <summary>Removes the registration. WinDivert registers itself again on the next open.</summary>
    void DeleteRegistration();
}

/// <summary>The real <c>WinDivert</c> service, through the service manager and the registry.</summary>
public sealed class WindowsDriverService : IDriverService
{
    private const string ServiceName = "WinDivert";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint Delete = 0x10000;
    private const int ErrorServiceDoesNotExist = 1060;

    public DriverServiceState QueryState()
    {
        using var service = OpenService(ServiceQueryStatus);
        if (service is null)
            return DriverServiceState.NotInstalled;
        if (!QueryServiceStatus(service.Handle, out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.CurrentState switch
        {
            1 => DriverServiceState.Stopped,
            2 => DriverServiceState.StartPending,
            3 => DriverServiceState.StopPending,
            4 => DriverServiceState.Running,
            _ => DriverServiceState.Other
        };
    }

    public string? RegisteredImagePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string imagePath)
            return null;
        // Windows expands environment variables only in a REG_EXPAND_SZ value.
        return key.GetValueKind("ImagePath") == RegistryValueKind.ExpandString
            ? Environment.ExpandEnvironmentVariables(imagePath)
            : imagePath;
    }

    public void DeleteRegistration()
    {
        using var service = OpenService(Delete);
        if (service is null)
            return;
        if (!DeleteService(service.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static ScHandle? OpenService(uint access)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, ServiceName, access);
            if (service != IntPtr.Zero)
                return new ScHandle(service);
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
                return null;
            throw new Win32Exception(error);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private sealed class ScHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => CloseServiceHandle(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
