using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;

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

    private readonly record struct Owner(uint ProcessId, ProcessIdentity Identity);

    private readonly IFlowTracker _flowTracker;
    private readonly Func<IReadOnlyList<OpenSocket>> _readSockets;
    private readonly Func<uint, ProcessIdentity> _identify;
    private readonly Func<long> _clock;
    private readonly ConcurrentDictionary<FlowKey, (SocketKey Socket, uint ProcessId)> _adopted = new();
    private readonly object _refreshLock = new();
    private volatile Dictionary<SocketKey, Owner> _owners = new();
    private long _lastReadStarted = long.MinValue;

    /// <param name="clock">Timestamps on the clock WinDivert stamps events with (QueryPerformanceCounter).</param>
    public SocketOwners(IFlowTracker flowTracker, Func<IReadOnlyList<OpenSocket>> readSockets, Func<uint, ProcessIdentity> identify,
        Func<long>? clock = null)
    {
        _flowTracker = flowTracker;
        _readSockets = readSockets;
        _identify = identify;
        _clock = clock ?? Stopwatch.GetTimestamp;
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
        var previous = _owners;
        var known = previous.Values
            .GroupBy(owner => owner.ProcessId)
            .ToDictionary(group => group.Key, group => group.First().Identity);

        var owners = new Dictionary<SocketKey, Owner>();
        foreach (var socket in _readSockets())
        {
            var key = new SocketKey(socket.Protocol, Normalize(socket.LocalAddress), socket.LocalPort);
            if (owners.ContainsKey(key))
                continue;
            if (!known.TryGetValue(socket.ProcessId, out var identity))
            {
                identity = _identify(socket.ProcessId);
                known[socket.ProcessId] = identity;
            }
            owners[key] = new Owner(socket.ProcessId, identity);
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
    /// Drops the sockets of a process that exited. The last read can be 30 seconds old, and a
    /// late packet on one of them would adopt a flow for the process again, under its name, for
    /// whatever process Windows gives the ID to next. The next read names that process afresh
    /// instead of reusing the name it had.
    /// </summary>
    public void Forget(uint processId)
    {
        lock (_refreshLock)
        {
            var owners = _owners;
            if (owners.Values.Any(owner => owner.ProcessId == processId))
                _owners = owners.Where(socket => socket.Value.ProcessId != processId).ToDictionary();
        }
    }

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
                _flowTracker.RegisterFlow(flowKey, processId, identity.Name, identity.Path);
                return true;
            }
            if (_flowTracker.LookupConnection(flowKey) is { IsActive: true } known && known.FlowKey == flowKey
                && known.ProcessId is not (0 or SystemProcessId))
            {
                identity = default;
                return false;
            }
        }
        identity = _identify(processId);
        _flowTracker.RegisterFlow(flowKey, processId, identity.Name, identity.Path);
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
        _flowTracker.RegisterFlow(flowKey, owner.ProcessId, owner.Identity.Name, owner.Identity.Path);
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
