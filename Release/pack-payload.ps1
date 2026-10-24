param(
    [Parameter(Mandatory = $true)][string]$PayloadDir,
    [Parameter(Mandatory = $true)][string]$SetupDir
)

# Упаковка payload в payload.lzma тем же форматом, который читает InstallEngine:
# "EHP1" + Deflate-поток записей "varint(длина пути) путь varint(размер) байты".
# Паковщик на PowerShell повторяет формат построчно: записи небольшие, объём
# одного вызова .Write() ограничен памятью сборочной машины, но для ~150 МБ
# стриминга по файлам этого достаточно.

$ErrorActionPreference = 'Stop'

# Абсолютный путь обязателен: относительный префикс испортил бы срез
# относительных путей файлов (FullName абсолютный, а Substring считал бы
# длину относительного префикса)
$PayloadDir = [System.IO.Path]::GetFullPath($PayloadDir)
$SetupDir = [System.IO.Path]::GetFullPath($SetupDir)

# Функция объявлена до цикла: PowerShell поднимает function-объявления только
# при исполнении достигшей их строки, поэтому порядок обязателен
function Write-Varint($stream2, $value) {
    # Арифметика byte-значений: PowerShell -bor возвращает Int32/Int64,
    # и прямой [byte] привод больших значений падает. Работаем масками int.
    $v = [int64][long]$value
    while ($v -ge 0x80) {
        $stream2.WriteByte([byte](($v -band 0x7F) -bor 0x80))
        $v = $v -shr 7
    }
    $stream2.WriteByte([byte]$v)
}

$target = Join-Path $SetupDir 'payload.lzma'

$files = Get-ChildItem $PayloadDir -Recurse -File | Sort-Object FullName

$stream = [System.IO.File]::Create($target)
try {
    $buffer = [System.Text.Encoding]::ASCII.GetBytes('EHP1')
    $stream.Write($buffer, 0, $buffer.Length)

    $deflate = New-Object System.IO.Compression.DeflateStream($stream,
        [System.IO.Compression.CompressionLevel]::Optimal, $true)

    $copyBuffer = New-Object byte[] 81920

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($PayloadDir.Length + 1).Replace('\', '/')
        $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($relative)
        $size = $file.Length

        Write-Varint $deflate $pathBytes.Length
        $deflate.Write($pathBytes, 0, $pathBytes.Length)
        Write-Varint $deflate $size

        $input = [System.IO.File]::OpenRead($file.FullName)
        try { $input.CopyTo($deflate) } finally { $input.Dispose() }
    }

    # Конец архива: путь нулевой длины
    Write-Varint $deflate 0
    $deflate.Dispose()
}
finally {
    $stream.Dispose()
}

$packed = (Get-Item $target).Length
$raw = ($files | Measure-Object Length -Sum).Sum
Write-Output ("payload: {0} files, {1:N1} MB -> {2:N1} MB compressed" -f $files.Count, ($raw/1MB), ($packed/1MB))
