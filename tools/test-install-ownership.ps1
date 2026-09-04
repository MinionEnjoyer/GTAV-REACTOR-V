[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-OwnershipTest {
    param(
        [Parameter(Mandatory)] [bool]$Condition,
        [Parameter(Mandatory)] [string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Write-FixtureText {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Value
    )
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Copy-FixtureTree {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$Destination
    )
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

$module = Join-Path $PSScriptRoot 'ReactorV.InstallOwnership.psm1'
Import-Module -Name $module -Force -ErrorAction Stop

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ('ReactorV-ownership-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTest = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTest.StartsWith(
        $resolvedTemp,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe ownership test root: $resolvedTest"
}

try {
    $existingScript = Join-Path $testRoot 'existing\scripts\ReactorV'
    $existingPlugin = Join-Path $testRoot 'existing\plugins\ReactorV'
    $incomingScript = Join-Path $testRoot 'incoming\scripts\ReactorV'
    $incomingPlugin = Join-Path $testRoot 'incoming\plugins\ReactorV'
    $backupScript = Join-Path $testRoot 'backup\Script-ReactorV'
    $backupPlugin = Join-Path $testRoot 'backup\Plugin-ReactorV'
    $targetScript = Join-Path $testRoot 'target\scripts\ReactorV'
    $targetPlugin = Join-Path $testRoot 'target\plugins\ReactorV'

    $userSettings = "{`n  `"toggleKey`": `"F8`",`n  `"renderer`": `"directx`"`n}`n"
    Write-FixtureText (Join-Path $existingScript 'ReactorV.json') $userSettings
    Write-FixtureText (Join-Path $existingScript 'RageWebUI.Script.dll') 'old-core'
    Write-FixtureText (Join-Path $existingScript 'RageWebUI.Legacy.dll') 'retired-core'
    Write-FixtureText (Join-Path $existingScript 'ReactorV.contract.json') 'old-contract'
    Write-FixtureText (Join-Path $existingScript 'ReactorV.RenderHook.log') 'old-log'
    Write-FixtureText (Join-Path $existingScript 'Partner.plugin') 'partner-plugin'
    Write-FixtureText (Join-Path $existingScript 'Partner.contract.json') 'partner-contract'
    Write-FixtureText (Join-Path $existingScript 'Partner\helper.dll') 'partner-helper'
    Write-FixtureText (Join-Path $existingPlugin 'libcef.dll') 'old-cef'
    $preloaderSettings = "{`n  `"externalGpuBrowserShadow`": true,`n  `"externalGpuFrameRate`": 30`n}`n"
    Write-FixtureText (Join-Path $existingPlugin 'ReactorV.Preloader.json') $preloaderSettings
    Write-FixtureText (Join-Path $existingPlugin 'ReactorV.EnhancedLiveTest.json') 'old-edition-marker'
    Write-FixtureText (Join-Path $existingPlugin 'ui\assets\app-old.js') 'old-ui'
    Write-FixtureText (Join-Path $existingPlugin 'ui\assets\allin1\vehicle.png') 'allin1-art'
    Write-FixtureText (Join-Path $existingPlugin 'ui\assets\partner\preview.webp') 'partner-art'
    Write-FixtureText (Join-Path $existingPlugin 'extensions\partner\settings.json') 'partner-settings'

    Write-FixtureText (Join-Path $incomingScript 'ReactorV.json') '{"toggleKey":"F9"}'
    Write-FixtureText (Join-Path $incomingScript 'RageWebUI.Script.dll') 'new-core'
    Write-FixtureText (Join-Path $incomingScript 'ReactorV.contract.json') 'new-contract'
    Write-FixtureText (Join-Path $incomingPlugin 'libcef.dll') 'new-cef'
    Write-FixtureText (Join-Path $incomingPlugin 'ReactorV.Preloader.json') 'new-preloader-settings'
    Write-FixtureText (Join-Path $incomingPlugin 'ReactorV.EnhancedLiveTest.json') 'new-edition-marker'
    Write-FixtureText (Join-Path $incomingPlugin 'ui\assets\app-new.js') 'new-ui'

    $manifest = @(Get-ReactorVPreservedFileManifest `
        -ExistingScriptRoot $existingScript `
        -ExistingPluginRoot $existingPlugin `
        -IncomingScriptRoot $incomingScript `
        -IncomingPluginRoot $incomingPlugin)
    $identities = @($manifest | ForEach-Object {
        "$($_.Scope):$($_.RelativePath)"
    })
    foreach ($required in @(
        'Script:ReactorV.json',
        'Script:Partner.plugin',
        'Script:Partner.contract.json',
        'Script:Partner/helper.dll',
        'Plugin:ReactorV.Preloader.json',
        'Plugin:ui/assets/allin1/vehicle.png',
        'Plugin:ui/assets/partner/preview.webp',
        'Plugin:extensions/partner/settings.json'
    )) {
        Assert-OwnershipTest ($identities -contains $required) "Missing preserved fixture: $required"
    }
    foreach ($forbidden in @(
        'Script:RageWebUI.Script.dll',
        'Script:RageWebUI.Legacy.dll',
        'Script:ReactorV.contract.json',
        'Script:ReactorV.RenderHook.log',
        'Plugin:libcef.dll',
        'Plugin:ReactorV.EnhancedLiveTest.json',
        'Plugin:ui/assets/app-old.js'
    )) {
        Assert-OwnershipTest ($identities -notcontains $forbidden) "Core fixture was treated as extension-owned: $forbidden"
    }

    Copy-FixtureTree $existingScript $backupScript
    Copy-FixtureTree $existingPlugin $backupPlugin
    Copy-FixtureTree $incomingScript $targetScript
    Copy-FixtureTree $incomingPlugin $targetPlugin
    Restore-ReactorVPreservedFiles `
        -Manifest $manifest `
        -BackupScriptRoot $backupScript `
        -BackupPluginRoot $backupPlugin `
        -TargetScriptRoot $targetScript `
        -TargetPluginRoot $targetPlugin

    Assert-OwnershipTest (
        (Test-ReactorVPreservedFileManifest `
            -Manifest $manifest `
            -TargetScriptRoot $targetScript `
            -TargetPluginRoot $targetPlugin)) `
        'The restored extension manifest did not verify.'
    Assert-OwnershipTest (
        [IO.File]::ReadAllText((Join-Path $targetScript 'ReactorV.json')) -ceq
            $userSettings) `
        'The user ReactorV.json was not preserved byte-for-byte.'
    Assert-OwnershipTest (
        [IO.File]::ReadAllText((Join-Path $targetScript 'RageWebUI.Script.dll')) -ceq
            'new-core') `
        'Package-owned script core was overwritten by preservation.'
    Assert-OwnershipTest (
        [IO.File]::ReadAllText((Join-Path $targetPlugin 'libcef.dll')) -ceq
            'new-cef') `
        'Package-owned plugin core was overwritten by preservation.'
    Assert-OwnershipTest (
        [IO.File]::ReadAllText((Join-Path $targetPlugin 'ReactorV.Preloader.json')) -ceq
            $preloaderSettings) `
        'The user preloader settings were not preserved byte-for-byte.'
    Assert-OwnershipTest (
        [IO.File]::ReadAllText((Join-Path $targetPlugin 'ReactorV.EnhancedLiveTest.json')) -ceq
            'new-edition-marker') `
        'Package-authoritative edition marker was overwritten by preservation.'
    Assert-OwnershipTest (
        -not (Test-Path -LiteralPath (Join-Path $targetScript 'ReactorV.RenderHook.log'))) `
        'Generated runtime logs should not be restored into the new package.'

    Write-FixtureText (Join-Path $targetPlugin 'ui\assets\partner\preview.webp') 'tampered'
    Assert-OwnershipTest (
        -not (Test-ReactorVPreservedFileManifest `
            -Manifest $manifest `
            -TargetScriptRoot $targetScript `
            -TargetPluginRoot $targetPlugin)) `
        'Tampered extension content incorrectly passed manifest verification.'

    $collisionPlugin = Join-Path $testRoot 'collision\plugins\ReactorV'
    Copy-FixtureTree $incomingPlugin $collisionPlugin
    Write-FixtureText (Join-Path $collisionPlugin 'ui\assets\partner\owned.png') 'collision'
    $collisionRejected = $false
    try {
        [void]@(Get-ReactorVPreservedFileManifest `
            -ExistingScriptRoot $existingScript `
            -ExistingPluginRoot $existingPlugin `
            -IncomingScriptRoot $incomingScript `
            -IncomingPluginRoot $collisionPlugin)
    } catch {
        $collisionRejected = $_.Exception.Message -like '*collides with extension-owned UI content*'
    }
    Assert-OwnershipTest $collisionRejected 'An incoming package was allowed to claim an existing extension namespace.'

    $collisionScript = Join-Path $testRoot 'collision\scripts\ReactorV'
    Copy-FixtureTree $incomingScript $collisionScript
    Write-FixtureText (Join-Path $collisionScript 'Partner.plugin') 'package-collision'
    $pluginCollisionRejected = $false
    try {
        [void]@(Get-ReactorVPreservedFileManifest `
            -ExistingScriptRoot $existingScript `
            -ExistingPluginRoot $existingPlugin `
            -IncomingScriptRoot $collisionScript `
            -IncomingPluginRoot $incomingPlugin)
    } catch {
        $pluginCollisionRejected = $_.Exception.Message -like '*collides with extension-owned script content*'
    }
    Assert-OwnershipTest $pluginCollisionRejected 'An incoming package was allowed to overwrite a third-party plugin.'

    $invalidExistingPlugin = Join-Path $testRoot 'invalid\plugins\ReactorV'
    Copy-FixtureTree $existingPlugin $invalidExistingPlugin
    Write-FixtureText `
        (Join-Path $invalidExistingPlugin 'ReactorV.Preloader.json') `
        '{"externalGpuBrowserShadow":"not-a-boolean"}'
    $invalidPreloaderRejected = $false
    try {
        [void]@(Get-ReactorVPreservedFileManifest `
            -ExistingScriptRoot $existingScript `
            -ExistingPluginRoot $invalidExistingPlugin `
            -IncomingScriptRoot $incomingScript `
            -IncomingPluginRoot $incomingPlugin)
    } catch {
        $invalidPreloaderRejected =
            $_.Exception.Message -like '*ReactorV.Preloader.json is invalid*'
    }
    Assert-OwnershipTest `
        $invalidPreloaderRejected `
        'An invalid preloader configuration was silently preserved or overwritten.'

    "OWNERSHIP_FILESYSTEM_PASS files=$($manifest.Count)"
} finally {
    if (Test-Path -LiteralPath $resolvedTest) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
