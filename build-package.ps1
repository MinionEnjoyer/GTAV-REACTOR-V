[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipHarness,
    [int]$PreloaderContentReadyBudgetMs = 1500,
    [int]$PreloaderReleaseBudgetMs = 3000,
    [int]$PersistentHostWarmDelayMs = 3500,
    [int]$GbayColdReadyBudgetMs = 3500,
    [int]$GbayFirstPresentationBudgetMs = 1000,
    [int]$GbayWarmPresentationBudgetMs = 500,
    [int]$GbayCloseBudgetMs = 500,
    [ValidateRange(10, 20)]
    [int]$CefColdStartCycles = 10,
    [long]$MaximumStagingBytes = 380MB,
    [long]$MaximumArchiveBytes = 190MB,
    [switch]$DisableLegacyStartupObserver,
    [switch]$IncludeExperimentalEnhancedRenderHook,
    [switch]$IncludeExperimentalLegacyRenderHook,
    [string]$ScriptHookSdkRoot = $env:REACTORV_SCRIPTHOOK_SDK_ROOT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$legacyStartupObserverEnabled = -not $DisableLegacyStartupObserver
$enhancedLiveTestGameExecutable = 'GTA5_Enhanced.exe'
$enhancedLiveTestGameVersion = '1.0.1158.13'
$enhancedLiveTestGameSha256 =
    '0c52864d4521d9c9d441348aa1156958792dde8825d0297c851753f167336401'
$enhancedLiveTestMarkerName = 'ReactorV.EnhancedLiveTest.json'
$legacyLiveTestGameExecutable = 'GTA5.exe'
$legacyLiveTestGameVersion = '1.0.3889.0'
$legacyLiveTestGameSha256 =
    '677e4e355cfbdb13273b1d992407e3c261b3a108dc4dd5c8a0c4c1da651802e5'
$legacyLiveTestMarkerName = 'ReactorV.LegacyLiveTest.json'
$legacyCpuFrameMarkerName = 'ReactorV.LegacyCpuFrames.enabled'
$includeExperimentalRenderHook =
    [bool]$IncludeExperimentalEnhancedRenderHook -or
    [bool]$IncludeExperimentalLegacyRenderHook
if ($IncludeExperimentalEnhancedRenderHook -and
    $IncludeExperimentalLegacyRenderHook) {
    throw 'Choose exactly one experimental render-hook target: Enhanced or Legacy.'
}
$qualityGatesEnabled =
    $Configuration -eq 'Release' -and -not $SkipTests -and -not $SkipHarness
if ($includeExperimentalRenderHook -and -not $qualityGatesEnabled) {
    $targetName = if ($IncludeExperimentalEnhancedRenderHook) { 'Enhanced' } else { 'Legacy' }
    throw @"
The $targetName render-hook live-test artifact requires a fully qualified Release
build. Do not combine an experimental render-hook switch with Debug, -SkipTests,
or -SkipHarness. Experimental output must never borrow a live-test name without
passing the complete package gates.
"@
}

if ($PersistentHostWarmDelayMs -le $PreloaderReleaseBudgetMs) {
    throw 'PersistentHostWarmDelayMs must exceed PreloaderReleaseBudgetMs so the delayed-host qualification proves browser reuse.'
}

if ([string]::IsNullOrWhiteSpace($ScriptHookSdkRoot)) {
    throw @'
ReactorV.ScriptProbe.asi requires the official ScriptHookV SDK at build time.
Download the SDK from https://www.dev-c.com/gtav/scripthookv/ and pass
-ScriptHookSdkRoot <extracted-SDK-root>, or set REACTORV_SCRIPTHOOK_SDK_ROOT.
The SDK is an external build dependency and is never included in Reactor packages.
'@
}
$resolvedScriptHookSdkRoot = (Resolve-Path -LiteralPath $ScriptHookSdkRoot).Path
$scriptHookHeader = Join-Path $resolvedScriptHookSdkRoot 'inc\main.h'
$scriptHookImportLibrary = @(
    (Join-Path $resolvedScriptHookSdkRoot 'lib\ScriptHookV.lib'),
    (Join-Path $resolvedScriptHookSdkRoot 'ScriptHookV.lib')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not (Test-Path -LiteralPath $scriptHookHeader -PathType Leaf)) {
    throw "The configured official ScriptHookV SDK is incomplete; missing: $scriptHookHeader"
}
if (-not $scriptHookImportLibrary) {
    throw "The configured official ScriptHookV SDK is missing ScriptHookV.lib under its root or lib directory: $resolvedScriptHookSdkRoot"
}
$scriptHookImportHash =
    (Get-FileHash -LiteralPath $scriptHookImportLibrary -Algorithm SHA256).Hash.ToLowerInvariant()

$projectRoot = $PSScriptRoot
$webRoot = Join-Path $projectRoot 'web'
$nativeRoot = Join-Path $projectRoot 'native'
$nativeBuild = Join-Path $nativeRoot 'build-msvc-scriptprobe'
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
$desktopPresentationProbeDependencies = @(
    'SharpDX.dll',
    'SharpDX.Direct3D11.dll',
    'SharpDX.DXGI.dll'
)
$webOutput = Join-Path $webRoot 'dist'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$stagingRoot = Join-Path $artifactsRoot 'staging'
$bootstrapRoot = Join-Path $stagingRoot 'scripts\ReactorV'
$rendererRoot = Join-Path $stagingRoot 'plugins\ReactorV'
$releaseEligible =
    $qualityGatesEnabled -and -not $includeExperimentalRenderHook
$artifactKind = if ($IncludeExperimentalEnhancedRenderHook) {
    'enhanced-live-test'
} elseif ($IncludeExperimentalLegacyRenderHook) {
    'legacy-live-test'
} elseif ($releaseEligible) {
    'release'
} else {
    'developer'
}
$harnessReportName = if ($IncludeExperimentalEnhancedRenderHook) {
    'reactor-harness-report.enhanced-live-test.json'
} elseif ($IncludeExperimentalLegacyRenderHook) {
    'reactor-harness-report.legacy-live-test.json'
} elseif ($releaseEligible) {
    'reactor-harness-report.json'
} else {
    'reactor-harness-report.developer.json'
}
$archiveName = if ($IncludeExperimentalEnhancedRenderHook) {
    'ReactorV-0.2.0-enhanced-live-test.zip'
} elseif ($IncludeExperimentalLegacyRenderHook) {
    'ReactorV-0.2.0-legacy-live-test.zip'
} elseif ($releaseEligible) {
    'ReactorV-0.2.0.zip'
} else {
    'ReactorV-0.2.0-developer.zip'
}
$harnessReportPath = Join-Path $artifactsRoot "harness\$harnessReportName"
$nativeCTestReportPath = Join-Path $artifactsRoot "harness\native-ctest.$artifactKind.junit.xml"
$archive = Join-Path $artifactsRoot $archiveName
$archiveHashPath = "$archive.sha256"
$projectRootPrefix = [IO.Path]::GetFullPath($projectRoot) + [IO.Path]::DirectorySeparatorChar

function ConvertTo-ProjectRelativeArtifactPath {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($projectRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact report path is outside the Reactor V repository: $fullPath"
    }
    return $fullPath.Substring($projectRootPrefix.Length).Replace('\', '/')
}

# A failed release gate must not leave a previous public artifact looking like
# the output of this invocation. Publish a ZIP, checksum, and report only after
# every current-source gate succeeds.
foreach ($staleReleaseOutput in @(
    $archive,
    $archiveHashPath,
    $harnessReportPath,
    $nativeCTestReportPath
)) {
    if (Test-Path -LiteralPath $staleReleaseOutput -PathType Leaf) {
        Remove-Item -LiteralPath $staleReleaseOutput -Force
    }
}

$harnessReport = [ordered]@{
    schema_version = 1
    generated_utc = $null
    configuration = $Configuration
    artifact_kind = $artifactKind
    release_eligible = $releaseEligible
    legacy_startup_observer = $(if ($legacyStartupObserverEnabled) { 'enabled' } else { 'disabled' })
    suites = [ordered]@{
        native = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        web = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        core = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        extension_examples = $(if ($SkipTests) { 'skipped' } else { 'pending' })
        d3d11 = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        d3d12 = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        external_gpu_browser_shadow = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        cef_cold_start = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        shvdn_fallback = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        api_contract = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        gbay_lifecycle = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        preloader_packaged = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        bootstrap_host = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        shared_profile = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        readiness_timeout = $(if ($SkipHarness -or $SkipTests) { 'skipped' } else { 'pending' })
        native_bootstrap_packaged = 'pending'
        native_script_probe_packaged = 'pending'
    }
    preloader = [ordered]@{
        content_ready_ms = $null
        content_ready_budget_ms = $PreloaderContentReadyBudgetMs
        release_ms = $null
        release_budget_ms = $PreloaderReleaseBudgetMs
        trace = $null
    }
    cef = [ordered]@{
        cycles_per_api = $CefColdStartCycles
        d3d11_passed = 0
        d3d12_passed = 0
    }
    external_gpu_browser = [ordered]@{
        enabled_by_default = $includeExperimentalRenderHook
        frame_rate = $null
        d3d11 = $null
        d3d12 = $null
    }
    gbay = [ordered]@{
        cold_ready_ms = $null
        cold_ready_budget_ms = $GbayColdReadyBudgetMs
        first_presentation_ms = $null
        first_presentation_budget_ms = $GbayFirstPresentationBudgetMs
        close_ms = $null
        close_budget_ms = $GbayCloseBudgetMs
        warm_presentation_ms = $null
        warm_presentation_budget_ms = $GbayWarmPresentationBudgetMs
        rapid_toggle_ms = $null
        maximum_black_fraction = $null
        minimum_changed_fraction = $null
        minimum_green_fraction = $null
        maximum_blue_fraction = $null
        menu_gets = $null
        expected_menu_gets = $null
        stress_menu_gets = $null
        menu_revision = $null
        menu_invokes = $null
        typed_invocations = $null
        route_coverage = $null
        ready_acknowledgements = $null
        stale_acknowledgements = $null
        stress_cycles = $null
        stress_ready_p50_ms = $null
        stress_ready_p95_ms = $null
        stress_ready_max_ms = $null
        stress_reveal_p50_ms = $null
        stress_reveal_p95_ms = $null
        stress_reveal_max_ms = $null
        effective_client_width = $null
        effective_client_height = $null
        effective_dpi = $null
        trace = $null
        first_screenshot = $null
        warm_screenshot = $null
    }
    bootstrap = [ordered]@{
        neutral_verification = $null
        verification_active = $null
        verification_active_reset = $null
        verification_promoted_in_place = $null
        main_menu_about = $null
        about_single_popup = $null
        about_no_intent = $null
        about_closed = $null
        startup_surface = $null
        startup_topmost = $null
        startup_demoted_on_close = $null
        startup_checks = $null
        startup_console_bounded = $null
        startup_copy_contract = $null
        early_escape_close = $null
        closed_intent_cleared = $null
        early_intent_preserved = $null
        provider_menu_ready = $null
        provider_startup_status = $null
        provider_status_requested_menu = $null
        intent_consumed_once = $null
        release_before_intent_absent = $null
        late_intent_armed = $null
        late_intent_consumed_once = $null
        late_presentation_ready = $null
        late_retry_checks = $null
        intent_claim_acknowledgements = $null
        claimed_menu_stayed_visible = $null
        cancel_after_reserve = $null
        cancelled_before_dispatch = $null
        cancelled_dispatch_rejected = $null
        cancelled_claim_rejected = $null
        cancelled_no_presentation = $null
        cancelled_status_neutral = $null
        transient_intent_preserved = $null
        provider_reconnected = $null
        reconnect_observed_intent = $null
        reconnect_closed = $null
        initializer_surface_owned = $null
        startup_to_gbay = $null
        transition_no_black = $null
        transition_no_transparent = $null
        transition_no_interstitial = $null
        single_popup = $null
        transition_frames = $null
        ready_acknowledgements = $null
        stale_acknowledgements = $null
        provider_disconnected = $null
        disconnect_intent_cancelled = $null
        about_preserved_on_disconnect = $null
        no_stale_intent = $null
        trace = $null
        about_screenshot = $null
        startup_screenshot = $null
        handoff_screenshot = $null
    }
    package = [ordered]@{
        staging_bytes = $null
        staging_budget_bytes = $MaximumStagingBytes
        archive_bytes = $null
        archive_budget_bytes = $MaximumArchiveBytes
        archive = $null
        sha256 = $null
        scripthook_sdk_import_sha256 = $scriptHookImportHash
        render_hook = $(if ($includeExperimentalRenderHook) {
            'experimental_live_test'
        } else {
            'experimental_unshipped'
        })
        legacy_cpu_frames = [bool]$IncludeExperimentalLegacyRenderHook
        source_commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
        target_game_executable = $(if ($IncludeExperimentalEnhancedRenderHook) {
            $enhancedLiveTestGameExecutable
        } elseif ($IncludeExperimentalLegacyRenderHook) {
            $legacyLiveTestGameExecutable
        } else { $null })
        target_game_version = $(if ($IncludeExperimentalEnhancedRenderHook) {
            $enhancedLiveTestGameVersion
        } elseif ($IncludeExperimentalLegacyRenderHook) {
            $legacyLiveTestGameVersion
        } else { $null })
        target_game_sha256 = $(if ($IncludeExperimentalEnhancedRenderHook) {
            $enhancedLiveTestGameSha256
        } elseif ($IncludeExperimentalLegacyRenderHook) {
            $legacyLiveTestGameSha256
        } else { $null })
    }
    native_tests = [ordered]@{
        total = $null
        skipped = $null
        junit = $null
        qualified_cases = @()
        exclusive_fullscreen = 'not-qualified-by-windowed-gates; requires explicit fullscreen and GTA acceptance'
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

function Invoke-CefColdStartStabilityGate {
    param(
        [Parameter(Mandatory)] [string]$Harness,
        [Parameter(Mandatory)] [ValidateSet('d3d11', 'd3d12')] [string]$Api,
        [Parameter(Mandatory)] [ValidateRange(10, 20)] [int]$Cycles
    )

    # Every process receives a fresh CEF root cache from HarnessRunDirectory.
    # Running separate processes is intentional: this qualifies the exact
    # global-context initialization/browser-creation boundary that previously
    # failed intermittently inside libcef.dll during package builds.
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        Write-Host "CEF cold-start gate: api=$Api cycle=$cycle/$Cycles"
        & $Harness '--api' $Api '--duration' '1.5'
        if ($LASTEXITCODE -ne 0) {
            throw "CEF cold-start gate failed: api=$Api cycle=$cycle/$Cycles exit=$LASTEXITCODE"
        }
    }
}

function Get-HarnessOutputValue {
    param(
        [Parameter(Mandatory)] [string]$Output,
        [Parameter(Mandatory)] [string]$Name
    )

    $match = [regex]::Match(
        $Output,
        "(?:^|\s)$([regex]::Escape($Name))=(?<value>[^\s]+)",
        [Text.RegularExpressions.RegexOptions]::Multiline
    )
    if (-not $match.Success) {
        throw "Harness output did not report '$Name'.`n$Output"
    }
    return $match.Groups['value'].Value
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

function Initialize-PackageBinaryInspection {
    if ($null -ne ('ReactorV.Package.BinaryInspection' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ReactorV.Package {
    public static class BinaryInspection {
        private const uint DontResolveDllReferences = 0x00000001;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(
            string fileName,
            IntPtr reserved,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        public static string MissingExport(string path, string[] names) {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, DontResolveDllReferences);
            if (module == IntPtr.Zero) {
                throw new InvalidOperationException(
                    "LoadLibraryEx(DONT_RESOLVE_DLL_REFERENCES) failed for " + path +
                    " (Win32 " + Marshal.GetLastWin32Error() + ").");
            }
            try {
                foreach (string name in names) {
                    if (GetProcAddress(module, name) == IntPtr.Zero) return name;
                }
                return null;
            }
            finally {
                FreeLibrary(module);
            }
        }

        public static string FindLeak(string path, string[] tokens) {
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                return FindLeak(stream, tokens);
            }
        }

        public static string FindLeak(Stream stream, string[] tokens) {
            var patterns = new List<KeyValuePair<string, byte[]>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in tokens) {
                if (String.IsNullOrWhiteSpace(token)) continue;
                AddPattern(patterns, seen, token, Encoding.UTF8.GetBytes(token));
                AddPattern(patterns, seen, token, Encoding.Unicode.GetBytes(token));
            }
            if (patterns.Count == 0) return null;

            int longest = 1;
            foreach (var pattern in patterns) {
                longest = Math.Max(longest, pattern.Value.Length);
            }
            byte[] buffer = new byte[65536 + longest];
            int retained = 0;
            for (;;) {
                int read = stream.Read(buffer, retained, 65536);
                int available = retained + read;
                foreach (var pattern in patterns) {
                    if (IndexOf(buffer, available, pattern.Value) >= 0) {
                        return pattern.Key;
                    }
                }
                if (read == 0) return null;
                retained = Math.Min(longest - 1, available);
                if (retained > 0) {
                    Buffer.BlockCopy(
                        buffer, available - retained, buffer, 0, retained);
                }
            }
        }

        private static void AddPattern(
            List<KeyValuePair<string, byte[]>> patterns,
            HashSet<string> seen,
            string label,
            byte[] bytes) {
            string identity = Convert.ToBase64String(bytes);
            if (bytes.Length > 0 && seen.Add(identity)) {
                patterns.Add(new KeyValuePair<string, byte[]>(label, bytes));
            }
        }

        private static int IndexOf(byte[] buffer, int count, byte[] pattern) {
            int last = count - pattern.Length;
            for (int offset = 0; offset <= last; ++offset) {
                int index = 0;
                while (index < pattern.Length &&
                       AsciiCaseEqual(buffer[offset + index], pattern[index])) {
                    ++index;
                }
                if (index == pattern.Length) return offset;
            }
            return -1;
        }

        private static bool AsciiCaseEqual(byte left, byte right) {
            if (left >= (byte)'A' && left <= (byte)'Z') left += 32;
            if (right >= (byte)'A' && right <= (byte)'Z') right += 32;
            return left == right;
        }
    }
}
'@
}

function Assert-NativeExports {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$Names
    )

    Initialize-PackageBinaryInspection
    $missing = [ReactorV.Package.BinaryInspection]::MissingExport($Path, $Names)
    if (-not [string]::IsNullOrWhiteSpace($missing)) {
        throw "Native ABI validation failed; '$missing' is not exported by: $Path"
    }
}

function Assert-CtestJUnit {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [string[]]$RequiredTestCases = @()
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "CTest did not produce its required JUnit receipt: $Path"
    }
    [xml]$junit = [IO.File]::ReadAllText($Path)
    $testCases = @($junit.SelectNodes('//testcase'))
    $skippedCases = @($junit.SelectNodes('//testcase/skipped'))
    $failedCases = @($junit.SelectNodes('//testcase/failure|//testcase/error'))
    $declaredSkipped = 0
    foreach ($suite in @($junit.SelectNodes('//testsuite[@skipped]'))) {
        $value = 0
        if ([int]::TryParse($suite.skipped, [ref]$value)) {
            $declaredSkipped += $value
        }
    }
    $skipped = [Math]::Max($skippedCases.Count, $declaredSkipped)
    if ($testCases.Count -eq 0) {
        throw "CTest JUnit receipt contains no test cases: $Path"
    }
    if ($failedCases.Count -ne 0) {
        throw "CTest JUnit receipt contains $($failedCases.Count) failed/error test case(s): $Path"
    }
    if ($skipped -ne 0) {
        $names = @(
            $skippedCases | ForEach-Object { $_.ParentNode.name }
        ) -join ', '
        throw "Release qualification forbids skipped native tests; found $skipped skipped test(s) ($names): $Path"
    }
    $testCaseNames = @($testCases | ForEach-Object { [string]$_.name })
    $missingRequiredCases = @(
        $RequiredTestCases | Where-Object { $testCaseNames -notcontains $_ }
    )
    if ($missingRequiredCases) {
        throw "CTest JUnit receipt omitted required qualification case(s): $($missingRequiredCases -join ', ')"
    }
    # CTest writes the local computer name by default. It has no evidentiary
    # value for this receipt and must not leak a developer/CI machine identity.
    foreach ($suite in @($junit.SelectNodes('//testsuite[@hostname]'))) {
        $suite.RemoveAttribute('hostname')
    }
    $junit.Save($Path)
    return [pscustomobject]@{
        Total = $testCases.Count
        Skipped = $skipped
        QualifiedCases = @($RequiredTestCases)
    }
}

function Get-LocalLeakTokens {
    $tokens = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $paths = @(
        $projectRoot,
        $resolvedScriptHookSdkRoot,
        $nativeBuild,
        $env:USERPROFILE,
        (Split-Path $cmake.Source -Parent),
        $(if (-not [string]::IsNullOrWhiteSpace($ctestPath)) {
            Split-Path $ctestPath -Parent
        })
    )
    foreach ($toolName in @('cmake', 'ctest', 'dotnet', 'node', 'pnpm', 'msbuild')) {
        $tool = Get-Command $toolName -ErrorAction SilentlyContinue
        if ($tool -and -not [string]::IsNullOrWhiteSpace($tool.Source)) {
            $paths += Split-Path $tool.Source -Parent
        }
    }
    foreach ($path in $paths) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $fullPath = [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
        if ($fullPath.Length -lt 5) { continue }
        $null = $tokens.Add($fullPath)
        $null = $tokens.Add($fullPath.Replace('\', '/'))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
        $null = $tokens.Add("Users\$($env:USERNAME)")
        $null = $tokens.Add("Users/$($env:USERNAME)")
        $null = $tokens.Add("\$($env:USERNAME)\")
        $null = $tokens.Add("/$($env:USERNAME)/")
    }
    return @($tokens)
}

function Assert-NoLocalPathLeaks {
    param(
        [Parameter(Mandatory)] [IO.FileInfo[]]$Files,
        [Parameter(Mandatory)] [string[]]$Tokens,
        [Parameter(Mandatory)] [string]$Label
    )

    Initialize-PackageBinaryInspection
    foreach ($file in $Files) {
        $leak = [ReactorV.Package.BinaryInspection]::FindLeak(
            $file.FullName, $Tokens)
        if (-not [string]::IsNullOrWhiteSpace($leak)) {
            throw "$Label contains a developer-local path token '$leak': $($file.FullName)"
        }
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

function Assert-TraceStageOrder {
    param(
        [Parameter(Mandatory)] [string]$Trace,
        [Parameter(Mandatory)] [string[]]$Stages
    )

    $cursor = -1
    foreach ($stage in $Stages) {
        $marker = "stage=$stage"
        $position = $Trace.IndexOf($marker, $cursor + 1, [StringComparison]::Ordinal)
        if ($position -le $cursor) {
            throw "Reactor stage '$stage' was missing or out of order."
        }
        $cursor = $position
    }
}

function Assert-TraceStageMinimum {
    param(
        [Parameter(Mandatory)] [string]$Trace,
        [Parameter(Mandatory)] [string]$Stage,
        [Parameter(Mandatory)] [int]$MinimumCount
    )

    $marker = "stage=$Stage"
    $count = ([regex]::Matches($Trace, [regex]::Escape($marker))).Count
    if ($count -lt $MinimumCount) {
        throw "Expected at least $MinimumCount '$marker' records; found $count."
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
    # Windows PowerShell 5.1 can discard the native process handle when
    # Start-Process redirects or owns the child. Capture it immediately so
    # ExitCode remains available after WaitForExit.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.Dispose()
        throw "The Reactor V preloader harness '$InstanceId' exceeded $TimeoutMilliseconds ms."
    }
    # PowerShell 5.1 can leave ExitCode unset after the timed overload. A
    # parameterless wait plus Refresh synchronizes the terminal process state.
    $process.WaitForExit()
    $process.Refresh()
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

$configureArguments = @(
    '-S', $nativeRoot,
    '-B', $nativeBuild,
    '-G', 'Visual Studio 17 2022',
    '-A', 'x64',
    "-DREACTORV_SCRIPTHOOK_SDK_ROOT=$resolvedScriptHookSdkRoot",
    "-DREACTORV_ENABLE_LEGACY_STARTUP_SHADOW=$(if ($legacyStartupObserverEnabled) { 'ON' } else { 'OFF' })"
)

Invoke-Checked $cmake.Source @configureArguments
Invoke-Checked $cmake.Source '--build' $nativeBuild '--config' $Configuration

$nativeTestsQualified = $false
$ctest = $null
$ctestPath = $null
if (-not $SkipTests) {
    $cmakeSiblingCTest = Join-Path (Split-Path $cmake.Source -Parent) 'ctest.exe'
    if (Test-Path -LiteralPath $cmakeSiblingCTest -PathType Leaf) {
        $ctestPath = (Get-Item -LiteralPath $cmakeSiblingCTest).FullName
    } else {
        $ctest = Get-Command 'ctest' -ErrorAction SilentlyContinue
        if ($ctest) { $ctestPath = $ctest.Source }
    }
    if ([string]::IsNullOrWhiteSpace($ctestPath)) {
        throw 'CTest is required to run the native tests.'
    }
    New-Item -ItemType Directory -Path (Split-Path $nativeCTestReportPath -Parent) -Force | Out-Null
    Invoke-Checked $ctestPath `
        '--test-dir' $nativeBuild `
        '--build-config' $Configuration `
        '--output-on-failure' `
        '--output-junit' $nativeCTestReportPath
    $nativeTestReceipt = Assert-CtestJUnit `
        -Path $nativeCTestReportPath `
        -RequiredTestCases @(
            'ReactorV.D3D11OverlayRenderer.HotPath',
            'ReactorV.LegacyHook.Integration',
            'ReactorV.LegacyHook.ResizeExternalLifecycle',
            'ReactorV.LegacyHook.FlipExternalLifecycle'
            'ReactorV.D3D11DeviceProbe'
            'ReactorV.SharedGpuFrame.LegacyCompatibility'
            'ReactorV.LegacyD3D11FrameBridge'
            'ReactorV.LegacyCpuFrameBridge'
        )
    $harnessReport.native_tests.total = $nativeTestReceipt.Total
    $harnessReport.native_tests.skipped = $nativeTestReceipt.Skipped
    $harnessReport.native_tests.junit =
        ConvertTo-ProjectRelativeArtifactPath $nativeCTestReportPath
    $harnessReport.native_tests.qualified_cases =
        @($nativeTestReceipt.QualifiedCases)
    $nativeTestsQualified = $true
}

$nativeCandidates = @(
    (Join-Path $nativeBuild "$Configuration\RageWebUI.Native.dll"),
    (Join-Path $nativeBuild 'RageWebUI.Native.dll')
)
$nativeLibrary = $nativeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $nativeLibrary) {
    throw "Expected native build output was not found beneath: $nativeBuild"
}
Assert-X64PeImage -Path $nativeLibrary
Assert-NativeExports -Path $nativeLibrary -Names @(
    'RWUI_GetSharedTextureCapabilities',
    'RWUI_StartSharedTextureProducer',
    'RWUI_StopSharedTextureProducer',
    'RWUI_ProbeSharedTexture',
    'RWUI_SubmitSharedTexture',
    'RWUI_SubmitSharedTextureStatus'
)
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
$nativeScriptProbeCandidates = @(
    (Join-Path $nativeBuild "$Configuration\ReactorV.ScriptProbe.asi"),
    (Join-Path $nativeBuild 'ReactorV.ScriptProbe.asi')
)
$nativeScriptProbe = $nativeScriptProbeCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $nativeScriptProbe) {
    throw "Expected ReactorV.ScriptProbe.asi build output was not found beneath: $nativeBuild"
}
Assert-X64PeImage -Path $nativeScriptProbe
$nativeRenderHook = $null
if ($includeExperimentalRenderHook) {
    $nativeRenderHookCandidates = @(
        (Join-Path $nativeBuild "$Configuration\ReactorV.RenderHook.asi"),
        (Join-Path $nativeBuild 'ReactorV.RenderHook.asi')
    )
    $nativeRenderHook = $nativeRenderHookCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $nativeRenderHook) {
        throw "Expected ReactorV.RenderHook.asi build output was not found beneath: $nativeBuild"
    }
    Assert-X64PeImage -Path $nativeRenderHook
    $editionHookExports = if ($IncludeExperimentalEnhancedRenderHook) {
        @(
            'RWUI_ArmEnhancedHook',
            'RWUI_BindEnhancedTarget',
            'RWUI_GetEnhancedHookDiagnostics'
        )
    } else {
        @(
            'RWUI_ArmLegacyHook',
            'RWUI_BindLegacyTarget',
            'RWUI_GetLegacyHookDiagnostics'
        )
    }
    $requiredHookExports = @(
        'RWUI_Initialize',
        'RWUI_SetVisible',
        'RWUI_SetSharedTextureProducerVisible'
    ) + $editionHookExports
    Assert-NativeExports -Path $nativeLibrary -Names $requiredHookExports
}
$nativeRouteProbe = $null
if (-not $SkipTests -and -not $SkipHarness) {
    $nativeRouteProbeCandidates = @(
        (Join-Path $nativeBuild "$Configuration\ReactorV.Bootstrap.RouteProbe.exe"),
        (Join-Path $nativeBuild 'ReactorV.Bootstrap.RouteProbe.exe')
    )
    $nativeRouteProbe = $nativeRouteProbeCandidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    if (-not $nativeRouteProbe) {
        throw "Expected ReactorV.Bootstrap.RouteProbe.exe test output was not found beneath: $nativeBuild"
    }
}

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
    if (-not $SkipHarness -and -not $SkipTests) {
        # A separate consumer regression fixture; NEVER copied to runtime staging.
        Invoke-Checked $pnpm.Source 'build:allin1'
        & (Join-Path $projectRoot 'tools/test-runtime-content-boundary.ps1')
    }
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
    if (-not $nativeTestsQualified) {
        throw 'Native tests cannot be marked passed without a zero-skip CTest JUnit receipt.'
    }
    $harnessReport.suites.native = 'passed'
    $harnessReport.suites.web = 'passed'
    $harnessReport.suites.core = 'passed'
    $harnessReport.suites.extension_examples = 'passed'
}

Copy-Item -LiteralPath $nativeLibrary -Destination $harnessOutput -Force
if ($nativeRouteProbe) {
    Copy-Item -LiteralPath $nativeRouteProbe -Destination $harnessOutput -Force
}
$harnessUi = Join-Path $harnessOutput 'ui'
if (Test-Path -LiteralPath $harnessUi) {
    Remove-Item -LiteralPath $harnessUi -Recurse -Force
}
Copy-Item -LiteralPath $webOutput -Destination $harnessUi -Recurse

# The fallback harness runs Runtime inside a ScriptHookVDotNet-like secondary
# AppDomain. Exercise the same out-of-process desktop witness that ships beside
# the UI, rather than silently failing because the development harness folder
# omitted its executable.
foreach ($file in @(
    'ReactorV.Preloader.exe',
    'ReactorV.Preloader.exe.config'
) + $desktopPresentationProbeDependencies) {
    $source = Join-Path $preloaderOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Expected harness desktop-presentation-probe file is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $harnessOutput -Force
}

if (-not $SkipTests -and -not $SkipHarness) {
    $harness = Join-Path $harnessOutput 'RageWebUI.Harness.exe'
    Invoke-Checked $harness '--api' 'd3d11' '--smoke'
    $harnessReport.suites.d3d11 = 'passed'
    Invoke-Checked $harness '--api' 'd3d12' '--smoke'
    $harnessReport.suites.d3d12 = 'passed'
    Invoke-CefColdStartStabilityGate `
        -Harness $harness `
        -Api 'd3d11' `
        -Cycles $CefColdStartCycles
    $harnessReport.cef.d3d11_passed = $CefColdStartCycles
    Invoke-CefColdStartStabilityGate `
        -Harness $harness `
        -Api 'd3d12' `
        -Cycles $CefColdStartCycles
    $harnessReport.cef.d3d12_passed = $CefColdStartCycles
    $harnessReport.suites.cef_cold_start = 'passed'
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
Copy-Item -LiteralPath $nativeScriptProbe -Destination (Join-Path $stagingRoot 'ReactorV.ScriptProbe.asi')
if ($nativeRenderHook) {
    Copy-Item -LiteralPath $nativeRenderHook -Destination (
        Join-Path $stagingRoot 'ReactorV.RenderHook.asi')
}

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
Copy-Item -LiteralPath (Join-Path $projectRoot 'ReactorV.contract.json') -Destination $bootstrapRoot

$runtimeManagedFiles = @(
    'RageWebUI.Runtime.dll',
    'RageWebUI.DirectX.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll'
)
$persistentHostDependencies = @(
    'RageWebUI.Runtime.dll',
    'RageWebUI.DirectX.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll'
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
foreach ($file in $runtimeManagedFiles) {
    $source = Join-Path $runtimeOutput $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected renderer output is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $rendererRoot
}

# The opt-in external GPU browser is constructed by ReactorV.Preloader.exe.
# Stage its CEF closure from that executable's own build output, rather than a
# sibling project's transitive output, so the packaged producer and CEF ABI
# cannot silently drift apart.
foreach ($file in $cefFiles) {
    $source = Join-Path $preloaderOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Expected external GPU browser dependency is missing from Preloader output: $source"
    }
    Copy-Item -LiteralPath $source -Destination $rendererRoot -Force
}

$loader = Join-Path $runtimeOutput 'runtimes\win-x64\native\WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $loader)) {
    throw "Expected WebView2 loader is missing: $loader"
}
Copy-Item -LiteralPath $loader -Destination $rendererRoot
Copy-Item -LiteralPath $nativeLibrary -Destination $rendererRoot
Copy-Item -LiteralPath (Join-Path $harnessOutput 'RageWebUI.Harness.exe') -Destination $rendererRoot
Copy-Item -LiteralPath (Join-Path $harnessOutput 'RageWebUI.Harness.exe.config') -Destination $rendererRoot
foreach ($file in $desktopPresentationProbeDependencies) {
    # These assemblies now belong to the production Preloader child probe.
    # Copy from that output so staging cannot accidentally depend on a
    # developer-only Harness build supplying the dependency closure.
    $source = Join-Path $preloaderOutput $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Expected production desktop-presentation-probe dependency is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $rendererRoot -Force
}
if ($nativeRouteProbe) {
    Copy-Item -LiteralPath $nativeRouteProbe -Destination $rendererRoot -Force
}
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
$packagedPreloaderSettingsPath = Join-Path $rendererRoot 'ReactorV.Preloader.json'
$packagedPreloaderSettings = Get-Content `
    -LiteralPath $packagedPreloaderSettingsPath `
    -Raw | ConvertFrom-Json
# Repository/developer runs default to the proven native-GPU path. A packaged
# artifact must instead describe the renderer it actually ships: edition-
# specific live-test packages include ReactorV.RenderHook.asi and keep the
# native path enabled; public/developer packages do not ship that hook and
# explicitly seed the managed fallback. This assignment also prevents a stale
# source-tree setting from leaking into the wrong artifact kind.
$packagedPreloaderSettings.externalGpuBrowserShadow =
    [bool]$includeExperimentalRenderHook
$packagedPreloaderSettings |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $packagedPreloaderSettingsPath -Encoding utf8
$packagedPreloaderSettings = Get-Content `
    -LiteralPath $packagedPreloaderSettingsPath `
    -Raw | ConvertFrom-Json
$expectedExternalGpuBrowserDefault = $includeExperimentalRenderHook
if ([bool]$packagedPreloaderSettings.externalGpuBrowserShadow -ne
    $expectedExternalGpuBrowserDefault) {
    throw "Packaged externalGpuBrowserShadow did not match the artifact kind '$artifactKind'."
}
$packagedExternalGpuFrameRate = [int]$packagedPreloaderSettings.externalGpuFrameRate
if ($packagedExternalGpuFrameRate -lt 15 -or
    $packagedExternalGpuFrameRate -gt 60) {
    throw 'Packaged externalGpuFrameRate must be between 15 and 60 FPS.'
}
$harnessReport.external_gpu_browser.enabled_by_default =
    $expectedExternalGpuBrowserDefault
$harnessReport.external_gpu_browser.frame_rate =
    $packagedExternalGpuFrameRate

$enhancedLiveTestMarkerPath = Join-Path $rendererRoot $enhancedLiveTestMarkerName
$legacyLiveTestMarkerPath = Join-Path $rendererRoot $legacyLiveTestMarkerName
if ($IncludeExperimentalEnhancedRenderHook) {
    [ordered]@{
        schema_version = 1
        artifact_kind = 'enhanced-live-test'
        public_release = $false
        target_edition = 'Enhanced'
        game_executable = $enhancedLiveTestGameExecutable
        game_version = $enhancedLiveTestGameVersion
        game_sha256 = $enhancedLiveTestGameSha256
        experimental_render_hook = $true
    } |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $enhancedLiveTestMarkerPath -Encoding utf8
} elseif ($IncludeExperimentalLegacyRenderHook) {
    [ordered]@{
        schema_version = 1
        artifact_kind = 'legacy-live-test'
        public_release = $false
        target_edition = 'Legacy'
        game_executable = $legacyLiveTestGameExecutable
        game_version = $legacyLiveTestGameVersion
        game_sha256 = $legacyLiveTestGameSha256
        experimental_render_hook = $true
    } |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $legacyLiveTestMarkerPath -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $nativeRoot "probe/$legacyCpuFrameMarkerName") `
        -Destination (Join-Path $rendererRoot $legacyCpuFrameMarkerName)
}
if (-not $IncludeExperimentalEnhancedRenderHook -and
    (Test-Path -LiteralPath $enhancedLiveTestMarkerPath)) {
    throw 'The Enhanced live-test marker must never appear in another edition, public, or developer player package.'
}
if (-not $IncludeExperimentalLegacyRenderHook -and
    (Test-Path -LiteralPath $legacyLiveTestMarkerPath)) {
    throw 'The Legacy live-test marker must never appear in another edition, public, or developer player package.'
}

# The persistent host now creates the production OverlayWindow itself, so its
# complete managed/native WebView dependency closure must survive packaging.
# Verify the staged copies are byte-for-byte the assemblies the Preloader was
# compiled and smoke-tested against instead of relying on transitive copy luck.
foreach ($file in $persistentHostDependencies) {
    $preloaderDependency = Join-Path $preloaderOutput $file
    $packagedDependency = Join-Path $rendererRoot $file
    if (-not (Test-Path -LiteralPath $preloaderDependency -PathType Leaf)) {
        throw "Expected persistent-host dependency is missing from Preloader output: $preloaderDependency"
    }
    Copy-Item -LiteralPath $preloaderDependency -Destination $packagedDependency -Force
    $expectedHash = (Get-FileHash -LiteralPath $preloaderDependency -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $packagedDependency -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) {
        throw "Persistent-host dependency does not match the Preloader build: $file"
    }
}

$externalGpuBrowserDependencies = @(
    'RageWebUI.DirectX.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll'
) + $cefFiles
foreach ($file in $externalGpuBrowserDependencies) {
    $preloaderDependency = Join-Path $preloaderOutput $file
    $packagedDependency = Join-Path $rendererRoot $file
    if (-not (Test-Path -LiteralPath $preloaderDependency -PathType Leaf)) {
        throw "Expected external GPU browser dependency is missing from Preloader output: $preloaderDependency"
    }
    if (-not (Test-Path -LiteralPath $packagedDependency -PathType Leaf)) {
        throw "External GPU browser package dependency is missing: $packagedDependency"
    }
    $expectedHash = (Get-FileHash -LiteralPath $preloaderDependency -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $packagedDependency -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) {
        throw "External GPU browser dependency does not match the Preloader build: $file"
    }
}

$localeRoot = Join-Path $rendererRoot 'locales'
New-Item -ItemType Directory -Path $localeRoot -Force | Out-Null
$externalGpuBrowserLocales = @(
    Get-ChildItem (Join-Path $preloaderOutput 'locales') -Filter 'en-US*.pak' -File
)
if (-not ($externalGpuBrowserLocales | Where-Object { $_.Name -eq 'en-US.pak' })) {
    throw "Expected external GPU browser locale is missing from Preloader output: locales\en-US.pak"
}
$externalGpuBrowserLocales | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $localeRoot
}

Copy-Item -LiteralPath $webOutput -Destination (Join-Path $rendererRoot 'ui') -Recurse
& (Join-Path $projectRoot 'tools/Assert-ReactorRuntimeContent.ps1') -UiRoot (Join-Path $rendererRoot 'ui')

if (-not $SkipTests -and -not $SkipHarness) {
    $preloader = Join-Path $rendererRoot 'ReactorV.Preloader.exe'
    $sharedWebViewRoot = Join-Path $artifactsRoot 'harness\SharedWebView2'
    $sharedWebViewProfile = Join-Path $sharedWebViewRoot 'WebView2'
    if (Test-Path -LiteralPath $sharedWebViewRoot) {
        Remove-Item -LiteralPath $sharedWebViewRoot -Recurse -Force
    }
    $packagedHarness = Join-Path $rendererRoot 'RageWebUI.Harness.exe'

    # Qualify the exact staged opt-in producer without GTA. The packaged
    # DirectX harness owns a real D3D11/D3D12 swap chain but submits no CPU
    # frames; the packaged persistent Preloader is the actual external CEF
    # producer. The gate requires authenticated shared-frame rendering,
    # content-ready, visible activation, and clean producer-first teardown for
    # both Legacy- and Enhanced-style renderer paths.
    $externalGpuQualificationTool = Join-Path `
        $projectRoot `
        'tools\qualify-external-gpu-browser.ps1'
    if (-not (Test-Path -LiteralPath $externalGpuQualificationTool -PathType Leaf)) {
        throw "External-GPU package qualification tool is missing: $externalGpuQualificationTool"
    }
    $externalGpuHarnessRoot = Join-Path `
        $artifactsRoot `
        'harness\ExternalGpuBrowser'
    if (Test-Path -LiteralPath $externalGpuHarnessRoot) {
        Remove-Item -LiteralPath $externalGpuHarnessRoot -Recurse -Force
    }
    $externalGpuQualificationJson = & $externalGpuQualificationTool `
        -Harness $packagedHarness `
        -Preloader $preloader `
        -UiDirectory (Join-Path $rendererRoot 'ui') `
        -OutputRoot $externalGpuHarnessRoot
    $externalGpuQualification =
        ($externalGpuQualificationJson -join [Environment]::NewLine) |
        ConvertFrom-Json
    if ($externalGpuQualification.schema_version -ne 1 -or
        $externalGpuQualification.enabled_by_default -ne $false) {
        throw 'External-GPU package qualification returned an invalid or non-opt-in receipt.'
    }
    foreach ($api in @('d3d11', 'd3d12')) {
        $apiResult = $externalGpuQualification.$api
        if ($apiResult.submitted_cpu_frames -ne 0 -or
            $apiResult.rendered_shared_frames -lt 1 -or
            $apiResult.last_shared_generation -lt 1) {
            throw "External-GPU $api receipt did not prove an exclusively shared rendered frame."
        }
        $apiResult.trace = ConvertTo-ProjectRelativeArtifactPath $apiResult.trace
        $harnessReport.external_gpu_browser[$api] = $apiResult
    }
    $harnessReport.suites.external_gpu_browser_shadow = 'passed'

    # Exercise the exact staged Core/Harness pair against the public v2
    # integration path before any renderer smoke tests can mask a contract
    # mismatch.
    Invoke-Checked $packagedHarness '--scenario' 'api-contract'
    $harnessReport.suites.api_contract = 'passed'
    Invoke-Checked $packagedHarness '--scenario' 'standalone-prefabs' `
        '--ui' (Join-Path $rendererRoot 'ui') `
        '--local-data-dir' (Join-Path $artifactsRoot ('harness\StandalonePrefabs-' + [Guid]::NewGuid().ToString('N')))
    $harnessReport.suites['standalone_prefabs'] = 'passed'
    $harnessReport.package['ui_profile'] = 'reactor-runtime'
    $harnessReport.package['contains_consumer_ui'] = $false

    # Run the separate consumer adapter with the packaged host through GBAY.
    # Standalone renderer/startup checks above use the actual packaged React UI.
    # The adapter is a regression fixture, not part of the Reactor package.
    # Exercise the Story Mode GBAY
    # lifecycle. This keeps the browser cold-prepared and hidden, requires the
    # painted-presentation acknowledgement before reveal, samples every visible
    # transition for black/transparent/About/setup intermediates, drives every
    # top-level GBAY route plus typed fixture actions, and qualifies route
    # restore/fallback, close, warm reopen, and rapid toggle.
    $gbayHarnessRoot = Join-Path $artifactsRoot 'harness\GbayLifecycle'
    if (Test-Path -LiteralPath $gbayHarnessRoot) {
        Remove-Item -LiteralPath $gbayHarnessRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $gbayHarnessRoot -Force | Out-Null
    $gbayStdout = Join-Path $gbayHarnessRoot 'harness.stdout.log'
    $gbayStderr = Join-Path $gbayHarnessRoot 'harness.stderr.log'
    # UI-relative host discovery is part of production behavior. Keep that
    # layout in an isolated consumer fixture, not in player staging.
    $consumerRuntime = Join-Path $artifactsRoot ('harness\ConsumerRuntime-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $consumerRuntime -Force | Out-Null
    Get-ChildItem -LiteralPath $rendererRoot -Force | Where-Object Name -ne 'ui' |
        Copy-Item -Destination $consumerRuntime -Recurse
    Copy-Item -LiteralPath (Join-Path $webRoot 'dist-allin1') -Destination (Join-Path $consumerRuntime 'ui') -Recurse
    $gbayProcess = Start-Process `
        -FilePath $packagedHarness `
        -ArgumentList @(
            '--scenario', 'gbay-lifecycle',
            '--ui', ('"{0}"' -f (Join-Path $consumerRuntime 'ui')),
            '--local-data-dir', ('"{0}"' -f $gbayHarnessRoot),
            '--gbay-cold-ready-budget-ms', "$GbayColdReadyBudgetMs",
            '--gbay-first-presentation-budget-ms', "$GbayFirstPresentationBudgetMs",
            '--gbay-warm-presentation-budget-ms', "$GbayWarmPresentationBudgetMs",
            '--gbay-close-budget-ms', "$GbayCloseBudgetMs"
        ) `
        -RedirectStandardOutput $gbayStdout `
        -RedirectStandardError $gbayStderr `
        -WindowStyle Hidden `
        -PassThru
    $null = $gbayProcess.Handle
    try {
        if (-not $gbayProcess.WaitForExit(60000)) {
            throw 'The packaged GBAY lifecycle harness exceeded 60000 ms.'
        }
        $gbayProcess.WaitForExit()
        $gbayProcess.Refresh()
        $gbayOutput = if (Test-Path -LiteralPath $gbayStdout) {
            [IO.File]::ReadAllText($gbayStdout)
        } else { '' }
        $gbayError = if (Test-Path -LiteralPath $gbayStderr) {
            [IO.File]::ReadAllText($gbayStderr)
        } else { '' }
        if ($gbayProcess.ExitCode -ne 0 -or
            -not $gbayOutput.Contains('RESULT PASS: scenario=gbay-lifecycle')) {
            throw "The packaged GBAY lifecycle harness failed with exit code $($gbayProcess.ExitCode).`n$gbayOutput`n$gbayError"
        }
        foreach ($requiredProof in @(
            'coldPrepared=True',
            'firstCompositionRefresh=True',
            'warmCompositionRefresh=True',
            'targetReuse=True',
            'rapidStable=True',
            'rapidNoIntermediate=True',
            'noBlack=True',
            'noTransparent=True',
            'transparentSurround=True',
            'noInterstitial=True',
            'dataActions=True',
            'routeMatrix=True',
            'routeRestored=True',
            'routeFallback=True',
            'routeCoverage=9/9',
            'stressMenuGets=0',
            'staleAcks=0',
            'stressCycles=100'
        )) {
            if (-not $gbayOutput.Contains($requiredProof)) {
                throw "The GBAY lifecycle harness did not prove '$requiredProof'.`n$gbayOutput"
            }
        }

        $invariantCulture = [Globalization.CultureInfo]::InvariantCulture
        $harnessReport.suites.gbay_lifecycle = 'passed'
        $harnessReport.gbay.cold_ready_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'coldReadyMs'), $invariantCulture)
        $harnessReport.gbay.first_presentation_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'firstPresentationMs'), $invariantCulture)
        $harnessReport.gbay.close_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'closeMs'), $invariantCulture)
        $harnessReport.gbay.warm_presentation_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'warmPresentationMs'), $invariantCulture)
        $harnessReport.gbay.rapid_toggle_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'rapidToggleMs'), $invariantCulture)
        $harnessReport.gbay.maximum_black_fraction = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'blackMax'), $invariantCulture)
        $harnessReport.gbay.minimum_changed_fraction = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'changedMin'), $invariantCulture)
        $harnessReport.gbay.minimum_green_fraction = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'greenMin'), $invariantCulture)
        $harnessReport.gbay.maximum_blue_fraction = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'blueMax'), $invariantCulture)
        $harnessReport.gbay.menu_gets = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'menuGets'), $invariantCulture)
        $harnessReport.gbay.stress_menu_gets = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressMenuGets'), $invariantCulture)
        $harnessReport.gbay.menu_revision = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'menuRevision'), $invariantCulture)
        $harnessReport.gbay.menu_invokes = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'menuInvokes'), $invariantCulture)
        $expectedMenuGets = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'expectedMenuGets'), $invariantCulture)
        if ($harnessReport.gbay.menu_gets -ne $expectedMenuGets) {
            throw "The GBAY lifecycle cache issued $($harnessReport.gbay.menu_gets) menu.get requests; expected $expectedMenuGets for the full and removed-route trees."
        }
        $harnessReport.gbay.expected_menu_gets = $expectedMenuGets
        $harnessReport.gbay.route_coverage = Get-HarnessOutputValue $gbayOutput 'routeCoverage'
        $harnessReport.gbay.typed_invocations = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'typedInvokes'), $invariantCulture)
        $harnessReport.gbay.ready_acknowledgements = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'readyAcks'), $invariantCulture)
        $harnessReport.gbay.stale_acknowledgements = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'staleAcks'), $invariantCulture)
        $harnessReport.gbay.stress_cycles = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressCycles'), $invariantCulture)
        $harnessReport.gbay.stress_ready_p50_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressReadyP50Ms'), $invariantCulture)
        $harnessReport.gbay.stress_ready_p95_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressReadyP95Ms'), $invariantCulture)
        $harnessReport.gbay.stress_ready_max_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressReadyMaxMs'), $invariantCulture)
        $harnessReport.gbay.stress_reveal_p50_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressRevealP50Ms'), $invariantCulture)
        $harnessReport.gbay.stress_reveal_p95_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressRevealP95Ms'), $invariantCulture)
        $harnessReport.gbay.stress_reveal_max_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressRevealMaxMs'), $invariantCulture)
        $harnessReport.gbay.stress_end_to_end_p50_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressEndToEndP50Ms'), $invariantCulture)
        $harnessReport.gbay.stress_end_to_end_p95_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressEndToEndP95Ms'), $invariantCulture)
        $harnessReport.gbay.stress_end_to_end_max_ms = [double]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'stressEndToEndMaxMs'), $invariantCulture)
        $harnessReport.gbay.effective_client_width = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'clientWidth'), $invariantCulture)
        $harnessReport.gbay.effective_client_height = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'clientHeight'), $invariantCulture)
        $harnessReport.gbay.effective_dpi = [int]::Parse(
            (Get-HarnessOutputValue $gbayOutput 'effectiveDpi'), $invariantCulture)
        $harnessReport.gbay.trace = ConvertTo-ProjectRelativeArtifactPath (
            Join-Path $gbayHarnessRoot 'reactorv-runtime.log')
        $harnessReport.gbay.first_screenshot = ConvertTo-ProjectRelativeArtifactPath (
            Join-Path $gbayHarnessRoot 'first-presentation.png')
        $harnessReport.gbay.warm_screenshot = ConvertTo-ProjectRelativeArtifactPath (
            Join-Path $gbayHarnessRoot 'warm-presentation.png')
        Write-Host $gbayOutput.Trim()
    }
    finally {
        if (-not $gbayProcess.HasExited) {
            Stop-Process -Id $gbayProcess.Id -Force -ErrorAction SilentlyContinue
        }
        $gbayProcess.Dispose()
    }

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
    # Production preload intentionally remains transparent. Qualify the shell
    # and executable bundles here; presentation-only images are validated as
    # packaged files below and must not be fetched just to satisfy this trace.
    foreach ($requiredMetric in @(
        '"readyState":"complete"',
        '"rootChildren":1',
        '"name":"app-',
        '.css"'
    )) {
        if (-not $performanceTrace.Text.Contains($requiredMetric)) {
            throw "The packaged Reactor V page trace is missing '$requiredMetric'."
        }
    }
    # Vite names the second application chunk after whichever shared module is
    # currently dominant (for example bridge-* or controller-*). Verify the
    # actual executable-resource contract instead of coupling qualification to
    # an optimizer-generated chunk label.
    $scriptResources = [regex]::Matches(
        $performanceTrace.Text,
        '"name":"[^"]+\.js"'
    ).Count
    if ($scriptResources -lt 2) {
        throw "The packaged Reactor V page trace contains only $scriptResources JavaScript resource(s); expected at least two."
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
    $harnessReport.preloader.trace =
        ConvertTo-ProjectRelativeArtifactPath $performanceTrace.Path
    Write-Host (
        "Reactor preloader performance PASS: content-ready={0:F1} ms, released={1:F1} ms" -f `
            $contentReadyMs,
            $releasedMs
    )

    # Launch the packaged harness first so its PID and top-level window stand
    # in for GTA. The packaged Preloader then owns the WebView for that PID.
    # The provider intentionally waits beyond the old release budget before it
    # attaches from a secondary AppDomain; recreating or releasing the browser
    # causes this qualification to fail deterministically.
    $bootstrapHostHarnessRoot = Join-Path $artifactsRoot 'harness\BootstrapHost'
    if (Test-Path -LiteralPath $bootstrapHostHarnessRoot) {
        Remove-Item -LiteralPath $bootstrapHostHarnessRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $bootstrapHostHarnessRoot -Force | Out-Null
    $bootstrapStdout = Join-Path $bootstrapHostHarnessRoot 'harness.stdout.log'
    $bootstrapStderr = Join-Path $bootstrapHostHarnessRoot 'harness.stderr.log'
    $bootstrapHarness = Start-Process `
        -FilePath $packagedHarness `
        -ArgumentList @(
            '--scenario', 'bootstrap-host',
            '--duration', '15',
            '--bootstrap-warm-delay-ms', "$PersistentHostWarmDelayMs",
            '--ui', ('"{0}"' -f (Join-Path $consumerRuntime 'ui')),
            '--local-data-dir', ('"{0}"' -f (Join-Path $bootstrapHostHarnessRoot 'Runtime'))
        ) `
        -RedirectStandardOutput $bootstrapStdout `
        -RedirectStandardError $bootstrapStderr `
        -WindowStyle Hidden `
        -PassThru
    $null = $bootstrapHarness.Handle
    $bootstrapPreloader = $null
    try {
        $windowDeadline = [Diagnostics.Stopwatch]::StartNew()
        while (-not $bootstrapHarness.HasExited -and
            $bootstrapHarness.MainWindowHandle -eq [IntPtr]::Zero -and
            $windowDeadline.ElapsedMilliseconds -lt 5000) {
            Start-Sleep -Milliseconds 25
            $bootstrapHarness.Refresh()
        }
        if ($bootstrapHarness.HasExited) {
            throw "The bootstrap-host harness exited before Preloader startup:`n$([IO.File]::ReadAllText($bootstrapStderr))"
        }
        if ($bootstrapHarness.MainWindowHandle -eq [IntPtr]::Zero) {
            throw 'The bootstrap-host harness did not publish a target window within 5000 ms.'
        }

        $bootstrapPreloader = Start-Process `
            -FilePath $preloader `
            -ArgumentList @(
                '--persistent-host',
                '--bootstrap-harness-webview-presenter',
                '--parent-pid', "$($bootstrapHarness.Id)",
                '--ui-dir', ('"{0}"' -f (Join-Path $consumerRuntime 'ui')),
                '--user-data-dir', ('"{0}"' -f (Join-Path $bootstrapHostHarnessRoot 'WebView2')),
                '--log-dir', ('"{0}"' -f (Join-Path $bootstrapHostHarnessRoot 'Logs')),
                '--instance-id', "bootstrap-host-$([Guid]::NewGuid().ToString('N'))"
            ) `
            -PassThru `
            -WindowStyle Hidden
        $null = $bootstrapPreloader.Handle

        if (-not $bootstrapHarness.WaitForExit(40000)) {
            throw 'The packaged bootstrap-host harness exceeded 40000 ms.'
        }
        $bootstrapHarness.WaitForExit()
        $bootstrapHarness.Refresh()
        $bootstrapOutput = if (Test-Path -LiteralPath $bootstrapStdout) {
            [IO.File]::ReadAllText($bootstrapStdout)
        } else { '' }
        $bootstrapError = if (Test-Path -LiteralPath $bootstrapStderr) {
            [IO.File]::ReadAllText($bootstrapStderr)
        } else { '' }
        if ($bootstrapHarness.ExitCode -ne 0) {
            throw "The packaged bootstrap-host harness failed with exit code $($bootstrapHarness.ExitCode).`n$bootstrapOutput`n$bootstrapError"
        }
        foreach ($requiredProof in @(
            'scenario=bootstrap-host',
            'survivedWarmWindow=True',
            'preProviderTransportReady=True',
            'mainMenuAbout=True',
            'packagedRoutePolicy=True',
            'neutralVerification=True',
            'verificationActive=True',
            'verificationActiveReset=True',
            'verificationPromotedInPlace=True',
            'aboutSinglePopup=True',
            'aboutNoIntent=True',
            'aboutClosed=True',
            'preStoryInitializerSignal=True',
            'startupSurface=True',
            'startupTopMost=True',
            'startupDemotedOnClose=True',
            'startupChecks=3',
            'startupConsoleBounded=True',
            'startupCopyContract=True',
            'earlyEscapeClose=True',
            'closedIntentCleared=True',
            'earlyIntentPreserved=True',
            'providerMenuReady=True',
            'providerStartupStatus=True',
            'providerStatusRequestedMenu=True',
            'runtimeReadyInitializerPreserved=True',
            'intentConsumedOnce=True',
            'initialPresentationReady=True',
            'readyIdle=True',
            'readyReopenPosted=True',
            'readyReopenAcknowledged=True',
            'readyReopenGbay=True',
            'readyReopenNoPreloader=True',
            'readyReopenClosedToIdle=True',
            'releaseBeforeIntentAbsent=True',
            'lateIntentArmed=True',
            'lateIntentConsumedOnce=True',
            'lateLogicalRetire=True',
            'latePresentationReady=True',
            'lateGbayPainted=True',
            'lateDismissPosted=True',
            'lateCloseNoPreloader=True',
            'intentClaimAcks=2',
            'claimedMenuStayedVisible=True',
            'cancelAfterReserve=True',
            'cancelledBeforeDispatch=True',
            'cancelledDispatchRejected=True',
            'cancelledClaimRejected=True',
            'cancelledNoPresentation=True',
            'cancelledStatusNeutral=True',
            'transientIntentPreserved=True',
            'providerReconnected=True',
            'reconnectObservedIntent=True',
            'reconnectClosed=True',
            'initializerSurfaceOwned=True',
            'startupToGbay=True',
            'transitionNoBlack=True',
            'transitionNoTransparent=True',
            'transitionNoInterstitial=True',
            'singlePopup=True',
            'readyAcks=3',
            'staleAcks=0',
            'providerDisconnected=True',
            'disconnectIntentCancelled=True',
            'aboutPreservedOnDisconnect=True',
            'noStaleIntent=True',
            'storyReadySignaled=True',
            'hostAliveAfterStoryReady=True'
        )) {
            if (-not $bootstrapOutput.Contains($requiredProof)) {
                throw "The bootstrap-host harness did not prove '$requiredProof'.`n$bootstrapOutput"
            }
        }
        if (-not $bootstrapPreloader.WaitForExit(8000)) {
            throw 'The persistent Preloader did not stop after its target process exited.'
        }
        $bootstrapPreloader.WaitForExit()
        $bootstrapPreloader.Refresh()
        if ($bootstrapPreloader.ExitCode -ne 0) {
            throw "The persistent Preloader failed with exit code $($bootstrapPreloader.ExitCode)."
        }
        $bootstrapTrace = Get-HarnessSessionTrace `
            -LogDirectory (Join-Path $bootstrapHostHarnessRoot 'Logs') `
            -ProcessId $bootstrapPreloader.Id
        # The provider-disconnect recovery exercise reconnects the same client,
        # so connection boundaries are intentionally repeated. Keep the
        # process/browser lifecycle records singleton-qualified, and validate
        # the first handshake ordering independently from the exact reconnect
        # counts below.
        Assert-TraceStages -Trace $bootstrapTrace.Text -Stages @(
            'preloader_start',
            'webview_initialize_begin',
            'webview_content_ready',
            'bootstrap_host_ready',
            'bootstrap_host_runtime_ready_signaled',
            'target_process_exit_signal_received',
            'target_process_exit_handling_begin',
            'target_process_exited',
            'webview_shutdown_input_parent_detached',
            'webview_shutdown_dispose_begin',
            'webview_shutdown_dispose_complete',
            'preloader_stop'
        )
        Assert-TraceStageOrder -Trace $bootstrapTrace.Text -Stages @(
            'bootstrap_host_ready',
            'bootstrap_host_client_connected',
            'bootstrap_host_provider_ready',
            'bootstrap_host_runtime_ready_signaled',
            'bootstrap_host_client_disconnected',
            'target_process_exited'
        )
        # Qualify the exact fail-closed bootstrap implementation, not merely a
        # visually successful surface. An older Preloader could render this
        # fixture while still trusting a JavaScript-ready acknowledgement and
        # therefore must not satisfy the release gate.
        Assert-TraceStageOrder -Trace $bootstrapTrace.Text -Stages @(
            'bootstrap_host_surface_pixel_verification_begin',
            'webview_bootstrap_pixels_verified',
            'bootstrap_host_surface_pixel_verification_passed',
            'webview_final_reveal_pixels_verified',
            'webview_reveal_committed'
        )
        if ($bootstrapTrace.Text -notmatch
            'stage=webview_bootstrap_pixels_verified[^\r\n]*surface=initializing[^\r\n]*paint_identity_marker=True') {
            throw "The persistent host did not prove the initializing surface's exact paint identity: $($bootstrapTrace.Path)"
        }
        if ($bootstrapTrace.Text -notmatch
            'stage=webview_final_reveal_pixels_verified[^\r\n]*surface=initializing[^\r\n]*paint_identity_marker=True') {
            throw "The persistent host did not prove the final initializing reveal's exact paint identity: $($bootstrapTrace.Path)"
        }
        if ($bootstrapTrace.Text -match
            'stage=(?:bootstrap_host_surface_pixel_verification_failed|webview_bootstrap_pixels_unverified|webview_final_reveal_pixels_unverified)') {
            throw "The persistent host trace contains a failed or unverified pixel proof: $($bootstrapTrace.Path)"
        }
        # The hardened bootstrap scenario intentionally exercises several
        # open/close/claim/cancel cycles. These records are repeated by design;
        # the ordered assertion above remains reserved for singleton lifecycle
        # boundaries while the minimums below prove each race path ran.
        foreach ($stageMinimum in @(
            @('bootstrap_host_client_connected', 2),
            @('bootstrap_host_provider_ready', 2),
            @('bootstrap_host_client_disconnected', 2),
            @('bootstrap_host_native_verification_toggle', 1),
            @('bootstrap_host_native_about_toggle', 2),
            @('bootstrap_host_native_toggle', 4),
            @('bootstrap_host_native_close', 2),
            @('default_menu_intent_armed', 4),
            @('default_menu_intent_claimed', 2),
            @('default_menu_intent_cancelled', 5)
        )) {
            Assert-TraceStageMinimum `
                -Trace $bootstrapTrace.Text `
                -Stage $stageMinimum[0] `
                -MinimumCount $stageMinimum[1]
        }
        # BootstrapHostClose is also the process-exit/worker-failure retirement
        # boundary. The preloader therefore records its typed event identity,
        # not an assumed physical Escape source that the event cannot prove.
        foreach ($requiredCancellation in @('native-close-boundary', 'provider-disconnected')) {
            if ($bootstrapTrace.Text -notmatch
                "stage=default_menu_intent_cancelled[^`r`n]*reason=$requiredCancellation") {
                throw "The persistent host did not cancel the one-shot menu intent for '$requiredCancellation': $($bootstrapTrace.Path)"
            }
        }
        if ($bootstrapTrace.Text -match 'stage=(?:webview_profile_release_begin|webview_warm_cache_released|maximum_lifetime_reached)') {
            throw "The persistent host released its browser or lifetime gate before handoff: $($bootstrapTrace.Path)"
        }
        $harnessReport.bootstrap.neutral_verification = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'neutralVerification'))
        $harnessReport.bootstrap.verification_active = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'verificationActive'))
        $harnessReport.bootstrap.verification_active_reset = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'verificationActiveReset'))
        $harnessReport.bootstrap.verification_promoted_in_place = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'verificationPromotedInPlace'))
        $harnessReport.bootstrap.main_menu_about = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'mainMenuAbout'))
        $harnessReport.bootstrap.about_single_popup = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'aboutSinglePopup'))
        $harnessReport.bootstrap.about_no_intent = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'aboutNoIntent'))
        $harnessReport.bootstrap.about_closed = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'aboutClosed'))
        $harnessReport.bootstrap.startup_surface = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupSurface'))
        $harnessReport.bootstrap.startup_topmost = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupTopMost'))
        $harnessReport.bootstrap.startup_demoted_on_close = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupDemotedOnClose'))
        $harnessReport.bootstrap.startup_checks = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupChecks'))
        $harnessReport.bootstrap.startup_console_bounded = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupConsoleBounded'))
        $harnessReport.bootstrap.startup_copy_contract = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupCopyContract'))
        $harnessReport.bootstrap.early_escape_close = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'earlyEscapeClose'))
        $harnessReport.bootstrap.closed_intent_cleared = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'closedIntentCleared'))
        $harnessReport.bootstrap.early_intent_preserved = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'earlyIntentPreserved'))
        $harnessReport.bootstrap.provider_menu_ready = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'providerMenuReady'))
        $harnessReport.bootstrap.provider_startup_status = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'providerStartupStatus'))
        $harnessReport.bootstrap.provider_status_requested_menu = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'providerStatusRequestedMenu'))
        $harnessReport.bootstrap.intent_consumed_once = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'intentConsumedOnce'))
        $harnessReport.bootstrap.release_before_intent_absent = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'releaseBeforeIntentAbsent'))
        $harnessReport.bootstrap.late_intent_armed = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'lateIntentArmed'))
        $harnessReport.bootstrap.late_intent_consumed_once = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'lateIntentConsumedOnce'))
        $harnessReport.bootstrap.late_presentation_ready = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'latePresentationReady'))
        $harnessReport.bootstrap.late_retry_checks = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'lateRetryChecks'))
        $harnessReport.bootstrap.intent_claim_acknowledgements = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'intentClaimAcks'))
        $harnessReport.bootstrap.claimed_menu_stayed_visible = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'claimedMenuStayedVisible'))
        $harnessReport.bootstrap.cancel_after_reserve = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelAfterReserve'))
        $harnessReport.bootstrap.cancelled_before_dispatch = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelledBeforeDispatch'))
        $harnessReport.bootstrap.cancelled_dispatch_rejected = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelledDispatchRejected'))
        $harnessReport.bootstrap.cancelled_claim_rejected = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelledClaimRejected'))
        $harnessReport.bootstrap.cancelled_no_presentation = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelledNoPresentation'))
        $harnessReport.bootstrap.cancelled_status_neutral = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'cancelledStatusNeutral'))
        $harnessReport.bootstrap.transient_intent_preserved = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'transientIntentPreserved'))
        $harnessReport.bootstrap.provider_reconnected = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'providerReconnected'))
        $harnessReport.bootstrap.reconnect_observed_intent = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'reconnectObservedIntent'))
        $harnessReport.bootstrap.reconnect_closed = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'reconnectClosed'))
        $harnessReport.bootstrap.initializer_surface_owned = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'initializerSurfaceOwned'))
        $harnessReport.bootstrap.startup_to_gbay = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'startupToGbay'))
        $harnessReport.bootstrap.transition_no_black = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'transitionNoBlack'))
        $harnessReport.bootstrap.transition_no_transparent = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'transitionNoTransparent'))
        $harnessReport.bootstrap.transition_no_interstitial = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'transitionNoInterstitial'))
        $harnessReport.bootstrap.single_popup = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'singlePopup'))
        $harnessReport.bootstrap.transition_frames = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'transitionFrames'))
        $harnessReport.bootstrap.ready_acknowledgements = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'readyAcks'))
        $harnessReport.bootstrap.stale_acknowledgements = [int]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'staleAcks'))
        $harnessReport.bootstrap.provider_disconnected = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'providerDisconnected'))
        $harnessReport.bootstrap.disconnect_intent_cancelled = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'disconnectIntentCancelled'))
        $harnessReport.bootstrap.about_preserved_on_disconnect = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'aboutPreservedOnDisconnect'))
        $harnessReport.bootstrap.no_stale_intent = [bool]::Parse(
            (Get-HarnessOutputValue $bootstrapOutput 'noStaleIntent'))
        $harnessReport.bootstrap.trace =
            ConvertTo-ProjectRelativeArtifactPath $bootstrapTrace.Path
        $harnessReport.bootstrap.about_screenshot =
            ConvertTo-ProjectRelativeArtifactPath (
                Join-Path $bootstrapHostHarnessRoot 'Runtime\main-menu-about.png')
        $harnessReport.bootstrap.startup_screenshot =
            ConvertTo-ProjectRelativeArtifactPath (
                Join-Path $bootstrapHostHarnessRoot 'Runtime\startup-initializing.png')
        $harnessReport.bootstrap.handoff_screenshot =
            ConvertTo-ProjectRelativeArtifactPath (
                Join-Path $bootstrapHostHarnessRoot 'Runtime\startup-to-gbay.png')
        $harnessReport.suites.bootstrap_host = 'passed'
        Write-Host "Reactor bootstrap-host PASS: delayed provider reused the packaged WebView after $PersistentHostWarmDelayMs ms."
    }
    finally {
        if ($bootstrapPreloader -ne $null) {
            if (-not $bootstrapPreloader.HasExited) {
                Stop-Process -Id $bootstrapPreloader.Id -Force -ErrorAction SilentlyContinue
            }
            $bootstrapPreloader.Dispose()
        }
        if (-not $bootstrapHarness.HasExited) {
            Stop-Process -Id $bootstrapHarness.Id -Force -ErrorAction SilentlyContinue
        }
        $bootstrapHarness.Dispose()
    }

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
    $null = $preloaderProcess.Handle
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
        $preloaderProcess.WaitForExit()
        $preloaderProcess.Refresh()
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

$chromiumCredits = Join-Path $artifactsRoot 'Chromium-CREDITS.txt'
Invoke-Checked (Join-Path $rendererRoot 'RageWebUI.Harness.exe') '--export-chromium-credits' $chromiumCredits
& (Join-Path $projectRoot 'tools/Stage-ReactorLegal.ps1') -Destination (Join-Path $rendererRoot 'legal') `
    -NativeBuild $nativeBuild -ChromiumCredits $chromiumCredits
& (Join-Path $projectRoot 'tools/test-legal-package.ps1') -LegalRoot (Join-Path $rendererRoot 'legal')
$harnessReport.suites.distribution_notices = 'passed'

# The harness is copied into staging only so the packaged-layout smoke test can
# exercise the exact runtime files. Remove its developer executables while
# retaining the SharpDX assemblies now required by ReactorV.Preloader.exe's
# production desktop-presentation child mode.
foreach ($harnessFile in @(
    'RageWebUI.Harness.exe',
    'RageWebUI.Harness.exe.config',
    'ReactorV.Bootstrap.RouteProbe.exe'
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
                $_.Name -match '(?i)allin1|gbay' -or
                $_.Extension -in @('.map', '.pdb', '.log', '.tmp', '.rpf', '.ytd', '.ydr', '.yft', '.meta')
            )) -or
            ($_.PSIsContainer -and $_.Name -eq 'node_modules')
        }
)
if ($unexpectedPackagedArtifacts) {
    throw "Development artifacts leaked into Reactor staging:`n$($unexpectedPackagedArtifacts.FullName -join "`n")"
}
$unexpectedTopLevel = @(
    Get-ChildItem -LiteralPath $stagingRoot -Force |
        Where-Object {
            $allowedTopLevel = @(
                'plugins',
                'scripts',
                'ReactorV.Bootstrap.asi',
                'ReactorV.ScriptProbe.asi')
            if ($includeExperimentalRenderHook) {
                $allowedTopLevel += 'ReactorV.RenderHook.asi'
            }
            $_.Name -notin $allowedTopLevel
        }
)
if ($unexpectedTopLevel) {
    throw "Unexpected Reactor staging root(s): $($unexpectedTopLevel.Name -join ', ')"
}
$experimentalRenderHook = @(
    Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Filter 'ReactorV.RenderHook.asi'
)
if ($includeExperimentalRenderHook -and $experimentalRenderHook.Count -ne 1) {
    $targetName = if ($IncludeExperimentalEnhancedRenderHook) { 'Enhanced' } else { 'Legacy' }
    throw "The $targetName live-test package must contain exactly one root ReactorV.RenderHook.asi."
}
if (-not $includeExperimentalRenderHook -and $experimentalRenderHook) {
    throw 'ReactorV.RenderHook.asi is experimental and must remain outside the public/developer player package pending live GTA acceptance.'
}
$enhancedLiveTestMarkers = @(
    Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Filter $enhancedLiveTestMarkerName
)
$legacyLiveTestMarkers = @(
    Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Filter $legacyLiveTestMarkerName
)
if ($IncludeExperimentalEnhancedRenderHook) {
    if ($enhancedLiveTestMarkers.Count -ne 1 -or
        -not ([IO.Path]::GetFullPath($enhancedLiveTestMarkers[0].FullName).Equals(
            [IO.Path]::GetFullPath($enhancedLiveTestMarkerPath),
            [StringComparison]::OrdinalIgnoreCase))) {
        throw "The Enhanced live-test package must contain exactly one marker at plugins\ReactorV\$enhancedLiveTestMarkerName."
    }
} elseif ($enhancedLiveTestMarkers) {
    throw 'The Enhanced live-test marker must remain outside every Legacy, public, or developer player package.'
}
if ($IncludeExperimentalLegacyRenderHook) {
    if ($legacyLiveTestMarkers.Count -ne 1 -or
        -not ([IO.Path]::GetFullPath($legacyLiveTestMarkers[0].FullName).Equals(
            [IO.Path]::GetFullPath($legacyLiveTestMarkerPath),
            [StringComparison]::OrdinalIgnoreCase))) {
        throw "The Legacy live-test package must contain exactly one marker at plugins\ReactorV\$legacyLiveTestMarkerName."
    }
} elseif ($legacyLiveTestMarkers) {
    throw 'The Legacy live-test marker must remain outside every Enhanced, public, or developer player package.'
}
$legacyCpuFrameMarkers = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse -Filter $legacyCpuFrameMarkerName)
if ($IncludeExperimentalLegacyRenderHook) {
    if ($legacyCpuFrameMarkers.Count -ne 1 -or
        $legacyCpuFrameMarkers[0].FullName -ne (Join-Path $rendererRoot $legacyCpuFrameMarkerName) -or
        (Get-FileHash -LiteralPath $legacyCpuFrameMarkers[0].FullName).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $nativeRoot "probe/$legacyCpuFrameMarkerName")).Hash) {
        throw 'Legacy requires the exact CPU-frame marker in plugins/ReactorV; a hook-only ZIP is incomplete.'
    }
} elseif ($legacyCpuFrameMarkers.Count -ne 0) {
    throw 'The Legacy CPU-frame marker must never ship in Enhanced or external-renderer packages.'
}
$sourceMapReferences = @(
    Get-ChildItem -LiteralPath (Join-Path $rendererRoot 'ui') -File -Recurse -Filter '*.js' |
        Select-String -SimpleMatch 'sourceMappingURL='
)
if ($sourceMapReferences) {
    throw "Source-map references leaked into the packaged Reactor UI."
}
$stagingFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
& (Join-Path $projectRoot 'tools/Stage-ReactorLegal.ps1') -Destination (Join-Path $rendererRoot 'legal') -VerifyOnly
& (Join-Path $projectRoot 'tools/Assert-ReactorRuntimeContent.ps1') -UiRoot (Join-Path $rendererRoot 'ui')
$sensitiveLocalTokens = @(Get-LocalLeakTokens)
Assert-NoLocalPathLeaks `
    -Files $stagingFiles `
    -Tokens $sensitiveLocalTokens `
    -Label 'Reactor staging'
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
$requiredScriptFiles = @(
    'RageWebUI.Script.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll',
    'ReactorV.json',
    'ReactorV.contract.json'
)
foreach ($relativePath in $requiredScriptFiles) {
    $candidate = Join-Path $bootstrapRoot $relativePath
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Script package validation failed; missing: $candidate"
    }
}
$requiredRendererFiles = @(
    'RageWebUI.Runtime.dll',
    'RageWebUI.DirectX.dll',
    'RageWebUI.Core.dll',
    'Newtonsoft.Json.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.dll',
    'RageWebUI.Native.dll',
    'ReactorV.Preloader.exe',
    'ReactorV.Preloader.exe.config',
    'ReactorV.Preloader.json',
    'SharpDX.dll',
    'SharpDX.Direct3D11.dll',
    'SharpDX.DXGI.dll',
    'ui\index.html',
    'ui\ragewebui-logo.png'
) + $cefFiles + @(
    'locales\en-US.pak'
)
if ($IncludeExperimentalEnhancedRenderHook) {
    $requiredRendererFiles += $enhancedLiveTestMarkerName
} elseif ($IncludeExperimentalLegacyRenderHook) {
    $requiredRendererFiles += $legacyLiveTestMarkerName
    $requiredRendererFiles += $legacyCpuFrameMarkerName
}
foreach ($relativePath in $requiredRendererFiles) {
    $candidate = Join-Path $rendererRoot $relativePath
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Renderer package validation failed; missing: $candidate"
    }
}
$packagedNativeLibrary = Join-Path $rendererRoot 'RageWebUI.Native.dll'
Assert-NativeExports -Path $packagedNativeLibrary -Names @(
    'RWUI_GetSharedTextureCapabilities',
    'RWUI_StartSharedTextureProducer',
    'RWUI_StopSharedTextureProducer',
    'RWUI_ProbeSharedTexture',
    'RWUI_SubmitSharedTexture',
    'RWUI_SubmitSharedTextureStatus'
)
$packagedNativeBootstrap = Join-Path $stagingRoot 'ReactorV.Bootstrap.asi'
if (-not (Test-Path -LiteralPath $packagedNativeBootstrap -PathType Leaf)) {
    throw "Native bootstrap package validation failed; missing: $packagedNativeBootstrap"
}
Assert-X64PeImage -Path $packagedNativeBootstrap
$harnessReport.suites.native_bootstrap_packaged = 'passed'
$packagedNativeScriptProbe = Join-Path $stagingRoot 'ReactorV.ScriptProbe.asi'
if (-not (Test-Path -LiteralPath $packagedNativeScriptProbe -PathType Leaf)) {
    throw "Native ScriptHook probe package validation failed; missing: $packagedNativeScriptProbe"
}
Assert-X64PeImage -Path $packagedNativeScriptProbe
$harnessReport.suites.native_script_probe_packaged = 'passed'
if ($includeExperimentalRenderHook) {
    $packagedNativeRenderHook = Join-Path $stagingRoot 'ReactorV.RenderHook.asi'
    if (-not (Test-Path -LiteralPath $packagedNativeRenderHook -PathType Leaf)) {
        $targetName = if ($IncludeExperimentalEnhancedRenderHook) { 'Enhanced' } else { 'Legacy' }
        throw "$targetName render-hook package validation failed; missing: $packagedNativeRenderHook"
    }
    Assert-X64PeImage -Path $packagedNativeRenderHook
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
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
    $renderHookZipEntry = @($zip.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq 'ReactorV.RenderHook.asi'
    })
    if ($includeExperimentalRenderHook -and
        $renderHookZipEntry.Count -ne 1) {
        $targetName = if ($IncludeExperimentalEnhancedRenderHook) { 'Enhanced' } else { 'Legacy' }
        throw "The $targetName live-test ZIP is missing its single root ReactorV.RenderHook.asi."
    }
    if (-not $includeExperimentalRenderHook -and
        $renderHookZipEntry) {
        throw 'The Reactor ZIP contains the experimental, unshipped ReactorV.RenderHook.asi.'
    }
    $enhancedLiveTestMarkerZipEntry = @($zip.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq
            "plugins/ReactorV/$enhancedLiveTestMarkerName"
    })
    if ($IncludeExperimentalEnhancedRenderHook -and
        $enhancedLiveTestMarkerZipEntry.Count -ne 1) {
        throw 'The Enhanced live-test ZIP is missing its single package marker.'
    }
    if (-not $IncludeExperimentalEnhancedRenderHook -and
        $enhancedLiveTestMarkerZipEntry) {
        throw 'The Reactor ZIP contains the Enhanced live-test package marker.'
    }
    $legacyLiveTestMarkerZipEntry = @($zip.Entries | Where-Object {
        $_.FullName.Replace('\', '/') -eq
            "plugins/ReactorV/$legacyLiveTestMarkerName"
    })
    if ($IncludeExperimentalLegacyRenderHook -and
        $legacyLiveTestMarkerZipEntry.Count -ne 1) {
        throw 'The Legacy live-test ZIP is missing its single package marker.'
    }
    if (-not $IncludeExperimentalLegacyRenderHook -and
        $legacyLiveTestMarkerZipEntry) {
        throw 'The Reactor ZIP contains the Legacy live-test package marker.'
    }
    $cpuEntries = @($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq "plugins/ReactorV/$legacyCpuFrameMarkerName" })
    if ($cpuEntries.Count -ne $(if ($IncludeExperimentalLegacyRenderHook) { 1 } else { 0 })) {
        throw 'The Reactor ZIP has an incorrect edition-specific CPU-frame marker count.'
    }
    foreach ($entry in @($zip.Entries | Where-Object { $_.Length -gt 0 })) {
        $entryStream = $entry.Open()
        try {
            $leak = [ReactorV.Package.BinaryInspection]::FindLeak(
                $entryStream, $sensitiveLocalTokens)
            if (-not [string]::IsNullOrWhiteSpace($leak)) {
                throw "Reactor ZIP entry contains a developer-local path token '$leak': $($entry.FullName)"
            }
        }
        finally {
            $entryStream.Dispose()
        }
    }
    $requiredZipEntries = @(
        'ReactorV.Bootstrap.asi',
        'ReactorV.ScriptProbe.asi',
        'scripts/ReactorV/RageWebUI.Script.dll',
        'scripts/ReactorV/RageWebUI.Core.dll',
        'scripts/ReactorV/Newtonsoft.Json.dll',
        'scripts/ReactorV/ReactorV.json',
        'scripts/ReactorV/ReactorV.contract.json',
        'plugins/ReactorV/RageWebUI.Runtime.dll',
        'plugins/ReactorV/RageWebUI.DirectX.dll',
        'plugins/ReactorV/RageWebUI.Core.dll',
        'plugins/ReactorV/Newtonsoft.Json.dll',
        'plugins/ReactorV/Microsoft.Web.WebView2.Core.dll',
        'plugins/ReactorV/Microsoft.Web.WebView2.WinForms.dll',
        'plugins/ReactorV/WebView2Loader.dll',
        'plugins/ReactorV/RageWebUI.Native.dll',
        'plugins/ReactorV/ReactorV.Preloader.exe',
        'plugins/ReactorV/ReactorV.Preloader.exe.config',
        'plugins/ReactorV/ReactorV.Preloader.json',
        'plugins/ReactorV/SharpDX.dll',
        'plugins/ReactorV/SharpDX.Direct3D11.dll',
        'plugins/ReactorV/SharpDX.DXGI.dll',
        'plugins/ReactorV/ui/index.html',
        'plugins/ReactorV/ui/ragewebui-logo.png'
    ) + @($cefFiles | ForEach-Object { "plugins/ReactorV/$($_)" }) + @(
        'plugins/ReactorV/locales/en-US.pak'
    )
    if ($IncludeExperimentalEnhancedRenderHook) {
        $requiredZipEntries += 'ReactorV.RenderHook.asi'
        $requiredZipEntries += "plugins/ReactorV/$enhancedLiveTestMarkerName"
    } elseif ($IncludeExperimentalLegacyRenderHook) {
        $requiredZipEntries += 'ReactorV.RenderHook.asi'
        $requiredZipEntries += "plugins/ReactorV/$legacyLiveTestMarkerName"
        $requiredZipEntries += "plugins/ReactorV/$legacyCpuFrameMarkerName"
    }
    foreach ($requiredEntry in $requiredZipEntries) {
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
Set-Content -LiteralPath $archiveHashPath -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
$harnessReport.generated_utc = [DateTime]::UtcNow.ToString('o')
$harnessReport.package.staging_bytes = [long]$stagingBytes
$harnessReport.package.archive_bytes = [long]$archiveBytes
$harnessReport.package.archive = ConvertTo-ProjectRelativeArtifactPath $archive
$harnessReport.package.sha256 = $hash
New-Item -ItemType Directory -Path (Split-Path $harnessReportPath -Parent) -Force | Out-Null
$harnessReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $harnessReportPath -Encoding utf8

Write-Host "Built: $archive"
Write-Host "SHA-256: $hash"
Write-Host "Package budgets PASS: staging=$stagingBytes bytes, archive=$archiveBytes bytes"
Write-Host "Harness report: $harnessReportPath"
if (-not $releaseEligible) {
    Write-Warning "Non-public artifact only ($artifactKind): $archiveName. It did not overwrite ReactorV-0.2.0.zip or its release receipt."
}
