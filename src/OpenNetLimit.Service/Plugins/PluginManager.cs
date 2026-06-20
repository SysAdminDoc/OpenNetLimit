using System.Text;
using System.Text.Json;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Plugins;

public sealed class PluginManager : IDisposable
{
    private readonly PluginOptions _options;
    private readonly ILogger<PluginManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _lock = new();
    private List<PluginManifest> _plugins = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PluginManager(PluginOptions options, ILogger<PluginManager> logger)
        : this(options, logger, null)
    {
    }

    public PluginManager(PluginOptions options, ILogger<PluginManager> logger, HttpClient? httpClient)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public IReadOnlyList<PluginManifest> GetPlugins()
    {
        lock (_lock)
            return _plugins.Select(Copy).ToList();
    }

    public IReadOnlyList<PluginManifest> Reload()
    {
        if (!_options.Enabled)
        {
            lock (_lock)
                _plugins = [];
            return [];
        }

        Directory.CreateDirectory(_options.PluginDirectory);
        var loaded = new List<PluginManifest>();
        foreach (var path in Directory.EnumerateFiles(_options.PluginDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(path);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
                if (manifest is null)
                {
                    _logger.LogWarning("Ignoring plugin manifest {Path}: manifest is empty", path);
                    continue;
                }

                if (!IsValid(manifest, out var reason))
                {
                    _logger.LogWarning("Ignoring plugin manifest {Path}: {Reason}", path, reason);
                    continue;
                }

                loaded.Add(Normalize(manifest));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load plugin manifest {Path}", path);
            }
        }

        lock (_lock)
            _plugins = loaded;
        return GetPlugins();
    }

    public async Task DispatchAsync(string eventType, object payload, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return;

        List<PluginManifest> plugins;
        lock (_lock)
            plugins = _plugins.Select(Copy).ToList();

        foreach (var plugin in plugins.Where(p => p.Enabled && IsSubscribed(p, eventType)))
        {
            if (!Uri.TryCreate(plugin.WebhookUrl, UriKind.Absolute, out var webhookUri))
                continue;

            try
            {
                var envelope = new PluginDispatchEnvelope
                {
                    PluginId = plugin.Id,
                    EventType = eventType,
                    Payload = payload
                };
                using var content = new StringContent(JsonSerializer.Serialize(envelope, JsonOptions), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(webhookUri, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("Plugin {PluginId} webhook returned {StatusCode}", plugin.Id, (int)response.StatusCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {PluginId} dispatch failed for {EventType}", plugin.Id, eventType);
            }
        }
    }

    private static bool IsSubscribed(PluginManifest plugin, string eventType) =>
        plugin.EventSubscriptions.Any(s =>
            s.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(eventType, StringComparison.OrdinalIgnoreCase));

    private static bool IsValid(PluginManifest manifest, out string reason)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            reason = "id is required";
            return false;
        }

        if (manifest.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
        {
            reason = "id contains unsupported characters";
            return false;
        }

        if (manifest.EventSubscriptions.Length == 0)
        {
            reason = "eventSubscriptions must not be empty";
            return false;
        }

        if (!Uri.TryCreate(manifest.WebhookUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = "webhookUrl must be absolute http/https";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static PluginManifest Normalize(PluginManifest manifest) =>
        new()
        {
            Id = manifest.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id.Trim() : manifest.Name.Trim(),
            Version = string.IsNullOrWhiteSpace(manifest.Version) ? "1.0.0" : manifest.Version.Trim(),
            Enabled = manifest.Enabled,
            EventSubscriptions = manifest.EventSubscriptions
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            WebhookUrl = manifest.WebhookUrl?.Trim()
        };

    private static PluginManifest Copy(PluginManifest plugin) =>
        new()
        {
            Id = plugin.Id,
            Name = plugin.Name,
            Version = plugin.Version,
            Enabled = plugin.Enabled,
            EventSubscriptions = plugin.EventSubscriptions.ToArray(),
            WebhookUrl = plugin.WebhookUrl
        };

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
