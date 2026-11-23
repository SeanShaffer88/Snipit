; Snipit installer — Inno Setup (per-user, x64).
; Packages the self-contained publish output (dist/files) with the bundled
; ffmpeg, into one Setup.exe. Mirrors the former SeanPak release shape:
; Snipit.exe + desktop/start-menu shortcuts + Start at login.

; Version is kept in this file alongside Directory.Build.props; bumped per release.
#define AppVersion "0.29.0"
#define OutputBaseFilename "Snipit-0.29.0-Setup"

[Setup]
; Stable, unique, never changes for this app.
AppId={{3114132b-886c-4c1f-9e39-8d9cd4fa970d}}
AppName=Snipit
AppVersion={#AppVersion}
AppPublisher=Sean Shaffer
AppPublisherURL=https://github.com/SeanShaffer88/Snipit
DefaultDirName={localappdata}\Programs\Snipit
DefaultGroupName=Snipit
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
OutputDir=dist-installer
OutputBaseFilename={#OutputBaseFilename}
AppIcon=assets\icon.ico

[Files]
; Everything the self-contained publish produces, ffmpeg included.
Source: "dist\files\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autodesktop}\Snipit"; Filename: "{app}\Snipit.exe"
Name: "{group}\Snipit"; Filename: "{app}\Snipit.exe"

[Registry]
; Start at login = HKCU Run key, owned by the app (it toggles the same key).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Snipit"; Value: "\"{app}\Snipit.exe\""; Flags: uninsdeletevalue
; The installed-copy detection key the app reads (StartAtLogin), in the same form SeanPak's uninstaller wrote.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Snipit"; ValueType: string; ValueName: "InstallLocation"; Value: "{app}"; Flags: uninsdeletevalue
