# Changelog

## Unreleased

### Added
- Bounded packet scheduler: rate-limited packets enter per-process queues instead of blocking the capture thread
- PacketScheduler with 512-packet per-process queue limit, 2-second max delay, and automatic drop policy
- Delay/drop/sent counters on PacketScheduler for observability
- Graceful queue flush on shutdown (all queued packets reinjected)
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
