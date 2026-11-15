using OpenNetLimit.Engine.Interception;
using SharpDivert;
using Xunit;

namespace OpenNetLimit.Tests;

public class WinDivertInterceptorTests
{
    [Fact]
    public void FlowLayer_IsOpenedAsPassiveObserver()
    {
        // WinDivertOpen returns ERROR_INVALID_PARAMETER (87) for the Flow layer unless
        // both Sniff and RecvOnly are set, which kept the engine from ever starting.
        Assert.Equal(WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly, WinDivertInterceptor.FlowLayerFlags);
    }

    [Fact]
    public void NetworkLayer_DivertsPacketsSoTheyCanBeShaped()
    {
        Assert.False(WinDivertInterceptor.NetworkLayerFlags.HasFlag(WinDivert.Flag.Sniff));
        Assert.False(WinDivertInterceptor.NetworkLayerFlags.HasFlag(WinDivert.Flag.RecvOnly));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(577)]
    [InlineData(1275)]
    public void OpenFailure_ExplainsKnownWindowsErrors(int code)
    {
        Assert.NotEqual(WinDivertOpenException.DescribeError(-1), WinDivertOpenException.DescribeError(code));
    }

    [Fact]
    public void OpenFailure_MessageNamesLayerFlagsAndCode()
    {
        var ex = new WinDivertOpenException(WinDivert.Layer.Flow, WinDivert.Flag.Sniff, 87, "The parameter is incorrect.");

        Assert.Contains("Flow", ex.Message);
        Assert.Contains("Sniff", ex.Message);
        Assert.Contains("87", ex.Message);
        Assert.Equal(87, ex.NativeErrorCode);
    }
}
