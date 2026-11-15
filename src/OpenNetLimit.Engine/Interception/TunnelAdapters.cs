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
/// </summary>
internal sealed class TunnelAdapters
{
    /// <summary>One network interface: its index, its IANA interface type and its description.</summary>
    internal readonly record struct Adapter(uint Index, int Type, string Description);

    private sealed record Snapshot(FrozenSet<uint> Known, FrozenSet<uint> Tunnels, long ReadAtMs);

    private const long RefreshEveryMs = 60_000;
    private const long UnknownWaitMs = 2_000;

    // IANA interface types (ifType). PPP (23) is left out on purpose: PPPoE is a PPP link, and
    // on a PC that dials its own DSL it's the only way out.
    private const int PropVirtual = 53;
    private const int Tunnel = 131;
    private const int Ppp = 23;

    /// <summary>
    /// What VPN adapters that pose as Ethernet (TAP, AnyConnect, GlobalProtect and the like)
    /// have in their description. Wintun and WireGuard adapters say so in their type too.
    /// </summary>
    private static readonly string[] VpnMarkers =
    [
        "TAP-", "VPN", "Tunnel", "WireGuard", "Wintun", "AnyConnect", "PANGP", "ZeroTier", "Hamachi",
        "Fortinet", "Juniper", "Pulse Secure", "SonicWall"
    ];

    private readonly Func<IEnumerable<Adapter>> _read;
    private readonly Func<long> _nowMs;
    private readonly Action<Action> _run;
    private volatile Snapshot _snapshot;
    private int _reading;

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
        // Already due, so the first packet starts the first read.
        _snapshot = new Snapshot(FrozenSet<uint>.Empty, FrozenSet<uint>.Empty, nowMs() - RefreshEveryMs);
    }

    /// <summary>Whether the interface a packet crossed is a VPN tunnel, as of the last read.</summary>
    public bool IsTunnel(uint interfaceIndex)
    {
        var snapshot = _snapshot;
        long sinceRead = _nowMs() - snapshot.ReadAtMs;
        if (sinceRead >= RefreshEveryMs
            || (sinceRead >= UnknownWaitMs && !snapshot.Known.Contains(interfaceIndex)))
            StartRead();
        return _snapshot.Tunnels.Contains(interfaceIndex);
    }

    /// <summary>Whether an interface of this type and description is a VPN tunnel.</summary>
    internal static bool IsTunnel(int type, string description)
    {
        if (type is PropVirtual or Tunnel)
            return true;
        if (type == Ppp)
            return false;
        foreach (var marker in VpnMarkers)
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
            foreach (var adapter in _read())
            {
                known.Add(adapter.Index);
                if (IsTunnel(adapter.Type, adapter.Description))
                    tunnels.Add(adapter.Index);
            }
            _snapshot = new Snapshot(known.ToFrozenSet(), tunnels.ToFrozenSet(), readAt);
        }
        catch (NetworkInformationException ex)
        {
            // Keep the last list and try again on the usual schedule.
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
