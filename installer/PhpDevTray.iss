#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by build-installer.ps1
#endif
#ifndef InstallerOutput
  #error InstallerOutput must be supplied by build-installer.ps1
#endif

[Setup]
AppId={{8CC08D39-6BA0-4611-BF08-22B5668619B3}
AppName=PHP DevTray
AppVersion={#AppVersion}
AppPublisher=PHP DevTray
DefaultDirName={localappdata}\Programs\PhpDevTray
DefaultGroupName=PHP DevTray
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir={#InstallerOutput}
OutputBaseFilename=PhpDevTray-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\PhpDevTray.exe
AppMutex=Local\PhpDevTray.Instance
SetupMutex=Local\PhpDevTray.Setup
CloseApplications=no
RestartApplications=no
UninstallDisplayName=PHP DevTray
VersionInfoDescription=PHP DevTray Installer
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PHP DevTray"; Filename: "{app}\PhpDevTray.exe"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{autodesktop}\PHP DevTray"; Filename: "{app}\PhpDevTray.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PhpDevTray.exe"; Description: "Launch PHP DevTray"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := not CheckForMutexes('Local\PhpDevTray.Instance');
  if not Result then
    SuppressibleMsgBox('PHP DevTray is running. Choose "Exit and stop services" from its tray menu, then run the installer again. Closing the window only hides it to the tray.', mbInformation, MB_OK, IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\PhpDevTray.Instance');
  if not Result then
    SuppressibleMsgBox('Choose "Exit and stop services" from the PHP DevTray tray menu before uninstalling. Your runtimes, configuration and captured email will be kept.', mbInformation, MB_OK, IDOK);
end;
