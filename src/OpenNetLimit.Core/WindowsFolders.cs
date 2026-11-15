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
    public static bool Contain(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return false;
        var folder = Path.GetDirectoryName(programPath.Trim().Trim('"'));
        if (string.IsNullOrEmpty(folder))
            return false;
        foreach (var windowsFolder in Folders)
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
