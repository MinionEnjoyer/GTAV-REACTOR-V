param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$TargetProcessId,
    [Parameter(Mandatory=$true)][long]$TargetStartUtcTicks,
    [ValidateRange(5,1800)][int]$MaximumSeconds=1800,
    [switch]$Rehearsal,
    [string]$FixtureDirectory,
    [string]$FixtureSha256,
    [string]$FixtureLog,
    [ValidateSet('GameplayHangFixture.exe','fixture-payload\ProviderFixture.exe')][string]$FixtureRelativeExecutable='GameplayHangFixture.exe'
)
# One exact, already-running process. No game launch, injection, postmortem
# debugger registration, settings changes, upload, full-memory dump or force kill.
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) { throw 'Use x64 Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'GameplayHang.Common.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (-not $OutputDirectory.StartsWith((Get-CollectorDataRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use the shared local diagnostics root.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh watcher output directory.' }
$expectedExe='D:\Programs\Steam\steamapps\common\Grand Theft Auto V Enhanced\GTA5_Enhanced.exe'
$expectedHash='0c52864d4521d9c9d441348aa1156958792dde8825d0297c851753f167336401'
$logRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ReactorV'
if ($Rehearsal) {
    $FixtureDirectory=Assert-CollectorOutput $FixtureDirectory
    $FixtureLog=[IO.Path]::GetFullPath($FixtureLog)
    if (-not $FixtureLog.StartsWith($FixtureDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or $FixtureSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid owned-fixture paths/hash.' }
    $expectedExe=Join-Path $FixtureDirectory $FixtureRelativeExecutable; $expectedHash=$FixtureSha256
} elseif ($FixtureDirectory -or $FixtureSha256 -or $FixtureLog -or $PSBoundParameters.ContainsKey('FixtureRelativeExecutable')) { throw 'Fixture parameters are rehearsal-only.' }
$collector=Get-CollectorToolPath
$collectorHash='d1fc99ae304bd1d2bf28abeb62531da959e2431916194981b88c958fd713a8e6'
Assert-GameplayCollectorAvailable
$self=[Diagnostics.Process]::GetCurrentProcess()
try { $observerStartTicks=$self.StartTime.ToUniversalTime().Ticks } finally { $self.Dispose() }
$target=Get-Process -Id $TargetProcessId -ErrorAction Stop
[void]$target.Handle
$monitor=$null; $ownsMutex=$false; $writer=$null; $state='starting'; $failure=$null; $capture=$null; $readyLog=$null; $monitorArmed=$false; $sequence=0; $cancelRequested=$false
$clock=[Diagnostics.Stopwatch]::StartNew()
$mutex=[Threading.Mutex]::new($false,('Local\ReactorV.GameplayHang.'+$TargetProcessId+'.'+$TargetStartUtcTicks))
function Assert-TargetIdentity {
    if ($target.HasExited) { throw 'Target has exited.' }
    if (-not (Test-GameplayIdentity $expectedExe $TargetStartUtcTicks $target.MainModule.FileName $target.StartTime.ToUniversalTime().Ticks)) { throw 'Target path/start identity mismatch.' }
}
function Event([string]$Name,$Data) {
    $writer.WriteLine(([ordered]@{utc=[DateTime]::UtcNow.ToString('o');event=$Name;data=$Data}|ConvertTo-Json -Depth 6 -Compress))
}
function Status {
    $script:sequence++
    Write-CollectorJson (Join-Path $OutputDirectory 'status.json') ([ordered]@{schema=1;observerPid=$PID;observerStartUtcTicks=$observerStartTicks;sequence=$sequence;updatedUtc=[DateTime]::UtcNow.ToString('o');state=$state;targetPid=$TargetProcessId;targetStartUtcTicks=$TargetStartUtcTicks;targetPath=$expectedExe;elapsedSeconds=$clock.Elapsed.TotalSeconds;readyLog=$readyLog;monitorPid=$(if($monitor){$monitor.Id}else{$null});monitorArmed=$monitorArmed;maximumDumps=1;rehearsal=[bool]$Rehearsal})
}
function Cancel-OwnedMonitor {
    if (-not $monitor -or $monitor.HasExited) { return }
    # -cancel affects every ProcDump for the target. Refuse ambiguous ownership.
    if (@(Get-GameplayCollectors | Where-Object Id -ne $monitor.Id).Count) { throw 'Concurrent ProcDump detected; no broad cancellation attempted.' }
    if ($target.HasExited) {
        if (-not $monitor.WaitForExit(5000)) { throw 'Collector remained after target exit; needs attention.' }
        return
    }
    Assert-TargetIdentity
    Event 'monitor_cancel_requested' @{monitorPid=$monitor.Id;targetPid=$TargetProcessId;method='documented-target-cancel'}
    $script:cancelRequested=$true
    $cancel=Start-Process -FilePath $collector -ArgumentList ('-cancel '+$TargetProcessId) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory 'cancel-stdout.txt') -RedirectStandardError (Join-Path $OutputDirectory 'cancel-stderr.txt')
    try { if (-not $cancel.WaitForExit(5000)) { throw 'Graceful cancellation helper did not finish; needs attention.' } }
    finally { $cancel.Dispose() }
    if (-not $monitor.WaitForExit(5000)) { throw 'Collector did not stop after cancellation; no process was force-killed.' }
}
try {
    Assert-TargetIdentity
    if ((Get-FileHash -LiteralPath $expectedExe).Hash -ine $expectedHash) { throw 'Target executable hash mismatch.' }
    try { $ownsMutex=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex=$true }
    if (-not $ownsMutex) { throw 'Watcher already owns this target session.' }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    $writer=[IO.StreamWriter]::new([IO.File]::Open((Join-Path $OutputDirectory 'events.jsonl'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read),[Text.UTF8Encoding]::new($false)); $writer.AutoFlush=$true
    Event 'identity_verified' @{targetPid=$TargetProcessId;startUtcTicks=$TargetStartUtcTicks;exe=$expectedExe;sha256=$expectedHash;collectorSha256=$collectorHash;maximumSeconds=$MaximumSeconds;maximumDumps=1;localOnly=$true}
    $state='waiting-for-story-ready'; Status
    while ($clock.Elapsed.TotalSeconds -lt $MaximumSeconds) {
        if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'stop.request')) { $state='cancelled'; break }
        if ($target.HasExited) { $state='target-exited-no-capture'; break }
        Assert-TargetIdentity
        if (-not (Test-GameplaySpace (Get-PSDrive C).Free)) { throw 'Free disk space below 2 GiB; stopping capture.' }
        if (-not $monitor) {
            $paths=if($Rehearsal){@($FixtureLog)}else{@(Get-ChildItem -LiteralPath $logRoot -Filter ('reactorv-session-*-'+$TargetProcessId+'.log') -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)}
            $readyPaths=@(foreach($path in $paths) {
                $text=Read-GameplayTail $path
                foreach($line in $text -split '[\r\n]+') {
                    if (Test-GameplayReadyLine $line $TargetProcessId $target.StartTime.ToUniversalTime()) { $path; break }
                }
            })
            if ($readyPaths.Count -gt 1) { throw 'Ambiguous game session logs; refusing capture.' }
            if ($readyPaths.Count -eq 1) {
                $readyLog=$readyPaths[0]
                Assert-TargetIdentity
                if (Get-GameplayCollectors) { throw 'ProcDump appeared before arming; refusing overlap.' }
                $dump=Join-Path $OutputDirectory 'gameplay-hang.dmp'
                $stdout=Join-Path $OutputDirectory 'procdump-stdout.txt'; $stderr=Join-Path $OutputDirectory 'procdump-stderr.txt'
                $arguments='-r 1 -a -at 5 -mc 1824 -n 1 -s 5 -h '+$TargetProcessId+' "'+$dump+'"'
                $monitor=Start-Process -FilePath $collector -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                [void]$monitor.Handle
                $monitorStartedAt=$clock.Elapsed.TotalSeconds; $state='monitor-starting'
                Event 'monitor_started' @{monitorPid=$monitor.Id;targetPid=$TargetProcessId;arguments=$arguments;readyLog=$readyLog;privacy='local-only; stack dumps may contain sensitive data'}
            }
        } else {
            $monitorText=Read-GameplayTail $stdout
            if (-not $monitorArmed -and $monitorText.Contains('Press Ctrl-C to end monitoring without terminating the process.')) {
                $monitorArmed=$true; $state='monitoring'; Event 'monitor_armed' @{monitorPid=$monitor.Id;trigger='procdump-hung-window';startupGate='exact-session-story-ready'}
            }
            if (-not $monitorArmed -and -not $monitor.HasExited -and $clock.Elapsed.TotalSeconds-$monitorStartedAt -gt 15) { throw 'ProcDump arming acknowledgement deadline.' }
            if (Test-Path -LiteralPath $dump) {
                $state='capturing'
                if ((Get-Item -LiteralPath $dump).Length -gt 512MB) { throw 'Dump exceeded 512 MiB; cancellation requested, file retained.' }
            }
            if ($monitor.HasExited) {
                $monitor.WaitForExit()
                $capture=Read-GameplayDump $dump $TargetProcessId
                if ($capture -and $monitor.ExitCode -in @(0,1) -and (Read-GameplayTail $stdout) -match 'Dump 1 complete:') {
                    $state='capture-complete'; Event 'capture_verified' $capture; break
                }
                if (-not $capture -and $target.HasExited) { $state='target-exited-no-capture'; break }
                throw ('Collector exited without a verified completed dump (exit '+$monitor.ExitCode+').')
            }
        }
        Status
        Start-Sleep -Milliseconds 500
    }
    if ($state -notin @('capture-complete','target-exited-no-capture','cancelled')) { $state='deadline-no-capture' }
} catch {
    $failure=$_.Exception.Message; $state='failed'
} finally {
    try { Cancel-OwnedMonitor } catch { $failure=$_.Exception.Message; $state='needs-attention' }
    # Process exit, stop request or deadline can race successful collection. Do
    # not label an already completed dump "no capture" just because that branch
    # won the polling race. Partial files remain local but never qualify.
    if (-not $failure -and -not $capture -and $monitor -and $monitor.HasExited -and (Test-Path -LiteralPath $dump)) {
        try {
            $monitor.WaitForExit()
            if ($monitor.ExitCode -in @(0,1) -and (Read-GameplayTail $stdout) -match 'Dump 1 complete:') {
                $capture=Read-GameplayDump $dump $TargetProcessId
                $state='capture-complete'; Event 'capture_verified_after_stop_or_exit' $capture
            } elseif ($state -eq 'target-exited-no-capture') { throw 'Target exited with an incomplete dump; file retained, not qualified.' }
        } catch { $failure=$_.Exception.Message; $state='failed' }
    }
    if ($writer) {
        Status
        Write-CollectorJson (Join-Path $OutputDirectory 'result.json') ([ordered]@{schema=1;state=$state;failure=$failure;observerPid=$PID;observerStartUtcTicks=$observerStartTicks;rehearsal=[bool]$Rehearsal;targetPid=$TargetProcessId;targetStartUtcTicks=$TargetStartUtcTicks;targetPath=$expectedExe;targetExitCode=$(if($target.HasExited){$target.ExitCode}else{$null});targetStillAlive=(-not $target.HasExited);monitorPid=$(if($monitor){$monitor.Id}else{$null});monitorArmed=$monitorArmed;monitorExitCode=$(if($monitor -and $monitor.HasExited){$monitor.ExitCode}else{$null});cancelRequested=$cancelRequested;elapsedSeconds=$clock.Elapsed.TotalSeconds;capture=$capture;gameLaunched=$false;installationChanged=$false;uploads=$false})
        $writer.Dispose()
    }
    if ($monitor) { $monitor.Dispose() }; $target.Dispose()
    if ($ownsMutex) { $mutex.ReleaseMutex() }; $mutex.Dispose()
}
if ($failure) { throw $failure }
Write-Output ('Gameplay hang watcher finished: '+$state+'. No installation changed.')
