#ifndef PublishDir
  #error PublishDir must point to the self-contained publish directory
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{992A937A-B667-4AE4-A426-8561520B4CF7}
AppName=Framelet
AppVersion=1.3.0
AppPublisher=Framelet
UninstallDisplayName=Framelet
DefaultDirName={localappdata}\Programs\QingCapture
DefaultGroupName=Framelet
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=Framelet-1.3.0-win-x64-Setup
SetupIconFile={#PublishDir}\icon.ico
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\framelet-f-62545da8.ico
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{userprograms}\轻截图.lnk"
Type: files; Name: "{userprograms}\卸载轻截图.lnk"
; Both translators now use offline/native plus embedded/site. Remove only the
; obsolete duplicated engine trees, never either model or the shared runtime.
Type: filesandordirs; Name: "{app}\offline\engine"
Type: filesandordirs; Name: "{app}\offline\qwen\engine"
Type: files; Name: "{app}\offline\runtime-manifest.json"
Type: files; Name: "{app}\offline\qwen\runtime-manifest.json"
; Removed test/compiler/server resources from prior installations only.
Type: filesandordirs; Name: "{app}\offline\embedded\site\bin"
Type: filesandordirs; Name: "{app}\offline\embedded\site\include"
Type: filesandordirs; Name: "{app}\offline\embedded\site\lib"
Type: filesandordirs; Name: "{app}\offline\embedded\site\llama_cpp\server"
Type: files; Name: "{app}\offline\embedded\site\llama_cpp\lib\*.lib"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\_core\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\_pyinstaller\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\f2py\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\fft\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\lib\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\linalg\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\ma\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\matrixlib\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\polynomial\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\random\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\testing\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\numpy\typing\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\setuptools\_distutils\compilers\C\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\setuptools\_distutils\tests"
Type: filesandordirs; Name: "{app}\offline\embedded\site\setuptools\tests"

[Icons]
Name: "{userprograms}\Framelet"; Filename: "{app}\QingCapture.exe"; IconFilename: "{app}\framelet-f-62545da8.ico"; AppUserModelID: "Framelet.Desktop"
Name: "{userprograms}\卸载 Framelet"; Filename: "{uninstallexe}"; IconFilename: "{app}\framelet-f-62545da8.ico"

[Run]
Filename: "{app}\QingCapture.exe"; Description: "启动 Framelet"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "QingCapture"; Flags: uninsdeletevalue

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  CompactResult: Integer;
  AppDirectory: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDirectory := ExpandConstant('{app}');
    { Do not recursively compress a user-selected broad/shared directory. }
    if CompareText(AppDirectory, ExpandConstant('{localappdata}\Programs\QingCapture')) <> 0 then
    begin
      Log('Custom installation directory: automatic compression skipped to avoid touching unrelated files.');
      Exit;
    end;
    { Transparent Windows compression changes disk allocation, never the
      model/runtime bytes. No external model download or dependency install. }
    if not Exec(ExpandConstant('{sys}\compact.exe'),
      '/C /F /S:"' + AppDirectory + '" /I /Q /EXE:LZX "' + AppDirectory + '\*"',
      AppDirectory, SW_HIDE, ewWaitUntilTerminated, CompactResult) then
      Log('Windows disk compression could not start; the standalone app remains usable.')
    else if CompactResult <> 0 then
      Log('Windows disk compression was unavailable or partial; the standalone app remains usable.')
    else
      Log('Transparent Windows disk compression finished. File contents remain unchanged.');
  end;
end;
