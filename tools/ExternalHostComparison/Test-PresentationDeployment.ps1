param([Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new private fixture directory.' }
$userProfile = [Environment]::GetFolderPath('UserProfile')
$baseline=Get-Content -LiteralPath (Join-Path $userProfile 'ReactorV-Diagnostics\isolation-backups\1ec91a465b7d47e2b1acfcc0d95cd8f4\receipt.json') -Raw|ConvertFrom-Json
$originalRuntime='54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6'
$fixture=Join-Path $OutputDirectory 'fixture-game'
$driver=Join-Path $PSScriptRoot 'Deploy-PresentationTest.ps1'
$shell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$checks=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Name) { if (-not $Condition) { throw ('FAIL: '+$Name) }; $checks.Add($Name) }
$expected=@{}
$baseline.Identity|Add-Member -NotePropertyName 'plugins/ReactorV/RageWebUI.Core.dll' -NotePropertyValue '674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862'
$baseline.Identity|Add-Member -NotePropertyName 'scripts/ReactorV/RageWebUI.Core.dll' -NotePropertyValue '674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862'
foreach ($entry in $baseline.Identity.PSObject.Properties) {
    $hash=if ($entry.Name -eq 'plugins/ReactorV/RageWebUI.Runtime.dll') { $originalRuntime } else { $entry.Value }
    $expected[$entry.Name]=$hash
    $source=Join-Path $baseline.Root $entry.Name
    Check ((Get-ComparisonHash $source) -ceq $hash) ('live baseline '+$entry.Name)
    $target=Join-Path $fixture $entry.Name
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath $source -Destination $target
}
function Driver([string]$Action,[string]$Run,[int]$Fault=0,[string]$StoreOverride='') {
    $out=Join-Path $OutputDirectory $Run
    $store=if($StoreOverride){$StoreOverride}else{Join-Path $out 'isolation-backups'}
    $priorPreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue' # deliberate child failures are test data
        & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $driver -Action $Action -GameDirectory $fixture -OutputDirectory $out -StoreDirectory $store -PayloadDirectory $PayloadDirectory -FixtureFailureAfterSwap $Fault *> "$OutputDirectory\$Run-$Action-$Fault.log"
        $script:driverExit=$LASTEXITCODE
    } finally { $ErrorActionPreference=$priorPreference }
}
function Assert-Original([string]$Label) {
    foreach ($relative in $expected.Keys) { Check ((Get-ComparisonHash (Join-Path $fixture $relative)) -ceq $expected[$relative]) ($Label+' '+$relative) }
}
Driver 'Preflight' 'preflight'; Check ($driverExit -eq 0) 'preflight'
foreach ($fault in 1..5) {
    Driver 'PrepareOnly' ('fault-'+$fault) $fault
    Check ($driverExit -ne 0) ('deliberate swap interruption '+$fault)
    Assert-Original ('rollback '+$fault)
    $r=Get-Content -LiteralPath "$OutputDirectory\fault-$fault\presentation-install-receipt.json" -Raw|ConvertFrom-Json
    Check ($r.phase -ceq 'Restored') ('rollback receipt '+$fault)
}
Driver 'PrepareOnly' 'main'; Check ($driverExit -eq 0) 'five-file prepare'
$r=Get-Content -LiteralPath "$OutputDirectory\main\presentation-install-receipt.json" -Raw|ConvertFrom-Json
Check ($r.phase -ceq 'Applied' -and $r.files.Count -eq 5 -and $r.schema -eq 3 -and $r.kind -ceq 'desktop-presentation-five-file-v3') 'applied receipt'
Check (@($r.files.backup|Select-Object -Unique).Count -eq 5) 'all targets have distinct backup paths including Core'
Check (@($r.baseline.PSObject.Properties).Count -eq 14) 'baseline includes both Core copies'
foreach ($entry in $r.files) {
    Check ((Get-ComparisonHash (Join-Path $fixture $entry.relative)) -ceq $entry.candidate) ('candidate applied '+$entry.name)
    Check ((Get-ComparisonHash $entry.backup) -ceq $entry.original) ('backup verified '+$entry.name)
}
$native=Get-Content -LiteralPath (Join-Path $r.storeDirectory ($r.isolationId+'\receipt.json')) -Raw|ConvertFrom-Json
foreach ($change in $native.Changes) {
    if ($null -eq $change.AppliedHash) { Check (-not (Test-Path -LiteralPath (Join-Path $fixture $change.Relative))) ('native absent '+$change.Relative) }
    else { Check ((Get-ComparisonHash (Join-Path $fixture $change.Relative)) -ceq $change.AppliedHash) ('test config '+$change.Relative) }
}
Driver 'PrepareOnly' 'main'; Check ($driverExit -ne 0) 'used/active test refused'
Driver 'Restore' 'main' 0 (Join-Path $OutputDirectory 'wrong-store'); Check ($driverExit -ne 0) 'wrong store refused'
# Alter only our private fixture. All candidates must remain untouched when
# any backup or installed binary conflicts with the receipt.
$conflict=Join-Path $PayloadDirectory 'Newtonsoft.Json.dll'
foreach ($file in $r.files) {
    Copy-Item -LiteralPath $conflict -Destination (Join-Path $fixture $file.relative) -Force
    Driver 'Restore' 'main'; Check ($driverExit -ne 0) ('intervening binary refused '+$file.name)
    foreach ($other in $r.files) {
        $hash=if($other.relative -ceq $file.relative){Get-ComparisonHash $conflict}else{$other.candidate}
        Check ((Get-ComparisonHash (Join-Path $fixture $other.relative)) -ceq $hash) ('no overwrite on '+$file.name+' conflict: '+$other.name)
    }
    Copy-Item -LiteralPath (Join-Path $PayloadDirectory $file.name) -Destination (Join-Path $fixture $file.relative) -Force
    Copy-Item -LiteralPath $conflict -Destination $file.backup -Force
    Driver 'Restore' 'main'; Check ($driverExit -ne 0) ('corrupt backup refused '+$file.name)
    foreach ($other in $r.files) { Check ((Get-ComparisonHash (Join-Path $fixture $other.relative)) -ceq $other.candidate) ('no overwrite on '+$file.name+' backup conflict: '+$other.name) }
    Copy-Item -LiteralPath (Join-Path $baseline.Root $file.relative) -Destination $file.backup -Force
}
# The new five-file driver must not reinterpret either older receipt version.
$receiptFile=Join-Path $OutputDirectory 'main\presentation-install-receipt.json'
foreach ($oldVersion in 1..2) {
    $r.kind=if($oldVersion -eq 1){'desktop-presentation-two-file-v1'}else{'desktop-presentation-three-file-v2'}; $r.schema=$oldVersion
    Write-CollectorJson $receiptFile $r
    Driver 'Restore' 'main'; Check ($driverExit -ne 0) ('old receipt version refused '+$oldVersion)
    foreach ($file in $r.files) { Check ((Get-ComparisonHash (Join-Path $fixture $file.relative)) -ceq $file.candidate) ('old receipt preserved target '+$file.relative) }
}
$r.kind='desktop-presentation-five-file-v3'; $r.schema=3
# Identical Core bytes are not permission to conflate their independent backups.
$coreBackup=$r.files[4].backup; $r.files[4].backup=$r.files[3].backup
Write-CollectorJson $receiptFile $r
Driver 'Restore' 'main'; Check ($driverExit -ne 0) 'aliased Core backup refused'
foreach ($file in $r.files) { Check ((Get-ComparisonHash (Join-Path $fixture $file.relative)) -ceq $file.candidate) ('alias refusal preserved '+$file.relative) }
$r.files[4].backup=$coreBackup
Write-CollectorJson $receiptFile $r
# Simulate an interrupted restoration after one binary was already restored.
$runtime=$r.files[0]
Copy-Item -LiteralPath $runtime.backup -Destination (Join-Path $fixture $runtime.relative) -Force
Driver 'Restore' 'main'; Check ($driverExit -eq 0) 'partial restore resumes'
Assert-Original 'full restore'
Driver 'Restore' 'main'; Check ($driverExit -eq 0) 'restore idempotent'
foreach ($relative in $expected.Keys) { Check ((Get-ComparisonHash (Join-Path $baseline.Root $relative)) -ceq $expected[$relative]) ('live unchanged '+$relative) }
Write-CollectorJson "$OutputDirectory\result.json" ([ordered]@{passed=$true;checks=@($checks);driverSha256=(Get-ComparisonHash $driver);commonSha256=(Get-ComparisonHash (Join-Path $PSScriptRoot 'Comparison.Common.ps1'));liveInstallationChanged=$false;gameLaunched=$false})
Write-Output ('PASS: '+$checks.Count+' five-file deployment/restore checks on private copies; live installation unchanged.')
