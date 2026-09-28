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
  #define MyAppVersion "1.0.1"
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
