[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipHarness,
    [int]$PreloaderContentReadyBudgetMs = 1500,
    [int]$PreloaderReleaseBudgetMs = 3000,
    [long]$MaximumStagingBytes = 380MB,
    [long]$MaximumArchiveBytes = 190MB
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = $PSScriptRoot
$webRoot = Join-Path $projectRoot 'web'
$nativeRoot = Join-Path $projectRoot 'native'
$nativeBuild = Join-Path $nativeRoot 'build'
$scriptProject = Join-Path $projectRoot 'src\ReactorV.Script\RageWebUI.Script.csproj'
$runtimeProject = Join-Path $projectRoot 'src\ReactorV.Runtime\RageWebUI.Runtime.csproj'
$preloaderProject = Join-Path $projectRoot 'src\ReactorV.Preloader\ReactorV.Preloader.csproj'
$harnessProject = Join-Path $projectRoot 'src\ReactorV.Harness\RageWebUI.Harness.csproj'
$testProject = Join-Path $projectRoot 'tests\ReactorV.Core.Tests\RageWebUI.Core.Tests.csproj'
$exampleProject = Join-Path $projectRoot 'examples\ReactorV.Extension.Examples\ReactorV.Extension.Examples.csproj'
$scriptOutput = Join-Path $projectRoot "src\ReactorV.Script\bin\$Configuration"
$runtimeOutput = Join-Path $projectRoot "src\ReactorV.Runtime\bin\$Configuration"
$preloaderOutput = Join-Path $projectRoot "src\ReactorV.Preloader\bin\$Configuration"
$harnessOutput = Join-Path $projectRoot "src\ReactorV.Harness\bin\$Configuration"
$webOutput = Join-Path $webRoot 'dist'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$stagingRoot = Join-Path $artifactsRoot 'staging'
$bootstrapRoot = Join-Path $stagingRoot 'scripts\ReactorV'
$rendererRoot = Join-Path $stagingRoot 'plugins\ReactorV'
$harnessReportPath = Join-Path $artifactsRoot 'harness\reactor-harness-report.json'
$harnessReport = [ordered]@{
    schema_version = 1
    generated_utc = $null
    configuration = $Configuration
    suites = [ordered]@{
        native = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        web = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        core = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        extension_examples = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        d3d11 = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        d3d12 = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        shvdn_fallback = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        api_contract = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        preloader_packaged = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        shared_profile = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        readiness_timeout = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        native_bootstrap_packaged = 'pending'
    }
    preloader = [ordered]@{
        content_ready_ms = $null
        content_ready_budget_ms = $PreloaderContentReadyBudgetMs
        release_ms = $null
        release_budget_ms = $PreloaderReleaseBudgetMs
        trace = $null
    }
    package = [ordered]@{
        staging_bytes = $null
        staging_budget_bytes = $MaximumStagingBytes
        archive_bytes = $null
        archive_budget_bytes = $MaximumArchiveBytes
        archive = $null
        sha256 = $null
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(ValueFromRemainingArguments)] [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $Command $($Arguments -join ' ')"
    }
}

function Assert-X64PeImage {
    param(
        [Parameter(Mandatory)] [string]$Path
    )

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "Native bootstrap is not a valid PE image: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or ($peOffset + 6) -gt $stream.Length) {
            throw "Native bootstrap has an invalid PE header offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Native bootstrap is missing the PE signature: $Path"
        }
        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664) {
            throw ('Native bootstrap must be an x64 PE image; found machine 0x{0:X4}: {1}' -f $machine, $Path)
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-HarnessSessionTrace {
    param(
        [Parameter(Mandatory)] [string]$LogDirectory,
        [Parameter(Mandatory)] [int]$ProcessId
    )

    $matches = @(
        Get-ChildItem -LiteralPath $LogDirectory -File -Filter "reactorv-session-*-$ProcessId.log" `
            -ErrorAction SilentlyContinue
    )
    if ($matches.Count -ne 1) {
        throw "Expected exactly one Reactor session trace for PID $ProcessId beneath $LogDirectory; found $($matches.Count)."
    }
    return [pscustomobject]@{
        Path = $matches[0].FullName
        Text = [IO.File]::ReadAllText($matches[0].FullName)
    }
}

function Assert-TraceStages {
    param(
        [Parameter(Mandatory)] [string]$Trace,
        [Parameter(Mandatory)] [string[]]$Stages
    )

    $cursor = -1
    foreach ($stage in $Stages) {
        $marker = "stage=$stage"
        $count = ([regex]::Matches($Trace, [regex]::Escape($marker))).Count
        if ($count -ne 1) {
            throw "Expected one '$marker' record; found $count."
        }
        $position = $Trace.IndexOf($marker, $cursor + 1, [StringComparison]::Ordinal)
        if ($position -le $cursor) {
            throw "Reactor stage '$stage' was missing or out of order."
        }
        $cursor = $position
    }
}

function Get-StageElapsedMilliseconds {
    param(
        [Parameter(Mandatory)] [string]$Trace,
        [Parameter(Mandatory)] [string]$Stage
    )

    $pattern = "(?m)^.*?elapsed_ms=(?<elapsed>[0-9.]+)\s+source=preloader\s+stage=$([regex]::Escape($Stage))(?:\s|$)"
    $match = [regex]::Match($Trace, $pattern)
    if (-not $match.Success) {
        throw "No elapsed time was recorded for preloader stage '$Stage'."
    }
    return [double]::Parse(
        $match.Groups['elapsed'].Value,
        [Globalization.CultureInfo]::InvariantCulture
    )
}

function Start-PreloaderHarness {
    param(
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string]$UiDirectory,
        [Parameter(Mandatory)] [string]$ProfileDirectory,
        [Parameter(Mandatory)] [string]$LogDirectory,
        [Parameter(Mandatory)] [string]$InstanceId,
        [int]$TimeoutMilliseconds = 15000
    )

    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList @(
            '--self-test',
            '--ui-dir', ('"{0}"' -f $UiDirectory),
            '--user-data-dir', ('"{0}"' -f $ProfileDirectory),
            '--log-dir', ('"{0}"' -f $LogDirectory),
            '--instance-id', $InstanceId
        ) `
        -PassThru `
        -WindowStyle Hidden
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.Dispose()
        throw "The Reactor V preloader harness '$InstanceId' exceeded $TimeoutMilliseconds ms."
    }
    $result = [pscustomobject]@{
        ProcessId = $process.Id
        ExitCode = $process.ExitCode
    }
    $process.Dispose()
    return $result
}

$cmake = Get-Command 'cmake' -ErrorAction SilentlyContinue
if (-not $cmake) {
    throw 'CMake is required to build the native DirectX compositor.'
}

$configureArguments = @('-S', $nativeRoot, '-B', $nativeBuild)
$ninja = Get-Command 'ninja' -ErrorAction SilentlyContinue
$clang = Get-Command 'clang' -ErrorAction SilentlyContinue
$clangxx = Get-Command 'clang++' -ErrorAction SilentlyContinue
if ($ninja -and $clang -and $clangxx) {
    $configureArguments += @(
        '-G', 'Ninja',
        "-DCMAKE_BUILD_TYPE=$Configuration",
        "-DCMAKE_C_COMPILER=$($clang.Source)",
        "-DCMAKE_CXX_COMPILER=$($clangxx.Source)"
    )
}

Invoke-Checked $cmake.Source @configureArguments
Invoke-Checked $cmake.Source '--build' $nativeBuild '--config' $Configuration

if (-not $SkipTests) {
    $ctest = Get-Command 'ctest' -ErrorAction SilentlyContinue
    if (-not $ctest) {
        throw 'CTest is required to run the native tests.'
    }
    Invoke-Checked $ctest.Source '--test-dir' $nativeBuild '--build-config' $Configuration '--output-on-failure'
}

$nativeCandidates = @(
    (Join-Path $nativeBuild "$Configuration\RageWebUI.Native.dll"),
    (Join-Path $nativeBuild 'RageWebUI.Native.dll')
)
$nativeLibrary = $nativeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $nativeLibrary) {
    throw "Expected native build output was not found beneath: $nativeBuild"
}
$nativeBootstrapCandidates = @(
    (Join-Path $nativeBuild "$Configuration\ReactorV.Bootstrap.asi"),
    (Join-Path $nativeBuild 'ReactorV.Bootstrap.asi')
)
$nativeBootstrap = $nativeBootstrapCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $nativeBootstrap) {
    throw "Expected ReactorV.Bootstrap.asi build output was not found beneath: $nativeBuild"
}
Assert-X64PeImage -Path $nativeBootstrap

$pnpm = Get-Command 'pnpm' -ErrorAction SilentlyContinue
if (-not $pnpm) {
    throw 'pnpm is required to build the web app. Install Node.js, then run: corepack enable'
}
$node = Get-Command 'node' -ErrorAction SilentlyContinue
if (-not $node) {
    # Codex's portable pnpm shim and similar tool bundles may keep Node in a
    # sibling runtime directory instead of placing it on the inherited PATH.
    $portableRoot = Split-Path (Split-Path (Split-Path $pnpm.Source -Parent) -Parent) -Parent
    $portableNode = Join-Path $portableRoot 'node\bin\node.exe'
    if (-not (Test-Path -LiteralPath $portableNode)) {
        throw 'Node.js is required to run the web build and tests.'
    }
    $env:PATH = "$(Split-Path $portableNode -Parent);$env:PATH"
}

Push-Location $webRoot
try {
    Invoke-Checked $pnpm.Source 'install' '--frozen-lockfile'
    if (-not $SkipTests) {
        Invoke-Checked $pnpm.Source 'test'
    }
    Invoke-Checked $pnpm.Source 'build'
}
finally {
    Pop-Location
}

if (-not $SkipTests) {
    Invoke-Checked 'dotnet' 'test' $testProject '--configuration' $Configuration
    Invoke-Checked 'dotnet' 'build' $exampleProject '--configuration' $Configuration
}
Invoke-Checked 'dotnet' 'build' $scriptProject '--configuration' $Configuration
Invoke-Checked 'dotnet' 'build' $runtimeProject '--configuration' $Configuration
Invoke-Checked 'dotnet' 'build' $preloaderProject '--configuration' $Configuration
Invoke-Checked 'dotnet' 'build' $harnessProject '--configuration' $Configuration
if (-not $SkipTests) {
    $harnessReport.suites.native = 'passed'
    $harnessReport.suites.web = 'passed'
    $harnessReport.suites.core = 'passed'
    $harnessReport.suites.extension_examples = 'passed'
}

Copy-Item -LiteralPath $nativeLibrary -Destination $harnessOutput -Force
$harnessUi = Join-Path $harnessOutput 'ui'
if (Test-Path -LiteralPath $harnessUi) {
    Remove-Item -LiteralPath $harnessUi -Recurse -Force
}
Copy-Item -LiteralPath $webOutput -Destination $harnessUi -Recurse

if (-not $SkipTests -and -not $SkipHarness) {
    $harness = Join-Path $harnessOutput 'RageWebUI.Harness.exe'
    Invoke-Checked $harness '--api' 'd3d11' '--smoke'
    $harnessReport.suites.d3d11 = 'passed'
    Invoke-Checked $harness '--api' 'd3d12' '--smoke'
    $harnessReport.suites.d3d12 = 'passed'
    Invoke-Checked $harness '--scenario' 'shvdn-fallback' '--smoke'
    $harnessReport.suites.shvdn_fallback = 'passed'
}

$expectedStagingPrefix = [IO.Path]::GetFullPath($artifactsRoot) + [IO.Path]::DirectorySeparatorChar
$resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStaging.StartsWith($expectedStagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear unexpected staging path: $resolvedStaging"
}
if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
New-Item -ItemType Directory -Path $rendererRoot -Force | Out-Null
Copy-Item -LiteralPath $nativeBootstrap -Destination (Join-Path $stagingRoot 'ReactorV.Bootstrap.asi')

$bootstrapFiles = @(
    'RageWebUI.Script.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll'
)
foreach ($file in $bootstrapFiles) {
    $source = Join-Path $scriptOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected bootstrap output is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $bootstrapRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'ReactorV.json') -Destination $bootstrapRoot

$runtimeManagedFiles = @(
    'RageWebUI.Runtime.dll',
    'RageWebUI.DirectX.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll'
)
$cefFiles = @(
    'CefSharp.BrowserSubprocess.Core.dll',
    'CefSharp.BrowserSubprocess.exe',
    'CefSharp.Core.dll',
    'CefSharp.Core.Runtime.dll',
    'CefSharp.dll',
    'CefSharp.OffScreen.dll',
    'chrome_100_percent.pak',
    'chrome_200_percent.pak',
    'chrome_elf.dll',
    'd3dcompiler_47.dll',
    'dxcompiler.dll',
    'dxil.dll',
    'icudtl.dat',
    'libcef.dll',
    'libEGL.dll',
    'libGLESv2.dll',
    'resources.pak',
    'v8_context_snapshot.bin',
    'vk_swiftshader_icd.json',
    'vk_swiftshader.dll',
    'vulkan-1.dll'
)
foreach ($file in $runtimeManagedFiles + $cefFiles) {
    $source = Join-Path $runtimeOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected renderer output is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $rendererRoot
}

$loader = Join-Path $runtimeOutput 'runtimes\win-x64\native\WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $loader)) {
    throw "Expected WebView2 loader is missing: $loader"
}
Copy-Item -LiteralPath $loader -Destination $rendererRoot
Copy-Item -LiteralPath $nativeLibrary -Destination $rendererRoot
Copy-Item -LiteralPath (Join-Path $harnessOutput 'RageWebUI.Harness.exe') -Destination $rendererRoot
Copy-Item -LiteralPath (Join-Path $harnessOutput 'RageWebUI.Harness.exe.config') -Destination $rendererRoot
$preloaderFiles = @(
    'ReactorV.Preloader.exe',
    'ReactorV.Preloader.exe.config'
)
foreach ($file in $preloaderFiles) {
    $source = Join-Path $preloaderOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected preloader output is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $rendererRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'ReactorV.Preloader.json') -Destination $rendererRoot

$localeRoot = Join-Path $rendererRoot 'locales'
New-Item -ItemType Directory -Path $localeRoot -Force | Out-Null
Get-ChildItem (Join-Path $runtimeOutput 'locales') -Filter 'en-US*.pak' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $localeRoot
}

Copy-Item -LiteralPath $webOutput -Destination (Join-Path $rendererRoot 'ui') -Recurse

if (-not $SkipTests -and -not $SkipHarness) {
    $preloader = Join-Path $rendererRoot 'ReactorV.Preloader.exe'
    $sharedWebViewRoot = Join-Path $artifactsRoot 'harness\SharedWebView2'
    $sharedWebViewProfile = Join-Path $sharedWebViewRoot 'WebView2'
    if (Test-Path -LiteralPath $sharedWebViewRoot) {
        Remove-Item -LiteralPath $sharedWebViewRoot -Recurse -Force
    }
    $packagedHarness = Join-Path $rendererRoot 'RageWebUI.Harness.exe'

    # Exercise the exact staged Core/Harness pair against the public v2
    # integration path before any renderer smoke tests can mask a contract
    # mismatch.
    Invoke-Checked $packagedHarness '--scenario' 'api-contract'
    $harnessReport.suites.api_contract = 'passed'

    # Performance gate the exact packaged preloader in isolation. The generous
    # ceilings catch the former two-second virtual-host navigation regression
    # without turning normal CI variance into a flaky release failure.
    $performanceRoot = Join-Path $artifactsRoot 'harness\PreloaderPerformance'
    if (Test-Path -LiteralPath $performanceRoot) {
        Remove-Item -LiteralPath $performanceRoot -Recurse -Force
    }
    $performanceRun = Start-PreloaderHarness `
        -Executable $preloader `
        -UiDirectory (Join-Path $rendererRoot 'ui') `
        -ProfileDirectory (Join-Path $performanceRoot 'WebView2') `
        -LogDirectory (Join-Path $performanceRoot 'Logs') `
        -InstanceId "performance-$([Guid]::NewGuid().ToString('N'))"
    if ($performanceRun.ExitCode -ne 0) {
        throw "The packaged Reactor V performance self-test failed with exit code $($performanceRun.ExitCode)."
    }
    $performanceTrace = Get-HarnessSessionTrace `
        -LogDirectory (Join-Path $performanceRoot 'Logs') `
        -ProcessId $performanceRun.ProcessId
    Assert-TraceStages -Trace $performanceTrace.Text -Stages @(
        'preloader_start',
        'webview_initialize_begin',
        'webview_environment_ready',
        'webview_navigation_begin',
        'webview_navigation_completed',
        'webview_page_timing',
        'webview_content_ready',
        'webview_profile_release_begin',
        'webview_profile_release_complete',
        'webview_warm_cache_released',
        'self_test_complete',
        'preloader_stop'
    )
    if ($performanceTrace.Text -match 'stage=(?:browser_failed|webview_page_readiness_failed)') {
        throw "The packaged Reactor V performance trace contains a startup failure: $($performanceTrace.Path)"
    }
    foreach ($requiredMetric in @(
        '"readyState":"complete"',
        '"rootChildren":1',
        '"name":"app-',
        '"name":"bridge-',
        '.css"',
        '"name":"ragewebui-logo.png"'
    )) {
        if (-not $performanceTrace.Text.Contains($requiredMetric)) {
            throw "The packaged Reactor V page trace is missing '$requiredMetric'."
        }
    }
    $contentReadyMs = Get-StageElapsedMilliseconds `
        -Trace $performanceTrace.Text `
        -Stage 'webview_content_ready'
    $releasedMs = Get-StageElapsedMilliseconds `
        -Trace $performanceTrace.Text `
        -Stage 'self_test_complete'
    if ($contentReadyMs -gt $PreloaderContentReadyBudgetMs) {
        throw "Reactor V content-ready time $contentReadyMs ms exceeded the $PreloaderContentReadyBudgetMs ms budget."
    }
    if ($releasedMs -gt $PreloaderReleaseBudgetMs) {
        throw "Reactor V preload release time $releasedMs ms exceeded the $PreloaderReleaseBudgetMs ms budget."
    }
    $harnessReport.suites.preloader_packaged = 'passed'
    $harnessReport.preloader.content_ready_ms = [Math]::Round($contentReadyMs, 3)
    $harnessReport.preloader.release_ms = [Math]::Round($releasedMs, 3)
    $harnessReport.preloader.trace = $performanceTrace.Path
    Write-Host (
        "Reactor preloader performance PASS: content-ready={0:F1} ms, released={1:F1} ms" -f `
            $contentReadyMs,
            $releasedMs
    )

    $preloaderProcess = Start-Process `
        -FilePath $preloader `
        -ArgumentList @(
            '--self-test',
            '--user-data-dir', ('"{0}"' -f $sharedWebViewProfile),
            '--log-dir', ('"{0}"' -f (Join-Path $sharedWebViewRoot 'Logs')),
            '--instance-id', "contention-$([Guid]::NewGuid().ToString('N'))"
        ) `
        -PassThru `
        -WindowStyle Hidden
    try {
        # Reproduce the production contention directly: both processes create
        # controllers from the same UDF at the same time. Their shared options
        # contract must prevent ERROR_INVALID_STATE, after which the preloader
        # releases its controller and waits for the harness to close the final
        # shared browser process.
        Invoke-Checked $packagedHarness '--scenario' 'shvdn-fallback' '--smoke' '--local-data-dir' $sharedWebViewRoot
        if (-not $preloaderProcess.WaitForExit(15000)) {
            throw 'The Reactor V preloader did not release the shared WebView2 profile after the runtime harness closed.'
        }
        if ($preloaderProcess.ExitCode -ne 0) {
            throw "The Reactor V shared-profile preloader test failed with exit code $($preloaderProcess.ExitCode)."
        }
        $contentionTrace = Get-HarnessSessionTrace `
            -LogDirectory (Join-Path $sharedWebViewRoot 'Logs') `
            -ProcessId $preloaderProcess.Id
        if (
            $contentionTrace.Text -notmatch 'stage=self_test_complete .*profile_released=True' -or
            $contentionTrace.Text -notmatch 'stage=preloader_stop .*exit_code=0'
        ) {
            throw "The shared-profile preloader exited without a complete successful trace: $($contentionTrace.Path)"
        }
        $harnessReport.suites.shared_profile = 'passed'
    }
    finally {
        if (-not $preloaderProcess.HasExited) {
            Stop-Process -Id $preloaderProcess.Id -Force -ErrorAction SilentlyContinue
        }
        $preloaderProcess.Dispose()
    }

    # A navigated document that never publishes the React ready marker must
    # fail closed. This prevents a timeout from becoming a false warm-cache
    # success in a release build.
    $failureRoot = Join-Path $artifactsRoot 'harness\ReadinessTimeout'
    if (Test-Path -LiteralPath $failureRoot) {
        Remove-Item -LiteralPath $failureRoot -Recurse -Force
    }
    $failureUi = Join-Path $failureRoot 'ui'
    New-Item -ItemType Directory -Path $failureUi -Force | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $failureUi 'index.html'),
        '<!doctype html><html><head><title>Timeout fixture</title></head><body><div id="root"></div></body></html>',
        [Text.UTF8Encoding]::new($false)
    )
    $failureRun = Start-PreloaderHarness `
        -Executable $preloader `
        -UiDirectory $failureUi `
        -ProfileDirectory (Join-Path $failureRoot 'WebView2') `
        -LogDirectory (Join-Path $failureRoot 'Logs') `
        -InstanceId "timeout-$([Guid]::NewGuid().ToString('N'))"
    if ($failureRun.ExitCode -eq 0) {
        throw 'The Reactor V missing-ready-marker fixture unexpectedly succeeded.'
    }
    $failureTrace = Get-HarnessSessionTrace `
        -LogDirectory (Join-Path $failureRoot 'Logs') `
        -ProcessId $failureRun.ProcessId
    foreach ($requiredFailure in @(
        'stage=webview_page_readiness_failed',
        'stage=browser_failed',
        'stage=preloader_stop exit_code=1'
    )) {
        if (-not $failureTrace.Text.Contains($requiredFailure)) {
            throw "The readiness-timeout trace is missing '$requiredFailure': $($failureTrace.Path)"
        }
    }
    if ($failureTrace.Text -match 'stage=(?:webview_content_ready|webview_warm_cache_released|self_test_complete)') {
        throw "The readiness-timeout fixture emitted a false success stage: $($failureTrace.Path)"
    }
    $harnessReport.suites.readiness_timeout = 'passed'
    Write-Host 'Reactor readiness-timeout fixture PASS: false success was withheld.'
}

# The harness is copied into staging only so the packaged-layout smoke test can
# exercise the exact runtime files. It is a developer executable, not part of
# the player payload.
foreach ($harnessFile in @(
    'RageWebUI.Harness.exe',
    'RageWebUI.Harness.exe.config'
)) {
    $candidate = Join-Path $rendererRoot $harnessFile
    if (Test-Path -LiteralPath $candidate) {
        Remove-Item -LiteralPath $candidate -Force
    }
}

$unexpectedPackagedArtifacts = @(
    Get-ChildItem -LiteralPath $stagingRoot -Force -Recurse |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (-not $_.PSIsContainer -and (
                $_.Name -like '*Harness*' -or
                $_.Extension -in @('.map', '.pdb', '.log', '.tmp')
            )) -or
            ($_.PSIsContainer -and $_.Name -eq 'node_modules')
        }
)
if ($unexpectedPackagedArtifacts) {
    throw "Development artifacts leaked into Reactor staging:`n$($unexpectedPackagedArtifacts.FullName -join "`n")"
}
$unexpectedTopLevel = @(
    Get-ChildItem -LiteralPath $stagingRoot -Force |
        Where-Object { $_.Name -notin @('plugins', 'scripts', 'ReactorV.Bootstrap.asi') }
)
if ($unexpectedTopLevel) {
    throw "Unexpected Reactor staging root(s): $($unexpectedTopLevel.Name -join ', ')"
}
$sourceMapReferences = @(
    Get-ChildItem -LiteralPath (Join-Path $rendererRoot 'ui') -File -Recurse -Filter '*.js' |
        Select-String -SimpleMatch 'sourceMappingURL='
)
if ($sourceMapReferences) {
    throw "Source-map references leaked into the packaged Reactor UI."
}
$stagingFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
$stagingBytes = ($stagingFiles | Measure-Object Length -Sum).Sum
if ($stagingBytes -gt $MaximumStagingBytes) {
    throw "Reactor staging size $stagingBytes bytes exceeded the $MaximumStagingBytes-byte budget."
}

# SHVDN recursively inspects every managed assembly beneath scripts. Keep the
# bootstrap intentionally tiny and fail the build if renderer dependencies ever
# leak back into that scan tree.
$allowedScriptDlls = @(
    'RageWebUI.Script.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll'
)
$unexpectedScriptDlls = Get-ChildItem (Join-Path $stagingRoot 'scripts') -Filter '*.dll' -File -Recurse |
    Where-Object { $allowedScriptDlls -notcontains $_.Name }
if ($unexpectedScriptDlls) {
    $paths = ($unexpectedScriptDlls.FullName -join "`n")
    throw "Renderer dependencies leaked beneath scripts:`n$paths"
}
$requiredRendererFiles = @(
    'RageWebUI.Runtime.dll',
    'RageWebUI.DirectX.dll',
    'CefSharp.Core.Runtime.dll',
    'libcef.dll',
    'RageWebUI.Native.dll',
    'ReactorV.Preloader.exe',
    'ReactorV.Preloader.exe.config',
    'ReactorV.Preloader.json',
    'ui\index.html'
)
foreach ($relativePath in $requiredRendererFiles) {
    $candidate = Join-Path $rendererRoot $relativePath
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Renderer package validation failed; missing: $candidate"
    }
}
$packagedNativeBootstrap = Join-Path $stagingRoot 'ReactorV.Bootstrap.asi'
if (-not (Test-Path -LiteralPath $packagedNativeBootstrap -PathType Leaf)) {
    throw "Native bootstrap package validation failed; missing: $packagedNativeBootstrap"
}
Assert-X64PeImage -Path $packagedNativeBootstrap
$harnessReport.suites.native_bootstrap_packaged = 'passed'

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$archive = Join-Path $artifactsRoot 'ReactorV-0.2.0.zip'
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $archive -CompressionLevel Optimal
$archiveBytes = (Get-Item -LiteralPath $archive).Length
if ($archiveBytes -gt $MaximumArchiveBytes) {
    throw "Reactor archive size $archiveBytes bytes exceeded the $MaximumArchiveBytes-byte budget."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $badEntries = @(
        $zip.Entries | Where-Object {
            $_.FullName -match '(^|/)(?:node_modules)(/|$)' -or
            $_.Name -like '*Harness*' -or
            [IO.Path]::GetExtension($_.Name) -in @('.map', '.pdb', '.log', '.tmp')
        }
    )
    if ($badEntries) {
        throw "Development artifacts leaked into the Reactor ZIP: $($badEntries.FullName -join ', ')"
    }
    foreach ($requiredEntry in @(
        'ReactorV.Bootstrap.asi',
        'scripts/ReactorV/RageWebUI.Script.dll',
        'plugins/ReactorV/RageWebUI.Runtime.dll',
        'plugins/ReactorV/ReactorV.Preloader.exe',
        'plugins/ReactorV/ui/index.html'
    )) {
        if (-not ($zip.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -eq $requiredEntry
        })) {
            throw "The Reactor ZIP is missing required entry '$requiredEntry'."
        }
    }
}
finally {
    $zip.Dispose()
}
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
$harnessReport.generated_utc = [DateTime]::UtcNow.ToString('o')
$harnessReport.package.staging_bytes = [long]$stagingBytes
$harnessReport.package.archive_bytes = [long]$archiveBytes
$harnessReport.package.archive = $archive
$harnessReport.package.sha256 = $hash
New-Item -ItemType Directory -Path (Split-Path $harnessReportPath -Parent) -Force | Out-Null
$harnessReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $harnessReportPath -Encoding utf8

Write-Host "Built: $archive"
Write-Host "SHA-256: $hash"
Write-Host "Package budgets PASS: staging=$stagingBytes bytes, archive=$archiveBytes bytes"
Write-Host "Harness report: $harnessReportPath"
