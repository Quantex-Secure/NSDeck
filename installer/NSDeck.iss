#ifndef SourceExe
#define SourceExe "..\release\NSDeck.exe"
#endif
#define MyAppVersion GetStringFileInfo(SourceExe, "ProductVersion")

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
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NSDeck"; Filename: "{app}\NSDeck.exe"
Name: "{autodesktop}\NSDeck"; Filename: "{app}\NSDeck.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NSDeck.exe"; Description: "Launch NSDeck"; Flags: nowait postinstall skipifsilent
