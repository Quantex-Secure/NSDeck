#ifndef SourceExe
#define SourceExe "..\release\NSDeck.exe"
#endif
#ifndef MyAppVersion
#define MyAppVersion GetStringFileInfo(SourceExe, "FileVersion")
#endif

[Setup]
AppId={{E1C6D188-1EC0-4CE4-920C-A7CCFD9009B8}
AppName=NSDeck
AppVersion={#MyAppVersion}
AppPublisher=Quantex Secure
VersionInfoCompany=Quantex Secure
VersionInfoCopyright=Copyright (C) 2026 Quantex Secure
VersionInfoDescription=NSDeck multi-provider DNS administration console
DefaultDirName={localappdata}\Programs\NSDeck
DefaultGroupName=NSDeck
OutputDir=..\release
OutputBaseFilename=NSDeck-Setup-{#MyAppVersion}
UninstallDisplayIcon={app}\NSDeck.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\nsdeck.ico

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "NSDeck.exe"; Flags: ignoreversion

Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\DOTNET-THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\Install-NSDeckJea.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NSDeck"; Filename: "{app}\NSDeck.exe"
Name: "{autodesktop}\NSDeck"; Filename: "{app}\NSDeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NSDeck.exe"; Description: "Launch NSDeck"; Flags: nowait postinstall skipifsilent
