using System.Net;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using Xunit;

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
    public void UnregisterFlow_OfAFlowItNeverHad_AddsNothing()
    {
        var tracker = new FlowTracker();

        tracker.UnregisterFlow(MakeFlowKey());

        // Its last packets would be credited to process 0 otherwise.
        Assert.Null(tracker.LookupConnection(MakeFlowKey()));
        Assert.Null(tracker.LookupProcessId(MakeFlowKey()));
    }

    [Fact]
    public void ForgetProcess_DropsItsOpenAndClosedFlows_AndKeepsEveryoneElses()
    {
        var tracker = new FlowTracker();
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "svchost", null);
        tracker.UnregisterFlow(MakeFlowKey(2));
        tracker.RegisterFlow(MakeFlowKey(3), 300, "curl", null);

        tracker.ForgetProcess(200);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Null(tracker.LookupConnection(MakeFlowKey(2)));
        Assert.Equal(300u, tracker.LookupProcessId(MakeFlowKey(3)));
    }

    [Fact]
    public void ForgetProcess_AfterItsIdWentToANewerProcess_KeepsTheNewOnesFlows()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "svchost", null);
        now += TimeSpan.FromSeconds(1);
        var reusedAt = now;
        tracker.UnregisterFlow(MakeFlowKey(2));
        tracker.RegisterFlow(MakeFlowKey(3), 200, "chrome", null);

        tracker.ForgetProcess(200, identifiedBefore: reusedAt);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        // Closing it after the new process started doesn't make it the new one's.
        Assert.Null(tracker.LookupConnection(MakeFlowKey(2)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(3))?.ProcessName);
    }

    [Fact]
    public void ForgetProcess_GoesByWhenTheFlowWasNamed_NotWhenItWasRegistered()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now);
        var tablesRead = now;
        now += TimeSpan.FromSeconds(1);
        var reusedAt = now;
        now += TimeSpan.FromSeconds(1);
        // A late packet adopted under the name a read of the socket tables gave the ID before chrome had it.
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, identifiedAt: tablesRead);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "chrome", null);

        tracker.ForgetProcess(200, identifiedBefore: reusedAt);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(2))?.ProcessName);
    }

    [Fact]
    public void RegisterFlow_ForTheSameProcessAgain_KeepsWhenItWasEstablished()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now);
        var established = now;
        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "chrome", null);
        now += TimeSpan.FromSeconds(1);

        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null);
        tracker.RegisterFlow(MakeFlowKey(2), 300, "curl", null);

        Assert.Equal(established, tracker.LookupConnection(MakeFlowKey(1))?.EstablishedAt);
        Assert.Equal(now, tracker.LookupConnection(MakeFlowKey(1))?.IdentifiedAt);
        Assert.Equal(now, tracker.LookupConnection(MakeFlowKey(2))?.EstablishedAt);
    }

    [Fact]
    public void ForgetProcess_TellsWhoeverListens_BeforeItsFlowsGo()
    {
        var tracker = new FlowTracker();
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null);
        uint? forgotten = null;
        bool flowStillThere = false;
        tracker.ForgettingProcess += processId =>
        {
            forgotten = processId;
            flowStillThere = tracker.LookupConnection(MakeFlowKey(1)) is not null;
        };

        tracker.ForgetProcess(200);

        Assert.Equal(200u, forgotten);
        Assert.True(flowStillThere);
        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
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

    [Fact]
    public void ConnectionInfo_TracksBytesThreadSafely()
    {
        var tracker = new FlowTracker();
        var key = MakeFlowKey();
        tracker.RegisterFlow(key, 1234, "chrome", null);

        var conn = tracker.LookupConnection(key);
        Assert.NotNull(conn);

        conn.AddBytesSent(1000);
        conn.AddBytesReceived(2000);
        conn.AddBytesSent(500);

        Assert.Equal(1500, conn.BytesSent);
        Assert.Equal(2000, conn.BytesReceived);
    }

    [Fact]
    public void ConnectionInfo_IsIPv6_CorrectForIPv4()
    {
        var tracker = new FlowTracker();
        var key = MakeFlowKey();
        tracker.RegisterFlow(key, 1, "test", null);

        var conn = tracker.LookupConnection(key);
        Assert.NotNull(conn);
        Assert.False(conn.IsIPv6);
    }
}
