$ErrorActionPreference = 'Stop'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:EDITORHELPER_ENABLE_DEV_LOGIN = 'true'
$env:EDITORHELPER_ALLOW_LEGACY_ADMIN_TOKEN = 'true'
$env:EDITORHELPER_DATA_DIR = Join-Path $PSScriptRoot 'EditorHelper.Server\server-data'
$env:EDITORHELPER_OWNER_TELEGRAM_ID = '<ваш Telegram ID>'

$serverProject = Join-Path $PSScriptRoot 'EditorHelper.Server\EditorHelper.Server.csproj'
Write-Host 'Starting Editor Helper API on http://127.0.0.1:5080'
Write-Host 'Development-only local login is enabled; do not expose this server to your network.'
dotnet run --project $serverProject
