![OpenNetLimit dashboard and per-app bandwidth controls](assets/marketing/readme-hero.png)

<h1 align="center">OpenNetLimit</h1>

<p align="center"><strong>See which apps use your connection. Set the limits that matter.</strong></p>

<p align="center">
  <a href="https://github.com/SysAdminDoc/OpenNetLimit/releases/latest"><img alt="Version 1.0.2" src="https://img.shields.io/badge/version-1.0.2-27C7F3?style=flat-square"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-4BE5A2?style=flat-square"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0A84FF?style=flat-square">
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-6D4AFF?style=flat-square">
</p>

<p align="center">
  <a href="https://ko-fi.com/X8K126YVER">
    <img height="42" src="https://storage.ko-fi.com/cdn/kofi2.png?v=3" alt="Buy me a coffee on Ko-fi" />
  </a>
</p>

<p align="center">
  <sub><em>If this project helps you, a coffee helps me keep working on it.</em></sub>
</p>

<p align="center">
  <a href="https://github.com/SysAdminDoc/OpenNetLimit/releases/latest"><strong>Download for Windows</strong></a>
  ·
  <a href="#quick-start">Quick start</a>
  ·
  <a href="docs/automation-api.md">Automation guide</a>
</p>

OpenNetLimit gives Windows users a clear view of per-app traffic and direct control over bandwidth. It runs locally and stores traffic history in SQLite. Limits are enforced through WinDivert.

Use it when a download is wrecking a call or when a background app is chewing through a hotspot allowance. OpenNetLimit shows the culprit and lets you act without replacing your firewall.

## What it does

| Capability | What you get |
|---|---|
| Live traffic | Current download and upload rates for each active process, plus a rolling connection chart |
| Per-app control | Independent download and upload ceilings, blocking rules, wildcard paths, and protocol filters |
| Usage history | Hourly or daily charts backed by a local database, with quotas and threshold alerts |
| Repeatable rules | Schedules, app groups, import and export, plus a scriptable command-line client |
| Local automation | A loopback REST API with keyed mutations and an explicit opt-in for remote access |
| Private defaults | Rules and history remain on the PC. VirusTotal, GeoIP, webhooks, and remote access stay off until configured |

## A clearer view of your connection

| Guided first run | Bandwidth history |
|---|---|
| ![OpenNetLimit first-run setup](assets/screenshots/01-first-run.png) | ![OpenNetLimit bandwidth history](assets/screenshots/03-bandwidth-history.png) |

| Focused limit editor | Full light theme |
|---|---|
| ![OpenNetLimit limit editor](assets/screenshots/04-set-limit.png) | ![OpenNetLimit light theme](assets/screenshots/05-light-theme.png) |

## Download

The release ZIP contains a self-contained Windows x64 build. A separate .NET installation is not required.

[Download OpenNetLimit 1.0.2](https://github.com/SysAdminDoc/OpenNetLimit/releases/latest)

Each release includes a SHA-256 checksum file. Compare it before installation if the ZIP came from anywhere other than this repository.

## Quick start

1. Download the latest Windows x64 ZIP and extract it to a permanent folder.
2. Open an Administrator PowerShell window in that folder and run `./Install-Service.ps1`.
3. Launch `OpenNetLimit.UI.exe`.
4. Right-click an application in the traffic list to set or remove its limit.

The service starts with Windows after installation. Run `./Uninstall-Service.ps1` from an Administrator PowerShell window to remove it. Traffic history and rules in `%ProgramData%\OpenNetLimit` are left intact.

### Driver trust note

OpenNetLimit loads the signed WinDivert kernel driver, so Windows requires administrator rights for the service. Some security products flag WinDivert because it is a dual-use network driver. Review the [driver trust and enterprise deployment notes](docs/enterprise-driver-deployment.md) before approving it on a managed PC.

## Everyday use

### Limit an application

1. Find the process in the live traffic table.
2. Right-click it and choose **Set Bandwidth Limit**.
3. Enter either limit in KB/s, then select **OK**.

The rule takes effect immediately. System-critical Windows processes are protected from blocking and rate limits.

### Check traffic history

Open **History**, choose a process or keep **All**, then switch between hourly and daily totals. Data stays in `%ProgramData%\OpenNetLimit\traffic.db`.

### Use the command line

```powershell
./onl.exe status
./onl.exe snapshot
./onl.exe rules add --process steam.exe --download 8192 --upload 1024
./onl.exe stats top --days 7 --limit 10
```

See the [CLI and REST API guide](docs/automation-api.md) for authentication, remote access, and the complete endpoint list.

## Build from source

You need the .NET 8 SDK on Windows.

```powershell
dotnet restore
dotnet build OpenNetLimit.sln -c Release
dotnet test OpenNetLimit.sln -c Release
./scripts/build-release.ps1
```

Marketing screenshots are captured on isolated Windows desktops. The capture process never switches away from the user's current desktop.

```powershell
./scripts/capture-marketing.ps1
```

## How it is put together

| Project | Responsibility |
|---|---|
| `OpenNetLimit.UI` | WPF dashboard, history, rule editing, localization, and theme support |
| `OpenNetLimit.Service` | Background engine, local storage, named-pipe IPC, and REST API |
| `OpenNetLimit.Engine` | WinDivert interception, flow tracking, and packet scheduling |
| `OpenNetLimit.CLI` | Scriptable access to status, rules, history, groups, and quotas |

Persistent state lives in `%ProgramData%\OpenNetLimit`. The UI communicates with the service over a local named pipe. Read operations are available to local users, while rule changes require administrator rights.

## Documentation

- [CLI and REST API](docs/automation-api.md)
- [Driver trust and enterprise deployment](docs/enterprise-driver-deployment.md)
- [Third-party notices](THIRD-PARTY-NOTICES.txt)
- [Changelog](CHANGELOG.md)

## Brand archive

The original crossing-lanes logo directions are preserved in [`assets/brand/concepts`](assets/brand/concepts). The accompanying `selection.json` identifies the approved app-icon direction and its untouched master. Prior social artwork and an unused version-refresh study live in [`assets/marketing/concepts`](assets/marketing/concepts). The complete README hero review, including the rejected clipped layout and selected evergreen final, lives in [`assets/concepts/2026-09-12-readme-hero`](assets/concepts/2026-09-12-readme-hero). Production-ready artwork remains in `assets/brand`, `assets/marketing`, and `src/OpenNetLimit.UI/Assets`.

## License

OpenNetLimit is released under the [MIT License](LICENSE). WinDivert and other bundled components retain their own licenses, which are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
