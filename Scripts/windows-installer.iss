; Inno Setup script for Athena.UI (Windows exe installer)
;
; Compiled in CI by ISCC. Values are injected on the command line, e.g.:
;   ISCC /DMyAppVersion=1.2.3 /DSourceDir=publish\win-x64 /DRepoRoot=. Scripts\windows-installer.iss
;
; Design notes:
;  - Per-user install (PrivilegesRequired=lowest). With "lowest", {autopf}
;    resolves to %LocalAppData%\Programs\Athena, a user-writable location.
;    This is REQUIRED so the in-app updater (Athena.Updater, runs without
;    elevation) can overwrite files in place. Installing into Program Files
;    would break self-update.
;  - SourceDir is the flat `dotnet publish` output for win-x64. It must
;    already contain Athena.UI.exe and the updater\ subfolder.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir must be defined (the win-x64 publish folder)
#endif
#ifndef RepoRoot
  #define RepoRoot "."
#endif

#define MyAppName "Athena"
#define MyAppExeName "Athena.UI.exe"
#define MyAppPublisher "mehaifeng"
#define MyAppURL "https://github.com/mehaifeng/AthenaAgent"

[Setup]
; A stable AppId so future installers upgrade in place instead of installing side by side.
AppId={{8F3A6C2E-5B1D-4E7A-9C44-2A6F0B7E1D90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputBaseFilename=Athena-{#MyAppVersion}-win-x64-setup
SetupIconFile={#RepoRoot}\Assets\Athena.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user install so the in-app updater can write files without admin rights.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
; The installer shows a language picker (English / 简体中文) on launch.
; ChineseSimplified.isl is not bundled with Inno Setup by default; CI drops it
; into the compiler's Languages folder before building (see release.yml).
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Bundle the entire self-contained publish output (app + updater\ subfolder).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; AppUserModelID 不是可选的装饰：未打包的 Win32 应用发 WinRT toast 时，
; CreateToastNotifier 的 AUMID 必须能对应到一个带 System.AppUserModel.ID 的
; 开始菜单快捷方式，否则 Show() 既不报错也不显示任何东西。
; WindowsShortcutAumid 就是从 {group}\Athena.lnk 上读回这个值的；读不到时它会
; 自己把属性补上去（覆盖不了才退回 PowerShell 的 AUMID，署名变成「Windows PowerShell」）。
; 装机时就写好可以省掉首次通知那一次 shell COM 往返。改这里请同步那边的候选路径与常量。
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "com.athena.ai"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "com.athena.ai"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
