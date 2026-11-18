#define MyAppName "Sharp Agent"
#ifndef MyAppVersion
  #define MyAppVersion "1.4.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

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
OutputDir={#OutputDir}
OutputBaseFilename=SharpAgent-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\SharpAgent.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "plugins\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\plugins\*"; DestDir: "{localappdata}\SharpAgent\plugins"; Flags: recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall
Source: "installed.mode"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Sharp Agent"; Filename: "{app}\SharpAgent.exe"
Name: "{autodesktop}\Sharp Agent"; Filename: "{app}\SharpAgent.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\SharpAgent.exe"; Description: "Launch Sharp Agent"; Flags: nowait postinstall skipifsilent
