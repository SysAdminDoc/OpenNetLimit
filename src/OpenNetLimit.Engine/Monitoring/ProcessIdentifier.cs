using System.Runtime.InteropServices;
using System.Text;

namespace OpenNetLimit.Engine.Monitoring;

public static class ProcessIdentifier
{
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr hProcess, ref uint packageFullNameLength, StringBuilder? packageFullName);
}
