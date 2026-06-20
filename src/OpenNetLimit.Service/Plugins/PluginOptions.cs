namespace OpenNetLimit.Service.Plugins;

public sealed class PluginOptions
{
    public bool Enabled { get; init; }
    public string PluginDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenNetLimit",
        "plugins");

    public static PluginOptions FromEnvironment()
    {
        var dir = Environment.GetEnvironmentVariable("OPENNETLIMIT_PLUGIN_DIR");
        return new PluginOptions
        {
            Enabled = IsEnabled(Environment.GetEnvironmentVariable("OPENNETLIMIT_PLUGINS_ENABLED")),
            PluginDirectory = string.IsNullOrWhiteSpace(dir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenNetLimit", "plugins")
                : dir
        };
    }

    private static bool IsEnabled(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
