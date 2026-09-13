# Controller-side ownership, health and closeout. Dot-source after CollectorHealth
# and GameplayHang.Common. No game launch, installation or forced termination.
function Read-GameplayJson([string]$Path) {
    # Heartbeats are atomic File.Replace snapshots. ReadAllText's sharing mode
    # races that writer; allow replacement while retaining our opened snapshot.
    # A transient sharing/byte-lock conflict may retry for at most 250 ms. Never
    # cache health, refresh timestamps, or retry malformed/oversized records.
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $stream=$null; $reader=$null; $memory=$null
        try {
            if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) { throw 'Invalid observer JSON file: reparse point.' }
            $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            if ($stream.Length -gt 64KB) { throw 'Invalid observer JSON file: oversized.' }
            # Bound the actual read too, even if another writer grows the file
            # after Length was sampled. Do not reopen the path after validation.
            $bytes=[byte[]]::new(65537); $count=0
            while ($count -lt $bytes.Length) {
                $read=$stream.Read($bytes,$count,$bytes.Length-$count)
                if ($read -eq 0) { break }
                $count+=$read
            }
            if ($count -gt 64KB) { throw 'Invalid observer JSON file: oversized.' }
            $memory=[IO.MemoryStream]::new($bytes,0,$count,$false)
            $reader=[IO.StreamReader]::new($memory,[Text.UTF8Encoding]::new($false,$true),$true)
            $json=$reader.ReadToEnd()
            if ([string]::IsNullOrWhiteSpace($json)) { throw 'Invalid observer JSON file: empty.' }
            return ($json | ConvertFrom-Json -ErrorAction Stop)
        } catch {
            $cause=$_.Exception
            while ($cause.InnerException) { $cause=$cause.InnerException }
            if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { return $null }
            $code=$cause.HResult -band 0xffff
            if ($cause -isnot [IO.IOException] -or $code -notin @(32,33) -or $clock.ElapsedMilliseconds -ge 250) { throw }
        } finally {
            if ($reader) { $reader.Dispose() }
            if ($memory) { $memory.Dispose() }
            if ($stream) { $stream.Dispose() }
        }
        Start-Sleep -Milliseconds ([Math]::Min(25,[Math]::Max(1,250-$clock.ElapsedMilliseconds)))
    }
}
function Assert-GameplayObserverRecord($Record,$Expected,[bool]$Terminal,[DateTime]$Now) {
    if (-not $Record -or $Record.schema -ne 1 -or $Record.observerPid -ne $Expected.observerPid -or
        $Record.observerStartUtcTicks -ne $Expected.observerStartUtcTicks -or $Record.targetPid -ne $Expected.targetPid -or
        $Record.targetStartUtcTicks -ne $Expected.targetStartUtcTicks -or $Record.targetPath -ine $Expected.targetPath -or
        $Record.rehearsal -isnot [bool] -or $Record.rehearsal -ne $Expected.rehearsal -or $Record.monitorArmed -isnot [bool]) { throw 'Observer record identity/schema mismatch.' }
    if ($Terminal) {
        if ($Record.state -notin @('capture-complete','target-exited-no-capture','cancelled','deadline-no-capture','failed','needs-attention')) { throw 'Unknown observer result state.' }
        if ($Record.gameLaunched -ne $false -or $Record.installationChanged -ne $false -or $Record.uploads -ne $false) { throw 'Observer result scope mismatch.' }
        if ($Record.state -eq 'target-exited-no-capture' -and $Record.targetStillAlive -ne $false) { throw 'Observer target-exit result contradicts target state.' }
        if ($Record.state -eq 'capture-complete' -and (-not $Record.monitorArmed -or -not $Record.capture -or $Record.capture.pid -ne $Expected.targetPid)) { throw 'Observer capture result lacks arming/target evidence.' }
        if ($Record.state -ne 'capture-complete' -and $Record.capture) { throw 'Observer non-capture result contains capture evidence.' }
        if ($Record.monitorArmed -and ($Record.monitorPid -le 0 -or $Record.monitorExitCode -notin @(0,1))) { throw 'Observer result lacks a completed monitor identity.' }
    } else {
        if ($Record.state -notin @('waiting-for-story-ready','monitor-starting','monitoring','capturing','capture-complete','target-exited-no-capture','cancelled','deadline-no-capture','failed','needs-attention') -or
            $Record.sequence -lt 1 -or $Record.sequence -lt $Expected.lastSequence -or $Record.maximumDumps -ne 1) { throw 'Observer heartbeat state/sequence mismatch.' }
        $age=($Now-[DateTime]::Parse($Record.updatedUtc).ToUniversalTime()).TotalSeconds
        if ($age -lt -2 -or $age -gt 5) { throw 'Observer heartbeat is stale or future-dated.' }
        if ($Record.state -in @('monitoring','capturing') -and (-not $Record.monitorArmed -or $Record.monitorPid -le 0)) { throw 'Observer heartbeat claims monitoring without acknowledgement.' }
    }
}
function Start-GameplayObserver([Diagnostics.Process]$Target,[string]$Directory,[string]$Watcher,[string]$FixtureRoot='', [string]$FixtureLog='') {
    Assert-GameplayCollectorAvailable
    $Directory=Assert-CollectorOutput $Directory
    if (Test-Path -LiteralPath $Directory) { throw 'Observer output has already been used.' }
    $expected=@{targetPid=$Target.Id;targetStartUtcTicks=$Target.StartTime.ToUniversalTime().Ticks;targetPath=$Target.MainModule.FileName;rehearsal=[bool]$FixtureRoot;lastSequence=0L}
    $shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "'+$Watcher+'" -OutputDirectory "'+$Directory+'" -TargetProcessId '+$Target.Id+' -TargetStartUtcTicks '+$expected.targetStartUtcTicks
    if ($FixtureRoot) {
        $fixtureExe=Join-Path $FixtureRoot 'fixture-payload\ProviderFixture.exe'
        if ($expected.targetPath -ine $fixtureExe) { throw 'Controller rehearsal target is not its prepared ProviderFixture.' }
        $arguments+=' -Rehearsal -FixtureDirectory "'+$FixtureRoot+'" -FixtureLog "'+$FixtureLog+'" -FixtureSha256 '+(Get-FileHash -LiteralPath $fixtureExe).Hash+' -FixtureRelativeExecutable "fixture-payload\ProviderFixture.exe"'
    }
    $process=Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput ($Directory+'-stdout.txt') -RedirectStandardError ($Directory+'-stderr.txt')
    [void]$process.Handle
    $expected.observerPid=$process.Id; $expected.observerStartUtcTicks=$process.StartTime.ToUniversalTime().Ticks
    Write-CollectorJson ($Directory+'-owner.json') $expected
    return @{process=$process;expected=$expected;directory=$Directory;clock=[Diagnostics.Stopwatch]::StartNew();armed=$false;lastState='starting';failure=$null;result=$null;stopRequested=$false}
}
function Update-GameplayObserver($Session) {
    try {
        $process=$Session.process
        if ($process.HasExited) {
            $process.WaitForExit()
            if (-not $Session.result) {
                $result=Read-GameplayJson (Join-Path $Session.directory 'result.json')
                Assert-GameplayObserverRecord $result $Session.expected $true ([DateTime]::UtcNow)
                if ($process.ExitCode -ne 0 -or $result.failure -or $result.state -in @('failed','needs-attention')) { throw ('Observer failed: '+$result.failure) }
                if ($result.state -eq 'capture-complete') {
                    $dump=Read-GameplayDump (Join-Path $Session.directory 'gameplay-hang.dmp') $Session.expected.targetPid
                    if (-not $dump -or $dump.sha256 -cne $result.capture.sha256 -or $result.monitorExitCode -notin @(0,1)) { throw 'Observer capture verification mismatch.' }
                }
                $Session.result=$result; $Session.armed=$Session.armed -or $result.monitorArmed; $Session.lastState=$result.state
            }
        } else {
            $record=Read-GameplayJson (Join-Path $Session.directory 'status.json')
            if (-not $record -and $Session.clock.Elapsed.TotalSeconds -le 15) { return }
            Assert-GameplayObserverRecord $record $Session.expected $false ([DateTime]::UtcNow)
            $Session.expected.lastSequence=[long]$record.sequence; $Session.armed=$Session.armed -or $record.monitorArmed; $Session.lastState=$record.state
        }
    } catch { if (-not $Session.failure) { $Session.failure=$_.Exception.Message } }
}
function Stop-GameplayObserver($Session) {
    if (-not $Session) { return [pscustomobject]@{safeToRestore=$false;qualified=$false;failure='Observer was not started.';result=$null} }
    try {
        if (-not $Session.process.HasExited) {
            # The child's fresh output may not exist yet during early startup.
            $wait=[Diagnostics.Stopwatch]::StartNew()
            while (-not [IO.Directory]::Exists($Session.directory) -and -not $Session.process.HasExited -and $wait.Elapsed.TotalSeconds -lt 3) { Start-Sleep -Milliseconds 100 }
            if ([IO.Directory]::Exists($Session.directory)) {
                [IO.File]::WriteAllText((Join-Path $Session.directory 'stop.request'),'controller closeout')
                $Session.stopRequested=$true
            }
            if (-not $Session.process.WaitForExit(12000)) { throw 'Observer did not stop; no process force-killed.' }
        }
        Update-GameplayObserver $Session
    } catch { if (-not $Session.failure) { $Session.failure=$_.Exception.Message } }
    $safe=$Session.process.HasExited -and -not (Get-GameplayCollectors)
    $qualified=$safe -and -not $Session.failure -and $Session.armed -and $Session.result -and $Session.result.state -in @('target-exited-no-capture','capture-complete')
    [pscustomobject]@{safeToRestore=[bool]$safe;qualified=[bool]$qualified;failure=$Session.failure;everArmed=$Session.armed;state=$Session.lastState;observerPid=$Session.expected.observerPid;targetPid=$Session.expected.targetPid;targetStartUtcTicks=$Session.expected.targetStartUtcTicks;stopRequested=$Session.stopRequested;result=$Session.result}
}
function Assert-GameplayObserverStopped([string]$Directory) {
    if (Get-GameplayCollectors) { throw 'A ProcDump collector remains active; restoration refused.' }
    foreach($record in @((Read-GameplayJson ($Directory+'-owner.json')),(Read-GameplayJson (Join-Path $Directory 'status.json')))) {
      if ($record -and $record.observerPid -gt 0 -and $record.observerStartUtcTicks -gt 0) {
        $process=Get-Process -Id $record.observerPid -ErrorAction SilentlyContinue
        if ($process) {
            try { if (-not $process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq $record.observerStartUtcTicks) { throw 'The gameplay observer is still running; restoration refused.' } }
            finally { $process.Dispose() }
        }
      }
    }
}
