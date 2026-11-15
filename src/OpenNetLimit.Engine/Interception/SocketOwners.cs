using System.Collections.Concurrent;
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
    private readonly record struct SocketKey(TransportProtocol Protocol, IPAddress Address, ushort Port);

    private readonly record struct Owner(uint ProcessId, ProcessIdentity Identity);

    private readonly IFlowTracker _flowTracker;
    private readonly Func<IReadOnlyList<OpenSocket>> _readSockets;
    private readonly Func<uint, ProcessIdentity> _identify;
    private readonly ConcurrentDictionary<FlowKey, (SocketKey Socket, uint ProcessId)> _adopted = new();
    private readonly object _refreshLock = new();
    private volatile Dictionary<SocketKey, Owner> _owners = new();

    public SocketOwners(IFlowTracker flowTracker, Func<IReadOnlyList<OpenSocket>> readSockets, Func<uint, ProcessIdentity> identify)
    {
        _flowTracker = flowTracker;
        _readSockets = readSockets;
        _identify = identify;
    }

    public int AdoptedFlowCount => _adopted.Count;

    /// <summary>
    /// Reads the socket tables again and ends adopted flows whose socket closed or changed hands.
    /// The flow layer never reports the end of a flow it didn't see start, so this is how they close.
    /// </summary>
    public void Refresh()
    {
        lock (_refreshLock)
        {
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
