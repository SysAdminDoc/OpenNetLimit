using System.Globalization;
using System.Resources;
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

    [Fact]
    public void Poll_DriverUnloadsButTheStartErrorStays_DoesNotSayResumeWorks()
    {
        const string startError = "WinDivert couldn't open the network layer (error 87).";
        var failed = new DiagnosticInfo { Paused = true, DriverLoadedReason = "wireguard.exe has it open.", EngineError = startError };
        var unloaded = new DiagnosticInfo { Paused = true, EngineError = startError };

        Assert.Equal(new TrayNotice("Pause_Unloaded_Title", BodyKey: "Pause_Unloaded_Body"),
            EnginePause.AfterPoll(failed, unloaded));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "wireguard.exe has it open.", "wireguard.exe has it open.")]
    [InlineData("Error 87.", null, "Error 87.")]
    [InlineData("Error 87.", "wireguard.exe has it open.", "Error 87. wireguard.exe has it open.")]
    [InlineData("It can't resume yet. wireguard.exe has it open.", "wireguard.exe has it open.",
        "It can't resume yet. wireguard.exe has it open.")]
    [InlineData(" ", "", null)]
    public void PausedDetail_SaysTheErrorAndTheHolderOnce(string? error, string? reason, string? expected)
    {
        var status = new DiagnosticInfo { Paused = true, EngineError = error, DriverLoadedReason = reason };

        Assert.Equal(expected, EnginePause.PausedDetail(status));
    }

    [Fact]
    public void Resume_FailedWhileAnotherProgramHoldsTheDriver_ShowsBoth()
    {
        var status = new DiagnosticInfo { Paused = true, EngineError = "Error 87.", DriverLoadedReason = "wireguard.exe has it open." };

        var notice = EnginePause.AfterResume("{}", status);

        Assert.Equal("Error 87. wireguard.exe has it open.", notice.Body);
    }

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("one two three", 9, "one two…")]
    [InlineData("one two three", 10, "one two…")]
    [InlineData("abcdefghij", 5, "abcd…")]
    [InlineData("a bcdefghijkl", 8, "a bcdef…")]
    [InlineData("ab cdefg", 5, "ab…")]
    [InlineData("ab cdefghi", 6, "ab cd…")]
    [InlineData("abcdefghij", 1, "…")]
    [InlineData("abcdefghij", 0, "")]
    [InlineData("abcdefghij", -1, "")]
    public void Fit_CutsLongTextWithAnEllipsis(string text, int max, string expected)
    {
        var fitted = TrayNotice.Fit(text, max);

        Assert.Equal(expected, fitted);
        Assert.True(fitted.Length <= Math.Max(max, 0));
    }

    [Fact]
    public void Fit_NeverSplitsAnEmoji()
    {
        var smile = char.ConvertFromUtf32(0x1F600);

        var fitted = TrayNotice.Fit("abc" + smile + "defgh", 5);

        Assert.Equal("abc…", fitted);
    }

    [Fact]
    public void Fit_NeverSplitsAFlagOrAFamily()
    {
        string Letters(params int[] codes) => string.Concat(codes.Select(char.ConvertFromUtf32));
        var usFlag = Letters(0x1F1FA, 0x1F1F8);
        var frenchFlag = Letters(0x1F1EB, 0x1F1F7);
        var family = Letters(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467);

        // Each has room for part of the second one (half a flag, the first person of a family),
        // which would draw as something else.
        Assert.Equal(usFlag + "…", TrayNotice.Fit(usFlag + frenchFlag, 7));
        Assert.Equal(family + "…", TrayNotice.Fit(family + family, 12));
    }

    [Fact]
    public void Fit_NeverEndsOnASpaceThatBelongsToTheCharacterBeforeIt()
    {
        // U+0600 (Arabic number sign) joins whatever follows it, a space included, into one
        // character. Cutting at that space left the sign to join the ellipsis instead.
        var numberSign = ((char)0x0600).ToString();

        var fitted = TrayNotice.Fit("ab cd" + numberSign + " efghij", 9);

        Assert.Equal("ab cd" + numberSign + " e…", fitted);
    }

    [Fact]
    public void Fit_KeepsTheSpaceASignHoldsWhenTheCutEndsOnIt()
    {
        // Trimming spaces before the ellipsis took the sign's own space too, so it joined the
        // ellipsis anyway: when the cut lands right after the sign, and when it lands on a space
        // of its own that follows it.
        var numberSign = ((char)0x0600).ToString();

        Assert.Equal("abcd" + numberSign + " …", TrayNotice.Fit("abcd" + numberSign + " efghij", 7));
        Assert.Equal("ab" + numberSign + " …", TrayNotice.Fit("ab" + numberSign + "  cdefgh", 6));
    }

    [Fact]
    public void Fit_BackAtAWordEnd_LeavesOutWhatCameAfterIt()
    {
        // The trim before the ellipsis looks only at what's kept. Starting from the last
        // character that fit would put back the word after the space, up to its tab.
        Assert.Equal("abcd…", TrayNotice.Fit("abcd ef\tghij", 9));
    }
}

/// <summary>These resolve text, so they can't run while another test switches the language.</summary>
[Collection(CultureSwitchingCollection.Name)]
public sealed class TrayNoticeTextTests
{
    private static readonly string LongReason = string.Join(" ", Enumerable.Repeat("wireguard.exe has it open.", 20));

    [Fact]
    public void LongServiceWording_FitsTheBalloon()
    {
        var body = new TrayNotice("Pause_StillLoaded_Title", Body: LongReason).ResolveBody();
        var kept = body[..^1];

        // It cuts at the last whole word now, so it can fall a few characters short of the limit.
        Assert.True(body.Length <= TrayNotice.MaxBodyLength, $"{body.Length} characters");
        Assert.True(body.Length > TrayNotice.MaxBodyLength / 2, $"{body.Length} characters");
        Assert.EndsWith("…", body);
        Assert.StartsWith(kept, LongReason);
        Assert.Equal(' ', LongReason[kept.Length]);
    }

    [Theory]
    [InlineData("Pause_StayNotSaved_Body", "")]
    [InlineData("Pause_StayNotSaved_Body", "es")]
    [InlineData("Pause_StayNotCleared_Body", "")]
    [InlineData("Pause_StayNotCleared_Body", "es")]
    public void StayWarnings_LeaveRoomForWhatHoldsTheDriver(string key, string culture)
    {
        var resources = new ResourceManager("OpenNetLimit.UI.Resources.Strings", typeof(LocalizationManager).Assembly);
        var warning = resources.GetString(key, CultureInfo.GetCultureInfo(culture))!;
        var holder = string.Join(" ", Enumerable.Repeat("svchost.exe (Windows Sandbox) has it open.", 3));

        // A 150-character list of programs still fits whole beside the warning.
        Assert.True(warning.Length <= 100, $"{warning.Length} characters");
        Assert.True(holder.Length + 1 + warning.Length <= TrayNotice.MaxBodyLength);
    }

    [Fact]
    public void LongServiceWording_GivesWayToTheAddedSentence()
    {
        var notice = new TrayNotice("Pause_StillLoaded_Title", Body: LongReason, ExtraKey: "Pause_StayNotSaved_Body");

        var body = notice.ResolveBody();
        var extra = LocalizationManager.Text("Pause_StayNotSaved_Body");

        Assert.True(body.Length <= TrayNotice.MaxBodyLength, $"{body.Length} characters");
        Assert.EndsWith("… " + extra, body);
        Assert.StartsWith("wireguard.exe has it open.", body);
    }

    [Fact]
    public void ShortWording_IsLeftAlone()
    {
        var notice = new TrayNotice("Pause_StillLoaded_Title", Body: "wireguard.exe has it open.", ExtraKey: "Pause_StayNotSaved_Body");

        Assert.Equal("wireguard.exe has it open. " + LocalizationManager.Text("Pause_StayNotSaved_Body"), notice.ResolveBody());
    }
}
