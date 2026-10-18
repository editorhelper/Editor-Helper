@echo off
REM Builds Editor Helper and puts the ready program into the dist folder.
REM Run this after every code change.
REM (Comments are in English: Russian text breaks in the Windows console codepage.)

cd /d "%~dp0"

echo.
echo  Building Editor Helper...
echo.

dotnet publish EditorHelper.csproj -c Release -p:PublishProfile=App --nologo -v quiet

if errorlevel 1 (
    echo.
    echo  BUILD FAILED - see the messages above
    echo.
    pause
    exit /b 1
)

echo.
echo  Done. Run: dist\EditorHelper.exe
echo.