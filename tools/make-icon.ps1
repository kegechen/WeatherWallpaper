# Generate app.ico (sun + cloud) in all sizes Windows asks for.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Out app.ico] [-Preview shots\icon.png]
# Sizes <= 64 are stored as 32-bit DIBs (readable by every loader, incl. System.Drawing.Icon); 128/256 as PNG.
param([string]$Out = "$PSScriptRoot\..\app.ico", [string]$Preview = '')
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 32.0     # design grid is 32 x 32
    function C([double]$cx, [double]$cy, [double]$r) { New-Object System.Drawing.RectangleF ([float](($cx - $r) * $s)), ([float](($cy - $r) * $s)), ([float](2 * $r * $s)), ([float](2 * $r * $s)) }

    # sun: warm gradient + thin rim
    $sun = C 12 11.5 8.6
    $sb = New-Object System.Drawing.Drawing2D.LinearGradientBrush $sun, ([System.Drawing.Color]::FromArgb(255, 255, 222, 92)), ([System.Drawing.Color]::FromArgb(255, 255, 160, 36)), 45.0
    $g.FillEllipse($sb, $sun)
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(170, 226, 128, 16)), ([float][Math]::Max(1.0, 0.7 * $s))
    $g.DrawEllipse($rim, $sun)

    # cloud = capsule base + three puffs, filled as one shape (winding = union)
    $cloud = New-Object System.Drawing.Drawing2D.GraphicsPath ([System.Drawing.Drawing2D.FillMode]::Winding)
    $parts = @((C 11 24.3 3.7), (C 26.3 24.3 3.7), (C 14 21 4.8), (C 20.5 17.3 6.6), (C 26 21.2 4.4))
    foreach ($r in $parts) { $cloud.AddEllipse($r) }
    $cloud.AddRectangle((New-Object System.Drawing.RectangleF ([float](11 * $s)), ([float](20.6 * $s)), ([float](15.3 * $s)), ([float](7.4 * $s))))

    # outline first (each part stroked; the fill on top leaves only the outer half) so the white cloud reads on light backgrounds
    $ol = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 64, 88, 120)), ([float][Math]::Max(1.6, 0.9 * $s))
    $ol.LineJoin = 'Round'
    $g.DrawPath($ol, $cloud)
    $cb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, ([float](9 * $s)), $size, ([float](21 * $s))), ([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(255, 208, 222, 238)), 90.0
    $g.FillPath($cb, $cloud)
    $g.Dispose()
    return $bmp
}

function To-Png($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }

function To-Dib($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) }
    }
    $stride = [int]([Math]::Ceiling($w / 32.0) * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $stride
        for ($x = 0; $x -lt $w; $x++) { if ($bmp.GetPixel($x, $y).A -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) } }
        $bw.Write($row)
    }
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($z in $sizes) {
    $b = Draw-Icon $z
    $data = if ($z -le 64) { To-Dib $b } else { To-Png $b }
    if ($Preview -and $z -eq 256) { $b.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    , @($z, $data)
}

$fs = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Out))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($im in $images) {
    $z = $im[0]; $d = $im[1]
    $dim = if ($z -ge 256) { 0 } else { $z }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$d.Length); $bw.Write([int]$offset)
    $offset += $d.Length
}
foreach ($im in $images) { $bw.Write([byte[]]$im[1]) }
$bw.Close()
Write-Host "OK -> $Out ($($sizes -join ', '))"
