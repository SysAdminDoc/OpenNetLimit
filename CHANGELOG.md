# Changelog

## Unreleased

### Added
- Bounded packet scheduler: rate-limited packets enter per-process queues instead of blocking the capture thread
- PacketScheduler with 512-packet per-process queue limit, 2-second max delay, and automatic drop policy
- Delay/drop/sent counters on PacketScheduler for observability
- Graceful queue flush on shutdown (all queued packets reinjected)
- WPF UI connects to service via named pipe with auto-reconnect
- Live process list with per-second bandwidth and total byte display
- Honest connection states: Disconnected (gray), Connecting (orange), Connected (green), Error (red)
- Auto-reconnect every 3 seconds when service is not running
- 1-second polling for traffic snapshots and rule counts
- Proper ViewModel disposal on window close
- Per-connection byte tracking with thread-safe AddBytesSent/AddBytesReceived on ConnectionInfo
- IPv4/IPv6 detection on connections (IsIPv6 property)
- Network loop records per-connection bytes alongside per-process totals
- Diagnostic STATUS command returns service uptime, active flows/rules, packet delay/drop/sent counters
- DiagnosticInfo model in Core for structured status reporting
- Structured console logging with timestamps in service
- Log directory created at %ProgramData%\OpenNetLimit\logs
- RuleReconciler: rule changes atomically update live rate limiter state via OnRulesChanged event
- Rule file versioning: schema envelope with version field, backward-compatible with legacy array format
- Atomic rule file save (write to temp, rename) prevents corruption on crash
- WinDivert native binaries (WinDivert.dll, WinDivert64.sys) automatically included via Native.WinDivert NuGet package
- Third-party license notices (THIRD-PARTY-NOTICES.txt) documenting WinDivert LGPL/GPL and SharpDivert MIT
- README updated with current project status, WinDivert trust/HVCI/EDR guidance, and troubleshooting
- Fail-safe service lifecycle: validates admin privileges before starting interceptor
- Graceful shutdown: interceptor stop and rule save wrapped in try/catch to prevent data loss
- Last-error recording: writes startup/crash errors to %ProgramData%\OpenNetLimit\last-error.txt
- Rule load failure recovery: starts with empty rule set instead of crashing
- Pipe server crash isolation: logged and recorded without taking down the engine
- Data directory auto-creation on startup

### Security
- Secured named-pipe IPC with explicit ACL (Administrators: FullControl, Users: ReadWrite)
- Added client identity checking via impersonation for mutation commands
- Mutation commands (ADD_RULE, REMOVE_RULE, UPDATE_RULE) require administrator privileges
- Read-only commands (SNAPSHOT, RULES, PROCESSES, STATUS) available to all authenticated local users
- Added IPC protocol definitions with versioning and command validation
- Input length limits prevent oversized command injection
- Fixed REMOVE_RULE bug that was calling RemoveAll() instead of removing only the specified rule

### Fixed
- Restored solution build: all 5 projects (Core, Engine, Service, UI, Tests) compile successfully
- Fixed WinDivertInterceptor to use correct SharpDivert 1.1.0 API (enums, RecvEx tuple return, address access, packet parsing)
- Fixed TrafficMonitor thread-safe byte counters (Interlocked.Add on ProcessTrafficInfo backing fields)
- Fixed test project TFM mismatch (net8.0 → net8.0-windows to match Engine dependency)
- Fixed PipeServer using-statement nesting syntax error
- Added missing `using Xunit;` directives in all test files
- Pinned .NET SDK to 8.0.x via global.json to avoid broken .NET 9 SDK test runner
