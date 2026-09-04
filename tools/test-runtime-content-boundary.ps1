[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'web/dist'
$audit = Join-Path $PSScriptRoot 'Assert-ReactorRuntimeContent.ps1'
& $audit -UiRoot $source
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ReactorV-ContentBoundary-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$checks = 1
function Expect-Blocked([string]$Name, [scriptblock]$Alter) {
    $fixture = Join-Path $testRoot $Name
    Copy-Item -LiteralPath $source -Destination $fixture -Recurse
    & $Alter $fixture
    $blocked = $false
    try { & $audit -UiRoot $fixture } catch { $blocked = $true }
    if (-not $blocked) { throw "Content gate accepted $Name" }
    $script:checks++
}
Expect-Blocked 'unexpected-logo' { param($fixture)
    Copy-Item -LiteralPath (Join-Path $fixture 'ragewebui-logo.png') -Destination (Join-Path $fixture 'allin1-logo.png')
}
Expect-Blocked 'wrong-profile' { param($fixture)
    $file = Join-Path $fixture 'reactor-ui.json'
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $manifest.profile = 'consumer-adapter'
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json -Depth 8))
}
Expect-Blocked 'missing-asset' { param($fixture)
    Remove-Item -LiteralPath (Join-Path $fixture 'ragewebui-logo.png')
}
Expect-Blocked 'modified-ui' { param($fixture)
    [IO.File]::WriteAllText((Join-Path $fixture 'index.html'), '<div>Unexpected page</div>')
}
Expect-Blocked 'consumer-code-valid-hash' { param($fixture)
    $file = Join-Path $fixture 'reactor-ui.json'
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $entry = $manifest.files | Where-Object path -eq 'ragewebui.js'
    $code = Join-Path $fixture $entry.path
    [IO.File]::WriteAllText($code, 'window.fixture = "GBAY";')
    $entry.sha256 = (Get-FileHash -LiteralPath $code).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json -Depth 8))
}
Expect-Blocked 'manifest-path-escape' { param($fixture)
    $file = Join-Path $fixture 'reactor-ui.json'
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $manifest.files[0].path = '../outside.js'
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json -Depth 8))
}
Expect-Blocked 'missing-font-notice-and-manifest-entry' { param($fixture)
    $file = Join-Path $fixture 'reactor-ui.json'
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $manifest.files = @($manifest.files | Where-Object path -ne 'fonts/OFL-Oswald.txt')
    Remove-Item -LiteralPath (Join-Path $fixture 'fonts/OFL-Oswald.txt')
    [IO.File]::WriteAllText($file, ($manifest | ConvertTo-Json -Depth 8))
}
Write-Host "Runtime content boundary: $checks checks passed. Fixtures: $testRoot"
