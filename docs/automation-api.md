# CLI and REST API

OpenNetLimit exposes a command-line client and a small HTTP API. Both talk to the background service. The API listens only on `http://127.0.0.1:47719/` unless remote access is explicitly enabled.

## Command-line client

The release ZIP includes `onl.exe`.

```powershell
./onl.exe status
./onl.exe snapshot
./onl.exe processes
./onl.exe rules list
./onl.exe rules add --process steam.exe --download 8192 --upload 1024
./onl.exe rules remove <rule-id>
./onl.exe stats hourly --process steam.exe --hours 24
./onl.exe stats top --days 7 --limit 20
```

Limits passed to `--download` and `--upload` use KB/s. Rule actions accept `Limit`, `Block`, or `Allow`.

The client reads these environment variables:

| Variable | Purpose |
|---|---|
| `OPENNETLIMIT_API_URL` | API base URL. The default is `http://127.0.0.1:47719`. |
| `OPENNETLIMIT_API_KEY` | Key used for mutations and every remote request. |

## Local API behavior

Local read endpoints do not require a key. Mutating requests require `OPENNETLIMIT_API_KEY`, sent through either `X-OpenNetLimit-Key` or `Authorization: Bearer <key>`.

```powershell
$headers = @{ 'X-OpenNetLimit-Key' = $env:OPENNETLIMIT_API_KEY }
Invoke-RestMethod http://127.0.0.1:47719/api/v1/status
Invoke-RestMethod http://127.0.0.1:47719/api/v1/rules -Headers $headers
```

## Service settings

| Variable | Purpose |
|---|---|
| `OPENNETLIMIT_API_URLS` | Semicolon or comma separated listener prefixes. |
| `OPENNETLIMIT_API_KEY` | Required for mutations and remote requests. |
| `OPENNETLIMIT_ENABLE_REMOTE_API=1` | Permits a non-loopback listener when a key is also set. |
| `OPENNETLIMIT_API_DISABLED=1` | Turns off the HTTP listener. |
| `OPENNETLIMIT_VIRUSTOTAL_API_KEY` | Enables hash-only VirusTotal lookups. Files are not uploaded. |
| `OPENNETLIMIT_VIRUSTOTAL_CACHE_HOURS` | VirusTotal cache duration. The default is 12 hours. |
| `OPENNETLIMIT_GEOIP_ENABLED=1` | Enables lookups for public remote IP addresses. |
| `OPENNETLIMIT_GEOIP_ENDPOINT` | GeoIP provider base URL. |
| `OPENNETLIMIT_GEOIP_CACHE_HOURS` | GeoIP cache duration. The default is 24 hours. |
| `OPENNETLIMIT_PLUGINS_ENABLED=1` | Enables declarative webhook plugins. |
| `OPENNETLIMIT_PLUGIN_DIR` | Plugin manifest directory. |

Remote administration fails closed. OpenNetLimit ignores non-loopback listener prefixes unless both the remote flag and an API key are configured.

## Endpoints

| Endpoint | Access | Purpose |
|---|---|---|
| `GET /health` | Local read or keyed remote | Liveness check |
| `GET /api/v1/status` | Local read or keyed remote | Service diagnostics |
| `GET /api/v1/snapshot` | Local read or keyed remote | Current per-process traffic |
| `GET /api/v1/processes` | Local read or keyed remote | Tracked processes |
| `GET /api/v1/rules` | Local read or keyed remote | List rules |
| `GET /api/v1/rules/{id}` | Local read or keyed remote | Read one rule |
| `POST /api/v1/rules` | Key required | Add a rule |
| `PUT /api/v1/rules/{id}` | Key required | Update a rule |
| `DELETE /api/v1/rules/{id}` | Key required | Remove a rule |
| `POST /api/v1/rules/import?replace=true` | Key required | Import rule JSON |
| `GET /api/v1/stats/hourly` | Local read or keyed remote | Hourly history |
| `GET /api/v1/stats/daily` | Local read or keyed remote | Daily history |
| `GET /api/v1/stats/top` | Local read or keyed remote | Largest traffic users |
| `GET /api/v1/groups` | Local read or keyed remote | List rule groups |
| `GET /api/v1/groups/{name}` | Local read or keyed remote | Read a rule group |
| `GET /api/v1/quotas` | Local read or keyed remote | Current quota states |
| `GET /api/v1/connections` | Local read or keyed remote | Recent connections |
| `GET /api/v1/alerts/events` | Local read or keyed remote | Recent alert events |
| `GET /api/v1/alerts/rules` | Local read or keyed remote | List alert rules |
| `POST /api/v1/alerts/rules` | Key required | Add an alert rule |
| `PUT /api/v1/alerts/rules/{id}` | Key required | Update an alert rule |
| `DELETE /api/v1/alerts/rules/{id}` | Key required | Remove an alert rule |
| `GET /api/v1/verification` | Key required | Hash a local executable and query VirusTotal |
| `GET /api/v1/geoip` | Key required | Look up a public IP address |
| `GET /api/v1/plugins` | Local read or keyed remote | List loaded plugins |
| `POST /api/v1/plugins/reload` | Key required | Reload plugin manifests |

## Webhook plugins

Plugins are JSON manifests. They send selected events to an HTTPS endpoint and never load a DLL or script into the OpenNetLimit service.

```json
{
  "id": "alert-hook",
  "name": "Alert Hook",
  "version": "1.0.0",
  "enabled": true,
  "eventSubscriptions": ["alert.triggered", "quota.warning", "quota.exceeded"],
  "webhookUrl": "https://example.test/opennetlimit"
}
```

Hostnames that resolve to loopback, private, or link-local addresses are rejected. Webhook requests have a ten-second timeout.
