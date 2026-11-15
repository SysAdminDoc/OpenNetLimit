using System.Diagnostics;

namespace OpenNetLimit.Service;

/// <summary>
/// The packet engine reports loop failures through <see cref="Trace"/>. With no listener those
/// reached only an attached debugger, so a packet loop that kept failing left nothing in the
/// service log. This forwards them to the service's logger.
/// </summary>
internal sealed class LoggerTraceListener(ILogger logger) : TraceListener
{
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        logger.Log(LevelFor(eventType), "{Message}", message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
        string? format, params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id, args is null || format is null ? format : string.Format(format, args));

    public override void Write(string? message) => WriteLine(message);

    public override void WriteLine(string? message) => logger.LogInformation("{Message}", message);

    internal static LogLevel LevelFor(TraceEventType eventType) => eventType switch
    {
        TraceEventType.Critical => LogLevel.Critical,
        TraceEventType.Error => LogLevel.Error,
        TraceEventType.Warning => LogLevel.Warning,
        TraceEventType.Information => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}
