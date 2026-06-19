# OpenNetLimit — Development Roadmap

## Vision

An open-source, per-application bandwidth limiter and network monitor for Windows — a free alternative to NetLimiter, built on WinDivert.

---

## Phase 0: Foundation (Current)

**Goal:** Project scaffold, build system, core abstractions.

- [x] Research & feasibility study
- [x] Project scaffold with .NET 8 solution
- [x] Define core interfaces (IPacketInterceptor, IRateLimiter, IRuleEngine, ITrafficMonitor)
- [x] Define data models (BandwidthRule, ConnectionInfo, ProcessTrafficInfo)
- [x] Token bucket rate limiter implementation
- [x] Unit test infrastructure

**Deliverable:** Buildable solution with core library and tests.

---

## Phase 1: MVP — Per-Process Bandwidth Limiting (v0.1)

**Goal:** Capture packets, identify owning process, enforce speed limits.

- [ ] WinDivert FLOW layer integration — track (flow → process ID) mappings
- [ ] WinDivert NETWORK layer integration — capture and reinject packets
- [ ] Flow-to-packet correlation (match packets to processes via 5-tuple lookup)
- [ ] Token bucket enforcement — queue packets when over limit, release on schedule
- [ ] Per-process upload & download rate limiting
- [ ] Console test harness (set a limit, observe throttling)
- [ ] Integration tests with loopback traffic

**Technical Notes:**
- WinDivert's `ProcessId` field is only available at FLOW and SOCKET layers, NOT the NETWORK layer.
- Architecture: FLOW handle watches connection lifecycle and builds a lookup table of `(protocol, localAddr, localPort, remoteAddr, remotePort) → processId`.
- NETWORK handle captures actual packets. For each packet, parse headers, look up the 5-tuple in the flow table to find the owning process.
- Rate limiting uses a token bucket per process: tokens refill at the configured bytes/sec rate, each packet consumes tokens equal to its size. If tokens are insufficient, the packet is queued and reinjected after a calculated delay.

**Deliverable:** CLI tool that throttles a named process to a specified bandwidth.

---

## Phase 2: Service + Basic GUI (v0.2)

**Goal:** Background service with a usable desktop interface.

- [ ] Windows service (or background worker) hosting the engine
- [ ] Named pipe IPC between service and GUI
- [ ] IPC protocol: query active processes, get traffic stats, add/remove rules
- [ ] WPF GUI: main window with process list, live bandwidth per process
- [ ] GUI: right-click a process → set download/upload limit
- [ ] GUI: system tray icon with quick access
- [ ] Real-time traffic display (bytes/sec per process, total)
- [ ] Start-on-boot option

**Deliverable:** Installable app with tray icon showing per-app bandwidth and allowing limits.

---

## Phase 3: Rules, Blocking & Statistics (v0.3)

**Goal:** Persistent rules, connection blocking, historical data.

- [ ] Persistent rule storage (JSON or SQLite)
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
- [ ] Optional WFP callout driver for lower-latency packet handling
- [ ] Signed installer (MSIX or Inno Setup)
- [ ] First-run setup wizard
- [ ] Auto-update mechanism
- [ ] Comprehensive error handling and logging
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

### P0

- [ ] P0 — Restore solution build and test harness
  Why: The advertised foundation is not buildable; tests cannot restore and the engine has wrapper/API compile errors.
  Evidence: `dotnet build OpenNetLimit.sln -c Debug`; `tests/OpenNetLimit.Tests/OpenNetLimit.Tests.csproj`; `src/OpenNetLimit.Engine/Interception/WinDivertInterceptor.cs`; `src/OpenNetLimit.Engine/Monitoring/TrafficMonitor.cs`; SharpDivert 1.1.0 README/API.
  Touches: `*.csproj`, `WinDivertInterceptor.cs`, `TrafficMonitor.cs`, tests.
  Acceptance: `dotnet restore`, `dotnet build OpenNetLimit.sln -c Debug`, and `dotnet test OpenNetLimit.sln -c Debug` pass on Windows x64.
  Complexity: M

- [ ] P0 — Package WinDivert and document license/trust requirements
  Why: SharpDivert requires a separate WinDivert v2.2 binary, admin privileges, and careful DLL/driver trust handling before any user can run the service.
  Evidence: SharpDivert NuGet README; WinDivert homepage; LOLDrivers `windivert.sys` entry; `src/OpenNetLimit.Engine/OpenNetLimit.Engine.csproj`.
  Touches: packaging project files, app manifest/service install assets, README, third-party notices.
  Acceptance: build output or installer includes the expected WinDivert runtime files with published hashes, admin/service launch path, third-party license notice, and clear error when the driver cannot load.
  Complexity: M

- [ ] P0 — Secure the elevated named-pipe IPC boundary
  Why: The service accepts rule mutation commands over a default-ACL pipe, which is unsafe for an elevated traffic-control process.
  Evidence: `src/OpenNetLimit.Service/IPC/PipeServer.cs`; Microsoft named-pipe security docs; NetLimiter user permissions.
  Touches: `PipeServer`, IPC DTOs, service host, UI client, IPC tests.
  Acceptance: pipe ACL allows only intended local principals, client identity is checked, commands are schema-versioned and validated, unauthorized clients are denied in tests.
  Complexity: M

- [ ] P0 — Define fail-safe service lifecycle and recovery behavior
  Why: Users need predictable behavior when the service, driver, or rule store fails; competitor issues show filtering durability and lost diagnostics are trust breakers.
  Evidence: `src/OpenNetLimit.Service/EngineWorker.cs`; Fort Firewall issue #6; Fort Firewall issue #435; NetLimiter components model.
  Touches: `EngineWorker`, service install configuration, rule load/save path, logging.
  Acceptance: startup validates prerequisites before activation, shutdown/crash behavior is documented and configurable, service recovery is configured, last-known startup failure is visible to UI and logs.
  Complexity: M

- [ ] P0 — Replace capture-thread sleep with bounded packet scheduling
  Why: `Thread.Sleep` inside the network receive loop can stall unrelated traffic and hide queue/drop behavior under load.
  Evidence: `src/OpenNetLimit.Engine/Interception/WinDivertInterceptor.cs`; Portmaster latency issue #1141; WinDivert NETWORK/FLOW layer docs.
  Touches: `WinDivertInterceptor`, `ProcessRateLimiter`, packet queue abstractions, stress tests.
  Acceptance: over-limit packets enter bounded per-process queues with cancellation, max-delay/drop policy, delay/drop counters, and throughput/latency tests.
  Complexity: L

### P1

- [ ] P1 — Add a rule-to-enforcement reconciler with schema migration
  Why: Rules currently persist separately from limiter state, `ADD_RULE` does not enforce limits, and `REMOVE_RULE` clears all live limits.
  Evidence: `src/OpenNetLimit.Service/IPC/PipeServer.cs`; `src/OpenNetLimit.Core/Models/BandwidthRule.cs`; OpenSnitch rule schema; NetBalancer rules docs.
  Touches: `RuleEngine`, `PipeServer`, `IRateLimiter`, rule storage, migration tests.
  Acceptance: every rule add/update/remove atomically updates live enforcement, rule files are versioned/validated/migrated, and tests cover priority, direction, path wildcard, remote address/port, and disabled schedules.
  Complexity: M

- [ ] P1 — Add retained diagnostics and redacted support bundles
  Why: Packet filters need durable evidence after outages; current service logs are not packaged or exposed, and Fort users reported losing useful logs after closure.
  Evidence: Fort Firewall issue #435; Portmaster debug information pattern; `src/OpenNetLimit.Service/EngineWorker.cs`.
  Touches: service logging, `%ProgramData%\OpenNetLimit`, UI diagnostics view/export, documentation.
  Acceptance: rolling logs and counters include driver load, flow map size, queue delays/drops, rule changes, IPC denials, and a UI/CLI support bundle with redaction.
  Complexity: M

- [ ] P1 — Wire the WPF shell to the service with honest states
  Why: The UI currently builds but remains a static disconnected grid with no pipe client, empty/loading/error states, or rule actions.
  Evidence: `src/OpenNetLimit.UI/ViewModels/MainViewModel.cs`; `src/OpenNetLimit.UI/MainWindow.xaml`; GlassWire features; Sniffnet program monitoring docs.
  Touches: UI view models, pipe client, XAML states, accessibility metadata.
  Acceptance: UI auto-connects to the service, shows driver/admin/service status, renders loading/empty/error states, displays live processes, and disables mutation controls when disconnected or unauthorized.
  Complexity: M

- [ ] P1 — Build a connection/accounting model for adapters, IPv4/IPv6, TCP/UDP, and process identity
  Why: Competitors expose per-app connection detail and adapter-aware traffic; current `ConnectionInfo` byte counters and active connection counts are not updated.
  Evidence: `src/OpenNetLimit.Core/Models/ConnectionInfo.cs`; `src/OpenNetLimit.Engine/Monitoring/TrafficMonitor.cs`; NetLimiter basic features; NetBalancer feature table; Sniffnet programs docs.
  Touches: `FlowTracker`, `TrafficMonitor`, packet parser adapter, UI process/connection views, tests.
  Acceptance: snapshots include protocol, direction, adapter, endpoint, PID/name/path/service where available, bytes sent/received, active connection counts, and sortable/filterable UI data.
  Complexity: L

- [ ] P1 — Create a Windows compatibility and performance matrix
  Why: WinDivert/WFP tools are sensitive to HVCI, VPNs, tethering, IPv6, UDP, and high throughput; these are release-blocking reliability conditions.
  Evidence: Fort Firewall README HVCI note; Fort Firewall issue #435; WinDivert docs; NetLimiter release compatibility notes.
  Touches: integration test harness, benchmark scripts, release checklist, docs.
  Acceptance: scripted smoke tests cover loopback and external traffic, IPv4/IPv6, TCP/UDP, VPN coexistence, USB tethering, HVCI state, and high-throughput delay/drop metrics.
  Complexity: L

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

