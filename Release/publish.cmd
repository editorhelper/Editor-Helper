@echo off
REM Builds the release payload: self-contained publish, ready to be packaged
REM by the installer. No obfuscation - the client ships readable IL because the
REM project is open source, and the client holds no secrets to protect.
REM
REM Output goes to Release\payload, which the installer packages.
REM
REM   publish.cmd                       -- release build for a local server
REM   publish.cmd https://api.example.com
REM
REM Comments are in English: Russian text breaks in the Windows console codepage.
setlocal EnableExtensions

cd /d "%~dp0.."

set "API_URL=%~1"
if "%API_URL%"=="" set "API_URL=http://127.0.0.1:5080"

set "PAYLOAD=Release\payload"
set "PUBLISH=Release\publish"

echo.
echo  Editor Helper - release build
echo  API address: %API_URL%
echo.

echo  [1/4] Cleaning previous output...
if exist "%PAYLOAD%" rmdir /s /q "%PAYLOAD%"
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"

echo  [2/4] Publishing self-contained build...
REM Self-contained: a beta tester does not need the .NET runtime installed.
dotnet publish EditorHelper.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishDir=%PUBLISH%\ -p:EhDefaultApiBaseUrl="%API_URL%" ^
    -p:DebugType=none -p:PublishReadyToRun=false --nologo -v quiet
if errorlevel 1 goto :failed

echo  [3/4] Assembling payload...
xcopy "%PUBLISH%\*" "%PAYLOAD%\" /e /i /q /y >nul
if errorlevel 1 goto :failed

echo  [4/4] Checking payload...
if not exist "%PAYLOAD%\EditorHelper.exe" goto :failed
if not exist "%PAYLOAD%\EditorHelper.dll" goto :failed
if exist "%PAYLOAD%\Data" goto :demo_data
if exist "%PAYLOAD%\settings.json" goto :dev_file
if exist "%PAYLOAD%\EditorHelper.pdb" goto :dev_file

echo.
echo  Done. Payload: %PAYLOAD%
echo  Next: Release\installer.cmd
echo.
endlocal
exit /b 0

:demo_data
echo.
echo  FAILED: demo Data folder reached the payload. Release builds must not
echo  ship the sample catalog - users should see the real server catalog.
echo.
endlocal
exit /b 1

:dev_file
echo.
echo  FAILED: a development or debug file reached the payload.
echo.
endlocal
exit /b 1

:failed
echo.
echo  BUILD FAILED - see the messages above
echo.
endlocal
exit /b 1
