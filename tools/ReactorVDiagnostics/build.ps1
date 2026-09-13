[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\diagnostics'))
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; an existing diagnostic package is never overwritten.' }
$project = Join-Path $PSScriptRoot 'ReactorV.Diagnostics.csproj'
$fixture = Join-Path $PSScriptRoot 'fixtures\FixtureProcess\FixtureProcess.csproj'
$tests = Join-Path $PSScriptRoot 'Tests\ReactorV.Diagnostics.Tests.csproj'
& dotnet build $fixture -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture build failed.' }
& dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }
& dotnet test $tests -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic regression tests failed.' }
$package = Join-Path $output 'ReactorV-Diagnostics-0.1.0'
New-Item -ItemType Directory -Path $package | Out-Null
$binaryRoot = Join-Path $PSScriptRoot 'bin\Release\net48'
foreach ($name in @('ReactorV.Diagnostics.exe','ReactorV.Diagnostics.exe.config','Newtonsoft.Json.dll')) {
    $source = Join-Path $binaryRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $package $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifests') -Destination (Join-Path $package 'manifests') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $package 'READ-ME-FIRST.md')
$inputs = Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($package.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ schemaVersion = 1; toolVersion = '0.1.0'; referenceRelease = '0.2.4'; files = @($inputs) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output 'ReactorV-Diagnostics-0.1.0.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  ReactorV-Diagnostics-0.1.0.zip" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
Write-Output "Built portable diagnostics: $zip"
Write-Output "SHA256: $hash"
