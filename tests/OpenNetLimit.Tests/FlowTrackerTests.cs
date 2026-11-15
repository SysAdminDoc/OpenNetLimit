using System.Net;
using OpenNetLimit.Core.Models;
using OpenNetLimit.Engine.Interception;
using OpenNetLimit.Engine.Monitoring;
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
        DateTime? cutoff = DateTime.MaxValue;
        bool flowStillThere = false;
        tracker.ForgettingProcess += (processId, identifiedBefore) =>
        {
            forgotten = processId;
            cutoff = identifiedBefore;
            flowStillThere = tracker.LookupConnection(MakeFlowKey(1)) is not null;
        };

        tracker.ForgetProcess(200);

        Assert.Equal(200u, forgotten);
        Assert.Null(cutoff);
        Assert.True(flowStillThere);
        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
    }

    [Fact]
    public void AFlowNamedBeforeItsProcessWasForgotten_IsTurnedAwayWhenItArrivesLate()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now);
        var namedEarlier = now;
        now += TimeSpan.FromSeconds(1);
        var idFree = now;
        var registered = new List<string>();
        tracker.FlowRegistered += (_, name, _) => registered.Add(name);

        tracker.ForgetProcess(200, idFree);
        // A socket owner looked up just before the forget, registering just after it.
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, namedEarlier);
        // The next process with the ID, named after it was free.
        tracker.RegisterFlow(MakeFlowKey(2), 200, "chrome", null, idFree);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(2))?.ProcessName);
        Assert.Equal(["chrome"], registered);
    }

    [Fact]
    public void AForgetWithNothingKnownAboutTheId_TurnsNoLaterFlowAway()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now);
        var namedEarlier = now;
        now += TimeSpan.FromSeconds(1);

        // Who has the ID now isn't known, so a flow named before may be the next process's.
        tracker.ForgetProcess(200);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null, namedEarlier);

        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(1))?.ProcessName);
    }

    [Fact]
    public void ACutoffFromBeforeTheClockWasSetBack_TurnsNoNewProcessAway()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now, () => 0);
        tracker.ForgetProcess(200, now);

        // The clock goes back an hour, and the next process with the ID is named.
        now -= TimeSpan.FromHours(1);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null, now);
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(1))?.ProcessName);

        // A cutoff just ahead of the clock still holds: the next process is stamped 16 ms past its start.
        tracker.ForgetProcess(300, now + HeldProcess.StartStampLag);
        tracker.RegisterFlow(MakeFlowKey(2), 300, "svchost", null, now);
        Assert.Null(tracker.LookupConnection(MakeFlowKey(2)));
    }

    [Fact]
    public void ACutoffFromBeforeTheClockWasSetBack_StaysLetGo_WhenTheClockCatchesUpWithIt()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        long tick = 0;
        var tracker = new FlowTracker(() => now, () => tick);
        var cutoff = now;
        tracker.ForgetProcess(200, cutoff);

        // The clock went back an hour and nothing else exited while it caught up. A flow named
        // in that hour belongs to whatever has the ID now.
        tick += (long)(TimeSpan.FromHours(1) - TimeSpan.FromMilliseconds(500)).TotalMilliseconds;
        now = cutoff - TimeSpan.FromMilliseconds(500);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null, cutoff - TimeSpan.FromMilliseconds(600));

        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(1))?.ProcessName);
    }

    [Fact]
    public void ACutoff_IsKeptForAMinute()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        long tick = 0;
        var tracker = new FlowTracker(() => now, () => tick);
        var namedEarlier = now - TimeSpan.FromSeconds(1);
        tracker.ForgetProcess(200, now);

        tick += 59_000;
        now += TimeSpan.FromSeconds(59);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, namedEarlier);
        tick += 2_000;
        now += TimeSpan.FromSeconds(2);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "chrome", null, namedEarlier);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(2))?.ProcessName);
    }

    [Fact]
    public void ACutoff_IsLetGoWhenTheClockIsSetBackWithinTheMinute()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        long tick = 0;
        var tracker = new FlowTracker(() => now, () => tick);
        var cutoff = now;
        tracker.ForgetProcess(200, cutoff);

        // 20 seconds on, the clock goes back 30 seconds, and the next process with the ID is named.
        tick += 20_000;
        now = cutoff - TimeSpan.FromSeconds(10);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "chrome", null, now - TimeSpan.FromSeconds(1));

        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(1))?.ProcessName);
    }

    [Fact]
    public void ACutoff_HoldsWhenWindowsTimeStepsTheClockBackALittle()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        long tick = 0;
        var tracker = new FlowTracker(() => now, () => tick);
        var cutoff = now;
        tracker.ForgetProcess(200, cutoff);

        // Two seconds back after a sleep, while a name read just before the forget comes in late.
        tick += 100;
        now = cutoff - TimeSpan.FromSeconds(2);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, cutoff - TimeSpan.FromMilliseconds(50));

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
    }

    [Fact]
    public void ANameStampedBeforeTheClockWentBack_IsTurnedAwayAfterAForget()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var namedBeforeTheClockWentBack = now + TimeSpan.FromHours(1);
        var tracker = new FlowTracker(() => now, () => 0);
        tracker.ForgetProcess(200, now);

        now += TimeSpan.FromMilliseconds(100);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, namedBeforeTheClockWentBack);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
    }

    [Fact]
    public void ForgetHeldProcess_DropsEveryFlowOfTheId_WhenEverItWasNamed()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now, () => 0);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null);
        // It had only just started: named 16 ms past its start.
        tracker.RegisterFlow(MakeFlowKey(2), 200, "svchost", null, now + HeldProcess.StartStampLag);
        tracker.RegisterFlow(MakeFlowKey(3), 300, "chrome", null);

        // The clock goes back an hour before it exits.
        now -= TimeSpan.FromHours(1);
        tracker.ForgetHeldProcess(200);

        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Null(tracker.LookupConnection(MakeFlowKey(2)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(3))?.ProcessName);
    }

    [Fact]
    public void ForgetHeldProcess_TurnsAwayALateName_AndTellsListenersEverythingGoes()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new FlowTracker(() => now, () => 0);
        DateTime? toldBefore = DateTime.MaxValue;
        tracker.ForgettingProcess += (_, before) => toldBefore = before;
        var namedEarlier = now;

        now += TimeSpan.FromSeconds(1);
        tracker.ForgetHeldProcess(200);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, namedEarlier);
        now += TimeSpan.FromSeconds(1);
        tracker.RegisterFlow(MakeFlowKey(2), 200, "chrome", null);

        Assert.Null(toldBefore);
        Assert.Null(tracker.LookupConnection(MakeFlowKey(1)));
        Assert.Equal("chrome", tracker.LookupConnection(MakeFlowKey(2))?.ProcessName);
    }

    [Fact]
    public void AForgottenIdsCutoff_IsLetGoOnceItsLongPast()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        long tick = 0;
        var tracker = new FlowTracker(() => now, () => tick);
        var namedEarlier = now - TimeSpan.FromSeconds(1);
        tracker.ForgetProcess(200, now);

        // Another process's exit, minutes later, sweeps the old cutoff away.
        now += TimeSpan.FromMinutes(5);
        tick += (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        tracker.ForgetProcess(300, now);
        tracker.RegisterFlow(MakeFlowKey(1), 200, "svchost", null, namedEarlier);

        Assert.Equal("svchost", tracker.LookupConnection(MakeFlowKey(1))?.ProcessName);
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
