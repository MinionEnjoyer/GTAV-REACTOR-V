[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$starter = Join-Path $repo 'examples/ReactorV.StandaloneStarter'
$output = Join-Path $repo ('artifacts/ReactorV-StarterKit-0.1.0-preview-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
[IO.Directory]::CreateDirectory($output) | Out-Null
& (Join-Path $PSScriptRoot 'Stage-ReactorLegal.ps1') -Destination (Join-Path $output 'legal') -StarterKit
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $output 'LICENSE')
foreach ($name in @('StarterA', 'StarterB')) {
    & dotnet build (Join-Path $starter "$name/$name.csproj") -c Release --verbosity quiet
    if ($LASTEXITCODE) { throw "Could not build $name" }
    $id = if ($name -eq 'StarterA') { 'reactorv.starter-a' } else { 'reactorv.starter-b' }
    $relative = "scripts/ReactorV.$name.dll"
    $package = Join-Path $output "packages/$name"
    $target = Join-Path $package "payload/$relative"
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $starter "$name/bin/Release/net48/ReactorV.$name.dll") -Destination $target
    $manifest = [ordered]@{
        schema_version = 1; id = $id; version = '0.1.0'; editions = @('legacy', 'enhanced')
        requires = [ordered]@{ product = 'reactor-v'; minimum_runtime_version = '0.2.0';
            maximum_runtime_version_exclusive = '1.0.0'; extension_api_version = 1 }
        files = @([ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    [IO.File]::WriteAllText((Join-Path $package 'consumer.json'), ($manifest | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    [IO.Directory]::CreateDirectory((Join-Path $output "source/$name")) | Out-Null
    Copy-Item -LiteralPath (Join-Path $starter "$name/$name.csproj") -Destination (Join-Path $output "source/$name/$name.csproj")
}
[IO.Directory]::CreateDirectory((Join-Path $output 'source/Shared')) | Out-Null
foreach ($name in @('MenuPrefabs.cs', 'StarterExtension.cs', 'StarterScript.cs')) {
    Copy-Item -LiteralPath (Join-Path $starter "Shared/$name") -Destination (Join-Path $output "source/Shared/$name")
}
Copy-Item -LiteralPath (Join-Path $starter 'Starter.props') -Destination (Join-Path $output 'source/Starter.props')
Copy-Item -LiteralPath (Join-Path $starter 'Manage-Starter.ps1') -Destination (Join-Path $output 'Manage-Starter.ps1')
[IO.Directory]::CreateDirectory((Join-Path $output 'reference')) | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'src/ReactorV.Core/bin/Release/netstandard2.0/RageWebUI.Core.dll') -Destination (Join-Path $output 'reference/RageWebUI.Core.dll')
# A relative compile-only reference works after extraction on any machine. It is never copied into a mod payload.
[IO.File]::WriteAllText((Join-Path $output 'source/Directory.Build.props'),
    '<Project><PropertyGroup><ReactorApiDirectory>$(MSBuildThisFileDirectory)..\reference</ReactorApiDirectory></PropertyGroup></Project>')
$index = [ordered]@{ schema_version = 1; kind = 'reactor-starter-kit'; version = '0.1.0-preview';
    includes_runtime = $false; live_game_tested = $false; keybindings = @{ StarterA = 'F6'; StarterB = 'F7' };
    prefabs = @('settings', 'scroll-list', 'card-grid', 'status', 'searchable-catalogue', 'tabbed-settings', 'service-checklist', 'side-editor', 'confirmed-action');
    automatic_install = $false; sample_state = 'memory-only';
    build = 'dotnet build source/StarterA/StarterA.csproj -c Release';
    check = 'powershell -NoProfile -ExecutionPolicy Bypass -File Manage-Starter.ps1 -Mode Check -GameRoot "GTA directory" -PackageRoot packages/StarterA';
    install = 'powershell -NoProfile -ExecutionPolicy Bypass -File Manage-Starter.ps1 -Mode Install -GameRoot "GTA directory" -PackageRoot packages/StarterA';
    uninstall = 'powershell -NoProfile -ExecutionPolicy Bypass -File Manage-Starter.ps1 -Mode Uninstall -GameRoot "GTA directory" -PackageRoot packages/StarterA' }
[IO.File]::WriteAllText((Join-Path $output 'kit.json'), ($index | ConvertTo-Json -Depth 5))
# Rebuild outside the checkout, proving there are no hidden repository references
# or an accidental dependency on the enclosing repository's Git metadata.
$reproRoot = Join-Path ([IO.Path]::GetTempPath()) ('ReactorV-Starter-Repro-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($reproRoot) | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'source') -Destination (Join-Path $reproRoot 'source') -Recurse
Copy-Item -LiteralPath (Join-Path $output 'reference') -Destination (Join-Path $reproRoot 'reference') -Recurse
foreach ($name in @('StarterA', 'StarterB')) {
    & dotnet build (Join-Path $reproRoot "source/$name/$name.csproj") -c Release --verbosity quiet
    if ($LASTEXITCODE) { throw "Exported source cannot build: $name" }
    $built = Join-Path $reproRoot "source/$name/bin/Release/net48/ReactorV.$name.dll"
    $packaged = Join-Path $output "packages/$name/payload/scripts/ReactorV.$name.dll"
    if ((Get-FileHash $built).Hash -ne (Get-FileHash $packaged).Hash) { throw "Exported $name does not reproduce its packaged binary." }
}
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-standalone-starter-kit.ps1') -KitRoot $output
if ($LASTEXITCODE) { throw 'Starter installer / shared-runtime tests failed. No distributable ZIP was produced.' }
# Exclude build outputs, logs, dependency caches, and development documentation from the ZIP.
& (Join-Path $PSScriptRoot 'Stage-ReactorLegal.ps1') -Destination (Join-Path $output 'legal') -VerifyOnly -StarterKit
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$archive = "$output.zip"
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        $relative = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
        if ($relative -match '/(bin|obj)/') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative) | Out-Null
    }
} finally { $zip.Dispose() }
[pscustomobject]@{ archive = $archive; staging = $output; sha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() }
