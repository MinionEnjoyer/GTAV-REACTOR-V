[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Archive,

    [Parameter(Mandatory = $true)]
    [string]$GameRoot,

    [ValidateSet('Enhanced', 'Legacy')]
    [string]$Edition = 'Enhanced'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ownershipModulePath = Join-Path $PSScriptRoot 'ReactorV.InstallOwnership.psm1'
if (-not (Test-Path -LiteralPath $ownershipModulePath -PathType Leaf)) {
    throw "The Reactor V installer ownership module is missing: $ownershipModulePath"
}
Import-Module -Name $ownershipModulePath -Force -ErrorAction Stop

$editionProfile = if ($Edition -eq 'Enhanced') {
    [ordered]@{
        ArtifactKind = 'enhanced-live-test'
        ArchivePattern = 'ReactorV-*-enhanced-live-test.zip'
        GameExecutable = 'GTA5_Enhanced.exe'
        GameVersion = '1.0.1158.13'
        GameSha256 = '0C52864D4521D9C9D441348AA1156958792DDE8825D0297C851753F167336401'
        Marker = 'plugins/ReactorV/ReactorV.EnhancedLiveTest.json'
        OtherMarker = 'plugins/ReactorV/ReactorV.LegacyLiveTest.json'
    }
} else {
    [ordered]@{
        ArtifactKind = 'legacy-live-test'
        ArchivePattern = 'ReactorV-*-legacy-live-test.zip'
        GameExecutable = 'GTA5.exe'
        GameVersion = '1.0.3889.0'
        GameSha256 = '677E4E355CFBDB13273B1D992407E3C261B3A108DC4DD5C8A0C4C1DA651802E5'
        Marker = 'plugins/ReactorV/ReactorV.LegacyLiveTest.json'
        OtherMarker = 'plugins/ReactorV/ReactorV.EnhancedLiveTest.json'
    }
}
$expectedArtifactKind = [string]$editionProfile.ArtifactKind
$expectedGameExecutable = [string]$editionProfile.GameExecutable
$expectedGameVersion = [string]$editionProfile.GameVersion
$expectedGameSha256 = [string]$editionProfile.GameSha256
$liveTestMarkerRelativePath = [string]$editionProfile.Marker
$otherLiveTestMarkerRelativePath = [string]$editionProfile.OtherMarker

function Assert-FileSystemPath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [ValidateSet('Leaf', 'Container')] [string]$Kind
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if ($resolved.Provider.Name -ne 'FileSystem') {
        throw "Expected a filesystem $Kind path: $Path"
    }
    if (-not (Test-Path -LiteralPath $resolved.ProviderPath -PathType $Kind)) {
        throw "Expected an existing filesystem $Kind path: $Path"
    }
    return [IO.Path]::GetFullPath($resolved.ProviderPath)
}

function Copy-DirectorySnapshot {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )

    if ((Get-Item -LiteralPath $Source -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to copy a reparse-point install directory: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Get-OwnedExtensionAssetManifest {
    param(
        [Parameter(Mandatory)] [string]$Root
    )

    $manifest = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Root)) {
        return $manifest
    }
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Expected the extension asset root to be a directory: $Root"
    }
    if ((Get-Item -LiteralPath $Root -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to preserve a reparse-point extension asset root: $Root"
    }

    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)
    if ($files.Count -gt 4096) {
        throw "The extension asset root exceeds the 4096-file preservation limit: $Root"
    }
    [long]$totalBytes = 0
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to preserve a reparse-point extension asset: $($file.FullName)"
        }
        if (-not $file.Extension.Equals('.png', [StringComparison]::OrdinalIgnoreCase)) {
            throw "The protected ALLIN1 artwork root may contain only PNG files: $($file.FullName)"
        }
        $totalBytes += $file.Length
        if ($totalBytes -gt 536870912) {
            throw "The extension asset root exceeds the 512 MiB preservation limit: $Root"
        }
        $relative = $file.FullName.Substring($Root.Length).TrimStart(
            [char[]]'\/').Replace('\', '/')
        $manifest[$relative] = (
            Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        ).Hash
    }
    return $manifest
}

function Test-OwnedExtensionAssetManifest {
    param(
        [Parameter(Mandatory)] [System.Collections.IDictionary]$Expected,
        [Parameter(Mandatory)] [string]$Root
    )

    $actual = Get-OwnedExtensionAssetManifest -Root $Root
    if ($actual.Count -ne $Expected.Count) {
        return $false
    }
    foreach ($relative in $Expected.Keys) {
        if (-not $actual.Contains($relative) -or
            [string]$actual[$relative] -ne [string]$Expected[$relative]) {
            return $false
        }
    }
    return $true
}

function Restore-InstallSnapshot {
    param(
        [Parameter(Mandatory)] [string]$BackupRoot,
        [Parameter(Mandatory)] [hashtable]$Targets,
        [Parameter(Mandatory)] [hashtable]$WasPresent
    )

    foreach ($name in @('Plugin', 'Script')) {
        $target = [string]$Targets[$name]
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        if ([bool]$WasPresent[$name]) {
            $snapshot = Join-Path $BackupRoot "$name-ReactorV"
            Copy-DirectorySnapshot -Source $snapshot -Destination $target
        }
    }

    foreach ($name in @('Bootstrap', 'ScriptProbe', 'RenderHook')) {
        $target = [string]$Targets[$name]
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
        if ([bool]$WasPresent[$name]) {
            Copy-Item `
                -LiteralPath (Join-Path $BackupRoot ([IO.Path]::GetFileName($target))) `
                -Destination $target `
                -Force
        }
    }
}

$resolvedArchive = Assert-FileSystemPath -Path $Archive -Kind Leaf
$resolvedGameRoot = (
    Assert-FileSystemPath -Path $GameRoot -Kind Container
).TrimEnd([char[]]'\/')
$archiveName = [IO.Path]::GetFileName($resolvedArchive)
if ($archiveName -notlike ([string]$editionProfile.ArchivePattern)) {
    throw "This installer accepts only an explicitly named $Edition live-test ZIP; got: $archiveName"
}

$gameExecutablePath = Join-Path $resolvedGameRoot $expectedGameExecutable
if (-not (Test-Path -LiteralPath $gameExecutablePath -PathType Leaf)) {
    throw "The selected folder is not GTA V $Edition; missing: $gameExecutablePath"
}
$actualGameSha256 =
    (Get-FileHash -LiteralPath $gameExecutablePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualGameSha256 -ne $expectedGameSha256) {
    throw @"
Unsupported GTA V $Edition executable for this controlled live test.
Expected $expectedGameVersion SHA-256: $expectedGameSha256
Detected SHA-256:          $actualGameSha256
No files were changed.
"@
}
$gameVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    $gameExecutablePath).FileVersion
if ($gameVersion -ne $expectedGameVersion) {
    throw "Unsupported GTA V $Edition version for this controlled live test. Expected $expectedGameVersion; detected $gameVersion. No files were changed."
}

$gameProcessName = [IO.Path]::GetFileNameWithoutExtension($expectedGameExecutable)
if (Get-Process -Name $gameProcessName -ErrorAction SilentlyContinue) {
    throw "$expectedGameExecutable is running. Close GTA V $Edition before installing the live-test package."
}

$archiveHash =
    (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash.ToUpperInvariant()
$archiveHashPath = "$resolvedArchive.sha256"
if (Test-Path -LiteralPath $archiveHashPath -PathType Leaf) {
    $declaredHashText = [IO.File]::ReadAllText($archiveHashPath)
    $declaredHashMatch = [regex]::Match(
        $declaredHashText,
        '(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])')
    if (-not $declaredHashMatch.Success) {
        throw "The live-test checksum receipt is malformed: $archiveHashPath"
    }
    if ($declaredHashMatch.Value.ToUpperInvariant() -ne $archiveHash) {
        throw "The live-test ZIP does not match its checksum receipt: $archiveHashPath"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
try {
    $entryNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $fileEntries = [Collections.Generic.List[IO.Compression.ZipArchiveEntry]]::new()
    foreach ($entry in $zip.Entries) {
        $normalized = $entry.FullName.Replace('\', '/')
        $identity = $normalized.TrimEnd('/')
        $segments = @($identity.Split('/'))
        if ([string]::IsNullOrWhiteSpace($identity) -or
            $normalized.StartsWith('/', [StringComparison]::Ordinal) -or
            $identity -match '^[A-Za-z]:' -or
            $segments -contains '..' -or
            $segments -contains '.' -or
            $segments -contains '') {
            throw "Unsafe path in live-test ZIP: $($entry.FullName)"
        }
        if (-not $entryNames.Add($identity)) {
            throw "Duplicate or case-colliding path in live-test ZIP: $identity"
        }
        $allowed =
            $identity -in @(
                'ReactorV.Bootstrap.asi',
                'ReactorV.ScriptProbe.asi',
                'ReactorV.RenderHook.asi',
                'plugins',
                'plugins/ReactorV',
                'scripts',
                'scripts/ReactorV') -or
            $identity.StartsWith(
                'plugins/ReactorV/',
                [StringComparison]::OrdinalIgnoreCase) -or
            $identity.StartsWith(
                'scripts/ReactorV/',
                [StringComparison]::OrdinalIgnoreCase)
        if (-not $allowed) {
            throw "Unexpected install root in live-test ZIP: $identity"
        }
        if (-not [string]::IsNullOrEmpty($entry.Name)) {
            $fileEntries.Add($entry)
        }
    }

    if ($fileEntries.Count -lt 50) {
        throw "Unexpected Reactor V live-test package file count: $($fileEntries.Count)."
    }
    foreach ($requiredEntry in @(
        'ReactorV.Bootstrap.asi',
        'ReactorV.ScriptProbe.asi',
        'ReactorV.RenderHook.asi',
        'scripts/ReactorV/RageWebUI.Script.dll',
        'plugins/ReactorV/ReactorV.Preloader.exe',
        $liveTestMarkerRelativePath
    )) {
        if (-not $entryNames.Contains($requiredEntry)) {
            throw "The $Edition live-test ZIP is missing required entry: $requiredEntry"
        }
    }
    if (@($fileEntries | Where-Object {
            $_.Name.Equals(
                'ReactorV.RenderHook.asi',
                [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 1) {
        throw "The $Edition live-test ZIP must contain exactly one ReactorV.RenderHook.asi at its root."
    }
    foreach ($protectedBridge in @(
        'scripts/ReactorV/ALLIN1.ReactorBridge.plugin',
        'scripts/ReactorV/ALLIN1.ReactorBridge.contract.json'
    )) {
        if ($entryNames.Contains($protectedBridge)) {
            throw "The Reactor package must not own the ALLIN1 bridge: $protectedBridge"
        }
    }
    $protectedExtensionAssetRoot = 'plugins/ReactorV/ui/assets/allin1'
    if (@($entryNames | Where-Object {
            $_.Equals(
                $protectedExtensionAssetRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            $_.StartsWith(
                $protectedExtensionAssetRoot + '/',
                [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0) {
        throw "The Reactor package must not own extension artwork: $protectedExtensionAssetRoot"
    }
    if ($entryNames.Contains($otherLiveTestMarkerRelativePath)) {
        throw "The $Edition live-test ZIP contains the other edition's package marker: $otherLiveTestMarkerRelativePath"
    }

    $markerEntry = @(
        $zip.Entries | Where-Object {
            $_.FullName.Replace('\', '/').Equals(
                $liveTestMarkerRelativePath,
                [StringComparison]::OrdinalIgnoreCase)
        }
    ) | Select-Object -First 1
    if ($null -eq $markerEntry) {
        throw "The $Edition live-test package marker is missing: $liveTestMarkerRelativePath"
    }
    $markerStream = $markerEntry.Open()
    try {
        $reader = [IO.StreamReader]::new($markerStream)
        try {
            $marker = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $markerStream.Dispose()
    }
}
finally {
    $zip.Dispose()
}
if ($null -eq $marker) {
    throw "The $Edition live-test package marker is empty or invalid."
}

$requiredMarkerFields = @(
    'schema_version',
    'artifact_kind',
    'public_release',
    'target_edition',
    'game_executable',
    'game_version',
    'game_sha256',
    'experimental_render_hook'
)
foreach ($field in $requiredMarkerFields) {
    if ($marker.PSObject.Properties.Name -notcontains $field) {
        throw "The $Edition live-test package marker is missing '$field'."
    }
}
if ([int]$marker.schema_version -ne 1 -or
    [string]$marker.artifact_kind -ne $expectedArtifactKind -or
    $marker.public_release -isnot [bool] -or
    [bool]$marker.public_release -ne $false -or
    [string]$marker.target_edition -ne $Edition -or
    [string]$marker.game_executable -ne $expectedGameExecutable -or
    [string]$marker.game_version -ne $expectedGameVersion -or
    ([string]$marker.game_sha256).ToUpperInvariant() -ne $expectedGameSha256 -or
    $marker.experimental_render_hook -isnot [bool] -or
    [bool]$marker.experimental_render_hook -ne $true) {
    throw "The package marker does not authorize this exact $Edition live-test install."
}

$repositoryRoot = Assert-FileSystemPath `
    -Path (Join-Path $PSScriptRoot '..') `
    -Kind Container
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$nonce = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path $artifactsRoot "install-staging-$stamp-$nonce"
$backup = Join-Path $artifactsRoot "install-backup-$stamp-$nonce"

$targets = @{
    Plugin = Join-Path $resolvedGameRoot 'plugins\ReactorV'
    Script = Join-Path $resolvedGameRoot 'scripts\ReactorV'
    Bootstrap = Join-Path $resolvedGameRoot 'ReactorV.Bootstrap.asi'
    ScriptProbe = Join-Path $resolvedGameRoot 'ReactorV.ScriptProbe.asi'
    RenderHook = Join-Path $resolvedGameRoot 'ReactorV.RenderHook.asi'
}
$gameRootPrefix = $resolvedGameRoot + [IO.Path]::DirectorySeparatorChar
foreach ($target in $targets.Values) {
    if (-not [IO.Path]::GetFullPath([string]$target).StartsWith(
            $gameRootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe Reactor V install target: $target"
    }
}
foreach ($name in @('Plugin', 'Script')) {
    $target = [string]$targets[$name]
    if ((Test-Path -LiteralPath $target) -and
        -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw "Expected the existing Reactor V $name target to be a directory: $target"
    }
    if ((Test-Path -LiteralPath $target) -and
        ((Get-Item -LiteralPath $target -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a reparse-point Reactor V $name target: $target"
    }
}
foreach ($name in @('Bootstrap', 'ScriptProbe', 'RenderHook')) {
    $target = [string]$targets[$name]
    if ((Test-Path -LiteralPath $target) -and
        -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Expected the existing Reactor V $name target to be a file: $target"
    }
    if ((Test-Path -LiteralPath $target) -and
        ((Get-Item -LiteralPath $target -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a reparse-point Reactor V $name target: $target"
    }
}

$wasPresent = @{}
foreach ($name in $targets.Keys) {
    $wasPresent[$name] = Test-Path -LiteralPath ([string]$targets[$name])
}
$bridgeTarget = Join-Path ([string]$targets.Script) 'ALLIN1.ReactorBridge.plugin'
$bridgeContractTarget = Join-Path `
    ([string]$targets.Script) `
    'ALLIN1.ReactorBridge.contract.json'
$bridgeWasPresent = Test-Path -LiteralPath $bridgeTarget -PathType Leaf
$bridgeContractWasPresent = Test-Path `
    -LiteralPath $bridgeContractTarget `
    -PathType Leaf
if ($bridgeWasPresent -ne $bridgeContractWasPresent) {
    throw 'The existing ALLIN1 Reactor bridge pair is incomplete. Run ALLIN1 Install / Repair before updating Reactor V.'
}
$bridgeHashBefore = if ($bridgeWasPresent) {
    (Get-FileHash -LiteralPath $bridgeTarget -Algorithm SHA256).Hash
} else { $null }
$bridgeContractHashBefore = if ($bridgeContractWasPresent) {
    (Get-FileHash -LiteralPath $bridgeContractTarget -Algorithm SHA256).Hash
} else { $null }
$extensionAssetRelativePath = 'ui\assets\allin1'
$extensionAssetTarget = Join-Path `
    ([string]$targets.Plugin) `
    $extensionAssetRelativePath
$extensionAssetsWerePresent = Test-Path `
    -LiteralPath $extensionAssetTarget `
    -PathType Container
$extensionAssetManifestBefore = if ($extensionAssetsWerePresent) {
    Get-OwnedExtensionAssetManifest -Root $extensionAssetTarget
} else {
    [ordered]@{}
}

New-Item -ItemType Directory -Path $stage, $backup -Force | Out-Null
$mutationStarted = $false
try {
    Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $stage
    $packageFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse)
    if ($packageFiles.Count -ne $fileEntries.Count) {
        throw "Expanded package file count drifted from ZIP preflight: expected $($fileEntries.Count), got $($packageFiles.Count)."
    }

    # Reactor owns the package runtime, while existing menu extensions and the
    # player's documented ReactorV.json settings remain externally owned.
    # Resolve and hash that boundary before the first install-target mutation.
    $preservedFiles = @(Get-ReactorVPreservedFileManifest `
        -ExistingScriptRoot ([string]$targets.Script) `
        -ExistingPluginRoot ([string]$targets.Plugin) `
        -IncomingScriptRoot (Join-Path $stage 'scripts\ReactorV') `
        -IncomingPluginRoot (Join-Path $stage 'plugins\ReactorV'))
    $userConfigurationPreserved = @($preservedFiles | Where-Object {
        [string]$_.Scope -eq 'Script' -and
        [string]$_.RelativePath -ieq 'ReactorV.json'
    }).Count -eq 1
    $preloaderConfigurationPreserved = @($preservedFiles | Where-Object {
        [string]$_.Scope -eq 'Plugin' -and
        [string]$_.RelativePath -ieq 'ReactorV.Preloader.json'
    }).Count -eq 1
    $preservedPackagePaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($preservedFile in $preservedFiles) {
        $prefix = if ([string]$preservedFile.Scope -eq 'Script') {
            'scripts/ReactorV'
        } else {
            'plugins/ReactorV'
        }
        [void]$preservedPackagePaths.Add(
            "$prefix/$([string]$preservedFile.RelativePath)")
    }

    foreach ($name in @('Plugin', 'Script')) {
        $target = [string]$targets[$name]
        if ([bool]$wasPresent[$name]) {
            Copy-DirectorySnapshot `
                -Source $target `
                -Destination (Join-Path $backup "$name-ReactorV")
        }
    }
    foreach ($name in @('Bootstrap', 'ScriptProbe', 'RenderHook')) {
        $target = [string]$targets[$name]
        if ([bool]$wasPresent[$name]) {
            Copy-Item `
                -LiteralPath $target `
                -Destination (Join-Path $backup ([IO.Path]::GetFileName($target))) `
                -Force
        }
    }

    $mutationStarted = $true
    foreach ($name in @('Plugin', 'Script')) {
        $target = [string]$targets[$name]
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to replace a reparse-point install directory: $target"
            }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
    foreach ($name in @('Bootstrap', 'ScriptProbe', 'RenderHook')) {
        $target = [string]$targets[$name]
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }

    New-Item `
        -ItemType Directory `
        -Path (Split-Path ([string]$targets.Plugin)), `
            (Split-Path ([string]$targets.Script)) `
        -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $stage 'plugins\ReactorV') `
        -Destination ([string]$targets.Plugin) `
        -Recurse `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $stage 'scripts\ReactorV') `
        -Destination ([string]$targets.Script) `
        -Recurse `
        -Force
    foreach ($name in @('Bootstrap', 'ScriptProbe', 'RenderHook')) {
        $target = [string]$targets[$name]
        Copy-Item `
            -LiteralPath (Join-Path $stage ([IO.Path]::GetFileName($target))) `
            -Destination $target `
            -Force
    }

    Restore-ReactorVPreservedFiles `
        -Manifest $preservedFiles `
        -BackupScriptRoot (Join-Path $backup 'Script-ReactorV') `
        -BackupPluginRoot (Join-Path $backup 'Plugin-ReactorV') `
        -TargetScriptRoot ([string]$targets.Script) `
        -TargetPluginRoot ([string]$targets.Plugin)
    $preservedFilesVerified = Test-ReactorVPreservedFileManifest `
        -Manifest $preservedFiles `
        -TargetScriptRoot ([string]$targets.Script) `
        -TargetPluginRoot ([string]$targets.Plugin)
    if (-not $preservedFilesVerified) {
        throw 'The Reactor V update changed or removed extension-owned files or user configuration.'
    }

    $mismatches = [Collections.Generic.List[string]]::new()
    $packageFilesVerified = 0
    foreach ($file in $packageFiles) {
        $relative = $file.FullName.Substring($stage.Length).TrimStart(
            [char[]]'\/')
        if ($preservedPackagePaths.Contains($relative.Replace('\', '/'))) {
            continue
        }
        $installed = Join-Path $resolvedGameRoot $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
            $mismatches.Add("MISSING $relative")
            continue
        }
        $expectedHash = (
            Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        ).Hash
        $actualHash = (
            Get-FileHash -LiteralPath $installed -Algorithm SHA256
        ).Hash
        if ($expectedHash -ne $actualHash) {
            $mismatches.Add("HASH $relative")
            continue
        }
        $packageFilesVerified++
    }
    if ($mismatches.Count -gt 0) {
        throw ($mismatches -join [Environment]::NewLine)
    }

    $bridgePreserved = $true
    if ($bridgeWasPresent) {
        $bridgePreserved =
            (Test-Path -LiteralPath $bridgeTarget -PathType Leaf) -and
            (Test-Path -LiteralPath $bridgeContractTarget -PathType Leaf) -and
            ((Get-FileHash -LiteralPath $bridgeTarget -Algorithm SHA256).Hash -eq
                $bridgeHashBefore) -and
            ((Get-FileHash -LiteralPath $bridgeContractTarget -Algorithm SHA256).Hash -eq
                $bridgeContractHashBefore)
        if (-not $bridgePreserved) {
            throw 'The Reactor V update changed or removed the existing ALLIN1 Reactor bridge pair.'
        }
    }
    $extensionAssetsPreserved =
        -not $extensionAssetsWerePresent -or
        (Test-OwnedExtensionAssetManifest `
            -Expected $extensionAssetManifestBefore `
            -Root $extensionAssetTarget)
    if (-not $extensionAssetsPreserved) {
        throw 'The Reactor V update changed or removed existing ALLIN1 catalog artwork.'
    }

    [pscustomobject]@{
        ArtifactKind = $expectedArtifactKind
        ArchiveHash = $archiveHash
        FilesVerified = $packageFilesVerified
        Backup = $backup
        Install = $resolvedGameRoot
        GameExecutable = $expectedGameExecutable
        GameVersion = $gameVersion
        GameSha256 = $actualGameSha256
        BridgePreserved = $bridgePreserved
        BridgePresent = Test-Path -LiteralPath $bridgeTarget -PathType Leaf
        ExtensionAssetsPreserved = $extensionAssetsPreserved
        ExtensionAssetFiles = $extensionAssetManifestBefore.Count
        PreservedFilesVerified = $preservedFilesVerified
        PreservedExtensionFiles = @($preservedFiles | Where-Object {
            -not (([string]$_.Scope -eq 'Script' -and
                [string]$_.RelativePath -ieq 'ReactorV.json') -or
                ([string]$_.Scope -eq 'Plugin' -and
                [string]$_.RelativePath -ieq 'ReactorV.Preloader.json'))
        }).Count
        UserConfigurationPreserved = $userConfigurationPreserved
        PreloaderConfigurationPreserved = $preloaderConfigurationPreserved
        Edition = $Edition
        RenderHook = Test-Path `
            -LiteralPath ([string]$targets.RenderHook) `
            -PathType Leaf
    }
}
catch {
    $installError = $_
    if ($mutationStarted) {
        try {
            Restore-InstallSnapshot `
                -BackupRoot $backup `
                -Targets $targets `
                -WasPresent $wasPresent
        }
        catch {
            throw "Live-test install failed: $($installError.Exception.Message)`nRollback also failed: $($_.Exception.Message)`nBackup: $backup"
        }
    }
    throw $installError
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
