[CmdletBinding()]
param([string]$NativeBuildDirectory = (Join-Path $PSScriptRoot '..\native\build-msvc-scriptprobe\Release'))
$ErrorActionPreference = 'Stop'
if (Get-Process -Name GTA5,GTA5_Enhanced -ErrorAction SilentlyContinue) {
    throw 'Close GTA before running the standalone display test. This test never launches the game.'
}
$nativeRoot = (Resolve-Path -LiteralPath $NativeBuildDirectory).Path
$runner = Join-Path $nativeRoot 'ReactorV.EnhancedHook.Integration.Tests.exe'
$native = Join-Path $nativeRoot 'RageWebUI.Native.dll'
$producer = Join-Path $nativeRoot 'ReactorV.Preloader.exe'
$bridgeRunner = Join-Path $nativeRoot 'ReactorV.LegacyD3D11FrameBridge.Tests.exe'
foreach ($required in @($runner, $native, $producer, $bridgeRunner)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing built test component: $required" }
}
Write-Host 'Standalone render test: local colored frame, then cross-process shared frame in DXGI fullscreen.'
Write-Host 'This temporarily takes over the display. Refused fullscreen is a failure, not a windowed pass.'
& $runner $native $producer --legacy-fullscreen-external
if ($LASTEXITCODE -ne 0) { throw "Fullscreen qualification failed (exit $LASTEXITCODE). Do not mark Legacy fullscreen qualified." }
& $bridgeRunner --fullscreen
if ($LASTEXITCODE -ne 0) { throw "Legacy compatibility bridge fullscreen failed (exit $LASTEXITCODE)." }
Write-Host 'Native fullscreen checks passed. The packaged CEF browser and GTA live acceptance remain separate required checks.'
