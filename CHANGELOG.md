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
- Connection logger: rolling log of established, deleted, and blocked connections (max 10K entries)
- CONNECTION_LOG IPC command returns last 100 log entries
- Blocked events include matching rule name for debugging
- Connection blocking: rules with Action=Block drop matching packets silently
- TotalBlocked counter on interceptor for blocked packet tracking
- PacketsBlocked exposed via STATUS diagnostic command
- System tray icon with Show/Exit context menu
- Minimize-to-tray: window hides when minimized, double-click tray icon to restore
- Right-click context menu on process list: Set Bandwidth Limit / Remove Limit
- SetLimitDialog for entering download/upload limits in KB/s
- Wildcard pattern matching for rule paths (`*\chrome.exe`, `C:\app?.exe`)
- Accessibility: AutomationProperties on all key UI elements (status, summary, process list, status bar)
- LiveSetting=Polite on connection status for screen reader announcements
- Keyboard tab navigation cycle on main window
- Directory.Build.props with NuGet audit (moderate level, all mode)
- UI displays permission mode (Administrator / Read-only) in status bar
- Admin detection on UI startup determines available actions
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
