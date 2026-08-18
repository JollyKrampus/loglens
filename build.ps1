<#
.SYNOPSIS
    Builds LogLens as a portable Windows executable.

.DESCRIPTION
    Two flavours:

      -Mode SelfContained  (default)
          One .exe, ~150 MB, needs nothing installed on the target machine.
          This is the one to drop on a USB stick or a jump box.

      -Mode Framework
          One .exe, ~3 MB, requires the .NET 8 Desktop Runtime on the target.
          Good when you control the fleet and the runtime is already there.

    Output lands in .\dist\.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Mode Framework
#>
[CmdletBinding()]
param(
    [ValidateSet('SelfContained', 'Framework')]
    [string]$Mode = 'SelfContained',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$OutDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'LogLens\LogLens.csproj'

if (-not (Test-Path $project)) { throw "Cannot find $project" }

$selfContained = ($Mode -eq 'SelfContained')
$publishDir = Join-Path $OutDir $Mode

Write-Host "Building LogLens ($Mode, $Runtime)..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

$args = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    "--self-contained=$($selfContained.ToString().ToLower())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-o', $publishDir
)

# The SDK rejects single-file compression for framework-dependent publishes
# (NETSDK1176), so only self-contained builds ask for it.
if ($selfContained) { $args += '-p:EnableCompressionInSingleFile=true' }

# PublishTrimmed is deliberately NOT used: WPF is not trim-safe and trimming
# silently breaks XAML reflection at runtime.

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir 'LogLens.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $exe  ($sizeMb MB)"
Write-Host ""
Write-Host "Copy that single file anywhere. On first run it writes" -ForegroundColor Gray
Write-Host "loglens.workspace.json beside itself, or to %APPDATA%\LogLens" -ForegroundColor Gray
Write-Host "if the folder is read-only." -ForegroundColor Gray
