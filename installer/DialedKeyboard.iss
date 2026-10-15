; Dialed Keyboard setup. Build it with installer\build-installer.ps1, which publishes the app
; into installer\build\app and passes AppVersion.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{5B0E2C8A-6F3D-4B71-9C2E-8A4D1F7E3B60}
AppName=Dialed Keyboard
AppVersion={#AppVersion}
AppVerName=Dialed Keyboard {#AppVersion}
AppPublisher=Dialed
AppPublisherURL=https://github.com/Corbyns616/dialed-keyboard
AppSupportURL=https://github.com/Corbyns616/dialed-keyboard
DefaultDirName={autopf}\DialedKeyboard
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\DialedKeyboard\app.ico
UninstallDisplayIcon={app}\DialedKeyboard.exe
UninstallDisplayName=Dialed Keyboard
OutputDir=Output
OutputBaseFilename=DialedKeyboard-Setup
Compression=lzma2/ultra64
SolidCompression=yes
; The startup shortcut belongs to the person installing, who is normally also the admin.
UsedUserAreasWarning=no

[Tasks]
Name: startup; Description: "Start Dialed Keyboard when I sign in to Windows"
Name: desktopicon; Description: "Add a shortcut to the desktop"

[Files]
Source: "build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "trust-and-sign.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dialed Keyboard"; Filename: "{app}\DialedKeyboard.exe"
Name: "{autodesktop}\Dialed Keyboard"; Filename: "{app}\DialedKeyboard.exe"; Tasks: desktopicon
; A shortcut rather than a Run registry entry: Windows opens shortcuts the way a uiAccess app needs.
Name: "{userstartup}\Dialed Keyboard"; Filename: "{app}\DialedKeyboard.exe"; Tasks: startup

[Run]
; shellexec: a uiAccess exe only starts through ShellExecute. postinstall runs it as the signed-in user.
Filename: "{app}\DialedKeyboard.exe"; Description: "Start Dialed Keyboard now"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\trust-and-sign.ps1"" -InstallDir ""{app}"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveCertificate"

[UninstallDelete]
Type: files; Name: "{app}\setup-error.log"

[Code]
function RunTrustAndSign(Extra: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\trust-and-sign.ps1') +
    '" -InstallDir "' + ExpandConstant('{app}') + '"' + Extra,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Result := ResultCode;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Close the installed copy so its files can be replaced. A copy running from anywhere else is left alone.
  Exec('powershell.exe',
    '-NoProfile -Command "$d = ''' + ExpandConstant('{app}') + '\''; ' +
    'Get-Process DialedKeyboard -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -and $_.Path.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (RunTrustAndSign('') <> 0) then
    MsgBox('Dialed Keyboard was copied to your computer, but Windows could not finish setting it up, ' +
      'so it may not start. Please run this setup again. If it still fails, the details are in ' +
      ExpandConstant('{app}\setup-error.log') + '.', mbError, MB_OK);
end;
