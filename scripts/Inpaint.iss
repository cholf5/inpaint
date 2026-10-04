; Windows 安装包脚本（Inno Setup 6）：把自包含发布目录打成 per-user Setup.exe，
; 装到 %LOCALAPPDATA%\Programs\Inpaint，带开始菜单/可选桌面快捷方式和卸载器。
; 安装器 UI 仅英文；应用内语言不受影响。
;
; 编译（在仓库根目录）:
;   ISCC /DAppVersion=1.0.0 scripts\Inpaint.iss
; 源目录默认为仓库根下的 Inpaint-{AppVersion}-win-x64（CI publish 步骤的输出），
; 本地打包时可用 /DSourceDir=<发布目录> 覆盖。
; 产出: artifacts/windows/Inpaint-{AppVersion}-win-x64-setup.exe

#define MyAppName "Inpaint"
#define MyAppExe "Inpaint.App.exe"
#define MyAppPublisher "cholf5"
#define MyAppURL "https://github.com/cholf5/inpaint"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\Inpaint-" + AppVersion + "-win-x64"
#endif

[Setup]
; AppId 是安装/升级/卸载识别同一应用的稳定标识，一经发版不可更改
AppId={{0DDAC8C5-D0E3-45B1-B9A9-A5253F30FB7F}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
; per-user 安装：无需管理员权限（应用数据本就在 %APPDATA%\Inpaint）
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExe}
; 升级安装时提示关闭正在运行的应用
CloseApplications=yes
SetupIconFile=..\src\Inpaint.App\Assets\app-icon.ico
OutputDir=..\artifacts\windows
OutputBaseFilename=Inpaint-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
