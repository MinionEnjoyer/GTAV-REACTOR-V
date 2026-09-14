[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$ReleaseVersion,
    [Parameter(Mandatory)] [string]$EnhancedArchive,
    [Parameter(Mandatory)] [string]$EnhancedSidecar,
    [Parameter(Mandatory)] [string]$LegacyArchive,
    [Parameter(Mandatory)] [string]$LegacySidecar,
    [string]$PythonPath = 'python',
    [Parameter(Mandatory)] [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$diagnostics = Join-Path $PSScriptRoot 'ReactorVDiagnostics'
foreach ($path in @($EnhancedArchive, $EnhancedSidecar, $LegacyArchive, $LegacySidecar)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing explicit release input: $path" }
}
# The Python generator validates both exact artifact filenames and their pinned
# sidecars before it writes either manifest or the release index.
& $PythonPath (Join-Path $diagnostics 'build-manifests.py') `
    --release-version $ReleaseVersion `
    --enhanced $EnhancedArchive --enhanced-sidecar $EnhancedSidecar `
    --legacy $LegacyArchive --legacy-sidecar $LegacySidecar `
    --output (Join-Path $diagnostics 'manifests')
if ($LASTEXITCODE -ne 0) { throw 'Pinned release-manifest generation failed.' }
& (Join-Path $diagnostics 'build.ps1') -ReleaseVersion $ReleaseVersion -PythonPath $PythonPath -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic release package gate failed.' }
