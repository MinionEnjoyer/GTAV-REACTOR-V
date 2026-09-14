param(
    [Parameter(Mandatory=$true)][string]$SourcePreparation,
    [Parameter(Mandatory=$true)][string]$DeploymentResult,
    [Parameter(Mandatory=$true)][string]$HangQualificationDirectory,
    [Parameter(Mandatory=$true)][string]$SessionQualificationResult,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '..\CollectorReliability\CollectorHealth.ps1')
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
$OutputDirectory=Assert-CollectorOutput $OutputDirectory
$SourcePreparation=Assert-CollectorOutput $SourcePreparation
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh package directory.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$hangResult=Get-Content -LiteralPath (Join-Path $HangQualificationDirectory 'tests.json') -Raw|ConvertFrom-Json
$hangSources=Get-Content -LiteralPath (Join-Path $HangQualificationDirectory 'source-identity.json') -Raw|ConvertFrom-Json
if (-not $hangResult.passed -or $hangResult.checks.Count -lt 100 -or @($hangResult.checks|Where-Object {-not $_.passed}).Count) { throw 'Hang capture qualification is missing or failed.' }
foreach ($name in @('Watch-GameplayHang.ps1','GameplayHang.Common.ps1')) {
    $path=Join-Path $repo ('tools\GameplayHangCapture\'+$name)
    $entry=@($hangSources|Where-Object path -ieq $path)
    if ($entry.Count -ne 1 -or $entry[0].sha256 -cne (Get-ComparisonHash $path)) { throw 'Hang capture qualification does not match current source.' }
}
$sessionResult=Get-Content -LiteralPath $SessionQualificationResult -Raw|ConvertFrom-Json
if (-not $sessionResult.passed -or $sessionResult.checks.Count -lt 40 -or $sessionResult.gameLaunched -ne $false -or $sessionResult.installationChanged -ne $false -or $sessionResult.sessionSha256 -cne (Get-ComparisonHash (Join-Path $repo 'tools\GameplayHangCapture\GameplayHang.Session.ps1'))) { throw 'Observer session qualification mismatch.' }
$deployment=Get-Content -LiteralPath $DeploymentResult -Raw|ConvertFrom-Json
if (-not $deployment.passed -or $deployment.driverSha256 -cne (Get-ComparisonHash (Join-Path $PSScriptRoot 'Deploy-PresentationTest.ps1')) -or
    $deployment.commonSha256 -cne (Get-ComparisonHash (Join-Path $PSScriptRoot 'Comparison.Common.ps1'))) { throw 'Deployment rehearsal does not match current tools.' }
$source=Get-Content -LiteralPath "$SourcePreparation\prepared.json" -Raw|ConvertFrom-Json
if ($source.schema -ne 5 -or $source.root -ine $SourcePreparation) { throw 'Wrong source payload manifest (hang-observed schema 5 required).' }
foreach($entry in $source.tools) {
    if ((Get-ComparisonHash (Join-Path $repo $entry.path)) -cne $entry.sha256) { throw 'Source preparation tools changed; prepare a fresh source payload.' }
}
$payload=Join-Path $SourcePreparation 'fixture-payload'
$items=@(Get-ChildItem -LiteralPath $payload -Recurse -Force)
if (@($items|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -or
    @($items|Where-Object { -not $_.PSIsContainer }).Count -ne $source.payload.Count) { throw 'Source payload file set changed.' }
foreach ($entry in $source.payload) {
    $path=[IO.Path]::GetFullPath((Join-Path $payload $entry.path))
    if (-not $path.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-ComparisonHash $path) -cne $entry.sha256) { throw 'Source payload changed or escapes root.' }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
Copy-Item -LiteralPath $payload -Destination "$OutputDirectory\fixture-payload" -Recurse
$toolFiles=@('tools\ExternalHostComparison\Run-PresentationComparison.ps1','tools\ExternalHostComparison\Deploy-PresentationTest.ps1','tools\ExternalHostComparison\Comparison.Common.ps1','tools\ExternalHostComparison\ProviderFixture.cs','tools\CollectorReliability\CollectorHealth.ps1','tools\GameplayHangCapture\GameplayHang.Common.ps1','tools\GameplayHangCapture\GameplayHang.Session.ps1','tools\GameplayHangCapture\Watch-GameplayHang.ps1')
$tools=@(foreach ($relative in $toolFiles) {
    $target=Join-Path $OutputDirectory $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    [pscustomobject]@{path=$relative;sha256=(Get-ComparisonHash $target)}
})
Copy-Item -LiteralPath $DeploymentResult -Destination "$OutputDirectory\deployment-rehearsal.json"
Copy-Item -LiteralPath $SessionQualificationResult -Destination "$OutputDirectory\session-qualification.json"
Copy-Item -LiteralPath (Join-Path $HangQualificationDirectory 'tests.json') -Destination "$OutputDirectory\hang-qualification.json"
Copy-Item -LiteralPath (Join-Path $HangQualificationDirectory 'source-identity.json') -Destination "$OutputDirectory\hang-source-identity.json"
$userProfile = [Environment]::GetFolderPath('UserProfile')
$baseline=Join-Path $userProfile 'ReactorV-Diagnostics\isolation-backups\4f61325b43bb4854b74831f2551ca72c\receipt.json'
$baselineData=Get-Content -LiteralPath $baseline -Raw|ConvertFrom-Json
$baselineIdentity=[ordered]@{}
foreach ($entry in $baselineData.Identity.PSObject.Properties) { $baselineIdentity[$entry.Name]=$entry.Value }
$baselineIdentity['plugins/ReactorV/RageWebUI.Runtime.dll']='54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6'
foreach ($relative in @('plugins/ReactorV/RageWebUI.Core.dll','scripts/ReactorV/RageWebUI.Core.dll')) {
    $baselineIdentity[$relative]='674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862'
}
# A fresh baseline projection, not a rewrite of the historical isolation receipt.
Write-CollectorJson "$OutputDirectory\baseline.json" ([ordered]@{schema=1;kind='restored-presentation-baseline-v3';Root=$baselineData.Root;Status='Restored';Identity=$baselineIdentity;sourceReceipt=$baseline})
# A new seal, not a rewrite of the source preparation or any previous live result.
$evidence=@(foreach($name in @('deployment-rehearsal.json','session-qualification.json','hang-qualification.json','hang-source-identity.json')) {
    [pscustomobject]@{path=$name;sha256=(Get-ComparisonHash (Join-Path $OutputDirectory $name))}
})
Write-CollectorJson "$OutputDirectory\prepared.json" ([ordered]@{schema=5;root=$OutputDirectory;gameDirectory=$source.gameDirectory;createdUtc=[DateTime]::UtcNow.ToString('o');tools=$tools;payload=$source.payload;evidence=$evidence;baselineSha256=(Get-ComparisonHash "$OutputDirectory\baseline.json");sourcePreparation=$SourcePreparation;hangQualificationRoot=$HangQualificationDirectory;installationChanged=$false})
foreach ($entry in $source.payload) { if ((Get-ComparisonHash (Join-Path "$OutputDirectory\fixture-payload" $entry.path)) -cne $entry.sha256) { throw 'Copied package verification failed.' } }
Write-Output ('FROZEN PACKAGE READY FOR OFFLINE REHEARSAL: '+$OutputDirectory+'. Live installation unchanged.')
