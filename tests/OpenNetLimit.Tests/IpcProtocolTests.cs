using OpenNetLimit.Core.IPC;
using Xunit;

namespace OpenNetLimit.Tests;

public class IpcProtocolTests
{
    [Theory]
    [InlineData("SNAPSHOT")]
    [InlineData("RULES")]
    [InlineData("PROCESSES")]
    [InlineData("STATUS")]
    public void ReadCommands_AreValid(string command)
    {
        Assert.True(IpcProtocol.IsValidCommand(command));
        Assert.False(IpcProtocol.RequiresAdmin(command));
    }

    [Theory]
    [InlineData("ADD_RULE")]
    [InlineData("REMOVE_RULE")]
    [InlineData("UPDATE_RULE")]
    [InlineData("VERIFY_PROCESS")]
    [InlineData("GEOIP")]
    public void WriteCommands_AreValid_AndRequireAdmin(string command)
    {
        Assert.True(IpcProtocol.IsValidCommand(command));
        Assert.True(IpcProtocol.RequiresAdmin(command));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("DROP_TABLE")]
    [InlineData("")]
    public void InvalidCommands_AreRejected(string command)
    {
        Assert.False(IpcProtocol.IsValidCommand(command));
    }

    [Fact]
    public void ReadCommands_AreCaseInsensitive()
    {
        Assert.Contains("snapshot", IpcProtocol.ReadCommands);
        Assert.Contains("Snapshot", IpcProtocol.ReadCommands);
    }

    [Fact]
    public void WriteCommands_AreCaseInsensitive()
    {
        Assert.Contains("add_rule", IpcProtocol.WriteCommands);
        Assert.Contains("Add_Rule", IpcProtocol.WriteCommands);
    }

    [Fact]
    public void ProtocolVersion_IsPositive()
    {
        Assert.True(IpcProtocol.ProtocolVersion > 0);
    }
}
