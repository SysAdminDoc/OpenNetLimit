using System.IO;
using System.IO.Pipes;
using System.Text;
using OpenNetLimit.Core.IPC;

namespace OpenNetLimit.UI.Services;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class PipeClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        LastError = null;

        try
        {
            Disconnect();

            _pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut);
            await _pipe.ConnectAsync(2000, ct);

            _reader = new StreamReader(_pipe, Encoding.UTF8);
            _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };

            State = ConnectionState.Connected;
            return true;
        }
        catch (TimeoutException)
        {
            LastError = "Service not running";
            State = ConnectionState.Disconnected;
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = ConnectionState.Error;
            return false;
        }
    }

    public async Task<string?> SendCommandAsync(string command)
    {
        await _sendLock.WaitAsync();
        try
        {
            StreamWriter? writer;
            StreamReader? reader;

            lock (_lock)
            {
                if (_pipe is null || !_pipe.IsConnected || _writer is null || _reader is null)
                {
                    State = ConnectionState.Disconnected;
                    return null;
                }

                // Capture references under lock to prevent Disconnect() from
                // nulling them between check and use
                writer = _writer;
                reader = _reader;
            }

            await writer.WriteLineAsync(command);
            var response = await reader.ReadLineAsync();
            return response;
        }
        catch
        {
            State = ConnectionState.Disconnected;
            Disconnect();
            return null;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _reader = null;
            _pipe = null;
        }
        State = ConnectionState.Disconnected;
    }

    public void Dispose() => Disconnect();
}
