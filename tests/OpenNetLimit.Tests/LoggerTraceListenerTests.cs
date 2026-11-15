using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenNetLimit.Service;
using Xunit;

namespace OpenNetLimit.Tests;

public class LoggerTraceListenerTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }

        public bool Has(LogLevel level, string message)
        {
            lock (Entries) return Entries.Any(e => e.Level == level && e.Message == message);
        }
    }

    [Fact]
    public void PacketLoopError_ReachesTheServiceLog()
    {
        // The engine reports a failing packet loop with Trace.TraceError. With no listener the
        // message reached only an attached debugger, so the service log never showed it.
        var logger = new CapturingLogger();
        var listener = new LoggerTraceListener(logger);
        var message = $"NetworkLoop error (1): test {Guid.NewGuid():N}";
        Trace.Listeners.Add(listener);
        try
        {
            Trace.TraceError(message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.True(logger.Has(LogLevel.Error, message));
    }

    [Fact]
    public void FormattedTrace_IsFormattedBeforeLogging()
    {
        var logger = new CapturingLogger();
        var listener = new LoggerTraceListener(logger);

        listener.TraceEvent(null, "test", TraceEventType.Warning, 0, "failed {0} times", 3);

        Assert.True(logger.Has(LogLevel.Warning, "failed 3 times"));
    }

    [Theory]
    [InlineData(TraceEventType.Critical, LogLevel.Critical)]
    [InlineData(TraceEventType.Error, LogLevel.Error)]
    [InlineData(TraceEventType.Warning, LogLevel.Warning)]
    [InlineData(TraceEventType.Information, LogLevel.Information)]
    [InlineData(TraceEventType.Verbose, LogLevel.Debug)]
    public void TraceLevel_MapsToTheMatchingLogLevel(TraceEventType eventType, LogLevel expected)
    {
        Assert.Equal(expected, LoggerTraceListener.LevelFor(eventType));
    }
}
