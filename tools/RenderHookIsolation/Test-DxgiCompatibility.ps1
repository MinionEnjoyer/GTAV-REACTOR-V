[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeBuildDirectory,
    [Parameter(Mandatory)][string]$ProxyPath,
    [Parameter(Mandatory)][string]$RunDirectory,
    [string]$NativeLibraryPath
)

$ErrorActionPreference = 'Stop'
$buildDir = (Resolve-Path -LiteralPath $NativeBuildDirectory).Path
$proxyFile = (Resolve-Path -LiteralPath $ProxyPath).Path
if (-not $NativeLibraryPath) { $NativeLibraryPath = Join-Path $buildDir 'RageWebUI.Native.dll' }
$nativeFile = (Resolve-Path -LiteralPath $NativeLibraryPath).Path
$runDir = [IO.Path]::GetFullPath($RunDirectory)
if (Test-Path -LiteralPath $runDir) { throw 'Use a new diagnostic directory; earlier evidence is never overwritten.' }
$sources = @{
    proxy = $proxyFile
    native = $nativeFile
    loader = Join-Path $buildDir 'ReactorV.AppLocalDxgi.Tests.exe'
    importer = Join-Path $buildDir 'ReactorV.DxgiImportFixture.dll'
    integration = Join-Path $buildDir 'ReactorV.EnhancedHook.Integration.Tests.exe'
    producer = Join-Path $buildDir 'ReactorV.Preloader.exe'
}
foreach ($source in $sources.Values) {
    $item = Get-Item -LiteralPath $source
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected an ordinary input file: $source"
    }
}
if (Get-Process -Name GTA5,GTA5_Enhanced -ErrorAction SilentlyContinue) {
    throw 'Close GTA before running the offline GPU fixtures.'
}
New-Item -ItemType Directory -Path $runDir | Out-Null
$manifest = [ordered]@{
    kind = 'offline-dxgi-compatibility'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    inputs = @{}
    tests = @()
    limitation = 'Synthetic windowed render fixtures; no GTA, presets, RenoDX or game acceptance proof.'
}
foreach ($key in $sources.Keys) {
    $manifest.inputs[$key] = @{ path = $sources[$key]; sha256 = (Get-FileHash -LiteralPath $sources[$key] -Algorithm SHA256).Hash }
}
function Save-Manifest {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir 'manifest.json') -Encoding UTF8
}
function Invoke-Fixture([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $directory = Split-Path -Parent $Executable
    $stdout = Join-Path $directory "$Name.stdout.log"
    $stderr = Join-Path $directory "$Name.stderr.log"
    # Inputs are filesystem paths/known fixture switches, not shell programs.
    $quoted = @($Arguments | ForEach-Object {
        if ($_.Contains('"')) { throw 'Unexpected quote in fixture argument' }
        '"' + $_ + '"'
    })
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -WorkingDirectory $directory `
        -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $finished = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        # Terminate only the exact offline process created above. The integration
        # fixture itself has bounded, owned producer cleanup. Never target GTA.
        $process.Kill()
        $process.WaitForExit()
    }
    $process.Refresh()
    $manifest.tests += @{ name = $Name; pid = $process.Id; exitCode = $process.ExitCode; timedOut = -not $finished; stdout = $stdout; stderr = $stderr }
    Save-Manifest
    Get-Content -LiteralPath $stdout
    Get-Content -LiteralPath $stderr
    if (-not $finished -or $process.ExitCode -ne 0) { throw "$Name failed; evidence saved in $directory" }
}
Save-Manifest

$loaderDir = Join-Path $runDir 'loader'
New-Item -ItemType Directory -Path "$loaderDir\plugins\ReactorV" | Out-Null
Copy-Item -LiteralPath $sources.loader -Destination "$loaderDir\Loader Test.exe"
Copy-Item -LiteralPath $sources.importer -Destination "$loaderDir\plugins\ReactorV\import.dll"
Copy-Item -LiteralPath $proxyFile -Destination "$loaderDir\dxgi.dll"
Invoke-Fixture 'loader' "$loaderDir\Loader Test.exe" @('--child', 'real') 20

foreach ($edition in @('enhanced', 'legacy-flip')) {
    $renderDir = Join-Path $runDir $edition
    $pluginDir = Join-Path $renderDir 'plugins\ReactorV'
    New-Item -ItemType Directory -Path $pluginDir | Out-Null
    Copy-Item -LiteralPath $sources.integration -Destination "$renderDir\Render Integration Test.exe"
    Copy-Item -LiteralPath $proxyFile -Destination "$renderDir\dxgi.dll"
    Copy-Item -LiteralPath $nativeFile -Destination "$pluginDir\RageWebUI.Native.dll"
    # Production authenticates the producer against the native module's exact
    # directory. Both belong in plugins/ReactorV, not beside app-local ReShade.
    Copy-Item -LiteralPath $sources.producer -Destination "$pluginDir\ReactorV.Preloader.exe"
    $arguments = @("$pluginDir\RageWebUI.Native.dll", "$pluginDir\ReactorV.Preloader.exe")
    if ($edition -eq 'legacy-flip') { $arguments += '--legacy-flip-external' }
    Invoke-Fixture $edition "$renderDir\Render Integration Test.exe" $arguments 45
}
$manifest.completedUtc = [DateTime]::UtcNow.ToString('o')
Save-Manifest
Write-Output "PASS: real proxy loader, Enhanced external GPU rendering, and Legacy flip lifecycle. Evidence: $runDir"
