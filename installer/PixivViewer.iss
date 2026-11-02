; ============================================================
;  Pixiv Viewer WinUI —— Inno Setup 安装包脚本
;
;  用法（在仓库根目录执行）：
;    installer\build-installer.ps1
;
;  或手动：
;    & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\PixivViewer.iss
;
;  可选覆盖参数：
;    /DMySourceDir="D:\CSharp\Release\PixivViewer-win-x64"
;    /DMyAppVersion="1.0.1"
; ============================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.3"
#endif

#ifndef MySourceDir
  #define MySourceDir "D:\CSharp\Release\PixivViewer-win-x64"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "D:\CSharp\Release"
#endif

#define MyAppName "Pixiv Viewer"
#define MyAppPublisher "junsi233"
#define MyAppURL "https://pixiv.junsi233.top"
#define MyAppExeName "Pixiv Viewer WinUI.exe"
#define MyAppId "{{8E2C4A61-5B7D-4F3E-9C1A-2D6B0E7F4A93}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; 固定「为所有用户安装」：升级时 Inno 能从 HKLM 的卸载项找到旧安装目录，
; 真正做到覆盖安装（以前弹「仅为我安装/所有用户」，选默认就找不到旧目录，得手动改路径）
PrivilegesRequired=admin
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousSetupType=yes
UsePreviousTasks=yes
AllowNoIcons=yes

OutputDir={#MyOutputDir}
OutputBaseFilename=PixivViewer-Setup-{#MyAppVersion}-x64
SetupIconFile=..\Pixiv Viewer WinUI\Pixiv Viewer WinUI\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; 覆盖安装时先关掉正在运行的程序，避免文件占用
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\Unofficial\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 自包含发布目录（.NET + Windows App SDK 运行时都在一起），整个文件夹原样安装
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Pixiv 第三方客户端"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "Pixiv 第三方客户端"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 安装目录本身由 Inno 负责清理，这里只兜底删掉运行期可能残留的日志
Type: files; Name: "{app}\*.log"

[Code]
{ 卸载时询问是否一并删除登录凭证 / 设置 / 下载记录（%LOCALAPPDATA%\PixivViewer） }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PixivViewer');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除本地数据？' + #13#10#13#10 +
                '包含：登录凭证、偏好设置、下载记录、日志' + #13#10 +
                DataDir, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
