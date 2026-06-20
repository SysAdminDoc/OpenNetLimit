# OpenNetLimit

Open-source per-application bandwidth limiter and network monitor for Windows.

A free alternative to [NetLimiter](https://www.netlimiter.com/), built on [WinDivert](https://github.com/basil00/WinDivert).

## Features

- **Per-application bandwidth limiting** — set download/upload speed limits on any process
- **Real-time traffic monitoring** — live per-process bandwidth display with scrolling chart
- **Connection blocking** — block network access for specific applications
- **Traffic statistics** — SQLite-backed hourly/daily usage tracking with historical queries
- **Quota management** — daily/weekly/monthly data caps with auto-throttle or auto-block
- **Bandwidth usage alerts** — threshold rules with cooldowns and tray notifications
- **Rule scheduling** — time-of-day and day-of-week recurring schedules
- **Wildcard rules** — match processes by path patterns (`*\chrome.exe`)
- **Bandwidth priorities** — high/normal/low priority levels per application
- **Import/export rules** — share rule sets between machines
- **System tray** — minimize to tray, quick access context menu
- **Theme support** — persisted dark/light UI toggle from the status bar
- **Localization** — persisted UI language toggle with English and Spanish catalogs
- **Connection logging** — rolling log of established, closed, and blocked connections
- **Windows service detection** — identifies svchost-hosted service names
- **UWP/Store app detection** — identifies AppX package names
- **Secure IPC** — ACL-protected named pipe, admin required for rule changes
- **REST API / remote administration** — localhost API by default, optional keyed remote bind
- **VirusTotal process verification** — optional hash-only executable reputation checks
- **Geographic IP lookup** — optional cached remote IP country/city lookup
- **Plugin webhooks** — optional manifest-based event plugins without in-process code loading

## Requirements

- Windows 10 or later (x64)
- .NET 8.0 Runtime
- Administrator privileges (required for WinDivert driver)

### WinDivert Driver

OpenNetLimit uses the [WinDivert](https://reqrypt.org/windivert.html) user-mode packet capture library. The native binaries (`WinDivert.dll` and `WinDivert64.sys`) are included automatically via the `Native.WinDivert` NuGet package.

**Important notes:**

- The WinDivert driver (`WinDivert64.sys`) is a signed kernel driver that loads on first use and requires administrator privileges.
- Some enterprise security tools (EDR/AV) may flag or block the WinDivert driver. If you encounter driver load failures, check your security software's block list.
- Hypervisor-Protected Code Integrity (HVCI) may prevent the driver from loading on some systems. See [LOLDrivers entry](https://www.loldrivers.io/drivers/45a31a17-f78d-48ec-beba-74f6bfc5f96e/) for details.
- WinDivert is licensed under LGPL-3.0 / GPL-2.0. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

### Enterprise Deployment (WDAC/EDR)

WinDivert's signed driver is tracked by [LOLDrivers](https://www.loldrivers.io/drivers/45a31a17-f78d-48ec-beba-74f6bfc5f96e/) as a dual-use driver and may trigger alerts in enterprise EDR products (CrowdStrike, Defender for Endpoint, etc.). The [SigmaHQ detection rule](https://detection.fyi/sigmahq/sigma/windows/driver_load/driver_load_win_windivert/) `driver_load_win_windivert` fires at HIGH severity on driver load. This is expected for legitimate bandwidth management use — WinDivert is not malware but has been used by adversary tooling in the past.

**Bundled binary hashes (WinDivert 2.2.2 via Native.WinDivert NuGet):**

| File | SHA-256 |
|---|---|
| `WinDivert.dll` (x64) | `C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2` |
| `WinDivert64.sys` (x64) | `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2` |

**WDAC allowlist policy (Windows Defender Application Control):**

To allow WinDivert on WDAC-enforced systems, add the file hashes to your supplemental policy:

```xml
<FileRules>
  <Allow ID="ID_ALLOW_WINDIVERT_DLL" FriendlyName="WinDivert.dll"
         Hash="C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2" />
  <Allow ID="ID_ALLOW_WINDIVERT_SYS" FriendlyName="WinDivert64.sys"
         Hash="8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2" />
</FileRules>
```

**EDR allowlist steps (common products):**

- **Microsoft Defender for Endpoint:** Add WinDivert64.sys path or hash to *Indicators > Allow* in the Defender Security Center.
- **CrowdStrike Falcon:** Create a Machine Learning exclusion for the WinDivert64.sys hash or path under *IOC Management*.
- **SentinelOne:** Add the WinDivert file hashes to the *Exclusions > Hashes* allowlist in the management console.
- **Carbon Black:** Add a bypass rule for the WinDivert driver hash in the *Reputation* tab.

Verify hashes match the values above before adding allowlist entries. If you build WinDivert from source or use a different version, recompute hashes with `certutil -hashfile WinDivert64.sys SHA256`.

## Getting Started

### Building

```powershell
dotnet restore
dotnet build
dotnet test
```

### Running the Service

The service must run as Administrator to load the WinDivert driver:

```powershell
dotnet run --project src/OpenNetLimit.Service
```

### Running the UI

In a separate terminal (does not require admin for monitoring):

```powershell
dotnet run --project src/OpenNetLimit.UI
```

The UI will auto-connect to the service and display live traffic data. On first launch, a setup wizard guides you through requirements. Use the status-bar theme and language buttons to switch dark/light mode and English/Spanish text. Set `OPENNETLIMIT_UI_CULTURE` to override the startup UI culture.

### Setting Bandwidth Limits

1. Right-click a process in the traffic list
2. Select **Set Bandwidth Limit...**
3. Enter download/upload limits in KB/s
4. The limit takes effect immediately via the background service

### Troubleshooting

- **Service won't start:** Ensure you're running as Administrator. Check `%ProgramData%\OpenNetLimit\last-error.txt` for details.
- **UI shows "Disconnected":** The background service isn't running. Start it first.
- **Driver blocked by AV/EDR:** Add WinDivert64.sys to your security software's allow list.
- **HVCI preventing driver load:** WinDivert may be blocked on systems with Hypervisor-Protected Code Integrity enabled.
- **REST API mutation returns 403:** Set `OPENNETLIMIT_API_KEY` and send it with `X-OpenNetLimit-Key`.
- **Remote API does not listen on LAN:** Set both `OPENNETLIMIT_ENABLE_REMOTE_API=1` and `OPENNETLIMIT_API_KEY`, then provide a non-loopback `OPENNETLIMIT_API_URLS` prefix.
- **Process verification says NotConfigured:** Set `OPENNETLIMIT_VIRUSTOTAL_API_KEY`. OpenNetLimit sends only SHA-256 hashes to VirusTotal; it does not upload files.
- **GeoIP lookup says Disabled:** Set `OPENNETLIMIT_GEOIP_ENABLED=1`. Public remote IPs are sent to the configured GeoIP provider and cached; private, loopback, link-local, and multicast addresses are not queried.

## Architecture

- **OpenNetLimit.Core** — Shared models, interfaces, IPC protocol definitions
- **OpenNetLimit.Engine** — WinDivert packet interception, flow tracking, token bucket rate limiting, packet scheduling
- **OpenNetLimit.Service** — Windows background service, rule engine, traffic statistics (SQLite), quota management
- **OpenNetLimit.UI** — WPF desktop GUI with LiveCharts2 real-time charts
- **OpenNetLimit.Tests** — xUnit unit tests

### Data Storage

All persistent data is stored in `%ProgramData%\OpenNetLimit\`:

| File | Purpose |
|---|---|
| `rules.json` | Bandwidth rules and quota configurations |
| `traffic.db` | SQLite database with hourly/daily traffic statistics |
| `last-error.txt` | Last startup error for troubleshooting |
| `logs/` | Service log directory |

### IPC Protocol

The UI communicates with the service via a named pipe (`OpenNetLimit`). Commands:

| Command | Access | Description |
|---|---|---|
| `SNAPSHOT` | Read | Current traffic snapshot with per-process bandwidth |
| `PROCESSES` | Read | All tracked processes |
| `RULES` | Read | All bandwidth rules |
| `STATUS` | Read | Service diagnostics (uptime, counters) |
| `STATS_HOURLY [name]` | Read | Hourly traffic stats (optional process filter) |
| `STATS_DAILY [name]` | Read | Daily traffic stats (optional process filter) |
| `STATS_TOP` | Read | Top processes by total bandwidth |
| `QUOTAS` | Read | All quota states with usage percentages |
| `CONNECTION_LOG` | Read | Last 100 connection log entries |
| `EXPORT_RULES` | Read | Export all rules as JSON |
| `ADD_RULE {json}` | Admin | Add a new bandwidth rule |
| `REMOVE_RULE {guid}` | Admin | Remove a rule by ID |
| `UPDATE_RULE {json}` | Admin | Update an existing rule |
| `IMPORT_RULES {json}` | Admin | Import rules (merge mode) |
| `VERIFY_PROCESS {path}` | Admin | Hash an executable and query VirusTotal if configured |
| `GEOIP {ip}` | Admin | Resolve a public IP address to approximate country/city if enabled |
| `ALERT_RULES` | Read | List bandwidth alert rules |
| `ALERT_EVENTS` | Read | Recent triggered alert events |
| `PLUGINS` | Read | Loaded plugin manifests |
| `ADD_ALERT_RULE {json}` | Admin | Add a bandwidth alert threshold |
| `UPDATE_ALERT_RULE {json}` | Admin | Update a bandwidth alert threshold |
| `REMOVE_ALERT_RULE {guid}` | Admin | Remove a bandwidth alert threshold |
| `RELOAD_PLUGINS` | Admin | Reload plugin manifests from disk |

### REST API

The service also exposes a small REST API for local automation and optional remote administration.
By default it listens only on `http://127.0.0.1:47719/`.

| Setting | Description |
|---|---|
| `OPENNETLIMIT_API_URLS` | Semicolon- or comma-separated listener prefixes. Defaults to `http://127.0.0.1:47719/`. |
| `OPENNETLIMIT_API_KEY` | Required for all REST mutations and all remote requests. Send as `X-OpenNetLimit-Key` or `Authorization: Bearer <key>`. |
| `OPENNETLIMIT_ENABLE_REMOTE_API=1` | Allows non-loopback listener prefixes only when `OPENNETLIMIT_API_KEY` is also set. |
| `OPENNETLIMIT_API_DISABLED=1` | Disables the REST listener. |
| `OPENNETLIMIT_VIRUSTOTAL_API_KEY` | Enables hash-only VirusTotal process verification. |
| `OPENNETLIMIT_VIRUSTOTAL_CACHE_HOURS` | Verification cache duration. Defaults to 12 hours. |
| `OPENNETLIMIT_VIRUSTOTAL_DISABLED=1` | Disables VirusTotal verification even when a key exists. |
| `OPENNETLIMIT_GEOIP_ENABLED=1` | Enables public-IP geolocation lookups. Disabled by default. |
| `OPENNETLIMIT_GEOIP_ENDPOINT` | GeoIP provider base URL. Defaults to `https://free.freeipapi.com/api/json/`. |
| `OPENNETLIMIT_GEOIP_CACHE_HOURS` | GeoIP cache duration. Defaults to 24 hours. |
| `OPENNETLIMIT_PLUGINS_ENABLED=1` | Enables manifest-based webhook plugins. Disabled by default. |
| `OPENNETLIMIT_PLUGIN_DIR` | Plugin manifest directory. Defaults to `%ProgramData%\OpenNetLimit\plugins`. |

Remote administration is intentionally fail-closed: non-loopback prefixes are ignored unless both
`OPENNETLIMIT_ENABLE_REMOTE_API=1` and `OPENNETLIMIT_API_KEY` are configured.

| Endpoint | Access | Description |
|---|---|---|
| `GET /health` | Local read / keyed remote | Liveness check |
| `GET /api/v1/status` | Local read / keyed remote | Service diagnostics |
| `GET /api/v1/snapshot` | Local read / keyed remote | Current traffic snapshot |
| `GET /api/v1/processes` | Local read / keyed remote | Tracked processes |
| `GET /api/v1/rules` | Local read / keyed remote | All bandwidth rules |
| `GET /api/v1/rules/{id}` | Local read / keyed remote | One bandwidth rule |
| `POST /api/v1/rules` | Key required | Add a bandwidth rule |
| `PUT /api/v1/rules/{id}` | Key required | Update a bandwidth rule |
| `DELETE /api/v1/rules/{id}` | Key required | Remove a bandwidth rule |
| `POST /api/v1/rules/import?replace=true` | Key required | Import rule JSON |
| `GET /api/v1/stats/hourly?processName=chrome&hours=24` | Local read / keyed remote | Hourly stats |
| `GET /api/v1/stats/daily?processName=chrome&days=30` | Local read / keyed remote | Daily stats |
| `GET /api/v1/stats/top?days=7&limit=20` | Local read / keyed remote | Top processes by traffic |
| `GET /api/v1/quotas` | Local read / keyed remote | Quota states |
| `GET /api/v1/connections` | Local read / keyed remote | Recent connection log |
| `GET /api/v1/verification?path=C:\Windows\System32\notepad.exe` | Key required | Hash a file and query VirusTotal |
| `GET /api/v1/verification/cache` | Key required | Cached verification results |
| `GET /api/v1/geoip?ip=8.8.8.8` | Key required | Resolve a public IP address to approximate location |
| `GET /api/v1/geoip/cache` | Key required | Cached GeoIP results |
| `GET /api/v1/alerts/rules` | Local read / keyed remote | List bandwidth alert rules |
| `GET /api/v1/alerts/events?limit=100` | Local read / keyed remote | Recent bandwidth alert events |
| `POST /api/v1/alerts/rules` | Key required | Add a bandwidth alert rule |
| `PUT /api/v1/alerts/rules/{id}` | Key required | Update a bandwidth alert rule |
| `DELETE /api/v1/alerts/rules/{id}` | Key required | Remove a bandwidth alert rule |
| `GET /api/v1/plugins` | Local read / keyed remote | Loaded plugin manifests |
| `POST /api/v1/plugins/reload` | Key required | Reload plugin manifests |

### Plugin Manifests

Plugins are declarative JSON webhook manifests. OpenNetLimit does not load plugin DLLs or scripts in-process.

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

## License

MIT — see [LICENSE](LICENSE).

WinDivert is licensed separately under LGPL-3.0 / GPL-2.0 — see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
