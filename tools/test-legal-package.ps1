[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$LegalRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$audit = Join-Path $PSScriptRoot 'Stage-ReactorLegal.ps1'
& $audit -Destination $LegalRoot -VerifyOnly
$root = Join-Path ([IO.Path]::GetTempPath()) ('ReactorV-LegalTests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$checks = 1
function Expect-Blocked([string]$Name, [scriptblock]$Alter) {
    $fixture = Join-Path $root $Name
    Copy-Item -LiteralPath $LegalRoot -Destination $fixture -Recurse
    & $Alter $fixture
    $blocked = $false
    try { & $audit -Destination $fixture -VerifyOnly } catch { $blocked = $true }
    if (-not $blocked) { throw "Legal gate accepted $Name" }
    $script:checks++
}
Expect-Blocked 'missing-license' { param($f) Remove-Item -LiteralPath (Join-Path $f 'LICENSE') }
Expect-Blocked 'modified-notice' { param($f) [IO.File]::WriteAllText((Join-Path $f 'SharpDX-LICENSE.txt'), 'Incomplete') }
Expect-Blocked 'omitted-browser-credits' { param($f)
    $file = Join-Path $f 'components.json'
    $index = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $index.files = @($index.files | Where-Object path -ne 'Chromium-CREDITS.txt')
    Remove-Item -LiteralPath (Join-Path $f 'Chromium-CREDITS.txt')
    [IO.File]::WriteAllText($file, ($index | ConvertTo-Json -Depth 5))
}
Expect-Blocked 'wrong-profile' { param($f)
    $file = Join-Path $f 'components.json'
    $index = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $index.profile = 'starter'
    [IO.File]::WriteAllText($file, ($index | ConvertTo-Json -Depth 5))
}
Expect-Blocked 'path-escape' { param($f)
    $file = Join-Path $f 'components.json'
    $index = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $index.files[0].path = '../LICENSE'
    [IO.File]::WriteAllText($file, ($index | ConvertTo-Json -Depth 5))
}
$manifest = Get-Content -LiteralPath (Join-Path $LegalRoot 'components.json') -Raw
if ($manifest -match '(?i)([A-Z]:[\\/]|Users[\\/])') { throw 'Machine-specific paths leaked into legal manifest.' }
$checks++
Write-Host "Legal packaging: $checks checks passed."
