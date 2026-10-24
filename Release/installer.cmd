@echo off
REM Builds the custom Editor Helper installer from Release\payload.
REM The Inno Setup based installer is retired: the installer now shares the
REM application's Premium Cosmic design and is built from EditorHelper.Setup.
REM
REM   installer.cmd                 -- build payload (if missing) + installer
REM   installer.cmd 0.6.0           -- override the version (rarely needed;
REM                                     the version comes from csproj)
setlocal EnableExtensions

cd /d "%~dp0.."

REM Payload must exist; build it when missing
if not exist "Release\payload\EditorHelper.exe" (
    echo  Payload missing - building it first...
    call Release\publish.cmd
    if errorlevel 1 goto :failed
)

call Release\pack-installer.cmd
if errorlevel 1 goto :failed

endlocal
exit /b 0

:failed
echo.
echo  INSTALLER BUILD FAILED
echo.
endlocal
exit /b 1
