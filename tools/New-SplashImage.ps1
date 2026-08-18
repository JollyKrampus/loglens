<#
.SYNOPSIS
    Draws the LogLens startup splash and writes LogLens\Assets\splash.png.

.DESCRIPTION
    A single-file self-contained WPF exe has a noticeable blank pause on startup
    (extraction, JIT, XAML load, workspace load). WPF's SplashScreen build item shows
    this image almost immediately — it renders via Win32/WIC before the framework has
    finished spinning up — and fades it out once the main window paints.

    SplashScreen sizes its window from the image's RAW pixel dimensions and ignores
    DPI metadata entirely (verified against dotnet/wpf's WindowsBase SplashScreen.cs:
    it feeds WIC's GetBitmapSize straight into CreateWindowEx). So the art is drawn
    at 2x for quality and then downscaled to the size the window will actually be —
    embedding 192-dpi metadata instead ships a card twice the intended size on any
    100%-scale display. Transparent corners give a rounded card.
#>
[CmdletBinding()]
param(
    [string]$OutputPng = (Join-Path $PSScriptRoot '..\LogLens\Assets\splash.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 520x320 logical, drawn at 2x.
$W = 1040; $H = 640

$dir = Split-Path $OutputPng -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# ---- card -------------------------------------------------------------------

$card = New-RoundedRect 4 4 ($W - 8) ($H - 8) 44
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point 0, 0),
    (New-Object System.Drawing.Point 0, $H),
    [System.Drawing.Color]::FromArgb(255, 34, 40, 54),
    [System.Drawing.Color]::FromArgb(255, 20, 22, 30))
$g.FillPath($grad, $card); $grad.Dispose()

$edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 58, 58, 60)), 3
$g.DrawPath($edge, $card); $edge.Dispose()
$card.Dispose()

# ---- the magnifier mark (same geometry family as the icon) ------------------

$LensMetal = [System.Drawing.Color]::FromArgb(255, 240, 244, 250)
$LensGlass = [System.Drawing.Color]::FromArgb(255, 12, 15, 22)
$BarInfo   = [System.Drawing.Color]::FromArgb(255, 127, 200, 255)
$BarWarn   = [System.Drawing.Color]::FromArgb(255, 255, 192, 97)
$BarError  = [System.Drawing.Color]::FromArgb(255, 255, 107, 107)

# Icon coordinates live in a 256 box; place that box centred, y = 52, size 250.
$scale = 250.0 / 256.0
$ox = ($W - 250) / 2.0 - 14   # nudge left so the handle reads as centred
$oy = 52.0
function MapX([double]$v) { return $ox + $v * $scale }
function MapY([double]$v) { return $oy + $v * $scale }
function MapS([double]$v) { return $v * $scale }

$cx = MapX 108; $cy = MapY 104
$r = MapS 78; $ring = MapS 20

# handle first, so the ring covers the joint
$reach = $r + $ring / 2 - (MapS 6)
$hx = $cx + $reach * 0.7071; $hy = $cy + $reach * 0.7071
$pen = New-Object System.Drawing.Pen $LensMetal, (MapS 30)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($pen, $hx, $hy, (MapX 216), (MapY 212))
$pen.Dispose()

$glassR = $r - $ring / 2
$glass = New-Object System.Drawing.SolidBrush $LensGlass
$g.FillEllipse($glass, $cx - $glassR, $cy - $glassR, $glassR * 2, $glassR * 2)
$glass.Dispose()

$clip = New-Object System.Drawing.Drawing2D.GraphicsPath
$clip.AddEllipse($cx - $glassR, $cy - $glassR, $glassR * 2, $glassR * 2)
$state = $g.Save()
$g.SetClip($clip)

$barH = MapS 14
$bars = @(
    @{ x1 = 56; x2 = 156; c = $BarInfo;  row = -1 },
    @{ x1 = 56; x2 = 138; c = $BarWarn;  row = 0 },
    @{ x1 = 56; x2 = 162; c = $BarError; row = 1 }
)
foreach ($b in $bars) {
    $by = $cy + $b.row * (MapS 23) - $barH / 2
    $bp = New-RoundedRect (MapX $b.x1) $by (MapS ($b.x2 - $b.x1)) $barH ($barH / 2)
    $bb = New-Object System.Drawing.SolidBrush $b.c
    $g.FillPath($bb, $bp); $bb.Dispose(); $bp.Dispose()
}

$g.Restore($state); $clip.Dispose()

$rim = New-Object System.Drawing.Pen $LensMetal, $ring
$g.DrawEllipse($rim, $cx - $r, $cy - $r, $r * 2, $r * 2)
$rim.Dispose()

# ---- wordmark ---------------------------------------------------------------

$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center

$title = New-Object System.Drawing.Font 'Segoe UI Semibold', 92, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 228, 228, 228))
$g.DrawString('LogLens', $title, $fg, (New-Object System.Drawing.RectangleF 0, 380, $W, 120), $fmt)
$title.Dispose(); $fg.Dispose()

$tag = New-Object System.Drawing.Font 'Segoe UI', 34, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 138, 142, 150))
$g.DrawString('real-time log viewer', $tag, $dim, (New-Object System.Drawing.RectangleF 0, 508, $W, 60), $fmt)
$tag.Dispose(); $dim.Dispose(); $fmt.Dispose()

$g.Dispose()

# Downscale the 2x art to the size the splash window will actually be on screen.
# SplashScreen uses raw pixel dimensions, so this IS the displayed size; the 2x
# draw + high-quality downscale is purely for edge quality.
$outW = 640
$outH = [int][math]::Round($outW * $H / $W)   # keep the aspect exactly

$final = New-Object System.Drawing.Bitmap $outW, $outH, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$fg = [System.Drawing.Graphics]::FromImage($final)
$fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$fg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $outW, $outH))
$fg.Dispose()
$bmp.Dispose()

$final.SetResolution(96, 96)
$final.Save($OutputPng, [System.Drawing.Imaging.ImageFormat]::Png)
$final.Dispose()

Write-Host "Wrote $OutputPng (${outW}x${outH}, $([math]::Round((Get-Item $OutputPng).Length/1KB,0)) KB)" -ForegroundColor Green
