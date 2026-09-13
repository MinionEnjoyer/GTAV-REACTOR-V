param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new fixture directory.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$driver=Join-Path $repo 'artifacts\issue-1-controller-startup\Local-Controller-Test.ps1'
if ((Get-ComparisonHash $driver) -cne 'fc401c9a4a6aad1bb918f25ea1a04c4aa5f3e2552fb6b2e7e1ba478bf12086a0') { throw 'Driver changed.' }
$userProfile = [Environment]::GetFolderPath('UserProfile')
$baseline=Get-Content -LiteralPath (Join-Path $userProfile 'ReactorV-Diagnostics\isolation-backups\4f61325b43bb4854b74831f2551ca72c\receipt.json') -Raw|ConvertFrom-Json
$originalRuntime='54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6'
$candidate='5359dc1e4cea9225288a7057dc934f985608774ae3fd55ddff78fb663c53ed60'
$fixture=Join-Path $OutputDirectory 'fixture-game'; $run=Join-Path $OutputDirectory 'run'; $store=Join-Path $OutputDirectory 'backups'
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name) { if(-not $Condition){throw ('FAIL: '+$Name)}; $checks.Add($Name) }
foreach($entry in $baseline.Identity.PSObject.Properties) {
    $expected=if($entry.Name -eq 'plugins/ReactorV/RageWebUI.Runtime.dll'){$originalRuntime}else{$entry.Value}
    $source=Join-Path $baseline.Root $entry.Name
    Check ((Get-ComparisonHash $source) -ieq $expected) ('real baseline '+$entry.Name)
    $target=Join-Path $fixture $entry.Name
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath $source -Destination $target
}
function Driver([string]$Action,[string]$BackupStore=$store) {
    & $shell -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $driver -Action $Action -GameDirectory $fixture -OutputDirectory $run -StoreDirectory $BackupStore
    $script:driverExit=$LASTEXITCODE
}
Driver 'Preflight'; Check ($driverExit -eq 0) 'fixture preflight'
Driver 'PrepareOnly'; Check ($driverExit -eq 0) 'fixture prepare'
$runtimeTarget=Join-Path $fixture 'plugins\ReactorV\RageWebUI.Runtime.dll'
Check ((Get-ComparisonHash $runtimeTarget) -ceq $candidate) 'candidate applied'
$runtimeReceipt=Get-Content -LiteralPath "$run\runtime-install-receipt.json" -Raw|ConvertFrom-Json
Check ((Get-ComparisonHash $runtimeReceipt.runtimeBackup) -ceq $originalRuntime) 'original runtime backup'
$nativeReceipt=Get-Content -LiteralPath (Join-Path $store ($runtimeReceipt.isolationId+'\receipt.json')) -Raw|ConvertFrom-Json
foreach($change in $nativeReceipt.Changes) {
    $target=Join-Path $fixture $change.Relative
    if ($null -eq $change.AppliedHash) { Check (-not (Test-Path -LiteralPath $target)) ('native file absent '+$change.Relative) }
    else { Check ((Get-ComparisonHash $target) -ieq $change.AppliedHash) ('config applied '+$change.Relative) }
}
Driver 'Restore' (Join-Path $OutputDirectory 'wrong-backups')
Check ($driverExit -ne 0) 'wrong backup store refused'
Check ((Get-ComparisonHash $runtimeTarget) -ceq $candidate) 'wrong store did not overwrite'
# Deliberate conflict only in this newly created fixture tree. Restore must not
# erase an intervening file. Retain all fixture evidence and repair only our copy.
$conflict=Join-Path $repo 'artifacts\issue-1-controller-startup\probe-source\plugins\ReactorV\Newtonsoft.Json.dll'
Copy-Item -LiteralPath $conflict -Destination $runtimeTarget -Force
Driver 'Restore'; Check ($driverExit -ne 0) 'intervening runtime refused'
Check ((Get-ComparisonHash $runtimeTarget) -ceq (Get-ComparisonHash $conflict)) 'intervening file preserved'
Copy-Item -LiteralPath (Join-Path $repo 'artifacts\issue-1-controller-startup\probe-source\plugins\ReactorV\RageWebUI.Runtime.dll') -Destination $runtimeTarget -Force
Driver 'Restore'; Check ($driverExit -eq 0) 'fixture restored'
foreach($entry in $baseline.Identity.PSObject.Properties) {
    $expected=if($entry.Name -eq 'plugins/ReactorV/RageWebUI.Runtime.dll'){$originalRuntime}else{$entry.Value}
    Check ((Get-ComparisonHash (Join-Path $fixture $entry.Name)) -ieq $expected) ('fixture restored '+$entry.Name)
    Check ((Get-ComparisonHash (Join-Path $baseline.Root $entry.Name)) -ieq $expected) ('real installation unchanged '+$entry.Name)
}
Write-CollectorJson "$OutputDirectory\result.json" ([ordered]@{passed=$true;checks=@($checks.ToArray());gameLaunched=$false;realInstallationChanged=$false;fixture=$fixture;driverSha256=(Get-ComparisonHash $driver)})
Write-Host ('PASS: '+$checks.Count+' deployment/restore checks on private copies only.')
