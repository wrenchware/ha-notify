Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\HaNotify\Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$source = [Drawing.Image]::FromFile((Join-Path $assetDirectory 'home-assistant.png'))
$images = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $bitmap = New-Object Drawing.Bitmap($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $images += ,@($size, $stream.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'ha-notify.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDirectory 'ha-notify.ico'))
$writer = New-Object IO.BinaryWriter($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($entry in $images) {
    $dimension = if ($entry[0] -eq 256) { 0 } else { $entry[0] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$entry[1].Length); $writer.Write([uint32]$offset)
    $offset += $entry[1].Length
}
foreach ($entry in $images) { $writer.Write([byte[]]$entry[1]) }
$writer.Dispose()
$source.Dispose()
