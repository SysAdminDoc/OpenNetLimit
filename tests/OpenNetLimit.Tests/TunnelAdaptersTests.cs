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
        public bool Fail { get; set; }

        public TunnelAdapters Create() => new(() =>
        {
            Reads++;
            if (Fail)
                throw new NetworkInformationException();
            return Adapters.ToList();
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
    [InlineData(131, "Teredo Tunneling Pseudo-Interface", true)]
    // These pose as Ethernet, so their description gives them away.
    [InlineData(6, "TAP-Windows Adapter V9", true)]
    [InlineData(6, "OpenVPN Data Channel Offload", true)]
    [InlineData(6, "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", true)]
    [InlineData(6, "PANGP Virtual Ethernet Adapter Secure", true)]
    [InlineData(6, "ZeroTier Virtual Port", true)]
    // Real adapters, and the switches Hyper-V and WSL add, are not.
    [InlineData(6, "Killer E3100G 2.5 Gigabit Ethernet Controller", false)]
    [InlineData(71, "Intel(R) Wi-Fi 7 BE200 320MHz", false)]
    [InlineData(6, "Hyper-V Virtual Ethernet Adapter #2", false)]
    [InlineData(24, "Software Loopback Interface 1", false)]
    // PPPoE is a PPP link, and on a PC that dials its own DSL it's the only way out.
    [InlineData(23, "WAN Miniport (PPPOE)", false)]
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
    public void AFailedRead_KeepsTheLastList_UntilItsNextTurn()
    {
        var fake = new FakeAdapters();
        fake.Adapters.Add(new TunnelAdapters.Adapter(40, 53, "WireGuard Tunnel"));
        var adapters = fake.Create();
        adapters.IsTunnel(40);
        fake.RunQueued();

        fake.Fail = true;
        fake.Now = 60_000;
        adapters.IsTunnel(40);
        fake.RunQueued();

        Assert.True(adapters.IsTunnel(40));
        Assert.Empty(fake.Queued);
        Assert.Equal(2, fake.Reads);
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
