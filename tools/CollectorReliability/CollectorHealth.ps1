# Dot-source from Windows PowerShell 5.1. Heartbeats come from the actual polling
# loop, never an independent timer that could conceal a wedged collector.
foreach ($healthModule in @('Microsoft.PowerShell.Utility','Microsoft.PowerShell.Management','Microsoft.PowerShell.Security')) {
    Import-Module (Join-Path $PSHOME ('Modules\' + $healthModule + '\' + $healthModule + '.psd1'))
}

function Get-CollectorDataRoot {
    # New diagnostics must be visible to both packaged and ordinary desktop
    # processes. AppData writes can be redirected into a private MSIX cache.
    return (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'ReactorV-Diagnostics')
}

function Get-CollectorToolPath {
    return (Join-Path (Get-CollectorDataRoot) 'tools\procdump-12.01\procdump64.exe')
}

function Assert-CollectorOutput([string]$Directory) {
    $resolved = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $roots = @((Get-CollectorDataRoot), (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ReactorV-Diagnostics'))
    $withinRoot = $false
    foreach ($root in $roots) {
        if ($resolved.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { $withinRoot=$true }
    }
    if (-not $withinRoot) {
        throw 'Collector output must be a subdirectory of the shared diagnostics root or the legacy local diagnostics root.'
    }
    $node = $resolved
    while ($node) {
        if ([IO.Directory]::Exists($node) -and ([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Collector output cannot traverse a reparse point.'
        }
        $node = [IO.Path]::GetDirectoryName($node)
    }
    return $resolved
}

function Write-CollectorJson([string]$Path, $Value) {
    if ([IO.File]::Exists($Path) -and ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing reparse-point output.' }
    $temp = $Path + '.new-' + [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp, $Path) }
}

function New-CollectorHealth([string]$Directory, [ValidateSet('hang','session','fixture')][string]$Role, [Guid]$RunId, [bool]$Offline = $false) {
    if ($RunId -eq [Guid]::Empty) { throw 'A nonempty run identity is required.' }
    $directory = Assert-CollectorOutput $Directory
    [void][IO.Directory]::CreateDirectory($directory)
    $path = Join-Path $directory ($Role + '-heartbeat.json')
    # Exclusive claim prevents two collectors from overwriting each other's signal.
    $claim = [IO.File]::Open(($path + '.claim'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $claim.Dispose()
    if ([IO.File]::Exists($path)) { throw 'Existing heartbeat; choose a new output directory.' }
    $process = [Diagnostics.Process]::GetCurrentProcess()
    try { $start = $process.StartTime.ToUniversalTime().Ticks; $exe = $process.MainModule.FileName }
    finally { $process.Dispose() }
    $context = [pscustomobject]@{
        Path=$path; StopPath=(Join-Path $directory ($Role + '-stop-' + $RunId.ToString('N') + '.request'))
        Record=[ordered]@{schema=1;runId=$RunId.ToString('N');role=$Role;observerPid=$PID;processStartTicks=$start;
            executable=$exe;sequence=0L;updatedUtc=$null;state='starting';readyForGame=$false;offline=$Offline;message='Starting.'}
    }
    Update-CollectorHealth $context 'starting' 'Starting.'
    return $context
}

function Update-CollectorHealth($Context, [ValidateSet('starting','waiting','observing','capturing','complete','error','cancelled','probe-complete')][string]$State,
    [string]$Message, [bool]$ReadyForGame = $false) {
    $Context.Record.sequence++
    $Context.Record.updatedUtc = [DateTime]::UtcNow.ToString('o')
    $Context.Record.state = $State
    $Context.Record.readyForGame = $ReadyForGame -and -not $Context.Record.offline -and $State -eq 'waiting'
    $Context.Record.message = $Message
    Write-CollectorJson $Context.Path $Context.Record
}

function Test-CollectorStop($Context) { return [IO.File]::Exists($Context.StopPath) }

function Get-CollectorHealth([string]$Directory, [ValidateSet('hang','session','fixture')][string]$Role, [Guid]$RunId,
    [double]$MaxAgeSeconds = 5) {
    $result = [ordered]@{healthy=$false;readyForGame=$false;reason='unavailable';record=$null}
    $process = $null
    try {
        $path = Join-Path (Assert-CollectorOutput $Directory) ($Role + '-heartbeat.json')
        if ((Get-Item -LiteralPath $path).Length -gt 64KB) { throw 'Oversized heartbeat.' }
        $r = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        $result.record = $r
        if ($r.schema -ne 1 -or $r.runId -cne $RunId.ToString('N') -or $r.role -cne $Role -or $r.sequence -lt 1) { throw 'Identity/schema mismatch.' }
        if ($r.state -in @('complete','error','cancelled','probe-complete')) { $result.reason='terminal-' + $r.state; return [pscustomobject]$result }
        if ($r.state -notin @('starting','waiting','observing','capturing')) { throw 'Unknown state.' }
        $age = ([DateTime]::UtcNow - [DateTime]::Parse($r.updatedUtc).ToUniversalTime()).TotalSeconds
        if ($age -lt -1 -or $age -gt $MaxAgeSeconds) { $result.reason='stale-heartbeat'; return [pscustomobject]$result }
        $process = [Diagnostics.Process]::GetProcessById([int]$r.observerPid)
        [void]$process.Handle
        if ($process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$r.processStartTicks -or
            $process.MainModule.FileName -ine $r.executable) { throw 'Process identity mismatch.' }
        $result.healthy = $true; $result.reason='live-and-fresh'
        $result.readyForGame = $r.state -eq 'waiting' -and $r.readyForGame -eq $true -and $r.offline -eq $false
    } catch { $result.reason='unavailable-or-identity-mismatch' }
    finally { if ($process) { $process.Dispose() } }
    return [pscustomobject]$result
}

function Confirm-CollectorProgress([string]$Directory, [string]$Role, [Guid]$RunId, [switch]$RequireGameReady) {
    $before = Get-CollectorHealth $Directory $Role $RunId
    if (-not $before.healthy) { throw ('Collector not healthy: ' + $before.reason) }
    Start-Sleep -Milliseconds 2200
    $after = Get-CollectorHealth $Directory $Role $RunId
    if (-not $after.healthy -or $after.record.sequence -le $before.record.sequence) { throw 'Collector heartbeat is not advancing.' }
    if ($RequireGameReady -and -not $after.readyForGame) { throw 'Collector is not ready for a game session.' }
    return $after
}

function Invoke-CollectorOfflineProbe([string]$Directory, [string]$Role, [Guid]$RunId, [ValidateRange(5,900)][int]$Seconds) {
    $health = New-CollectorHealth $Directory $Role $RunId $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-CollectorStop $health) { Update-CollectorHealth $health 'cancelled' 'Offline probe stopped cooperatively.'; return }
        Update-CollectorHealth $health 'waiting' 'Offline heartbeat probe only; no game observation or capture.'
        Start-Sleep -Milliseconds 500
    }
    Update-CollectorHealth $health 'probe-complete' 'Bounded offline heartbeat probe completed.'
}
