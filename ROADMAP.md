# OpenNetLimit roadmap

Actionable work only. Historical and completed roadmap material is archived in CHANGELOG.md; blocked work is kept in Roadmap_Blocked.md.

The v1.0.2 README hero remediation and release refresh are complete.

## Issue Intake (2026-09-26)

Open GitHub issues checked on 2026-09-26. Both reports describe the same failure: the service installs and runs, but the packet engine never starts, so the UI shows "Engine: Service not running" and no limit can be set. They are the only field reports the project has, and both come from the published zip.

### P1

- [ ] P1: WinDivert Flow layer fails to open with error 87, so the engine never starts (issue #3)
  Reported: faisalasghar, 2026-09-21, v1.0.2 on Windows 11, bug. `Install-Service.ps1` reports "installed and running"; the UI keeps saying Engine: Service not running.
  Why: the reporter's log shows error 87 (The parameter is incorrect) when the Flow layer handle is opened. Their analysis, which matches the WinDivert documentation: `WINDIVERT_LAYER_FLOW` must be opened with `WINDIVERT_FLAG_SNIFF | WINDIVERT_FLAG_RECV_ONLY`, and `WinDivertInterceptor.cs` passes Sniff alone. Driver hashes match the published ones, so it is not a corrupt download or an elevation problem.
  Next: add `RecvOnly` to the Flow-layer open flags in `src/OpenNetLimit.Engine/Interception/WinDivertInterceptor.cs`, log the flags and the Win32 error on every open failure, add a test that pins the flag set, ship v1.0.3, and reply on both issues.
  Acceptance: on a clean Windows 11 VM the service starts, the UI shows the engine as running within 5 seconds, and a limit set on one process measurably throttles it.
  Evidence: https://github.com/SysAdminDoc/OpenNetLimit/issues/3; https://reqrypt.org/windivert-doc.html (Flow layer flags)
  Complexity: S

- [ ] P1: v1.0.0 zip: no guided first run, UI loops between Connecting and Service not running, Set Bandwidth Limit does nothing (issue #1)
  Reported: theArnoll, 2026-09-06, v1.0.0 on Windows 11 25H2, two screenshots, `last-error.txt` says "Failed to start packet interceptor."
  Why: the same interceptor start failure as #3, seen through the UI: the first-run guide never appears, the status pill alternates between the two states, and the context-menu action on Applications is a no-op while the engine is down.
  Next: fixed by the #3 flag change; separately, the UI should say plainly why the engine is down (surface `last-error.txt`'s first line in the status area) instead of looping, and the first-run guide should still open when the engine is unavailable.
  Evidence: https://github.com/SysAdminDoc/OpenNetLimit/issues/1
  Complexity: S
