param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'GameplayHang.Common.ps1')
. (Join-Path $PSScriptRoot 'GameplayHang.Session.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh session-test output directory.' }
if (Get-GameplayCollectors) { throw 'No concurrent collector during session tests.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$checks=[Collections.Generic.List[string]]::new()
function Check([string]$Name,[bool]$Condition) { if (-not $Condition) { throw ('FAIL: '+$Name) }; $checks.Add($Name) }
Add-Type -Path (Join-Path $PSScriptRoot 'GameplayJsonFixture.cs')
$sourcePaths=@($PSCommandPath,(Join-Path $PSScriptRoot 'GameplayJsonFixture.cs'),(Join-Path $PSScriptRoot 'GameplayHang.Session.ps1'))
$sourceHashes=@($sourcePaths | ForEach-Object { [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()} })
$jsonPath=Join-Path $OutputDirectory 'reader.json'
Write-CollectorJson $jsonPath @{sequence=1;mirror=1}
$fixture=[GameplayJsonFixture]::new($jsonPath,$false,5000)
try {
    $oldReaderFailed=$false
    try { [void][IO.File]::ReadAllText($jsonPath) } catch { $oldReaderFailed=$true }
    Check 'old ReadAllText reproduces sharing failure against a compatible writer' $oldReaderFailed
    Check 'snapshot reader coexists with a compatible writer' ((Read-GameplayJson $jsonPath).sequence -eq 1)
} finally { $fixture.Dispose() }

$fixture=[GameplayJsonFixture]::new($jsonPath,$true,75)
try {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    Check 'brief exclusive sharing conflict recovers within its read budget' ((Read-GameplayJson $jsonPath).sequence -eq 1 -and $clock.ElapsedMilliseconds -lt 1500)
} finally { $fixture.Dispose() }
$fixture=[GameplayJsonFixture]::new($jsonPath,$true,5000)
try {
    $clock=[Diagnostics.Stopwatch]::StartNew(); $rejected=$false
    try { [void](Read-GameplayJson $jsonPath) } catch { $rejected=$true }
    Check 'persistent sharing conflict fails closed with bounded retry' ($rejected -and $clock.ElapsedMilliseconds -ge 200 -and $clock.ElapsedMilliseconds -lt 1500)
} finally { $fixture.Dispose() }

foreach($duration in @(75,5000)) {
    $fixture=[GameplayJsonFixture]::new($jsonPath,$false,$duration,$true)
    try {
        $clock=[Diagnostics.Stopwatch]::StartNew(); $record=$null; $rejected=$false
        try { $record=Read-GameplayJson $jsonPath } catch { $rejected=$true }
        if ($duration -eq 75) { Check 'brief byte-range lock recovers' (-not $rejected -and $record.sequence -eq 1) }
        else { Check 'persistent byte-range lock fails within its budget' ($rejected -and $clock.ElapsedMilliseconds -ge 200 -and $clock.ElapsedMilliseconds -lt 1500) }
    } finally { $fixture.Dispose() }
}
Write-CollectorJson $jsonPath @{sequence=0;mirror=0}
$fixture=[GameplayJsonFixture]::new($jsonPath,1000)
try {
    $lastSequence=0; $observed=[Collections.Generic.HashSet[int]]::new()
    for($i=0;$i -lt 600;$i++) {
        $snapshot=Read-GameplayJson $jsonPath
        if ($null -eq $snapshot -or $snapshot.sequence -ne $snapshot.mirror -or $snapshot.sequence -lt $lastSequence) { throw 'Torn/missing/regressing concurrent snapshot.' }
        $lastSequence=$snapshot.sequence; [void]$observed.Add([int]$lastSequence)
        Start-Sleep -Milliseconds 1
    }
    Check 'atomic replacements remain readable as complete monotonic snapshots' ($observed.Count -ge 20)
} finally { $fixture.Dispose() }
Check 'snapshot reader never blocks the atomic replacement writer' ($null -eq $fixture.Failure -and $fixture.Writes -ge 20)
Check 'missing snapshot is unavailable, not cached healthy data' ($null -eq (Read-GameplayJson (Join-Path $OutputDirectory 'missing.json')))

foreach($invalid in @('{broken', (' ' * 65537), '')) {
    [IO.File]::WriteAllText($jsonPath,$invalid)
    $rejected=$false; try { [void](Read-GameplayJson $jsonPath) } catch { $rejected=$true }
    Check ('invalid snapshot fails closed: length='+$invalid.Length) $rejected
}
[IO.File]::WriteAllText($jsonPath,('{"sequence":1}' + (' ' * (65536-14))))
Check 'exact 64 KiB valid snapshot is accepted' ((Read-GameplayJson $jsonPath).sequence -eq 1)
[IO.File]::WriteAllBytes($jsonPath,[byte[]]@(0x7b,0x22,0xff,0x22,0x3a,0x31,0x7d))
$rejected=$false; try { [void](Read-GameplayJson $jsonPath) } catch { $rejected=$true }
Check 'invalid UTF-8 fails rather than silently replacing evidence bytes' $rejected
[IO.File]::WriteAllText($jsonPath,'{"sequence":1}',[Text.UTF8Encoding]::new($true))
Check 'BOM-encoded JSON remains readable' ((Read-GameplayJson $jsonPath).sequence -eq 1)
$junctionTarget=Join-Path $OutputDirectory 'owned-junction-target'
[void][IO.Directory]::CreateDirectory($junctionTarget)
$junctionPath=Join-Path $OutputDirectory 'reparse.json'
[void](New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget)
$rejected=$false; try { [void](Read-GameplayJson $junctionPath) } catch { $rejected=$true }
Check 'reparse-point record path rejected' $rejected
$now=[DateTime]::UtcNow
$expected=@{observerPid=99;observerStartUtcTicks=111;targetPid=123;targetStartUtcTicks=222;targetPath='C:\fixture.exe';rehearsal=$true;lastSequence=2L}
$baseline=@{schema=1;observerPid=99;observerStartUtcTicks=111;targetPid=123;targetStartUtcTicks=222;targetPath='c:\fixture.exe';rehearsal=$true;monitorArmed=$true;monitorPid=88;monitorExitCode=0;state='monitoring';sequence=3L;maximumDumps=1;updatedUtc=$now.ToString('o');targetStillAlive=$false;gameLaunched=$false;installationChanged=$false;uploads=$false}
function Accept($Record,[bool]$Terminal=$false) {
    try { Assert-GameplayObserverRecord ([pscustomobject]$Record) $expected $Terminal $now; return $true } catch { return $false }
}
Check 'exact live heartbeat accepted' (Accept $baseline)
$stale=$baseline.Clone(); $stale.updatedUtc=$now.AddSeconds(-6).ToString('o')
Write-CollectorJson $jsonPath $stale
$fixture=[GameplayJsonFixture]::new($jsonPath,$true,75)
try { Check 'recovered file access does not refresh a stale heartbeat' (-not (Accept (Read-GameplayJson $jsonPath))) }
finally { $fixture.Dispose() }
foreach ($mutation in @(
    @('schema',2),@('observerPid',100),@('observerStartUtcTicks',112),@('targetPid',124),@('targetStartUtcTicks',223),
    @('targetPath','C:\other.exe'),@('rehearsal',$false),@('rehearsal','True'),@('monitorArmed','True'),
    @('state','unknown'),@('sequence',0),@('sequence',1),@('maximumDumps',2),
    @('updatedUtc',$now.AddSeconds(-6).ToString('o')),@('updatedUtc',$now.AddSeconds(3).ToString('o')),@('updatedUtc','invalid'),@('monitorArmed',$false),@('monitorPid',0)
)) {
    $copy=$baseline.Clone(); $copy[$mutation[0]]=$mutation[1]
    Check ('heartbeat rejected: '+$mutation[0]+'='+$mutation[1]) (-not (Accept $copy))
}
$terminal=$baseline.Clone(); $terminal.state='target-exited-no-capture'; $terminal.updatedUtc=$now.AddDays(-1).ToString('o')
Check 'retained exited-child result does not require a live heartbeat' (Accept $terminal $true)
foreach($field in @('gameLaunched','installationChanged','uploads')) { $copy=$terminal.Clone(); $copy[$field]=$true; Check ('scope violation rejected: '+$field) (-not (Accept $copy $true)) }
$copy=$terminal.Clone(); $copy.state='monitoring'; Check 'nonterminal result rejected' (-not (Accept $copy $true))
Check 'missing record rejected' (-not (Accept $null))
foreach($mutation in @(@('targetStillAlive',$true),@('monitorPid',0),@('monitorExitCode',$null),@('monitorExitCode',7),@('capture',@{pid=123}))) {
    $copy=$terminal.Clone(); $copy[$mutation[0]]=$mutation[1]
    Check ('inconsistent terminal evidence rejected: '+$mutation[0]) (-not (Accept $copy $true))
}
$copy=$terminal.Clone(); $copy.state='capture-complete'
Check 'completed capture requires actual capture evidence' (-not (Accept $copy $true))
$copy.capture=@{pid=123}; Check 'capture with matching identity accepted for subsequent file verification' (Accept $copy $true)
$copy.capture=@{pid=124}; Check 'capture from another target rejected' (-not (Accept $copy $true))
$copy.capture=@{pid=123}; $copy.monitorArmed=$false; Check 'capture without acknowledgement rejected' (-not (Accept $copy $true))

# A real owned child exits without a result: never infer collector success merely
# from a PID or allow it to satisfy the comparison's evidence standard.
$child=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList '-NoProfile -NonInteractive -Command "exit 7"' -WindowStyle Hidden -PassThru
[void]$child.Handle
try {
    $session=@{process=$child;expected=$expected;directory=(Join-Path $OutputDirectory 'missing-result');clock=[Diagnostics.Stopwatch]::StartNew();armed=$false;lastState='starting';failure=$null;result=$null;stopRequested=$false}
    Check 'owned failing child exits naturally' ($child.WaitForExit(5000) -and $child.ExitCode -eq 7)
    Update-GameplayObserver $session
    Check 'missing child result fails closed' ([bool]$session.failure)
    $firstFailure=$session.failure
    $session.directory=Join-Path $OutputDirectory 'recovered-record'
    [void][IO.Directory]::CreateDirectory($session.directory)
    Write-CollectorJson (Join-Path $session.directory 'result.json') $terminal
    Update-GameplayObserver $session
    Check 'later readable evidence cannot clear an existing observer failure' ($session.failure -ceq $firstFailure)
    $closeout=Stop-GameplayObserver $session
    Check 'failed observation is not qualified' (-not $closeout.qualified)
    Check 'dead observer without collectors does not obstruct safe restore' $closeout.safeToRestore
} finally { $child.Dispose() }

$directory=Join-Path $OutputDirectory 'restore-guard'
$self=[Diagnostics.Process]::GetCurrentProcess()
try {
    $owner=@{observerPid=$self.Id;observerStartUtcTicks=$self.StartTime.ToUniversalTime().Ticks}
    Write-CollectorJson ($directory+'-owner.json') $owner
    $rejected=$false; try { Assert-GameplayObserverStopped $directory } catch { $rejected=$true }
    Check 'retained owner blocks restore even before first child heartbeat' $rejected
    $owner.observerStartUtcTicks++
    Write-CollectorJson ($directory+'-owner.json') $owner
    $accepted=$true; try { Assert-GameplayObserverStopped $directory } catch { $accepted=$false }
    Check 'PID reuse does not target an unrelated process' $accepted
} finally { $self.Dispose() }
foreach($source in $sourceHashes) { Check ('qualified source unchanged: '+[IO.Path]::GetFileName($source.path)) ((Get-FileHash -LiteralPath $source.path).Hash -ieq $source.sha256) }
Write-CollectorJson (Join-Path $OutputDirectory 'result.json') ([ordered]@{passed=$true;checks=$checks.ToArray();sessionSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'GameplayHang.Session.ps1')).Hash.ToLowerInvariant();sources=$sourceHashes;gameLaunched=$false;installationChanged=$false})
Write-Output ('PASS: '+$checks.Count+' observer session checks.')
