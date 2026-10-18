@echo off
REM Starts the Editor Helper server locally.
REM Set TELEGRAM_BOT_TOKEN / TELEGRAM_CHANNEL_ID here or in server-data\secrets.json.
REM (Comments in English: Russian text breaks in the Windows console codepage.)

cd /d "%~dp0"

echo.
echo  Building Editor Helper server...
echo.

dotnet build EditorHelper.Server.sln -v q --nologo
if errorlevel 1 (
    echo.
    echo  BUILD FAILED - see the messages above
    echo.
    pause
    exit /b 1
)

echo.
echo  Starting server. Admin panel: http://127.0.0.1:5080/admin
echo  Press Ctrl+C to stop.
echo.

dotnet run --project EditorHelper.Server\EditorHelper.Server.csproj --no-build

pause
