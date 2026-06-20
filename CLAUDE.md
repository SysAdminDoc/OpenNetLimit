# CLAUDE.md — OpenNetLimit

## Build & Test

```powershell
dotnet build OpenNetLimit.sln -c Debug
dotnet test OpenNetLimit.sln -c Debug
```

Requires .NET 8 SDK (pinned via global.json). All projects target net8.0-windows/x64 except Core (net8.0).

## Project Structure

- `src/OpenNetLimit.Core` — Shared models and interfaces (netstandard-compatible)
- `src/OpenNetLimit.Engine` — WinDivert integration, flow tracking, rate limiting (requires SharpDivert, unsafe code)
- `src/OpenNetLimit.Service` — Windows background service hosting the engine
- `src/OpenNetLimit.UI` — WPF desktop GUI
- `src/OpenNetLimit.CLI` — Command-line interface (`onl`) for REST API scripting
- `tests/OpenNetLimit.Tests` — xUnit tests

## File Hygiene

- Only allowed markdown files: README.md, CLAUDE.md, CHANGELOG.md, ROADMAP.md, RESEARCH.md, Roadmap_Blocked.md
- ROADMAP.md contains only actionable items. Delete completed items (don't check off with [x]).
- Move blocked items to Roadmap_Blocked.md with a note explaining the blocker.
- Any newly blocked roadmap item must be removed from ROADMAP.md and added to Roadmap_Blocked.md so ROADMAP.md stays actionable-only.
- Do not create TODO.md, COMPLETED.md, SESSION_SUMMARY.md, or any other tracking markdown files.
- Completed work lives in git history and CHANGELOG.md, not in the roadmap.

## Conventions

- Engine code using SharpDivert requires `AllowUnsafeBlocks` (pointer-based packet headers).
- SharpDivert 1.1.0 API: enums are nested (`WinDivert.Layer`, `WinDivert.Flag`, `WinDivert.Event`), `RecvEx` returns `(uint recvLen, uint addrLen)` tuple, flow data accessed via `addr.Flow.ProcessId`, outbound check via `addr.Outbound` bool property.
- Thread-safe counters in ProcessTrafficInfo use explicit backing fields with `AddBytesSent`/`AddBytesReceived` methods.
- REST API defaults to `http://127.0.0.1:47719/`; writes require `OPENNETLIMIT_API_KEY`, and remote binds require both `OPENNETLIMIT_ENABLE_REMOTE_API=1` and `OPENNETLIMIT_API_KEY`.
- VirusTotal verification is opt-in with `OPENNETLIMIT_VIRUSTOTAL_API_KEY`; it hashes local files only and does not upload executables.
- GeoIP lookup is opt-in with `OPENNETLIMIT_GEOIP_ENABLED=1`; public IPs go to the configured provider, while private/local ranges are suppressed.
- Bandwidth alert rules persist to `%ProgramData%\OpenNetLimit\alerts.json`; UI tray notifications are driven by `ALERT_EVENTS`.
- Plugins are opt-in manifest webhooks under `%ProgramData%\OpenNetLimit\plugins` (or `OPENNETLIMIT_PLUGIN_DIR`); do not load untrusted code in-process.
