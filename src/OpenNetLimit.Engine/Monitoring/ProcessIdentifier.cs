using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenNetLimit.Engine.Monitoring;

public static class ProcessIdentifier
{
    public static string? GetServiceName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!process.ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch
        {
            return null;
        }

        try
        {
            uint bytesNeeded = 0;
            EnumServicesStatusEx(IntPtr.Zero, 0, 0x30, 1, IntPtr.Zero, 0, ref bytesNeeded, out _, IntPtr.Zero, null);

            var scManager = OpenSCManager(null, null, 0x0004);
            if (scManager == IntPtr.Zero) return null;
            try
            {
                var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    if (!EnumServicesStatusEx(scManager, 0, 0x30, 1, buffer, bytesNeeded, ref bytesNeeded, out uint count, IntPtr.Zero, null))
                        return null;

                    var entrySize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                    for (uint i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + (int)(i * entrySize));
                        if (entry.ServiceStatusProcess.dwProcessId == processId)
                            return entry.lpServiceName;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(scManager);
            }
        }
        catch
        {
            // Fallback: not critical
        }

        return null;
    }

    public static string? GetAppxPackageName(uint processId)
    {
        try
        {
            var handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                uint length = 0;
                GetPackageFullName(handle, ref length, null);
                if (length == 0) return null;

                var sb = new StringBuilder((int)length);
                if (GetPackageFullName(handle, ref length, sb) == 0)
                    return sb.ToString();
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            // Not a UWP/Store app
        }
        return null;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(
        IntPtr hSCManager, int infoLevel, uint serviceType, uint serviceState,
        IntPtr lpServices, uint cbBufSize, ref uint pcbBytesNeeded,
        out uint lpServicesReturned, IntPtr lpResumeHandle, string? pszGroupName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr hProcess, ref uint packageFullNameLength, StringBuilder? packageFullName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpServiceName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }
}
