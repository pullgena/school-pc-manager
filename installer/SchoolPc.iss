#ifndef Role
  #define Role "Agent"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.6"
#endif
#if Role == "Agent"
  #define RoleLabel "관리 대상"
  #define AuditFile "agent-audit.log"
#else
  #define RoleLabel "관리자"
  #define AuditFile "admin-audit.log"
#endif
#define AppExe "SchoolPc." + Role + ".exe"

[Setup]
AppId=SchoolPc.{#Role}
AppName=School PC {#RoleLabel}
AppVersion={#AppVersion}
AppPublisher=pullgena
AppPublisherURL=https://github.com/pullgena/school-pc-manager
DefaultDirName={localappdata}\Programs\SchoolPcManager\{#Role}
DefaultGroupName=School PC
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\release
OutputBaseFilename=SchoolPc.{#Role}.Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
AppMutex=Local\SchoolPc.{#Role}
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExe}

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로가기 만들기"; Flags: unchecked
#if Role == "Agent"
Name: "autostart"; Description: "Windows 로그인 시 관리 대상을 트레이에서 자동 실행"
#endif

[Files]
Source: "..\out\{#Role}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\사용방법.md"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\installed.ini"; Section: "SchoolPc"; Key: "Role"; String: "{#Role}"
Filename: "{app}\installed.ini"; Section: "SchoolPc"; Key: "Version"; String: "{#AppVersion}"

[Icons]
Name: "{group}\School PC {#RoleLabel}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\School PC {#RoleLabel}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
#if Role == "Agent"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SchoolPc.Agent"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart
#endif

[Run]
Filename: "{app}\{#AppExe}"; Description: "School PC {#RoleLabel} 실행"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\{#AppExe}.previous"
Type: files; Name: "{app}\installed.ini"
Type: files; Name: "{localappdata}\SchoolPcManager\{#AuditFile}"
#if Role == "Agent"
Type: files; Name: "{localappdata}\SchoolPcManager\agent.settings"
Type: files; Name: "{localappdata}\SchoolPcManager\agent.settings.new"
Type: files; Name: "{localappdata}\SchoolPcManager\agent.certificate"
#else
Type: files; Name: "{localappdata}\SchoolPcManager\admin.profiles"
Type: files; Name: "{localappdata}\SchoolPcManager\admin.profiles.new"
Type: files; Name: "{localappdata}\SchoolPcManager\admin.receive-folders"
Type: files; Name: "{localappdata}\SchoolPcManager\admin.receive-folders.new"
#endif

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  CurrentExe: String;
begin
  if CurStep = ssInstall then
  begin
    CurrentExe := ExpandConstant('{app}\{#AppExe}');
    if FileExists(CurrentExe) then
      if not FileCopy(CurrentExe, CurrentExe + '.previous', False) then
        RaiseException('이전 버전 백업을 만들지 못했습니다. 프로그램을 종료한 뒤 다시 시도하세요.');
  end;
#if Role == "Agent"
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SchoolPc.Agent');
#endif
end;
