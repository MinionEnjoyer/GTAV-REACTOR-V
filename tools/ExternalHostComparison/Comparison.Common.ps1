# Windows PowerShell 5.1 diagnostic helpers; no installation or process launch.
function Get-ComparisonHash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Read-ComparisonText([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return '' }
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($stream.Length -gt 16MB) { throw 'Diagnostic log exceeds the 16 MiB bound.' }
        $reader=[IO.StreamReader]::new($stream)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
function Get-ComparisonFlag([string]$Command,[string]$Name) {
    $m=[regex]::Matches($Command,'(?:^|\s)--'+[regex]::Escape($Name)+'=(?:"([^"]*)"|(\S+))(?=\s|$)')
    if ($m.Count -ne 1) { return $null }
    if ($m[0].Groups[1].Success) { return $m[0].Groups[1].Value }
    $m[0].Groups[2].Value
}
function Test-ComparisonProfile([string]$Command,[string]$Profile) {
    (Get-ComparisonFlag $Command 'user-data-dir') -ieq (Join-Path $Profile 'EBWebView')
}
function Test-ComparisonRoute([string]$HostTrace,[string]$TargetTrace,[int]$TargetId,[int]$HostId) {
    $targetLines=@($TargetTrace -split '[\r\n]+' | Where-Object { $_ -match ('(?:^|\s)pid='+$TargetId+'\s') }) -join "`n"
    $reasons=[Collections.Generic.List[string]]::new()
    if ($targetLines -match 'stage=bootstrap_host_fallback |stage=webview_controller_request_begin ') { $reasons.Add('in-process-fallback') }
    if ($HostTrace -match 'stage=(browser_failed|webview_failed|webview_initialization_failed|webview_controller_deadline_elapsed) ') { $reasons.Add('external-host-startup-failed') }
    $attached=$targetLines -match ('stage=bootstrap_host_attached pid='+$TargetId+' host_pid='+$HostId+' ')
    $client=$HostTrace -match ('stage=bootstrap_host_client_connected pid='+$TargetId+'(?:\s|$)')
    [pscustomobject]@{attached=($attached -and $client);failures=@($reasons.ToArray());targetTrace=$targetLines}
}
function Test-ComparisonEvidence($Records,[bool]$Attached,[string[]]$Failures) {
    $helpers=@($Records|Where-Object role -eq 'desktop-probe')
    $records=@($Records|Where-Object role -ne 'desktop-probe'); $roots=@($records|Where-Object role -eq 'browser'); $hosts=@($records|Where-Object role -eq 'host')
    $browsers=@($records|Where-Object { $_.role -notin @('host','target') })
    $isolated=@($hosts)+$browsers
    $checks=[ordered]@{
        attachedToExactHost=$Attached
        noDisqualifiers=(@($Failures).Count -eq 0)
        oneHost=($hosts.Count -eq 1)
        oneTarget=(@($records|Where-Object role -eq 'target').Count -eq 1)
        oneBrowserRoot=($roots.Count -eq 1)
        rendererObserved=(@($browsers|Where-Object role -eq 'renderer').Count -gt 0)
        completeModuleCoverage=(@($records|Where-Object { $_.moduleSamples -lt 3 }).Count -eq 0)
        noModuleReadFailures=(@($records|Where-Object { $_.moduleFailures -gt 0 }).Count -eq 0)
        noOverlayInHostOrBrowser=(@($isolated|Where-Object { $_.modules.Keys -contains 'gameoverlayrenderer64.dll' }).Count -eq 0)
        noNativeOrCef=(@($records|Where-Object { $_.modules.Keys -contains 'ragewebui.native.dll' -or $_.modules.Keys -contains 'libcef.dll' }).Count -eq 0)
        allObservedExitedZero=(@($records|Where-Object { $null -eq $_.exitCode -or $_.exitCode -cne 0 }).Count -eq 0)
        # A helper is not a second host and cannot satisfy browser coverage.
        # A deadline-terminated helper is still NOT a clean-exit success.
        observedProbeHelpersAuthenticated=(@($helpers|Where-Object { $_.identityVerified -ne $true }).Count -eq 0)
        observedProbeHelpersExitedZero=(@($helpers|Where-Object { $null -eq $_.exitCode -or $_.exitCode -cne 0 }).Count -eq 0)
        noForbiddenModulesInObservedHelpers=(@($helpers|Where-Object { $_.modules.Keys -contains 'gameoverlayrenderer64.dll' -or $_.modules.Keys -contains 'ragewebui.native.dll' -or $_.modules.Keys -contains 'libcef.dll' }).Count -eq 0)
    }
    [pscustomobject]@{qualified=(@($checks.Values|Where-Object { $_ -ne $true }).Count -eq 0);checks=$checks}
}
function New-ComparisonTracker([string]$EventPath) {
    $writer=[IO.StreamWriter]::new([IO.File]::Open($EventPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read),[Text.UTF8Encoding]::new($false))
    $writer.AutoFlush=$true
    @{records=@{};processes=@{};writer=$writer;linkFailures=0;unexpectedPreloaders=@{};expectedPreloaderPath='';expectedPreloaderHash=''}
}
function Write-ComparisonEvent($Tracker,[string]$Name,$Data) {
    $Tracker.writer.WriteLine((@{utc=[DateTime]::UtcNow.ToString('o');event=$Name;data=$Data}|ConvertTo-Json -Depth 6 -Compress))
}
function Add-ComparisonProcess($Tracker,[Diagnostics.Process]$Process,[string]$Role,[int]$ParentId) {
    [void]$Process.Handle
    $key=[string]$Process.Id
    if ($Tracker.records.ContainsKey($key)) { throw 'Duplicate PID in this session; refusing ambiguous process reuse.' }
    $Tracker.records[$key]=[ordered]@{pid=$Process.Id;createdUtc=$Process.StartTime.ToUniversalTime().ToString('o');parentPid=$ParentId;role=$Role;exe=$Process.MainModule.FileName;version=$Process.MainModule.FileVersionInfo.FileVersion;moduleSamples=0;moduleFailures=0;modules=@{};exitCode=$null;exitUtc=$null}
    $Tracker.processes[$key]=$Process
    Write-ComparisonEvent $Tracker 'linked' $Tracker.records[$key]
}
function Test-ComparisonProbeIdentity($Candidate,$Observed,$HostIdentity,[string]$ExpectedPath,[string]$ExpectedHash) {
    # CLI role alone is never authority. Require the retained live host identity,
    # a matching OS creation time/path, and the exact prepared executable bytes.
    if (-not $HostIdentity.alive -or $Candidate.ParentProcessId -ne $HostIdentity.pid -or
        $Candidate.ProcessId -eq $HostIdentity.pid -or $Candidate.ProcessId -ne $Observed.pid) { return $false }
    if (-not $ExpectedPath -or -not $ExpectedHash -or
        $Candidate.ExecutablePath -ine $ExpectedPath -or $Observed.path -ine $ExpectedPath -or
        $Observed.sha256 -cne $ExpectedHash) { return $false }
    if ([Math]::Abs(($Observed.createdUtc-$Candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 10 -or
        $Observed.createdUtc -lt $HostIdentity.createdUtc) { return $false }
    $match=[regex]::Match($Candidate.CommandLine,'^(?:"([^"]+)"|(\S+))\s+--desktop-presentation-probe\s+([A-Za-z0-9+/]{2,65534}={0,2})$')
    if (-not $match.Success) { return $false }
    $commandPath=if($match.Groups[1].Success){$match.Groups[1].Value}else{$match.Groups[2].Value}
    if ($commandPath -ine $ExpectedPath) { return $false }
    try { [void][Convert]::FromBase64String($match.Groups[3].Value) } catch { return $false }
    return $true
}
function Update-ComparisonPreloaders($Tracker,[Diagnostics.Process]$HostProcess) {
    foreach ($candidate in @(Get-CimInstance Win32_Process -Filter "Name='ReactorV.Preloader.exe'")) {
        $key=[string]$candidate.ProcessId
        if ($candidate.ProcessId -eq $HostProcess.Id) {
            if ([Math]::Abs(($HostProcess.StartTime.ToUniversalTime()-$candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -le 10) { continue }
            $Tracker.unexpectedPreloaders[$key]='host-pid-reused'
            Write-ComparisonEvent $Tracker 'unclassified-preloader' @{pid=$candidate.ProcessId;parentPid=$candidate.ParentProcessId;reason='host-pid-reused'}
            continue
        }
        if ($Tracker.records.ContainsKey($key)) {
            if ([Math]::Abs(([DateTime]::Parse($Tracker.records[$key].createdUtc).ToUniversalTime()-$candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -le 10) { continue }
            $Tracker.unexpectedPreloaders[$key]='pid-reused'
            Write-ComparisonEvent $Tracker 'unclassified-preloader' @{pid=$candidate.ProcessId;parentPid=$candidate.ParentProcessId;reason='pid-reused'}
            continue
        }
        if ($Tracker.unexpectedPreloaders.ContainsKey($key)) { continue }
        $process=$null
        try {
            $process=[Diagnostics.Process]::GetProcessById($candidate.ProcessId); [void]$process.Handle
            $hostIdentity=@{pid=$HostProcess.Id;createdUtc=$HostProcess.StartTime.ToUniversalTime();alive=(-not $HostProcess.HasExited)}
            $observed=@{pid=$process.Id;createdUtc=$process.StartTime.ToUniversalTime();path=$process.MainModule.FileName;sha256=''}
            if ($observed.path -ieq $Tracker.expectedPreloaderPath) { $observed.sha256=Get-ComparisonHash $observed.path }
            if (-not (Test-ComparisonProbeIdentity $candidate $observed $hostIdentity $Tracker.expectedPreloaderPath $Tracker.expectedPreloaderHash)) { throw 'identity-or-role-mismatch' }
            Add-ComparisonProcess $Tracker $process 'desktop-probe' $candidate.ParentProcessId
            $Tracker.records[$key].identityVerified=$true
            $Tracker.records[$key].sha256=$observed.sha256
            Write-ComparisonEvent $Tracker 'desktop-probe-authenticated' @{pid=$process.Id;parentPid=$candidate.ParentProcessId;createdUtc=$Tracker.records[$key].createdUtc;sha256=$observed.sha256}
            $process=$null # retained by tracker until final exit sampling
        } catch {
            $Tracker.unexpectedPreloaders[$key]='identity-unavailable-or-mismatch'
            Write-ComparisonEvent $Tracker 'unclassified-preloader' @{pid=$candidate.ProcessId;parentPid=$candidate.ParentProcessId;createdUtc=$candidate.CreationDate.ToUniversalTime().ToString('o');reason='identity-unavailable-or-mismatch';error=$_.Exception.GetType().Name}
        } finally { if ($process) { $process.Dispose() } }
    }
}
function Update-ComparisonTracker($Tracker,[Diagnostics.Process]$HostProcess,[string]$Profile) {
    if ($HostProcess) {
        Update-ComparisonPreloaders $Tracker $HostProcess
        $candidates=@(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'")
        foreach ($pass in 1..2) { foreach ($candidate in $candidates) {
            $key=[string]$candidate.ProcessId; if ($Tracker.records.ContainsKey($key)) { continue }
            $parent=[string]$candidate.ParentProcessId
            if (-not $Tracker.processes.ContainsKey($parent) -or $Tracker.processes[$parent].HasExited -or $Tracker.records[$parent].role -in @('target','desktop-probe')) { continue }
            # No duplicate user-data flags accepted, even on a child process.
            $hasProfile=$candidate.CommandLine -match '(?:^|\s)--user-data-dir='
            if ($hasProfile -and -not (Test-ComparisonProfile $candidate.CommandLine $Profile)) { continue }
            if ($candidate.ParentProcessId -eq $HostProcess.Id -and
                (-not (Test-ComparisonProfile $candidate.CommandLine $Profile) -or
                 (Get-ComparisonFlag $candidate.CommandLine 'webview-exe-name') -cne 'ReactorV.Preloader.exe')) { continue }
            $process=$null
            try {
                $process=[Diagnostics.Process]::GetProcessById($candidate.ProcessId); [void]$process.Handle
                if ([Math]::Abs(($process.StartTime.ToUniversalTime()-$candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 10 -or
                    $process.StartTime.ToUniversalTime() -lt $Tracker.processes[$parent].StartTime.ToUniversalTime()) { throw 'Process creation identity mismatch.' }
                $role=Get-ComparisonFlag $candidate.CommandLine 'type'; if (-not $role) { $role='browser' }
                if ($role -notmatch '^[a-zA-Z0-9_.-]{1,100}$') { throw 'Invalid browser role.' }
                Add-ComparisonProcess $Tracker $process $role $candidate.ParentProcessId
            } catch {
                if ($process) { $process.Dispose() }; $Tracker.linkFailures++
                Write-ComparisonEvent $Tracker 'link-unavailable' @{pid=$candidate.ProcessId;error=$_.Exception.GetType().Name}
            }
        } }
    }
    foreach ($key in @($Tracker.processes.Keys)) {
        $process=$Tracker.processes[$key]; $record=$Tracker.records[$key]; $process.Refresh()
        if ($process.HasExited) {
            if ($null -eq $record.exitCode) { $record.exitCode=$process.ExitCode; $record.exitUtc=[DateTime]::UtcNow.ToString('o'); Write-ComparisonEvent $Tracker 'exited' @{pid=$process.Id;role=$record.role;exitCode=$record.exitCode} }
            continue
        }
        try {
            $modules=@($process.Modules); if (-not $modules.Count) { throw 'Empty modules.' }
            foreach ($module in $modules) { $record.modules[$module.ModuleName.ToLowerInvariant()]=$true }
            $record.moduleSamples++
        } catch { $record.moduleFailures++ }
    }
}
