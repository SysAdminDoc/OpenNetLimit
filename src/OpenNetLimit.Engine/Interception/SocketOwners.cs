using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Monitoring;

namespace OpenNetLimit.Engine.Interception;

internal readonly record struct ProcessIdentity(string Name, string? Path);

/// <summary>
/// WinDivert's flow layer only reports flows established after the handle opens. Traffic on
/// connections an app already had open (a download in progress, a browser's kept-alive
/// connections, QUIC sockets) matched no flow, so the app never showed up and its limit never
/// applied. This keeps the owners of open sockets from the OS tables and adopts a flow for the
/// owner the first time a packet on an unknown flow passes through one of its sockets.
/// </summary>
internal sealed class SocketOwners
{
    internal const uint SystemProcessId = 4;

    private readonly record struct SocketKey(TransportProtocol Protocol, IPAddress Address, ushort Port);

    /// <param name="StartedAt">When the process started, to tell it from a later one with its ID.</param>
    /// <param name="IdentifiedAt">
    /// When it counts as named: the read's start, or the process's start if that's later. Never
    /// well past the clock when it was named, so a clock set back can't put it out of a forget's reach.
    /// </param>
    private readonly record struct Owner(uint ProcessId, ProcessIdentity Identity, long? StartedAt, DateTime IdentifiedAt);

    private readonly IFlowTracker _flowTracker;
    private readonly Func<IReadOnlyList<OpenSocket>> _readSockets;
    private readonly Func<uint, ProcessIdentity> _identify;
    private readonly Func<uint, long?> _startedAt;
    private readonly Func<long> _clock;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<FlowKey, (SocketKey Socket, uint ProcessId)> _adopted = new();
    private readonly object _refreshLock = new();
    private volatile Dictionary<SocketKey, Owner> _owners = new();
    private long _lastReadStarted = long.MinValue;

    /// <param name="startedAt">When the process with an ID started, or null when that can't be read.</param>
    /// <param name="clock">Timestamps on the clock WinDivert stamps events with (QueryPerformanceCounter).</param>
    /// <param name="utcNow">The clock the flow tracker and the traffic monitor go by.</param>
    public SocketOwners(IFlowTracker flowTracker, Func<IReadOnlyList<OpenSocket>> readSockets, Func<uint, ProcessIdentity> identify,
        Func<uint, long?> startedAt, Func<long>? clock = null, Func<DateTime>? utcNow = null)
    {
        _flowTracker = flowTracker;
        _readSockets = readSockets;
        _identify = identify;
        _startedAt = startedAt;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        flowTracker.ForgettingProcess += Forget;
    }

    public int AdoptedFlowCount => _adopted.Count;

    /// <summary>
    /// Reads the socket tables again and ends adopted flows whose socket closed or changed hands.
    /// The flow layer never reports the end of a flow it didn't see start, so this is how they close.
    /// </summary>
    public void Refresh()
    {
        lock (_refreshLock)
            RefreshLocked();
    }

    private void RefreshIfOlderThan(long timestamp)
    {
        if (Volatile.Read(ref _lastReadStarted) > timestamp)
            return;
        lock (_refreshLock)
        {
            if (_lastReadStarted <= timestamp)
                RefreshLocked();
        }
    }

    private void RefreshLocked()
    {
        Volatile.Write(ref _lastReadStarted, _clock());
        var readAt = _utcNow();
        var known = _owners.Values
            .GroupBy(owner => owner.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());

        var named = new Dictionary<uint, Owner>();
        var owners = new Dictionary<SocketKey, Owner>();
        foreach (var socket in _readSockets())
        {
            var key = new SocketKey(socket.Protocol, Normalize(socket.LocalAddress), socket.LocalPort);
            if (owners.ContainsKey(key))
                continue;
            if (!named.TryGetValue(socket.ProcessId, out var owner))
                named[socket.ProcessId] = owner = Name(socket.ProcessId, known, readAt);
            owners[key] = owner;
        }
        _owners = owners;

        foreach (var (flowKey, adopted) in _adopted)
        {
            if (owners.TryGetValue(adopted.Socket, out var owner) && owner.ProcessId == adopted.ProcessId)
                continue;
            _flowTracker.UnregisterFlow(flowKey);
            _adopted.TryRemove(flowKey, out _);
        }
    }

    /// <summary>
    /// The name the last read gave the ID, while the process that has it started when that one
    /// did. A process that exits with only a listening socket never sends a packet, so nothing
    /// tells this to forget it, and Windows can give its ID to another program meanwhile.
    /// </summary>
    private Owner Name(uint processId, Dictionary<uint, Owner> known, DateTime readAt)
    {
        // Read before the name, so a process that takes the ID in between is named again next time.
        long? startedAt = _startedAt(processId);
        var now = _utcNow();
        if (startedAt is not null && known.TryGetValue(processId, out var last) && last.StartedAt == startedAt)
            return last with { IdentifiedAt = NoLaterThan(readAt, now) };
        return new Owner(processId, _identify(processId), startedAt, NamedAt(readAt, startedAt, now));
    }

    /// <summary>
    /// When a new owner counts as named. The read's start, unless the process started after it
    /// (while the tables were being read): a name is never older than its process, or the next
    /// process check would take it for an earlier one that had the ID.
    /// </summary>
    private static DateTime NamedAt(DateTime readAt, long? startedAt, DateTime now)
    {
        var named = readAt;
        if (startedAt is { } started && started > 0)
        {
            var startedUtc = DateTime.FromFileTimeUtc(started) + HeldProcess.StartStampLag;
            if (startedUtc > named)
                named = startedUtc;
        }
        return NoLaterThan(named, now);
    }

    /// <summary>
    /// A start or a read stamped well past the clock happened before the clock was set back. A
    /// name that far ahead would outlast every forget until the clock caught up, so it's named now.
    /// </summary>
    private static DateTime NoLaterThan(DateTime named, DateTime now) =>
        named - now > HeldProcess.MostAheadOfTheClock ? now : named;

    /// <summary>
    /// Drops the sockets of a process that exited. The last read can be 30 seconds old, and a
    /// late packet on one of them would adopt a flow for the process again, under its name, for
    /// whatever process Windows gives the ID to next. The next read names that process afresh
    /// instead of reusing the name it had. Sockets a read found after <paramref name="identifiedBefore"/>
    /// are the next process's, and stay.
    /// </summary>
    public void Forget(uint processId, DateTime? identifiedBefore = null)
    {
        lock (_refreshLock)
        {
            var owners = _owners;
            if (owners.Values.Any(owner => Stale(owner, processId, identifiedBefore)))
                _owners = owners.Where(socket => !Stale(socket.Value, processId, identifiedBefore)).ToDictionary();
        }
    }

    private static bool Stale(Owner owner, uint processId, DateTime? identifiedBefore) =>
        owner.ProcessId == processId && (identifiedBefore is null || owner.IdentifiedAt < identifiedBefore);

    /// <summary>
    /// Registers a flow the flow layer reported as established and says who it went to.
    /// The flow layer credits many flows to System (PID 4): every connection that was already
    /// open when the handle opened, and plenty of new ones, apps' HTTPS included, sometimes next
    /// to a second report with the real PID in either order. Those apps then never showed up and
    /// never matched their rules. A System report is checked against the socket tables, read
    /// again when they are older than the event, and one that still points nowhere never replaces
    /// a real owner (the call returns false and registers nothing).
    /// </summary>
    public bool RegisterEstablished(in FlowKey flowKey, uint reportedProcessId, long eventTimestamp,
        out uint processId, out ProcessIdentity identity)
    {
        processId = reportedProcessId;
        if (reportedProcessId == SystemProcessId)
        {
            if (TryFindRealOwner(flowKey, eventTimestamp, out var owner))
            {
                processId = owner.ProcessId;
                identity = owner.Identity;
                _flowTracker.RegisterFlow(flowKey, processId, identity.Name, identity.Path, owner.IdentifiedAt);
                return true;
            }
            if (_flowTracker.LookupConnection(flowKey) is { IsActive: true } known && known.FlowKey == flowKey
                && known.ProcessId is not (0 or SystemProcessId))
            {
                identity = default;
                return false;
            }
        }
        var identifiedAt = _utcNow();
        identity = _identify(processId);
        _flowTracker.RegisterFlow(flowKey, processId, identity.Name, identity.Path, identifiedAt);
        return true;
    }

    private bool TryFindRealOwner(in FlowKey flowKey, long eventTimestamp, out Owner owner)
    {
        if (!TryFindOwner(flowKey.Protocol, flowKey.LocalAddress, flowKey.LocalPort, out _, out owner))
        {
            RefreshIfOlderThan(eventTimestamp);
            if (!TryFindOwner(flowKey.Protocol, flowKey.LocalAddress, flowKey.LocalPort, out _, out owner))
                return false;
        }
        return owner.ProcessId != SystemProcessId;
    }

    /// <summary>
    /// Registers a flow for a packet the tracker doesn't know when its local socket has a known
    /// owner. Returns false when no open socket matches.
    /// </summary>
    public bool TryAdopt(in FlowKey packetKey, bool isOutbound)
    {
        var ends = WinDivertInterceptor.PacketEndpoints.From(packetKey, isOutbound);
        if (!TryFindOwner(packetKey.Protocol, ends.LocalAddress, (ushort)ends.LocalPort, out var socket, out var owner))
            return false;

        var flowKey = new FlowKey(packetKey.Protocol, ends.LocalAddress, (ushort)ends.LocalPort,
            ends.RemoteAddress, (ushort)ends.RemotePort);
        // Named as of the read, which can be 30 seconds old. When the process exited since and
        // its ID went to another, the flow goes with it.
        _flowTracker.RegisterFlow(flowKey, owner.ProcessId, owner.Identity.Name, owner.Identity.Path, owner.IdentifiedAt);
        _adopted[flowKey] = (socket, owner.ProcessId);
        return true;
    }

    /// <summary>
    /// Tries the exact address first, then the wildcard a socket bound to every address
    /// reports, then the IPv6 wildcard of a dual-stack socket that also carries IPv4.
    /// </summary>
    private bool TryFindOwner(TransportProtocol protocol, IPAddress address, ushort port, out SocketKey socket, out Owner owner)
    {
        var owners = _owners;
        socket = new SocketKey(protocol, address, port);
        if (owners.TryGetValue(socket, out owner))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            socket = new SocketKey(protocol, IPAddress.Any, port);
            if (owners.TryGetValue(socket, out owner))
                return true;
        }

        socket = new SocketKey(protocol, IPAddress.IPv6Any, port);
        return owners.TryGetValue(socket, out owner);
    }

    /// <summary>Dual-stack sockets list IPv4 peers as IPv4-mapped IPv6; packets carry plain IPv4.</summary>
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
