; vTerm setup (Inno Setup 6), like vXplorer's. Packages dist\vTerm.exe - build it first with build.cmd (which also runs this).
;
; Make the setup:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\vTerm.iss
; It lands in dist\setup\vTerm-setup.exe - the same name in every release, so a web page can link to the latest (the version is read from dist\vTerm.exe).
;
; Installs for the current user only (no admin): %LOCALAPPDATA%\Programs\vTerm. Settings and the log stay in %APPDATA%\vTerm,
; so an existing setup carries straight over. Everything the options change is under HKCU and is undone by the uninstaller.

#define SourceExe "..\dist\vTerm.exe"
#if !FileExists(SourceExe)
  #error dist\vTerm.exe is missing - run build.cmd first
#endif
; "0.1.0.0" -> "0.1.0"
#define AppVer RemoveFileExt(GetVersionNumbersString(SourceExe))

[Setup]
AppId={{06CD4D4D-4445-41EA-9BDF-29EDF1E0FFFD}
AppName=vTerm
AppVersion={#AppVer}
AppVerName=vTerm {#AppVer}
AppPublisher=V
VersionInfoVersion={#AppVer}
DefaultDirName={autopf}\vTerm
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; every running vTerm holds this (Program.cs): setup and uninstall ask you to close it first
AppMutex=vTerm.Running
SetupIconFile=..\vterm.ico
UninstallDisplayIcon={app}\vTerm.exe
UninstallDisplayName=vTerm
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist\setup
OutputBaseFilename=vTerm-setup
; a newer setup over an older one: keeps the options picked last time
UsePreviousTasks=yes

[Messages]
SetupAppRunningError=vTerm is open. Close its windows, then click OK to carry on.
UninstallAppRunningError=vTerm is open. Close its windows, then click OK to carry on.

[Tasks]
Name: "openinmenu"; Description: "Add ""Open in vTerm"" to the right-click menu of folders and drives"
Name: "desktopicon"; Description: "A shortcut on the desktop"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\vTerm"; Filename: "{app}\vTerm.exe"
Name: "{autodesktop}\vTerm"; Filename: "{app}\vTerm.exe"; Tasks: desktopicon

[Registry]
; "Open in vTerm" on folders, drives and a folder's empty space: a tab starting there
Root: HKCU; Subkey: "Software\Classes\Directory\shell\vterm.open"; ValueType: string; ValueName: ""; ValueData: "Open in vTerm"; Flags: uninsdeletekey; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\vterm.open"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\vTerm.exe"",0"; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\vterm.open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\vTerm.exe"" --dir ""%1"""; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\vterm.open"; ValueType: string; ValueName: ""; ValueData: "Open in vTerm"; Flags: uninsdeletekey; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\vterm.open"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\vTerm.exe"",0"; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\vterm.open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\vTerm.exe"" --dir ""%1"""; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\vterm.open"; ValueType: string; ValueName: ""; ValueData: "Open in vTerm"; Flags: uninsdeletekey; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\vterm.open"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\vTerm.exe"",0"; Tasks: openinmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\vterm.open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\vTerm.exe"" --dir ""%V"""; Tasks: openinmenu

[Run]
Filename: "{app}\vTerm.exe"; Description: "Start vTerm now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; left by the updater next to the exe
Type: files; Name: "{app}\vTerm.new.exe"
Type: files; Name: "{app}\vTerm.old.exe"
Type: files; Name: "{app}\vTerm.failed.exe"
Type: files; Name: "{app}\vterm-write-test.tmp"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() and DirExists(ExpandConstant('{userappdata}\vTerm')) then
  begin
    if MsgBox('Also delete your vTerm settings (saved connections, profiles, options) and log?' + #13#10 + #13#10 +
              ExpandConstant('{userappdata}\vTerm') + #13#10 + #13#10 +
              'Keep them (No) if you might install vTerm again - it picks up where you left off.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\vTerm'), True, True, True);
  end;
end;
