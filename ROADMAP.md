# OpenNetLimit — Development Roadmap

## Vision

An open-source, per-application bandwidth limiter and network monitor for Windows — a free alternative to NetLimiter, built on WinDivert.

---

## Phase 2: Service + Basic GUI (v0.2)

**Goal:** Background service with a usable desktop interface.

- [ ] GUI: right-click a process → set download/upload limit
- [ ] GUI: system tray icon with quick access

**Deliverable:** Installable app with tray icon showing per-app bandwidth and allowing limits.

---

## Phase 3: Rules, Blocking & Statistics (v0.3)

**Goal:** Persistent rules, connection blocking, historical data.

- [ ] Connection blocking rules (by app path, IP range, port, domain)
- [ ] Wildcard rules for app paths (e.g., `*\chrome.exe`)
- [ ] Real-time traffic charts (LiveCharts2 or OxyPlot)
- [ ] SQLite-backed traffic statistics (per-process, per-hour/day)
- [ ] Historical traffic reports and graphs
- [ ] Connection log (allowed/blocked, with timestamps)
- [ ] DNS resolution for displaying domain names alongside IPs

**Deliverable:** Feature-complete local traffic manager.

---

## Phase 4: Advanced Features (v0.4)

**Goal:** Quotas, scheduling, priorities.

- [ ] Quota management: set daily/weekly/monthly data caps per app
- [ ] Auto-throttle or auto-block when quota exceeded
- [ ] Quota warnings and notifications
- [ ] Rule scheduling: activate/deactivate rules on a time schedule
- [ ] Bandwidth priority system (high/medium/low priority per app)
- [ ] Windows service detection (filter by svchost service name)
- [ ] UWP / Store app detection and filtering
- [ ] Import/export rule sets
- [ ] Profile system (e.g., "Gaming", "Work", "Metered")

**Deliverable:** Power-user traffic manager rivaling NetLimiter's feature set.

---

## Phase 5: Performance & Polish (v1.0)

**Goal:** Production-quality release.

- [ ] Performance profiling and optimization
- [ ] First-run setup wizard
- [ ] User documentation
- [ ] Accessibility review (keyboard nav, screen reader support)

**Deliverable:** v1.0 public release.

---

## Future (Post-1.0)

- Remote administration (manage other machines)
- REST/gRPC API for third-party integration
- VirusTotal integration for process verification
- Geographic IP location display
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

## Research-Driven Additions

### P1

### P2

- [ ] P2 — Add .NET LTS upgrade and NuGet audit strategy
  Why: .NET 8 support ends November 10, 2026, while this repo targets .NET 8 and already has package-version drift in `Microsoft.Extensions.Hosting.WindowsServices`.
  Evidence: Microsoft .NET lifecycle; NuGet audit docs; `src/OpenNetLimit.Service/OpenNetLimit.Service.csproj`; `dotnet list package --outdated`.
  Touches: shared build props/targets, project files, CI or local audit script, README.
  Acceptance: repository has an explicit .NET 10 LTS migration decision or schedule, restore audit can run as a dedicated gate, and package-update policy is documented.
  Complexity: M

- [ ] P2 — Add local user permissions and read-only monitor mode
  Why: Commercial tools protect who can monitor or change rules, and an elevated local service needs separate view and mutation permissions.
  Evidence: NetLimiter permissions docs; NetBalancer password-protected settings; `PipeServer.cs`.
  Touches: IPC authorization, settings model, UI, tests.
  Acceptance: non-admin viewing and rule mutation are governed separately, read-only mode blocks all mutation commands, and denied attempts are logged and visible.
  Complexity: M

- [ ] P2 — Establish accessibility and localization foundations before UI growth
  Why: The UI is currently hard-coded English with minimal accessibility metadata, while comparable OSS tools ship localization and polished monitor states.
  Evidence: `src/OpenNetLimit.UI/MainWindow.xaml`; simplewall localization support; Sniffnet notification docs.
  Touches: WPF resources, XAML automation properties, UI tests, README.
  Acceptance: user-visible strings move to resources, key controls expose accessible names/status, high-contrast/light/dark checks pass, and a UI automation smoke test covers disconnected, loading, empty, and live states.
  Complexity: M

