# OpenNetLimit — Blocked Roadmap Items

Items moved here from ROADMAP.md because they require external resources, hardware configurations, or manual testing that cannot be completed autonomously.

---

## P1 — Create a Windows compatibility and performance matrix
**Blocker:** Requires running the service with a live WinDivert driver across multiple Windows configurations (HVCI enabled/disabled, VPN active, USB tethering, various AV/EDR tools). These are physical/VM environment requirements that cannot be automated without the target environments.

Why: WinDivert/WFP tools are sensitive to HVCI, VPNs, tethering, IPv6, UDP, and high throughput; these are release-blocking reliability conditions.
Evidence: Fort Firewall README HVCI note; Fort Firewall issue #435; WinDivert docs; NetLimiter release compatibility notes.
Touches: integration test harness, benchmark scripts, release checklist, docs.
Acceptance: scripted smoke tests cover loopback and external traffic, IPv4/IPv6, TCP/UDP, VPN coexistence, USB tethering, HVCI state, and high-throughput delay/drop metrics.
Complexity: L

---

## Phase 1 — Integration tests with loopback traffic
**Blocker:** Requires administrator privileges and a loaded WinDivert driver to capture and reinject loopback packets. Cannot be run in a standard CI/dev environment without elevated permissions.

## Phase 1 — Console test harness (set a limit, observe throttling)
**Blocker:** Requires administrator privileges and a loaded WinDivert driver to observe real throttling behavior.

## Phase 2 — Start-on-boot option
**Blocker:** Requires Windows service installation (`sc.exe create` or installer), which needs admin privileges and modifies system state.

## Phase 5 — Optional WFP callout driver for lower-latency packet handling
**Blocker:** Requires kernel driver development, WHQL signing, and specialized testing infrastructure.

## Phase 5 — Signed installer (MSIX or Inno Setup)
**Blocker:** Requires code signing certificate and installer toolchain decision.

## Phase 5 — Auto-update mechanism
**Blocker:** Requires hosted update server/GitHub releases infrastructure and update protocol design.

## Phase 5 — Performance profiling and optimization
**Blocker:** Requires running the service with a live WinDivert driver under real network traffic to identify actual bottlenecks. Cannot meaningfully profile packet processing, queue latency, or memory pressure without the driver loaded and real traffic flowing.
