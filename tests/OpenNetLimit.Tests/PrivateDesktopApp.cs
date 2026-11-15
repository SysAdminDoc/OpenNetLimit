using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenNetLimit.Tests;

/// <summary>
/// Runs a GUI executable on its own hidden desktop so UI tests can drive real windows
/// without anything appearing on the interactive desktop.
/// </summary>
internal sealed class PrivateDesktopApp : IDisposable
{
    private const uint DesktopAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint WmClose = 0x0010;

    private readonly IntPtr _desktop;
    private IntPtr _process;

    public uint ProcessId { get; }

    private PrivateDesktopApp(IntPtr desktop, IntPtr process, uint processId)
    {
        _desktop = desktop;
        _process = process;
        ProcessId = processId;
    }

    public static PrivateDesktopApp Start(string executable, IReadOnlyDictionary<string, string> environment)
    {
        var desktopName = $"OpenNetLimitTest-{Guid.NewGuid():N}";
        var desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed.");

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                variables[key] = value;
        }
        foreach (var (key, value) in environment)
            variables[key] = value;
        var block = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(p => $"{p.Key}={p.Value}")) + "\0\0");

        try
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = $"winsta0\\{desktopName}" };
            if (!CreateProcess(executable, new StringBuilder($"\"{executable}\""), IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNewProcessGroup, block, Path.GetDirectoryName(executable),
                    ref startup, out var info))
            {
                var error = Marshal.GetLastWin32Error();
                CloseDesktop(desktop);
                throw new System.ComponentModel.Win32Exception(error, $"Could not start {executable}.");
            }

            CloseHandle(info.hThread);
            return new PrivateDesktopApp(desktop, info.hProcess, info.dwProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    /// <summary>Returns the visible, titled top-level windows the process owns right now.</summary>
    public IReadOnlyList<(IntPtr Handle, string Title)> Windows()
    {
        var found = new List<(IntPtr, string)>();
        EnumDesktopWindows(_desktop, (window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != ProcessId || !IsWindowVisible(window))
                return true;
            var length = GetWindowTextLength(window);
            if (length <= 0)
                return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(window, title, title.Capacity);
            found.Add((window, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Polls until a window matching <paramref name="match"/> is visible. Returns null when the
    /// process exits or the timeout passes first.
    /// </summary>
    public (IntPtr Handle, string Title)? WaitForWindow(Func<string, bool> match, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            foreach (var window in Windows())
            {
                if (match(window.Title))
                    return window;
            }
            if (HasExited)
                return null;
            Thread.Sleep(150);
        }
        return null;
    }

    public void Close(IntPtr window) => PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        if (_process != IntPtr.Zero)
        {
            if (WaitForSingleObject(_process, 0) != 0)
            {
                TerminateProcess(_process, 1);
                WaitForSingleObject(_process, 5000);
            }
            CloseHandle(_process);
            _process = IntPtr.Zero;
        }
        CloseDesktop(_desktop);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, string? device, IntPtr deviceMode, uint flags, uint desiredAccess, IntPtr attributes);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
