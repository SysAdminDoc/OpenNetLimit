using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using OpenNetLimit.Core.IPC;

namespace OpenNetLimit.UI.Services;

/// <summary>
/// What the header status pill shows. Text and detail are resource keys unless the service
/// supplied its own wording, so the pill re-renders on a language switch.
/// </summary>
public sealed record ServiceStatusView(
    ConnectionState State,
    string TextKey,
    string? Text = null,
    string? DetailKey = null,
    string? Detail = null,
    bool Paused = false)
{
    public string ResolveText() => Text ?? LocalizationManager.Text(TextKey);

    public string? ResolveDetail() => Detail ?? (DetailKey is null ? null : LocalizationManager.Text(DetailKey));
}

public enum ServiceInstallState
{
    Unknown,
    NotInstalled,
    Installed
}

/// <summary>Turns connection attempts and STATUS replies into the status pill.</summary>
public sealed class ServiceStatusTracker
{
    public const string ServiceName = "OpenNetLimit";
    private const string PipeClientNotRunning = "Service not running";

    private bool _connectAttempted;

    public ServiceStatusView Current { get; private set; } = new(ConnectionState.Disconnected, "Status_Disconnected");

    public event Action<ServiceStatusView>? Changed;

    /// <summary>
    /// Only the first attempt shows "Connecting...". Retries keep the last result on screen,
    /// otherwise the pill flips between two states every few seconds and the reason never stays put.
    /// </summary>
    public void ConnectStarting()
    {
        if (_connectAttempted)
            return;
        _connectAttempted = true;
        Set(new ServiceStatusView(ConnectionState.Connecting, "Status_Connecting"));
    }

    public void ConnectFailed(ServiceInstallState install, string? clientError)
    {
        if (install == ServiceInstallState.NotInstalled)
        {
            Set(new ServiceStatusView(ConnectionState.Error, "Status_ServiceNotInstalled",
                DetailKey: "Status_ServiceNotInstalled_Detail"));
            return;
        }

        var ownWording = !string.IsNullOrWhiteSpace(clientError) &&
                         !string.Equals(clientError, PipeClientNotRunning, StringComparison.OrdinalIgnoreCase);
        Set(new ServiceStatusView(ConnectionState.Error, "Status_ServiceNotRunning",
            Text: ownWording ? clientError : null,
            DetailKey: "Status_ServiceNotRunning_Detail"));
    }

    /// <summary>Applies a STATUS reply. Null means the service answered but gave no usable status.</summary>
    public void StatusReceived(DiagnosticInfo? diagnostics)
    {
        if (diagnostics is { Paused: true })
        {
            Set(new ServiceStatusView(ConnectionState.Connected, "Status_Paused",
                DetailKey: "Status_Paused_Detail",
                Detail: string.IsNullOrWhiteSpace(diagnostics.DriverLoadedReason) ? null : diagnostics.DriverLoadedReason,
                Paused: true));
            return;
        }

        if (diagnostics is null || diagnostics.Running)
        {
            Set(new ServiceStatusView(ConnectionState.Connected, "Status_Connected"));
            return;
        }

        Set(new ServiceStatusView(ConnectionState.Error, "Status_EngineFailed",
            DetailKey: "Status_EngineFailed_Detail",
            Detail: string.IsNullOrWhiteSpace(diagnostics.EngineError) ? null : diagnostics.EngineError));
    }

    public void Disconnected() =>
        Set(new ServiceStatusView(ConnectionState.Disconnected, "Status_ServiceDisconnected"));

    private void Set(ServiceStatusView view)
    {
        if (view == Current)
            return;
        Current = view;
        Changed?.Invoke(view);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Reads a STATUS reply. Returns null for an error reply or anything that isn't a status.</summary>
    public static DiagnosticInfo? ParseDiagnostics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("running", out _))
                return null;
            return document.RootElement.Deserialize<DiagnosticInfo>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks whether the Windows service is registered. Any user can read this key.</summary>
    public static ServiceInstallState DetectInstallState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return key is null ? ServiceInstallState.NotInstalled : ServiceInstallState.Installed;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ServiceInstallState.Unknown;
        }
    }
}
