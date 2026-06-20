namespace OpenNetLimit.Core.IPC;

public static class IpcProtocol
{
    public const string PipeName = "OpenNetLimit";
    public const int ProtocolVersion = 1;

    public static readonly HashSet<string> ReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "SNAPSHOT", "RULES", "PROCESSES", "STATUS", "CONNECTION_LOG", "EXPORT_RULES",
        "STATS_HOURLY", "STATS_DAILY", "STATS_TOP", "QUOTAS", "ALERT_RULES", "ALERT_EVENTS"
    };

    public static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD_RULE", "REMOVE_RULE", "UPDATE_RULE", "IMPORT_RULES", "VERIFY_PROCESS", "GEOIP",
        "ADD_ALERT_RULE", "UPDATE_ALERT_RULE", "REMOVE_ALERT_RULE"
    };

    public static bool IsValidCommand(string command) =>
        ReadCommands.Contains(command) || WriteCommands.Contains(command);

    public static bool RequiresAdmin(string command) =>
        WriteCommands.Contains(command);
}
