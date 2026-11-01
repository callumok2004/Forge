#define NameLong "Forge Dev"
#define DirName "Forge Dev"
#define ExeBasename "Forge"
#define RegValueName "ForgeDev"
#define AppUserId "ForgeDev.Forge"
#define AppxPackageName "ForgeDev.ForgeDev"
#define AppxPackage "forge_dev_shell.appx"
#define AppxCert "forge-dev-shell.cer"
#define AppxDll "forge_dev_explorer_command.dll"

[Setup]
AppId={{B5DFFA0D-D5E8-466E-851D-8A7146B2A75C}
AppName={#NameLong}
AppVerName={#NameLong} {#Version}
AppVersion={#Version}
VersionInfoVersion={#Version}
AppPublisher={#NameLong}
DefaultDirName={userpf}\{#DirName}
DefaultGroupName={#NameLong}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=ForgeDevSetup-x64-{#Version}
SetupIconFile={#AssetsDir}\forge-dev.ico
UninstallDisplayIcon={app}\{#ExeBasename}.exe
UninstallDisplayName={#NameLong}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ChangesEnvironment=yes
ChangesAssociations=yes
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=force
RestartApplications=no
SourceDir={#SourceDir}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
OpenWith=Open w&ith %1
AddContextMenuFiles=Add "Open with %1" action to Windows Explorer file context menu
AddContextMenuFolders=Add "Open with %1" action to Windows Explorer directory context menu
AddToPath=Add to PATH (requires shell restart)
Other=Other:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addcontextmenufiles"; Description: "{cm:AddContextMenuFiles,{#NameLong}}"; GroupDescription: "{cm:Other}"
Name: "addcontextmenufolders"; Description: "{cm:AddContextMenuFolders,{#NameLong}}"; GroupDescription: "{cm:Other}"
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:Other}"

[Files]
Source: "*"; Excludes: "\appx"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef HasAppx
Source: "appx\{#AppxCert}"; DestDir: "{app}\appx"; Flags: ignoreversion; Check: ShouldUseWindows11ContextMenu
Source: "appx\*.png"; DestDir: "{app}\appx"; Flags: ignoreversion; Check: ShouldUseWindows11ContextMenu
Source: "appx\{#AppxDll}"; DestDir: "{app}\appx"; Flags: ignoreversion; Check: ShouldUseWindows11ContextMenu
Source: "appx\{#AppxPackage}"; DestDir: "{app}\appx"; BeforeInstall: RemoveAppxPackage; AfterInstall: AddAppxPackage; Flags: ignoreversion; Check: ShouldUseWindows11ContextMenu
#endif

[Icons]
Name: "{autoprograms}\{#NameLong}"; Filename: "{app}\{#ExeBasename}.exe"; AppUserModelID: "{#AppUserId}"
Name: "{autodesktop}\{#NameLong}"; Filename: "{app}\{#ExeBasename}.exe"; AppUserModelID: "{#AppUserId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeBasename}.exe"; Description: "{cm:LaunchProgram,{#NameLong}}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\*\shell\{#RegValueName}"; ValueType: expandsz; ValueName: ""; ValueData: "{cm:OpenWith,{#NameLong}}"; Tasks: addcontextmenufiles; Flags: uninsdeletekey; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\*\shell\{#RegValueName}"; ValueType: expandsz; ValueName: "Icon"; ValueData: "{app}\{#ExeBasename}.exe"; Tasks: addcontextmenufiles; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\*\shell\{#RegValueName}\command"; ValueType: expandsz; ValueName: ""; ValueData: """{app}\{#ExeBasename}.exe"" ""%1"""; Tasks: addcontextmenufiles; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\shell\{#RegValueName}"; ValueType: expandsz; ValueName: ""; ValueData: "{cm:OpenWith,{#NameLong}}"; Tasks: addcontextmenufolders; Flags: uninsdeletekey; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\shell\{#RegValueName}"; ValueType: expandsz; ValueName: "Icon"; ValueData: "{app}\{#ExeBasename}.exe"; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\shell\{#RegValueName}\command"; ValueType: expandsz; ValueName: ""; ValueData: """{app}\{#ExeBasename}.exe"" ""%V"""; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\background\shell\{#RegValueName}"; ValueType: expandsz; ValueName: ""; ValueData: "{cm:OpenWith,{#NameLong}}"; Tasks: addcontextmenufolders; Flags: uninsdeletekey; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\background\shell\{#RegValueName}"; ValueType: expandsz; ValueName: "Icon"; ValueData: "{app}\{#ExeBasename}.exe"; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\directory\background\shell\{#RegValueName}\command"; ValueType: expandsz; ValueName: ""; ValueData: """{app}\{#ExeBasename}.exe"" ""%V"""; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\{#RegValueName}"; ValueType: expandsz; ValueName: ""; ValueData: "{cm:OpenWith,{#NameLong}}"; Tasks: addcontextmenufolders; Flags: uninsdeletekey; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\{#RegValueName}"; ValueType: expandsz; ValueName: "Icon"; ValueData: "{app}\{#ExeBasename}.exe"; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\Drive\shell\{#RegValueName}\command"; ValueType: expandsz; ValueName: ""; ValueData: """{app}\{#ExeBasename}.exe"" ""%V"""; Tasks: addcontextmenufolders; Check: not ShouldUseWindows11ContextMenu
Root: HKCU; Subkey: "Software\Classes\Applications\{#ExeBasename}.exe"; ValueType: none; ValueName: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#ExeBasename}.exe\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeBasename}.exe"
Root: HKCU; Subkey: "Software\Classes\Applications\{#ExeBasename}.exe\shell\open\command"; ValueType: expandsz; ValueName: ""; ValueData: """{app}\{#ExeBasename}.exe"" ""%1"""
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{code:AddToPath|{app}}"; Tasks: addtopath; Check: NeedsAddToPath(ExpandConstant('{app}'))

[Code]
function IsWindows11OrLater(): Boolean;
begin
  Result := GetWindowsVersion >= $0A0055F0;
end;

function ShouldUseWindows11ContextMenu(): Boolean;
begin
#ifdef HasAppx
  Result := IsWindows11OrLater() and (WizardIsTaskSelected('addcontextmenufiles') or WizardIsTaskSelected('addcontextmenufolders'));
#else
  Result := False;
#endif
end;

function PowerShell(const Command: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec('powershell.exe', '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command ' + AddQuotes(Command), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

#ifdef HasAppx
function AppxInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := PowerShell('if (Get-AppxPackage -Name ''{#AppxPackageName}'') { exit 0 } else { exit 1 }', ResultCode) and (ResultCode = 0);
end;

function CertTrusted(): Boolean;
var
  ResultCode: Integer;
begin
  Result := PowerShell('if (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq ''{#CertThumbprint}'' }) { exit 0 } else { exit 1 }', ResultCode) and (ResultCode = 0);
end;

procedure RemoveAppxPackage();
var
  ResultCode: Integer;
begin
  if AppxInstalled() then
    PowerShell('Get-AppxPackage -Name ''{#AppxPackageName}'' | Remove-AppxPackage', ResultCode);
end;

procedure AddAppxPackage();
var
  ResultCode: Integer;
begin
  if not CertTrusted() then
    ShellExec('runas', 'certutil.exe', '-addstore TrustedPeople ' + AddQuotes(ExpandConstant('{app}\appx\{#AppxCert}')), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  PowerShell('Add-AppxPackage -Path ''' + ExpandConstant('{app}\appx\{#AppxPackage}') + ''' -ExternalLocation ''' + ExpandConstant('{app}') + '''', ResultCode);
  Log('Add-AppxPackage result ' + IntToStr(ResultCode));
end;
#endif

function NeedsAddToPath(Dir: String): Boolean;
var
  Path: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Result := True
  else
    Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

function AddToPath(Dir: String): String;
var
  Path: String;
begin
  RegQueryStringValue(HKCU, 'Environment', 'Path', Path);

  if (Length(Path) > 0) and (Path[Length(Path)] = ';') then
    Result := Path + Dir
  else if Length(Path) = 0 then
    Result := Dir
  else
    Result := Path + ';' + Dir;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path, Dir: String;
  Parts: TArrayOfString;
  Joined: String;
  I, Start: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

#ifdef HasAppx
  RemoveAppxPackage();
#endif

  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    exit;

  Dir := ExpandConstant('{app}');
  Joined := '';
  Start := 1;
  SetArrayLength(Parts, 0);

  for I := 1 to Length(Path) + 1 do
    if (I > Length(Path)) or (Path[I] = ';') then begin
      SetArrayLength(Parts, GetArrayLength(Parts) + 1);
      Parts[GetArrayLength(Parts) - 1] := Copy(Path, Start, I - Start);
      Start := I + 1;
    end;

  for I := 0 to GetArrayLength(Parts) - 1 do
    if (Parts[I] <> '') and (CompareText(Parts[I], Dir) <> 0) then begin
      if Joined <> '' then
        Joined := Joined + ';';
      Joined := Joined + Parts[I];
    end;

  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Joined);
end;
