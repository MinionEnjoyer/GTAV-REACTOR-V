param(
    [ValidateSet('Prepare','Preflight','Rehearse','Run','Restore')][string]$Action='Preflight',
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
# Local, single-session comparison. Run stays open on the desktop; never launches
# GTA, changes Steam settings, force-kills a process, uploads, or edits product code.
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) { throw 'Use x64 Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
Import-Module (Join-Path $PSHOME 'Modules\CimCmdlets\CimCmdlets.psd1')
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
. (Join-Path $PSScriptRoot '..\GameplayHangCapture\GameplayHang.Common.ps1')
. (Join-Path $PSScriptRoot '..\GameplayHangCapture\GameplayHang.Session.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (-not $OutputDirectory.StartsWith((Get-CollectorDataRoot)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use the shared diagnostics root, not AppData.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$driver=Join-Path $PSScriptRoot 'Deploy-PresentationTest.ps1'
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$game='D:\Programs\Steam\steamapps\common\Grand Theft Auto V Enhanced'
$installed=Join-Path $game 'plugins\ReactorV'
$store=Join-Path $OutputDirectory 'live\isolation-backups'
$fixturePayload=Join-Path $OutputDirectory 'fixture-payload'
$preparedPath=Join-Path $OutputDirectory 'prepared.json'
$live=Join-Path $OutputDirectory 'live'
$userProfile = [Environment]::GetFolderPath('UserProfile')
$qualified=Join-Path $userProfile 'ReactorV-Diagnostics\external-browser-offline-20260909T195835Z-8ff3f2fbe4124692a7f7464ea62c0dbe'
$candidateHash='061c490c8d65bb06b5ee77c8d2dcd7e2e982349ea2df5063d41bf2cfc91db07e'
$preloaderHash='5d08fec9024e5e0bb81f0cbeb2b9ed1c3c9fa6c981f4fc0c42ff74d224704b10'
$scriptHash='ba7762220f7ad548181325b915175abfd953e1a4d6a4fbc9641a18ceb5c76db5'
$coreHash='708c4b28d53e7a2a68808265409d55dd2114d572789d9fec3ef621fe777d6279'
$originalCoreHash='674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862'
$coreTargets=@('plugins/ReactorV/RageWebUI.Core.dll','scripts/ReactorV/RageWebUI.Core.dll')
$originalScriptHash='a9df990bf43700758b1905568159ffd5b6ac592b70fb6027f08c28a05cedac0f'
$originalPreloaderHash='77d05d87ad12a391911935ad84e43b0c8e996b607291adf2f335a9bafa163db1'
$baselineReceipt=Join-Path $userProfile 'ReactorV-Diagnostics\isolation-backups\4f61325b43bb4854b74831f2551ca72c\receipt.json'
if (Test-Path -LiteralPath (Join-Path $repo 'baseline.json')) { $baselineReceipt=Join-Path $repo 'baseline.json' }
$originalRuntimeHash='54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6'
$gameplayRestoreBlocked=$false
$toolFiles=@('tools\ExternalHostComparison\Run-PresentationComparison.ps1','tools\ExternalHostComparison\Deploy-PresentationTest.ps1','tools\ExternalHostComparison\Comparison.Common.ps1','tools\ExternalHostComparison\ProviderFixture.cs','tools\CollectorReliability\CollectorHealth.ps1','tools\GameplayHangCapture\GameplayHang.Common.ps1','tools\GameplayHangCapture\GameplayHang.Session.ps1','tools\GameplayHangCapture\Watch-GameplayHang.ps1')

function Require-ComparisonStopped {
    if ($script:gameplayRestoreBlocked) { throw 'Observer closeout did not confirm it stopped; restoration needs attention.' }
    if (Get-Process -Name GTA5,GTA5_Enhanced,ReactorV.Preloader,ProviderFixture -ErrorAction SilentlyContinue) { throw 'Close GTA and the prior comparison host/fixture first.' }
    Assert-GameplayObserverStopped (Join-Path $live 'gameplay-hang')
}
function Invoke-ComparisonDriver([string]$DriverAction) {
    if (Test-Path -LiteralPath $preparedPath) {
        $sealed=Get-Content -LiteralPath $preparedPath -Raw|ConvertFrom-Json
        $deploymentTools=@($sealed.tools|Where-Object { $_.path -in @('tools\ExternalHostComparison\Deploy-PresentationTest.ps1','tools\ExternalHostComparison\Comparison.Common.ps1') })
        if ($deploymentTools.Count -ne 2) { throw 'Missing sealed deployment tool identities.' }
        foreach ($entry in $deploymentTools) {
            if ((Get-ComparisonHash (Join-Path $repo $entry.path)) -cne $entry.sha256) { throw 'Sealed deployment tool changed; preserve the package for recovery.' }
        }
    }
    & $shell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $driver -Action $DriverAction -GameDirectory $game -OutputDirectory $live -StoreDirectory $store -PayloadDirectory $fixturePayload
    if ($LASTEXITCODE -ne 0) { throw ('Deployment driver failed: '+$DriverAction) }
}
function Get-ComparisonManifest([string]$Directory) {
    $items=@(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if (@($items|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Reparse point in payload.' }
    @($items|Where-Object { -not $_.PSIsContainer }|Sort-Object FullName|ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($Directory.Length).TrimStart('\');sha256=(Get-ComparisonHash $_.FullName)}
    })
}
function Assert-ComparisonBaseline {
    $baseline=Get-Content -LiteralPath $baselineReceipt -Raw|ConvertFrom-Json
    if ($baseline.Status -cne 'Restored' -or $baseline.Root -ine $game -or $baseline.Identity.PSObject.Properties.Count -eq 0) { throw 'Invalid restored baseline receipt.' }
    foreach ($entry in $baseline.Identity.PSObject.Properties) {
        $expected=if ($entry.Name -eq 'plugins/ReactorV/RageWebUI.Runtime.dll') { $originalRuntimeHash } else { $entry.Value }
        if ((Get-ComparisonHash (Join-Path $game $entry.Name)) -ine $expected) { throw ('Installed baseline changed: '+$entry.Name) }
    }
    foreach ($relative in $coreTargets) {
        if ((Get-ComparisonHash (Join-Path $game $relative)) -cne $originalCoreHash) { throw ('Installed Core baseline changed: '+$relative) }
    }
}
function Assert-ComparisonApplied {
    $runtime=Get-Content -LiteralPath "$live\presentation-install-receipt.json" -Raw|ConvertFrom-Json
    if ($runtime.schema -ne 3 -or $runtime.phase -cne 'Applied' -or $runtime.kind -cne 'desktop-presentation-five-file-v3' -or $runtime.files.Count -ne 5 -or $runtime.gameDirectory -ine $game -or $runtime.storeDirectory -ine $store -or $runtime.isolationId -notmatch '^[a-f0-9]{32}$') { throw 'Applied presentation receipt identity invalid.' }
    if ((Get-ComparisonHash "$installed\RageWebUI.Runtime.dll") -cne $candidateHash -or (Get-ComparisonHash "$installed\ReactorV.Preloader.exe") -cne $preloaderHash -or (Get-ComparisonHash "$game\scripts\ReactorV\RageWebUI.Script.dll") -cne $scriptHash) { throw 'Applied presentation binary mismatch.' }
    foreach ($relative in $coreTargets) { if ((Get-ComparisonHash (Join-Path $game $relative)) -cne $coreHash) { throw ('Applied Core mismatch: '+$relative) } }
    $receipt=Get-Content -LiteralPath (Join-Path $store ($runtime.isolationId+'\receipt.json')) -Raw|ConvertFrom-Json
    if ($receipt.Status -cne 'Prepared' -or $receipt.Root -ine $game -or $receipt.Mode -cne 'native-off-windowed') { throw 'Isolation receipt is not the intended prepared mode.' }
    foreach ($entry in $receipt.Identity.PSObject.Properties) {
        $path=Join-Path $game $entry.Name
        $change=@($receipt.Changes|Where-Object Relative -eq $entry.Name)
        if ($change.Count -gt 1) { throw 'Duplicate applied change.' }
        if ($change.Count -eq 1 -and $null -eq $change[0].AppliedHash) {
            if (Test-Path -LiteralPath $path) { throw ('Expected absent native file: '+$entry.Name) }
        } else {
            $expected=if($change.Count){$change[0].AppliedHash}else{$entry.Value}
            if ((Get-ComparisonHash $path) -ine $expected) { throw ('Applied layout changed: '+$entry.Name) }
        }
    }
}
function Assert-ComparisonPrepared {
    $script:prepared=Get-Content -LiteralPath $preparedPath -Raw|ConvertFrom-Json
    if ($prepared.schema -ne 5 -or $prepared.root -ine $OutputDirectory -or $prepared.gameDirectory -ine $game) { throw 'Preparation identity mismatch (hang-observed schema 5 required).' }
    if ($prepared.tools.Count -ne $toolFiles.Count -or @($prepared.tools.path|Select-Object -Unique).Count -ne $toolFiles.Count -or @($prepared.tools.path|Where-Object {$_ -cnotin $toolFiles}).Count) { throw 'Prepared observer/tool set mismatch.' }
    foreach ($entry in $prepared.tools) {
        if ((Get-ComparisonHash (Join-Path $repo $entry.path)) -cne $entry.sha256) { throw ('Comparison tool changed; re-prepare and rehearse: '+$entry.path) }
    }
    if ($prepared.sourcePreparation) {
        $evidenceNames=@('deployment-rehearsal.json','session-qualification.json','hang-qualification.json','hang-source-identity.json')
        if ($prepared.evidence.Count -ne 4 -or @($prepared.evidence.path|Select-Object -Unique).Count -ne 4 -or @($prepared.evidence.path|Where-Object { $_ -cnotin $evidenceNames }).Count) { throw 'Prepared qualification evidence set mismatch.' }
        foreach($entry in $prepared.evidence) { if ((Get-ComparisonHash (Join-Path $OutputDirectory $entry.path)) -cne $entry.sha256) { throw 'Sealed qualification evidence changed.' } }
    }
    $deployment=Get-Content -LiteralPath "$OutputDirectory\deployment-rehearsal.json" -Raw|ConvertFrom-Json
    if (-not $deployment.passed -or $deployment.driverSha256 -cne (Get-ComparisonHash $driver) -or
        $deployment.commonSha256 -cne (Get-ComparisonHash (Join-Path $PSScriptRoot 'Comparison.Common.ps1'))) { throw 'Matching five-file deployment rehearsal required.' }
    if ($prepared.baselineSha256 -and (Get-ComparisonHash $baselineReceipt) -cne $prepared.baselineSha256) { throw 'Baseline manifest changed.' }
    $actual=Get-ComparisonManifest $fixturePayload
    if (($actual|ConvertTo-Json -Compress) -cne ($prepared.payload|ConvertTo-Json -Compress)) { throw 'Prepared payload changed.' }
    foreach ($entry in $prepared.payload|Where-Object { $_.path.StartsWith('ui\') }) {
        if ((Get-ComparisonHash (Join-Path $installed $entry.path)) -cne $entry.sha256) { throw 'Installed UI differs from the rehearsed UI.' }
    }
    foreach ($entry in $prepared.payload|Where-Object { -not $_.path.StartsWith('ui\') -and -not $_.path.StartsWith('isolation-helper\') -and $_.path -notin @('ProviderFixture.exe','RageWebUI.Runtime.dll','ReactorV.Preloader.exe','RageWebUI.Script.dll','RageWebUI.Core.dll') }) {
        if ((Get-ComparisonHash (Join-Path $installed $entry.path)) -cne $entry.sha256) { throw ('Installed host dependency differs from rehearsal: '+$entry.path) }
    }
    if (@(Get-ChildItem -LiteralPath "$installed\ui" -Recurse -File).Count -ne @($prepared.payload|Where-Object { $_.path.StartsWith('ui\') }).Count) { throw 'Installed UI file set changed.' }
    if ((Get-ComparisonHash "$installed\ReactorV.Preloader.exe") -cnotin @($preloaderHash,$originalPreloaderHash)) { throw 'Installed preloader changed.' }
    if ((Get-ComparisonHash "$game\scripts\ReactorV\RageWebUI.Script.dll") -cnotin @($scriptHash,$originalScriptHash)) { throw 'Installed script changed.' }
    if ((Get-ComparisonHash "$fixturePayload\RageWebUI.Script.dll") -cne $scriptHash) { throw 'Prepared script candidate changed.' }
    if ((Get-ComparisonHash "$fixturePayload\RageWebUI.Core.dll") -cne $coreHash) { throw 'Prepared Core candidate changed.' }
    foreach ($relative in $coreTargets) { if ((Get-ComparisonHash (Join-Path $game $relative)) -cnotin @($coreHash,$originalCoreHash)) { throw ('Installed Core changed: '+$relative) } }
}
function Read-ComparisonSession([string]$Path,[DateTime]$Since) {
    $text=Read-ComparisonText $Path
    @($text -split '[\r\n]+'|Where-Object {
        if ($_ -match '^(\d{4}-\d\d-\d\dT\S+)\s') {
            try { [DateTime]::Parse($matches[1]).ToUniversalTime() -ge $Since } catch { $false }
        } else { $false }
    }) -join "`n"
}
function Invoke-ComparisonObservation([string]$Directory,[Diagnostics.Process]$Target,[bool]$Offline) {
    $tracker=New-ComparisonTracker (Join-Path $Directory 'events.jsonl')
    $hostProcess=$null; $clock=[Diagnostics.Stopwatch]::StartNew(); $exitAt=$null; $attached=$false
    $hangSession=$null; $hangCloseout=$null; $announcedHangArmed=$false; $announcedHangFailure=$false
    $failures=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $profile=Join-Path $Directory 'profile'; $hostLogs=Join-Path $Directory 'host-logs'
    [void][IO.Directory]::CreateDirectory($hostLogs)
    $runtimePath=if($Offline){Join-Path $Directory 'target-logs\reactorv-runtime.log'}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ReactorV\reactorv-runtime.log'}
    $runtimeDirectory=if($Offline){$fixturePayload}else{$installed}
    function Disqualify([string]$Reason) {
        if ($failures.Add($Reason)) {
            Write-ComparisonEvent $tracker 'disqualified' @{reason=$Reason}
            Write-Host ('NOT A QUALIFIED COMPARISON: '+$Reason+'. Exit GTA normally when convenient; evidence collection continues.') -ForegroundColor Red
        }
    }
    try {
        Add-ComparisonProcess $tracker $Target 'target' 0
        $startUtc=$Target.StartTime.ToUniversalTime()
        $watcher=Join-Path $PSScriptRoot '..\GameplayHangCapture\Watch-GameplayHang.ps1'
        $hangSession=Start-GameplayObserver $Target (Join-Path $Directory 'gameplay-hang') $watcher $(if($Offline){$OutputDirectory}else{''}) $(if($Offline){Join-Path $Directory 'target-logs\gameplay-ready.log'}else{''})
        Write-ComparisonEvent $tracker 'gameplay-observer-started' $hangSession.expected
        if (Get-Process -Name ReactorV.Preloader -ErrorAction SilentlyContinue) { throw 'An unexpected preloader is already running; refusing a second host.' }
        $tracker.expectedPreloaderPath=[IO.Path]::GetFullPath("$runtimeDirectory\ReactorV.Preloader.exe")
        $tracker.expectedPreloaderHash=Get-ComparisonHash $tracker.expectedPreloaderPath
        $arguments=@('--persistent-host','--no-external-gpu-browser-shadow','--parent-pid',[string]$Target.Id,
            '--ui-dir',('"'+$runtimeDirectory+'\ui"'),'--user-data-dir',('"'+$profile+'"'),
            '--log-dir',('"'+$hostLogs+'"'),'--instance-id',('comparison-'+[Guid]::NewGuid().ToString('N')))
        $hostProcess=Start-Process -FilePath "$runtimeDirectory\ReactorV.Preloader.exe" -ArgumentList $arguments -WorkingDirectory $runtimeDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Directory\host-stdout.txt" -RedirectStandardError "$Directory\host-stderr.txt"
        Add-ComparisonProcess $tracker $hostProcess 'host' $PID
        Write-ComparisonEvent $tracker 'host-launched' @{targetPid=$Target.Id;hostPid=$hostProcess.Id;osParentPid=$PID;offline=$Offline;selfTest=$false;externalGpuShadow=$false}
        $maximum=if($Offline){150}else{1800}
        while ($clock.Elapsed.TotalSeconds -lt $maximum) {
            Update-GameplayObserver $hangSession
            if ($hangSession.failure -and -not $announcedHangFailure) { $announcedHangFailure=$true; Disqualify 'gameplay-observer-failed'; Write-ComparisonEvent $tracker 'gameplay-observer-failure' @{error=$hangSession.failure} }
            if ($hangSession.armed -and -not $announcedHangArmed) {
                $announcedHangArmed=$true
                Write-ComparisonEvent $tracker 'gameplay-hang-monitor-armed' @{observerPid=$hangSession.expected.observerPid;targetPid=$Target.Id}
                Write-Host 'GAMEPLAY HANG MONITOR ARMED for this session. Dumps stay local; this does not confirm visible GBay.' -ForegroundColor Green
            }
            if ($hangSession.result -and $hangSession.result.state -eq 'capture-complete') { Disqualify 'gameplay-hang-captured' }
            if ($hangSession.result -and -not $Target.HasExited -and $hangSession.result.state -ne 'capture-complete') { Disqualify 'gameplay-observer-ended-before-target' }
            Update-ComparisonTracker $tracker $hostProcess $profile
            $hostTrace=Read-ComparisonText "$hostLogs\reactorv-preloader.log"
            $targetTrace=Read-ComparisonSession $runtimePath $startUtc
            $route=Test-ComparisonRoute $hostTrace $targetTrace $Target.Id $hostProcess.Id
            foreach ($reason in $route.failures) { Disqualify $reason }
            if ($route.attached -and -not $attached) {
                $attached=$true
                Write-ComparisonEvent $tracker 'exact-host-attached' @{targetPid=$Target.Id;hostPid=$hostProcess.Id;seconds=$clock.Elapsed.TotalSeconds}
                Write-Host 'EXTERNAL HOST ATTACHED. Visible GBay and input still need user confirmation.' -ForegroundColor Green
            }
            foreach ($record in $tracker.records.Values) {
                if ($record.role -ne 'target' -and $record.modules.ContainsKey('gameoverlayrenderer64.dll')) { Disqualify 'steam-overlay-observed-in-external-process' }
                if ($record.modules.ContainsKey('ragewebui.native.dll') -or $record.modules.ContainsKey('libcef.dll')) { Disqualify 'native-or-cef-observed' }
            }
            if ($tracker.unexpectedPreloaders.Count) { Disqualify 'unclassified-or-competing-preloader' }
            if ($clock.Elapsed.TotalSeconds -gt 45 -and $hostTrace -notmatch 'stage=webview_content_ready ') { Disqualify 'external-content-readiness-deadline' }
            if ($clock.Elapsed.TotalSeconds -gt 180 -and -not $attached) { Disqualify 'provider-attachment-deadline' }
            if ($Target.HasExited -and $null -eq $exitAt) {
                $exitAt=$clock.Elapsed.TotalSeconds
                Write-Host 'Target exited. Keeping the collector open for the 90-second browser shutdown watch.'
            }
            if ($null -ne $exitAt -and $clock.Elapsed.TotalSeconds-$exitAt -gt 12 -and -not $hostProcess.HasExited) { Disqualify 'external-host-did-not-stop-after-target' }
            if ($null -ne $exitAt -and $clock.Elapsed.TotalSeconds-$exitAt -ge 90) { break }
            Write-CollectorJson "$Directory\status.json" ([ordered]@{observerPid=$PID;updatedUtc=[DateTime]::UtcNow.ToString('o');elapsedSeconds=$clock.Elapsed.TotalSeconds;targetPid=$Target.Id;hostPid=$hostProcess.Id;attached=$attached;gameplayObserverState=$hangSession.lastState;gameplayMonitorArmed=$hangSession.armed;gameplayObserverFailure=$hangSession.failure;disqualifiers=@($failures);offline=$Offline})
            Start-Sleep -Milliseconds 300
        }
        if (-not $Target.HasExited) { Disqualify 'session-deadline-live-processes-retained' }
        if ($tracker.linkFailures) { Disqualify 'process-linkage-incomplete' }
        if ($hostTrace -notmatch 'stage=webview_environment_contract [^\r\n]*overrides=none composition=software-stable') { Disqualify 'environment-contract-mismatch' }
        if ($hostTrace -notmatch 'stage=webview_navigation_completed success=True') { Disqualify 'navigation-not-successful' }
        if ([regex]::Matches($hostTrace,'stage=webview_controller_request_returned ').Count -ne 1) { Disqualify 'controller-request-count-not-one' }
        if ($hostTrace -notmatch 'stage=preloader_stop exit_code=0') { Disqualify 'no-clean-preloader-stop' }
        $dumps=@(Get-ChildItem -LiteralPath $profile -Recurse -Force -File -Filter '*.dmp' -ErrorAction SilentlyContinue).Count
        if ($dumps) { Disqualify 'browser-dump-present' }
        if (-not $Offline) { Assert-ComparisonApplied; Assert-ComparisonPrepared }
        if ($Offline -and (Read-ComparisonText "$Directory\target-logs\fixture-result.txt") -notmatch '^PASS secondary_appdomain=True renderer=Bootstrap WebView2 generation=[1-9]') { Disqualify 'fixture-provider-not-accepted' }
    } catch {
        Disqualify ('observer-error-'+$_.Exception.GetType().Name)
        Write-ComparisonEvent $tracker 'observer-error' @{error=$_.Exception.ToString()}
    } finally {
        try {
            $hangCloseout=Stop-GameplayObserver $hangSession
            $script:gameplayRestoreBlocked=(-not $hangCloseout.safeToRestore)
            if (-not $hangCloseout.qualified) { Disqualify 'gameplay-observer-closeout-not-qualified' }
            if (-not $hangCloseout.safeToRestore) { Disqualify 'gameplay-observer-still-active-or-unconfirmed' }
            Write-CollectorJson "$Directory\gameplay-observer-closeout.json" $hangCloseout
            Update-ComparisonTracker $tracker $hostProcess $profile
            if ($tracker.unexpectedPreloaders.Count) { Disqualify 'unclassified-or-competing-preloader' }
            $verdict=Test-ComparisonEvidence @($tracker.records.Values) $attached @($failures)
            $helperCoverage=@($tracker.records.Values|Where-Object role -eq 'desktop-probe'|ForEach-Object { [ordered]@{pid=$_.pid;moduleSamples=$_.moduleSamples;moduleFailures=$_.moduleFailures;exitCode=$_.exitCode;identityVerified=$_.identityVerified} })
            $result=[ordered]@{schema=2;offline=$Offline;qualified=$verdict.qualified;checks=$verdict.checks;disqualifiers=@($failures);browserDumpCount=$dumps;elapsedSeconds=$clock.Elapsed.TotalSeconds;records=@($tracker.records.Values);observedHelperCoverage=$helperCoverage;functionalAcceptance='Not established: user must confirm visible GBay, input and provider behavior.';limitation='Sampled modules and helper processes; short-lived helpers may be missed. Helper module coverage is reported separately, not inferred from browser coverage. The observer does not kill processes; the runtime may terminate its own desktop probe at its deadline. No causal proof about Steam or automatic fallback accepted.'}
            $result['gameplayHangObserver']=$hangCloseout
            Write-CollectorJson "$Directory\result.json" $result
            # Copy only exact-session runtime lines, not other applications or profiles.
            if ($targetTrace) { [IO.File]::WriteAllText("$Directory\target-runtime-session.log",$route.targetTrace,[Text.UTF8Encoding]::new($false)) }
        } finally {
            if ($hangSession) { $hangSession.process.Dispose() }
            $tracker.writer.Dispose()
            foreach ($process in $tracker.processes.Values) { $process.Dispose() }
        }
    }
    return [pscustomobject]$result
}

Require-ComparisonStopped
if ($Action -ne 'Restore') { Assert-GameplayCollectorAvailable }
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh comparison directory; prior evidence is never overwritten.' }
    Assert-ComparisonBaseline
    $qualification=Get-Content -LiteralPath "$qualified\results.json" -Raw|ConvertFrom-Json
    if (-not $qualification.passed -or $qualification.results.Count -ne 2) { throw 'Expected two qualified offline trials.' }
    $approved=Get-Content -LiteralPath "$qualified\payload-before.json" -Raw|ConvertFrom-Json
    foreach ($item in $approved) {
        if ((Get-ComparisonHash (Join-Path "$qualified\payload" $item.path)) -cne $item.sha256) { throw 'Qualified payload changed.' }
    }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    Copy-Item -LiteralPath "$qualified\payload" -Destination $fixturePayload -Recurse
    # Retain qualified UI/dependencies; stage the exact offline-tested build,
    # including the shared Core binary destined for two independent targets.
    $build=Join-Path $repo 'src\ReactorV.Preloader\bin\Release'
    if ((Get-ComparisonHash "$build\RageWebUI.Runtime.dll") -cne $candidateHash -or (Get-ComparisonHash "$build\ReactorV.Preloader.exe") -cne $preloaderHash) { throw 'Offline-tested candidate build changed.' }
    if ((Get-ComparisonHash "$build\RageWebUI.Core.dll") -cne $coreHash) { throw 'Offline-tested Core build changed.' }
    foreach ($name in @('RageWebUI.Runtime.dll','ReactorV.Preloader.exe','RageWebUI.Core.dll')) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $fixturePayload $name) -Force }
    $scriptBuild=Join-Path $repo 'src\ReactorV.Script\bin\Release\RageWebUI.Script.dll'
    if ((Get-ComparisonHash $scriptBuild) -cne $scriptHash) { throw 'Offline-tested script build changed.' }
    if ((Get-ComparisonHash (Join-Path ([IO.Path]::GetDirectoryName($scriptBuild)) 'RageWebUI.Core.dll')) -cne $coreHash) { throw 'Script and host Core builds disagree.' }
    Copy-Item -LiteralPath $scriptBuild -Destination "$fixturePayload\RageWebUI.Script.dll" -Force
    [void][IO.Directory]::CreateDirectory("$fixturePayload\isolation-helper")
    $helperSource=Join-Path $userProfile 'Desktop\ReactorV-issue1-isolation-local-test\ReactorV-issue1-isolation-v1'
    foreach ($name in @('ReactorV.Issue1Isolation.exe','Newtonsoft.Json.dll')) { Copy-Item -LiteralPath (Join-Path $helperSource $name) -Destination "$fixturePayload\isolation-helper" }
    Invoke-ComparisonDriver 'Preflight'
    # Compile the disposable secondary-AppDomain target for the new candidate.
    $references=@('System.dll',"$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\netstandard.dll","$fixturePayload\RageWebUI.Core.dll","$fixturePayload\RageWebUI.Runtime.dll","$fixturePayload\Newtonsoft.Json.dll")
    & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 ("/out:$fixturePayload\ProviderFixture.exe") ($references|ForEach-Object {'/reference:'+$_}) "$PSScriptRoot\ProviderFixture.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    $tools=@($toolFiles|ForEach-Object { [pscustomobject]@{path=$_;sha256=(Get-ComparisonHash (Join-Path $repo $_))} })
    Write-CollectorJson $preparedPath ([ordered]@{schema=5;root=$OutputDirectory;gameDirectory=$game;createdUtc=[DateTime]::UtcNow.ToString('o');tools=$tools;payload=(Get-ComparisonManifest $fixturePayload);priorDependencyQualificationRoot=$qualified;installationChanged=$false})
    Write-Host ('PREPARED FILES ONLY. Next rehearse: '+$OutputDirectory)
    exit 0
}
if ($Action -eq 'Restore') {
    # Restoration uses the fixed qualified driver/receipt, independently of
    # later comparison-tool changes; never needs the test payload to survive.
    Invoke-ComparisonDriver 'Restore'; Assert-ComparisonBaseline
    Write-Host 'RESTORED: all 14 original baseline hashes match, including both Core DLLs.' -ForegroundColor Green
    exit 0
}
Assert-ComparisonPrepared
Assert-ComparisonBaseline
Invoke-ComparisonDriver 'Preflight'
if ($Action -eq 'Preflight') { Write-Host 'PREFLIGHT PASSED. No installation or live process started.'; exit 0 }
if ($Action -eq 'Rehearse') {
    $directory=Join-Path $OutputDirectory ('rehearsal-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
    [void][IO.Directory]::CreateDirectory("$directory\target-logs")
    $target=Start-Process -FilePath "$fixturePayload\ProviderFixture.exe" -ArgumentList @(('"'+$fixturePayload+'"'),('"'+$directory+'\target-logs"')) -WorkingDirectory $fixturePayload -WindowStyle Hidden -PassThru -RedirectStandardOutput "$directory\target-stdout.txt" -RedirectStandardError "$directory\target-stderr.txt"
    $result=Invoke-ComparisonObservation $directory $target $true
    Assert-ComparisonPrepared; Assert-ComparisonBaseline
    Write-CollectorJson "$OutputDirectory\rehearsal.json" ([ordered]@{passed=$result.qualified;directory=$directory;preparedSha256=(Get-ComparisonHash $preparedPath);completedUtc=[DateTime]::UtcNow.ToString('o')})
    if (-not $result.qualified) { throw ('Rehearsal failed: '+$directory) }
    Write-Host 'OFFLINE HANDOFF REHEARSAL PASSED. GTA installation unchanged.' -ForegroundColor Green
    exit 0
}

# User-launched desktop action only. One test per prepared directory.
$rehearsal=Get-Content -LiteralPath "$OutputDirectory\rehearsal.json" -Raw|ConvertFrom-Json
if (-not $rehearsal.passed -or $rehearsal.preparedSha256 -cne (Get-ComparisonHash $preparedPath)) { throw 'A successful matching offline rehearsal is required.' }
if (Test-Path -LiteralPath $live) { throw 'This comparison has already been used; inspect its receipts/results before another test.' }
$mutex=[Threading.Mutex]::new($false,'Local\ReactorV.ControllerCapturePreparation'); $ownsMutex=$false; $restoreFailed=$false
try {
    try { $ownsMutex=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex=$true }
    if (-not $ownsMutex) { throw 'Another diagnostic preparation is active.' }
    [void][IO.Directory]::CreateDirectory($live)
    Invoke-ComparisonDriver 'PrepareOnly'; Assert-ComparisonApplied
    $armed=[DateTime]::UtcNow; $target=$null; $wait=[Diagnostics.Stopwatch]::StartNew()
    Write-Host 'READY FOR USER LAUNCH: start GTA Enhanced in STORY MODE normally. Keep this window open.' -ForegroundColor Green
    Write-Host 'Launch within five minutes. In Story Mode FIRST test ESC and Back before opening GBay. If a vehicle is available, check the HUD before GBay too.'
    Write-Host 'Then open GBay with F9, close it with F9, and test ESC and Back again. Record the original result before trying another pause tab. Check GBay reopen and HUD, then exit normally.'
    Write-Host 'No startup preloader screen is expected in this native-off test. F9 GBay is the check.'
    Write-Host 'Gameplay hang capture waits for this session to become Story-ready, then stays active. Watch for GAMEPLAY HANG MONITOR ARMED; observer failures are not a passing test.'
    Write-Host 'The observer rejects unknown/competing hosts. It waits 90 seconds after game exit, then restores ALL FIVE binary targets and native/config originals.'
    while ($wait.Elapsed.TotalSeconds -lt 300) {
        $candidates=@(Get-Process -Name GTA5_Enhanced -ErrorAction SilentlyContinue)
        if ($candidates.Count -gt 1) { foreach($p in $candidates){$p.Dispose()}; throw 'Multiple GTA processes; refusing ambiguous target.' }
        if ($candidates.Count -eq 1) {
            $target=$candidates[0]; [void]$target.Handle
            if ($target.StartTime.ToUniversalTime() -lt $armed -or $target.MainModule.FileName -ine "$game\GTA5_Enhanced.exe") { throw 'Target is not the new expected game process.' }
            if ((Get-ComparisonHash $target.MainModule.FileName) -cne '69da07ff67d05e9ded11289e597e8b8dc5855b0a429c085f37d148dc267cb2c5') { throw 'Game executable identity changed.' }
            break
        }
        Write-CollectorJson "$live\status.json" ([ordered]@{observerPid=$PID;updatedUtc=[DateTime]::UtcNow.ToString('o');state='waiting-for-user-game-launch';armedUtc=$armed.ToString('o')})
        Start-Sleep -Milliseconds 200
    }
    if (-not $target) { throw 'Five-minute launch deadline reached. Restoring without starting a host.' }
    $result=Invoke-ComparisonObservation $live $target $false
    Write-Host ('Technical comparison qualified: '+$result.qualified+'. Visible GBay still requires your report.')
} finally {
    try {
        if (Test-Path -LiteralPath "$live\presentation-install-receipt.json") {
            Require-ComparisonStopped
            Invoke-ComparisonDriver 'Restore'; Assert-ComparisonBaseline
            Write-Host 'RESTORED: original installation verified. Logs/backups retained locally.' -ForegroundColor Green
        }
    } catch { $restoreFailed=$true; Write-Host ('RESTORATION NEEDS ATTENTION: '+$_.Exception.Message+'. Do not delete the receipt/backups. Close GTA/host, then use Restore Comparison.cmd.') -ForegroundColor Red }
    if ($ownsMutex) { $mutex.ReleaseMutex() }; $mutex.Dispose()
}
if ($restoreFailed) { throw 'Restoration remains incomplete; retained receipts identify the required recovery.' }
