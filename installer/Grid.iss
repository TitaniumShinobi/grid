; Grid Stage 1 installer.
; The application identity and icon are temporary recovery-stage assets.
; This installer owns only its application directory and shortcuts. It never
; removes Grid's connection store or any external MO2/game content.

#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8F9C0D7A-6A31-4C89-AB6C-4FEE8C6EA3E2}
AppName=Grid
AppVersion={#AppVersion}
AppPublisher=Grid
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Grid Setup
DefaultDirName={localappdata}\Programs\Grid
DefaultGroupName=Grid
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=GridSetup
SetupIconFile={#SourceDir}\Assets\TemporaryIdentity\GridTemporary.ico
UninstallDisplayName=Grid
UninstallDisplayIcon={app}\Grid.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
CreateUninstallRegKey=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Grid"; Filename: "{app}\Grid.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Grid.exe"; Comment: "Grid"

[Run]
Filename: "{app}\Grid.exe"; Description: "Launch Grid"; Flags: nowait postinstall skipifsilent
