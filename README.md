# OpenNetLimit

Open-source per-application bandwidth limiter and network monitor for Windows.

A free alternative to [NetLimiter](https://www.netlimiter.com/), built on [WinDivert](https://github.com/basil00/WinDivert).

## Status

**Early development** — the core engine compiles and tests pass, but the service requires a working WinDivert driver installation to run. The UI is not yet connected to the service.

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
- Administrator privileges (required for WinDivert driver)

### WinDivert Driver

OpenNetLimit uses the [WinDivert](https://reqrypt.org/windivert.html) user-mode packet capture library. The native binaries (`WinDivert.dll` and `WinDivert64.sys`) are included automatically via the `Native.WinDivert` NuGet package.

**Important notes:**

- The WinDivert driver (`WinDivert64.sys`) is a signed kernel driver that loads on first use and requires administrator privileges.
- Some enterprise security tools (EDR/AV) may flag or block the WinDivert driver. If you encounter driver load failures, check your security software's block list.
- Hypervisor-Protected Code Integrity (HVCI) may prevent the driver from loading on some systems. See [LOLDrivers entry](https://www.loldrivers.io/drivers/45a31a17-f78d-48ec-beba-74f6bfc5f96e/) for details.
- WinDivert is licensed under LGPL-3.0 / GPL-2.0. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Building

```powershell
dotnet restore
dotnet build
dotnet test
```

## Running

The service must run as Administrator to load the WinDivert driver:

```powershell
dotnet run --project src/OpenNetLimit.Service
```

If the driver fails to load, the service will log the error and write diagnostic info to `%ProgramData%\OpenNetLimit\last-error.txt`.

## Architecture

- **OpenNetLimit.Core** — Shared models, interfaces, IPC protocol
- **OpenNetLimit.Engine** — WinDivert integration, flow tracking, rate limiting, packet scheduling
- **OpenNetLimit.Service** — Windows background service hosting the engine
- **OpenNetLimit.UI** — WPF desktop GUI
- **OpenNetLimit.Tests** — Unit and integration tests

## License

MIT — see [LICENSE](LICENSE).

WinDivert is licensed separately under LGPL-3.0 / GPL-2.0 — see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
