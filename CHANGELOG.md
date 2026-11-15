# Changelog

## Unreleased

### Added
- Limits for Windows services. Windows Update, Delivery Optimization, BITS and the rest run inside svchost, which no rule could touch. Now the app list shows each svchost with the services in it, and Set limit on one puts the limit on a service, picked from a list when several share the process (the dialog names the ones the limit slows too). The rule follows the service when Windows restarts it in another svchost, including one that starts after the rule is added, and when the PC's clock is set back. When a svchost stops, its limit comes off before Windows can hand its process ID to another program, and what it downloaded still counts toward the service's data cap. The rule editor has a Service field, and the command line takes `onl rules add --service DoSvc`. A svchost running DNS, DHCP, RPC, the firewall, the connection sharing behind Mobile Hotspot and WSL, or another service Windows needs to stay online is never limited or blocked, and its row says "Windows (never limited)" with the reason in the menu. A rule on svchost, by name or by its path, or on one of those services is turned away with the reason, whether it's added, edited or imported. Older rules on svchost by name can still be edited and turned off
- `onl selftest` checks that limits work on your PC. It limits its own upload for a few seconds, measures loopback traffic against the limit, then removes the rule and checks the speed comes back
- A Windows installer. It sets up the service to start with Windows, adds OpenNetLimit to the Start menu and Apps & Features, and takes over a service you installed from the ZIP. Uninstalling removes the service and keeps your rules and history
- The app checks GitHub once a day for a newer release and shows it as a button in the status bar, which opens the release page. Nothing is downloaded or installed. Turn it off with the Updates button, or set `OPENNETLIMIT_NO_UPDATE_CHECK=1` to turn it off for everyone on a managed PC
- A loopback benchmark in `tools/OpenNetLimit.Bench` measures throughput, round trips and UDP loss. Run it with the service stopped and again with it running to see what the engine costs on your PC
- New issues on GitHub open a bug report form that asks for the version, how you installed it, `last-error.txt` and the self test output. Feature requests have their own form
- Pause for games. The tray menu, `onl pause` and `POST /api/v1/engine/pause` stop filtering and unload the WinDivert driver, for games like Valorant and Call of Duty whose anti-cheat won't run next to a packet driver. If another program still has the driver open, the tray and `onl pause` (exit code 2) name it, and the app tells you once the driver finally unloads. Resume brings everything back. If it can't start, for example because another program is still letting go of the driver, it stays paused, says why in the window (along with whatever still has the driver open) and tells you when you can try again. A failed resume unloads the driver again only if it loaded it itself, so it won't stop a driver that WireGuard or another program loaded meanwhile. The status also notices when another program loads the driver while you're paused. The app shows Paused instead of an engine error while it's off. An engine that stopped after errors keeps saying so while Resume tries to start it again, and shows as running as soon as it does
- Stay paused after restart, in the tray menu or as `onl pause --stay-paused`, keeps the engine off through reboots until you resume. Without it, a pause ends when the service restarts. If that choice can't be saved or cleared, the tray, `onl pause` and `onl resume` warn you
- A Rules tab in the app. It lists every rule with its limit, schedule, group and how much of its data cap is used, and you can add, edit, turn off or delete rules from it. Undo brings back a rule you deleted, in the same place in the list and with the data it had already used. One that comes back turned off starts its cap over once you turn it on, like any other rule turned off and on. One form covers hours and days, and a daily, weekly or monthly data cap that blocks or slows the app once it's used up. A rule can have just a cap with no speed limit. When something in the form is wrong, the box that needs fixing gets the focus. Speed alerts get their own list and form on the same tab. An error from the service that isn't plain text is shown as it came instead of closing the app
- Export and Import on the Rules tab save your rules to a file and load them back, on the same PC or another one. Import also reads the service's own `rules.json`, and a rule with the same ID replaces the one you have
- A whole-PC limit puts one cap on every app's traffic added together, and Windows Update and other Windows services count toward it too. Their DNS lookups don't, even when a big answer falls back to TCP, and a svchost that runs only Windows networking (DNS, DHCP and the like) doesn't count at all, so DNS over HTTPS stays out of it too. The Windows DNS Server role doesn't count either, answers it sends over TCP included. With a VPN like WireGuard, OpenVPN or Tailscale connected, what goes through it counts once, as the VPN's encrypted traffic, rather than a second time inside the tunnel. That covers VPNs whose encrypted side Windows itself sends, like WireGuard's and OpenVPN's kernel drivers. An IPsec or GRE VPN, whose encrypted side isn't something the limit can count, counts inside its tunnel instead, and so do Windows' own VPNs (SSTP, L2TP, PPTP and IKEv2). Inside Windows' own VPNs, and past a PPPoE or dial-up connection, traffic to a private address counts too, since that's a network at the other end of the link and not your own. Windows' own Teredo and IP-HTTPS adapters aren't taken for VPNs. Set it from the new card on the Real-time tab, with `onl pc-limit set --download 1024`, or with `PUT /api/v1/pc-limit`. It counts internet traffic only, so copies to a NAS or another PC on your network stay at full speed, unless you check **Count local network traffic too**. Traffic that never leaves the PC isn't counted either way, even when it's sent to the PC's own network address. A download cap leaves uploads at full speed and an upload cap leaves downloads alone. An app with its own limit stays under both, and the limit comes back after the service restarts. A limit with a misspelled field, a field given twice or no speed in it gets turned away with the reason, where it used to switch the limit off. In the card's dialog, OK only changes what you typed in or checked, so a limit changed from the command line while it was open isn't undone (even one the app hadn't shown yet), and a speed you didn't touch keeps its exact rate. Scripts get the same with `PATCH /api/v1/pc-limit`, which changes only the fields you send, and `DELETE` turns the limit off without touching the local network choice

### Fixed
- A program copied to a Windows name, like `lsass.exe` or `svchost.exe` in a downloads folder, no longer gets past every rule and the whole-PC limit. Only the real ones in System32 are left alone. The service also reads the path of protected Windows processes now, which it couldn't before
- Apps that exit leave the app list and the API a few seconds later. Before, every program that ever ran stayed listed, and a new program that got an old one's process ID was shown under the old name with both traffic totals added together, so a service's data cap could fill up with another app's downloads. Now each one gets its own row, and a connection an exited app left open can't bring its name back onto the new one, even when Windows hands the ID on before the exit is noticed. An app that starts just as its connections are being looked up isn't mistaken for the one that had its ID before. What an exited app downloaded still counts toward its data cap
- Remove limit leaves an app's Allow rule alone. An Allow rule keeps a wider rule, like one on the app's folder, off that app, so deleting it could put a limit on the app you meant to free
- Rules and speed alerts are saved the moment they change. They used to be written only when the service stopped, so a crash or power cut lost every change since it started. A `rules.json` or `alerts.json` the service can't read is copied to `.unreadable` next to it (`.unreadable-2` and up if an older copy is there, which is never written over). Another file that only starts with the same name, like notes saved next to it or a copy renamed to `.unreadable-02`, isn't taken for one. The file itself stays as it is until you change a rule. If no copy can be made, the service turns rule, alert and whole-PC limit changes away until it's restarted, and the Rules tab says why. It says so even when the app isn't running as administrator, and edits stay off while the app reconnects to the service
- Rules with the same priority keep their order when you add, edit or remove another rule. With more than 16 rules, any change could shuffle them and switch which one applies to an app. A rule you delete and add back, by Undo or by importing it, goes back to its old place among them
- A rule list with an empty entry in it is refused with a reason instead of dropping the app's or CLI's connection, and a `rules.json` with one loads the rest of its rules
- A quota rule you delete and import again in the same period carries on with what it had used. It started over from zero, so a used-up app got its whole allowance back. Turning the rule or its cap off and on again starts the cap over, so that's how to reset a used-up cap by hand. That works even when two windows or scripts turn it off and on at nearly the same moment, which could leave the app blocked before
- A quota that blocks or slows an app now kicks in within about a second of running out. Usage was only checked every 30 seconds, so a fast download could run well past its cap (a 1.5 MB quota let 7.7 MB through on a 300 KB/s download). Quotas that only warn are still checked every 30 seconds.
- A quota that runs out between two checks, like during a fast download, no longer sends a "nearly used up" warning after its "used up" alert
- Quotas keep what they've used when the service restarts, so a reboot or an update no longer hands a used-up app its whole allowance back. The service saves quota use to `quotas.json` next to the rules. A quota whose day, week or month ended while the service was off starts over, and so does one whose rule now points at another app or another period. When the rules file won't load, the saved use waits for it to be fixed instead of being thrown away
- A service that isn't running as administrator refuses rule and alert changes and says why. It used to take them and lose them, since it never loaded the saved rules and doesn't save over them. While it refuses, the app greys out every button that makes a change, and its tooltip and the empty Rules tab give the reason
- When the app can't read the rules from the service, the Rules tab no longer says you have no rules or no alerts
- Long tray balloons, like a bandwidth alert for a program with a long name, end in "…" after the last whole word instead of Windows cutting them off mid-word at 255 characters. When a warning adds a sentence of its own, that sentence stays whole and the rest gets shortened. The "…" never joins onto a sign left before a line break
- Rules and alerts typed with `.exe` (like `steam.exe`, the README's own example) match the app. Windows names processes without the extension, so those rules never applied. History lookups by name accept `.exe` too
- The packet engine starts again. The WinDivert flow layer was opened with the wrong flags and Windows refused it with error 87, so the service ran but never filtered anything (#3)
- Apps show up in the list again. Flow addresses were read in the wrong byte order, so no packet was ever matched to the app that sent it (#4)
- Downloads and connections that were already open when the engine started are now credited to their app, and so are the many new flows Windows reports as belonging to System (#4)
- A limit now holds an app to the rate you set. Packets that had to wait were never charged against the limit, so a 256 KB/s rule let curl run at almost 5 MB/s
- Limits reach apps that start or reconnect after the rule was added
- Removing or changing a rule also reaches apps that are idle or have exited, and a new app that gets an old app's process ID no longer inherits its limit
- The Limit column shows the rate each app is held to
- Speeds no longer drop to 0 B/s when the app, the CLI and the history writer read the engine at the same time, and usage history counts every byte once
- Finishing or skipping the first run wizard no longer closes the whole app (#1)
- When the engine can't start, the app says why and stays on that message instead of flipping between Connecting and Service not running (#1, #3)
- Right-clicking an app selects it, so Set bandwidth limit works from the menu. When the menu is disabled it tells you why (#1)
- Changes made from the app were always rejected as unauthorized, even when it ran as administrator. The service checked who was connected before the client had sent anything, which Windows doesn't allow (#1)
- `onl rules add` works. The service only took rule actions as numbers while the CLI sends names (#1)
- The control pipe stays up when the service isn't elevated or the engine fails to start, so the app and CLI can still report the problem
- DNS answers are cached from responses again, which gives domain rules something to match
- Fast downloads no longer stall, and short bursts of traffic no longer lose packets while the engine catches up. The driver's packet queue was left at its small default, and the driver dropped whatever didn't fit without reporting it
- Packet engine errors show up in the service log and the Windows event log. They used to go where only a debugger could see them
- Stopping the service takes a fraction of a second instead of up to 10 seconds, so uninstalling, updating and shutting down Windows don't wait on it
- When the service isn't installed, the app points you to the setup first and to `Install-Service.ps1` only for the ZIP
- A quota set to block now blocks. It used to set a limit of zero, which the engine reads as no limit at all, so the app kept going at full speed. The block covers every app the rule matches, rules by path included, and lifts when the quota period resets
- Saving a rule no longer lifts a quota's throttle. It used to put the throttled app back on its normal limit. The throttle still ends when the quota period resets, or when you raise the quota's limit far enough, remove the rule or point it at another app
- Scheduled rules switch on and off within a minute of their start and end times. Before, a rule only followed its schedule after some rule was edited
- Raising a used-up quota's limit above the one it ran out at, and above what the app has used, gives the app its traffic back right away. Lowering the limit keeps the quota used up, and so does raising it again to anything up to the old limit. For a quota that isn't used up, a new limit or warning level that leaves the app under the warning level means it's warned again once it gets there
- Pointing a quota rule at a different app starts that app's quota from zero. It used to inherit the old app's usage, so the new app could be blocked at once
- A quota you add while the service is running counts from the moment you save it. An app that had already downloaded a lot was blocked as soon as the rule went in
- Two quota rules on the same app name (say, Chrome in two folders) each keep their own limit. Quotas on rules that only name a folder are counted too
- Traffic that a Block rule or a used-up quota drops no longer counts as the app's usage, so a blocked app shows 0 B/s and its quota stops filling
- Importing an exported rules file a second time updates its rules instead of adding a second copy of each. Rules that share an id each get their own, so each one can be edited or deleted separately
- Quota alerts sent to plugins report the usage they fired at. A quota period reset right after could make a plugin see 0 bytes used
- The engine starts when another WinDivert program has left its driver registered to a folder it since deleted. OpenNetLimit removes that leftover registration and loads its own driver. Before, it refused to start with an error that didn't name the cause
- When antivirus quarantines `WinDivert64.sys`, the startup error says so and names the folder the file belongs in
- If another program is still loading the WinDivert driver when the service starts, the service waits for it to finish (up to 10 seconds) before opening the driver. Opening it partway through its load can crash Windows
- Adding, changing or importing a rule with no program name or path closed the client's pipe connection, and the REST API answered 500. Both now answer with the reason, and REST returns 400
- Imported rules can spell out their action and direction as names (`"action": "Block"`). Import used to accept only numbers
- The control pipe's `EXPORT_RULES` answers on one line like every other command. It sent indented JSON, so a client reading line by line got only `[`
- Table cells in the app have padding and sit centered in their rows. The text used to hug the top left corner
- A speed alert set on a program's full path no longer goes off for apps whose path Windows won't share, like protected system processes. It used to fire for every one of them. An alert sent through the API or a file with a blank path goes by its name alone, where it used to never fire for apps with a known path
- Usage history finds an app however its name is typed. `onl stats hourly --process Steam` used to come back empty for `steam`, and a name with a space or `&` in it got cut short. Days where Windows reported the same app under two spellings now add up into one row

### Changed
- OpenNetLimit runs on .NET 10, the current long-term support release. Microsoft stopped patching .NET 8 on November 10, 2026. The release builds bundle the runtime, so there's still nothing extra to install. Building from source needs the .NET 10 SDK
- The service loads your saved rules before the REST API starts, so a script that calls the API right after a restart never sees an empty rule list

## 1.0.2 (2026-09-12)

### Changed
- Added a version-free README hero built from the approved crossing-lanes mark and a current product capture
- Put the hero at the top of the README and removed the repeated live-dashboard view from the body
- Replaced the versioned social artwork with the same evergreen composition
- Archived the earlier card, both reviewed layouts, reduced-width proofs, and current private-desktop captures
- Added a repeatable hero renderer and regression checks for placement, duplication, dimensions, copy, and selected-art parity

## 1.0.1 (2026-09-07)

### Changed
- Archived the original crossing-lanes logo directions and prior social artwork with machine-readable selection records
- Preserved the approved app-icon concept as an untouched brand master
- Refreshed all five product screenshots from the versioned desktop build
- Updated the GitHub social preview footer for v1.0.1 without changing its approved composition
- Rebuilt the self-contained Windows x64 release package and checksum

## 1.0.0 (2026-09-06)

### Added
- New OpenNetLimit logo, Windows icon family, product screenshots, and social preview artwork
- Redesigned WPF dashboard with clearer status, traffic summaries, history charts, and limit editing
- Cohesive dark and light themes with accessible chart labels and higher-contrast controls
- Isolated marketing capture tool with realistic fixture data and blank-render detection
- Reproducible Windows x64 release packaging with service install and removal scripts
- First-run setup wizard: 3-page guided walkthrough for new users (requirements, tips, finish)
- Wizard shows on first launch, stores completion marker in %LocalAppData%\OpenNetLimit
- Skip button available on all wizard pages
- Comprehensive README with feature list, setup guide, IPC protocol reference, troubleshooting
- Accessibility: AutomationProperties on limit dialog text boxes
- Quota management: daily/weekly/monthly data caps per app with QuotaConfig on rules
- QuotaTracker monitors usage vs limits, fires warning and exceeded events
- Auto-throttle or auto-block when quota exceeded (configurable per rule)
- Warning threshold (default 80%) triggers notification event before quota hit
- Bandwidth priority system: High/Normal/Low priority on rules
- Profile system: ProfileName field on BandwidthRule for grouping rules into profiles
- QUOTAS IPC command returns all quota states with usage percentages
- Windows service detection: identifies svchost service names via SCM API
- UWP/Store app detection: identifies AppX package names via GetPackageFullName
- ServiceName and AppxPackage fields on ProcessTrafficInfo for display
- SQLite-backed traffic statistics: per-process hourly and daily byte counters
- TrafficStatsDb with upsert-on-conflict aggregation, WAL mode, 90-day auto-purge
- STATS_HOURLY, STATS_DAILY, STATS_TOP IPC commands for querying historical data
- Real-time traffic chart (LiveCharts2): 60-second scrolling download/upload graph
- Chart shows download (blue) and upload (green) with smooth line rendering
- Historical data query: top processes by total bandwidth, per-process time series
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
- Rule scheduling: time-of-day and day-of-week recurring schedules on BandwidthRule
- Import/export rule sets via IPC (EXPORT_RULES / IMPORT_RULES commands)
- Import supports merge (append) and replace modes
- DNS resolver with 10-minute cache for reverse-resolving IP addresses to hostnames
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
- REST API for local automation and third-party integration (`/api/v1/status`, snapshots, processes, rules, stats, quotas, connection log)
- Optional remote administration via explicit non-loopback REST bind and API key
- Keyed REST rule mutation endpoints for add/update/delete/import
- Optional VirusTotal process verification using local SHA-256 hashes and cached file reports
- `VERIFY_PROCESS` IPC command and REST verification endpoints for executable reputation checks
- Optional cached GeoIP lookup for public remote IP country/city display
- `GEOIP` IPC command and REST GeoIP endpoints with private-address suppression
- Bandwidth alert rules with per-process/path thresholds, cooldowns, persistence, and recent event history
- Tray balloon notifications for newly triggered bandwidth alerts in the WPF UI
- Alert rule/event IPC commands and REST endpoints
- Manifest-based webhook plugin system for alert and quota events
- Plugin list/reload IPC commands and REST endpoints
- Persisted dark/light WPF theme support with a status-bar toggle
- English/Spanish WPF localization with a persisted status-bar language toggle

### Security
- Updated Microsoft.Data.Sqlite to 8.0.30 and SourceGear.sqlite3 to 3.53.4, removing the vulnerable SQLitePCLRaw 2.1.6 dependency
- Secured named-pipe IPC with explicit ACL (Administrators: FullControl, Users: ReadWrite)
- Added client identity checking via impersonation for mutation commands
- Mutation commands (ADD_RULE, REMOVE_RULE, UPDATE_RULE) require administrator privileges
- Read-only commands (SNAPSHOT, RULES, PROCESSES, STATUS) available to all authenticated local users
- Added IPC protocol definitions with versioning and command validation
- Input length limits prevent oversized command injection
- Fixed REMOVE_RULE bug that was calling RemoveAll() instead of removing only the specified rule
- REST API is loopback-only by default; non-loopback listener prefixes require `OPENNETLIMIT_ENABLE_REMOTE_API=1` and `OPENNETLIMIT_API_KEY`
- REST mutations require `X-OpenNetLimit-Key` or bearer token authentication
- VirusTotal verification is opt-in via `OPENNETLIMIT_VIRUSTOTAL_API_KEY`, sends hashes only, and requires admin/keyed access
- GeoIP lookups are opt-in via `OPENNETLIMIT_GEOIP_ENABLED=1`, require admin/keyed access, and never query private, loopback, link-local, or multicast addresses
- Alert rule mutations require existing admin/keyed control-plane access
- Plugins are opt-in via `OPENNETLIMIT_PLUGINS_ENABLED=1` and dispatch webhook events only; no in-process plugin DLL/script loading

### Fixed
- Fixed QuotaTracker period reset: quotas now track a baseline so `ResetPeriod()` correctly counts only bytes since the reset, not cumulative totals
- Fixed PipeServer admin impersonation: `IsClientAdmin` now captures client identity inside the `RunAsClient` callback instead of reading the service's own token afterward
- Fixed TrafficStatsDb thread safety: all SQLite operations now serialized via lock to prevent corruption from concurrent stats/purge timer threads
- Fixed payloadLength calculation: uses WinDivert `Data.Length` (actual payload) instead of `packet.Length` (full packet including IP/TCP/UDP headers)
- Fixed SetLimitAsync: updates existing rule for a process instead of creating duplicates on repeated limit changes
- Fixed PipeClient thread safety: SemaphoreSlim serializes concurrent send/receive pairs to prevent interleaved pipe I/O
- Fixed DnsResolver TOCTOU: merged separate cache and expiry dictionaries into a single ConcurrentDictionary with a record struct entry
- Bounded MainViewModel._seenAlertIds to prevent unbounded memory growth in long-running UI sessions
- Extracted shared WildcardMatcher utility in Core, replacing three duplicate implementations in BandwidthRule, BandwidthAlertRule, and BandwidthAlertTracker
- Cached ProcessIdentifier service name lookups with 60-second TTL to avoid enumerating all Windows services on every svchost flow
- Upgraded LiveCharts2 from pre-release 2.0.0-rc4.5 to stable 2.0.5
- Protected system processes (svchost, lsass, csrss, dns, etc.) are now exempt from rate limiting and blocking to prevent network starvation from overly broad wildcard rules
- Added WDAC/EDR enterprise deployment documentation to README with WinDivert SHA-256 hashes, WDAC allowlist XML, and EDR allowlist steps for common products
- Rule conditions: rules can now filter by remote IP address (exact or CIDR subnet), remote port, and protocol (TCP/UDP). Interceptor evaluates connection-level conditions per packet via `FindMatchingRule` overload
- Added `ProtocolFilter`, `MatchesConnection`, and `HasConnectionFilters` to `BandwidthRule` model; existing `RemoteAddressFilter`/`RemotePortFilter` fields now active in rule evaluation
- App groups: rules can be assigned to named groups via `GroupName` field. `GROUPS` and `GROUP_RULES` IPC commands, `GET /api/v1/groups` and `GET /api/v1/groups/{name}` REST endpoints for querying groups and their rules
- CLI tool (`onl`): scriptable rule management, status queries, stats, group queries, and import/export via the REST API. Supports `OPENNETLIMIT_API_URL` and `OPENNETLIMIT_API_KEY` environment variables

### Security
- Added SourceGear.sqlite3 as the native SQLite provider and updated it to 3.53.4
- Added PluginManager SSRF protection: webhook URLs targeting loopback, private RFC1918, or link-local addresses are rejected at manifest validation
- Added 10-second HTTP timeout on plugin webhook dispatch to prevent indefinite hangs
- DPAPI-protected API key storage: service checks `%ProgramData%\OpenNetLimit\apikey.protected` (encrypted with LocalMachine scope) when `OPENNETLIMIT_API_KEY` env var is not set
- Rules support `CountryFilter` array for country-code-based connection filtering using the existing GeoIP infrastructure
- RESX-based localization: replaced hardcoded string dictionaries with `Strings.resx` (English) and `Strings.es.resx` (Spanish) resource files via `ResourceManager`. New languages can be added by dropping satellite assemblies without code changes
- RTL language support: main window applies `FlowDirection.RightToLeft` when the current UI culture uses a right-to-left script

- DNS-based rule matching: rules can specify `DnsDomainFilter` to match traffic by resolved domain name. Supports exact domains (`cdn.example.com`) and wildcard subdomains (`*.example.com`). DNS responses (UDP port 53) are parsed in real-time to build an IP→domain correlation cache used during rule evaluation. DnsDomainCache holds up to 50K entries with TTL-based expiry.
- Historical bandwidth timeline: new History tab with scrollable column chart showing per-process hourly/daily bandwidth from SQLite stats database; process filter dropdown and hourly/daily granularity toggle; localized in English and Spanish
- Fixed History tab Daily radio button: clicking "Daily" now correctly switches chart to daily granularity (was unbound, chart always showed hourly data regardless of selection)
- Quota enforcement: quotas with OnExceeded=Throttle now reduce process rate to ThrottleBytesPerSecond; OnExceeded=Block drops traffic to zero. Limits revert on quota period reset via rule reconciliation.
- Fixed FlowTracker data race: RegisterFlow creates new ConnectionInfo instead of mutating shared object; UnregisterFlow replaces entry to avoid torn DateTime? reads by PurgeStale
- Fixed DnsResponseParser out-of-bounds: compressed DNS pointer targets are now bounds-checked
- Fixed WinDivertInterceptor StartAsync race: `_isRunning` set before task launch to prevent concurrent StopAsync from missing running tasks
- Fixed WinDivertInterceptor Dispose deadlock: uses ConfigureAwait(false) to avoid synchronization context deadlock
- Fixed PipeClient Disconnect/SendCommandAsync race: stream references captured under lock before I/O; Disconnect acquires same lock
- Fixed ConnectionLogger/BandwidthAlertTracker trim TOCTOU: Count check moved inside lock to prevent over-trimming under contention
- Fixed BandwidthAlertTracker stale-entry prune: uses maximum rule CooldownSeconds instead of hardcoded 300s, so long-cooldown alerts are not re-fired early
- Fixed RestApiServer body-read DoS: reads with hard byte cap on the stream instead of reading entire body before checking size, preventing chunked-encoding OOM attacks
- Fixed DnsResolver cache eviction: when all entries are fresh and cache exceeds capacity, evicts oldest entries instead of silently growing past limit
- Fixed HistoryViewModel CancellationTokenSource leak: old CTS disposed before replacement
- Fixed RuleEngine.ImportRules: imported rules now validated (must have ProcessName or ProcessPath), matching AddRule/UpdateRule behavior
- Fixed QuotaTracker.Update: uses QuotaState.PercentUsed property instead of duplicating the percentage formula
- Removed dead pass-through properties from PipeServer (DiagnosticProvider, ConnectionLogProvider, StatsProvider, QuotaTracker). EngineWorker sets these on ControlPlaneState directly
- Fixed CLI import stdin DoS: reads with 1MB hard cap matching REST API body limit
- Extracted shared EnvHelper.IsEnabled utility, removing four copy-pasted implementations across RestApiOptions, GeoIpOptions, PluginOptions, VirusTotalOptions
- Fixed TrafficMonitor first-packet race: replaced AddOrUpdate with GetOrAdd + atomic AddBytes, preventing silent byte loss when two threads race on a new process
- Removed unused BandwidthPriority enum and field from BandwidthRule (dead data never read by any runtime code)
- Download/upload speed colors now use theme-aware DynamicResource brushes (DownloadBrush/UploadBrush) that adapt to light/dark themes
- SetLimitDialog: inline validation with red border and error text replaces blocking MessageBox; screen-reader-friendly with LiveSetting=Assertive
- Disabled context menu items now show tooltip explaining "Administrator privileges required"
- DataGrid columns use star-sizing instead of hardcoded pixel widths for DPI/font-size resilience
- Fixed time-dependent RuleSchedule tests: use fixed local-time DateTime values instead of DateTime.Today to avoid midnight/timezone fragility
- DnsResolverTests: failure test is now resilient to captive-portal DNS that resolves all addresses
- Added PacketScheduler tests: MaxDelay drop, queue capacity enforcement, multi-process isolation, RemoveProcess behavior (5 new tests)
- Fixed BandwidthAlertTracker event trimming race: uses lock-based approach matching ConnectionLogger
- Fixed BandwidthAlertTracker.GetRecentEvents: array snapshot + slice from end instead of O(n) Reverse()
- Fixed PluginManager SSRF: webhook URLs with hostnames (non-IP) are now rejected at manifest validation
- Fixed QuotaTracker mutable state leak: GetAllQuotaStates/GetQuotaState return defensive copies
- Fixed EngineWorker quota reset timer race: _lastQuotaResetCheck uses Interlocked long ticks
- Fixed ParseIPv6Addr hot-path allocation: constructs IPAddress from 16-byte stackalloc span
- Fixed TrafficMonitor snapshot TOCTOU: exchanged values captured in locals, totals computed from locals
- Fixed TrafficMonitor TakeSnapshot returning live mutable references: creates shallow copy per ProcessTrafficInfo
- Fixed PacketScheduler stale queue accumulation: empty queues idle >5 minutes pruned in DrainReady
- Fixed WinDivert handle leak: StartAsync disposes partially-opened handles on failure
- Fixed HistoryViewModel concurrent LoadDataAsync: cancels previous load on new request
- Prune BandwidthAlertTracker _lastTriggered: entries older than 300s removed at start of each Update sweep
- Added RuleSchedule tests: daytime/overnight wrapping, day-of-week, boundary conditions
- Added DnsResponseParser tests: AAAA records, truncated payloads, invalid pointers, oversized responses
- Added IPv6 CIDR matching tests: /64 and /128 prefixes

### Fixed
- Enabled TreatWarningsAsErrors in Directory.Build.props. Nullable analysis and CS-series warnings now break the build
- Added WinDivert driver signature validation at startup: logs warning if binary certificate is expired, with link to WinDivert#397 and cross-signing policy context
- Fixed ConnectionLogger counter drift: replaced Interlocked.Increment/_count with lock-based trim using ConcurrentQueue.Count directly
- Eliminated ParsePacket IPAddress string roundtrip. IP addresses now construct from bytes through stackalloc spans with zero string allocation in the packet-processing loop
- Added TrafficStatsDb purge transaction: hourly and daily DELETEs now wrapped in a transaction for atomicity
- Validated BandwidthRule requires ProcessName or ProcessPath: AddRule/UpdateRule throw ArgumentException for rules that cannot match any process
- Added quota period auto-reset: daily quotas reset at midnight, weekly on Monday midnight, monthly on 1st midnight via timer in EngineWorker
- Added DnsResolver cache eviction: max 10,000 entries with periodic pruning of expired entries
- Added DnsResolver and ConnectionLogger unit tests covering cache behavior, entry trimming, and recent retrieval
- Fixed RuleSchedule UTC time comparison: schedule start/end times now evaluated against local time instead of UTC
- Fixed REST API rate-limiter dictionary leak: idle rate limiters (>10 minutes) are now pruned and disposed
- Fixed PluginManager IPv4-mapped IPv6 SSRF bypass: `::ffff:192.168.x.x` addresses now correctly detected as private via MapToIPv4
- Fixed silent crash in WinDivert capture loops: non-cancellation exceptions (driver faults, I/O errors) are now caught, logged, and retried with backoff; persistent failures (10 consecutive) set `_isRunning = false`
- Fixed TrafficMonitor snapshot race: `CurrentDownloadBytesPerSecond`/`CurrentUploadBytesPerSecond` now use volatile-backed fields to prevent torn reads by concurrent pipe/REST/UI callers
- Fixed PacketScheduler dispose race: `_sendLock` serializes DrainReady timer callback and Dispose flush loop to prevent concurrent WinDivert handle access
- Fixed FlowTracker memory leak: closed flows are now purged every 5 minutes (PurgeStale was dead code)
- Fixed named pipe squatting vulnerability: first pipe instance uses FirstPipeInstance flag to prevent attackers from pre-creating the pipe name
- Fixed DnsResolver swallowing OperationCanceledException: shutdown cancellation is now properly propagated instead of being caught and cached as null
- Fixed QuotaTracker unsynchronized state mutation: per-state lock prevents torn reads and double-fire of warning/exceeded events under concurrent timer ticks
- Fixed CLI crash on invalid numeric input: --download, --upload, and --port now validate with TryParse and show user-friendly error messages
- Pinned floating NuGet versions (Microsoft.Data.Sqlite 8.0.11, ProtectedData 8.0.0) for reproducible builds
- Added pipe server auto-restart with exponential backoff (1s to 30s) on crash. IPC recovers without a service restart
- Fixed PluginManager SSRF gap for IPv6 ULA (`fc00::/7`) and link-local (`fe80::/10`) addresses
- Fixed ConnectionLogger.GetRecent O(n) performance by replacing Reverse().Take() with an array slice from the end
- Made WinDivertInterceptor.IsRunning thread-safe via volatile backing field
- Enhanced driver load failure diagnostics: last-error.txt now includes HVCI, EDR, and driver signing troubleshooting steps
- Added Windows Event Log integration: service events written to Application log under "OpenNetLimit" source
- RuleEngine now returns defensive copies from GetRule, GetAllRules, FindMatchingRule, and GetRulesByGroup. Callers can no longer mutate internal state
- Added REST API rate limiting: 10 requests per 10-second window per caller IP; returns 429 when exceeded
- Exposed IPacketInterceptor diagnostics (TotalBlocked, TotalDelayed, TotalDropped, TotalSent, GetRecentConnectionLog) through the interface. EngineWorker no longer downcasts to WinDivertInterceptor
- Replaced DPAPI LocalMachine API key storage with Windows Credential Manager (CredWrite/CredRead) scoped to the service account; legacy DPAPI keys auto-migrated on first load
- Restored solution build: all 5 projects (Core, Engine, Service, UI, Tests) compile successfully
- Fixed WinDivertInterceptor to use correct SharpDivert 1.1.0 API (enums, RecvEx tuple return, address access, packet parsing)
- Fixed TrafficMonitor thread-safe byte counters (Interlocked.Add on ProcessTrafficInfo backing fields)
- Fixed test project TFM mismatch (net8.0 → net8.0-windows to match Engine dependency)
- Fixed PipeServer using-statement nesting syntax error
- Added missing `using Xunit;` directives in all test files
- Pinned .NET SDK to 8.0.x via global.json to avoid broken .NET 9 SDK test runner
- Traffic snapshots now preserve executable paths when available, enabling process verification and path-aware integrations

## Roadmap archive, 2026-08-10: ROADMAP.md

<details>
<summary>Original roadmap snapshot</summary>

````markdown
# OpenNetLimit development roadmap

## Vision

An open-source, per-application bandwidth limiter and network monitor for Windows. It is a free alternative to NetLimiter built on WinDivert.

---

## Future (Post-1.0)


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
````

</details>
