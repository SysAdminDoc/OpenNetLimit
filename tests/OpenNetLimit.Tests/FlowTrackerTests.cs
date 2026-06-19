using System.Net;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;

namespace OpenNetLimit.Tests;

public class FlowTrackerTests
{
    private static FlowKey MakeFlowKey(ushort localPort = 12345, ushort remotePort = 80) =>
        new(TransportProtocol.Tcp,
            IPAddress.Parse("192.168.1.100"), localPort,
            IPAddress.Parse("93.184.216.34"), remotePort);

    [Fact]
    public void RegisterAndLookup_ReturnsProcessId()
    {
        var tracker = new FlowTracker();
        var key = MakeFlowKey();

        tracker.RegisterFlow(key, 1234, "chrome", @"C:\chrome.exe");

        Assert.Equal((uint)1234, tracker.LookupProcessId(key));
    }

    [Fact]
    public void LookupReversedKey_ReturnsProcessId()
    {
        var tracker = new FlowTracker();
        var key = MakeFlowKey();
        tracker.RegisterFlow(key, 1234, "chrome", null);

        var reversed = new FlowKey(
            TransportProtocol.Tcp,
            IPAddress.Parse("93.184.216.34"), 80,
            IPAddress.Parse("192.168.1.100"), 12345);

        Assert.Equal((uint)1234, tracker.LookupProcessId(reversed));
    }

    [Fact]
    public void UnknownFlow_ReturnsNull()
    {
        var tracker = new FlowTracker();
        Assert.Null(tracker.LookupProcessId(MakeFlowKey()));
    }

    [Fact]
    public void UnregisterFlow_MarksAsClosed()
    {
        var tracker = new FlowTracker();
        var key = MakeFlowKey();
        tracker.RegisterFlow(key, 1234, "chrome", null);

        tracker.UnregisterFlow(key);

        var conn = tracker.LookupConnection(key);
        Assert.NotNull(conn);
        Assert.False(conn.IsActive);
    }

    [Fact]
    public void GetActiveConnections_ExcludesClosed()
    {
        var tracker = new FlowTracker();
        var key1 = MakeFlowKey(12345);
        var key2 = MakeFlowKey(12346);

        tracker.RegisterFlow(key1, 1, "a", null);
        tracker.RegisterFlow(key2, 2, "b", null);
        tracker.UnregisterFlow(key1);

        var active = tracker.GetActiveConnections();
        Assert.Single(active);
        Assert.Equal((uint)2, active[0].ProcessId);
    }

    [Fact]
    public void GetConnectionsByProcess_FiltersCorrectly()
    {
        var tracker = new FlowTracker();
        tracker.RegisterFlow(MakeFlowKey(100), 1, "a", null);
        tracker.RegisterFlow(MakeFlowKey(101), 1, "a", null);
        tracker.RegisterFlow(MakeFlowKey(102), 2, "b", null);

        var conns = tracker.GetConnectionsByProcess(1);
        Assert.Equal(2, conns.Count);
    }
}
