# Generates assets\SpaceSwipe.ico (multi-size, PNG-compressed) — same design as the tray icon.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $s = $size / 32.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $purple = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x8B, 0x5C, 0xF6))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 255, 255, 255))
    $dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 255, 255, 255))
    $g.FillPath($purple, (New-RoundedPath (1 * $s) (3 * $s) (30 * $s) (26 * $s) (7 * $s)))
    $g.FillPath($dim, (New-RoundedPath (5 * $s) (9 * $s) (6 * $s) (14 * $s) (2 * $s)))
    $g.FillPath($white, (New-RoundedPath (13 * $s) (8 * $s) (14 * $s) (16 * $s) (3 * $s)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) { , (New-IconPng $sz) }

$out = Join-Path $PSScriptRoot '..\assets\SpaceSwipe.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Close()
[System.IO.File]::WriteAllBytes((Join-Path (Split-Path $out) 'icon.png'), $pngs[-1])
"Wrote $out and icon.png"
