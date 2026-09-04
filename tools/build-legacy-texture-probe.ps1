[CmdletBinding()]
param(
    [string]$NativeBuildDirectory = (Join-Path $PSScriptRoot '..\native\build-msvc-scriptprobe\Release')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$nativeBuild = (Resolve-Path -LiteralPath $NativeBuildDirectory).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $repository "artifacts\ReactorV-Legacy-TextureProbe-$stamp"
if (Test-Path -LiteralPath $destination) { throw "Output already exists: $destination" }
$files = [ordered]@{
    'ReactorV.RenderHook.asi' = (Join-Path $nativeBuild 'ReactorV.RenderHook.asi')
    'plugins/ReactorV/RageWebUI.Native.dll' = (Join-Path $nativeBuild 'RageWebUI.Native.dll')
    'plugins/ReactorV/ReactorV.TextureProbe.Partner.exe' = (Join-Path $nativeBuild 'ReactorV.TextureProbe.Partner.exe')
    'plugins/ReactorV/ReactorV.LegacyTextureProbe.enabled' = (Join-Path $repository 'native\probe\ReactorV.LegacyTextureProbe.enabled')
}
foreach ($source in $files.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing probe component: $source" }
}
$null = New-Item -ItemType Directory -Path $destination
$receipt = [ordered]@{
    schema_version = 3
    artifact_kind = 'legacy-texture-probe'
    diagnostic_only = $true
    public_release = $false
    requires_existing_marker = 'plugins/ReactorV/ReactorV.LegacyLiveTest.json'
    target_edition = 'Legacy'
    game_version = '1.0.3889.0'
    automatically_launches_game = $false
    menu_readiness_modified = $false
    input_capture_modified = $false
    visual_pattern = 'Centered cyan/magenta control; beneath it a white-bordered locally uploaded RGB/checkerboard chart with an alpha row. 30 seconds of foreground drawing. Ctrl+Shift+F8 toggles/rearms the diagnostic only. A black lower plate without the chart means textured drawing is not yet visibly confirmed.'
    log = 'scripts/ReactorV/ReactorV.LegacyTextureProbe.log'
    visibility_log = 'scripts/ReactorV/ReactorV.LegacyTextureProbe.visibility.log'
    visibility_requires_user_confirmation = $true
    files = @()
}
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path (Join-Path $destination 'Install Files') $entry.Key
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
    Copy-Item -LiteralPath $entry.Value -Destination $target
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "Copied probe component failed verification: $($entry.Key)"
    }
    $receipt.files += [ordered]@{ path = $entry.Key; sha256 = $hash; bytes = (Get-Item -LiteralPath $target).Length }
}
# Generated artifact metadata, never machine-local developer paths.
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'probe-manifest.json') -Encoding utf8
$zip = "$destination.zip"
Compress-Archive -LiteralPath (Join-Path $destination 'Install Files'), (Join-Path $destination 'probe-manifest.json') -DestinationPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output ([pscustomobject]@{ Archive = $zip; SHA256 = $zipHash; Bytes = (Get-Item -LiteralPath $zip).Length; Installed = $false })
