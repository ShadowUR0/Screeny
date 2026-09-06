#define MyAppName "Screeny"
#define MyAppVersion "1.8.1"
#define MyAppExeName "Screeny.exe"

[Setup]
AppId={{A7B6D948-8F70-4ED4-B072-1979969F0741}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Screeny
DefaultGroupName=Screeny
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=Screeny-Setup
SetupIconFile=..\Assets\screeny icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Screeny"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Screeny"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
