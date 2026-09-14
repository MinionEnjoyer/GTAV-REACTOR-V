param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) { throw 'Use x64 Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'GameplayHang.Common.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh test output directory.' }
if ((Get-GameplayCollectors) -or (Get-Process -Name GTA5,GTA5_Enhanced,GameplayHangFixture -ErrorAction SilentlyContinue)) { throw 'Close game/collector/previous fixtures first.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$sourceIdentity=@(foreach($source in @('GameplayHang.Common.ps1','Watch-GameplayHang.ps1','GameplayHangFixture.cs','Test-GameplayHang.ps1','..\CollectorReliability\CollectorHealth.ps1')) {
    $sourcePath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $source))
    [pscustomobject]@{path=$sourcePath;sha256=(Get-FileHash -LiteralPath $sourcePath).Hash.ToLowerInvariant()}
})
Write-CollectorJson (Join-Path $OutputDirectory 'source-identity.json') $sourceIdentity
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed) {
    $checks.Add([pscustomobject]@{name=$Name;passed=$Passed})
    if (-not $Passed) { throw ('Check failed: '+$Name) }
}
$testFailure=$null
try {
    $born=[DateTime]::UtcNow.AddSeconds(-10)
    $prefix=[DateTime]::UtcNow.ToString('o')+' session=fixture pid=123 elapsed_ms=1 source=script stage=diagnostic_tick_heartbeat elapsed_ms=1 '
    $ready=$prefix+'story_ready=True playable=True browser_ready=True'
    Check 'current exact-session readiness' (Test-GameplayReadyLine $ready 123 $born)
    Check 'wrong PID rejected' (-not (Test-GameplayReadyLine $ready 124 $born))
    Check 'old session rejected' (-not (Test-GameplayReadyLine $ready 123 ([DateTime]::UtcNow.AddSeconds(1))))
    Check 'future timestamp rejected' (-not (Test-GameplayReadyLine ($ready -replace '^\S+', [DateTime]::UtcNow.AddMinutes(1).ToString('o')) 123 $born))
    Check 'non-playable rejected' (-not (Test-GameplayReadyLine ($ready.Replace('playable=True','playable=False')) 123 $born))
    Check 'non-ready browser rejected' (-not (Test-GameplayReadyLine ($ready.Replace('browser_ready=True','browser_ready=False')) 123 $born))
    Check 'non-script source rejected' (-not (Test-GameplayReadyLine ($ready.Replace('source=script','source=host')) 123 $born))
    Check 'non-heartbeat rejected' (-not (Test-GameplayReadyLine ($ready.Replace('diagnostic_tick_heartbeat','construction_begin')) 123 $born))
    Check 'overflow PID rejected without throwing' (-not (Test-GameplayReadyLine ($ready.Replace('pid=123','pid=99999999999999')) 123 $born))
    Check 'duplicate identity rejected' (-not (Test-GameplayReadyLine ($ready+' pid=123') 123 $born))
    Check 'conflicting readiness rejected' (-not (Test-GameplayReadyLine ($ready+' playable=False') 123 $born))
    Check 'exact path and start accepted' (Test-GameplayIdentity 'C:\test.exe' 100 'c:\test.exe' 100)
    Check 'different start rejected' (-not (Test-GameplayIdentity 'C:\test.exe' 100 'c:\test.exe' 101))
    Check 'different path rejected' (-not (Test-GameplayIdentity 'C:\test.exe' 100 'c:\other.exe' 100))
    Check 'unset start rejected' (-not (Test-GameplayIdentity 'C:\test.exe' 0 'c:\test.exe' 0))
    Check 'disk threshold accepted' (Test-GameplaySpace 2GB)
    Check 'low disk rejected' (-not (Test-GameplaySpace (2GB-1)))

    $fixtureRoot=Join-Path $OutputDirectory 'fixture'
    [void][IO.Directory]::CreateDirectory($fixtureRoot)
    $fixtureExe=Join-Path $fixtureRoot 'GameplayHangFixture.exe'
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ('/out:'+$fixtureExe) (Join-Path $PSScriptRoot 'GameplayHangFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    $fixtureHash=(Get-FileHash -LiteralPath $fixtureExe).Hash
    $watcher=Join-Path $PSScriptRoot 'Watch-GameplayHang.ps1'
    $powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'

    foreach ($scenario in @('hang','responsive','pre-ready-hang','transient','cancel','deadline','wrong-identity','wrong-hash','overlapping-collector')) {
        Write-Output ('Rehearsal: '+$scenario)
        $mode=if($scenario -in @('cancel','deadline')){'responsive'}elseif($scenario -in @('wrong-identity','wrong-hash','overlapping-collector')){'short'}else{$scenario}
        $log=Join-Path $fixtureRoot ($scenario+'.log')
        $caseOutput=Join-Path $OutputDirectory $scenario
        $fixture=Start-Process -FilePath $fixtureExe -ArgumentList ($mode+' "'+$log+'"') -WindowStyle Hidden -PassThru
        [void]$fixture.Handle
        $watch=$null; $otherCollector=$null
        try {
            if ($scenario -eq 'overlapping-collector') {
                # A harmless owned fixture, not ProcDump. Its process name tests
                # the cross-architecture overlap guard without another attach.
                $stubExe=Join-Path $fixtureRoot 'procdump.exe'
                [IO.File]::Copy($fixtureExe,$stubExe,$false)
                $otherCollector=Start-Process -FilePath $stubExe -ArgumentList ('short "'+(Join-Path $fixtureRoot 'other-collector.log')+'"') -WindowStyle Hidden -PassThru
                [void]$otherCollector.Handle
                Check 'other architecture collector name detected' (@(Get-GameplayCollectors | Where-Object Id -eq $otherCollector.Id).Count -eq 1)
            }
            $startTicks=$fixture.StartTime.ToUniversalTime().Ticks
            if($scenario -eq 'wrong-identity') { $startTicks++ }
            $seconds=if($scenario -eq 'deadline'){5}else{30}
            $caseHash=if($scenario -eq 'wrong-hash'){'0'*64}else{$fixtureHash}
            $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$watcher+'" -OutputDirectory "'+$caseOutput+'" -TargetProcessId '+$fixture.Id+' -TargetStartUtcTicks '+$startTicks+' -MaximumSeconds '+$seconds+' -Rehearsal -FixtureDirectory "'+$fixtureRoot+'" -FixtureSha256 '+$caseHash+' -FixtureLog "'+$log+'"'
            $watch=Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory ($scenario+'-stdout.txt')) -RedirectStandardError (Join-Path $OutputDirectory ($scenario+'-stderr.txt'))
            [void]$watch.Handle
            if($scenario -eq 'cancel') {
                $cancelClock=[Diagnostics.Stopwatch]::StartNew(); $armed=$false
                while (-not $watch.HasExited -and $cancelClock.Elapsed.TotalSeconds -lt 8) {
                    $statusPath=Join-Path $caseOutput 'status.json'
                    if (Test-Path -LiteralPath $statusPath) {
                        $status=[IO.File]::ReadAllText($statusPath)|ConvertFrom-Json
                        if ($status.monitorArmed) { $armed=$true; break }
                    }
                    Start-Sleep -Milliseconds 100
                }
                Check 'cancel scenario really armed before stop' $armed
                [IO.File]::WriteAllText((Join-Path $caseOutput 'stop.request'),'owned rehearsal cancellation')
            }
            Check ($scenario+': bounded watcher exit') ($watch.WaitForExit(40000))
            $watch.WaitForExit()
            if ($scenario -in @('wrong-identity','wrong-hash','overlapping-collector')) {
                Check ($scenario+': fails before output/attachment') ($watch.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $caseOutput))
                $guard=if($scenario -eq 'wrong-identity'){'Target path/start identity mismatch'}elseif($scenario -eq 'wrong-hash'){'Target executable hash mismatch'}else{'Another ProcDump is active; refusing overlap.'}
                Check ($scenario+': reports exact guard') (([IO.File]::ReadAllText((Join-Path $OutputDirectory ($scenario+'-stderr.txt')))).Contains($guard))
            } else {
                if ($watch.ExitCode -ne 0) { throw ('Watcher failed: '+[IO.File]::ReadAllText((Join-Path $OutputDirectory ($scenario+'-stderr.txt')))) }
                $result=[IO.File]::ReadAllText((Join-Path $caseOutput 'result.json'))|ConvertFrom-Json
                Check ($scenario+': local rehearsal only') ($result.rehearsal -and -not $result.gameLaunched -and -not $result.installationChanged -and -not $result.uploads)
                Check ($scenario+': no watcher failure') (-not $result.failure)
                if ($scenario -eq 'hang') {
                    Check 'real hung window captured' ($result.state -eq 'capture-complete' -and $result.monitorArmed)
                    Check 'original target survives capture' $result.targetStillAlive
                    Check 'dump has correct PID, threads, stacks and modules' ($result.capture.pid -eq $fixture.Id -and $result.capture.threadsWithStacks -gt 0 -and $result.capture.threadsWithContexts -gt 0 -and $result.capture.modules -gt 0)
                    $validDump=Join-Path $caseOutput 'gameplay-hang.dmp'; $dumpPid=$fixture.Id
                } else {
                    Check ($scenario+': no dump') (-not $result.capture -and -not (Test-Path -LiteralPath (Join-Path $caseOutput 'gameplay-hang.dmp')))
                    if ($scenario -eq 'pre-ready-hang') { Check 'pre-ready hang never armed' (-not $result.monitorArmed -and $result.state -eq 'target-exited-no-capture') }
                    elseif ($scenario -in @('cancel','deadline')) {
                        $expectedState=if($scenario -eq 'cancel'){'cancelled'}else{'deadline-no-capture'}
                        Check ($scenario+': cooperative cancellation and live target') ($result.state -eq $expectedState -and $result.cancelRequested -and $result.targetStillAlive)
                    } else { Check ($scenario+': armed and followed to natural exit') ($result.monitorArmed -and $result.state -eq 'target-exited-no-capture') }
                }
            }
            if ($otherCollector) { Check 'other collector fixture natural exit' ($otherCollector.WaitForExit(10000) -and $otherCollector.ExitCode -eq 0) }
            Check ($scenario+': collector exited') (-not (Get-GameplayCollectors))
            Check ($scenario+': fixture natural exit bounded') ($fixture.WaitForExit(25000))
            Check ($scenario+': fixture natural exit zero') ($fixture.ExitCode -eq 0)
        } finally {
            # Never force-kill a collector or target on a failing test. All fixtures
            # have a finite lifetime; watcher itself has a deadline.
            if ($watch) { $watch.Dispose() }; if ($otherCollector) { $otherCollector.Dispose() }; $fixture.Dispose()
        }
    }
    # Corrupt copies of our generated fixture dump only. Preserve the captured file.
    $validBytes=[IO.File]::ReadAllBytes($validDump)
    foreach($corruption in @('signature','full-memory','directory','pid','truncated','missing-thread-stream','context-bounds')) {
        $bytes=[byte[]]$validBytes.Clone()
        $directory=[BitConverter]::ToUInt32($bytes,12)
        $streamCount=[BitConverter]::ToUInt32($bytes,8)
        if ($corruption -eq 'signature') { $bytes[0]=0 }
        elseif ($corruption -eq 'full-memory') { $bytes[24]=$bytes[24] -bor 2 }
        elseif ($corruption -eq 'directory') { [BitConverter]::GetBytes([uint32]::MaxValue).CopyTo($bytes,12) }
        elseif ($corruption -eq 'truncated') { $bytes=[byte[]]$bytes[0..30] }
        else {
            for ($i=0;$i -lt $streamCount;$i++) {
                $entry=[int]$directory+12*$i
                $type=[BitConverter]::ToUInt32($bytes,$entry)
                $rva=[int][BitConverter]::ToUInt32($bytes,$entry+8)
                if($corruption -eq 'pid' -and $type -eq 15) { [BitConverter]::GetBytes([uint32]($dumpPid+1)).CopyTo($bytes,$rva+8) }
                if($corruption -eq 'missing-thread-stream' -and $type -eq 3) { [BitConverter]::GetBytes([uint32]99).CopyTo($bytes,$entry) }
                if($corruption -eq 'context-bounds' -and $type -eq 3) { [BitConverter]::GetBytes([uint32]::MaxValue).CopyTo($bytes,$rva+4+44) }
            }
        }
        $badPath=Join-Path $OutputDirectory ('invalid-'+$corruption+'.dmp')
        [IO.File]::WriteAllBytes($badPath,$bytes)
        $rejected=$false
        try { $null=Read-GameplayDump $badPath $dumpPid } catch { $rejected=$true }
        Check ('corrupt dump rejected: '+$corruption) $rejected
    }
    foreach($source in $sourceIdentity) { Check ('tested source unchanged: '+[IO.Path]::GetFileName($source.path)) ((Get-FileHash -LiteralPath $source.path).Hash -ieq $source.sha256) }
    Check 'no leftover game, fixture or collector process' (-not (Get-GameplayCollectors) -and -not (Get-Process -Name GTA5,GTA5_Enhanced,GameplayHangFixture -ErrorAction SilentlyContinue))
} catch { $testFailure=$_.Exception.Message }
Write-CollectorJson (Join-Path $OutputDirectory 'tests.json') ([ordered]@{passed=(-not $testFailure);failure=$testFailure;checks=$checks.ToArray();gameLaunched=$false;installationChanged=$false;uploads=$false})
if ($testFailure) { throw $testFailure }
Write-Output ('PASS: '+$checks.Count+' gameplay-hang checks; no game launched or installation changed.')
