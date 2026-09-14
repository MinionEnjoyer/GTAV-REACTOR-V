param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\CollectorHealth.ps1')
Add-Type -Path (Join-Path $PSScriptRoot '..\ProcessLifetime.cs')
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('ReactorV-Diagnostics\collector-selftest-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runId = [Guid]::NewGuid()
$checks = [Collections.Generic.List[string]]::new()
$owned = @()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL: ' + $Name) }
    $checks.Add($Name)
}
function Spawn([string]$Script, [string[]]$Extra, [string]$Name) {
    $args = @('-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$Script) + $Extra
    $quoted = ($args | ForEach-Object { [ReactorV.Diagnostics.ProcessLifetime]::Quote($_) }) -join ' '
    $child = Start-Process -FilePath $shell -ArgumentList $quoted -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory ($Name + '-stdout.txt')) `
        -RedirectStandardError (Join-Path $OutputDirectory ($Name + '-stderr.txt'))
    [void]$child.Handle
    $script:owned += $child
    return $child
}
function Wait-Healthy([string]$Directory, [string]$Role, [Guid]$Id) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $health = Get-CollectorHealth $Directory $Role $Id
        if ($health.healthy) { return $health }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 10)
    throw ('Heartbeat not healthy: ' + $Directory + ', ' + $health.reason)
}
try {
    Check ([ReactorV.Diagnostics.ProcessLifetime]::Quote('') -ceq '""') 'Empty argument quoting'
    Check ([ReactorV.Diagnostics.ProcessLifetime]::Quote('C:\path with spaces\') -ceq '"C:\path with spaces\\"') 'Trailing slash and spaces quoting'
    Check ([ReactorV.Diagnostics.ProcessLifetime]::Quote('a"b') -ceq '"a\"b"') 'Embedded quote escaping'
    $rejected=$false
    try { Assert-CollectorOutput $env:TEMP | Out-Null } catch { $rejected=$true }
    Check $rejected 'Output outside local diagnostics rejected'
    $rejected=$false
    try { Assert-CollectorOutput (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ReactorV-Diagnostics-other\test') | Out-Null } catch { $rejected=$true }
    Check $rejected 'Sibling-prefix path rejected'
    $sharedOutput=Join-Path (Get-CollectorDataRoot) 'path-policy-fixture'
    Check ((Assert-CollectorOutput $sharedOutput) -ieq $sharedOutput) 'Shared non-AppData output root accepted'
    $rejected=$false
    try { Assert-CollectorOutput ((Get-CollectorDataRoot) + '-other\test') | Out-Null } catch { $rejected=$true }
    Check $rejected 'Shared sibling-prefix path rejected'
    $rejected=$false
    try { Assert-CollectorOutput (Get-CollectorDataRoot) | Out-Null } catch { $rejected=$true }
    Check $rejected 'Shared root itself is not a run output'
    Check ((Get-CollectorToolPath).StartsWith((Get-CollectorDataRoot) + '\tools\', [StringComparison]::OrdinalIgnoreCase)) 'Collector dependency uses shared non-AppData root'

    $fixtures=@{}
    foreach ($mode in @('normal','exit','stall','error','startup-error')) {
        $directory=Join-Path $OutputDirectory $mode
        $child = Spawn (Join-Path $PSScriptRoot 'Fixture.ps1') @('-OutputDirectory',$directory,'-RunId',$runId.ToString('N'),'-Mode',$mode,'-Seconds','18') $mode
        $fixtures[$mode]=@{process=$child;directory=$directory}
    }
    $normal=$fixtures.normal.directory
    $first=Wait-Healthy $normal 'fixture' $runId
    Check (-not $first.readyForGame) 'Offline fixture never reports game readiness'
    $next=Confirm-CollectorProgress $normal 'fixture' $runId
    Check ($next.record.sequence -gt $first.record.sequence) 'Actual loop heartbeat advances'
    $rejected=$false
    try { Confirm-CollectorProgress $normal 'fixture' $runId -RequireGameReady | Out-Null } catch { $rejected=$true }
    Check $rejected 'Offline probe rejected by live game gate'
    Check (-not (Get-CollectorHealth $normal 'fixture' ([Guid]::NewGuid())).healthy) 'Mismatched run identity rejected'
    $rejected=$false
    try { New-CollectorHealth $normal 'fixture' $runId $true | Out-Null } catch { $rejected=$true }
    Check $rejected 'Duplicate heartbeat owner rejected'
    Check ($fixtures.exit.process.WaitForExit(5000) -and $fixtures.exit.process.ExitCode -eq 37) 'Abrupt fixture exit observed by retained handle'
    Check (-not (Get-CollectorHealth $fixtures.exit.directory 'fixture' $runId 30).healthy) 'Dead process rejected even with fresh-looking heartbeat'
    Check ($fixtures.error.process.WaitForExit(5000) -and $fixtures.error.process.ExitCode -eq 38) 'Handled exception returns distinct failure code'
    Check ((Get-CollectorHealth $fixtures.error.directory 'fixture' $runId).reason -eq 'terminal-error') 'Handled exception publishes terminal health'
    Check ($fixtures['startup-error'].process.WaitForExit(5000) -and $fixtures['startup-error'].process.ExitCode -ne 0) 'Pre-heartbeat startup failure is not success'
    Check (-not (Get-CollectorHealth $fixtures['startup-error'].directory 'fixture' $runId).healthy) 'Missing startup heartbeat rejected'
    $stallClock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $stall=Get-CollectorHealth $fixtures.stall.directory 'fixture' $runId
        if ($stall.reason -eq 'stale-heartbeat') { break }
        Start-Sleep -Milliseconds 100
    } while ($stallClock.Elapsed.TotalSeconds -lt 8)
    Check ($stall.reason -eq 'stale-heartbeat' -and -not $fixtures.stall.process.HasExited) 'Live but wedged fixture rejected by stale heartbeat'

    $tamperDirectory=Join-Path $OutputDirectory 'invalid-record'
    [void][IO.Directory]::CreateDirectory($tamperDirectory)
    $tamper=($next.record | ConvertTo-Json | ConvertFrom-Json)
    $tamper.processStartTicks=[long]$tamper.processStartTicks + 1
    Write-CollectorJson (Join-Path $tamperDirectory 'fixture-heartbeat.json') $tamper
    Check (-not (Get-CollectorHealth $tamperDirectory 'fixture' $runId 30).healthy) 'Reused/mismatched PID start identity rejected'
    [IO.File]::WriteAllText((Join-Path $tamperDirectory 'fixture-heartbeat.json'), '{broken')
    Check (-not (Get-CollectorHealth $tamperDirectory 'fixture' $runId).healthy) 'Malformed status rejected'
    $stop=Join-Path $normal ('fixture-stop-' + $runId.ToString('N') + '.request')
    [IO.File]::WriteAllText($stop,'Stop own offline fixture.')
    Check ($fixtures.normal.process.WaitForExit(3000)) 'Cooperative fixture cancellation exits promptly'
    Check ((Get-CollectorHealth $normal 'fixture' $runId).reason -eq 'terminal-cancelled') 'Cooperative cancellation is explicitly terminal'

    # Exercise both real entry points before any game paths/helper/install code.
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    $pairDirectory=Join-Path $OutputDirectory 'actual-entrypoints'
    $pair=@()
    foreach ($spec in @(@{role='hang';file='Watch-Controller-Hang.ps1'},@{role='session';file='Local-Controller-Test.ps1'})) {
        $child=Spawn (Join-Path $root ('artifacts\issue-1-controller-startup\' + $spec.file)) @('-OutputDirectory',$pairDirectory,'-RunId',$runId.ToString('N'),'-HealthProbeSeconds','8') $spec.role
        $pair+=@{role=$spec.role;process=$child}
    }
    foreach ($spec in $pair) { Wait-Healthy $pairDirectory $spec.role $runId | Out-Null }
    $beforePair=@{}
    foreach ($spec in $pair) { $beforePair[$spec.role]=Get-CollectorHealth $pairDirectory $spec.role $runId }
    Start-Sleep -Milliseconds 1500
    foreach ($spec in $pair) {
        $health=Get-CollectorHealth $pairDirectory $spec.role $runId
        Check ($health.healthy -and $health.record.sequence -gt $beforePair[$spec.role].record.sequence -and -not $health.readyForGame) ($spec.role + ' real entry point advances in offline-only mode')
    }
    foreach ($spec in $pair) {
        Check ($spec.process.WaitForExit(12000) -and $spec.process.ExitCode -eq 0) ($spec.role + ' offline entry point exits normally')
        Check ((Get-CollectorHealth $pairDirectory $spec.role $runId).reason -eq 'terminal-probe-complete') ($spec.role + ' offline completion cannot be mistaken for armed')
    }
    Check (-not (Test-Path -LiteralPath (Join-Path $pairDirectory 'runtime-install-receipt.json'))) 'Offline entry points never prepare a runtime installation'
    Check (-not (Test-Path -LiteralPath (Join-Path $pairDirectory 'hang-events.jsonl'))) 'Offline entry points never arm dump capture'

    $blockedDirectory=Join-Path $OutputDirectory 'deny-breakaway'
    $blocked=Spawn (Join-Path $PSScriptRoot 'Parent.ps1') @('-OutputDirectory',$blockedDirectory,'-DenyBreakaway') 'deny-parent'
    Check ($blocked.WaitForExit(10000) -and $blocked.ExitCode -eq 0) 'Restrictive disposable job fixture completes'
    $denied=Get-Content -LiteralPath (Join-Path $blockedDirectory 'parent.json') -Raw | ConvertFrom-Json
    Check ([bool]$denied.blocked) 'Independent launch honors job restriction without fallback'

    # Actual host launch may be denied by an ancestor job. Record it, never use an
    # alternate launch mechanism to bypass containment or call this a survival pass.
    $detached=$null; $lifetime='not-tested'; $lifetimeMessage=$null
    try {
        $lifetimeDirectory=Join-Path $OutputDirectory 'parent-exit'
        $args=@('-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',(Join-Path $PSScriptRoot 'Parent.ps1'),'-OutputDirectory',$lifetimeDirectory)
        $detached=[ReactorV.Diagnostics.ProcessLifetime]::StartDetached($shell,$args,$PSScriptRoot)
        Check ($detached.WaitForExit(10000) -and $detached.ExitCode -eq 0) 'Independent disposable parent exits'
        $parent=Get-Content -LiteralPath (Join-Path $lifetimeDirectory 'parent.json') -Raw | ConvertFrom-Json
        Check (-not $parent.independentInJob -and $parent.ordinaryInJob) 'Fixture records intended lifetime ownership'
        $survivor=Confirm-CollectorProgress (Join-Path $lifetimeDirectory 'independent') 'fixture' ([Guid]$parent.runId)
        Check ($survivor.healthy -and $survivor.record.processStartTicks -eq $parent.independentStartTicks) 'Independent child advances after parent/job close'
        $ordinary=$null
        try { $ordinary=[Diagnostics.Process]::GetProcessById($parent.ordinaryPid) } catch [ArgumentException] { }
        try { Check ((-not $ordinary) -or $ordinary.StartTime.ToUniversalTime().Ticks -ne $parent.ordinaryStartTicks) 'Ordinary fixture child is gone after owning job closes' }
        finally { if ($ordinary) { $ordinary.Dispose() } }
        [IO.File]::WriteAllText((Join-Path $lifetimeDirectory ('independent\fixture-stop-' + $parent.runId + '.request')), 'Stop own surviving fixture.')
        $lifetime='passed-parent-exit'
    } catch {
        if ($_.Exception.ToString() -match 'Child is still job-bound|parent job does not permit|Independent collector launch refused') {
            $lifetime='blocked-by-host-job'; $lifetimeMessage=$_.Exception.Message
        } else { throw }
    } finally { if ($detached) { $detached.Dispose() } }
    foreach ($child in $owned) { Check ($child.WaitForExit(12000)) ('Own fixture exited: ' + $child.Id) }
    $report=[ordered]@{schema=1;utc=[DateTime]::UtcNow.ToString('o');passed=$checks.Count;checks=$checks.ToArray();
        lifetimeQualification=$lifetime;lifetimeMessage=$lifetimeMessage;actualTurnHandoffQualified=$false;
        noGameLaunch=$true;noInstallationChanges=$true;noUploads=$true;output=$OutputDirectory}
    Write-CollectorJson (Join-Path $OutputDirectory 'test-result.json') $report
    $report | ConvertTo-Json -Depth 6
} finally { foreach ($child in $owned) { $child.Dispose() } }
