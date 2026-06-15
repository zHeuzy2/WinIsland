#define AppName      "WinIsland"
#define AppVersion   GetEnv("APP_VERSION")
#define AppPublisher "zHeuzy"
#define AppURL       "https://github.com/zHeuzy/WinIsland"
#define AppExeName   "WinIsland.exe"
#define BuildDir     GetEnv("BUILD_DIR")

[Setup]
AppId={{A3F1B2C4-7E8D-4F9A-B0C1-2D3E4F5A6B7C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=WinIsland-{#AppVersion}-Setup
SetupIconFile=..\WinIsland\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Require Windows 10 2004+
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Require elevation (writes to Program Files)
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english";    MessagesFile: "compiler:Default.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"

[Tasks]
Name: "startup"; Description: "Start WinIsland with Windows"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: not startup

[Registry]
; Optional Windows startup entry (only when the user ticks the task)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startup

[Run]
; Relaunch after install. Shows as a checkbox in interactive mode and also runs
; after a silent in-app update (no skipifsilent) so the updated app comes back.
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall shellexec

[UninstallRun]
; Kill the running instance before uninstalling so files aren't locked.
Filename: "taskkill.exe"; Parameters: "/f /im {#AppExeName}"; \
  Flags: runhidden skipifdoesntexist; RunOnceId: "KillApp"
