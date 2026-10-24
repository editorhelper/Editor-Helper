@echo off
REM Builds the custom Editor Helper installer.
REM
REM   1. dotnet publish EditorHelper (the app payload, self-contained) - done by publish.cmd
REM   2. dotnet publish EditorHelper.Setup (self-contained: one exe, runs without .NET installed)
REM   3. pack the payload into payload.lzma (Deflate) beside the setup exe
REM   4. copy the setup exe to Release\output as EditorHelper-Setup-<version>.exe
REM
REM Result: Release\output\EditorHelper-Setup-<version>.exe - a single file.
REM
REM Comments are in English: Russian text breaks in the Windows console codepage.
setlocal EnableExtensions

cd /d "%~dp0.."

set "SETUP_DIR=EditorHelper.Setup"
set "PAYLOAD=Release\payload"
set "SETUP_PUBLISH=Release\setup"
set "OUTPUT=Release\output"

echo.
echo  Editor Helper - custom installer build
echo.

if not exist "%PAYLOAD%\EditorHelper.exe" (
    echo  Release\payload is missing. Run Release\publish.cmd first.
    goto :failed
)

echo  [1/4] Cleaning...
if exist "%SETUP_PUBLISH%" rmdir /s /q "%SETUP_PUBLISH%"
if not exist "%OUTPUT%" mkdir "%OUTPUT%"

echo  [2/4] Publishing setup (self-contained)...
REM Self-contained: the installer runs on a clean Windows without .NET.
REM PublishDir is absolute: a relative path resolves against the PROJECT
REM folder, not the repo root, and the exe would land in the wrong place
REM PublishDir must be ABSOLUTE: MSBuild resolves a relative one against the
REM project folder. %~dp0 ends with a backslash - trimmed for concatenation
dotnet publish "%SETUP_DIR%\EditorHelper.Setup.csproj" -c Release -r win-x64 --self-contained true -p:PublishDir=%~dp0setup\ -p:PublishSingleFile=false -p:DebugType=none --nologo
if errorlevel 1 goto :failed

if not exist "%SETUP_PUBLISH%\EditorHelperSetup.exe" goto :failed

echo  [3/4] Packing payload...
powershell -NoProfile -ExecutionPolicy Bypass -File Release\pack-payload.ps1 ^
    -PayloadDir "%PAYLOAD%" -SetupDir "%SETUP_PUBLISH%"
if errorlevel 1 goto :failed

if not exist "%SETUP_PUBLISH%\payload.lzma" goto :failed

echo  [4/4] Finalizing single-file installer...
powershell -NoProfile -ExecutionPolicy Bypass -File Release\make-setup-dist.ps1 ^
    -SetupDir "%SETUP_PUBLISH%" -OutputDir "%OUTPUT%"
if errorlevel 1 goto :failed

echo.
echo  Done. Installer: %OUTPUT%\EditorHelper-Setup-<version>.exe
echo.
endlocal
exit /b 0

:failed
echo.
echo  INSTALLER BUILD FAILED
echo.
endlocal
exit /b 1
