[CmdletBinding()]
param([string]$NativeBuildDirectory = (Join-Path $PSScriptRoot '..\native\build-msvc-scriptprobe\Release'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$nativeBuild = (Resolve-Path -LiteralPath $NativeBuildDirectory).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $repository "artifacts\ReactorV-Legacy-CpuBrowserTest-$stamp"
if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
$files = [ordered]@{
    'ReactorV.RenderHook.asi' = (Join-Path $nativeBuild 'ReactorV.RenderHook.asi')
    'plugins/ReactorV/RageWebUI.Native.dll' = (Join-Path $nativeBuild 'RageWebUI.Native.dll')
    'plugins/ReactorV/ReactorV.LegacyCpuFrames.enabled' = (Join-Path $repository 'native\probe\ReactorV.LegacyCpuFrames.enabled')
}
foreach ($source in $files.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing component: $source" }
}
$null = New-Item -ItemType Directory -Path $destination
$receipt = [ordered]@{
    schema_version = 1
    artifact_kind = 'legacy-cpu-browser-test'
    diagnostic_only = $true
    public_release = $false
    requires_existing_marker = 'plugins/ReactorV/ReactorV.LegacyLiveTest.json'
    target_edition = 'Legacy'
    game_version = '1.0.3889.0'
    game_sha256 = '677e4e355cfbdb13273b1d992407e3c261b3a108dc4dd5c8a0c4c1da651802e5'
    automatically_launches_game = $false
    menu_readiness_modified = $false
    input_capture_modified = $false
    transport = 'authenticated-bgra-mapping-v1.2'
    producer = 'existing accelerated CEF; bounded worker readback'
    consumer = 'two reusable unshared game-local D3D11 textures'
    maximum_frames_per_second = 15
    maximum_frame_bytes = 33554432
    texture_pattern_probe_suppressed = $true
    diagnostic_device_probes_suppressed = $true
    logs = @('scripts/ReactorV/ReactorV.CpuFrames.Producer.log', 'scripts/ReactorV/ReactorV.CpuFrames.Consumer.log')
    visibility_requires_user_confirmation = $true
    files = @()
}
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path (Join-Path $destination 'Install Files') $entry.Key
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
    Copy-Item -LiteralPath $entry.Value -Destination $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "Copied component failed verification: $($entry.Key)"
    }
    $receipt.files += [ordered]@{ path = $entry.Key; sha256 = $hash; bytes = (Get-Item -LiteralPath $target).Length }
}
$manifest = Join-Path $destination 'probe-manifest.json'
# Generated metadata contains only portable paths and content hashes.
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8
$zip = "$destination.zip"
Compress-Archive -LiteralPath (Join-Path $destination 'Install Files'), $manifest -DestinationPath $zip
[pscustomobject]@{ Archive = $zip; SHA256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant();
    Bytes = (Get-Item -LiteralPath $zip).Length; Installed = $false }
