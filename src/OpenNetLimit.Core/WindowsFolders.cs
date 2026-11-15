namespace OpenNetLimit.Core;

/// <summary>The folders Windows runs its own programs from, which only administrators and Windows can write to.</summary>
public static class WindowsFolders
{
    private static readonly string[] Folders = Read();

    /// <summary>
    /// Whether the program at <paramref name="programPath"/> sits directly in System32 or SysWOW64.
    /// Anyone can copy a program to lsass.exe or svchost.exe somewhere else, so a Windows name
    /// means Windows' own program only there. A path with no folder isn't in either.
    /// </summary>
    public static bool Contain(string? programPath) => In(programPath, Folders);

    /// <summary>
    /// Whether a path someone wrote in a rule names a program in System32 or SysWOW64, however
    /// it's spelled: as <see cref="Contain"/> takes it, through Sysnative, the name 32-bit
    /// programs see System32 by, or through the \Device\ name of a volume, which the service
    /// never gives a program on a drive. That's for reading rules only. No program runs from
    /// Sysnative, and a real folder someone makes by that name isn't Windows'.
    /// </summary>
    public static bool NamedIn(string? rulePath)
    {
        if (In(rulePath, Folders) || In(rulePath, Sysnative))
            return true;
        const string device = @"\Device\";
        var path = rulePath?.Trim().Trim('"');
        if (path is null || !path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            return false;
        // \Device\HarddiskVolume3\Windows\System32\svchost.exe, read as on Windows' own drive.
        var volumeEnd = path.IndexOf('\\', device.Length);
        if (volumeEnd < 0)
            return false;
        var onWindowsDrive = Path.Combine(Path.GetPathRoot(Folders[0])!, path[(volumeEnd + 1)..]);
        return In(onWindowsDrive, Folders) || In(onWindowsDrive, Sysnative);
    }

    private static readonly string[] Sysnative = [Path.Combine(Path.GetDirectoryName(Folders[0])!, "Sysnative")];

    private static bool In(string? programPath, string[] folders)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return false;
        var folder = Path.GetDirectoryName(programPath.Trim().Trim('"'));
        if (string.IsNullOrEmpty(folder))
            return false;
        foreach (var windowsFolder in folders)
        {
            if (string.Equals(folder, windowsFolder, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string[] Read()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
            windows = Path.GetDirectoryName(Environment.SystemDirectory) ?? @"C:\Windows";
        return [Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64")];
    }
}
