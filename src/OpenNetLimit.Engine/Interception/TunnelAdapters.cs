using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace OpenNetLimit.Engine.Interception;

/// <summary>
/// Which network interfaces are VPN tunnels. With a VPN up, an app's packets cross WinDivert
/// twice: on the tunnel as the app sent them, and again encrypted on the real adapter. The
/// whole-PC limit counts only the encrypted side, so the same bytes don't count twice.
/// The list is read off the packet thread, again a minute after the last read, or two seconds
/// after a packet names an interface it doesn't have yet (a VPN that just connected).
/// A VPN whose encrypted side can't be counted (ESP, GRE, IP in IP) has its tunnel side counted
/// instead. One whose server is on the local network counts as local traffic, like the
/// Tailscale and ZeroTier peers that talk to each other directly over it.
/// </summary>
internal sealed class TunnelAdapters
{
    /// <summary>One network interface: its index, its IANA interface type and its description.</summary>
    internal readonly record struct Adapter(uint Index, int Type, string Description);

    /// <summary>How the whole-PC limit takes a packet on an interface.</summary>
    internal enum Side
    {
        /// <summary>An ordinary adapter. The local network counts only when the limit includes it.</summary>
        Ordinary,

        /// <summary>A VPN tunnel whose encrypted side the limit counts on the real adapter, so this side doesn't.</summary>
        CountedOnItsCarrier,

        /// <summary>
        /// A tunnel whose encrypted side can't be counted right now. It counts here, and the local
        /// network only when the limit includes it, as on an ordinary adapter.
        /// </summary>
        CountsHere,

        /// <summary>
        /// A dial-up link, Windows' own VPNs included. It counts here, and a private address at the
        /// far end is out past it (a company network behind the VPN), so it isn't taken for the
        /// local network.
        /// </summary>
        PastALink
    }

    private sealed record Snapshot(FrozenSet<uint> Known, FrozenSet<uint> Tunnels, FrozenSet<uint> Links, long ReadAtMs);

    private const long RefreshEveryMs = 60_000;
    private const long UnknownWaitMs = 2_000;
    private const long UncountedCarrierCountsForMs = 10_000;

    // IANA interface types (ifType). PPP (23) is left out on purpose: PPPoE is a PPP link, and
    // on a PC that dials its own DSL it's the only way out. Windows' own VPNs (IKEv2, L2TP,
    // SSTP, PPTP) are PPP links too, which count, while their encrypted side comes from
    // networking services or is ESP or GRE, which don't.
    private const int PropVirtual = 53;
    private const int Tunnel = 131;
    private const int Ppp = 23;

    /// <summary>
    /// What VPN adapters that pose as Ethernet (TAP, AnyConnect, GlobalProtect and the like)
    /// have in their description. Wintun and WireGuard adapters say so in their type too.
    /// </summary>
    private static readonly string[] VpnMarkers =
    [
        "TAP-", "VPN", "Tunnel", "WireGuard", "Wintun", "AnyConnect", "Cisco Secure Client", "PANGP", "ZeroTier",
        "Hamachi", "Fortinet", "Juniper", "Pulse Secure", "Ivanti", "SonicWall", "Zscaler", "NordLynx", "ExpressVPN"
    ];

    /// <summary>
    /// Windows' IPv6 transition tunnels. They're tunnels by type, but nothing counts what carries
    /// them: Teredo and IP-HTTPS come from the IP Helper service, which is networking, and 6to4
    /// and ISATAP are IPv6 in IP. What crosses them counts there.
    /// </summary>
    private static readonly string[] TransitionMarkers = ["Teredo", "6to4", "ISATAP", "IP-HTTPS"];

    // What only a tunnel's encrypted side is: IPv4 in IP, IPv6 in IP, GRE, ESP and AH.
    private const byte IPv4InIP = 4;
    private const byte IPv6InIP = 41;
    private const byte Gre = 47;
    private const byte Esp = 50;
    private const byte Ah = 51;

    // IPv6 headers that can come before what a packet carries.
    private const byte HopByHop = 0;
    private const byte Routing = 43;
    private const byte Fragment = 44;
    private const byte DestinationOptions = 60;
    private const int MaxExtensionHeaders = 8;

    private readonly Func<IEnumerable<Adapter>> _read;
    private readonly Func<long> _nowMs;
    private readonly Action<Action> _run;
    private volatile Snapshot _snapshot;
    private int _reading;
    private long _uncountedCarrierAtMs;

    /// <summary>
    /// Interfaces a packet named that the list didn't have, with when that was first asked. A
    /// read that starts later looks for each one. One still missing after it (an interface the
    /// list never shows) waits for the minute like the rest.
    /// </summary>
    private readonly ConcurrentDictionary<uint, long> _lookedFor = new();

    public TunnelAdapters()
        : this(ReadAdapters, () => Environment.TickCount64,
            work => ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null))
    {
    }

    /// <param name="run">Runs a read of the adapter list. The service runs it on the thread pool.</param>
    internal TunnelAdapters(Func<IEnumerable<Adapter>> read, Func<long> nowMs, Action<Action> run)
    {
        _read = read;
        _nowMs = nowMs;
        _run = run;
        long now = nowMs();
        // Already due, so the first packet starts the first read.
        _snapshot = new Snapshot(FrozenSet<uint>.Empty, FrozenSet<uint>.Empty, FrozenSet<uint>.Empty, now - RefreshEveryMs);
        _uncountedCarrierAtMs = now - UncountedCarrierCountsForMs;
    }

    /// <summary>Whether the interface a packet crossed is a VPN tunnel, as of the last read.</summary>
    public bool IsTunnel(uint interfaceIndex)
    {
        var snapshot = _snapshot;
        long now = _nowMs();
        long sinceRead = now - snapshot.ReadAtMs;
        if (sinceRead >= RefreshEveryMs
            || (sinceRead >= UnknownWaitMs && !snapshot.Known.Contains(interfaceIndex)
                && snapshot.ReadAtMs < _lookedFor.GetOrAdd(interfaceIndex, now)))
            StartRead();
        return _snapshot.Tunnels.Contains(interfaceIndex);
    }

    /// <summary>
    /// How the whole-PC limit takes a packet on this interface. A tunnel's own side is left for
    /// its encrypted side to count, unless an encrypted side the limit can't count has left a
    /// real adapter in the last 10 seconds. Which tunnel that one carried isn't known, so while
    /// it lasts, every tunnel's own side counts.
    /// </summary>
    public Side SideOf(uint interfaceIndex)
    {
        if (IsTunnel(interfaceIndex))
            return _nowMs() - Volatile.Read(ref _uncountedCarrierAtMs) >= UncountedCarrierCountsForMs
                ? Side.CountedOnItsCarrier
                : Side.CountsHere;
        return _snapshot.Links.Contains(interfaceIndex) ? Side.PastALink : Side.Ordinary;
    }

    /// <summary>
    /// Looks at a packet nothing parsed. One this PC sends on a real adapter that carries another
    /// the way only a tunnel does is a VPN's encrypted side, which the limit can't count. Only
    /// one sent counts: a GRE or ESP packet nobody asked for can arrive from anywhere.
    /// </summary>
    public void SawUnparsed(ReadOnlySpan<byte> packet, uint interfaceIndex, bool outbound)
    {
        if (outbound && IsUncountedCarrier(packet) && !IsTunnel(interfaceIndex))
            Volatile.Write(ref _uncountedCarrierAtMs, _nowMs());
    }

    /// <summary>
    /// Whether an IP packet carries another one the way only a tunnel does, and so is nothing
    /// the whole-PC limit counts: ESP, AH, GRE, or IPv4 or IPv6 inside IP.
    /// </summary>
    internal static bool IsUncountedCarrier(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
            return false;
        int version = packet[0] >> 4;
        if (version == 4)
            return packet.Length >= 20 && IsCarrier(packet[9]);
        if (version != 6 || packet.Length < 40)
            return false;
        // IPv6 can put option, routing and fragment headers ahead of what it carries. A tunnel's
        // packet too big for the path goes in fragments, for one.
        int next = packet[6];
        int offset = 40;
        for (int i = 0; i < MaxExtensionHeaders; i++)
        {
            switch (next)
            {
                case HopByHop or Routing or DestinationOptions when packet.Length >= offset + 2:
                    next = packet[offset];
                    offset += (packet[offset + 1] + 1) * 8;
                    break;
                case Fragment when packet.Length >= offset + 8:
                    next = packet[offset];
                    // Past the first fragment, what follows is the middle of the packet.
                    if (((packet[offset + 2] << 8) | packet[offset + 3]) >> 3 != 0)
                        return IsCarrier(next);
                    offset += 8;
                    break;
                default:
                    return IsCarrier(next);
            }
        }
        return false;
    }

    private static bool IsCarrier(int protocol) => protocol is IPv4InIP or IPv6InIP or Gre or Esp or Ah;

    /// <summary>Whether an interface of this type and description is a VPN tunnel.</summary>
    internal static bool IsTunnel(int type, string description)
    {
        if (type == Ppp || HasAny(description, TransitionMarkers))
            return false;
        return type is PropVirtual or Tunnel || HasAny(description, VpnMarkers);
    }

    private static bool HasAny(string description, string[] markers)
    {
        foreach (var marker in markers)
            if (description.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void StartRead()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
            return;
        _run(() =>
        {
            try
            {
                Read();
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        });
    }

    private void Read()
    {
        long readAt = _nowMs();
        try
        {
            var known = new HashSet<uint>();
            var tunnels = new HashSet<uint>();
            var links = new HashSet<uint>();
            foreach (var adapter in _read())
            {
                known.Add(adapter.Index);
                if (IsTunnel(adapter.Type, adapter.Description))
                    tunnels.Add(adapter.Index);
                else if (adapter.Type == Ppp)
                    links.Add(adapter.Index);
            }
            _snapshot = new Snapshot(known.ToFrozenSet(), tunnels.ToFrozenSet(), links.ToFrozenSet(), readAt);
            foreach (var index in _lookedFor.Keys)
                if (known.Contains(index))
                    _lookedFor.TryRemove(index, out _);
        }
        catch (Exception ex)
        {
            // Keep the last list and try again on the usual schedule. This runs on the thread
            // pool, where anything thrown would end the service.
            Trace.TraceWarning($"Couldn't read the network adapters: {ex.Message}");
            _snapshot = _snapshot with { ReadAtMs = readAt };
        }
    }

    internal static IEnumerable<Adapter> ReadAdapters()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = adapter.GetIPProperties();
            // Both give the interface's index, but an adapter may run only one of the two.
            if (adapter.Supports(NetworkInterfaceComponent.IPv4))
                yield return new Adapter((uint)properties.GetIPv4Properties().Index, (int)adapter.NetworkInterfaceType,
                    adapter.Description);
            if (adapter.Supports(NetworkInterfaceComponent.IPv6))
                yield return new Adapter((uint)properties.GetIPv6Properties().Index, (int)adapter.NetworkInterfaceType,
                    adapter.Description);
        }
    }
}
