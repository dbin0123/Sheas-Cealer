; Sheas Cealer Nix - Inno Setup 安装脚本
;
; 版本号与架构由 CI 注入：
;   /DMyAppVersion=1.0.0 /DMyAppArch=x64compatible
;
; 输出到 out\installer\（相对脚本目录），CI 从这里取产物。

#define MyAppName "Sheas Cealer Nix"
; 默认值仅供本地直接编译用；CI 通过 /D 注入，必须用 #ifndef 包住，
; 否则命令行定义会被这里的 #define 覆盖（或触发重复定义错误）。
#ifndef MyAppVersion
  #define MyAppVersion "1.0.4"
#endif
#ifndef MyAppArch
  #define MyAppArch "x64compatible"
#endif
#define MyAppPublisher "Space Time"
#define MyAppURL "https://github.com/dbin0123/Sheas-Cealer"
#define MyAppExeName "Sheas-Cealer-Nix.exe"
#define PublishDir "..\out\publish"

[Setup]
OutputDir=..\out\installer
AppId={{D3A7F2C1-9B4E-4E8A-B6C5-1F0E2D3C4A5B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Sheas-Cealer-Nix
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Sheas-Cealer-Nix-Setup-{#MyAppVersion}-{#MyAppArch}
SetupIconFile=..\Sheas-Cealer-Nix-Logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; agent 需要管理员权限改系统代理 / hosts，所以整体提权安装
PrivilegesRequired=admin
ArchitecturesAllowed={#MyAppArch}
ArchitecturesInstallIn64BitMode={#MyAppArch}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Sheas Cealer Nix}"; Flags: nowait postinstall skipifsilent

; 卸载善后：机器级的东西（hosts 里那段伪造块、机器级 Root 存储里的 MITM 根证书、
; /tmp 之类的 socket/pid、还在跑的 mihomo/nginx）都不是 Inno 装的，Inno 一个都不会删。
; 交给 agent 自己那份唯一实现（Cealing-Agent --uninstall，见 Cealing-Agent/Uninstaller.cs）：
; 卸载器本来就是管理员令牌（PrivilegesRequired=admin），agent 直接以同一身份改系统。
;
; 顺序很关键：[UninstallRun] 在文件被删之前执行，所以这里 {app}\Cealing-Agent.exe 还在。
; waituntilterminated 也必须等 —— 让它跑到一半就被删掉，等于留下一张没人认领的证书。
; 不带 --purge-user-data：用户数据（自己维护的上游规则、证书）由下面 InitializeUninstall
; 里问过一次，再由 usPostUninstall 那一步真的删，取消卸载时一个字节都不会少。
[UninstallRun]
Filename: "{app}\Cealing-Agent.exe"; Parameters: "--uninstall --data-dir ""{userappdata}\Sheas-Cealer-Nix"" --user-home ""{userprofile}"""; WorkingDir: "{app}"; RunOnceId: "CealingAgentPurge"; Flags: waituntilterminated runhidden skipifdoesntexist

[Code]
var
  PurgeUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  // 只记录选择，删除要等到卸载真正开始之后（见 CurUninstallStepChanged）：
  // 这里用户还可能取消整个卸载，提前删就变成「问了一句就把用户的规则清了」。
  PurgeUserData := (MsgBox(
    'Also delete the user data folder (your rules, generated certificates and logs)?' #13#10 #13#10
    + ExpandConstant('{userappdata}\Sheas-Cealer-Nix'),
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not PurgeUserData then
      Exit;

    DataDir := ExpandConstant('{userappdata}\Sheas-Cealer-Nix');

    if DirExists(DataDir) then
      // agent 在上面那步已经把目录属主归还给当前用户，这里删得动。
      // 删失败不报错中断卸载：文件被占用（还在跑的日志句柄）时留个空壳比卡住好。
      DelTree(DataDir, True, True, True);
  end;
end;
