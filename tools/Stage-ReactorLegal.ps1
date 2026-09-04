[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Destination,
    [string]$NativeBuild, [string]$ChromiumCredits, [switch]$StarterKit, [switch]$VerifyOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($Destination)
$indexPath = Join-Path $target 'components.json'
$profile = if ($StarterKit) { 'starter' } else { 'runtime' }
if ($VerifyOnly) {
    $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    if ($index.schema_version -ne 1 -or $index.profile -ne $profile -or $index.project_license -ne 'MIT') {
        throw 'Unsupported legal manifest.'
    }
    $seen = @{}
    foreach ($entry in $index.files) {
        if ($entry.path -notmatch '^[A-Za-z0-9._-]+$' -or $seen.ContainsKey($entry.path) -or
            $entry.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid legal manifest entry.' }
        $seen[$entry.path] = $true
        $file = Get-Item -LiteralPath (Join-Path $target $entry.path)
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() -ne $entry.sha256) {
            throw "Missing or changed legal notice: $($entry.path)"
        }
    }
    if (-not $seen.ContainsKey('LICENSE') -or -not $seen.ContainsKey('THIRD_PARTY_NOTICES.md') -or
        @((Get-ChildItem -LiteralPath $target -Force)).Count -ne ($seen.Count + 1)) { throw 'Incomplete legal payload.' }
    if (-not $StarterKit) {
        foreach ($required in @('SharpDX-LICENSE.txt', 'MinHook-LICENSE.txt', 'Chromium-CREDITS.txt',
            'react-LICENSE.txt', 'react-dom-LICENSE.txt', 'scheduler-LICENSE.txt')) {
            if (-not $seen.ContainsKey($required)) { throw "Missing required notice: $required" }
        }
        foreach ($pattern in @('^CefSharp.Common-.*-LICENSE$', '^CefSharp.OffScreen-.*-LICENSE$',
            '^chromiumembeddedframework.runtime.win-x64-.*-LICENSE.txt$', '^Newtonsoft.Json-.*-LICENSE.md$',
            '^Microsoft.Web.WebView2-.*-LICENSE.txt$', '^Microsoft.Web.WebView2-.*-NOTICE.txt$')) {
            if (@($seen.Keys | Where-Object { $_ -match $pattern }).Count -ne 1) { throw "Missing/ambiguous notice: $pattern" }
        }
    }
    Write-Host "Legal notices verified: $($seen.Count) files."
    return
}
if (Test-Path -LiteralPath $target) { throw "Legal staging must be a new directory: $target" }
[IO.Directory]::CreateDirectory($target) | Out-Null
$entries = [Collections.Generic.List[object]]::new()
function Add-Notice([string]$Source, [string]$Name, [string]$Component, [string]$Version) {
    $file = Get-Item -LiteralPath $Source
    if ($file.PSIsContainer -or $file.Length -lt 100) { throw "Missing or empty notice: $Component" }
    Copy-Item -LiteralPath $Source -Destination (Join-Path $target $Name)
    $entries.Add([ordered]@{ path = $Name; component = $Component; version = $Version;
        sha256 = (Get-FileHash -LiteralPath (Join-Path $target $Name)).Hash.ToLowerInvariant() })
}
Add-Notice (Join-Path $repo 'LICENSE') 'LICENSE' 'Reactor V' 'MIT'
Add-Notice (Join-Path $repo 'THIRD_PARTY_NOTICES.md') 'THIRD_PARTY_NOTICES.md' 'Distribution notices' '1'
if (-not $StarterKit) {
    $assets = Get-Content -LiteralPath (Join-Path $repo 'src/ReactorV.Preloader/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($name in @('CefSharp.Common', 'CefSharp.OffScreen', 'chromiumembeddedframework.runtime.win-x64', 'Microsoft.Web.WebView2', 'Newtonsoft.Json')) {
        $matches = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name.Split('/')[0] -eq $name })
        if ($matches.Count -ne 1) { throw "Expected one restored version of $name" }
        $library = $matches[0]; $version = $library.Name.Split('/')[1]
        $roots = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            $candidate = Join-Path $_ $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate }
        })
        if ($roots.Count -ne 1) { throw "Ambiguous/missing restored dependency: $name" }
        $notices = @($library.Value.files | Where-Object { $_ -match '^(LICENSE|NOTICE)(\.(txt|md))?$' })
        if ($notices.Count -eq 0) { throw "No upstream notice found for $name" }
        foreach ($notice in $notices) {
            Add-Notice (Join-Path $roots[0] $notice) "$name-$version-$notice" $name $version
        }
    }
    if (-not $assets.libraries.PSObject.Properties['SharpDX/4.2.0']) { throw 'Revalidate SharpDX notice for the new version.' }
    Add-Notice (Join-Path $repo 'legal/SharpDX-LICENSE.txt') 'SharpDX-LICENSE.txt' 'SharpDX' '4.2.0'
    if (-not $NativeBuild -or -not $ChromiumCredits) { throw 'Native build and Chromium credits are required.' }
    Add-Notice (Join-Path $NativeBuild '_deps/minhook-src/LICENSE.txt') 'MinHook-LICENSE.txt' 'MinHook' '1.3.4'
    Add-Notice $ChromiumCredits 'Chromium-CREDITS.txt' 'Bundled Chromium components' 'same CEF runtime'
    Push-Location (Join-Path $repo 'web')
    try {
        $js = 'const p=require("node:path"); const names=["react","react-dom","scheduler"]; console.log(JSON.stringify(names.map(name=>{const file=require.resolve(name+"/package.json",{paths:[p.dirname(require.resolve("react-dom/package.json"))]});return {name,version:require(file).version,root:p.dirname(file)};})))'
        $packages = ($js | & node) | ConvertFrom-Json
        if ($LASTEXITCODE) { throw 'Could not resolve frontend license sources.' }
        foreach ($package in $packages) {
            Add-Notice (Join-Path $package.root 'LICENSE') "$($package.name)-LICENSE.txt" $package.name $package.version
        }
    } finally { Pop-Location }
}
$index = [ordered]@{ schema_version = 1; profile = $profile; project_license = 'MIT'; files = @($entries.ToArray()) }
[IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
& $PSCommandPath -Destination $target -VerifyOnly -StarterKit:$StarterKit
