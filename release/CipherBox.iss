; CipherBox 安装包脚本（Inno Setup 7）
;
; 由 release\publish-release.ps1 调用：
;   ISCC.exe /DAppVersion=1.8.2.2 /DSourceDir=<自包含产物目录> CipherBox.iss
;
; 特点：
;   - 按当前用户安装到 %LOCALAPPDATA%\Programs\CipherBox，不申请管理员权限
;   - 首次启动不做任何联网行为（程序本身完全离线）
;   - 自带卸载程序，会出现在「应用和功能」里
;   - 不删除用户自己加密的 .cbx 文件

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\release\dist\selfcontained"
#endif

[Setup]
AppId={{8F2A5C71-9D4B-4E63-8A1F-2B7C5E9D3A64}
AppName=CipherBox
AppVersion={#AppVersion}
AppVerName=CipherBox {#AppVersion}
AppPublisher=CipherBox contributors
AppPublisherURL=https://github.com/
DefaultDirName={localappdata}\Programs\CipherBox
DefaultGroupName=CipherBox
DisableProgramGroupPage=yes
UninstallDisplayName=CipherBox {#AppVersion}
UninstallDisplayIcon={app}\CipherBox.exe
SetupIconFile=..\src\CBox.App\Assets\CipherBox.ico
; 按当前用户安装，不需要管理员
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=CipherBox-{#AppVersion}-Setup
; 133 MB 单文件，用最大压缩
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 只支持 64 位 Windows 10 及以上（.NET 10 运行时要求）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\CipherBox.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.en.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CipherBox"; Filename: "{app}\CipherBox.exe"
Name: "{group}\{cm:UninstallProgram,CipherBox}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CipherBox"; Filename: "{app}\CipherBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CipherBox.exe"; Description: "{cm:LaunchProgram,CipherBox}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时不碰用户自己加密的 .cbx —— 只删程序目录里我们放进去的东西
Type: filesandordirs; Name: "{app}\docs"
