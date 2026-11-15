namespace OpenNetLimit.Core;

public static class ProcessNames
{
    /// <summary>
    /// The engine names processes the way Windows does, without ".exe" ("steam"). Rules and alerts
    /// store names in that form, so "steam.exe" typed in the CLI, the REST API or an import matches.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (name is null)
            return null;

        var trimmed = name.Trim();
        return trimmed.Length > 4 && trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
