namespace OpenNetLimit.Core.IPC;

public static class IpcProtocol
{
    public const string PipeName = "OpenNetLimit";
    public const int ProtocolVersion = 1;

    public static readonly HashSet<string> ReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "SNAPSHOT", "RULES", "PROCESSES", "STATUS"
    };

    public static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD_RULE", "REMOVE_RULE", "UPDATE_RULE"
    };

    public static bool IsValidCommand(string command) =>
        ReadCommands.Contains(command) || WriteCommands.Contains(command);

    public static bool RequiresAdmin(string command) =>
        WriteCommands.Contains(command);
}
