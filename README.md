# OpenNetLimit

Open-source per-application bandwidth limiter and network monitor for Windows.

A free alternative to [NetLimiter](https://www.netlimiter.com/), built on [WinDivert](https://github.com/basil00/WinDivert).

## Features

- **Per-application bandwidth limiting** — set download/upload speed limits on any process
- **Real-time traffic monitoring** — live per-process bandwidth display with scrolling chart
- **Connection blocking** — block network access for specific applications
- **Traffic statistics** — SQLite-backed hourly/daily usage tracking with historical queries
- **Quota management** — daily/weekly/monthly data caps with auto-throttle or auto-block
- **Rule scheduling** — time-of-day and day-of-week recurring schedules
- **Wildcard rules** — match processes by path patterns (`*\chrome.exe`)
- **Bandwidth priorities** — high/normal/low priority levels per application
- **Import/export rules** — share rule sets between machines
- **System tray** — minimize to tray, quick access context menu
- **Connection logging** — rolling log of established, closed, and blocked connections
- **Windows service detection** — identifies svchost-hosted service names
- **UWP/Store app detection** — identifies AppX package names
- **Secure IPC** — ACL-protected named pipe, admin required for rule changes
- **REST API / remote administration** — localhost API by default, optional keyed remote bind

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

The UI will auto-connect to the service and display live traffic data. On first launch, a setup wizard guides you through requirements.

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

### REST API

The service also exposes a small REST API for local automation and optional remote administration.
By default it listens only on `http://127.0.0.1:47719/`.

| Setting | Description |
|---|---|
| `OPENNETLIMIT_API_URLS` | Semicolon- or comma-separated listener prefixes. Defaults to `http://127.0.0.1:47719/`. |
| `OPENNETLIMIT_API_KEY` | Required for all REST mutations and all remote requests. Send as `X-OpenNetLimit-Key` or `Authorization: Bearer <key>`. |
| `OPENNETLIMIT_ENABLE_REMOTE_API=1` | Allows non-loopback listener prefixes only when `OPENNETLIMIT_API_KEY` is also set. |
| `OPENNETLIMIT_API_DISABLED=1` | Disables the REST listener. |

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

## License

MIT — see [LICENSE](LICENSE).

WinDivert is licensed separately under LGPL-3.0 / GPL-2.0 — see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
