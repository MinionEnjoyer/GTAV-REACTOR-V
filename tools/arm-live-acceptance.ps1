[CmdletBinding()]
param(
    [string]$Receipt,
    [ValidateRange(30, 7200)]
    [int]$ProcessTimeoutSeconds = 1200,
    [ValidateRange(5, 300)]
    [int]$StepTimeoutSeconds = 45,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\ReactorV.Harness\RageWebUI.Harness.csproj'
$harness = Join-Path $root 'src\ReactorV.Harness\bin\Release\RageWebUI.Harness.exe'

$running = @(
    Get-Process -Name 'GTA5', 'GTA5_Enhanced' -ErrorAction SilentlyContinue
)
if ($running.Count -gt 0) {
    throw 'Close GTA before arming the live acceptance run. This guarantees the frontend About step is observed.'
}

if (-not $SkipBuild) {
    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Could not build the Reactor live acceptance harness (exit $LASTEXITCODE)."
    }
}
if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    throw "The live acceptance harness was not found: $harness"
}

$arguments = @(
    '--scenario', 'live-acceptance',
    '--live-process-timeout-seconds', "$ProcessTimeoutSeconds",
    '--live-step-timeout-seconds', "$StepTimeoutSeconds"
)
if (-not [string]::IsNullOrWhiteSpace($Receipt)) {
    $arguments += @('--receipt', [IO.Path]::GetFullPath($Receipt))
}

Write-Host 'Armed. Launch GTA normally, remain in the foreground, then select Story Mode when prompted.'
Write-Host 'This tool does not launch, close, or modify GTA.'
& $harness @arguments
exit $LASTEXITCODE
