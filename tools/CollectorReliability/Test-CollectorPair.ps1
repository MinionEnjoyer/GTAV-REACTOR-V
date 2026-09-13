param([Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$Offline)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CollectorHealth.ps1')
if (-not ('ReactorV.Diagnostics.ProcessLifetime' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ProcessLifetime.cs') }
$OutputDirectory = Assert-CollectorOutput $OutputDirectory
$manifest = Get-Content -LiteralPath (Join-Path $OutputDirectory 'launch.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.state -eq 'failed' -or $manifest.collectors.Count -ne 2 -or
    @($manifest.collectors.role | Sort-Object -Unique).Count -ne 2 -or
    @($manifest.collectors | Where-Object { $_.role -notin @('hang','session') }).Count -ne 0) { throw 'Invalid collector launch manifest.' }
if ([bool]$manifest.offline -ne [bool]$Offline) { throw 'Offline probes cannot qualify a live game test.' }
$before = @{}
foreach ($entry in $manifest.collectors) { $before[$entry.role] = Get-CollectorHealth $OutputDirectory $entry.role ([Guid]$manifest.runId) }
Start-Sleep -Milliseconds 2200
$records = @()
foreach ($entry in $manifest.collectors) {
    $a = $before[$entry.role]
    $b = Get-CollectorHealth $OutputDirectory $entry.role ([Guid]$manifest.runId)
    if (-not $a.healthy -or -not $b.healthy -or $b.record.sequence -le $a.record.sequence) { throw ($entry.role + ': heartbeat missing, stale, dead, or not advancing.') }
    if ($b.record.observerPid -ne $entry.pid -or $b.record.processStartTicks -ne $entry.processStartTicks -or
        $b.record.executable -ine $entry.executable) { throw 'Collector does not match the launched process identity.' }
    $process = [Diagnostics.Process]::GetProcessById($entry.pid)
    try {
        if ($process.StartTime.ToUniversalTime().Ticks -ne $entry.processStartTicks -or [ReactorV.Diagnostics.ProcessLifetime]::InJob($process)) { throw 'Collector is no longer independently running.' }
    } finally { $process.Dispose() }
    if (-not $Offline -and -not $b.readyForGame) { throw ($entry.role + ': not ready for game launch.') }
    if ((Get-FileHash -LiteralPath $entry.script).Hash -ine $entry.scriptSha256) { throw 'Collector script changed after launch.' }
    $records += $b.record
}
[ordered]@{checkedUtc=[DateTime]::UtcNow.ToString('o');runId=$manifest.runId;offline=[bool]$Offline;
    bothAdvancing=$true;bothIndependent=$true;readyForGame=(-not [bool]$Offline);collectors=$records} | ConvertTo-Json -Depth 6
