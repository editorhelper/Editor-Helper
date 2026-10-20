@echo off
REM Compiles the Windows installer from Release\payload.
REM Requires: publish.cmd already ran, and Inno Setup 6 installed.
REM
REM   installer.cmd                 -- uses the version from the csproj
REM   installer.cmd 0.4.0           -- overrides the version
REM
REM Comments are in English: Russian text breaks in the Windows console codepage.
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0.."

set "OVERRIDE_VERSION=%~1"

REM Inno Setup compiler: standard install locations
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if "%ISCC%"=="" (
    echo  Inno Setup 6 not found. Install it:
    echo      winget install --id JRSoftware.InnoSetup
    echo  and run this script again.
    goto :failed
)

if not exist "Release\payload\EditorHelper.exe" (
    echo  Release\payload is missing. Run Release\publish.cmd first.
    goto :failed
)

REM Version: an explicit argument wins, otherwise read <Version> from the project.
REM Space is listed among the delimiters, otherwise the leading indentation of
REM the csproj line becomes the first token and "0.3.0" lands in token 3.
if not "%OVERRIDE_VERSION%"=="" (
    set "VERSION=%OVERRIDE_VERSION%"
) else (
    for /f "tokens=2 delims=<> " %%v in ('findstr /r /c:"<Version>[0-9]" EditorHelper.csproj') do set "VERSION=%%v"
)

echo %VERSION%| findstr /r "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 (
    echo  Could not read a valid version from the csproj ^(got "%VERSION%"^).
    echo  Pass it explicitly: installer.cmd 0.4.0
    goto :failed
)

echo.
echo  Building installer for version %VERSION%...
echo.

"%ISCC%" /Qp /DMyAppVersion=%VERSION% "Release\setup.iss"
if errorlevel 1 goto :failed

set "OUT=Release\output\EditorHelper-Setup-%VERSION%.exe"
if not exist "%OUT%" goto :failed

echo.
echo  Done: %OUT%
for %%A in ("%OUT%") do echo  Size: %%~zA bytes
echo.
endlocal
exit /b 0

:failed
echo.
echo  INSTALLER BUILD FAILED
echo.
endlocal
exit /b 1
