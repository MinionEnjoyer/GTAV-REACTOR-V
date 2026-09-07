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
    Write-FixtureText (Join-Path $incomingPlugin 'ReactorV.Preloader.json') '{"externalGpuBrowserShadow":false,"externalGpuFrameRate":30}'
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

    # Consumer entrypoint, logo, hashed chunks and identity must survive a neutral
    # runtime upgrade together. Native binaries must still update normally.
    $consumerPlugin = Join-Path $testRoot 'consumer/plugins/ReactorV'
    $consumerBackup = Join-Path $testRoot 'consumer-backup/Plugin-ReactorV'
    $consumerTarget = Join-Path $testRoot 'consumer-target/plugins/ReactorV'
    Copy-FixtureTree $existingPlugin $consumerPlugin
    Write-FixtureText (Join-Path $consumerPlugin 'ui/reactor-ui.json') '{"schema_version":1,"profile":"partner-composition","owner":"partner","contains_consumer_content":true}'
    Write-FixtureText (Join-Path $consumerPlugin 'ui/index.html') '<script src="assets/app-old.js"></script>'
    Write-FixtureText (Join-Path $consumerPlugin 'ui/partner-logo.png') 'consumer-logo'
    Write-FixtureText (Join-Path $consumerPlugin 'ui/assets/app-old.css') 'consumer-style'
    Write-FixtureText (Join-Path $incomingPlugin 'ui/index.html') '<script src="assets/app-new.js"></script>'
    Write-FixtureText (Join-Path $incomingPlugin 'ui/reactor-ui.json') '{"schema_version":1,"profile":"reactor-runtime","contains_consumer_content":false}'
    $consumerManifest = @(Get-ReactorVPreservedFileManifest `
        -ExistingScriptRoot $existingScript -ExistingPluginRoot $consumerPlugin `
        -IncomingScriptRoot $incomingScript -IncomingPluginRoot $incomingPlugin)
    $emptyManifest = @(Get-ReactorVPreservedFileManifest `
        -ExistingScriptRoot (Join-Path $testRoot 'missing-script') `
        -ExistingPluginRoot (Join-Path $testRoot 'missing-plugin') `
        -IncomingScriptRoot $incomingScript -IncomingPluginRoot $incomingPlugin)
    Assert-OwnershipTest ($emptyManifest.Count -eq 0) 'Fresh install unexpectedly preserved UI content.'
    $neutralManifest = @(Get-ReactorVPreservedFileManifest `
        -ExistingScriptRoot $incomingScript -ExistingPluginRoot $incomingPlugin `
        -IncomingScriptRoot $incomingScript -IncomingPluginRoot $incomingPlugin)
    Assert-OwnershipTest (@($neutralManifest | Where-Object { $_.RelativePath -eq 'ui/index.html' }).Count -eq 0) 'Neutral UI was incorrectly frozen during upgrade.'
    $consumerPaths = @($consumerManifest | Where-Object Scope -eq 'Plugin' | ForEach-Object RelativePath)
    foreach ($relative in @('ui/index.html','ui/reactor-ui.json','ui/partner-logo.png','ui/assets/app-old.js','ui/assets/app-old.css')) {
        Assert-OwnershipTest ($consumerPaths -contains $relative) "Consumer UI not preserved: $relative"
    }
    Assert-OwnershipTest ($consumerPaths -notcontains 'libcef.dll') 'Consumer ownership must not capture native runtime files.'
    Copy-FixtureTree $consumerPlugin $consumerBackup
    Copy-FixtureTree $incomingPlugin $consumerTarget
    Restore-ReactorVPreservedFiles -Manifest $consumerManifest `
        -BackupScriptRoot $backupScript -BackupPluginRoot $consumerBackup `
        -TargetScriptRoot $targetScript -TargetPluginRoot $consumerTarget
    Assert-OwnershipTest (Test-ReactorVPreservedFileManifest -Manifest $consumerManifest `
        -TargetScriptRoot $targetScript -TargetPluginRoot $consumerTarget) 'Consumer UI restore did not verify.'
    Assert-OwnershipTest ([IO.File]::ReadAllText((Join-Path $consumerTarget 'libcef.dll')) -ceq 'new-cef') 'Consumer UI preservation blocked runtime upgrade.'
    Write-FixtureText (Join-Path $consumerPlugin 'ui/reactor-ui.json') '{"schema_version":1,"profile":"partner-composition","contains_consumer_content":true}'
    $invalidUiRejected = $false
    try {
        [void]@(Get-ReactorVPreservedFileManifest -ExistingScriptRoot $existingScript `
            -ExistingPluginRoot $consumerPlugin -IncomingScriptRoot $incomingScript -IncomingPluginRoot $incomingPlugin)
    } catch { $invalidUiRejected = $_.Exception.Message -like '*UI ownership marker*' }
    Assert-OwnershipTest $invalidUiRejected 'Invalid consumer identity was silently replaced.'

    "OWNERSHIP_FILESYSTEM_PASS files=$($manifest.Count) consumerFiles=$($consumerManifest.Count)"
} finally {
    if (Test-Path -LiteralPath $resolvedTest) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
