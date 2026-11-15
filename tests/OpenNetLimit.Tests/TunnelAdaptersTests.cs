using System.Net.NetworkInformation;
using OpenNetLimit.Engine.Interception;
using Xunit;

namespace OpenNetLimit.Tests;

public class TunnelAdaptersTests
{
    private sealed class FakeAdapters
    {
        public List<TunnelAdapters.Adapter> Adapters { get; } = [];
        public List<Action> Queued { get; } = [];
        public long Now { get; set; }
        public int Reads { get; private set; }
        public Exception? Fail { get; set; }

        /// <summary>Runs once the read has its list, before it's published.</summary>
        public Action? DuringRead { get; set; }

        public TunnelAdapters Create() => new(() =>
        {
            Reads++;
            if (Fail is not null)
                throw Fail;
            var list = Adapters.ToList();
            DuringRead?.Invoke();
            return list;
        }, () => Now, Queued.Add);

        public void RunQueued()
        {
            var queued = Queued.ToList();
            Queued.Clear();
            foreach (var work in queued)
                work();
        }
    }

    [Theory]
    // Wintun (WireGuard, Tailscale) and wireguard-nt say what they are in their type.
    [InlineData(53, "WireGuard Tunnel", true)]
    [InlineData(53, "Tailscale Tunnel", true)]
    [InlineData(131, "Contoso Secure Link", true)]
    // Windows' IPv6 transition tunnels are tunnels by type, but nothing counts what carries them.
    [InlineData(131, "Teredo Tunneling Pseudo-Interface", false)]
    [InlineData(131, "Microsoft 6to4 Adapter", false)]
    [InlineData(131, "Microsoft ISATAP Adapter #2", false)]
    [InlineData(131, "Microsoft IP-HTTPS Platform Adapter", false)]
    // These pose as Ethernet, so their description gives them away.
    [InlineData(6, "TAP-Windows Adapter V9", true)]
    [InlineData(6, "OpenVPN Data Channel Offload", true)]
    [InlineData(6, "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", true)]
    [InlineData(6, "PANGP Virtual Ethernet Adapter Secure", true)]
    [InlineData(6, "ZeroTier Virtual Port", true)]
    [InlineData(6, "Cisco Secure Client Virtual Miniport Adapter for Windows x64", true)]
    [InlineData(6, "Zscaler Network Adapter 1.0.2.0", true)]
    [InlineData(6, "Ivanti Secure Access Client Virtual Adapter", true)]
    // Real adapters, and the switches Hyper-V and WSL add, are not.
    [InlineData(6, "Killer E3100G 2.5 Gigabit Ethernet Controller", false)]
    [InlineData(71, "Intel(R) Wi-Fi 7 BE200 320MHz", false)]
    [InlineData(6, "Hyper-V Virtual Ethernet Adapter #2", false)]
    [InlineData(6, "Cisco VIC Ethernet Interface", false)]
    [InlineData(24, "Software Loopback Interface 1", false)]
    // PPPoE is a PPP link, and on a PC that dials its own DSL it's the only way out.
    [InlineData(23, "WAN Miniport (PPPOE)", false)]
    // So is a VPN Windows dials itself (SSTP, L2TP, PPTP, IKEv2), whatever it's named. Its
    // encrypted side comes from a networking service or is ESP or GRE, none of which count,
    // so it has to count inside.
    [InlineData(23, "Contoso VPN", false)]
    [InlineData(23, "WAN Miniport (IKEv2)", false)]
    public void IsTunnel_GoesByTypeThenDescription(int type, string description, bool tunnel)
    {
        Assert.Equal(tunnel, TunnelAdapters.IsTunnel(type, description));
    }

    [Fact]
    public void TheFirstPacket_StartsAReadOffThePacketThread_AndLaterOnesUseIt()
    {
        var fake = new FakeAdapters { Now = 5_000 };
        fake.Adapters.Add(new TunnelAdapters.Adapter(11, 6, "Killer E3100G 2.5 Gigabit Ethernet Controller"));
        fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
        var adapters = fake.Create();

        Assert.False(adapters.IsTunnel(40));
        Assert.Equal(0, fake.Reads);
        Assert.Single(fake.Queued);
        fake.RunQueued();

        Assert.True(adapters.IsTunnel(40));
        Assert.False(adapters.IsTunnel(11));
        Assert.Equal(1, fake.Reads);
        Assert.Empty(fake.Queued);
    }

    [Fact]
    public void ANewAdapter_IsLookedUpAfterTwoSeconds_AndTheListIsReadAgainEveryMinute()
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(11, 6, "Killer E3100G 2.5 Gigabit Ethernet Controller"));
        var adapters = fake.Create();
        adapters.IsTunnel(11);
        fake.RunQueued();

        // A VPN connects, and its adapter is one the last read didn't have.
        fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
        fake.Now = 1_999;
        Assert.False(adapters.IsTunnel(40));
        Assert.Empty(fake.Queued);
        fake.Now = 2_000;
        Assert.False(adapters.IsTunnel(40));
        fake.RunQueued();
        Assert.True(adapters.IsTunnel(40));
        Assert.Equal(2, fake.Reads);

        // Known adapters wait for the minute, and only one read is ever on its way.
        fake.Now = 61_999;
        adapters.IsTunnel(11);
        Assert.Empty(fake.Queued);
        fake.Now = 62_000;
        adapters.IsTunnel(11);
        adapters.IsTunnel(99);
        Assert.Single(fake.Queued);
    }

    [Fact]
    public void AnInterfaceTheListNeverHas_IsLookedForOnce_ThenWaitsForTheMinute()
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(11, 6, "Killer E3100G 2.5 Gigabit Ethernet Controller"));
        var adapters = fake.Create();
        adapters.IsTunnel(11);
        fake.RunQueued();

        fake.Now = 2_000;
        Assert.False(adapters.IsTunnel(99));
        fake.RunQueued();
        Assert.Equal(2, fake.Reads);

        // Still missing after a read that looked for it, so no read every two seconds for it.
        for (fake.Now = 4_000; fake.Now < 62_000; fake.Now += 2_000)
            adapters.IsTunnel(99);
        Assert.Empty(fake.Queued);

        fake.Now = 62_000;
        adapters.IsTunnel(99);
        Assert.Single(fake.Queued);
    }

    [Fact]
    public void AnInterfaceFirstAskedAboutWhileAReadIsOnItsWay_IsLookedForByTheNextOne()
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(11, 6, "Killer E3100G 2.5 Gigabit Ethernet Controller"));
        var adapters = fake.Create();
        adapters.IsTunnel(11);
        fake.RunQueued();

        // A read starts at 2000 for another interface. After it has the list, the VPN's adapter
        // comes up and a packet names it at 2500.
        fake.Now = 2_000;
        adapters.IsTunnel(99);
        fake.DuringRead = () =>
        {
            fake.DuringRead = null;
            fake.Now = 2_500;
            fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
            Assert.False(adapters.IsTunnel(40));
        };
        fake.RunQueued();
        Assert.False(adapters.IsTunnel(40));

        // That read began before the packet named it, so it never looked, and the next one does.
        fake.Now = 4_500;
        adapters.IsTunnel(40);
        Assert.Single(fake.Queued);
        fake.RunQueued();
        Assert.True(adapters.IsTunnel(40));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedRead_KeepsTheLastList_UntilItsNextTurn(bool unexpected)
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
        var adapters = fake.Create();
        adapters.IsTunnel(40);
        fake.RunQueued();

        // Anything the read throws stays in it: on the thread pool it would end the service.
        fake.Fail = unexpected ? new InvalidOperationException("No adapters") : new NetworkInformationException();
        fake.Now = 60_000;
        adapters.IsTunnel(40);
        fake.RunQueued();

        Assert.True(adapters.IsTunnel(40));
        Assert.Empty(fake.Queued);
        Assert.Equal(2, fake.Reads);
    }

    private static byte[] Packet(int version, byte protocol, int length = 0)
    {
        var packet = new byte[Math.Max(length, version == 4 ? 20 : 40)];
        packet[0] = (byte)(version << 4);
        packet[version == 4 ? 9 : 6] = protocol;
        return packet;
    }

    [Fact]
    public void ATunnel_IsCountedOnItsCarrier_UntilThePcSendsACarrierNothingCounts()
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
        fake.Adapters.Add(new TunnelAdapters.Adapter(11, 6, "Killer E3100G 2.5 Gigabit Ethernet Controller"));
        fake.Adapters.Add(new TunnelAdapters.Adapter(30, 23, "WAN Miniport (PPPOE)"));
        var adapters = fake.Create();
        adapters.IsTunnel(40);
        fake.RunQueued();

        Assert.Equal(TunnelAdapters.Side.CountedOnItsCarrier, adapters.SideOf(40));
        Assert.Equal(TunnelAdapters.Side.Ordinary, adapters.SideOf(11));
        // A dial-up link counts where it is, and what's past it isn't the local network.
        Assert.Equal(TunnelAdapters.Side.CountsHere, adapters.SideOf(30));

        // ESP that arrives unasked can come from anywhere, one on a tunnel is inside it, and UDP
        // is counted where it crosses.
        var esp = Packet(4, 50);
        fake.Now = 5_000;
        adapters.SawUnparsed(esp, 11, outbound: false);
        adapters.SawUnparsed(esp, 40, outbound: true);
        adapters.SawUnparsed(Packet(4, 17), 11, outbound: true);
        Assert.Equal(TunnelAdapters.Side.CountedOnItsCarrier, adapters.SideOf(40));

        // An IPsec VPN's ESP leaves on the real adapter, and nothing counts it.
        adapters.SawUnparsed(esp, 11, outbound: true);
        Assert.Equal(TunnelAdapters.Side.CountsHere, adapters.SideOf(40));
        Assert.Equal(TunnelAdapters.Side.Ordinary, adapters.SideOf(11));
        Assert.Equal(TunnelAdapters.Side.CountsHere, adapters.SideOf(30));
        fake.Now = 14_999;
        Assert.Equal(TunnelAdapters.Side.CountsHere, adapters.SideOf(40));
        fake.Now = 15_000;
        Assert.Equal(TunnelAdapters.Side.CountedOnItsCarrier, adapters.SideOf(40));
    }

    [Theory]
    [InlineData(4, 50, true)]
    [InlineData(4, 51, true)]
    [InlineData(4, 47, true)]
    [InlineData(4, 41, true)]
    [InlineData(4, 4, true)]
    [InlineData(6, 50, true)]
    [InlineData(6, 4, true)]
    [InlineData(4, 6, false)]
    [InlineData(4, 17, false)]
    [InlineData(4, 1, false)]
    [InlineData(6, 58, false)]
    [InlineData(6, 17, false)]
    public void IsUncountedCarrier_GoesByTheIpProtocol(int version, byte protocol, bool carrier)
    {
        var packet = Packet(version, protocol);

        Assert.Equal(carrier, TunnelAdapters.IsUncountedCarrier(packet));
        // Cut short, it's no packet at all.
        Assert.False(TunnelAdapters.IsUncountedCarrier(packet.AsSpan(0, version == 4 ? 19 : 39)));
    }

    [Fact]
    public void IsUncountedCarrier_LooksPastIPv6ExtensionHeaders()
    {
        // Hop-by-hop options (8 bytes), then destination options (16 bytes), then ESP.
        var options = Packet(6, 0, 64);
        options[40] = 60;
        options[48] = 50;
        options[49] = 1;
        Assert.True(TunnelAdapters.IsUncountedCarrier(options));

        // A routing header, then UDP.
        var routed = Packet(6, 43, 64);
        routed[40] = 17;
        Assert.False(TunnelAdapters.IsUncountedCarrier(routed));

        // A VPN's packet too big for the path goes in fragments: the first and a later one.
        var first = Packet(6, 44, 56);
        first[40] = 47;
        Assert.True(TunnelAdapters.IsUncountedCarrier(first));
        var later = Packet(6, 44, 56);
        later[40] = 50;
        later[43] = 0xA8;
        Assert.True(TunnelAdapters.IsUncountedCarrier(later));

        // Past the first fragment, what follows the fragment header is the middle of the
        // packet, and isn't read as a header even when it looks like one.
        var middle = Packet(6, 44, 64);
        middle[40] = 60;
        middle[43] = 0xA8;
        middle[48] = 50;
        Assert.False(TunnelAdapters.IsUncountedCarrier(middle));

        // A header the packet ends inside isn't read past the end.
        Assert.False(TunnelAdapters.IsUncountedCarrier(Packet(6, 0, 41)));
        Assert.False(TunnelAdapters.IsUncountedCarrier(Packet(6, 44, 47)));
    }

    [Fact]
    public void TheRealList_HasTheLoopback_AndItIsNoTunnel()
    {
        // Once for IPv4 and once for IPv6, with the same index.
        var loopbacks = TunnelAdapters.ReadAdapters()
            .Where(adapter => adapter.Type == (int)NetworkInterfaceType.Loopback)
            .ToList();

        Assert.Contains(loopbacks, adapter => adapter.Index == 1);
        Assert.All(loopbacks, adapter => Assert.False(TunnelAdapters.IsTunnel(adapter.Type, adapter.Description)));
    }
}
