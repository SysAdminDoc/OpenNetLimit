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
./onl.exe rules add --service DoSvc --download 1024
./onl.exe rules remove <rule-id>
./onl.exe stats hourly --process steam.exe --hours 24
./onl.exe stats top --days 7 --limit 20
./onl.exe pc-limit
./onl.exe pc-limit set --download 1024 --upload 256 --include-lan
./onl.exe pc-limit off
./onl.exe selftest
```

Limits passed to `--download` and `--upload` use KB/s. Rule actions accept `Limit`, `Block`, or `Allow`. A process name works with or without `.exe`, so `steam` and `steam.exe` are the same rule.

`--service` takes a Windows service's short name (the one `sc query` and Task Manager's Services tab show, like `BITS`, `DoSvc` or `wuauserv`) in place of `--process`. Give one of them, never both. The rule follows the service into whichever `svchost.exe` Windows runs it in. When the svchost holding it also runs a service Windows needs to stay online (DNS, DHCP, RPC, the firewall and the like), the rule is kept but nothing in that svchost is limited. A rule on `svchost` by name, or on one of those networking services, is refused with the reason.

`./onl.exe pc-limit set` caps every app's traffic added together. A direction you leave out isn't capped, and it counts only internet traffic unless you add `--include-lan`. Traffic that never leaves the PC isn't counted either way. `pc-limit off` turns it off, and plain `pc-limit` shows what's set. Like `rules add`, it needs `OPENNETLIMIT_API_KEY`.

`./onl.exe selftest` checks that limits work on this PC. It puts a 256 KB/s upload limit on itself (pick another rate with `--limit <KB/s>`), sends data to itself over loopback, and removes the rule when it's done. It prints PASS and exits 0 when the rate held at the limit and came back after the rule was removed. It needs `OPENNETLIMIT_API_KEY` because it adds a rule.

The client reads these environment variables:

| Variable | Purpose |
|---|---|
| `OPENNETLIMIT_API_URL` | API base URL. The default is `http://127.0.0.1:47719`. |
| `OPENNETLIMIT_API_KEY` | Key used for mutations and every remote request. |

## Local API behavior

Local read endpoints do not require a key. Mutating requests require `OPENNETLIMIT_API_KEY`, sent through either `X-OpenNetLimit-Key` or `Authorization: Bearer <key>`.

Each caller gets a burst of 100 requests, then 50 a second. Every program on the PC counts as one caller (all of 127.0.0.0/8 and `::1`), and an IPv6 address counts by its /64. A request past that waits its turn instead of failing, so a script can add rules one at a time as fast as it likes. Only when 100 of its requests are already waiting does the next one get `429`. Wrong keys have a budget of their own, 10 tries that come back one every 6 seconds. A wrong key uses its try the moment it arrives, so sending guesses all at once gets no more of them through. Once the tries are gone, every request from that caller that carries a key gets `429` until one comes back. Reads without a key still work meanwhile. Every `429` has a `Retry-After` header with the seconds to wait.

A service that couldn't load its rules (it isn't running as administrator) answers changes to `/api/v1/rules`, `/api/v1/alerts/rules` and `/api/v1/pc-limit` with `503` and the reason in `error`. Reads still work.

`PUT /api/v1/pc-limit` takes `{"downloadBytesPerSecond": 1048576, "uploadBytesPerSecond": 0, "includeLan": false}` and answers with the limit now in effect. Rates are bytes per second, and 0 leaves that direction uncapped. Field names can be in any case. A negative rate, a field it doesn't know, a field given twice (in any mix of cases), a body with no rate in it (`{}` included) or one that isn't a limit at all gets `400` with the reason in `error`, and the limit already set stays.

`PATCH /api/v1/pc-limit` changes only the fields you send and keeps the rest as they are at that moment, so `{"uploadBytesPerSecond": 262144}` caps uploads without touching a download cap someone else just set. Any one field is enough. An empty body (`{}`), a field set to `null` or a negative rate gets `400`. `DELETE` is the same as a `PATCH` of both rates to 0: the limit goes off and `includeLan` stays for the next time it's set.

A rule sent to `POST /api/v1/rules` or `PUT /api/v1/rules/{id}` names its target with `processName`, `processPath` or `serviceName`. A `serviceName` rule leaves the other two out. The same mistakes `rules add` turns away (a service beside a program, a networking service, a new rule on `svchost` by name) get `400` with the reason in `error`. An older rule on `svchost` by name can still be updated, so it can be turned off. In `GET /api/v1/snapshot`, each svchost carries `services` (the short names Windows runs in it, or `null` before they're known), `isProtected`, and `networkingServices` (the ones that keep it from being limited).

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
| `GET /api/v1/pc-limit` | Local read or keyed remote | Read the whole-PC limit |
| `PUT /api/v1/pc-limit` | Key required | Set the whole-PC limit |
| `PATCH /api/v1/pc-limit` | Key required | Change part of the whole-PC limit |
| `DELETE /api/v1/pc-limit` | Key required | Turn the whole-PC limit off |
| `POST /api/v1/engine/pause?stay=true` | Key required | Pause filtering and unload the driver. `stay=true` keeps it paused after a restart |
| `POST /api/v1/engine/resume` | Key required | Resume filtering |
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
