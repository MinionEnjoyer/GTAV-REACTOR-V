param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$DiagnosticPatch,
    [Parameter(Mandatory = $true)][string]$TestReport,
    [Parameter(Mandatory = $true)][string]$WindowedProbeReport
)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageZip = $packageRoot + '.zip'
if ((Test-Path -LiteralPath $packageRoot) -or (Test-Path -LiteralPath $packageZip)) {
    throw 'Choose a new output directory/ZIP. Existing packages are never overwritten.'
}
$test = Get-Content -LiteralPath $TestReport -Raw | ConvertFrom-Json
$probe = Get-Content -LiteralPath $WindowedProbeReport -Raw | ConvertFrom-Json
if (-not $test.passed -or $test.failures -ne 0 -or -not $probe.passed -or $probe.gameLaunched) {
    throw 'Offline validation did not pass.'
}
$patchHash = (Get-FileHash -LiteralPath $DiagnosticPatch -Algorithm SHA256).Hash.ToLowerInvariant()
if ($patchHash -ne 'e5fed9c0b50445ceaadcd53099e6d7116c95cd15d3c903bcbf426ef70d7a2496') {
    throw 'Unexpected diagnostic patch identity.'
}
$build = Join-Path $PSScriptRoot 'bin\Release\net48'
$helperHash = (Get-FileHash -LiteralPath (Join-Path $build 'ReactorV.Issue1Isolation.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($test.helperSha256 -ne $helperHash -or $probe.helperSha256 -ne $helperHash) {
    throw 'The validation reports belong to a different helper build. Test this exact EXE again.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
$license = Join-Path $nugetCache 'newtonsoft.json\13.0.4\LICENSE.md'
$inputs = @{}
foreach ($name in @('ReactorV.Issue1Isolation.exe', 'ReactorV.Issue1Isolation.exe.config', 'Newtonsoft.Json.dll')) {
    $inputs[$name] = Join-Path $build $name
}
$inputs['README.md'] = Join-Path $PSScriptRoot 'README.md'
$inputs['LICENSE'] = Join-Path $repo 'LICENSE'
$inputs['Newtonsoft.Json-LICENSE.md'] = $license
$inputs['ReactorV-0.2.2-issue1-diagnostic-v1-patch.zip'] = $DiagnosticPatch
foreach ($source in $inputs.Values) { if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing input: $source" } }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
foreach ($name in $inputs.Keys) { Copy-Item -LiteralPath $inputs[$name] -Destination (Join-Path $packageRoot $name) }
$manifest = [ordered]@{
    name = 'ReactorV-issue1-isolation-v1'
    diagnosticOnly = $true
    gtaVersion = '1.0.1158.13 Enhanced'
    offlineTests = [int]$test.tests
    offlineTestsPassed = $true
    offlineWindowedLoaderProbePassed = $true
    inGameIsolationAcceptance = 'not performed'
    files = @(Get-ChildItem -LiteralPath $packageRoot -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ path = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
[IO.File]::WriteAllText((Join-Path $packageRoot 'package-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $packageZip, [IO.Compression.CompressionLevel]::Optimal, $true)
Get-Item -LiteralPath $packageZip | Select-Object FullName, Length
Get-FileHash -LiteralPath $packageZip -Algorithm SHA256
