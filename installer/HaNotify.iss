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
DisableReadyPage=no
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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\HA Notify"; Filename: "{app}\HaNotify.exe"; AppUserModelID: "Personal.HaNotify.Notifications"
Name: "{autodesktop}\HA Notify"; Filename: "{app}\HaNotify.exe"; Tasks: desktopicon; AppUserModelID: "Personal.HaNotify.Notifications"

[Run]
Filename: "{app}\HaNotify.exe"; Description: "Open HA Notify"; Flags: nowait postinstall skipifsilent; Check: OfferLaunch

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
var
  RestartAfterUpdate: Boolean;

function OfferLaunch(): Boolean;
begin
  Result := not RestartAfterUpdate;
end;

function OpenEvent(Access: LongWord; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function VersionPart(var Value: String): Integer;
var P: Integer;
begin
  P := Pos('.', Value);
  if P = 0 then begin Result := StrToIntDef(Value, 0); Value := ''; end
  else begin Result := StrToIntDef(Copy(Value, 1, P - 1), 0); Delete(Value, 1, P); end;
end;

function CompareVersions(Left, Right: String): Integer;
var I, L, R: Integer;
begin
  Result := 0;
  for I := 1 to 4 do begin
    L := VersionPart(Left); R := VersionPart(Right);
    if L < R then begin Result := -1; Exit; end;
    if L > R then begin Result := 1; Exit; end;
  end;
end;

function PreviousVersion(): String;
begin
  if not GetVersionNumbersString(ExpandConstant('{app}\HaNotify.exe'), Result) then Result := '';
  if (Length(Result) > 2) and (Copy(Result, Length(Result) - 1, 2) = '.0') then
    Delete(Result, Length(Result) - 1, 2);
end;

procedure CurPageChanged(CurPageID: Integer);
var Old: String;
begin
  if CurPageID = wpReady then begin
    Old := PreviousVersion();
    if Old <> '' then begin
      WizardForm.PageNameLabel.Caption := 'Update HA Notify';
      WizardForm.PageDescriptionLabel.Caption := Old + '  ->  {#AppVersion}';
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var Old: String;
begin
  Old := PreviousVersion();
  if Old = '' then Result := 'Install HA Notify {#AppVersion}'
  else Result := 'HA Notify ' + Old + ' -> {#AppVersion}';
  Result := Result + NewLine + NewLine + 'Setup will close HA Notify if it is running and reopen it after the update. Your connection settings will be kept.' + NewLine + NewLine + MemoDirInfo + NewLine + MemoTasksInfo;
end;

function HandleRunningApp(ForceClose: Boolean): Boolean;
var Locator, Services, Processes, Process: Variant; I: Integer; Event: THandle;
begin
  Result := False;
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('.', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT * FROM Win32_Process WHERE Name = ''HaNotify.exe''');
  for I := 0 to Processes.Count - 1 do begin
    Process := Processes.ItemIndex(I);
    if not VarIsNull(Process.ExecutablePath) then
      if CompareText(Process.ExecutablePath, ExpandConstant('{app}\HaNotify.exe')) = 0 then begin
        Result := True;
        RestartAfterUpdate := True;
        if ForceClose then begin
          Log('Closing legacy or unresponsive HA Notify PID ' + IntToStr(Process.ProcessId));
          Process.Terminate(0);
        end
        else begin
          Event := OpenEvent($0002, False, 'Local\Personal.HaNotify.UpdateShutdown.' + IntToStr(Process.ProcessId));
          if Event <> 0 then begin
            Log('Requesting graceful shutdown of HA Notify PID ' + IntToStr(Process.ProcessId));
            SetEvent(Event); CloseHandle(Event);
          end;
        end;
      end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var I: Integer; Running: Boolean; Old: String;
begin
  Result := '';
  Old := PreviousVersion();
  if (Old <> '') and (CompareVersions(Old, '{#AppVersion}') > 0) then begin
    Result := 'A newer version (' + Old + ') is already installed. This installer will not downgrade it.';
    Exit;
  end;
  try
    Running := HandleRunningApp(False);
    if Running then begin
      for I := 1 to 20 do begin
        Sleep(250);
        if not HandleRunningApp(False) then Exit;
      end;
      { Older versions do not support the shutdown event. Only close the exact target executable. }
      HandleRunningApp(True);
      for I := 1 to 20 do begin
        Sleep(250);
        if not HandleRunningApp(False) then Exit;
      end;
      Result := 'HA Notify could not be closed. Please try the update again.';
    end;
  except
    Result := 'Setup could not close HA Notify: ' + GetExceptionMessage;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing: String; ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
    if RegQueryStringValue(HKCU, RunKey, 'HaNotify', Existing) and (Existing <> '') then
      RegWriteStringValue(HKCU, RunKey, 'HaNotify', '"' + ExpandConstant('{app}\HaNotify.exe') + '" --tray');
  if (CurStep = ssDone) and RestartAfterUpdate then
    Exec(ExpandConstant('{app}\HaNotify.exe'), '--tray', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

function InitializeUninstall(): Boolean;
var I: Integer;
begin
  Result := True;
  try
    if HandleRunningApp(False) then begin
      for I := 1 to 20 do begin Sleep(250); if not HandleRunningApp(False) then Exit; end;
      HandleRunningApp(True);
      Sleep(500);
    end;
  except
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'HaNotify', Existing) then
      if CompareText(Existing, '"' + ExpandConstant('{app}\HaNotify.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, RunKey, 'HaNotify');
end;