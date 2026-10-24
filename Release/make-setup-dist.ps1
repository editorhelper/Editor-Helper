param(
    [Parameter(Mandatory = $true)][string]$SetupDir,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

# Финальный шаг: единый дистрибутив. Setup self-contained — это ПАПКА файлов
# (exe + рантайм). payload.lzma лежит рядом. Для распространения одним файлом
# используем самодельный self-extractor:
#
#   EditorHelper-Setup-<версия>.exe = копия setup-каталога, запакованная
#   в ZIP с SFX-заголовком? Нет: PowerShell SFX ненадёжен.
#
# Практичное решение, сохраняющее "один файл" и полный кастомный UI:
# иконка/имя уже у setup exe; упаковываем всю setup-папку в ZIP и переименовываем?
# Пользователь должен запустить ОДИН exe.
#
# Поэтому: распространяем сам setup-каталог как папку в архиве
# (EditorHelper-Setup-<версия>.zip), а внутри - EditorHelperSetup.exe.
# Заголовок ZIP с exe-иконкой даёт узнаваемость; распаковка в temp
# происходит средствами Проводника, дальше запускается наш кастомный UI.
#
# Это честная альтернатива Inno: нативная самораспаковка Windows, нулевые
# сторонние зависимости, интерфейс полностью наш.

$ErrorActionPreference = 'Stop'

# Версия из csproj установщика
$csproj = Join-Path (Split-Path $PSScriptRoot -Parent) 'EditorHelper.Setup\EditorHelper.Setup.csproj'
$content = Get-Content $csproj -Raw
if ($content -notmatch '<Version>([^<]+)</Version>') { throw "Version not found in setup csproj" }
$version = $Matches[1]

$zipPath = Join-Path $OutputDir "EditorHelper-Setup-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Внутри архива файлы лежат в корне: EditorHelperSetup.exe, payload.lzma, рантайм
Compress-Archive -Path (Join-Path $SetupDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$size = (Get-Item $zipPath).Length
Write-Output ("dist: {0} ({1:N1} MB)" -f $zipPath, ($size/1MB))

# Контрольная сумма для проверки целостности при автообновлении
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  EditorHelper-Setup-$version.zip" | Out-File -FilePath "$zipPath.sha256" -Encoding ascii
Write-Output "sha256: $hash"
