[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ReleaseVersion,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$releaseRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$archiveName = "ReactorV-$ReleaseVersion-installer.zip"
$archivePath = Join-Path $outputRoot $archiveName
$checksumPath = "$archivePath.sha256"
if ((Test-Path -LiteralPath $archivePath) -or
    (Test-Path -LiteralPath $checksumPath)) {
    throw "Installer output already exists: $archivePath"
}

$files = [ordered]@{
    'LICENSE' = 'LICENSE'
    'README.md' = 'tools\INSTALLER-README.md'
    'tools/install-live-test-package.ps1' = 'tools\install-live-test-package.ps1'
    'tools/ReactorV.InstallOwnership.psm1' = 'tools\ReactorV.InstallOwnership.psm1'
}
foreach ($source in $files.Values) {
    $path = Join-Path $releaseRoot $source
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer input is missing: $path"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open(
    $archivePath,
    [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $files.GetEnumerator()) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip,
            (Join-Path $releaseRoot $entry.Value),
            $entry.Key,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

$verify = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($verify.Entries.Count -ne $files.Count) {
        throw 'Installer inventory mismatch.'
    }
    foreach ($entry in $files.GetEnumerator()) {
        $packedEntry = $verify.GetEntry($entry.Key)
        if ($null -eq $packedEntry) {
            throw "Installer entry is missing: $($entry.Key)"
        }
        $stream = $packedEntry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $packedHash = [BitConverter]::ToString(
                    $sha.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
        $sourceHash = (Get-FileHash -LiteralPath (
            Join-Path $releaseRoot $entry.Value) -Algorithm SHA256).Hash
        if ($packedHash -ne $sourceHash) {
            throw "Installer file mismatch: $($entry.Key)"
        }
    }
}
finally {
    $verify.Dispose()
}

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $checksumPath,
    "$hash  $archiveName`n",
    [Text.UTF8Encoding]::new($false))
Write-Output "INSTALLER_VERIFIED files=$($files.Count) sha256=$hash path=$archivePath"
