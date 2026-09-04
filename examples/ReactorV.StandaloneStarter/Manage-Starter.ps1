[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Check', 'Install', 'Uninstall')] [string]$Mode,
    [Parameter(Mandatory)] [string]$GameRoot,
    [Parameter(Mandatory)] [string]$PackageRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SafePath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '[:\\]' -or
        @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "Unsafe portable path: $Relative"
    }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]'\/')
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Path escapes the selected root.'
    }
    # Check every existing ancestor, including the root, before following any link.
    $cursor = $path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse-point paths are not supported: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $path
}

function Read-BoundedJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Item -LiteralPath $Path).Length -gt 65536) { throw "Missing or oversized JSON: $Path" }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$manifestPath = Get-SafePath $PackageRoot 'consumer.json'
$manifest = Read-BoundedJson $manifestPath
$identities = @{
    'reactorv.starter-a' = 'scripts/ReactorV.StarterA.dll'
    'reactorv.starter-b' = 'scripts/ReactorV.StarterB.dll'
}
if ($manifest.schema_version -ne 1 -or -not $identities.ContainsKey([string]$manifest.id) -or
    $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or @($manifest.files).Count -ne 1) {
    throw 'Invalid starter manifest. This installer only owns the two named starter assemblies.'
}
$entry = @($manifest.files)[0]
if ($entry.path -cne $identities[$manifest.id] -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'The starter cannot own runtime files, another mod, or an arbitrary path.'
}
$dependency = $manifest.requires
if ($dependency.product -cne 'reactor-v' -or $dependency.extension_api_version -ne 1 -or
    $dependency.minimum_runtime_version -cne '0.2.0' -or $dependency.maximum_runtime_version_exclusive -cne '1.0.0' -or
    (@($manifest.editions | Sort-Object) -join ',') -cne 'enhanced,legacy') {
    throw 'Unsupported starter dependency contract.'
}
$destination = Get-SafePath $GameRoot $entry.path
$receiptPath = Get-SafePath $GameRoot ('scripts/.reactorv/consumers/' + $manifest.id + '.json')
$receipt = $null
if (Test-Path -LiteralPath $receiptPath) {
    $receipt = Read-BoundedJson $receiptPath
    if ($receipt.schema_version -ne 1 -or $receipt.id -cne $manifest.id -or
        @($receipt.files).Count -ne 1 -or $receipt.files[0].path -cne $entry.path -or
        $receipt.files[0].sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid ownership receipt; no files changed.' }
}
$edition = if (Test-Path -LiteralPath (Get-SafePath $GameRoot 'GTA5_Enhanced.exe')) { 'enhanced' }
    elseif (Test-Path -LiteralPath (Get-SafePath $GameRoot 'GTA5.exe')) { 'legacy' }
    else { throw 'Select the GTA root containing GTA5.exe or GTA5_Enhanced.exe.' }

if ($Mode -ne 'Check') {
    if (Get-Process -Name GTA5,GTA5_Enhanced -ErrorAction SilentlyContinue) { throw 'Close GTA before installing or removing a starter.' }
}
if (Test-Path -LiteralPath $destination) {
    if (-not $receipt -or (Get-Hash $destination) -cne $receipt.files[0].sha256) {
        throw 'An unowned or modified starter assembly is present. Preserve it manually; no files changed.'
    }
}

if ($Mode -ne 'Uninstall') {
    $source = Get-SafePath $PackageRoot ('payload/' + $entry.path)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Hash $source) -cne $entry.sha256) {
        throw 'Starter payload hash mismatch or missing DLL.'
    }
    $contract = Read-BoundedJson (Get-SafePath $GameRoot 'scripts/ReactorV/ReactorV.contract.json')
    if ($contract.schema_version -ne 1 -or $contract.product -cne 'reactor-v' -or $contract.extension_api_version -ne 1 -or
        $contract.runtime_version -notmatch '^\d+\.\d+\.\d+$' -or
        [version]$contract.runtime_version -lt [version]'0.2.0' -or [version]$contract.runtime_version -ge [version]'1.0.0') {
        throw 'Install Reactor V >= 0.2.0 and < 1.0.0 with extension API 1 before installing the starter.'
    }
    foreach ($capability in @('story.extensions', 'story.menus', 'story.menu-presentation', 'story.menu-bound-parameters')) {
        if ($capability -cnotin $contract.capabilities) { throw "Reactor is missing required capability: $capability" }
    }
    $core = Get-SafePath $GameRoot 'scripts/ReactorV/RageWebUI.Core.dll'
    $pluginCore = Get-SafePath $GameRoot 'plugins/ReactorV/RageWebUI.Core.dll'
    foreach ($relative in @('scripts/ReactorV/RageWebUI.Script.dll', 'scripts/ReactorV/RageWebUI.Core.dll',
        'plugins/ReactorV/RageWebUI.Core.dll', 'plugins/ReactorV/ReactorV.Preloader.exe', 'ScriptHookV.dll', 'ScriptHookVDotNet3.dll')) {
        if (-not (Test-Path -LiteralPath (Get-SafePath $GameRoot $relative) -PathType Leaf)) {
            throw "Required dependency is missing: $relative. Repair Reactor / the edition's ScriptHook runtimes first."
        }
    }
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($core).Version
    if ($assemblyVersion.ToString(3) -cne $contract.runtime_version -or (Get-Hash $core) -cne (Get-Hash $pluginCore)) {
        throw 'Reactor contract/Core versions or shared Core copies disagree. Repair the runtime first.'
    }
}

if ($Mode -eq 'Check') {
    $payloadPresent = Test-Path -LiteralPath $destination -PathType Leaf
    [pscustomobject]@{ status = 'compatible'; id = $manifest.id; edition = $edition;
        installed = ([bool]$receipt -and $payloadPresent); repair_required = ([bool]$receipt -and -not $payloadPresent);
        runtime_version = $contract.runtime_version; live_runtime_tested = $false }
    return
}

# Only these two exact files are ever changed. Keep byte snapshots until both writes commit.
$oldPayload = if (Test-Path -LiteralPath $destination) { [IO.File]::ReadAllBytes($destination) } else { $null }
$oldReceipt = if (Test-Path -LiteralPath $receiptPath) { [IO.File]::ReadAllBytes($receiptPath) } else { $null }
try {
    if ($Mode -eq 'Uninstall') {
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
        if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
    } else {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receiptPath)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
        if ((Get-Hash $destination) -cne $entry.sha256) { throw 'Installed starter did not match the verified payload.' }
        $owned = [ordered]@{ schema_version = 1; id = $manifest.id; version = $manifest.version;
            edition = $edition; requires = $dependency; files = @($entry) }
        [IO.File]::WriteAllText($receiptPath, ($owned | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    }
} catch {
    if ($null -ne $oldPayload) { [IO.File]::WriteAllBytes($destination, $oldPayload) }
    elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
    if ($null -ne $oldReceipt) { [IO.File]::WriteAllBytes($receiptPath, $oldReceipt) }
    elseif (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
    throw
}
[pscustomobject]@{ status = if ($Mode -eq 'Install') { 'installed' } else { 'removed' };
    id = $manifest.id; edition = $edition; shared_runtime_changed = $false }
