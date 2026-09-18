param(
    [ValidateSet('Preflight','PrepareOnly','Restore')][string]$Action='Preflight',
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$StoreDirectory,
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [ValidateRange(0,5)][int]$FixtureFailureAfterSwap=0
)
# Fixed-build five-file v3 transaction, including both consumed Core copies.
# Older sealed packages/drivers stay untouched; their receipts are rejected here.
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) { throw 'Use x64 Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
$helperDirectory=Join-Path $PayloadDirectory 'isolation-helper'
$helper=Join-Path $helperDirectory 'ReactorV.Issue1Isolation.exe'
$json=Join-Path $helperDirectory 'Newtonsoft.Json.dll'
if ((Get-ComparisonHash $helper) -cne '642e8b67ccf014ab8ec10b4486cdfc26173df97f63a3785057840ce9634d9083' -or
    (Get-ComparisonHash $json) -cne 'f0c07af0e84d4dd4da4bd7823ba4535bc0481b3bf623ed40b659b68147a6bb75') { throw 'Wrong isolation helper/dependency.' }
[void][Reflection.Assembly]::LoadFrom($json)
$assembly=[Reflection.Assembly]::LoadFrom($helper)
$core=$assembly.GetType('ReactorV.Issue1Isolation.Isolation',$true)
$capture=$assembly.GetType('ReactorV.Issue1Isolation.SessionCapture',$true)
$stopped=[Delegate]::CreateDelegate([Action],$capture.GetMethod('RequireStopped'))
function Require-Stopped {
    $capture.GetMethod('RequireStopped').Invoke($null,$null)
    if (Get-Process -Name ProviderFixture,ProbeFixture,GameplayHangFixture,procdump,procdump64,procdump64a -ErrorAction SilentlyContinue) { throw 'Close the offline fixture and diagnostic collector first.' }
}
function Assert-Safe([string]$Path) { $core.GetMethod('SafePath').Invoke($null,[object[]]@($Path)) }
foreach ($name in @('GameDirectory','OutputDirectory','StoreDirectory','PayloadDirectory')) {
    $path=[IO.Path]::GetFullPath((Get-Variable -Name $name -ValueOnly)).TrimEnd('\')
    Set-Variable -Name $name -Value $path
    Assert-Safe $path
}
foreach ($external in @($OutputDirectory,$StoreDirectory,$PayloadDirectory)) {
    if ($external -ieq $GameDirectory -or $external.StartsWith($GameDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $GameDirectory.StartsWith($external+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Payload, receipts and backups must be outside the game tree.' }
}
if ($FixtureFailureAfterSwap -gt 0 -and ($GameDirectory -notmatch '\\presentation-deployment-rehearsal-[^\\]+\\fixture-game$')) { throw 'Fault injection is allowed only in the dedicated private fixture tree.' }
$files=@(
    [ordered]@{relative='plugins/ReactorV/RageWebUI.Runtime.dll';name='RageWebUI.Runtime.dll';original='54833ddd212545e9fce7c773dba96c00e2c683bbb2a06b1d2e2c51e78d8db1a6';candidate='061c490c8d65bb06b5ee77c8d2dcd7e2e982349ea2df5063d41bf2cfc91db07e'},
    [ordered]@{relative='plugins/ReactorV/ReactorV.Preloader.exe';name='ReactorV.Preloader.exe';original='77d05d87ad12a391911935ad84e43b0c8e996b607291adf2f335a9bafa163db1';candidate='5d08fec9024e5e0bb81f0cbeb2b9ed1c3c9fa6c981f4fc0c42ff74d224704b10'},
    [ordered]@{relative='scripts/ReactorV/RageWebUI.Script.dll';name='RageWebUI.Script.dll';original='a9df990bf43700758b1905568159ffd5b6ac592b70fb6027f08c28a05cedac0f';candidate='ba7762220f7ad548181325b915175abfd953e1a4d6a4fbc9641a18ceb5c76db5'},
    [ordered]@{relative='plugins/ReactorV/RageWebUI.Core.dll';name='RageWebUI.Core.dll';original='674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862';candidate='708c4b28d53e7a2a68808265409d55dd2114d572789d9fec3ef621fe777d6279'},
    [ordered]@{relative='scripts/ReactorV/RageWebUI.Core.dll';name='RageWebUI.Core.dll';original='674bf7eb379fab6e95f49b724eb8010bf98aa0a909e8f32596b84ca4ffce6862';candidate='708c4b28d53e7a2a68808265409d55dd2114d572789d9fec3ef621fe777d6279'}
)
$expected=[Collections.Generic.Dictionary[string,string]]::new()
$originalExpected=[Collections.Generic.Dictionary[string,string]]::new()
foreach ($entry in $core.GetField('Candidate').GetValue($null).GetEnumerator()) { $expected.Add($entry.Key,$entry.Value); $originalExpected.Add($entry.Key,$entry.Value) }
foreach ($file in $files) { $expected[$file.relative]=$file.candidate; $originalExpected[$file.relative]=$file.original }
$isolation=$core.GetConstructors()[0].Invoke([object[]]@($GameDirectory,$StoreDirectory,$stopped,$expected.PSObject.BaseObject,$null))
$baseline=$core.GetConstructors()[0].Invoke([object[]]@($GameDirectory,$StoreDirectory,$stopped,$originalExpected.PSObject.BaseObject,$null))
function Backup-Path($File) { Join-Path $OutputDirectory ($File.relative.Replace('/','_')+'.original') }
function Assert-CoreLocations {
    $found=@(Get-ChildItem -LiteralPath (Join-Path $GameDirectory 'plugins'),(Join-Path $GameDirectory 'scripts') -Recurse -File -Filter 'RageWebUI.Core.dll')
    $allowed=@($files|Where-Object name -eq 'RageWebUI.Core.dll'|ForEach-Object { [IO.Path]::GetFullPath((Join-Path $GameDirectory $_.relative)) })
    if ($found.Count -ne 2 -or @($found|Where-Object { $_.FullName -notin $allowed }).Count) { throw 'Unexpected Core DLL layout.' }
}
$receiptPath=Join-Path $OutputDirectory 'presentation-install-receipt.json'
function Save-Receipt($Receipt) {
    Assert-Safe $receiptPath
    $temp=$receiptPath+'.new-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temp,($Receipt|ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($receiptPath)) { [IO.File]::Replace($temp,$receiptPath,[Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp,$receiptPath) }
}
function Swap-File($File,[string]$Source,[string]$SourceHash,[string]$CurrentHash) {
    Require-Stopped
    $target=Join-Path $GameDirectory $File.relative
    Assert-Safe $Source; Assert-Safe $target
    if ((Get-ComparisonHash $Source) -cne $SourceHash -or (Get-ComparisonHash $target) -cne $CurrentHash) { throw ('File identity conflict: '+$File.relative) }
    $temp=$target+'.presentation-stage-'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::Copy($Source,$temp,$false)
    if ((Get-ComparisonHash $temp) -cne $SourceHash) { throw 'Staged file hash mismatch.' }
    Require-Stopped
    if ((Get-ComparisonHash $target) -cne $CurrentHash) { throw 'Target changed while staging; refusing overwrite.' }
    [IO.File]::Replace($temp,$target,[Management.Automation.Language.NullString]::Value)
    if ((Get-ComparisonHash $target) -cne $SourceHash) { throw 'Replacement verification failed.' }
}
function Restore-Presentation {
    Require-Stopped
    Assert-Safe $receiptPath
    $r=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    if ($r.schema -ne 3 -or $r.kind -cne 'desktop-presentation-five-file-v3' -or $r.gameDirectory -ine $GameDirectory -or
        $r.outputDirectory -ine $OutputDirectory -or $r.storeDirectory -ine $StoreDirectory -or $r.files.Count -ne 5 -or
        $r.phase -notin @('BackedUp','Applying','Applied','Restoring','Restored')) { throw 'Receipt identity mismatch.' }
    # Validate ALL FIVE backups and targets before any native or binary restore.
    for ($i=0;$i -lt $files.Count;$i++) {
        $file=$files[$i]; $entry=$r.files[$i]; $backup=Backup-Path $file
        if ($entry.relative -cne $file.relative -or $entry.original -cne $file.original -or $entry.candidate -cne $file.candidate -or $entry.backup -ine $backup) { throw 'Unexpected receipt file entry.' }
        Assert-Safe $backup; Assert-Safe (Join-Path $GameDirectory $file.relative)
        if ((Get-ComparisonHash $backup) -cne $file.original) { throw ('Backup conflict: '+$file.name) }
        if ((Get-ComparisonHash (Join-Path $GameDirectory $file.relative)) -cnotin @($file.original,$file.candidate)) { throw ('Intervening file preserved: '+$file.name) }
    }
    $current=$core.GetMethod('Current').Invoke($isolation,$null)
    if ($r.isolationId) {
        if (-not $current -or $current.Id -cne $r.isolationId) { throw 'Isolation receipt identity changed.' }
    } elseif ($current -and $current.Id -cne $r.priorIsolationId) {
        # Recover the narrow interval after helper Prepare but before receipt save.
        foreach ($file in $files) { if ($current.Identity[$file.relative] -cne $file.candidate) { throw 'Unrelated isolation receipt.' } }
        $r.isolationId=$current.Id
    } elseif ($current -and $current.Status -cne 'Restored') { throw 'Unrelated active isolation receipt.' }
    $r.phase='Restoring'; Save-Receipt $r
    if ($current -and $current.Status -cne 'Restored') { $core.GetMethod('Restore').Invoke($isolation,$null) }
    foreach ($file in $files) {
        if ((Get-ComparisonHash (Join-Path $GameDirectory $file.relative)) -ceq $file.candidate) {
            Swap-File $file (Backup-Path $file) $file.original $file.candidate
        }
    }
    foreach ($entry in $r.baseline.PSObject.Properties) {
        if ((Get-ComparisonHash (Join-Path $GameDirectory $entry.Name)) -cne $entry.Value) { throw ('Baseline not restored: '+$entry.Name) }
    }
    $r.phase='Restored'; $r.restoredUtc=[DateTime]::UtcNow.ToString('o'); Save-Receipt $r
    Write-Output 'RESTORED: all five original binaries and native/config baseline verified. Backups retained.'
}
if ($Action -eq 'Restore') { Restore-Presentation; exit 0 }
Require-Stopped
Assert-CoreLocations
foreach ($file in $files) {
    $source=Join-Path $PayloadDirectory $file.name; Assert-Safe $source
    if ((Get-ComparisonHash $source) -cne $file.candidate) { throw ('Candidate hash mismatch: '+$file.name) }
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $GameDirectory 'GTA5_Enhanced.exe')).FileVersion -ne '1.0.1158.16') { throw 'Wrong game version.' }
$identity=$core.GetMethod('Preflight').Invoke($baseline,$null)
$identity['scripts/ALLIN1.dll']=Get-ComparisonHash (Join-Path $GameDirectory 'scripts/ALLIN1.dll')
if ($Action -eq 'Preflight') { Write-Output 'PREFLIGHT PASSED: baseline and all five exact candidate targets verified; no files changed.'; exit 0 }
if (Test-Path -LiteralPath $receiptPath) { throw 'This test output was already used; choose a new directory.' }
foreach ($file in $files) { if (Test-Path -LiteralPath (Backup-Path $file)) { throw 'Existing backup; refusing overwrite.' } }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$prior=$core.GetMethod('Current').Invoke($baseline,$null)
$entries=@()
foreach ($file in $files) {
    $entry=[ordered]@{relative=$file.relative;name=$file.name;original=$file.original;candidate=$file.candidate;backup=(Backup-Path $file)}
    [IO.File]::Copy((Join-Path $GameDirectory $file.relative),$entry.backup,$false)
    if ((Get-ComparisonHash $entry.backup) -cne $file.original) { throw 'Original backup hash mismatch.' }
    $entries+=@($entry)
}
$receipt=[ordered]@{schema=3;kind='desktop-presentation-five-file-v3';phase='BackedUp';gameDirectory=$GameDirectory;outputDirectory=$OutputDirectory;storeDirectory=$StoreDirectory;files=$entries;baseline=$identity;priorIsolationId=$(if($prior){$prior.Id}else{$null});isolationId=$null;preparedUtc=[DateTime]::UtcNow.ToString('o');restoredUtc=$null}
Save-Receipt $receipt
try {
    $receipt.phase='Applying'; Save-Receipt $receipt
    $swaps=0
    foreach ($file in $files) {
        Swap-File $file (Join-Path $PayloadDirectory $file.name) $file.candidate $file.original
        $swaps++
        if ($FixtureFailureAfterSwap -eq $swaps) { throw 'Deliberate fixture-only interrupted five-file deployment.' }
    }
    $native=$core.GetMethod('Prepare').Invoke($isolation,[object[]]@('native-off-windowed'))
    $receipt.isolationId=$native.Id; Save-Receipt $receipt
    $core.GetMethod('Verify').Invoke($isolation,[object[]]@($native.PSObject.BaseObject))
    $receipt.phase='Applied'; Save-Receipt $receipt
    Write-Output 'PREPARED: five-file desktop-presentation candidate, native off/windowed; no game launched.'
} catch {
    $failure=$_
    try { Restore-Presentation } catch { Write-Warning ('Rollback needs attention: '+$_.Exception.Message+'. Keep this receipt and all five backups.') }
    throw $failure
}
