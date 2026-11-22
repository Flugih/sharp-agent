#define MyAppName "Sharp Agent"
#define PublishDir "..\publish"
; The version comes from the published exe; RemoveFileExt drops its fourth (build) number.
#define MyAppVersion RemoveFileExt(GetVersionNumbersString(PublishDir + "\SharpAgent.exe"))

[Setup]
AppId={{5EC4E2AC-0D3B-48B5-9D0B-719B005245C7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Sharp Agent
DefaultDirName={localappdata}\Programs\SharpAgent
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=SharpAgent-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\SharpAgent.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
; User data left in the publish folder by a local run never goes into the installer.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\data,\data.migrating,\.env*,\auth,\history,\state.json*,\*.log,\workspace,\attachments,\temp,\installed.mode,\portable.mode"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.mode"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Sharp Agent"; Filename: "{app}\SharpAgent.exe"
Name: "{autodesktop}\Sharp Agent"; Filename: "{app}\SharpAgent.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Run]
Filename: "{app}\SharpAgent.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
