#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
#ifndef AppIdentity
  #define AppIdentity "MojinDashuai.Launcher"
#endif
#ifndef InstallFolder
  #define InstallFolder "魔金大帅"
#endif
#ifndef ShortcutName
  #define ShortcutName "魔金大帅"
#endif

[Setup]
AppId={#AppIdentity}
AppName=魔金大帅
AppVersion={#AppVersion}
AppVerName=魔金大帅 {#AppVersion}
AppPublisher=魔金大帅
AppPublisherURL=https://launcher.boshan.uk
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=魔金大帅安装程序
DefaultDirName={localappdata}\Programs\{#InstallFolder}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dark includetitlebar
WizardSmallImageFile=..\ui\public\brand\logo.png
SetupIconFile=..\src\Launcher.Desktop\Assets\launcher.ico
UninstallDisplayIcon={app}\MojinDashuai.Launcher.exe
UninstallDisplayName=魔金大帅
OutputDir={#OutputPath}
OutputBaseFilename=MojinDashuai-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
LZMAUseSeparateProcess=yes
SetupLogging=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=no
UsePreviousAppDir=yes

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{userprograms}\{#ShortcutName}"; Filename: "{app}\MojinDashuai.Launcher.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#ShortcutName}"; Filename: "{app}\MojinDashuai.Launcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\MojinDashuai.Launcher.exe"; Description: "启动魔金大帅"; Flags: nowait postinstall skipifsilent

[Messages]
SelectDirDesc=选择启动器安装位置
SelectDirLabel3=游戏文件位置将在首次登录后选择。
FinishedHeadingLabel=安装完成
FinishedLabel=魔金大帅已安装。

; Uninstall only removes installer-owned program files and shortcuts.
; Account sessions, updates and player-selected game directories are preserved.
