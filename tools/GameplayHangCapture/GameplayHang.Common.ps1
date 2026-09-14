# Read-only identity/readiness and bounded dump validation shared by watcher/tests.
function Test-GameplayReadyLine([string]$Line, [int]$TargetId, [DateTime]$BornUtc) {
    if ($Line -notmatch '^(?<stamp>\d{4}-\d\d-\d\dT\S+Z) .*\bpid=(?<target>\d+) .*\bsource=script stage=diagnostic_tick_heartbeat .*\bstory_ready=True playable=True browser_ready=True(?:\s|$)') { return $false }
    $parsedTarget=0
    if (-not [int]::TryParse($matches.target,[ref]$parsedTarget) -or $parsedTarget -ne $TargetId) { return $false }
    try { $stamp=[DateTime]::Parse($matches.stamp).ToUniversalTime() } catch { return $false }
    foreach ($field in @('pid','source','stage','story_ready','playable','browser_ready')) {
        if ([regex]::Matches($Line, ('(?:^|\s)'+$field+'=')).Count -ne 1) { return $false }
    }
    return $stamp -ge $BornUtc -and $stamp -le [DateTime]::UtcNow.AddSeconds(5)
}

function Read-GameplayTail([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return '' }
    if ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point input refused.' }
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try {
        $offset=[Math]::Max(0,$stream.Length-131072)
        [void]$stream.Seek($offset,[IO.SeekOrigin]::Begin)
        $reader=[IO.StreamReader]::new($stream)
        try {
            if ($offset -gt 0) { [void]$reader.ReadLine() }
            return $reader.ReadToEnd().Replace([string][char]0,'')
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Test-GameplayIdentity([string]$ExpectedPath,[long]$ExpectedTicks,[string]$ActualPath,[long]$ActualTicks) {
    return $ExpectedTicks -gt 0 -and $ExpectedTicks -eq $ActualTicks -and
        [string]::Equals($ExpectedPath,$ActualPath,[StringComparison]::OrdinalIgnoreCase)
}

function Test-GameplaySpace([long]$AvailableBytes) { return $AvailableBytes -ge 2GB }

function Get-GameplayCollectors {
    # Target-wide -cancel also reaches another architecture's ProcDump instance.
    Get-Process -Name procdump,procdump64,procdump64a -ErrorAction SilentlyContinue
}

function Assert-GameplayCollectorAvailable {
    $path=Get-CollectorToolPath
    $hash='d1fc99ae304bd1d2bf28abeb62531da959e2431916194981b88c958fd713a8e6'
    if ((Get-FileHash -LiteralPath $path).Hash -ine $hash -or (Get-AuthenticodeSignature -LiteralPath $path).Status -ne 'Valid') { throw 'ProcDump hash/signature mismatch.' }
    if ((Get-ItemProperty -LiteralPath 'HKCU:\Software\Sysinternals\ProcDump' -Name EulaAccepted -ErrorAction SilentlyContinue).EulaAccepted -ne 1) { throw 'ProcDump license must already be accepted; watcher does not change licensing state.' }
    if (Get-GameplayCollectors) { throw 'Another ProcDump is active; refusing overlap.' }
    if (-not (Test-GameplaySpace (Get-PSDrive C).Free)) { throw 'At least 2 GiB free space is required.' }
}

function Read-GameplayDump([string]$Path,[int]$ExpectedPid) {
    if (-not [IO.File]::Exists($Path)) { return $null }
    $stream=[IO.File]::OpenRead($Path); $reader=[IO.BinaryReader]::new($stream)
    try {
        $length=$stream.Length
        if ($length -lt 32 -or $length -gt 512MB) { throw 'Dump size outside the accepted range.' }
        if ($reader.ReadUInt32() -ne 0x504d444d) { throw 'Invalid minidump signature.' }
        [void]$reader.ReadUInt32()
        $count=$reader.ReadUInt32(); $directory=$reader.ReadUInt32()
        [void]$stream.Seek(24,[IO.SeekOrigin]::Begin); $flags=$reader.ReadUInt64()
        if (($flags -band 2) -ne 0 -or ($flags -band 0x1824) -ne 0x1824) { throw 'Unexpected/full-memory dump flags.' }
        if ($count -lt 1 -or $count -gt 128 -or [long]$directory+12L*$count -gt $length) { throw 'Invalid dump directory.' }
        $entries=@{}
        for ($i=0;$i -lt $count;$i++) {
            [void]$stream.Seek([long]$directory+12L*$i,[IO.SeekOrigin]::Begin)
            $type=$reader.ReadUInt32(); $size=$reader.ReadUInt32(); $rva=$reader.ReadUInt32()
            # ProcDump 12.01 pads its directory with all-zero UnusedStream slots.
            if ($type -eq 0 -and $size -eq 0 -and $rva -eq 0) { continue }
            if ([long]$rva+$size -gt $length -or $entries.ContainsKey([string]$type)) { throw 'Invalid or duplicate dump stream.' }
            $entries[[string]$type]=@{size=[long]$size;rva=[long]$rva}
        }
        foreach ($required in @('3','4','15','17')) { if (-not $entries.ContainsKey($required)) { throw ('Missing dump stream: '+$required) } }
        $misc=$entries['15']; if ($misc.size -lt 12) { throw 'Short process-info stream.' }
        [void]$stream.Seek($misc.rva+4,[IO.SeekOrigin]::Begin)
        $miscFlags=$reader.ReadUInt32(); $dumpPid=$reader.ReadUInt32()
        if (($miscFlags -band 1) -eq 0 -or $dumpPid -ne $ExpectedPid) { throw ('Dump process identity mismatch: '+$dumpPid) }
        $threads=$entries['3']; if ($threads.size -lt 4) { throw 'Short thread-list stream.' }
        [void]$stream.Seek($threads.rva,[IO.SeekOrigin]::Begin); $threadCount=$reader.ReadUInt32()
        if ($threadCount -lt 1 -or $threadCount -gt 8192 -or 4L+48L*$threadCount -gt $threads.size) { throw 'Invalid thread list.' }
        $withStack=0; $withContext=0
        for ($i=0;$i -lt $threadCount;$i++) {
            [void]$stream.Seek($threads.rva+4+48L*$i+32,[IO.SeekOrigin]::Begin)
            $stackSize=$reader.ReadUInt32(); $stackRva=$reader.ReadUInt32()
            $contextSize=$reader.ReadUInt32(); $contextRva=$reader.ReadUInt32()
            if ([long]$stackRva+$stackSize -gt $length -or [long]$contextRva+$contextSize -gt $length) { throw 'Invalid stack/context bounds.' }
            if ($stackSize -gt 0) { $withStack++ }; if ($contextSize -gt 0) { $withContext++ }
        }
        $modules=$entries['4']; if ($modules.size -lt 4) { throw 'Short module-list stream.' }
        [void]$stream.Seek($modules.rva,[IO.SeekOrigin]::Begin); $moduleCount=$reader.ReadUInt32()
        if ($moduleCount -lt 1 -or 4L+108L*$moduleCount -gt $modules.size -or $withStack -eq 0 -or $withContext -eq 0) { throw 'Missing usable stack/context/module evidence.' }
        return [pscustomobject]@{bytes=$length;pid=$dumpPid;flags=('0x{0:X}' -f $flags);threads=$threadCount;threadsWithStacks=$withStack;threadsWithContexts=$withContext;modules=$moduleCount;sha256=(Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()}
    } finally { $reader.Dispose(); $stream.Dispose() }
}
