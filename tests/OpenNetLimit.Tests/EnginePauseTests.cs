using OpenNetLimit.Core.IPC;
using OpenNetLimit.UI.Services;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class EnginePauseTests
{
    private static readonly DiagnosticInfo Held = new() { Paused = true, DriverLoadedReason = "wireguard.exe has it open." };
    private static readonly DiagnosticInfo Unloaded = new() { Paused = true };
    private static readonly DiagnosticInfo Running = new() { Running = true };

    [Theory]
    [InlineData(true, """PAUSE {"stayPaused":true}""")]
    [InlineData(false, """PAUSE {"stayPaused":false}""")]
    public void PauseCommand_CarriesTheStayChoice(bool stay, string expected)
    {
        Assert.Equal(expected, EnginePause.PauseCommand(stay));
    }

    [Fact]
    public void Pause_DriverUnloaded_SaysTheGameCanStart()
    {
        var notice = EnginePause.AfterPause("{}", Unloaded);

        Assert.Equal(new TrayNotice("Status_Paused", BodyKey: "Pause_Unloaded_Body"), notice);
    }

    [Fact]
    public void Pause_DriverStillLoaded_WarnsWithTheReason()
    {
        var notice = EnginePause.AfterPause("{}", Held);

        Assert.Equal("Pause_StillLoaded_Title", notice.TitleKey);
        Assert.Equal("wireguard.exe has it open.", notice.Body);
        Assert.True(notice.IsWarning);
    }

    [Fact]
    public void Pause_Refused_ShowsTheServicesError()
    {
        var notice = EnginePause.AfterPause("""{"error":"unauthorized: admin required"}""", null);

        Assert.Equal("Pause_Failed_Title", notice.TitleKey);
        Assert.Equal("unauthorized: admin required", notice.Body);
        Assert.True(notice.IsWarning);
    }

    [Fact]
    public void Pause_NotPaused_ShowsTheEngineError()
    {
        var notice = EnginePause.AfterPause("{}", new DiagnosticInfo { Running = false, EngineError = "Run as administrator." });

        Assert.Equal("Pause_Failed_Title", notice.TitleKey);
        Assert.Equal("Run as administrator.", notice.Body);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"error":""}""")]
    public void NoUsableReason_FallsBackToTheGenericText(string? reply)
    {
        var notice = EnginePause.AfterPause(reply, null);

        Assert.Null(notice.Body);
        Assert.Null(notice.BodyKey);
        Assert.Equal(LocalizationManager.Text("Engine_NoAnswer"), notice.ResolveBody());
    }

    [Fact]
    public void ConnectionDroppedBeforeTheAnswer_DoesNotClaimItFailed()
    {
        var pause = EnginePause.AfterPause(null, null);
        var resume = EnginePause.AfterResume(null, null);

        Assert.Equal(new TrayNotice("Engine_NoReply_Title", BodyKey: "Engine_ConnectionLost", IsWarning: true), pause);
        Assert.Equal(pause, resume);
    }

    [Fact]
    public void Pause_StayPausedNotSaved_WarnsThatThePauseEndsOnRestart()
    {
        var notice = EnginePause.AfterPause("{}", Unloaded, stayRequested: true);

        Assert.Equal(new TrayNotice("Status_Paused", BodyKey: "Pause_Unloaded_Body", IsWarning: true,
            ExtraKey: "Pause_StayNotSaved_Body"), notice);
    }

    [Fact]
    public void Pause_EarlierStayPausedNotCleared_WarnsThatItStaysPaused()
    {
        var notice = EnginePause.AfterPause("{}", new DiagnosticInfo { Paused = true, StayPaused = true }, stayRequested: false);

        Assert.Equal(new TrayNotice("Status_Paused", BodyKey: "Pause_Unloaded_Body", IsWarning: true,
            ExtraKey: "Pause_StayNotCleared_Body"), notice);
    }

    [Fact]
    public void Pause_DriverStillLoadedAndStayPausedNotSaved_WarnsAboutBoth()
    {
        var notice = EnginePause.AfterPause("{}", Held, stayRequested: true);

        Assert.Equal(new TrayNotice("Pause_StillLoaded_Title", Body: "wireguard.exe has it open.", IsWarning: true,
            ExtraKey: "Pause_StayNotSaved_Body"), notice);
        Assert.Equal("wireguard.exe has it open. " + LocalizationManager.Text("Pause_StayNotSaved_Body"), notice.ResolveBody());
    }

    [Fact]
    public void Pause_StayPausedSaved_SaysTheGameCanStart()
    {
        var notice = EnginePause.AfterPause("{}", new DiagnosticInfo { Paused = true, StayPaused = true }, stayRequested: true);

        Assert.Equal(new TrayNotice("Status_Paused", BodyKey: "Pause_Unloaded_Body"), notice);
    }

    [Fact]
    public void Resume_Running_SaysResumed()
    {
        var notice = EnginePause.AfterResume("{}", Running);

        Assert.Equal(new TrayNotice("Resume_Done_Title", BodyKey: "Resume_Done_Body"), notice);
    }

    [Fact]
    public void Resume_RunningButStayPausedNotCleared_WarnsItComesUpPausedAfterARestart()
    {
        var notice = EnginePause.AfterResume("{}", new DiagnosticInfo { Running = true, StayPaused = true });

        Assert.Equal(new TrayNotice("Resume_Done_Title", BodyKey: "Resume_Done_Body", IsWarning: true,
            ExtraKey: "Resume_StayNotCleared_Body"), notice);
    }

    [Fact]
    public void Resume_EngineDidNotStart_ShowsWhy()
    {
        var notice = EnginePause.AfterResume("{}", new DiagnosticInfo { Running = false, EngineError = "Close wireguard.exe." });

        Assert.Equal("Resume_Failed_Title", notice.TitleKey);
        Assert.Equal("Close wireguard.exe.", notice.Body);
        Assert.True(notice.IsWarning);
    }

    [Fact]
    public void Poll_DriverUnloadsAfterThePause_SaysSo()
    {
        Assert.Equal(new TrayNotice("Pause_Unloaded_Title", BodyKey: "Pause_Unloaded_Body"),
            EnginePause.AfterPoll(Held, Unloaded));
    }

    [Fact]
    public void Poll_DriverUnloadsAfterARefusedResume_SaysResumeWorksNow()
    {
        var refused = new DiagnosticInfo
        {
            Paused = true,
            DriverLoadedReason = "wireguard.exe has it open.",
            EngineError = "It can't resume until the driver finishes unloading. wireguard.exe has it open."
        };

        Assert.Equal(new TrayNotice("Pause_Unloaded_Title", BodyKey: "Pause_CanResume_Body"),
            EnginePause.AfterPoll(refused, Unloaded));
    }

    [Fact]
    public void Poll_NothingChangedOrResumed_SaysNothing()
    {
        Assert.Null(EnginePause.AfterPoll(Held, Held));
        Assert.Null(EnginePause.AfterPoll(Unloaded, Unloaded));
        Assert.Null(EnginePause.AfterPoll(Held, Running));
        Assert.Null(EnginePause.AfterPoll(null, Unloaded));
        Assert.Null(EnginePause.AfterPoll(Running, Unloaded));
    }
}
