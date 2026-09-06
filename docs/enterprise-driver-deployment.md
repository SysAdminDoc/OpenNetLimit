# Driver trust and enterprise deployment

OpenNetLimit uses WinDivert to inspect and control Windows network traffic. WinDivert includes a signed kernel driver named `WinDivert64.sys`. Loading that driver requires administrator rights.

## Why security software may alert

WinDivert is a legitimate open-source networking component, but malicious tools have also used it. Some EDR products therefore alert whenever the driver loads. An alert does not prove that OpenNetLimit is malicious, and it should not be ignored without validation either.

Before allowing the driver on a managed PC:

1. Download OpenNetLimit from the repository's GitHub release page.
2. Verify the release ZIP against its published SHA-256 checksum.
3. Confirm the bundled driver hashes below.
4. Test the release in the organization's normal staging environment.

## Bundled WinDivert 2.2.2 hashes

The files come from the `Native.WinDivert` NuGet package.

| File | SHA-256 |
|---|---|
| `WinDivert.dll` | `C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2` |
| `WinDivert64.sys` | `8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2` |

Verify a local copy with Windows PowerShell:

```powershell
Get-FileHash ./service/runtimes/win-x64/native/WinDivert.dll -Algorithm SHA256
Get-FileHash ./service/runtimes/win-x64/native/WinDivert64.sys -Algorithm SHA256
```

If the release contains different hashes, stop and compare the package version and release notes before creating an allow rule.

## WDAC supplemental rule example

Use the organization's normal policy tooling to merge a supplemental rule. Do not replace an existing WDAC policy with this fragment.

```xml
<FileRules>
  <Allow ID="ID_ALLOW_WINDIVERT_DLL" FriendlyName="OpenNetLimit WinDivert.dll"
         Hash="C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2" />
  <Allow ID="ID_ALLOW_WINDIVERT_SYS" FriendlyName="OpenNetLimit WinDivert64.sys"
         Hash="8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2" />
</FileRules>
```

Hash rules are intentionally narrow. Recheck them whenever OpenNetLimit updates its WinDivert dependency.

## EDR deployment guidance

Create an allow rule for the verified file hash or the fixed installation path. Keep the exception limited to OpenNetLimit's service directory. Broad process or folder exclusions weaken the host and are not needed.

Useful external references:

- [WinDivert project documentation](https://reqrypt.org/windivert.html)
- [WinDivert source repository](https://github.com/basil00/WinDivert)
- [Sigma rule for WinDivert driver loads](https://detection.fyi/sigmahq/sigma/windows/driver_load/driver_load_win_windivert/)
- [LOLDrivers WinDivert entry](https://www.loldrivers.io/drivers/45a31a17-f78d-48ec-beba-74f6bfc5f96e/)

## Troubleshooting driver startup

Check `%ProgramData%\OpenNetLimit\last-error.txt` first. Common causes include a non-elevated service process, an EDR block, or a Windows code-integrity policy that rejects the driver.

OpenNetLimit records service events in the Windows Application event log under the `OpenNetLimit` source. The log includes driver load failures and the path that was checked.
