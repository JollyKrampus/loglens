<#
.SYNOPSIS
    Generates log traffic in NLog's default layouts, to test LogLens against the
    format you actually run.

.DESCRIPTION
    Writes two files:

      nlog-pipe.log  -  ${longdate}|${level:uppercase=true}|${logger}|${message}
                        which is NLog's stock file layout, including multi-line
                        exception spill from ${exception:format=tostring}.

      nlog-json.log  -  NLog JsonLayout, one JSON object per line.

    Deliberately includes lines whose *message* contains the word "error" while
    the level is INFO. Keyword matching mis-colours those; the anchored NLog
    preset does not. That is the case worth checking.

.EXAMPLE
    .\Write-NLogTestLogs.ps1 -Seconds 60
#>
[CmdletBinding()]
param(
    [string]$Folder = (Join-Path $PSScriptRoot '..\testlogs'),
    [int]$Seconds = 30,
    [int]$LinesPerSecond = 8
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$Folder = (Resolve-Path $Folder).Path

$pipeFile = Join-Path $Folder 'nlog-pipe.log'
$jsonFile = Join-Path $Folder 'nlog-json.log'

$loggers = @(
    'Acme.Orders.OrderService',
    'Acme.Payments.PaymentGateway',
    'Acme.Web.AuthController',
    'Acme.Infrastructure.CacheWarmer',
    'Acme.Jobs.NightlyBatch'
)

$infoMessages = @(
    'Request handled in {0}ms',
    'Processed {0} records',
    'Cache warmed, {0} entries',
    # The trap: level is INFO but the message says "error".
    'Recovered from a transient error after {0} attempts',
    'Validation completed with no errors ({0} rules)'
)
$warnMessages = @('Slow query took {0}ms', 'Retry {0} of 3', 'Connection pool at {0}% capacity')
$errorMessages = @('Timeout calling payments after {0}ms', 'Upstream returned 500 on attempt {0}')

Write-Host "Writing NLog-format logs to:" -ForegroundColor Cyan
Write-Host "  $pipeFile"
Write-Host "  $jsonFile"

$deadline = (Get-Date).AddSeconds($Seconds)
$rand = [Random]::new()

while ((Get-Date) -lt $deadline) {
    $pipeBatch = New-Object System.Collections.Generic.List[string]
    $jsonBatch = New-Object System.Collections.Generic.List[string]

    for ($i = 0; $i -lt $LinesPerSecond; $i++) {
        $roll = $rand.Next(100)
        $logger = $loggers[$rand.Next($loggers.Count)]
        $n = $rand.Next(1, 900)

        if ($roll -lt 3) {
            $level = 'Fatal'; $msg = 'Unrecoverable state, shutting down'
        } elseif ($roll -lt 15) {
            $level = 'Error'; $msg = ($errorMessages[$rand.Next($errorMessages.Count)] -f $n)
        } elseif ($roll -lt 32) {
            $level = 'Warn';  $msg = ($warnMessages[$rand.Next($warnMessages.Count)] -f $n)
        } elseif ($roll -lt 85) {
            $level = 'Info';  $msg = ($infoMessages[$rand.Next($infoMessages.Count)] -f $n)
        } else {
            $level = 'Debug'; $msg = "Entering handler correlationId=$([Guid]::NewGuid())"
        }

        # NLog's ${longdate} is yyyy-MM-dd HH:mm:ss.ffff
        $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.ffff')
        $pipeBatch.Add(('{0}|{1}|{2}|{3}' -f $stamp, $level.ToUpper(), $logger, $msg))

        # Exceptions spill across lines with no level field on the continuations.
        if ($level -in @('Error', 'Fatal')) {
            $pipeBatch.Add('System.Net.Http.HttpRequestException: The operation timed out.')
            $pipeBatch.Add(' ---> System.TimeoutException: A task was canceled.')
            $pipeBatch.Add('   at Acme.Payments.PaymentClient.ChargeAsync(ChargeRequest r)')
            $pipeBatch.Add('   at Acme.Orders.OrderService.PlaceAsync(Order o)')
            $pipeBatch.Add('   --- End of inner exception stack trace ---')
        }

        $obj = [ordered]@{
            time    = (Get-Date).ToString('o')
            level   = $level
            logger  = $logger
            message = $msg
        }
        if ($level -in @('Error', 'Fatal')) {
            $obj.exception = 'System.Net.Http.HttpRequestException: The operation timed out.'
        }
        $jsonBatch.Add(($obj | ConvertTo-Json -Compress))
    }

    Add-Content -Path $pipeFile -Value $pipeBatch -Encoding utf8
    Add-Content -Path $jsonFile -Value $jsonBatch -Encoding utf8
    Start-Sleep -Milliseconds 1000
}

Write-Host "Done." -ForegroundColor Green
