using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>Reads the full path of the program a process runs.</summary>
internal static class ProcessImage
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SystemProcessIdInformation = 88;

    /// <summary>The longest path Windows has, in characters. Its length in bytes still fits the kernel's ushort.</summary>
    private const int MaxPathChars = 32767;

    /// <summary>
    /// The program's full path, or null when no process has the ID or it runs no program file,
    /// like the kernel. Protected processes such as lsass and csrss, whose memory nothing may
    /// read, give theirs too, so a copy of some program under their name can't pass for them.
    /// </summary>
    public static string? PathOf(uint processId)
    {
        if (processId is 0 or 4)
            return null;
        return Queried(processId) ?? FromKernel(processId);
    }

    /// <summary>Asks the process, which takes only the right to query its basic information.</summary>
    internal static unsafe string? Queried(uint processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;
        var buffer = ArrayPool<char>.Shared.Rent(MaxPathChars + 1);
        try
        {
            fixed (char* chars = buffer)
            {
                int size = buffer.Length;
                return QueryFullProcessImageNameW(handle, 0, chars, ref size) ? new string(chars, 0, size) : null;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Asks the kernel, which names any process's program without opening the process, even one
    /// that keeps everyone out. It answers with a device path
    /// (\Device\HarddiskVolume3\Windows\System32\lsass.exe), turned back into a drive letter here.
    /// </summary>
    internal static unsafe string? FromKernel(uint processId)
    {
        var buffer = ArrayPool<char>.Shared.Rent(MaxPathChars);
        try
        {
            fixed (char* chars = buffer)
            {
                var info = new ProcessIdInformation
                {
                    ProcessId = (nint)processId,
                    MaximumLength = MaxPathChars * sizeof(char),
                    Buffer = (nint)chars
                };
                int status = NtQuerySystemInformation(SystemProcessIdInformation, &info, sizeof(ProcessIdInformation), out _);
                if (status != 0 || info.Length == 0)
                    return null;
                return DrivePath(new string(chars, 0, info.Length / sizeof(char)));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>The device path with its volume swapped for the drive it's mounted as, or as it came when none matches.</summary>
    internal static string DrivePath(string devicePath)
    {
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd('\\');
            if (DeviceOf(drive) is { } device
                && devicePath.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + devicePath[device.Length..];
        }
        const string network = @"\Device\Mup\";
        return devicePath.StartsWith(network, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + devicePath[network.Length..]
            : devicePath;
    }

    private static unsafe string? DeviceOf(string drive)
    {
        const int size = 1024;
        char* target = stackalloc char[size];
        int length = QueryDosDeviceW(drive, target, size);
        if (length == 0)
            return null;
        // A list of targets, each ending in a NUL. The first is the one in use.
        return new string(target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIdInformation
    {
        public nint ProcessId;
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern unsafe int QueryDosDeviceW(string deviceName, char* targetPath, int max);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, int length, out int returnLength);
}
