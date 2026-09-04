param(
    [Parameter(Mandatory)] [string]$Harness,
    [Parameter(Mandatory)] [string]$Preloader,
    [Parameter(Mandatory)] [string]$UiDirectory,
    [Parameter(Mandatory)] [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($requiredFile in @($Harness, $Preloader)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "External-GPU qualification input is missing: $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $UiDirectory 'index.html') -PathType Leaf)) {
    throw "External-GPU qualification UI is missing: $UiDirectory"
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

function Read-SharedText {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Open-CoordinationEvent {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [Diagnostics.Process]$Owner,
        [int]$TimeoutMilliseconds = 5000
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        try { return [Threading.EventWaitHandle]::OpenExisting($Name) }
        catch [Threading.WaitHandleCannotBeOpenedException] {
            $Owner.Refresh()
            if ($Owner.HasExited) {
                throw "The consumer exited before publishing '$Name'."
            }
            Start-Sleep -Milliseconds 25
        }
    }
    throw "The consumer did not publish '$Name' within $TimeoutMilliseconds ms."
}

function Assert-OrderedStages {
    param(
        [Parameter(Mandatory)] [string]$Trace,
        [Parameter(Mandatory)] [string[]]$Stages
    )

    $cursor = -1
    foreach ($stage in $Stages) {
        $cursor = $Trace.IndexOf(
            "stage=$stage",
            $cursor + 1,
            [StringComparison]::Ordinal)
        if ($cursor -lt 0) {
            throw "External-GPU trace omitted or reordered stage '$stage'."
        }
    }
}

function Invoke-ApiQualification {
    param([Parameter(Mandatory)] [ValidateSet('d3d11', 'd3d12')] [string]$Api)

    $runRoot = Join-Path $OutputRoot $Api
    if (Test-Path -LiteralPath $runRoot) {
        throw "Refusing to reuse a stale external-GPU qualification directory: $runRoot"
    }
    $logDirectory = Join-Path $runRoot 'Logs'
    $profileDirectory = Join-Path $runRoot 'BrowserProfile'
    New-Item -ItemType Directory -Path $runRoot,$logDirectory,$profileDirectory -Force |
        Out-Null
    $stdoutPath = Join-Path $runRoot 'consumer.stdout.log'
    $stderrPath = Join-Path $runRoot 'consumer.stderr.log'

    $consumer = $null
    $preloaderProcess = $null
    $frameReady = $null
    $teardownComplete = $null
    $hostClose = $null
    $selfTestStop = $null
    $teardownSignaled = $false
    try {
        $consumer = Start-Process `
            -FilePath $Harness `
            -ArgumentList @(
                '--scenario', 'external-gpu-consumer',
                '--api', $Api,
                '--duration', '20'
            ) `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru `
            -WindowStyle Hidden
        $null = $consumer.Handle

        $frameReady = Open-CoordinationEvent `
            -Name "Local\ReactorV.ExternalGpuConsumerHarness.FrameReady.$($consumer.Id)" `
            -Owner $consumer
        $teardownComplete = Open-CoordinationEvent `
            -Name "Local\ReactorV.ExternalGpuConsumerHarness.TeardownComplete.$($consumer.Id)" `
            -Owner $consumer

        $preloaderProcess = Start-Process `
            -FilePath $Preloader `
            -ArgumentList @(
                '--self-test',
                '--persistent-host',
                '--parent-pid', "$($consumer.Id)",
                '--external-gpu-browser-shadow',
                '--ui-dir', ('"{0}"' -f $UiDirectory),
                '--user-data-dir', ('"{0}"' -f $profileDirectory),
                '--log-dir', ('"{0}"' -f $logDirectory),
                '--instance-id', "external-gpu-$Api-$([Guid]::NewGuid().ToString('N'))"
            ) `
            -PassThru `
            -WindowStyle Hidden
        $null = $preloaderProcess.Handle
        $hostClose = Open-CoordinationEvent `
            -Name "Local\ReactorV.BootstrapHostClose.$($consumer.Id)" `
            -Owner $preloaderProcess
        $selfTestStop = Open-CoordinationEvent `
            -Name "Local\ReactorV.Preloader.SelfTestStop.$($preloaderProcess.Id)" `
            -Owner $preloaderProcess

        if (-not $frameReady.WaitOne(15000)) {
            throw "The packaged $Api producer did not render a shared frame within 15000 ms."
        }

        # Rendering may lead the managed ContentReady callback by a few
        # scheduler turns. Require the host trace before graceful teardown.
        $tracePath = Join-Path $logDirectory 'reactorv-preloader.log'
        $trace = ''
        $readyTimer = [Diagnostics.Stopwatch]::StartNew()
        while ($readyTimer.ElapsedMilliseconds -lt 2500) {
            $trace = Read-SharedText -Path $tracePath
            if ($trace.Contains('stage=external_gpu_browser_shadow_content_ready')) {
                break
            }
            $preloaderProcess.Refresh()
            if ($preloaderProcess.HasExited) {
                throw "The packaged $Api Preloader exited before content-ready."
            }
            Start-Sleep -Milliseconds 25
        }
        if (-not $trace.Contains('stage=external_gpu_browser_shadow_content_ready')) {
            throw "The packaged $Api producer did not trace content-ready within 2500 ms of its first frame."
        }

        # Exercise the production close edge first so the browser relinquishes
        # visibility/input before the hidden WinForms host is closed. Keep the
        # fake GTA process alive until producer disposal has completed.
        $null = $hostClose.Set()
        $closeTimer = [Diagnostics.Stopwatch]::StartNew()
        while ($closeTimer.ElapsedMilliseconds -lt 2500) {
            $trace = Read-SharedText -Path $tracePath
            if ($trace.Contains('stage=bootstrap_host_native_close')) { break }
            $preloaderProcess.Refresh()
            if ($preloaderProcess.HasExited) {
                throw "The packaged $Api Preloader exited before its production close edge."
            }
            Start-Sleep -Milliseconds 25
        }
        if (-not $trace.Contains('stage=bootstrap_host_native_close')) {
            throw "The packaged $Api Preloader did not consume its production close edge."
        }

        # The hidden qualification host has no reliable HWND lifecycle from a
        # non-interactive session. Use the Preloader's self-test-only process
        # signal so the production host still owns and executes its full
        # disposal sequence on the STA.
        $null = $selfTestStop.Set()
        if (-not $preloaderProcess.WaitForExit(5000)) {
            throw "The packaged $Api Preloader did not stop within 5000 ms."
        }
        $preloaderProcess.WaitForExit()
        $preloaderProcess.Refresh()
        if ($preloaderProcess.ExitCode -ne 0) {
            throw "The packaged $Api Preloader failed with exit code $($preloaderProcess.ExitCode)."
        }

        $null = $teardownComplete.Set()
        $teardownSignaled = $true
        if (-not $consumer.WaitForExit(10000)) {
            throw "The packaged $Api consumer did not stop within 10000 ms."
        }
        $consumer.WaitForExit()
        $consumer.Refresh()
        $output = Read-SharedText -Path $stdoutPath
        $errorOutput = Read-SharedText -Path $stderrPath
        if ($consumer.ExitCode -ne 0) {
            throw "The packaged $Api consumer failed with exit code $($consumer.ExitCode).`n$output`n$errorOutput"
        }

        $expectedApi = if ($Api -eq 'd3d12') { 'Direct3D12' } else { 'Direct3D11' }
        $proof = [regex]::Match(
            $output,
            "(?m)^RESULT PASS: scenario=external-gpu-consumer api=$expectedApi " +
            'submitted=(?<submitted>[0-9]+) rendered=(?<rendered>[0-9]+) ' +
            'lastGeneration=(?<generation>[0-9]+) elapsedMs=(?<elapsed>[0-9.]+)\r?$')
        if (-not $proof.Success) {
            throw "The packaged $Api consumer omitted rendered shared-frame proof.`n$output"
        }
        $submitted = [uint64]::Parse($proof.Groups['submitted'].Value)
        $rendered = [uint64]::Parse($proof.Groups['rendered'].Value)
        $generation = [uint64]::Parse($proof.Groups['generation'].Value)
        if ($submitted -ne 0 -or $rendered -lt 1 -or $generation -lt 1) {
            throw "The packaged $Api proof was not exclusively cross-process GPU rendered: submitted=$submitted rendered=$rendered generation=$generation."
        }

        $trace = Read-SharedText -Path $tracePath
        Assert-OrderedStages -Trace $trace -Stages @(
            'external_gpu_browser_shadow_started',
            'external_gpu_browser_shadow_content_ready',
            'external_gpu_browser_shadow_stopped',
            'preloader_stop'
        )
        if ($trace -match
            'stage=external_gpu_browser_shadow_(?:unavailable|faulted|content_unavailable|start_rejected)') {
            throw "The packaged $Api trace contains a fallback or fault: $tracePath"
        }
        if ($trace -notmatch
            'stage=bootstrap_host_native_about_toggle[^\r\n]*visible=True') {
            throw "The packaged $Api external browser was never visibly activated: $tracePath"
        }
        if ($trace -match
            'stage=(?:target_process_exit_signal_received|target_process_exit_handling_begin|target_process_exited)') {
            throw "The packaged $Api gate lost its fake GTA target instead of disposing cleanly: $tracePath"
        }

        Write-Host (
            "External GPU browser PASS: api=$Api rendered=$rendered " +
            "generation=$generation cpuSubmitted=$submitted")
        return [ordered]@{
            api = $expectedApi
            submitted_cpu_frames = $submitted
            rendered_shared_frames = $rendered
            last_shared_generation = $generation
            first_shared_frame_ms = [Math]::Round(
                [double]::Parse(
                    $proof.Groups['elapsed'].Value,
                    [Globalization.CultureInfo]::InvariantCulture),
                3)
            self_test_stop_signaled = $true
            trace = $tracePath
        }
    }
    finally {
        if ($preloaderProcess -ne $null -and -not $preloaderProcess.HasExited) {
            if ($selfTestStop -ne $null) {
                $null = $selfTestStop.Set()
            }
            if (-not $preloaderProcess.WaitForExit(3000)) {
                Stop-Process -Id $preloaderProcess.Id -Force -ErrorAction SilentlyContinue
                $preloaderProcess.WaitForExit(3000) | Out-Null
            }
        }
        if ($teardownComplete -ne $null -and -not $teardownSignaled) {
            $null = $teardownComplete.Set()
        }
        if ($consumer -ne $null -and -not $consumer.HasExited) {
            if (-not $consumer.WaitForExit(6000)) {
                Stop-Process -Id $consumer.Id -Force -ErrorAction SilentlyContinue
                $consumer.WaitForExit(3000) | Out-Null
            }
        }
        if ($frameReady -ne $null) { $frameReady.Dispose() }
        if ($teardownComplete -ne $null) { $teardownComplete.Dispose() }
        if ($hostClose -ne $null) { $hostClose.Dispose() }
        if ($selfTestStop -ne $null) { $selfTestStop.Dispose() }
        if ($preloaderProcess -ne $null) { $preloaderProcess.Dispose() }
        if ($consumer -ne $null) { $consumer.Dispose() }
    }
}

$result = [ordered]@{
    schema_version = 1
    enabled_by_default = $false
    d3d11 = Invoke-ApiQualification -Api 'd3d11'
    d3d12 = Invoke-ApiQualification -Api 'd3d12'
}
$result | ConvertTo-Json -Depth 6
