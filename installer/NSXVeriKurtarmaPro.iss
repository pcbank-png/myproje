#define MyAppName "NSX Veri Kurtarma Pro"
#define MyAppVersion "1.3.8"
#define MyAppPublisher "NSX Yazılım"
#define MyAppExeName "NSXVeriKurtarmaPro.exe"
#define PublishRoot AddBackslash(SourcePath) + "..\PUBLISH_WIN_X64\"

#if !FileExists(PublishRoot + MyAppExeName) || !FileExists(PublishRoot + "NSXVeriKurtarmaPro.Updater.exe")
  #error Publish eksik. Once PUBLISH_WIN_X64.bat calistirin.
#endif

#if GetVersionNumbersString(PublishRoot + MyAppExeName) != (MyAppVersion + ".0") || GetVersionNumbersString(PublishRoot + "NSXVeriKurtarmaPro.Updater.exe") != (MyAppVersion + ".0")
  #error Publish ve installer surumleri uyusmuyor. BUILD_INSTALLER.bat calistirin.
#endif

#if !FileExists(PublishRoot + "libvlc\win-x64\libvlc.dll") || !FileExists(PublishRoot + "libvlc\win-x64\libvlccore.dll") || !FileExists(PublishRoot + "Languages\tr-TR.json") || !FileExists(PublishRoot + "Languages\en-US.json")
  #error Publish dil veya VLC dosyalari eksik. BUILD_INSTALLER.bat calistirin.
#endif

[Setup]
AppId={{A3CF0804-5AEF-4E56-BF5D-51C63E07C222}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NSX Yazılım\NSX Veri Kurtarma Pro
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\SETUP
OutputBaseFilename=NSXVeriKurtarmaPro_Setup_V{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\NSXVeriKurtarmaPro\Assets\logo.ico

; Windows Ayarlar > Uygulamalar ve Programlar ve Ozellikler icin kaldirma kaydini garanti et.
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

CloseApplications=yes
RestartApplications=no
SetupLogging=yes
MinVersion=10.0.17763
ChangesAssociations=yes

[Files]
; Both EXEs are self-contained; no runtime downloader or prerequisite installer.
Source: "{#PublishRoot}*"; DestDir: "{app}"; Excludes: "*.pdb,*.lib,*.db,*.db-wal,*.db-shm,*.sqlite,*.sqlite3,*.bak,*.log,*.dmp,*.nsx,license-state.dat,language.json,ui-settings.json,pro-filter-settings.json,quick-scan-scope.txt,appsettings*.json,App_Data\*,Backups\*,Logs\*,UpdateTemp\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Unconditional all-users shortcuts. The embedded EXE manifest requests admin.
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} Kaldır"; Filename: "{uninstallexe}"

[Registry]
Root: HKCR; Subkey: ".nsx"; ValueType: string; ValueName: ""; ValueData: "NSXVeriKurtarmaPro.Project"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "NSXVeriKurtarmaPro.Project"; ValueType: string; ValueName: ""; ValueData: "NSX Veri Kurtarma Projesi"; Flags: uninsdeletekey
Root: HKCR; Subkey: "NSXVeriKurtarmaPro.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCR; Subkey: "NSXVeriKurtarmaPro.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} uygulamasını başlat"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runascurrentuser
