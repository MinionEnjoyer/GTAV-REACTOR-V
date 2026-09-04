[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$UiRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $UiRoot).Path.TrimEnd('\', '/')
$manifest = Get-Content -LiteralPath (Join-Path $root 'reactor-ui.json') -Raw | ConvertFrom-Json
if ($manifest.schema_version -ne 1 -or $manifest.profile -ne 'reactor-runtime' -or
    $manifest.contains_consumer_content -ne $false) { throw 'Not a standalone Reactor UI build.' }
$expected = @{}
foreach ($item in $manifest.files) {
    $relative = [string]$item.path
    if ($relative -notmatch '^(index\.html|ragewebui\.js|ragewebui-logo\.png|fonts/(BebasNeue-Regular\.ttf|Oswald-Variable\.ttf|OFL-Bebas-Neue\.txt|OFL-Oswald\.txt)|assets/[A-Za-z0-9_-]+\.(js|css))$' -or
        $relative -match '(?i)allin1|gbay' -or $expected.ContainsKey($relative) -or
        $item.sha256 -notmatch '^[a-f0-9]{64}$') { throw "Forbidden or duplicate UI entry: $relative" }
    $expected[$relative] = $item.sha256
}
foreach ($required in @('index.html', 'ragewebui.js', 'ragewebui-logo.png',
    'fonts/BebasNeue-Regular.ttf', 'fonts/Oswald-Variable.ttf',
    'fonts/OFL-Bebas-Neue.txt', 'fonts/OFL-Oswald.txt')) {
    if (-not $expected.ContainsKey($required)) { throw "Missing required UI entry: $required" }
}
$seen = 0
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'UI links are not allowed.' }
    if ($file.PSIsContainer) { continue }
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($relative -eq 'reactor-ui.json') { continue }
    if (-not $expected.ContainsKey($relative)) { throw "Unmanifested UI payload: $relative" }
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$relative]) {
        throw "UI payload changed after build: $relative"
    }
    if ($file.Extension -in @('.js', '.css', '.html', '.json')) {
        if ([IO.File]::ReadAllText($file.FullName) -match '(?i)allin1|gbay') { throw "Consumer content in runtime UI: $relative" }
    }
    $seen++
}
if ($seen -ne $expected.Count) { throw 'UI manifest references missing payloads.' }
Write-Host "Standalone Reactor UI validated: $seen files; no consumer code or branding."
