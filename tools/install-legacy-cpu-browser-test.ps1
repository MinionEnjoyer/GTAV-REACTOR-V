[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Archive,
    [Parameter(Mandatory)] [string]$ExpectedArchiveSha256,
    [Parameter(Mandatory)] [string]$GameRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-Closed {
    if (@(Get-Process GTA5,GTA5_Enhanced,ReactorV.Preloader -ErrorAction SilentlyContinue).Count) {
        throw 'Close GTA and ReactorV.Preloader before installing this diagnostic.'
    }
}
function Assert-NoReparse([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse point not allowed: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
Assert-Closed
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$root = (Resolve-Path -LiteralPath $GameRoot).Path
Assert-NoReparse $root
if ((Hash $archivePath) -ne $ExpectedArchiveSha256.ToLowerInvariant()) { throw 'Archive hash mismatch.' }
$expectedGameHash = '677e4e355cfbdb13273b1d992407e3c261b3a108dc4dd5c8a0c4c1da651802e5'
if ((Hash (Join-Path $root 'GTA5.exe')) -ne $expectedGameHash) { throw 'Not the validated Legacy game build.' }
$liveMarker = Join-Path $root 'plugins\ReactorV\ReactorV.LegacyLiveTest.json'
Assert-NoReparse $liveMarker
$live = Get-Content -LiteralPath $liveMarker -Raw | ConvertFrom-Json
if ($live.target_edition -ne 'Legacy' -or $live.game_sha256 -ne $expectedGameHash) { throw 'Legacy opt-in identity mismatch.' }
$before = [ordered]@{
    'ReactorV.RenderHook.asi' = 'e42ce68a6efe8f69c1269edb74f8867d0941dd73332df1be3818acad1e965a0c'
    'plugins/ReactorV/RageWebUI.Native.dll' = '3144548b5d37b4e5e03304bb8c3cec6fdc36d8bef998ac82fe3f355d6d82f997'
    'plugins/ReactorV/ReactorV.LegacyCpuFrames.enabled' = $null
}
$staging = [IO.Path]::ChangeExtension($archivePath, $null)
Assert-NoReparse $staging
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $manifestEntry = $zip.GetEntry('probe-manifest.json')
    if (-not $manifestEntry -or $manifestEntry.Length -gt 16384) { throw 'Missing or oversized manifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.artifact_kind -ne 'legacy-cpu-browser-test' -or $manifest.target_edition -ne 'Legacy' -or
        $manifest.game_sha256 -ne $expectedGameHash -or $manifest.files.Count -ne 3) { throw 'Wrong diagnostic package.' }
    $names = @($manifest.files | ForEach-Object { [string]$_.path })
    if (@($names | Sort-Object -Unique).Count -ne 3 -or @(Compare-Object @($before.Keys) $names).Count) {
        throw 'Payload is not the exact three-file allowlist.'
    }
    $payloads = @($zip.Entries | Where-Object { $_.Name -ne '' })
    if ($payloads.Count -ne 4) { throw 'Unexpected archive files.' }
    foreach ($file in $manifest.files) {
        $entry = $zip.GetEntry("Install Files/$($file.path)")
        if (-not $entry -or $entry.Length -gt 4194304 -or $entry.Length -ne $file.bytes) { throw "Invalid entry: $($file.path)" }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        $source = Join-Path (Join-Path $staging 'Install Files') $file.path
        Assert-NoReparse $source
        if ($digest -ne $file.sha256 -or (Hash $source) -ne $digest) { throw "Payload hash mismatch: $($file.path)" }
    }
} finally { $zip.Dispose() }
foreach ($relative in $before.Keys) {
    $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $target.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target escapes game root.' }
    Assert-NoReparse $target
    if ($null -eq $before[$relative]) {
        if (Test-Path -LiteralPath $target) { throw "New marker already exists: $target" }
    } elseif ((Hash $target) -ne $before[$relative]) { throw "Installed baseline changed: $relative" }
}
$backup = Join-Path $repository ('artifacts\install-backup-legacy-cpu-browser-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$null = New-Item -ItemType Directory -Path $backup
foreach ($relative in $before.Keys) {
    if ($null -eq $before[$relative]) { continue }
    $saved = Join-Path $backup $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $saved
    if ((Hash $saved) -ne $before[$relative]) { throw 'Backup verification failed.' }
}
$changed = @()
try {
    foreach ($relative in $before.Keys) { # Marker last: no partially installed opt-in.
        Assert-Closed
        $file = @($manifest.files | Where-Object { $_.path -eq $relative })[0]
        $target = Join-Path $root $relative
        $changed += $relative
        Copy-Item -LiteralPath (Join-Path (Join-Path $staging 'Install Files') $relative) -Destination $target -Force
        if ((Hash $target) -ne $file.sha256) { throw "Installed hash mismatch: $relative" }
    }
} catch {
    $failure = $_
    foreach ($relative in $changed) {
        $target = Join-Path $root $relative
        if ($null -ne $before[$relative]) {
            Copy-Item -LiteralPath (Join-Path $backup $relative) -Destination $target -Force
        } elseif (Test-Path -LiteralPath $target) {
            # Only the exact marker created by this transaction can be removed.
            Remove-Item -LiteralPath $target -Force
        }
    }
    throw $failure
}
$receipt = [ordered]@{ status = 'installed_verified'; installed_at = (Get-Date).ToString('o');
    archive = $archivePath; archive_sha256 = $ExpectedArchiveSha256; game_root = $root;
    backup = $backup; enhanced_changed = $false; game_launched = $false; before = $before; files = $manifest.files }
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'install-receipt.json') -Encoding utf8
[pscustomobject]$receipt
