[CmdletBinding()]
param(
    [string]$ReleaseVersion = '0.2.8',
    [string]$PythonPath = 'python',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\diagnostics')
)
$ErrorActionPreference = 'Stop'
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'ReleaseVersion must be major.minor.patch.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; an existing diagnostic package is never overwritten.' }
$project = Join-Path $PSScriptRoot 'ReactorV.Diagnostics.csproj'
$fixture = Join-Path $PSScriptRoot 'fixtures\FixtureProcess\FixtureProcess.csproj'
$tests = Join-Path $PSScriptRoot 'Tests\ReactorV.Diagnostics.Tests.csproj'
& $PythonPath (Join-Path $PSScriptRoot 'test-build-manifests.py')
if ($LASTEXITCODE -ne 0) { throw 'Release-manifest generator regression tests failed.' }
& dotnet build $fixture -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture build failed.' }
& dotnet build $project -c Release --nologo -v quiet -p:Version=$ReleaseVersion
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }
& dotnet test $tests -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic regression tests failed.' }
$index = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifests\release-index.json') -Raw | ConvertFrom-Json
$references = @($index.releases | Where-Object { $_.releaseVersion -eq $ReleaseVersion })
$releaseEditions = (@($references.edition | Sort-Object -Unique) -join ',')
if ($references.Count -ne 2 -or $releaseEditions -ne 'Enhanced,Legacy') {
    throw "Release index does not contain both pinned editions for $ReleaseVersion. Run prepare-release-diagnostics.ps1 after final package sidecars are available."
}
$package = Join-Path $output "ReactorV-Diagnostics-$ReleaseVersion"
New-Item -ItemType Directory -Path $package | Out-Null
function Test-ByteSequence([byte[]]$Bytes, [byte[]]$Needle) {
    if ($Needle.Length -eq 0 -or $Needle.Length -gt $Bytes.Length) { return $false }
    for ($offset = 0; $offset -le $Bytes.Length - $Needle.Length; $offset++) {
        if ($Bytes[$offset] -ne $Needle[0]) { continue }
        $matches = $true
        for ($index = 1; $index -lt $Needle.Length; $index++) { if ($Bytes[$offset + $index] -ne $Needle[$index]) { $matches = $false; break } }
        if ($matches) { return $true }
    }
    return $false
}
$binaryRoot = Join-Path $PSScriptRoot 'bin\Release\net48'
foreach ($name in @('ReactorV.Diagnostics.exe','ReactorV.Diagnostics.exe.config','Newtonsoft.Json.dll')) {
    $source = Join-Path $binaryRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $package $name)
}
$checkerExe = Join-Path $package 'ReactorV.Diagnostics.exe'
$developerPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$checkerBytes = [IO.File]::ReadAllBytes($checkerExe)
foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
    if (Test-ByteSequence $checkerBytes ($encoding.GetBytes($developerPath))) { throw 'Packaged diagnostics EXE contains the local developer profile path.' }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifests') -Destination (Join-Path $package 'manifests') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $package 'READ-ME-FIRST.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\LICENSE') -Destination (Join-Path $package 'LICENSE')
$thirdParty = Join-Path $package 'THIRD-PARTY-NOTICES'
New-Item -ItemType Directory -Path $thirdParty | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES\Newtonsoft.Json-13.0.4-MIT.txt') -Destination (Join-Path $thirdParty 'Newtonsoft.Json-13.0.4-MIT.txt')
$inputs = Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($package.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ schemaVersion = 1; toolVersion = $ReleaseVersion; bundledReferences = @($index.releases | ForEach-Object { [ordered]@{ releaseVersion = $_.releaseVersion; edition = $_.edition; artifact = $_.artifact; packageSha256 = $_.packageSha256 } }); files = @($inputs) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output "ReactorV-Diagnostics-$ReleaseVersion.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  ReactorV-Diagnostics-$ReleaseVersion.zip" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
Write-Output "Built portable diagnostics: $zip"
Write-Output "SHA256: $hash"
