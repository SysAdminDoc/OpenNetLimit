# Research - OpenNetLimit

## Executive Summary
OpenNetLimit is a Windows-only, .NET/WPF attempt to build an open-source NetLimiter-style per-application bandwidth limiter on top of WinDivert. Verified: the repository has a useful domain model and testable primitives for flow tracking, rule ordering, and token buckets, but the core service/engine and test suite do not currently build, so the highest-value direction is to turn the scaffold into a trustworthy local control plane before adding parity features. Priority opportunities: restore build/test health; align the WinDivert wrapper API and runtime packaging; secure the elevated named-pipe IPC boundary; define fail-safe service behavior and retained diagnostics; replace capture-thread sleeps with bounded packet scheduling; reconcile rule storage with live limiter state; wire the WPF shell to the service with honest disconnected/error states; build compatibility benchmarks for HVCI/VPN/tethering/IPv6; add an upgrade/security-audit path before .NET 8 support ends; delay remote/plugin novelty until local reliability is proven.

## Product Map
- Core workflows: run elevated packet service; correlate WinDivert flow events to process identity; apply per-process upload/download limits; expose snapshots/rules over named pipes; display process traffic in WPF.
- User personas: power users on metered/slow links; gamers/streamers avoiding background-app contention; Windows administrators needing local per-app policy; OSS users wanting a NetLimiter/NetBalancer alternative.
- Platforms and distribution: Windows 10+ x64 today; .NET 8 WPF/Worker Service; WinDivert driver/admin requirement; no installer, manifest, bundled WinDivert runtime, CI, or Git metadata present.
- Key integrations and data flows: WinDivert FLOW/SOCKET-like process mapping plus NETWORK packet capture; `RuleEngine` JSON in `%ProgramData%\OpenNetLimit\rules.json`; named pipe `OpenNetLimit`; future SQLite/statistics/charts are roadmap-only.

## Competitive Landscape
- NetLimiter: strongest reference for per-app limits, single-connection limits, priorities, quotas, scheduler, user permissions, remote admin, Store/service detection, API, and VirusTotal support. Learn the rule/permission/diagnostic depth; avoid copying remote admin before local IPC is safe.
- NetBalancer: strong process limits/priorities, adapter-specific controls, global limits, tray mini-window, password-protected settings, traffic history, and sync service. Learn adapter-aware limits and protected settings; avoid cloud/sync complexity until local data and auth are stable.
- Fort Firewall: closest OSS Windows competitor; supports app/global rules, path wildcards, parent-process rules, svchost service filters, speed-limit app groups, zones, statistics, bandwidth graphs, and its own driver. Learn Windows-specific process identity and diagnostics; avoid its reported pain around filtering durability/log loss.
- simplewall: mature WFP-based Windows firewall with permanent/temporary filters, logging, localization, IPv6, WSL, Store, service support, GPG-signed binaries, and CLI install/uninstall. Learn persistent system-rule lifecycle and uninstall cleanup; avoid ambiguous "not Windows Firewall" positioning.
- Portmaster: privacy firewall with free rules and paid network history/bandwidth visibility. Learn debug bundles, WFP state reporting, filter lists, and per-app network history; avoid latency for allowed traffic and prompt UX confusion.
- GlassWire: best UX benchmark for visual history, discreet alerts, bandwidth usage, remote monitoring, profile firewall control, threat checks, dark themes, and management console. Learn approachable visual states and alerts; avoid overpromising malware/IDS protection.
- Sniffnet: fast OSS monitor with program identification, favorites, notifications, IP blacklists, and adapter previews. Learn program-centric monitoring and notification thresholds; avoid monitor-only scope drift because OpenNetLimit's differentiator is control.
- OpenSnitch/TrafficToll: useful adjacent references for temporary/permanent rules, rule precedence, process command/parent matching, and CLI-configured per-process Linux traffic shaping. Learn rule schema flexibility and revert-on-exit safety; avoid Linux `tc` assumptions on Windows.

## Security, Privacy, and Reliability
- Verified: `dotnet build OpenNetLimit.sln -c Debug` fails because `tests/OpenNetLimit.Tests/OpenNetLimit.Tests.csproj` targets `net8.0` while referencing `src/OpenNetLimit.Engine` targeting `net8.0-windows`; `dotnet test` cannot restore.
- Verified: `src/OpenNetLimit.Engine/Interception/WinDivertInterceptor.cs` uses symbols and shapes not present in SharpDivert 1.1.0 (`WinDivertLayer`, `WinDivertOpenFlags`, `WinDivertParser`, `addr.ProcessId`, `addr.Flags`, scalar `RecvEx` result). The installed SharpDivert README/API uses `WinDivert.Layer`, `WinDivert.Flag`, tuple `RecvEx`, `WinDivertAddress.Flow.ProcessId`, `Outbound`, and `WinDivertIndexedPacketParser`.
- Verified: `src/OpenNetLimit.Engine/Monitoring/TrafficMonitor.cs` does not compile because `Interlocked.Add(ref existing.TotalBytesSent, ...)` and `Interlocked.Add(ref existing.TotalBytesReceived, ...)` target properties.
- Verified: `src/OpenNetLimit.Service/IPC/PipeServer.cs` creates a named pipe without an explicit security descriptor, then accepts line-based `ADD_RULE`/`REMOVE_RULE` commands. Microsoft documents that default pipe ACLs can grant read access to Everyone and anonymous users; an elevated traffic-control service needs explicit client identity and authorization.
- Verified: `ADD_RULE` only calls `_ruleEngine.AddRule(rule)` and never updates `_rateLimiter`; `REMOVE_RULE` removes one rule but calls `_rateLimiter.RemoveAll()`, so live enforcement can drift from stored rules.
- Verified: `src/OpenNetLimit.Engine/Interception/WinDivertInterceptor.cs` sleeps inside the packet receive loop (`Thread.Sleep(delay)`), which risks blocking unrelated packets and makes cancellation/backpressure unclear.
- Verified: SharpDivert requires downloading WinDivert v2.2 and placing the binary in the program directory; OpenNetLimit has no runtime packaging, app manifest, third-party notice, driver hash verification, or AV/EDR guidance.
- Verified: WinDivert's signed driver is also tracked by LOLDrivers as a driver that may be blocked in enterprise controls; packaging needs trust documentation and clear hashes rather than silent driver load failure.
- Likely: `RuleEngine.SaveRules` writes JSON directly without atomic replace, schema versioning, backup, or validation, so a crash or malformed rule file can corrupt startup policy.
- Likely: the UI currently builds but remains a disconnected static grid (`MainViewModel.StatusText = "Disconnected"`) with no pipe client, empty/loading/error states, rule controls, or accessibility labels.

## Architecture Assessment
- Keep the four-project split (`Core`, `Engine`, `Service`, `UI`), but introduce a testable WinDivert adapter boundary so packet parsing, flow correlation, limiter decisions, and service recovery can be exercised without loading the driver.
- Move build policy into a shared props/targets file: align Windows TFMs for tests, set x64 consistently, define supported SDK/runtime, and add NuGet audit behavior before adding dependencies.
- Replace ad hoc string IPC with a versioned request/response contract, explicit ACLs, identity checks, command validation, and integration tests using a non-admin denied client and an authorized client.
- Refactor rule application into a reconciler that projects persisted rules into limiter/blocker state, including priority, wildcard/path matching, remote address/port/domain predicates, direction, schedule windows, and future migrations.
- Add observability from day one: rolling service logs, WinDivert open/load errors, packet queue metrics, dropped/delayed counters, current WFP/driver state, and a redacted support bundle.
- Add distribution hardening before release: manifest/service installer, WinDivert binary placement, third-party licenses, GPG/Authenticode/hash publishing, uninstall cleanup, and failure messages when admin/driver prerequisites are missing.
- Test gaps: no successful solution build, no integration tests for IPC, packet loop, WinDivert adapter, rules persistence, UI service states, high-throughput limiter behavior, IPv6/UDP, or Windows compatibility conditions.
- Documentation gaps: README still presents planned features as the product shape; it lacks current status, admin/driver prerequisites, unsupported states, license implications, and troubleshooting.

## Rejected Ideas
- Rewrite as a router/network-wide bandwidth controller; GlassWire community discussion notes host tools cannot generally control every gateway/subnet, and OpenNetLimit's differentiator is local per-app control.
- Start with a custom WFP callout driver; Microsoft recommends user-mode WFP management when built-in filtering is enough, and the current project has not yet proven a WinDivert MVP.
- Add remote administration now; NetLimiter/NetBalancer prove demand, but current local named-pipe auth and rule reconciliation are not safe enough.
- Add VirusTotal/threat-intel features now; NetLimiter and GlassWire include them, but OpenNetLimit first needs reliable process identity, logging, and privacy controls.
- Switch to Electron/web UI; HN/Portmaster feedback highlights UI-stack friction, and the existing repo is already a lightweight WPF Windows app.
- Build a plugin ecosystem before v1; the project still lacks a stable rule schema, IPC contract, and security model for third-party code.
- Mobile/cross-platform clients; WinDivert and WPF make this a Windows-local product, and adjacent tools cover cross-platform monitoring without solving Windows throttling.

## Sources
Direct competitors:
- https://www.netlimiter.com/features
- https://www.netlimiter.com/releases
- https://www.netlimiter.com/docs/netlimiter-overview/basic-features
- https://www.netlimiter.com/docs/basic-concepts/quota-rule
- https://www.netlimiter.com/docs/security/permissions
- https://seriousbit.com/netbalancer/
- https://netbalancer.com/docs
- https://www.glasswire.com/features/
- https://forum.glasswire.com/t/per-app-bandwidth-control-limitation-prioritization-a-la-netbalancer/2152
- https://news.ycombinator.com/item?id=29761978

OSS and adjacent projects:
- https://github.com/tnodir/fort
- https://github.com/tnodir/fort/issues/6
- https://github.com/tnodir/fort/issues/435
- https://github.com/henrypp/simplewall
- https://github.com/safing/portmaster
- https://github.com/safing/portmaster/issues/1141
- https://github.com/evilsocket/opensnitch/wiki/Rules
- https://github.com/cryzed/TrafficToll
- https://sniffnet.app/news/v1.5/
- https://github.com/GyulyVGC/sniffnet/wiki/Programs
- https://github.com/GyulyVGC/sniffnet/wiki/Notifications

Platform, dependency, and security:
- https://github.com/basil00/WinDivert/wiki/WinDivert-Documentation
- https://reqrypt.org/windivert.html
- https://www.nuget.org/packages/SharpDivert/
- https://learn.microsoft.com/en-us/windows/win32/fwp/about-windows-filtering-platform
- https://learn.microsoft.com/en-us/windows-hardware/drivers/network/introduction-to-windows-filtering-platform-callout-drivers
- https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
- https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core
- https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- https://www.loldrivers.io/drivers/45a31a17-f78d-48ec-beba-74f6bfc5f96e/

## Open Questions
- Does OpenNetLimit need to preserve an MIT-only distribution story, or is LGPL/GPL-compatible redistribution of SharpDivert/WinDivert acceptable with notices and source-offer obligations?
- Should the default failure mode be fail-open, fail-closed, or user-selectable when the service, driver, or rule store fails?
- Is first public distribution expected as portable zip, MSIX, winget/installer, or service-only package? Driver placement, signing, and update strategy depend on that choice.
