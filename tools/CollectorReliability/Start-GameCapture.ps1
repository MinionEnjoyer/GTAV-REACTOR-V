param([string]$OutputDirectory, [switch]$PreflightOnly)
# One explicitly authorized, user-launched Story Mode diagnostic. Never launches
# GTA, changes Steam/graphics/profile settings, or uploads dumps. Run from the
# desktop, not a host that restricts independent collector lifetime.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'CollectorHealth.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'ProcessLifetime.cs')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$driver = Join-Path $root 'artifacts\issue-1-controller-startup\Local-Controller-Test.ps1'
$watcher = Join-Path $root 'artifacts\issue-1-controller-startup\Watch-Controller-Hang.ps1'
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$gameDirectory = 'D:\Programs\Steam\steamapps\common\Grand Theft Auto V Enhanced'
$collector = Get-CollectorToolPath
$storeDirectory = Join-Path (Get-CollectorDataRoot) 'isolation-backups'
$collectorHash = 'd1fc99ae304bd1d2bf28abeb62531da959e2431916194981b88c958fd713a8e6'

function Require-GameStopped {
    if (Get-Process -Name GTA5_Enhanced,GTA5,ReactorV.Preloader -ErrorAction SilentlyContinue) { throw 'Close GTA and its preloader before preparing this test.' }
}
function Invoke-Driver([string]$Action) {
    # Synchronous, short-lived qualified preflight/restore, not a background observer.
    & $shell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $driver -Action $Action -GameDirectory $gameDirectory -OutputDirectory $OutputDirectory -StoreDirectory $storeDirectory
    if ($LASTEXITCODE -ne 0) { throw ('Qualified deployment driver failed: ' + $Action) }
}

$mutex = [Threading.Mutex]::new($false, 'Local\ReactorV.ControllerCapturePreparation')
$ownsMutex = $false
$children = @(); $manifest = $null
try {
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another test launcher is preparing a session; no changes made.' }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path (Get-CollectorDataRoot) ('controller-hang-test6-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
    }
    $OutputDirectory = Assert-CollectorOutput $OutputDirectory
    if (-not $OutputDirectory.StartsWith((Get-CollectorDataRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Live desktop capture requires the shared diagnostics root, outside AppData.' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; no existing test output is overwritten.' }
    $runId = [Guid]::NewGuid()
    if (-not $PreflightOnly) {
        [void][IO.Directory]::CreateDirectory($OutputDirectory)
        $manifest = [ordered]@{schema=1;runId=$runId.ToString('N');offline=$false;createdUtc=[DateTime]::UtcNow.ToString('o');
            parentPid=$PID;parentJobFlags=('0x{0:X}' -f [ReactorV.Diagnostics.ProcessLifetime]::CurrentJobFlags());
            noGameLaunch=$true;gameDirectory=$gameDirectory;mode='native-off-windowed';maximumDumps=6;collectorDeadlineHours=6;
            runtimeSha256='5359dc1e4cea9225288a7057dc934f985608774ae3fd55ddff78fb663c53ed60';collectorPath=$collector;
            state='preflight';collectors=@();restoreDriver=$driver;restoreOutputDirectory=$OutputDirectory;storeDirectory=$storeDirectory}
        # Persist dependency/preflight failures too, before any installation change.
        Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    }
    Require-GameStopped
    if (Get-Process -Name procdump64 -ErrorAction SilentlyContinue) { throw 'An existing ProcDump capture is running; refusing to overlap it.' }
    if (-not [IO.File]::Exists($collector)) { throw ('Verified ProcDump dependency is missing: ' + $collector + '. No installation changed.') }
    if ((Get-FileHash -LiteralPath $collector).Hash -ine $collectorHash -or
        (Get-AuthenticodeSignature -LiteralPath $collector).Status -ne 'Valid') { throw 'ProcDump identity/signature mismatch.' }
    if ((Get-PSDrive C).Free -lt 4GB) { throw 'At least 4 GiB of free local space is required for this diagnostic.' }
    Invoke-Driver 'Preflight'
    if ($PreflightOnly) {
        Write-Output 'PREFLIGHT PASSED. No installation or collectors were started.'
        return
    }
    $manifest.state='starting'
    Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    foreach ($spec in @(@{role='hang';file=$watcher},@{role='session';file=$driver})) {
        # The hang observer must be independently polling before any installation
        # is changed. The existing session driver owns backup/prepare/capture.
        Require-GameStopped
        $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',$spec.file,
            '-OutputDirectory',$OutputDirectory,'-RunId',$runId.ToString('N'))
        if ($spec.role -eq 'session') { $arguments += @('-Action','PrepareAndCapture','-GameDirectory',$gameDirectory,'-StoreDirectory',$storeDirectory) }
        $child = [ReactorV.Diagnostics.ProcessLifetime]::StartDetached($shell,$arguments,$root)
        $children += $child
        $entry = [ordered]@{role=$spec.role;pid=$child.Id;processStartTicks=$child.StartTime.ToUniversalTime().Ticks;
            executable=$shell;inJob=[ReactorV.Diagnostics.ProcessLifetime]::InJob($child);script=$spec.file;
            scriptSha256=(Get-FileHash -LiteralPath $spec.file).Hash.ToLowerInvariant()}
        $manifest.collectors += $entry
        Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
        $wait = [Diagnostics.Stopwatch]::StartNew()
        do {
            $health = Get-CollectorHealth $OutputDirectory $spec.role $runId
            if ($health.healthy -and $health.readyForGame) { break }
            if ($child.HasExited) { throw ($spec.role + ' collector exited during setup, code ' + $child.ExitCode + '. Inspect its status and Windows PowerShell events.') }
            if ($health.reason -like 'terminal-*') { throw ($spec.role + ' collector stopped during setup: ' + $health.reason) }
            Start-Sleep -Milliseconds 250
        } while ($wait.Elapsed.TotalSeconds -lt 45)
        if (-not $health.healthy -or -not $health.readyForGame) { throw ($spec.role + ' collector readiness deadline exceeded.') }
        Confirm-CollectorProgress $OutputDirectory $spec.role $runId -RequireGameReady | Out-Null
    }
    Require-GameStopped
    # This checks both identities, both script hashes, job independence and two
    # advancing ready-for-game samples. A one-time "armed" file is insufficient.
    $gate = & (Join-Path $PSScriptRoot 'Test-CollectorPair.ps1') -OutputDirectory $OutputDirectory | ConvertFrom-Json
    if (-not $gate.readyForGame) { throw 'Pair readiness gate did not pass.' }
    Write-CollectorJson (Join-Path $OutputDirectory 'prelaunch-check.json') $gate
    $manifest.state='ready-for-user-launch'; $manifest.readyUtc=[DateTime]::UtcNow.ToString('o')
    Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
    Write-Host ''
    Write-Host 'READY - diagnostic installation prepared; BOTH collectors are independently polling.' -ForegroundColor Green
    Write-Host ('Evidence/backups: ' + $OutputDirectory)
    Write-Host 'Launch GTA Enhanced into STORY MODE as usual, try GBay once, and stay in game for at least two minutes.'
    Write-Host 'Then exit GTA normally and wait three minutes for post-exit observation. Report whether GBay appeared or the game crashed.'
    Write-Host 'Memory snapshots stay local and may contain private data. Do not upload the entire output folder.'
    Write-Host 'Collectors stop after this session (six-hour maximum). The test installation remains until explicitly restored.'
} catch {
    $failure = $_.Exception.Message
    if ($manifest) {
        $manifest.state='failed'; $manifest.error=$failure
        foreach ($entry in $manifest.collectors) {
            [IO.File]::WriteAllText((Join-Path $OutputDirectory ($entry.role + '-stop-' + $manifest.runId + '.request')), 'Stop after setup failure.')
        }
        # Do not restore files while another collector may still be preparing or
        # collecting. Stop cooperatively only; never force-kill game/collectors.
        $stopWait=[Diagnostics.Stopwatch]::StartNew()
        do {
            $stillRunning=@($children | Where-Object { -not $_.HasExited })
            if ($stillRunning.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        } while ($stopWait.Elapsed.TotalSeconds -lt 30)
        $receiptPath=Join-Path $OutputDirectory 'runtime-install-receipt.json'
        if ($stillRunning.Count -gt 0) { $manifest.recovery='needs-attention: collectors have not stopped; no concurrent restore attempted' }
        elseif (Test-Path -LiteralPath $receiptPath) {
            try { Require-GameStopped; Invoke-Driver 'Restore'; $manifest.recovery='restored-after-setup-failure' }
            catch { $manifest.recovery='needs-attention: ' + $_.Exception.Message }
        } else { $manifest.recovery='no-installation-receipt; no restoration attempted' }
        Write-CollectorJson (Join-Path $OutputDirectory 'launch.json') $manifest
        Write-Host ('Setup failed. Evidence/recovery state: ' + $OutputDirectory) -ForegroundColor Red
        Write-Host $manifest.recovery
    }
    Write-Error $failure -ErrorAction Continue
    Write-Host 'NOT READY. Do not start a gameplay test from this failed preparation.' -ForegroundColor Red
    exit 1
} finally {
    foreach ($child in $children) { $child.Dispose() }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
