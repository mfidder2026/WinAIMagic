# Renders the WinAIMagic icon: a rounded square with the blue gradient of make_icon.swift and hand-drawn sparkles, as one .ico
# with PNG images from 16 to 256 px, for installer\WinAIMagic.ico and src\WinAIMagic.App\Assets\WinAIMagic.ico.
#   pwsh scripts\make_icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$targets = @((Join-Path $root 'installer\WinAIMagic.ico'), (Join-Path $root 'src\WinAIMagic.App\Assets\WinAIMagic.ico'))

function Add-Star($path, [double]$cx, [double]$cy, [double]$r) {
    $k = 0.16 * $r
    $tips = @(@(0, -1), @(1, 0), @(0, 1), @(-1, 0), @(0, -1))
    $path.StartFigure()
    for ($i = 0; $i -lt 4; $i++) {
        $a = $tips[$i]; $b = $tips[$i + 1]
        $c = @((($a[0] + $b[0]) * $k), (($a[1] + $b[1]) * $k))
        # one curve from tip to tip through the waist
        $path.AddBezier([single]($cx + $a[0] * $r), [single]($cy + $a[1] * $r), [single]($cx + $c[0]), [single]($cy + $c[1]),
            [single]($cx + $c[0]), [single]($cy + $c[1]), [single]($cx + $b[0] * $r), [single]($cy + $b[1] * $r))
    }
    $path.CloseFigure()
}

function Render([int]$px) {
    $bmp = New-Object Drawing.Bitmap $px, $px, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = [double]$px
    # small sizes keep less margin: every pixel counts there
    $inset = if ($px -le 32) { $s * 0.03 } else { $s * 0.09 }
    $radius = $s * 0.2
    $w = $s - 2 * $inset
    $shape = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $shape.AddArc([single]$inset, [single]$inset, [single]$d, [single]$d, 180, 90)
    $shape.AddArc([single]($inset + $w - $d), [single]$inset, [single]$d, [single]$d, 270, 90)
    $shape.AddArc([single]($inset + $w - $d), [single]($inset + $w - $d), [single]$d, [single]$d, 0, 90)
    $shape.AddArc([single]$inset, [single]($inset + $w - $d), [single]$d, [single]$d, 90, 90)
    $shape.CloseFigure()
    $rect = New-Object Drawing.RectangleF ([single]$inset), ([single]$inset), ([single]$w), ([single]$w)
    $fill = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, ([Drawing.Color]::FromArgb(255, 51, 84, 250)), ([Drawing.Color]::FromArgb(255, 23, 31, 107)), 60.0
    $g.FillPath($fill, $shape)

    $stars = New-Object Drawing.Drawing2D.GraphicsPath
    $stars.FillMode = 'Winding'
    if ($px -le 20) {
        Add-Star $stars ($s * 0.50) ($s * 0.50) ($s * 0.38)
    } else {
        Add-Star $stars ($s * 0.45) ($s * 0.54) ($s * 0.29)
        Add-Star $stars ($s * 0.71) ($s * 0.30) ($s * 0.13)
        Add-Star $stars ($s * 0.72) ($s * 0.73) ($s * 0.085)
    }
    $g.FillPath([Drawing.Brushes]::White, $stars)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($px in $sizes) { , (Render $px) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $px = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($px % 256)); $w.Write([byte]($px % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
foreach ($t in $targets) {
    New-Item -ItemType Directory -Force (Split-Path $t) | Out-Null
    [IO.File]::WriteAllBytes($t, $out.ToArray())
    Write-Output "$t $($out.Length) bytes"
}
