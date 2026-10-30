; ============================================================
;  Aurora —— Inno Setup 安装包脚本
;
;  用法：
;    iscc installer\setup.iss /DAppVersion=0.1
;    （或直接运行 installer\build-installer.ps1，它会先发布再调用本脚本）
;
;  安装位置策略：
;    默认「为所有用户安装」→ C:\Program Files\Aurora（需要管理员）
;    也可以在安装向导第一步选「仅为我安装」→ %LocalAppData%\Programs\...
;    两种方式都允许自定义目录。
; ============================================================

#ifndef AppVersion
  #define AppVersion "0.1.11"
#endif

#define AppName "Aurora"
#define AppPublisher "lishangi688"
#define AppExeName "Aurora.exe"
#define SourceDir "..\dist\Aurora"

[Setup]
AppId={{B7E4C1D2-9A3F-4E58-8C11-2F6D7A9B0C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/lishangi688/adofai-aurora
AppSupportURL=https://github.com/lishangi688/adofai-aurora/issues
AppUpdatesURL=https://github.com/lishangi688/adofai-aurora/releases

; 默认装到 Program Files（所有用户）；允许在向导里改为「仅为我安装」
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; admin = 默认所有用户；dialog = 允许用户切换成当前用户安装
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist\installer
OutputBaseFilename=Aurora-Setup-{#AppVersion}
SetupIconFile=..\src\AdofaiModManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=yes
MinVersion=10.0
DisableWelcomePage=no

; 跟随 Windows 界面语言自动选择；不弹语言选择框（中文优先）
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no

[Languages]
; 第一项是默认语言
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 注意：排除安装器自身的输出目录，避免递归
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "运行 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 程序不会往安装目录写东西，这里只是兜底清理
Type: filesandordirs; Name: "{app}\Resources"

[Code]
// 卸载时询问是否删除用户数据（配置/收藏/日志）。
// 默认按钮是「否」——保守起见不删；静默卸载也一律不删。
// 注意：无论选什么，都不会碰游戏目录里的 mod。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LocalDir: string;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  if UninstallSilent then
    exit;

  DataDir := ExpandConstant('{userappdata}\AdofaiModManager');
  LocalDir := ExpandConstant('{localappdata}\AdofaiModManager');

  if (not DirExists(DataDir)) and (not DirExists(LocalDir)) then
    exit;

  if MsgBox('是否同时删除本软件的配置、收藏与日志？' + #13#10 + #13#10 +
            '选择「否」会保留它们，以后重新安装仍然有效。' + #13#10 +
            '无论选什么，都不会影响游戏目录里的 mod。',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    DelTree(LocalDir, True, True, True);
  end;
end;
