[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ReleaseVersion = '0.2.7'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-InstallerPackage {
    param([Parameter(Mandatory)] [bool]$Condition,
          [Parameter(Mandatory)] [string]$Message)
    if (-not $Condition) { throw $Message }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'ReactorV-installer-package-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedTestRoot) -notmatch '^ReactorV-installer-package-[0-9a-f]{32}$') {
    throw "Refusing unsafe installer package test directory: $resolvedTestRoot"
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    & (Join-Path $PSScriptRoot 'build-installer.ps1') `
        -ReleaseVersion $ReleaseVersion -OutputDirectory $testRoot
    if (-not $?) { throw 'Installer package build failed.' }

    $archiveName = "ReactorV-$ReleaseVersion-installer.zip"
    $archive = Join-Path $testRoot $archiveName
    $checksum = "$archive.sha256"
    Assert-InstallerPackage (Test-Path -LiteralPath $archive -PathType Leaf) `
        'Installer archive was not created.'
    $expectedArchiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-InstallerPackage (
        [IO.File]::ReadAllText($checksum).Trim() -ceq "$expectedArchiveHash  $archiveName") `
        'Installer checksum sidecar does not identify the produced archive.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $expected = [ordered]@{
            'LICENSE' = 'LICENSE'
            'README.md' = 'tools\INSTALLER-README.md'
            'tools/install-live-test-package.ps1' = 'tools\install-live-test-package.ps1'
            'tools/ReactorV.InstallOwnership.psm1' = 'tools\ReactorV.InstallOwnership.psm1'
        }
        Assert-InstallerPackage ($zip.Entries.Count -eq $expected.Count) `
            'Installer archive inventory is not exact.'
        foreach ($item in $expected.GetEnumerator()) {
            $entry = $zip.GetEntry($item.Key)
            Assert-InstallerPackage ($null -ne $entry) "Installer entry is missing: $($item.Key)"
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            $source = (Get-FileHash -LiteralPath (Join-Path $root $item.Value) -Algorithm SHA256).Hash
            Assert-InstallerPackage ($actual -ceq $source) "Installer entry differs from source: $($item.Key)"
        }
    }
    finally { $zip.Dispose() }

    Write-Output "INSTALLER_PACKAGE_PASS version=$ReleaseVersion sha256=$expectedArchiveHash"
}
finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
