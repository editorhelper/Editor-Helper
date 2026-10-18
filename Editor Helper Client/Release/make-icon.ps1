$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'Resources\avatar.png'
$target = Join-Path $root 'Resources\app.ico'

# ICO с PNG-сжатием внутри: один файл на несколько размеров, как ожидает
# проводник Windows и установщик
$sizes = 256, 64, 48, 32, 16

$original = [System.Drawing.Image]::FromFile($source)
$pngs = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($original, 0, 0, $size, $size)
    $graphics.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $pngs += ,@($size, $stream.ToArray())
    $stream.Dispose()
}
$original.Dispose()

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)

$writer.Write([UInt16]0)                      # reserved
$writer.Write([UInt16]1)                      # type: icon
$writer.Write([UInt16]$pngs.Count)            # image count

$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
    $size = $entry[0]
    $data = $entry[1]
    $byte = if ($size -ge 256) { 0 } else { $size }

    $writer.Write([Byte]$byte)                # width
    $writer.Write([Byte]$byte)                # height
    $writer.Write([Byte]0)                    # palette
    $writer.Write([Byte]0)                    # reserved
    $writer.Write([UInt16]1)                  # planes
    $writer.Write([UInt16]32)                 # bpp
    $writer.Write([UInt32]$data.Length)       # data size
    $writer.Write([UInt32]$offset)            # data offset
    $offset += $data.Length
}

foreach ($entry in $pngs) { $writer.Write($entry[1]) }
$writer.Flush()

[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose()
$out.Dispose()

Write-Output ("written " + $target + " (" + (Get-Item $target).Length + " bytes)")
