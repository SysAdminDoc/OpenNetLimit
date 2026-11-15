using System.Globalization;
using System.Resources;
using System.Text.Json;
using OpenNetLimit.Core.IPC;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Xunit;

namespace OpenNetLimit.Tests;

public class ServiceStatusTests
{
    private static readonly JsonSerializerOptions ServiceJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static (ServiceStatusTracker Tracker, List<ServiceStatusView> Shown) NewTracker()
    {
        var tracker = new ServiceStatusTracker();
        var shown = new List<ServiceStatusView>();
        tracker.Changed += shown.Add;
        return (tracker, shown);
    }

    [Fact]
    public void ReconnectRetries_KeepTheFailureOnScreen()
    {
        var (tracker, shown) = NewTracker();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            tracker.ConnectStarting();
            tracker.ConnectFailed(ServiceInstallState.Installed, "Service not running");
        }

        Assert.Equal(["Status_Connecting", "Status_ServiceNotRunning"], shown.Select(s => s.TextKey));
    }

    [Fact]
    public void EngineFailure_ShowsTheServicesReason()
    {
        var (tracker, _) = NewTracker();

        tracker.ConnectStarting();
        tracker.StatusReceived(new DiagnosticInfo { Running = false, EngineError = "WinDivert rejected the flags (Windows error 87)" });

        Assert.Equal(ConnectionState.Error, tracker.Current.State);
        Assert.Equal("Status_EngineFailed", tracker.Current.TextKey);
        Assert.Equal("WinDivert rejected the flags (Windows error 87)", tracker.Current.ResolveDetail());
    }

    [Fact]
    public void EngineDownWithoutReason_PointsAtTheErrorFile()
    {
        var (tracker, _) = NewTracker();

        tracker.StatusReceived(new DiagnosticInfo { Running = false });

        Assert.Equal("Status_EngineFailed", tracker.Current.TextKey);
        Assert.Equal("Status_EngineFailed_Detail", tracker.Current.DetailKey);
        Assert.Null(tracker.Current.Detail);
    }

    [Fact]
    public void RunningEngine_ShowsConnectedWithNoDetail()
    {
        var (tracker, _) = NewTracker();

        tracker.StatusReceived(new DiagnosticInfo { Running = true });

        Assert.Equal(ConnectionState.Connected, tracker.Current.State);
        Assert.Null(tracker.Current.ResolveDetail());
    }

    [Fact]
    public void EngineRecovering_ClearsTheError()
    {
        var (tracker, shown) = NewTracker();

        tracker.StatusReceived(new DiagnosticInfo { Running = false, EngineError = "x" });
        tracker.StatusReceived(new DiagnosticInfo { Running = false, EngineError = "x" });
        tracker.StatusReceived(new DiagnosticInfo { Running = true });

        Assert.Equal(["Status_EngineFailed", "Status_Connected"], shown.Select(s => s.TextKey));
    }

    [Fact]
    public void MissingService_SaysItIsNotInstalled()
    {
        var (tracker, _) = NewTracker();

        tracker.ConnectFailed(ServiceInstallState.NotInstalled, "Service not running");

        Assert.Equal("Status_ServiceNotInstalled", tracker.Current.TextKey);
        Assert.Equal("Status_ServiceNotInstalled_Detail", tracker.Current.DetailKey);
    }

    [Fact]
    public void PipeErrorWording_IsKept()
    {
        var (tracker, _) = NewTracker();

        tracker.ConnectFailed(ServiceInstallState.Installed, "Access to the path is denied.");

        Assert.Equal("Access to the path is denied.", tracker.Current.ResolveText());
    }

    [Fact]
    public void ParseDiagnostics_ReadsTheServicesStatusReply()
    {
        var json = JsonSerializer.Serialize(new DiagnosticInfo { Running = false, EngineError = "reason" }, ServiceJson);

        var parsed = ServiceStatusTracker.ParseDiagnostics(json);

        Assert.NotNull(parsed);
        Assert.False(parsed.Running);
        Assert.Equal("reason", parsed.EngineError);
    }

    [Theory]
    [InlineData("{\"error\":\"Unknown command\"}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void ParseDiagnostics_IgnoresRepliesThatAreNotAStatus(string json)
    {
        Assert.Null(ServiceStatusTracker.ParseDiagnostics(json));
    }

    [Theory]
    [InlineData(false, true, "Menu_AdminRequired")]
    [InlineData(false, false, "Menu_AdminRequired")]
    [InlineData(true, false, "Menu_SelectAppFirst")]
    [InlineData(true, true, null)]
    public void LimitMenu_ExplainsWhyItIsDisabled(bool isAdmin, bool hasSelection, string? expected)
    {
        Assert.Equal(expected, LimitMenu.HintKey(isAdmin, hasSelection));
    }

    [Theory]
    [InlineData("Status_EngineFailed")]
    [InlineData("Status_EngineFailed_Detail")]
    [InlineData("Status_ServiceNotInstalled")]
    [InlineData("Status_ServiceNotInstalled_Detail")]
    [InlineData("Status_ServiceNotRunning_Detail")]
    [InlineData("Menu_SelectAppFirst")]
    public void NewStatusStrings_ExistInEveryLanguage(string key)
    {
        var resources = new ResourceManager("OpenNetLimit.UI.Resources.Strings", typeof(LocalizationManager).Assembly);

        var english = resources.GetString(key, CultureInfo.InvariantCulture);
        var spanish = resources.GetString(key, CultureInfo.GetCultureInfo("es"));

        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.False(string.IsNullOrWhiteSpace(spanish));
        Assert.NotEqual(english, spanish);
    }
}
