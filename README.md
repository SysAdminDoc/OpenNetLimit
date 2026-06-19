# OpenNetLimit

Open-source per-application bandwidth limiter and network monitor for Windows.

A free alternative to [NetLimiter](https://www.netlimiter.com/), built on [WinDivert](https://github.com/basil00/WinDivert).

## Features (Planned)

- Per-application upload/download bandwidth limiting
- Real-time traffic monitoring per process
- Connection blocking rules (by app, IP, port, domain)
- Traffic statistics and historical charts
- Quota management with auto-throttle
- Rule scheduling
- System tray integration

## Requirements

- Windows 10 or later (x64)
- .NET 8.0 Runtime
- Administrator privileges (required for WinDivert packet capture)

## Building

```powershell
dotnet restore
dotnet build
```

## Running

The service must run as Administrator to load the WinDivert driver:

```powershell
dotnet run --project src/OpenNetLimit.Service
```

## Architecture

- **OpenNetLimit.Core** — Shared models, interfaces, configuration
- **OpenNetLimit.Engine** — WinDivert integration, flow tracking, rate limiting
- **OpenNetLimit.Service** — Background service hosting the engine
- **OpenNetLimit.UI** — WPF desktop GUI
- **OpenNetLimit.Tests** — Unit and integration tests

See [ROADMAP.md](ROADMAP.md) for the development plan and [RESEARCH.md](RESEARCH.md) for technical background.

## License

MIT — see [LICENSE](LICENSE).
