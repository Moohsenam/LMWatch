; SafeChat installer.
;
; Built by tools\release.ps1, which publishes the app first and then calls:
;
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\build\publish tools\SafeChat.iss
;
; What comes out is one SafeChat-Setup-1.2.3.exe with the whole .NET runtime
; inside it, so a customer with no internet, no VPN and a bare Windows can
; still install it.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\build\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\build"
#endif

#define AppName    "SafeChat"
#define AppExe     "SafeChat.exe"
#define Publisher  "SafeChat"
#define SiteUrl    "https://safechat.ir"

[Setup]
; Never change this. It is how Windows knows an install is this program and not
; a second copy of it, so every update replaces the one that is there.
AppId={{8F3C6A41-5D72-4E19-9B2A-7C4E0D6F1A83}

AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#SiteUrl}
AppSupportURL={#SiteUrl}
AppUpdatesURL={#SiteUrl}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

; Installed for everyone on the machine, so Windows asks for approval once at
; install time and once per update.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; The app is 64-bit because the .NET runtime travelling with it is.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutputDir}
OutputBaseFilename=SafeChat-Setup-{#AppVersion}
SetupIconFile=..\src\ClaudeWatch.App\Assets\app.ico

; A self-contained .NET app is around 150 MB of files. This gets the installer
; down to roughly a third of that, at the cost of a slower build.
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4

; Almost nothing to read. Installing is one approval and a progress bar, and an
; update started from inside the app shows no window at all.
WizardStyle=modern
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=no

; An update arrives while the app is open. Windows is asked to close it first
; rather than the install failing on a locked file.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; runasoriginaluser matters: the installer is elevated, and without it the app
; would come back up as an administrator and stay that way. It asks for rights
; itself, for the firewall rule and the clock, only when it needs them.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall runasoriginaluser

[UninstallRun]
; The scheduled tasks and the firewall rule outlive the folder, so they are
; taken out before it goes.
; No runasoriginaluser here on purpose: deleting the scheduled tasks and the
; firewall rule needs rights, and inheriting the uninstaller's means Windows
; does not ask a second time.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
// Settings and history live in AppData and are the customer's, so an uninstall
// offers to keep them. Answering no to this is the only way they are removed.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataFolder: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataFolder := ExpandConstant('{userappdata}\SafeChat');

    if DirExists(DataFolder) then
    begin
      if MsgBox('Remove your SafeChat settings and activity history as well?'#13#10#13#10
                + DataFolder,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataFolder, True, True, True);
      end;
    end;
  end;
end;
