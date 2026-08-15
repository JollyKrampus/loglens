<#
.SYNOPSIS
    Generates fake log traffic so you can try LogLens without waiting on a real system.

.EXAMPLE
    .\Write-TestLogs.ps1 -Seconds 120
    Writes to .\testlogs\{dev,test,prod}-app.log for two minutes.
#>
[CmdletBinding()]
param(
    [string]$Folder = (Join-Path $PSScriptRoot '..\testlogs'),
    [int]$Seconds = 60,
    [int]$LinesPerSecond = 6
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$Folder = (Resolve-Path $Folder).Path

$envs = @('dev', 'test', 'prod')
$files = @{}
foreach ($e in $envs) { $files[$e] = Join-Path $Folder "$e-app.log" }

$components = @('OrderService', 'PaymentGateway', 'AuthController', 'CacheWarmer', 'ScheduledJobs')
$infos  = @('Request handled in {0}ms', 'Cache hit ratio {0}%', 'Processed {0} records', 'Heartbeat ok, uptime {0}s')
$warns  = @('Slow query took {0}ms', 'Retry {0} of 3', 'Connection pool at {0}% capacity')
$errors = @('Timeout calling payments after {0}ms', 'Unhandled 500 from upstream, attempt {0}', 'Deserialization failed at offset {0}')

Write-Host "Writing to $Folder for $Seconds seconds. Ctrl+C to stop early." -ForegroundColor Cyan
foreach ($e in $envs) { Write-Host "  $($files[$e])" }

$deadline = (Get-Date).AddSeconds($Seconds)
$rand = [Random]::new()

while ((Get-Date) -lt $deadline) {
    foreach ($e in $envs) {
        for ($i = 0; $i -lt $LinesPerSecond; $i++) {
            $roll = $rand.Next(100)
            $comp = $components[$rand.Next($components.Count)]
            $n    = $rand.Next(1, 900)

            # Prod stays noisier on errors so the sidebar badge has something to show.
            $errorOdds = if ($e -eq 'prod') { 12 } else { 5 }

            if ($roll -lt 2) {
                $level = 'FATAL'; $msg = "Process is shutting down: unrecoverable state in $comp"
            } elseif ($roll -lt (2 + $errorOdds)) {
                $level = 'ERROR'; $msg = ($errors[$rand.Next($errors.Count)] -f $n)
            } elseif ($roll -lt 30) {
                $level = 'WARN';  $msg = ($warns[$rand.Next($warns.Count)] -f $n)
            } elseif ($roll -lt 88) {
                $level = 'INFO';  $msg = ($infos[$rand.Next($infos.Count)] -f $n)
            } else {
                $level = 'DEBUG'; $msg = "Entering $comp handler with correlationId=$([Guid]::NewGuid())"
            }

            $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
            $line  = '{0} {1,-5} {2} {3}' -f $stamp, $level, $comp, $msg
            Add-Content -Path $files[$e] -Value $line -Encoding utf8

            # A stack trace now and then, to exercise the multi-line rules.
            if ($level -eq 'FATAL') {
                $trace = @(
                    "   at $comp.Execute(RequestContext ctx)",
                    "   at Pipeline.Invoke(HttpContext http)",
                    "   at Server.HandleAsync(Socket s)"
                )
                Add-Content -Path $files[$e] -Value $trace -Encoding utf8
            }
        }
    }
    Start-Sleep -Milliseconds 1000
}

Write-Host "Done." -ForegroundColor Green
