<#
.SYNOPSIS
    Packs the LogLens icon into a macOS .icns for the app bundle.

.DESCRIPTION
    Dot-sources New-AppIcon.ps1 to reuse its vector drawing — which also
    regenerates app.ico, keeping the two platforms' icons identical by
    construction — then renders the macOS size ladder and writes an .icns
    containing PNG entries, which every supported macOS accepts.

    The icns container is simple enough to write by hand: an 8-byte header
    ("icns" + big-endian total length) followed by chunks of 4-byte type code,
    big-endian length (including the 8-byte chunk header), and image data.

.EXAMPLE
    .\New-MacIcns.ps1
    Writes packaging\LogLens.icns (and refreshes LogLens\app.ico).
#>
[CmdletBinding()]
param(
    [string]$OutputIcns
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputIcns) { $OutputIcns = Join-Path $scriptDir '..\packaging\LogLens.icns' }

# Brings New-IconBitmap and Get-PngBytes into scope (and refreshes app.ico).
. (Join-Path $scriptDir 'New-AppIcon.ps1')

# Type code -> pixel size. The @2x codes (ic11-ic14) are Retina variants of the
# base sizes; storing every entry as PNG keeps this trivially correct.
$entries = [ordered]@{
    'icp4' = 16
    'icp5' = 32
    'ic11' = 32      # 16@2x
    'ic12' = 64      # 32@2x
    'ic07' = 128
    'ic13' = 256     # 128@2x
    'ic08' = 256
    'ic09' = 512
    'ic14' = 512     # 256@2x
    'ic10' = 1024    # 512@2x
}

$chunks = @()
foreach ($e in $entries.GetEnumerator()) {
    $bmp = New-IconBitmap $e.Value
    $chunks += @{ Type = $e.Key; Data = (Get-PngBytes $bmp) }
    $bmp.Dispose()
}

function Write-BigEndian([System.IO.BinaryWriter]$writer, [uint32]$value) {
    $writer.Write([byte](($value -shr 24) -band 0xFF))
    $writer.Write([byte](($value -shr 16) -band 0xFF))
    $writer.Write([byte](($value -shr 8) -band 0xFF))
    $writer.Write([byte]($value -band 0xFF))
}

$total = 8
foreach ($c in $chunks) { $total += 8 + $c.Data.Length }

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([System.Text.Encoding]::ASCII.GetBytes('icns'))
Write-BigEndian $bw ([uint32]$total)
foreach ($c in $chunks) {
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes($c.Type))
    Write-BigEndian $bw ([uint32](8 + $c.Data.Length))
    $bw.Write($c.Data)
}
$bw.Flush()

$dir = Split-Path $OutputIcns -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutputIcns, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host "Wrote $OutputIcns ($([math]::Round((Get-Item $OutputIcns).Length / 1KB)) KB, $($chunks.Count) entries)"
