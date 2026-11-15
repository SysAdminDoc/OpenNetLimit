using System.ComponentModel;
using OpenNetLimit.Engine.Interception;
using Xunit;
using FakeDriverService = OpenNetLimit.Tests.DriverPreflightTests.FakeDriverService;

namespace OpenNetLimit.Tests;

public sealed class DriverUnloaderTests
{
    private readonly List<TimeSpan> _sleeps = [];

    private DriverUnloader Unloader(FakeDriverService service, params string[] programs) =>
        new(service, (delay, _) => _sleeps.Add(delay), () => programs);

    [Theory]
    [InlineData(DriverServiceState.Stopped)]
    [InlineData(DriverServiceState.NotInstalled)]
    public void AlreadyUnloaded_SendsNoStop(DriverServiceState state)
    {
        var service = new FakeDriverService(state);

        var reason = Unloader(service).Unload(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal(0, service.StopRequests);
    }

    [Theory]
    [InlineData(DriverServiceState.Stopped)]
    [InlineData(DriverServiceState.NotInstalled)]
    public void Loaded_IsStoppedAndWatchedUntilItUnloads(DriverServiceState unloaded)
    {
        // The driver deletes its own registration, so an unload can end at "not installed".
        var service = new FakeDriverService(DriverServiceState.Running, DriverServiceState.Running, unloaded);

        var reason = Unloader(service).Unload(CancellationToken.None);

        Assert.Null(reason);
        Assert.Equal(1, service.StopRequests);
        Assert.Single(_sleeps);
    }

    [Fact]
    public void HeldOpenByAnotherProgram_SaysWhichAndThatItUnloadsByItself()
    {
        var service = new FakeDriverService(DriverServiceState.Running, DriverServiceState.StopPending);

        var reason = Unloader(service, "wireguard.exe").Unload(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("wireguard.exe", reason);
        Assert.Contains("unloads by itself once you close wireguard.exe", reason);
        Assert.Equal(DriverUnloader.UnloadLimit, _sleeps.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay));
    }

    [Fact]
    public void HeldOpenByAnUnknownProgram_NamesTheUsualSuspects()
    {
        var service = new FakeDriverService(DriverServiceState.Running, DriverServiceState.StopPending);

        var reason = Unloader(service).Unload(CancellationToken.None);

        Assert.Contains("another program has it open", reason);
        Assert.Contains("a VPN, packet capture or another limiter", reason);
    }

    [Fact]
    public void StillRunningAfterTheStop_SaysToCloseThemAndPauseAgain()
    {
        var service = new FakeDriverService(DriverServiceState.Running);

        var reason = Unloader(service, "a.exe", "b.exe").Unload(CancellationToken.None);

        Assert.Contains("These programs have WinDivert loaded: a.exe, b.exe.", reason);
        Assert.Contains("Close them and pause again", reason);
    }

    [Fact]
    public void StopRefused_SaysWhyWithoutWaiting()
    {
        var service = new FakeDriverService(DriverServiceState.Running)
        {
            StopFailure = new Win32Exception(5)
        };

        var reason = Unloader(service).Unload(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("may still be loaded", reason);
        Assert.Contains("Windows error 5", reason);
        Assert.Contains("Restart Windows", reason);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void StateUnreadable_SaysItMayStillBeLoaded()
    {
        var service = new FakeDriverService { QueryFailure = new Win32Exception(5) };

        var reason = Unloader(service).Unload(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("couldn't be read", reason);
        Assert.Contains("Windows error 5", reason);
        Assert.Equal(1, service.StopRequests);
    }

    [Fact]
    public void ListingTheProgramsFails_StillGivesTheReason()
    {
        var service = new FakeDriverService(DriverServiceState.Running, DriverServiceState.StopPending);
        var unloader = new DriverUnloader(service, (_, _) => { }, () => throw new Win32Exception(5));

        var reason = unloader.Unload(CancellationToken.None);

        Assert.Contains("unloads by itself", reason);
    }

    [Fact]
    public void Cancelled_StopsWatching()
    {
        var service = new FakeDriverService(DriverServiceState.Running, DriverServiceState.StopPending);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var unloader = new DriverUnloader(service, programsUsingWinDivert: () => []);

        Assert.Throws<OperationCanceledException>(() => unloader.Unload(cancel.Token));
    }

    [Fact]
    public void IsUnloaded_FollowsTheServiceState()
    {
        var service = new FakeDriverService(DriverServiceState.StopPending, DriverServiceState.Stopped);
        var unloader = Unloader(service);

        Assert.False(unloader.IsUnloaded());
        Assert.True(unloader.IsUnloaded());
    }

    [Fact]
    public void LoadsWinDivert_MatchesTheDllByFileNameOnly()
    {
        Assert.True(DriverUnloader.LoadsWinDivert([@"C:\Windows\System32\ntdll.dll", @"C:\Tools\WINDIVERT.DLL"]));
        Assert.False(DriverUnloader.LoadsWinDivert([@"C:\WinDivert.dll.bak", @"C:\WinDivert\other.dll"]));
    }
}
