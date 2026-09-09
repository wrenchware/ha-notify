#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif

[Setup]
AppId={{8C68885E-E04F-4B47-84CE-7E4B5BC870F1}
AppName=HA Notify
AppVersion={#AppVersion}
AppPublisher=wrenchware
AppPublisherURL=https://github.com/wrenchware/ha-notify
AppSupportURL=https://github.com/wrenchware/ha-notify/issues
DefaultDirName={localappdata}\Programs\HA Notify
DefaultGroupName=HA Notify
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
AppMutex=Local\Personal.HaNotify
CloseApplications=no
RestartApplications=no
OutputDir={#ReleaseDir}
OutputBaseFilename=HA-Notify-{#AppVersion}-Setup-x64
SetupIconFile=..\HaNotify\Assets\ha-notify.ico
UninstallDisplayIcon={app}\HaNotify.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\HA Notify"; Filename: "{app}\HaNotify.exe"; AppUserModelID: "Personal.HaNotify.Notifications"
Name: "{autodesktop}\HA Notify"; Filename: "{app}\HaNotify.exe"; Tasks: desktopicon; AppUserModelID: "Personal.HaNotify.Notifications"

[Run]
Filename: "{app}\HaNotify.exe"; Description: "Open HA Notify"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  if CurStep = ssPostInstall then
    if RegQueryStringValue(HKCU, RunKey, 'HaNotify', Existing) and (Existing <> '') then
      RegWriteStringValue(HKCU, RunKey, 'HaNotify', '"' + ExpandConstant('{app}\HaNotify.exe') + '" --tray');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'HaNotify', Existing) then
      if CompareText(Existing, '"' + ExpandConstant('{app}\HaNotify.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, RunKey, 'HaNotify');
end;
