# Builds the app icon (.ico) and the full Square44x44Logo targetsize set from the largest logo PNG in Assets.
# Re-run after changing Square150x150Logo.png; the outputs are committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = (Resolve-Path (Join-Path $PSScriptRoot '..\src\BatteryCarePortal.App\Assets')).Path
$srcPath = Join-Path $assets 'Square150x150Logo.png'
$src = [Drawing.Image]::FromFile($srcPath)

function Get-ResizedPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

try {
    # .ico with PNG-compressed entries
    $icoSizes = 16, 20, 24, 32, 48, 256
    $images = foreach ($s in $icoSizes) { , (Get-ResizedPng $s) }
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$icoSizes.Count)
    $offset = 6 + 16 * $icoSizes.Count
    for ($i = 0; $i -lt $icoSizes.Count; $i++) {
        $s = $icoSizes[$i]
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets 'batterycareportal.ico'), $ms.ToArray())

    # Square44x44Logo targetsize set: plain, unplated and lightunplated
    foreach ($s in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        $bytes = Get-ResizedPng $s
        foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
            [IO.File]::WriteAllBytes((Join-Path $assets "Square44x44Logo.targetsize-$s$suffix.png"), $bytes)
        }
    }
}
finally { $src.Dispose() }
