Set-StrictMode -Version Latest

$script:MaximumPreservedFiles = 8192
$script:MaximumPreservedBytes = 1073741824

function Assert-ReactorVDirectoryRoot {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [switch]$AllowMissing
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath)) {
        if ($AllowMissing) { return $fullPath }
        throw "Expected an existing Reactor V directory: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Expected a Reactor V directory: $fullPath"
    }
    if ((Get-Item -LiteralPath $fullPath -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to inspect a reparse-point Reactor V directory: $fullPath"
    }
    return $fullPath.TrimEnd([char[]]'\/')
}

function ConvertTo-ReactorVRelativePath {
    param([Parameter(Mandatory)] [string]$Path)

    $relative = $Path.Replace('\', '/').TrimStart('/')
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or
        $relative.IndexOf(':') -ge 0) {
        throw "Unsafe Reactor V relative path: $Path"
    }
    foreach ($part in $relative.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -eq '.' -or $part -eq '..') {
            throw "Unsafe Reactor V relative path: $Path"
        }
    }
    return $relative
}

function Get-ReactorVRelativeFiles {
    param([Parameter(Mandatory)] [string]$Root)

    $resolvedRoot = Assert-ReactorVDirectoryRoot -Path $Root -AllowMissing
    if (-not (Test-Path -LiteralPath $resolvedRoot)) { return @() }

    foreach ($directory in Get-ChildItem -LiteralPath $resolvedRoot -Directory -Recurse -Force) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to inspect a reparse-point Reactor V directory: $($directory.FullName)"
        }
    }

    $result = [Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to preserve a reparse-point Reactor V file: $($file.FullName)"
        }
        $relative = ConvertTo-ReactorVRelativePath (
            $file.FullName.Substring($resolvedRoot.Length).TrimStart([char[]]'\/'))
        $result.Add([pscustomobject]@{
            RelativePath = $relative
            FullName = $file.FullName
            Length = [long]$file.Length
        })
    }
    return @($result | Sort-Object RelativePath)
}

function Test-ReactorVCoreScriptPath {
    param([Parameter(Mandatory)] [string]$RelativePath)

    $leaf = [IO.Path]::GetFileName($RelativePath)
    $extension = [IO.Path]::GetExtension($leaf)
    if ($extension.Equals('.plugin', [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    if ($extension.Equals('.log', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    foreach ($known in @(
        'Newtonsoft.Json.dll',
        'RageWebUI.json',
        'ReactorV.contract.json'
    )) {
        if ($leaf.Equals($known, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    if ($leaf.StartsWith('RageWebUI.', [StringComparison]::OrdinalIgnoreCase) -and
        @('.dll', '.exe', '.config', '.json').Contains($extension.ToLowerInvariant())) {
        return $true
    }
    return $false
}

function Get-ReactorVExtensionNamespace {
    param([Parameter(Mandatory)] [string]$RelativePath)

    $parts = $RelativePath.Split('/')
    if ($parts.Length -ge 4 -and
        $parts[0].Equals('ui', [StringComparison]::OrdinalIgnoreCase) -and
        $parts[1].Equals('assets', [StringComparison]::OrdinalIgnoreCase)) {
        return "ui/assets/$($parts[2])"
    }
    if ($parts.Length -ge 3 -and
        $parts[0].Equals('extensions', [StringComparison]::OrdinalIgnoreCase)) {
        return "extensions/$($parts[1])"
    }
    return $null
}

function Get-ReactorVFileSha256 {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = $null
    $algorithm = $null
    try {
        $stream = [IO.File]::OpenRead($Path)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        return ([BitConverter]::ToString(
            $algorithm.ComputeHash($stream))).Replace('-', '')
    } finally {
        if ($algorithm) { $algorithm.Dispose() }
        if ($stream) { $stream.Dispose() }
    }
}

function Test-ReactorVPreloaderConfiguration {
    param([Parameter(Mandatory)] [string]$Path)

    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($null -eq $settings -or $settings -is [Array]) { return $false }

        $shadow = $settings.PSObject.Properties['externalGpuBrowserShadow']
        if ($null -eq $shadow -or $shadow.Value -isnot [bool]) { return $false }

        $frameRate = $settings.PSObject.Properties['externalGpuFrameRate']
        if ($null -ne $frameRate) {
            if ($frameRate.Value -isnot [int] -and
                $frameRate.Value -isnot [long]) {
                return $false
            }
            $resolvedFrameRate = [long]$frameRate.Value
            if ($resolvedFrameRate -lt 15 -or $resolvedFrameRate -gt 60) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
}

function New-ReactorVPreservedEntry {
    param(
        [Parameter(Mandatory)] [string]$Scope,
        [Parameter(Mandatory)] [object]$File
    )

    return [pscustomobject]@{
        Scope = $Scope
        RelativePath = [string]$File.RelativePath
        Length = [long]$File.Length
        Sha256 = Get-ReactorVFileSha256 -Path $File.FullName
    }
}

function Get-ReactorVPreservedFileManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$ExistingScriptRoot,
        [Parameter(Mandatory)] [string]$ExistingPluginRoot,
        [Parameter(Mandatory)] [string]$IncomingScriptRoot,
        [Parameter(Mandatory)] [string]$IncomingPluginRoot
    )

    $existingScriptFiles = @(Get-ReactorVRelativeFiles -Root $ExistingScriptRoot)
    $existingPluginFiles = @(Get-ReactorVRelativeFiles -Root $ExistingPluginRoot)
    $incomingScriptFiles = @(Get-ReactorVRelativeFiles -Root $IncomingScriptRoot)
    $incomingPluginFiles = @(Get-ReactorVRelativeFiles -Root $IncomingPluginRoot)

    $incomingScripts = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $incomingScriptFiles) {
        [void]$incomingScripts.Add([string]$file.RelativePath)
    }
    $incomingPlugins = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $incomingPluginFiles) {
        [void]$incomingPlugins.Add([string]$file.RelativePath)
    }

    $extensionStems = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $existingScriptFiles) {
        $relative = [string]$file.RelativePath
        if ([IO.Path]::GetExtension($relative).Equals(
                '.plugin', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$extensionStems.Add(
                $relative.Substring(0, $relative.Length - '.plugin'.Length))
        }
    }

    $entries = [Collections.Generic.List[object]]::new()
    foreach ($file in $existingScriptFiles) {
        $relative = [string]$file.RelativePath
        $isUserConfiguration = $relative.Equals(
            'ReactorV.json', [StringComparison]::OrdinalIgnoreCase)
        $isPlugin = [IO.Path]::GetExtension($relative).Equals(
            '.plugin', [StringComparison]::OrdinalIgnoreCase)
        $isCompanionContract = $false
        if ($relative.EndsWith('.contract.json', [StringComparison]::OrdinalIgnoreCase)) {
            $stem = $relative.Substring(
                0, $relative.Length - '.contract.json'.Length)
            $isCompanionContract = $extensionStems.Contains($stem)
        }

        if (($isPlugin -or $isCompanionContract) -and $incomingScripts.Contains($relative)) {
            throw "The incoming Reactor V package collides with extension-owned script content: $relative"
        }
        if ($isUserConfiguration -or
            -not $incomingScripts.Contains($relative) -and
            -not (Test-ReactorVCoreScriptPath -RelativePath $relative)) {
            $entries.Add((New-ReactorVPreservedEntry -Scope 'Script' -File $file))
        }
    }

    $incomingNamespaces = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $incomingPluginFiles) {
        $namespace = Get-ReactorVExtensionNamespace -RelativePath $file.RelativePath
        if ($namespace) { [void]$incomingNamespaces.Add($namespace) }
    }
    $existingNamespaces = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $existingPluginFiles) {
        $namespace = Get-ReactorVExtensionNamespace -RelativePath $file.RelativePath
        if ($namespace) { [void]$existingNamespaces.Add($namespace) }
    }
    foreach ($namespace in $existingNamespaces) {
        if ($incomingNamespaces.Contains($namespace)) {
            throw "The incoming Reactor V package collides with extension-owned UI content: $namespace"
        }
    }
    foreach ($file in $existingPluginFiles) {
        $relative = [string]$file.RelativePath
        if ($relative.Equals(
                'ReactorV.Preloader.json',
                [StringComparison]::OrdinalIgnoreCase)) {
            if (-not (Test-ReactorVPreloaderConfiguration -Path $file.FullName)) {
                throw 'The existing ReactorV.Preloader.json is invalid. Repair or remove it before updating Reactor V.'
            }
            $entries.Add((New-ReactorVPreservedEntry -Scope 'Plugin' -File $file))
            continue
        }
        if (Get-ReactorVExtensionNamespace -RelativePath $file.RelativePath) {
            $entries.Add((New-ReactorVPreservedEntry -Scope 'Plugin' -File $file))
        }
    }

    if ($entries.Count -gt $script:MaximumPreservedFiles) {
        throw "Reactor V extension preservation exceeds the $script:MaximumPreservedFiles-file limit."
    }
    [long]$totalBytes = 0
    foreach ($entry in $entries) {
        $totalBytes += [long]$entry.Length
        if ($totalBytes -gt $script:MaximumPreservedBytes) {
            throw 'Reactor V extension preservation exceeds the 1 GiB limit.'
        }
    }
    return @($entries | Sort-Object Scope, RelativePath)
}

function Resolve-ReactorVPreservedPath {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$RelativePath
    )

    $resolvedRoot = Assert-ReactorVDirectoryRoot -Path $Root
    $relative = ConvertTo-ReactorVRelativePath -Path $RelativePath
    $candidate = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $relative))
    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Preserved Reactor V path escaped its root: $RelativePath"
    }
    return $candidate
}

function Restore-ReactorVPreservedFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object[]]$Manifest,
        [Parameter(Mandatory)] [string]$BackupScriptRoot,
        [Parameter(Mandatory)] [string]$BackupPluginRoot,
        [Parameter(Mandatory)] [string]$TargetScriptRoot,
        [Parameter(Mandatory)] [string]$TargetPluginRoot
    )

    foreach ($entry in $Manifest) {
        $scope = [string]$entry.Scope
        if ($scope -ne 'Script' -and $scope -ne 'Plugin') {
            throw "Unsupported Reactor V preservation scope: $scope"
        }
        $sourceRoot = if ($scope -eq 'Script') { $BackupScriptRoot } else { $BackupPluginRoot }
        $targetRoot = if ($scope -eq 'Script') { $TargetScriptRoot } else { $TargetPluginRoot }
        $source = Resolve-ReactorVPreservedPath -Root $sourceRoot -RelativePath $entry.RelativePath
        $targetRoot = Assert-ReactorVDirectoryRoot -Path $targetRoot
        $target = [IO.Path]::GetFullPath((Join-Path $targetRoot (
            ConvertTo-ReactorVRelativePath -Path $entry.RelativePath)))

        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-Item -LiteralPath $source -Force).Attributes -band
                [IO.FileAttributes]::ReparsePoint) {
            throw "The preserved Reactor V snapshot file is missing or unsafe: $source"
        }
        if ((Get-ReactorVFileSha256 -Path $source) -ne
            [string]$entry.Sha256) {
            throw "The preserved Reactor V snapshot changed before restore: $($entry.RelativePath)"
        }
        $parent = Split-Path $target -Parent
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target -Force
        if ((Get-ReactorVFileSha256 -Path $target) -ne
            [string]$entry.Sha256) {
            throw "The preserved Reactor V file changed during restore: $($entry.RelativePath)"
        }
    }
}

function Test-ReactorVPreservedFileManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object[]]$Manifest,
        [Parameter(Mandatory)] [string]$TargetScriptRoot,
        [Parameter(Mandatory)] [string]$TargetPluginRoot
    )

    try {
        foreach ($entry in $Manifest) {
            $root = if ([string]$entry.Scope -eq 'Script') {
                $TargetScriptRoot
            } elseif ([string]$entry.Scope -eq 'Plugin') {
                $TargetPluginRoot
            } else {
                return $false
            }
            $path = Resolve-ReactorVPreservedPath `
                -Root $root `
                -RelativePath $entry.RelativePath
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Item -LiteralPath $path -Force).Attributes -band
                    [IO.FileAttributes]::ReparsePoint -or
                (Get-ReactorVFileSha256 -Path $path) -ne
                    [string]$entry.Sha256) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
}

Export-ModuleMember -Function @(
    'Get-ReactorVPreservedFileManifest',
    'Restore-ReactorVPreservedFiles',
    'Test-ReactorVPreservedFileManifest'
)
