; Inno Setup script. Built by scripts\package.py: ISCC /DSource=dist\app /DOut=dist installer\WinAIMagic.iss
#ifndef Source
  #define Source "..\dist\app"
#endif
#ifndef Out
  #define Out "..\dist"
#endif
#define AppVersion GetVersionNumbersString(Source + "\WinAIMagic.exe")

[Setup]
AppId={{7C7B3E0E-3C5B-4C4C-9D1F-57494E41494D}
AppName=WinAIMagic
AppVersion={#AppVersion}
AppPublisher=WinAIMagic
DefaultDirName={localappdata}\Programs\WinAIMagic
DefaultGroupName=WinAIMagic
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir={#Out}
OutputBaseFilename=WinAIMagic-Setup-{#AppVersion}
Compression=lzma2/normal
SolidCompression=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
UninstallDisplayIcon={app}\WinAIMagic.exe
SetupIconFile=WinAIMagic.ico
LicenseFile={#Source}\LICENSE.txt
WizardStyle=modern
DiskSpanning=no

[Languages]
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
nl.ModelTitle=Map voor de modellen
nl.ModelDesc=Waar wil je de AI-modellen bewaren?
nl.ModelText=Modellen zijn 20 tot 460 GB per stuk. Kies bij voorkeur een snelle NVMe-schijf met genoeg ruimte. Je kunt dit later in de app wijzigen.
en.ModelTitle=Folder for the models
en.ModelDesc=Where do you want to keep the AI models?
en.ModelText=Models are 20 to 460 GB each. Preferably choose a fast NVMe drive with enough space. You can change this later in the app.
de.ModelTitle=Ordner für die Modelle
de.ModelDesc=Wo möchten Sie die KI-Modelle speichern?
de.ModelText=Modelle sind je 20 bis 460 GB groß. Wählen Sie möglichst ein schnelles NVMe-Laufwerk mit genügend Platz. Sie können dies später in der App ändern.
fr.ModelTitle=Dossier des modèles
fr.ModelDesc=Où voulez-vous conserver les modèles d'IA ?
fr.ModelText=Les modèles font de 20 à 460 Go chacun. Choisissez de préférence un disque NVMe rapide avec assez d'espace. Vous pourrez le modifier plus tard dans l'application.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; older versions shipped two measuring tools and a second copy of cuBLAS beside the main engine
Type: files; Name: "{app}\engine\cuda\llama-bench.exe"
Type: files; Name: "{app}\engine\cuda\llama-perplexity.exe"
Type: files; Name: "{app}\engine\vulkan\llama-bench.exe"
Type: files; Name: "{app}\engine\vulkan\llama-perplexity.exe"
Type: files; Name: "{app}\engine\cpu\llama-bench.exe"
Type: files; Name: "{app}\engine\cpu\llama-perplexity.exe"
Type: files; Name: "{app}\engine\tiered\bin\cublas64_13.dll"
Type: files; Name: "{app}\engine\tiered\bin\cublasLt64_13.dll"

[Icons]
Name: "{group}\WinAIMagic"; Filename: "{app}\WinAIMagic.exe"
Name: "{group}\{cm:UninstallProgram,WinAIMagic}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\WinAIMagic"; Filename: "{app}\WinAIMagic.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WinAIMagic.exe"; Description: "{cm:LaunchProgram,WinAIMagic}"; Flags: nowait postinstall skipifsilent

[Code]
var
  ModelPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  ModelPage := CreateInputDirPage(wpSelectDir, CustomMessage('ModelTitle'), CustomMessage('ModelDesc'), CustomMessage('ModelText'), False, '');
  ModelPage.Add('');
  ModelPage.Values[0] := ExpandConstant('{localappdata}\WinAIMagic\models');
end;

// The app takes this over into its settings at the next start (models are never removed by an uninstall).
procedure CurStepChanged(CurStep: TSetupStep);
var
  Data: String;
begin
  if CurStep = ssPostInstall then
  begin
    Data := ExpandConstant('{localappdata}\WinAIMagic');
    ForceDirectories(Data);
    SaveStringToFile(Data + '\model-folder-from-setup.txt', ModelPage.Values[0], False);
  end;
end;
