using System.Collections.Concurrent;
using SharpDivert;

namespace OpenNetLimit.Engine.RateLimiting;

public sealed class PacketScheduler : IDisposable
{
    private readonly ConcurrentDictionary<uint, ProcessPacketQueue> _queues = new();
    private readonly Timer _drainTimer;
    private readonly object _sendLock = new();
    private WinDivert? _handle;
    private int _disposed;

    public const int MaxQueuePerProcess = 512;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private long _totalDelayed;
    private long _totalDropped;
    private long _totalSent;

    public long TotalDelayed => Volatile.Read(ref _totalDelayed);
    public long TotalDropped => Volatile.Read(ref _totalDropped);
    public long TotalSent => Volatile.Read(ref _totalSent);

    public PacketScheduler()
    {
        _drainTimer = new Timer(DrainReady, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetHandle(WinDivert handle)
    {
        _handle = handle;
        _drainTimer.Change(1, 1);
    }

    public void Enqueue(uint processId, ReadOnlySpan<byte> packetData, ReadOnlySpan<WinDivertAddress> addr, TimeSpan delay)
    {
        if (delay > MaxDelay)
        {
            Interlocked.Increment(ref _totalDropped);
            return;
        }

        var queue = _queues.GetOrAdd(processId, _ => new ProcessPacketQueue(MaxQueuePerProcess));
        Volatile.Write(ref queue.LastActivityTicks, Environment.TickCount64);

        var packet = new QueuedPacket(
            packetData.ToArray(),
            addr[0],
            Environment.TickCount64 + (long)delay.TotalMilliseconds);

        if (!queue.TryEnqueue(packet))
        {
            Interlocked.Increment(ref _totalDropped);
            return;
        }

        Interlocked.Increment(ref _totalDelayed);
    }

    private void DrainReady(object? state)
    {
        if (_handle is null || Volatile.Read(ref _disposed) != 0)
            return;

        if (!Monitor.TryEnter(_sendLock))
            return;

        try
        {
            long now = Environment.TickCount64;

            Span<WinDivertAddress> addrSpan = stackalloc WinDivertAddress[1];
            foreach (var (pid, queue) in _queues)
            {
                while (queue.TryPeek(out var pkt) && pkt.SendAtTicks <= now)
                {
                    if (queue.TryDequeue(out pkt))
                    {
                        try
                        {
                            addrSpan[0] = pkt.Address;
                            _handle.SendEx(pkt.Data, addrSpan);
                            Interlocked.Increment(ref _totalSent);
                        }
                        catch
                        {
                            Interlocked.Increment(ref _totalDropped);
                        }
                    }
                }

                // Prune empty queues idle for >5 minutes
                if (queue.Count == 0 && now - Volatile.Read(ref queue.LastActivityTicks) > 300_000)
                    _queues.TryRemove(pid, out _);
            }
        }
        finally
        {
            Monitor.Exit(_sendLock);
        }
    }

    public void RemoveProcess(uint processId)
    {
        _queues.TryRemove(processId, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _drainTimer.Dispose();

        lock (_sendLock)
        {
            if (_handle is not null)
            {
                Span<WinDivertAddress> flushAddr = stackalloc WinDivertAddress[1];
                foreach (var (_, queue) in _queues)
                {
                    while (queue.TryDequeue(out var pkt))
                    {
                        try
                        {
                            flushAddr[0] = pkt.Address;
                            _handle.SendEx(pkt.Data, flushAddr);
                        }
                        catch { }
                    }
                }
            }
        }

        _queues.Clear();
    }
}

internal readonly struct QueuedPacket
{
    public readonly byte[] Data;
    public readonly WinDivertAddress Address;
    public readonly long SendAtTicks;

    public QueuedPacket(byte[] data, WinDivertAddress address, long sendAtTicks)
    {
        Data = data;
        Address = address;
        SendAtTicks = sendAtTicks;
    }
}

internal sealed class ProcessPacketQueue
{
    private readonly object _lock = new();
    private readonly Queue<QueuedPacket> _queue = new();
    private readonly int _maxSize;
    public long LastActivityTicks = Environment.TickCount64;

    public ProcessPacketQueue(int maxSize)
    {
        _maxSize = maxSize;
    }

    public bool TryEnqueue(QueuedPacket packet)
    {
        lock (_lock)
        {
            if (_queue.Count >= _maxSize)
                return false;
            _queue.Enqueue(packet);
            return true;
        }
    }

    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    public bool TryPeek(out QueuedPacket packet)
    {
        lock (_lock)
        {
            return _queue.TryPeek(out packet);
        }
    }

    public bool TryDequeue(out QueuedPacket packet)
    {
        lock (_lock)
        {
            return _queue.TryDequeue(out packet);
        }
    }
}
