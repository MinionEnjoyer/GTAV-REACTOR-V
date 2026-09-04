[CmdletBinding()]
param(
    [ValidateSet('Build','Validate','Install')] [string]$Mode = 'Validate',
    [Parameter(Mandatory)] [string]$GameRoot,
    [string]$Archive,
    [string]$ExpectedArchiveSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\')
$gameHash = '677e4e355cfbdb13273b1d992407e3c261b3a108dc4dd5c8a0c4c1da651802e5'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Closed {
    if (@(Get-Process GTA5,GTA5_Enhanced,ReactorV.Preloader -ErrorAction SilentlyContinue).Count) {
        throw 'Close GTA and ReactorV.Preloader before staging/installing the diagnostic.'
    }
}
function Assert-NoReparse([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse path rejected: $cursor"
        }
        $cursor = Split-Path -Parent $cursor
    }
}
function Target([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target escapes game root.' }
    Assert-NoReparse $path
    return $path
}
Assert-Closed
Assert-NoReparse $root
if ((Hash (Target 'GTA5.exe')) -ne $gameHash) { throw 'Not the validated Legacy game build.' }
$live = Get-Content -LiteralPath (Target 'plugins/ReactorV/ReactorV.LegacyLiveTest.json') -Raw | ConvertFrom-Json
if ($live.target_edition -ne 'Legacy' -or $live.game_sha256 -ne $gameHash) { throw 'Legacy opt-in identity mismatch.' }
if ((Hash (Target 'plugins/ReactorV/ReactorV.LegacyCpuFrames.enabled')) -ne
    '4c0ab074a190ccacc6ceb45ac670e4a51cc247acff6658a142fb7b3f19ecccd9') { throw 'CPU browser baseline marker mismatch.' }
$basePaths = @('ReactorV.RenderHook.asi', 'ReactorV.Bootstrap.asi', 'plugins/ReactorV/RageWebUI.Native.dll',
    'plugins/ReactorV/RageWebUI.DirectX.dll',
    'plugins/ReactorV/ReactorV.Preloader.exe', 'plugins/ReactorV/RageWebUI.Core.dll',
    'scripts/ReactorV/RageWebUI.Core.dll', 'plugins/ReactorV/ui/ragewebui.js', 'plugins/ReactorV/ui/index.html')
function Allowed([string]$Path) {
    return $Path -cin $basePaths -or $Path -cmatch '^plugins/ReactorV/ui/assets/[A-Za-z0-9_-]+\.(js|css)$'
}
if ($Mode -eq 'Build') {
    if ((Hash (Target 'ReactorV.RenderHook.asi')) -ne
        '8d404b6cca88cd2f4f6d852e23cf5bc55357bae4586db6b50af7b67d0a3c1c3d') { throw 'Unexpected RenderHook baseline.' }
    if ((Hash (Target 'plugins/ReactorV/RageWebUI.Native.dll')) -ne
        '476247a7b87253018ffddfde90181f59e78c30de28f082e7e2fdfb93307a2c25') { throw 'Unexpected native baseline.' }
    if ((Hash (Target 'ReactorV.Bootstrap.asi')) -ne
        '007191398771c67f91c70679a97441b28e78a30e62ed94b53573c0721efe4656') { throw 'Unexpected bootstrap baseline.' }
    $native = Join-Path $repository 'native/build-msvc-scriptprobe/Release'
    $managed = Join-Path $repository 'src/ReactorV.Preloader/bin/Release'
    # These unchanged dependencies must already match the selected build.
    $dependencies = @()
    foreach ($name in @('RageWebUI.Runtime.dll','ReactorV.Preloader.exe.config',
        'Microsoft.Web.WebView2.Core.dll','CefSharp.dll')) {
        $hash = Hash (Join-Path $managed $name)
        if ((Hash (Target "plugins/ReactorV/$name")) -ne $hash) { throw "Dependency mismatch: $name" }
        $dependencies += @{path="plugins/ReactorV/$name"; sha256=$hash}
    }
    $sources = [ordered]@{
        'ReactorV.RenderHook.asi' = (Join-Path $native 'ReactorV.RenderHook.asi')
        'ReactorV.Bootstrap.asi' = (Join-Path $native 'ReactorV.Bootstrap.asi')
        'plugins/ReactorV/RageWebUI.Native.dll' = (Join-Path $native 'RageWebUI.Native.dll')
        'plugins/ReactorV/RageWebUI.DirectX.dll' = (Join-Path $managed 'RageWebUI.DirectX.dll')
        'plugins/ReactorV/ReactorV.Preloader.exe' = (Join-Path $managed 'ReactorV.Preloader.exe')
        'plugins/ReactorV/RageWebUI.Core.dll' = (Join-Path $managed 'RageWebUI.Core.dll')
        'scripts/ReactorV/RageWebUI.Core.dll' = (Join-Path $managed 'RageWebUI.Core.dll')
    }
    foreach ($asset in Get-ChildItem -LiteralPath (Join-Path $repository 'web/dist/assets') -File) {
        $relative = "plugins/ReactorV/ui/assets/$($asset.Name)"
        if (-not (Allowed $relative)) { throw "Unexpected build asset: $relative" }
        $sources[$relative] = $asset.FullName
    }
    $sources['plugins/ReactorV/ui/ragewebui.js'] = Join-Path $repository 'web/dist/ragewebui.js'
    # Entrypoint last. Preserve old hashed assets for rollback; no cleanup here.
    $sources['plugins/ReactorV/ui/index.html'] = Join-Path $repository 'web/dist/index.html'
    $staging = Join-Path $repository ('artifacts/ReactorV-Legacy-StatusReopenTest-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if (Test-Path -LiteralPath $staging) { throw 'Staging exists.' }
    $null = New-Item -ItemType Directory -Path $staging
    $manifest = [ordered]@{schema_version=2; revision=3; artifact_kind='legacy-cpu-recovery-test'; target_edition='Legacy';
        game_sha256=$gameHash; diagnostic_only=$true; public_release=$false; automatically_launches_game=$false;
        menu_readiness_modified=$true; native_about_pointer_routing=$true; new_input_hooks=$false;
        transport='authenticated-bgra-mapping-v1.2'; maximum_frames_per_second=15;
        transient_recoveries_per_10_seconds=3; retry_cooldown_ms=100; readback_timeout_ms=100;
        passive_status='native-560x68-top-right-no-input'; presentation_repaint_retry_ms=250;
        dependencies=$dependencies; files=@()}
    foreach ($item in $sources.GetEnumerator()) {
        $source = (Resolve-Path -LiteralPath $item.Value).Path
        $dest = Join-Path (Join-Path $staging 'Install Files') $item.Key
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force
        Copy-Item -LiteralPath $source -Destination $dest
        $hash = Hash $source
        if ((Hash $dest) -ne $hash) { throw 'Staging verification failed.' }
        $old = Target $item.Key
        $manifest.files += @{path=$item.Key; sha256=$hash; bytes=(Get-Item -LiteralPath $dest).Length;
            before_sha256=$(if (Test-Path -LiteralPath $old) { Hash $old } else { $null })}
    }
    $manifestPath = Join-Path $staging 'probe-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $Archive = "$staging.zip"
    Compress-Archive -LiteralPath (Join-Path $staging 'Install Files'),$manifestPath -DestinationPath $Archive
    [pscustomobject]@{Archive=$Archive; SHA256=(Hash $Archive); Files=$manifest.files.Count; Installed=$false}
    return
}
if (-not $Archive -or $ExpectedArchiveSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Archive and exact SHA256 required.' }
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
if ((Hash $archivePath) -ne $ExpectedArchiveSha256.ToLowerInvariant()) { throw 'Archive hash mismatch.' }
$staging = [IO.Path]::ChangeExtension($archivePath, $null)
Assert-NoReparse $staging
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entry = $zip.GetEntry('probe-manifest.json')
    if (-not $entry -or $entry.Length -gt 32768) { throw 'Missing/oversized manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.schema_version -ne 2 -or $manifest.artifact_kind -ne 'legacy-cpu-recovery-test' -or
        $manifest.target_edition -ne 'Legacy' -or $manifest.game_sha256 -ne $gameHash) { throw 'Wrong diagnostic.' }
    $names = @($manifest.files | ForEach-Object { [string]$_.path })
    if ($names.Count -lt 8 -or $names.Count -gt 32 -or @($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Invalid payload count/duplicates.' }
    foreach ($required in $basePaths) { if ($required -cnotin $names) { throw "Missing required file: $required" } }
    if (@($zip.Entries | Where-Object { $_.Name -ne '' }).Count -ne $names.Count + 1) { throw 'Unexpected archive entries.' }
    foreach ($file in $manifest.files) {
        if (-not (Allowed $file.path)) { throw "Disallowed target: $($file.path)" }
        $entry = $zip.GetEntry("Install Files/$($file.path)")
        if (-not $entry -or $entry.Length -gt 4194304 -or $entry.Length -ne $file.bytes) { throw 'Invalid entry size.' }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        $source = Join-Path (Join-Path $staging 'Install Files') $file.path
        Assert-NoReparse $source
        if ($digest -ne $file.sha256 -or (Hash $source) -ne $digest) { throw 'Payload hash mismatch.' }
    }
} finally { $zip.Dispose() }
function Assert-Baseline {
    foreach ($file in $manifest.files) {
        $target = Target $file.path
        if ($null -eq $file.before_sha256) {
            if (Test-Path -LiteralPath $target) { throw "Unexpected existing file: $($file.path)" }
        } elseif ((Hash $target) -ne $file.before_sha256) { throw "Changed baseline: $($file.path)" }
    }
    foreach ($dependency in $manifest.dependencies) {
        if ((Hash (Target $dependency.path)) -ne $dependency.sha256) { throw "Changed dependency: $($dependency.path)" }
    }
}
Assert-Baseline
if ($Mode -eq 'Validate') { [pscustomobject]@{Status='validated'; Files=$manifest.files.Count; Installed=$false}; return }
$backup = Join-Path $repository ('artifacts/install-backup-legacy-cpu-recovery-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$null = New-Item -ItemType Directory -Path $backup
foreach ($file in $manifest.files) {
    if ($null -eq $file.before_sha256) { continue }
    $saved = Join-Path $backup $file.path
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force
    Copy-Item -LiteralPath (Target $file.path) -Destination $saved
    if ((Hash $saved) -ne $file.before_sha256) { throw 'Backup verification failed.' }
}
Assert-Closed
Assert-Baseline
$changed = @()
try {
    foreach ($file in $manifest.files) {
        Assert-Closed
        $target = Target $file.path
        $changed += $file
        Copy-Item -LiteralPath (Join-Path (Join-Path $staging 'Install Files') $file.path) -Destination $target -Force
        if ((Hash $target) -ne $file.sha256) { throw "Installed hash mismatch: $($file.path)" }
    }
} catch {
    $failure = $_
    foreach ($file in $changed) {
        $target = Target $file.path
        if ($null -ne $file.before_sha256) { Copy-Item -LiteralPath (Join-Path $backup $file.path) -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
    throw $failure
}
$receipt = @{status='installed_verified'; installed_at=(Get-Date).ToString('o'); game_root=$root; backup=$backup;
    archive=$archivePath; archive_sha256=$ExpectedArchiveSha256; files=$manifest.files; enhanced_changed=$false; game_launched=$false}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'install-receipt.json') -Encoding utf8
[pscustomobject]@{Status='installed_verified'; Backup=$backup; Files=$manifest.files.Count; EnhancedChanged=$false; GameLaunched=$false}
