# Changelog

## Unreleased

### Fixed
- Restored solution build: all 5 projects (Core, Engine, Service, UI, Tests) compile successfully
- Fixed WinDivertInterceptor to use correct SharpDivert 1.1.0 API (enums, RecvEx tuple return, address access, packet parsing)
- Fixed TrafficMonitor thread-safe byte counters (Interlocked.Add on ProcessTrafficInfo backing fields)
- Fixed test project TFM mismatch (net8.0 → net8.0-windows to match Engine dependency)
- Fixed PipeServer using-statement nesting syntax error
- Added missing `using Xunit;` directives in all test files
- Pinned .NET SDK to 8.0.x via global.json to avoid broken .NET 9 SDK test runner
