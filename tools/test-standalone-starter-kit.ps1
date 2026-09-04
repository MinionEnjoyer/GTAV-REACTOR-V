[CmdletBinding()]
param([Parameter(Mandatory)] [string]$KitRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repo ('artifacts/starter-install-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$manager = Join-Path $KitRoot 'Manage-Starter.ps1'
$packageA = Join-Path $KitRoot 'packages/StarterA'
$packageB = Join-Path $KitRoot 'packages/StarterB'
$script:Checks = 0
function Assert-Result([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:Checks++
}
function Expect-Blocked([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:Checks++
        return
    }
    throw "Expected a blocked operation: $Pattern"
}
function Put-Text([string]$Root, [string]$Relative, [string]$Text) {
    $path = Join-Path $Root $Relative
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text)
}
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12))
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

foreach ($edition in @('legacy', 'enhanced')) {
    $game = Join-Path $testRoot $edition
    $exe = if ($edition -eq 'legacy') { 'GTA5.exe' } else { 'GTA5_Enhanced.exe' }
    Put-Text $game $exe 'TEST FIXTURE ONLY - NOT EXECUTABLE'
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'Missing or oversized JSON'
    $contractPath = Join-Path $game 'scripts/ReactorV/ReactorV.contract.json'
    $contract = Get-Content (Join-Path $repo 'ReactorV.contract.json') -Raw | ConvertFrom-Json
    Put-Text $game 'scripts/ReactorV/ReactorV.contract.json' ($contract | ConvertTo-Json -Depth 8)
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'Required dependency is missing'
    foreach ($file in @('scripts/ReactorV/RageWebUI.Script.dll', 'plugins/ReactorV/ReactorV.Preloader.exe', 'ScriptHookV.dll', 'ScriptHookVDotNet3.dll')) {
        Put-Text $game $file 'shared runtime fixture; never owned by a consumer'
    }
    foreach ($file in @('scripts/ReactorV/RageWebUI.Core.dll', 'plugins/ReactorV/RageWebUI.Core.dll')) {
        Copy-Item -LiteralPath (Join-Path $KitRoot 'reference/RageWebUI.Core.dll') -Destination (Join-Path $game $file)
    }
    Put-Text $game 'scripts/UnrelatedMod.dll' 'unrelated mod must survive'
    $baseline = @{}
    foreach ($file in Get-ChildItem -LiteralPath $game -Recurse -File) { $baseline[$file.FullName] = Hash $file.FullName }
    $check = & $manager -Mode Check -GameRoot $game -PackageRoot $packageA
    Assert-Result ($check.status -eq 'compatible' -and -not $check.installed) "$edition check failed"
    Assert-Result (-not (Test-Path (Join-Path $game 'scripts/.reactorv/consumers'))) 'Check mutated installation'
    $contract.runtime_version = '0.1.0'; Write-Json $contractPath $contract
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'Install Reactor'
    $contract.runtime_version = '1.0.0'; Write-Json $contractPath $contract
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'Install Reactor'
    $contract.runtime_version = '0.2.0'; $contract.extension_api_version = 2; Write-Json $contractPath $contract
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'Install Reactor'
    $contract.extension_api_version = 1; $savedCapabilities = $contract.capabilities
    $contract.capabilities = @('story.extensions'); Write-Json $contractPath $contract
    Expect-Blocked { & $manager -Mode Check -GameRoot $game -PackageRoot $packageA } 'missing required capability'
    $contract.capabilities = $savedCapabilities; Write-Json $contractPath $contract
    $sharedCore = Join-Path $game 'plugins/ReactorV/RageWebUI.Core.dll'
    [IO.File]::AppendAllText($sharedCore, 'mismatch')
    Expect-Blocked { & $manager -Mode Install -GameRoot $game -PackageRoot $packageA } 'shared Core copies disagree'
    Copy-Item -LiteralPath (Join-Path $KitRoot 'reference/RageWebUI.Core.dll') -Destination $sharedCore
    $contract.runtime_version = '0.2.1'; Write-Json $contractPath $contract
    Expect-Blocked { & $manager -Mode Install -GameRoot $game -PackageRoot $packageA } 'Core versions'
    $contract.runtime_version = '0.2.0'; Write-Json $contractPath $contract
    $baseline[$contractPath] = Hash $contractPath
    $a = Join-Path $game 'scripts/ReactorV.StarterA.dll'
    Put-Text $game 'scripts/ReactorV.StarterA.dll' 'unowned'
    Expect-Blocked { & $manager -Mode Install -GameRoot $game -PackageRoot $packageA } 'unowned or modified'
    # Exact generated fixture file; no recursive deletion or user installation involved.
    Remove-Item -LiteralPath $a
    & $manager -Mode Install -GameRoot $game -PackageRoot $packageA | Out-Null
    & $manager -Mode Install -GameRoot $game -PackageRoot $packageB | Out-Null
    & $manager -Mode Install -GameRoot $game -PackageRoot $packageA | Out-Null
    Assert-Result ((& $manager -Mode Check -GameRoot $game -PackageRoot $packageA).installed) 'Install receipt missing'
    Remove-Item -LiteralPath $a
    $damaged = & $manager -Mode Check -GameRoot $game -PackageRoot $packageA
    Assert-Result (-not $damaged.installed -and $damaged.repair_required) 'Receipt alone incorrectly reported a missing consumer as installed'
    & $manager -Mode Install -GameRoot $game -PackageRoot $packageA | Out-Null
    Assert-Result ((& $manager -Mode Check -GameRoot $game -PackageRoot $packageA).installed) 'Repair failed to restore the missing owned payload'
    $aReceipt = Join-Path $game 'scripts/.reactorv/consumers/reactorv.starter-a.json'
    $aReceiptOriginal = [IO.File]::ReadAllText($aReceipt)
    $forgedReceipt = $aReceiptOriginal | ConvertFrom-Json
    $forgedReceipt.files[0].path = 'scripts/UnrelatedMod.dll'
    Write-Json $aReceipt $forgedReceipt
    Expect-Blocked { & $manager -Mode Uninstall -GameRoot $game -PackageRoot $packageA } 'Invalid ownership receipt'
    [IO.File]::WriteAllText($aReceipt, $aReceiptOriginal)
    $b = Join-Path $game 'scripts/ReactorV.StarterB.dll'
    $bHash = Hash $b
    [IO.File]::AppendAllText($a, 'user modification')
    Expect-Blocked { & $manager -Mode Uninstall -GameRoot $game -PackageRoot $packageA } 'unowned or modified'
    Copy-Item -LiteralPath (Join-Path $packageA 'payload/scripts/ReactorV.StarterA.dll') -Destination $a
    & $manager -Mode Uninstall -GameRoot $game -PackageRoot $packageA | Out-Null
    Assert-Result (-not (Test-Path -LiteralPath $a)) 'Starter A was not removed'
    Assert-Result ((Hash $b) -eq $bHash) 'Removing Starter A changed Starter B'
    Assert-Result ((& $manager -Mode Check -GameRoot $game -PackageRoot $packageB).installed) 'B receipt was removed with A'
    foreach ($path in $baseline.Keys) { Assert-Result ((Hash $path) -eq $baseline[$path]) "Shared/unowned file changed: $path" }
    $receipt = Get-Content (Join-Path $game 'scripts/.reactorv/consumers/reactorv.starter-b.json') -Raw
    Assert-Result ($receipt -notmatch '(?i)[A-Z]:[\\/]|Users|OneDrive') 'Machine-local paths leaked into receipt'
    # Consumer removal must still work if its shared dependency was removed externally.
    Remove-Item -LiteralPath $contractPath
    & $manager -Mode Uninstall -GameRoot $game -PackageRoot $packageB | Out-Null
    Assert-Result (-not (Test-Path -LiteralPath $b)) 'B removal incorrectly required a working dependency'
    & $manager -Mode Uninstall -GameRoot $game -PackageRoot $packageB | Out-Null
}

$bad = Join-Path $testRoot 'bad-package'
[IO.Directory]::CreateDirectory($bad) | Out-Null
$manifest = Get-Content (Join-Path $packageA 'consumer.json') -Raw | ConvertFrom-Json
foreach ($path in @('../escape.dll', 'scripts/ReactorV/RageWebUI.Core.dll', 'scripts/ReactorV.StarterB.dll', 'C:/Users/developer/file.dll')) {
    $manifest.files[0].path = $path
    Write-Json (Join-Path $bad 'consumer.json') $manifest
    Expect-Blocked { & $manager -Mode Install -GameRoot (Join-Path $testRoot 'legacy') -PackageRoot $bad } 'cannot own'
}
$manifest.files[0].path = 'scripts/ReactorV.StarterA.dll'
Write-Json (Join-Path $bad 'consumer.json') $manifest
Expect-Blocked { & $manager -Mode Install -GameRoot (Join-Path $testRoot 'legacy') -PackageRoot $bad } 'payload hash mismatch or missing'

# Load the two actual compiled consumer DLLs into one real .NET Framework process.
# Their menu model has no GTA native calls; do not instantiate the GTA.Script entry points here.
$nugetOutput = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE) { throw 'Cannot locate restored test dependencies.' }
$nuget = ($nugetOutput -replace '^global-packages:\s*', '').Trim()
$jsonAssembly = Join-Path $nuget 'newtonsoft.json/13.0.4/lib/net45/Newtonsoft.Json.dll'
[Reflection.Assembly]::LoadFrom($jsonAssembly) | Out-Null
$coreAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $KitRoot 'reference/RageWebUI.Core.dll'))
$api = $coreAssembly.GetType('ReactorV.Integration.ReactorHostApi', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$describe = $api.GetMethod('DescribeExtensions', $flags)
$aAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $packageA 'payload/scripts/ReactorV.StarterA.dll'))
$bAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $packageB 'payload/scripts/ReactorV.StarterB.dll'))
foreach ($assembly in @($aAssembly, $bAssembly)) {
    Assert-Result (@($assembly.GetReferencedAssemblies() | Where-Object Name -Match 'ALLIN1').Count -eq 0) 'Consumer references ALLIN1'
}
$aType = $aAssembly.GetType('ReactorV.Starter.StarterExtension', $true)
$bType = $bAssembly.GetType('ReactorV.Starter.StarterExtension', $true)
$aInstance = [Activator]::CreateInstance($aType, @('reactorv.starter-a', 'Standalone A'))
$bInstance = [Activator]::CreateInstance($bType, @('reactorv.starter-b', 'Standalone B'))
try {
    Assert-Result ($describe.Invoke($null, @()).Count -eq 2) 'Compiled starters did not join one shared registry'
    $aInstance.Dispose()
    Assert-Result ($describe.Invoke($null, @()).Count -eq 1) 'Unloading compiled Starter A damaged Starter B'
    Assert-Result ($describe.Invoke($null, @())[0]['id'].ToString() -eq 'reactorv.starter-b') 'Wrong consumer survived'
} finally { $aInstance.Dispose(); $bInstance.Dispose() }
Assert-Result ($describe.Invoke($null, @()).Count -eq 0) 'Compiled starter leaked registration after disposal'
[pscustomobject]@{ checks = $script:Checks; status = 'passed'; fixture_root = $testRoot; actual_gta_installations_changed = $false }
