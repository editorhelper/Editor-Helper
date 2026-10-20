; Editor Helper — установщик для Windows.
;
; Сборка: Release\installer.cmd (нужен Inno Setup 6).
; Вход:   Release\payload  — self-contained сборка с обфусцированной сборкой.
; Выход:  Release\output\EditorHelper-Setup-<версия>.exe
;
; Секретов и файлов разработки здесь быть не должно: publish.cmd проверяет
; payload и не пускает в релиз Data\, settings.json и PDB.

#define MyAppName "Editor Helper"
#define MyAppExeName "EditorHelper.exe"
#define MyAppPublisher "Editor Helper"
#define MyAppURL "https://github.com/editor-helper"

; Версия берётся из проекта, а не пишется здесь вручную
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; Payload собирается Release\publish.cmd. Проверка на этапе компиляции:
; без неё установщик собрался бы пустым и пользователь получил нерабочий ярлык
#if !FileExists("payload\EditorHelper.exe")
  #error "Release\payload не собран. Запустите Release\publish.cmd перед сборкой установщика."
#endif

[Setup]
; Идентификатор приложения постоянен: по нему Windows отличает обновление
; от параллельной установки, а деинсталлятор находит свою запись
AppId={{8F3D2A1C-5B7E-4C9D-A2F6-1E4B8C7D9A35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Данные пользователя лежат в %LOCALAPPDATA%, поэтому установка «для всех»
; не мешает работе без прав администратора
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir=output
OutputBaseFilename=EditorHelper-Setup-{#MyAppVersion}
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Обновление поверх установленной версии: закрыть запущенное приложение,
; а не требовать от пользователя сделать это вручную
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian";  MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Всё содержимое payload: сборка self-contained, рантайм .NET ставится вместе
; с приложением, поэтому отдельная проверка .NET не нужна
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Папка приложения удаляется целиком: после деинсталляции не должно оставаться
; ни файлов, ни временных каталогов
Type: filesandordirs; Name: "{app}"

; Данные пользователя (настройки, избранное, сессия) лежат в %LOCALAPPDATA%
; и по умолчанию сохраняются — переустановка не должна сбрасывать аккаунт
