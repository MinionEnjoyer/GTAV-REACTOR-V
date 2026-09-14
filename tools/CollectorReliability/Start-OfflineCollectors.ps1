param([string]$OutputDirectory,
    [ValidateRange(10,900)][int]$Seconds = 120)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CollectorHealth.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'ProcessLifetime.cs')
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('ReactorV-Diagnostics\collector-handoff-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runId = [Guid]::NewGuid()
$manifest = [ordered]@{schema=1;runId=$runId.ToString('N');offline=$true;createdUtc=[DateTime]::UtcNow.ToString('o');
    durationSeconds=$Seconds;parentPid=$PID;parentJobFlags=('0x{0:X}' -f [ReactorV.Diagnostics.ProcessLifetime]::CurrentJobFlags());
    noGameLaunch=$true;noInstallationChanges=$true;state='starting';collectors=@()}
$children = @()
try {
    foreach ($spec in @(
        @{role='hang';file='Watch-Controller-Hang.ps1'},
        @{role='session';file='Local-Controller-Test.ps1'})) {
        $script = Join-Path $root ('artifacts\issue-1-controller-startup\' + $spec.file)
        $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$script,'-OutputDirectory',$OutputDirectory,
            '-RunId',$runId.ToString('N'),'-HealthProbeSeconds',[string]$Seconds)
        $child = [ReactorV.Diagnostics.ProcessLifetime]::StartDetached($shell, $arguments, $root)
        $children += $child
        $manifest.collectors += [ordered]@{role=$spec.role;pid=$child.Id;processStartTicks=$child.StartTime.ToUniversalTime().Ticks;
            executable=$shell;inJob=[ReactorV.Diagnostics.ProcessLifetime]::InJob($child);script=$script;
            scriptSha256=(Get-FileHash -LiteralPath $script).Hash.ToLowerInvariant()}
    }
    Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    $wait = [Diagnostics.Stopwatch]::StartNew()
    do {
        $allHealthy = $true
        foreach ($spec in $manifest.collectors) {
            $check = Get-CollectorHealth $OutputDirectory $spec.role $runId
            if (-not $check.healthy) { $allHealthy=$false }
        }
        if ($allHealthy) { break }
        foreach ($child in $children) { if ($child.HasExited) { throw ('Offline collector exited during startup: ' + $child.Id + ', code ' + $child.ExitCode) } }
        Start-Sleep -Milliseconds 250
    } while ($wait.Elapsed.TotalSeconds -lt 15)
    if (-not $allHealthy) { throw 'Offline heartbeat startup deadline exceeded.' }
    & (Join-Path $PSScriptRoot 'Test-CollectorPair.ps1') -OutputDirectory $OutputDirectory -Offline | Out-Null
    $manifest.state='offline-probes-running'
    Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    Write-Output ('Offline probes running. Output: ' + $OutputDirectory)
    Write-Output ('They will stop automatically after ' + $Seconds + ' seconds. No GTA launch, installation, capture, or upload.')
    Write-Output 'Immediate health checks passed; survival after this launcher exits is still to be verified.'
} catch {
    $manifest.state='failed'; $manifest.error=$_.Exception.Message
    Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    foreach ($spec in $manifest.collectors) {
        # Run-scoped cooperative stop only. All probes also have a finite deadline.
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($spec.role + '-stop-' + $runId.ToString('N') + '.request')), 'Stop offline probe.')
    }
    throw
} finally { foreach ($child in $children) { $child.Dispose() } }
