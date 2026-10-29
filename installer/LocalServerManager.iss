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
AppName=Local Server Manager
AppVersion={#AppVersion}
AppPublisher=Local Server Manager
DefaultDirName={localappdata}\Programs\LocalServerManager
DefaultGroupName=Local Server Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir={#InstallerOutput}
OutputBaseFilename=LocalServerManager-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\LocalServerManager.exe
AppMutex=Local\LocalServerManager.Instance
SetupMutex=Local\LocalServerManager.Setup
CloseApplications=no
RestartApplications=no
UninstallDisplayName=Local Server Manager
VersionInfoDescription=Local Server Manager Installer
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Local Server Manager"; Filename: "{app}\LocalServerManager.exe"; WorkingDir: "{app}"; Check: not WizardNoIcons
Name: "{autodesktop}\Local Server Manager"; Filename: "{app}\LocalServerManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LocalServerManager.exe"; Description: "Launch Local Server Manager"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  Result := not CheckForMutexes('Local\LocalServerManager.Instance');
  if not Result then
    SuppressibleMsgBox('Local Server Manager is running. Choose "Exit and stop services" from its tray menu, then run the installer again. Closing the window only hides it to the tray.', mbInformation, MB_OK, IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\LocalServerManager.Instance');
  if not Result then
    SuppressibleMsgBox('Choose "Exit and stop services" from the Local Server Manager tray menu before uninstalling. Your runtimes, configuration and captured email will be kept.', mbInformation, MB_OK, IDOK);
end;
