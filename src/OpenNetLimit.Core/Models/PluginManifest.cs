namespace OpenNetLimit.Core.Models;

public class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public bool Enabled { get; set; } = true;
    public string[] EventSubscriptions { get; set; } = [];
    public string? WebhookUrl { get; set; }
}

public class PluginDispatchEnvelope
{
    public string PluginId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public object? Payload { get; set; }
}
