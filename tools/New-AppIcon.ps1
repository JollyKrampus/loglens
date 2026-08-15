<#
.SYNOPSIS
    Draws the LogLens application icon and writes a multi-resolution .ico.

.DESCRIPTION
    The mark is a magnifier whose lens contains three coloured log lines
    (info blue / warn amber / error red — the same palette the app uses for
    severities). It reads as "looking closely at logs" at large sizes and still
    reads as "a lens with colour in it" at 16 px.

    Small sizes get a deliberately chunkier variant: at 16 px, strokes scaled
    down from the 256 px artwork land under a pixel and dissolve into grey mush,
    so anything 32 px or below is drawn with a bigger lens and fatter bars.

    Sizes 16-128 are stored as 32-bit BMP/DIB entries and 256 as PNG, which is
    the combination Windows Explorer and the taskbar handle most reliably.

.EXAMPLE
    .\New-AppIcon.ps1
    Writes LogLens\app.ico and a preview sheet next to it.
#>
[CmdletBinding()]
param(
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\LogLens\app.ico'),
    [string]$PreviewPng = (Join-Path $PSScriptRoot '..\docs\app-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------- palette ----

$BgTop     = [System.Drawing.Color]::FromArgb(255, 40, 51, 78)
$BgBottom  = [System.Drawing.Color]::FromArgb(255, 14, 18, 28)
$LensMetal = [System.Drawing.Color]::FromArgb(255, 240, 244, 250)
$LensGlass = [System.Drawing.Color]::FromArgb(255, 10, 14, 22)
$BarInfo   = [System.Drawing.Color]::FromArgb(255, 127, 200, 255)
$BarWarn   = [System.Drawing.Color]::FromArgb(255, 255, 192, 97)
$BarError  = [System.Drawing.Color]::FromArgb(255, 255, 107, 107)

# ---------------------------------------------------------------- helpers ----

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

function Add-Bar($graphics, [double]$x1, [double]$x2, [double]$yc, [double]$h, $color) {
    $path = New-RoundedRect $x1 ($yc - $h / 2) ($x2 - $x1) $h ($h / 2)
    $brush = New-Object System.Drawing.SolidBrush $color
    $graphics.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Draw everything in a 256x256 space and let the transform do the scaling.
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Below ~32 px the fine variant turns to mush, so use chunkier geometry.
    $compact = $size -le 32

    # --- background plate ---
    $plate = New-RoundedRect 6 6 244 244 ($(if ($compact) { 44 } else { 52 }))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0),
        (New-Object System.Drawing.Point $size, $size),
        $BgTop, $BgBottom)
    $g.FillPath($grad, $plate)
    $grad.Dispose()

    if (-not $compact) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 255, 255)), 3
        $g.DrawPath($edge, $plate)
        $edge.Dispose()
    }
    $plate.Dispose()

    # --- geometry ---
    if ($compact) {
        # Push the lens almost to the edges: at 16-32 px every spare pixel of
        # glass is worth more than breathing room around the plate.
        $cx = 112.0; $cy = 106.0; $r = 88.0; $ring = 28.0
        $barH = 26.0; $gap = 36.0
        $bars = @(
            @{ x1 = 60; x2 = 162; c = $BarInfo  },
            @{ x1 = 60; x2 = 140; c = $BarWarn  },
            @{ x1 = 60; x2 = 166; c = $BarError }
        )
        $handleW = 34.0; $handleTo = 236.0
    }
    else {
        $cx = 108.0; $cy = 104.0; $r = 78.0; $ring = 20.0
        $barH = 14.0; $gap = 23.0
        $bars = @(
            @{ x1 = 56; x2 = 156; c = $BarInfo  },
            @{ x1 = 56; x2 = 138; c = $BarWarn  },
            @{ x1 = 56; x2 = 162; c = $BarError }
        )
        $handleW = 30.0; $handleTo = 216.0
    }

    # --- handle (behind the ring so the joint looks solid) ---
    $reach = $r + $ring / 2 - 6
    $hx = $cx + $reach * 0.7071
    $hy = $cy + $reach * 0.7071
    $pen = New-Object System.Drawing.Pen $LensMetal, $handleW
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, $hx, $hy, $handleTo, $handleTo - ($cx - $cy))
    $pen.Dispose()

    # --- lens glass ---
    $glassR = $r - $ring / 2
    $glass = New-Object System.Drawing.SolidBrush $LensGlass
    $g.FillEllipse($glass, $cx - $glassR, $cy - $glassR, $glassR * 2, $glassR * 2)
    $glass.Dispose()

    # --- log lines, clipped to the glass ---
    $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clip.AddEllipse($cx - $glassR, $cy - $glassR, $glassR * 2, $glassR * 2)
    $saved = $g.Save()
    $g.SetClip($clip)

    $i = 0
    foreach ($b in $bars) {
        Add-Bar $g $b.x1 $b.x2 ($cy + ($i - 1) * $gap) $barH $b.c
        $i++
    }

    $g.Restore($saved)
    $clip.Dispose()

    # --- lens rim ---
    $rim = New-Object System.Drawing.Pen $LensMetal, $ring
    $g.DrawEllipse($rim, $cx - $r, $cy - $r, $r * 2, $r * 2)
    $rim.Dispose()

    $g.Dispose()
    return $bmp
}

# ------------------------------------------------------------ ico writing ----

function Get-DibBytes($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER. Height is doubled: XOR image plus the legacy AND mask.
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4))
    0..3 | ForEach-Object { $bw.Write([uint32]0) }

    # BGRA pixels, bottom-up.
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $data.Stride, $w * 4)
    }

    # AND mask: zeroed, because the alpha channel already carries transparency.
    $maskStride = [math]::Floor(($w + 31) / 32) * 4
    $bw.Write((New-Object byte[] ($maskStride * $h)))

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$images = @()

foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    # PNG compression only for 256; DIB below that is the most compatible.
    $payload = if ($s -eq 256) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
    $images += [pscustomobject]@{ Size = $s; Bytes = $payload; Bitmap = $bmp }
    Write-Host ("  {0,3}px  {1,8:N0} bytes" -f $s, $payload.Length)
}

$dir = Split-Path $OutputIco -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$fs = [System.IO.File]::Create($OutputIco)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = if ($img.Size -eq 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)               # palette count
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # colour planes
    $bw.Write([uint16]32)            # bits per pixel
    $bw.Write([uint32]$img.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}

foreach ($img in $images) { $bw.Write([byte[]]$img.Bytes, 0, $img.Bytes.Length) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

# ------------------------------------------------------------- preview -------

$pad = 16
[int]$previewW = [int](($sizes | Measure-Object -Sum).Sum) + $pad * ($sizes.Count + 1)
$preview = [System.Drawing.Bitmap]::new($previewW, 300)
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))

$x = $pad
foreach ($img in $images) {
    $pg.DrawImage($img.Bitmap, $x, [int](150 - $img.Size / 2), $img.Size, $img.Size)
    $x += $img.Size + $pad
}

$x = $pad
$font = New-Object System.Drawing.Font 'Segoe UI', 9
$white = [System.Drawing.Brushes]::White
foreach ($img in $images) {
    $pg.DrawString("$($img.Size)", $font, $white, $x, 250)
    $x += $img.Size + $pad
}

# Nearest-neighbour blow-up of the small sizes: this is the only honest way to
# judge whether 16 and 24 px actually read, rather than guessing from the 256.
$pg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$pg.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$x = $pad
foreach ($img in $images | Where-Object { $_.Size -le 48 }) {
    $zoom = 96
    $pg.DrawImage($img.Bitmap, $x, 12, $zoom, $zoom)
    $pg.DrawString("$($img.Size) x6", $font, $white, $x, ($zoom + 14))
    $x += $zoom + $pad
}

$pg.Dispose()
$preview.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

foreach ($img in $images) { $img.Bitmap.Dispose() }

Write-Host ""
Write-Host "Wrote $OutputIco" -ForegroundColor Green
Write-Host "Preview $PreviewPng" -ForegroundColor Gray
