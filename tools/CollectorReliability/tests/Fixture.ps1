param([Parameter(Mandatory=$true)][string]$OutputDirectory, [Parameter(Mandatory=$true)][Guid]$RunId,
    [ValidateSet('normal','exit','stall','error','startup-error')][string]$Mode = 'normal', [int]$Seconds = 12)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\CollectorHealth.ps1')
if ($Mode -eq 'startup-error') { throw 'Deliberate pre-heartbeat fixture failure.' }
$health = New-CollectorHealth $OutputDirectory 'fixture' $RunId $true
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-CollectorStop $health) { Update-CollectorHealth $health 'cancelled' 'Fixture stop acknowledged.'; exit 0 }
        if ($clock.Elapsed.TotalSeconds -ge 3) {
            if ($Mode -eq 'exit') { [Environment]::Exit(37) }
            if ($Mode -eq 'error') { throw 'Deliberate fixture exception.' }
        }
        if ($Mode -ne 'stall' -or $clock.Elapsed.TotalSeconds -lt 3) {
            Update-CollectorHealth $health 'waiting' 'Offline fixture.'
        }
        Start-Sleep -Milliseconds 250
    }
    Update-CollectorHealth $health 'probe-complete' 'Fixture completed naturally.'
} catch {
    Update-CollectorHealth $health 'error' $_.Exception.Message
    exit 38
}
