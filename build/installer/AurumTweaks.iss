; AurumTweaks.iss - Inno Setup script for the Aurum Tweaks installer.
;
; Produces a branded, versioned, admin-elevating Windows installer. It is intentionally honest:
;   * The installer version is read FROM the published binary (GetVersionNumbersString), so it can never
;     drift from what actually ships.
;   * It bundles a SELF-CONTAINED publish, so end users are never told "it's installed" only to hit a
;     missing-.NET-runtime wall on first launch. No hidden prerequisite.
;   * PrivilegesRequired=admin matches the app's own requireAdministrator manifest - no surprise elevation.
;
; Build steps (run from the repo root):
;   1) Publish self-contained x64:
;        dotnet publish src\AurumTweaks\AurumTweaks.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
;   2) Compile this script with Inno Setup 6:
;        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" build\installer\AurumTweaks.iss
;   3) The signed-ready installer lands in dist\AurumTweaks-Setup-<version>.exe
;      (then sign it: powershell -File build\sign.ps1 -PfxPath ... -File dist\AurumTweaks-Setup-<version>.exe)

#define MyAppName "Aurum Tweaks"
#define MyAppPublisher "Aurum Tweaks"
#define MyAppURL "https://github.com/"            ; replace with the real product/download URL before release
#define MyAppExeName "AurumTweaks.exe"

; Where the self-contained publish lives, relative to this .iss (build\installer -> repo root\publish\win-x64).
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif
#define SourceExe AddBackslash(SourceDir) + MyAppExeName

; Version is taken from the real binary when present, so the installer label matches what ships.
#if FileExists(SourceExe)
  #define MyAppVersion GetVersionNumbersString(SourceExe)
#else
  #define MyAppVersion "0.1.0"
  #pragma message "Publish not found at " + SourceExe + " - using fallback version " + MyAppVersion + ". Publish first for an honest version stamp."
#endif

[Setup]
; A stable AppId keeps upgrades/uninstall coherent across versions - never change it once shipped.
AppId={{8F2A7C61-3B4D-4E9A-9C2F-1A7E5D0B6F34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=AurumTweaks-Setup-{#MyAppVersion}
SetupIconFile=..\..\src\AurumTweaks\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Aurum Tweaks is a 64-bit, Windows 10+ tool that runs elevated - make all three explicit.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin

[Languages]
Name: "french";  MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish. ignoreversion because these are our own files, not shared system DLLs.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";        Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch after install. The exe self-elevates via its manifest; skip on silent installs.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
