#define MyAppName "file-lantern"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef SourceDir
  #error SourceDir preprocessor variable is required.
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{A4C3D970-4A92-4C6A-9F7A-B4BF9F71B892}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=rwrife
DefaultDirName={autopf}\file-lantern
DefaultGroupName=file-lantern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\FileLantern.App.exe
OutputDir={#OutputDir}
OutputBaseFilename=file-lantern-setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\file-lantern"; Filename: "{app}\FileLantern.App.exe"
Name: "{autodesktop}\file-lantern"; Filename: "{app}\FileLantern.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FileLantern.App.exe"; Description: "{cm:LaunchProgram,file-lantern}"; Flags: nowait postinstall skipifsilent
