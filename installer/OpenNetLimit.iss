; Built by scripts\build-release.ps1, which passes the version from Directory.Build.props and the
; release package folder it assembles. Installs per machine because the service needs
; administrator rights to load the WinDivert driver.
#ifndef AppVersion
#error AppVersion is required. Build with scripts\build-release.ps1, or pass /DAppVersion=X.Y.Z
#endif

#ifndef SourceDir
#error SourceDir is required: the release package folder scripts\build-release.ps1 assembles.
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts"
#endif

#ifndef Runtime
#define Runtime "win-x64"
#endif

#if Runtime == "win-arm64"
#define Arch "arm64"
#else
#define Arch "x64compatible"
#endif

[Setup]
AppId={{235DC51D-963E-435F-87F1-CAC0D50B3534}
AppName=OpenNetLimit
AppVersion={#AppVersion}
AppVerName=OpenNetLimit v{#AppVersion}
AppPublisher=SysAdminDoc
AppPublisherURL=https://github.com/SysAdminDoc/OpenNetLimit
AppSupportURL=https://github.com/SysAdminDoc/OpenNetLimit/issues
AppUpdatesURL=https://github.com/SysAdminDoc/OpenNetLimit/releases
DefaultDirName={autopf}\OpenNetLimit
DefaultGroupName=OpenNetLimit
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=OpenNetLimit-v{#AppVersion}-setup-{#Runtime}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\OpenNetLimit.UI\Assets\OpenNetLimit.ico
PrivilegesRequired=admin
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
UninstallDisplayIcon={app}\OpenNetLimit.UI.exe
UninstallDisplayName=OpenNetLimit
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany=SysAdminDoc
VersionInfoDescription=OpenNetLimit installer
VersionInfoProductName=OpenNetLimit
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The installer registers the service itself, so the ZIP's service scripts stay out.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Install-Service.ps1,Uninstall-Service.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenNetLimit"; Filename: "{app}\OpenNetLimit.UI.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall OpenNetLimit"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\OpenNetLimit.UI.exe"; Description: "{cm:LaunchProgram,OpenNetLimit}"; Flags: nowait postinstall skipifsilent

[Code]
const
  ServiceName = 'OpenNetLimit';
  ServiceMissing = 1060;

function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\') + FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Log(Format('%s %s exited with %d', [FileName, Params, ResultCode]));
  Result := ResultCode;
end;

function ServiceExists: Boolean;
begin
  Result := RunHidden('sc.exe', 'query ' + ServiceName) <> ServiceMissing;
end;

{ net stop waits until the service has stopped, so its files and the driver can be replaced.
  The driver unloads once nothing holds it; another WinDivert program keeping it loaded is fine. }
procedure StopEngine;
begin
  if ServiceExists then
    RunHidden('net.exe', 'stop ' + ServiceName);
  RunHidden('sc.exe', 'stop WinDivert');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopEngine;
  Result := '';
end;

{ Registers the service, or points an existing one (an earlier install, or one set up with
  Install-Service.ps1 from the ZIP) at this copy, then starts it. Starts with Windows. }
procedure InstallService;
var
  BinPath: String;
begin
  BinPath := '"\"' + ExpandConstant('{app}\service\OpenNetLimit.Service.exe') + '\""';
  if ServiceExists then
    RunHidden('sc.exe', 'config ' + ServiceName + ' binPath= ' + BinPath + ' start= auto')
  else
    RunHidden('sc.exe', 'create ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= ' + ServiceName);
  RunHidden('sc.exe', 'description ' + ServiceName + ' "Per-application bandwidth control and traffic monitoring."');
  if RunHidden('sc.exe', 'start ' + ServiceName) <> 0 then
    Log('The service did not start. The app shows the reason once it connects.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallService;
end;

{ Rules and traffic history in ProgramData stay, as with Uninstall-Service.ps1. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopEngine;
    if ServiceExists then
      RunHidden('sc.exe', 'delete ' + ServiceName);
    RegDeleteKeyIncludingSubkeys(HKLM, 'SYSTEM\CurrentControlSet\Services\EventLog\Application\' + ServiceName);
  end;
end;
