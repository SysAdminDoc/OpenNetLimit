using Cli = OpenNetLimit.CLI.Program;
using Xunit;

namespace OpenNetLimit.Tests;

public sealed class CliEngineControlTests
{
    private readonly StringWriter _error = new();

    [Fact]
    public void Paused_DriverUnloaded_ExitsZeroQuietly()
    {
        var code = Cli.PauseOutcome("""{"paused":true,"driverLoadedReason":null}""", _error);

        Assert.Equal(0, code);
        Assert.Empty(_error.ToString());
    }

    [Fact]
    public void Paused_DriverStillLoaded_ExitsTwoAndSaysWhy()
    {
        var code = Cli.PauseOutcome("""{"paused":true,"driverLoadedReason":"wireguard.exe has it open."}""", _error);

        Assert.Equal(2, code);
        Assert.Contains("wireguard.exe has it open.", _error.ToString());
    }

    [Fact]
    public void NotPaused_ExitsOneWithTheEngineError()
    {
        var code = Cli.PauseOutcome("""{"paused":false,"engineError":"Run as administrator."}""", _error);

        Assert.Equal(1, code);
        Assert.Contains("isn't paused. Run as administrator.", _error.ToString());
    }

    [Fact]
    public void Resumed_ExitsZero()
    {
        Assert.Equal(0, Cli.ResumeOutcome("""{"running":true,"paused":false}""", _error));
    }

    [Fact]
    public void ResumeFailed_ExitsOneWithTheEngineError()
    {
        var code = Cli.ResumeOutcome("""{"running":false,"engineError":"Close wireguard.exe."}""", _error);

        Assert.Equal(1, code);
        Assert.Contains("didn't start. Close wireguard.exe.", _error.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void AnswerThatIsNotAStatus_ExitsOne(string body)
    {
        Assert.Equal(1, Cli.PauseOutcome(body, _error));
        Assert.Equal(1, Cli.ResumeOutcome(body, _error));
    }
}
