using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenNetLimit.MarketingCapture;

internal static class Program
{
    private const uint DesktopAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint WmClose = 0x0010;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint WaitTimeout = 0x00000102;

    private static readonly (string View, string FileName)[] Views =
    [
        ("setup", "01-first-run.png"),
        ("live", "02-live-traffic.png"),
        ("history", "03-bandwidth-history.png"),
        ("limit", "04-set-limit.png"),
        ("live-light", "05-light-theme.png")
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var executable = Path.GetFullPath(options["app"]);
            var output = Path.GetFullPath(options["output"]);
            if (!File.Exists(executable))
                throw new FileNotFoundException("The OpenNetLimit executable was not found.", executable);

            Directory.CreateDirectory(output);
            SetProcessDpiAwarenessContext(new IntPtr(-4));

            var results = new List<CaptureResult>();
            foreach (var (view, fileName) in Views)
            {
                var destination = Path.Combine(output, fileName);
                results.Add(CaptureView(executable, output, view, destination));
            }

            var reportPath = Path.Combine(output, "capture-report.json");
            File.WriteAllText(
                reportPath,
                JsonSerializer.Serialize(
                    new { executable = Path.GetFileName(executable), isolatedDesktop = true, screenshots = results },
                    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Console.WriteLine(JsonSerializer.Serialize(new { output, count = results.Count, screenshots = results }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Usage: OpenNetLimit.MarketingCapture --app <exe> --output <directory>");
            options[args[index][2..]] = args[++index];
        }

        if (!options.ContainsKey("app") || !options.ContainsKey("output"))
            throw new ArgumentException("Usage: OpenNetLimit.MarketingCapture --app <exe> --output <directory>");
        return options;
    }

    private static CaptureResult CaptureView(string executable, string output, string view, string destination)
    {
        var desktopName = $"OpenNetLimitCapture-{Guid.NewGuid():N}";
        var desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create an isolated desktop.");

        PROCESS_INFORMATION processInfo = default;
        IntPtr environmentBlock = IntPtr.Zero;
        try
        {
            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = $"winsta0\\{desktopName}"
            };
            var commandLine = new StringBuilder($"\"{executable}\"");
            environmentBlock = BuildEnvironmentBlock(view);
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateUnicodeEnvironment | CreateNewProcessGroup,
                    environmentBlock,
                    Path.GetDirectoryName(executable),
                    ref startup,
                    out processInfo))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not start OpenNetLimit on the isolated desktop.");
            }

            CloseHandle(processInfo.hThread);
            processInfo.hThread = IntPtr.Zero;
            var window = WaitForWindow(desktop, processInfo.dwProcessId, TimeSpan.FromSeconds(45));
            Thread.Sleep(view is "live" or "live-light" or "history" ? 2600 : 1300);

            var (width, height, colorCount) = CaptureWindow(window.Handle, destination);
            if (colorCount < 16)
                throw new InvalidOperationException($"The {view} capture appears blank or unrendered.");

            PostMessage(window.Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
            if (WaitForSingleObject(processInfo.hProcess, 5000) == WaitTimeout)
                TerminateProcess(processInfo.hProcess, 0);

            return new CaptureResult(view, Path.GetFileName(destination), window.Title, width, height, colorCount);
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
            {
                if (WaitForSingleObject(processInfo.hProcess, 0) == WaitTimeout)
                    TerminateProcess(processInfo.hProcess, 1);
                CloseHandle(processInfo.hProcess);
            }
            CloseDesktop(desktop);
        }
    }

    private static IntPtr BuildEnvironmentBlock(string view)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                variables[key] = value;
        }
        variables["OPENNETLIMIT_CAPTURE_MODE"] = "1";
        variables["OPENNETLIMIT_CAPTURE_VIEW"] = view;
        variables["OPENNETLIMIT_UI_CULTURE"] = "en-US";

        var block = string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static WindowInfo WaitForWindow(IntPtr desktop, uint processId, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            WindowInfo? found = null;
            EnumDesktopWindows(desktop, (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != processId || !IsWindowVisible(window))
                    return true;
                var length = GetWindowTextLength(window);
                if (length <= 0)
                    return true;
                var title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);
                found = new WindowInfo(window, title.ToString());
                return false;
            }, IntPtr.Zero);
            if (found is not null)
                return found;
            Thread.Sleep(150);
        }
        throw new TimeoutException("OpenNetLimit did not create a visible window on the isolated desktop.");
    }

    private static (int Width, int Height, int ColorCount) CaptureWindow(IntPtr window, string destination)
    {
        if (!GetWindowRect(window, out var rect))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 400 || height < 250)
            throw new InvalidOperationException($"Unexpected capture size {width}x{height}.");

        var windowDc = GetWindowDC(window);
        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmapHandle = CreateCompatibleBitmap(windowDc, width, height);
        var previous = SelectObject(memoryDc, bitmapHandle);
        try
        {
            if (!PrintWindow(window, memoryDc, PwRenderFullContent))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow failed.");
            using var image = Image.FromHbitmap(bitmapHandle);
            using var bitmap = new Bitmap(image);
            var colors = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 28))
            {
                for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
                    colors.Add(bitmap.GetPixel(x, y).ToArgb());
            }
            bitmap.Save(destination, ImageFormat.Png);
            return (width, height, colors.Count);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(window, windowDc);
        }
    }

    private sealed record WindowInfo(IntPtr Handle, string Title);
    private sealed record CaptureResult(string View, string File, string Title, int Width, int Height, int SampledColors);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, string? device, IntPtr deviceMode, uint flags, uint desiredAccess, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
