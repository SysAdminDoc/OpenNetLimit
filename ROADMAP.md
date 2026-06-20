# OpenNetLimit — Development Roadmap

## Vision

An open-source, per-application bandwidth limiter and network monitor for Windows — a free alternative to NetLimiter, built on WinDivert.

---

## Future (Post-1.0)

- Bandwidth usage alerts and notifications
- Plugin system for extensibility
- Dark mode / theme support
- Localization / i18n

---

## Technical Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Packet interception | WinDivert (user-mode) | Signed driver included, no kernel dev needed, MIT-compatible |
| .NET wrapper | SharpDivert NuGet | Thin, maintained C# bindings for WinDivert 2.x |
| Runtime | .NET 8 (LTS) | Long-term support, good performance, modern C# features |
| GUI framework | WPF | Native Windows, rich data binding, mature ecosystem |
| Service hosting | .NET Worker Service | Built-in Windows service support via `Microsoft.Extensions.Hosting.WindowsServices` |
| IPC | Named Pipes | Simple, fast, built into .NET, no external dependencies |
| Statistics DB | SQLite (via Microsoft.Data.Sqlite) | Embedded, zero-config, good for time-series aggregation |
| Charts | LiveCharts2 | Open-source, WPF-native, good real-time performance |
| Rate limiting | Token bucket | Smooth bandwidth enforcement, industry standard algorithm |
| License | MIT | Maximum adoption, compatible with WinDivert's LGPL |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                   WPF GUI (UI)                       │
│  Process list │ Traffic charts │ Rule editor          │
└───────────────────────┬─────────────────────────────┘
                        │ Named Pipes (IPC)
┌───────────────────────▼─────────────────────────────┐
│              Background Service                      │
│  Rule Engine │ Traffic Monitor │ Stats (SQLite)       │
└───────────────────────┬─────────────────────────────┘
                        │ In-process
┌───────────────────────▼─────────────────────────────┐
│                    Engine                            │
│                                                      │
│  ┌─────────────┐    ┌──────────────────────────┐     │
│  │ Flow Tracker │    │ Process Rate Limiter     │     │
│  │ (FLOW layer) │    │ (token bucket per proc)  │     │
│  │             │    │                          │     │
│  │ Maps 5-tuple│───▶│ Queues packets when      │     │
│  │ → processId │    │ over budget, releases    │     │
│  └─────────────┘    │ on token refill          │     │
│                     └──────────────────────────┘     │
│  ┌──────────────────────────────────────────────┐    │
│  │ WinDivert Interceptor (NETWORK layer)        │    │
│  │ Captures all packets, parses headers,         │    │
│  │ looks up process via FlowTracker,             │    │
│  │ applies rate limiting, reinjects              │    │
│  └──────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────┘
                        │
              WinDivert Driver (kernel)
                        │
                 Windows Network Stack
```
