$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Comparison.Common.ps1')
$count=0
function Check([bool]$Condition,[string]$Name) { if (-not $Condition) { throw ('FAIL: '+$Name) }; $script:count++ }
function Record([string]$Role) { [pscustomobject]@{role=$Role;moduleSamples=4;moduleFailures=0;modules=@{'kernel32.dll'=$true};exitCode=0} }
$records=@((Record 'target'),(Record 'host'),(Record 'browser'),(Record 'renderer'))
Check (Test-ComparisonEvidence $records $true @()).qualified 'valid evidence'
Check (-not (Test-ComparisonEvidence $records $false @()).qualified) 'missing attachment'
Check (-not (Test-ComparisonEvidence $records $true @('fallback')).qualified) 'disqualifier'
foreach($role in @('target','host','browser','renderer')) { Check (-not (Test-ComparisonEvidence @($records|Where-Object role -ne $role) $true @()).qualified) ('missing '+$role) }
$records[0].modules['gameoverlayrenderer64.dll']=$true
Check (Test-ComparisonEvidence $records $true @()).qualified 'Steam in GTA alone does not invalidate external isolation'
$records[2].modules['gameoverlayrenderer64.dll']=$true
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'Steam in browser'
$records[2].modules.Remove('gameoverlayrenderer64.dll')
$records[1].modules['libcef.dll']=$true
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'CEF in host'
$records[1].modules.Remove('libcef.dll')
$records[0].modules['ragewebui.native.dll']=$true
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'native compositor in target'
$records[0].modules.Remove('ragewebui.native.dll')
$records[3].exitCode=$null
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'unobserved exit'
$records[3].exitCode=-1073741819
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'access violation'
$records[3].exitCode=0; $records[3].moduleSamples=0
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'missing module coverage'
$records[3].moduleSamples=4; $records[3].moduleFailures=1
Check (-not (Test-ComparisonEvidence $records $true @()).qualified) 'module read error'
$records[3].moduleFailures=0
$helper=Record 'desktop-probe'
$helper|Add-Member -NotePropertyName identityVerified -NotePropertyValue $true
$helper.moduleSamples=0
Check (Test-ComparisonEvidence ($records+@($helper)) $true @()).qualified 'authenticated short helper is not a second host or browser module-coverage claim'
$helper.exitCode=-1
Check (-not (Test-ComparisonEvidence ($records+@($helper)) $true @()).qualified) 'terminated helper is not silently a clean success'
$helper.exitCode=0; $helper.identityVerified=$false
Check (-not (Test-ComparisonEvidence ($records+@($helper)) $true @()).qualified) 'unverified helper rejected'
$helper.identityVerified=$true; $helper.modules['gameoverlayrenderer64.dll']=$true
Check (-not (Test-ComparisonEvidence ($records+@($helper)) $true @()).qualified) 'overlay in observed helper reported'
$exe='C:\exact payload\ReactorV.Preloader.exe'; $sha='a'*64
$created=[DateTime]::Parse('2026-09-10T09:15:00Z').ToUniversalTime()
$candidate=@{ProcessId=33;ParentProcessId=22;CreationDate=$created.AddSeconds(1);ExecutablePath=$exe;CommandLine=('"'+$exe+'" --desktop-presentation-probe e30=')}
$observed=@{pid=33;createdUtc=$created.AddSeconds(1);path=$exe;sha256=$sha}
$hostIdentity=@{pid=22;createdUtc=$created;alive=$true}
Check (Test-ComparisonProbeIdentity $candidate $observed $hostIdentity $exe $sha) 'exact direct helper authenticated'
foreach ($change in @(
    @{field='ParentProcessId';value=21}, @{field='ProcessId';value=22},
    @{field='CreationDate';value=$created.AddSeconds(2)}, @{field='ExecutablePath';value='C:\other\ReactorV.Preloader.exe'},
    @{field='CommandLine';value=('"'+$exe+'" --persistent-host')},
    @{field='CommandLine';value=('"'+$exe+'" --desktop-presentation-probe e30= --persistent-host')},
    @{field='CommandLine';value=('"C:\other\ReactorV.Preloader.exe" --desktop-presentation-probe e30=')},
    @{field='CommandLine';value=('"'+$exe+'" --desktop-presentation-probe aaaaa')}
)) {
    $bad=$candidate.Clone(); $bad[$change.field]=$change.value
    Check (-not (Test-ComparisonProbeIdentity $bad $observed $hostIdentity $exe $sha)) ('reject helper mismatch '+$change.field+' '+$change.value)
}
Check (-not (Test-ComparisonProbeIdentity $candidate $observed $hostIdentity $exe ('b'*64))) 'wrong executable hash rejected'
$hostIdentity.alive=$false
Check (-not (Test-ComparisonProbeIdentity $candidate $observed $hostIdentity $exe $sha)) 'dead parent rejected'
$hostIdentity.alive=$true; $hostIdentity.createdUtc=$created.AddSeconds(2)
Check (-not (Test-ComparisonProbeIdentity $candidate $observed $hostIdentity $exe $sha)) 'parent creation after child rejected'
$hostTrace='2026-09-09T00:00:00Z pid=22 stage=bootstrap_host_client_connected pid=11'
$targetTrace='2026-09-09T00:00:00Z pid=11 stage=bootstrap_host_attached pid=11 host_pid=22 generation=1'
Check (Test-ComparisonRoute $hostTrace $targetTrace 11 22).attached 'exact bidirectional handoff'
Check (-not (Test-ComparisonRoute $hostTrace $targetTrace 11 23).attached) 'wrong host rejected'
Check (-not (Test-ComparisonRoute $hostTrace $targetTrace 12 22).attached) 'wrong target rejected'
Check (-not (Test-ComparisonRoute '' $targetTrace 11 22).attached) 'host corroboration missing'
Check ((Test-ComparisonRoute $hostTrace ($targetTrace+"`n2026-09-09T00:00:00Z pid=11 stage=bootstrap_host_fallback reason=test") 11 22).failures -contains 'in-process-fallback') 'fallback rejected'
Check ((Test-ComparisonRoute $hostTrace ($targetTrace+"`n2026-09-09T00:00:00Z pid=12 stage=bootstrap_host_fallback reason=test") 11 22).failures.Count -eq 0) 'other session excluded'
Check ((Test-ComparisonRoute ($hostTrace+"`n2026-09-09T00:00:00Z pid=22 stage=webview_controller_deadline_elapsed test=1") $targetTrace 11 22).failures -contains 'external-host-startup-failed') 'startup timeout rejected'
Check (Test-ComparisonProfile '--user-data-dir="C:\a b\EBWebView"' 'C:\a b') 'exact EBWebView profile'
Check (-not (Test-ComparisonProfile '--user-data-dir="C:\a b2\EBWebView"' 'C:\a b')) 'prefix sibling rejected'
Check (-not (Test-ComparisonProfile '--user-data-dir="C:\a b"' 'C:\a b')) 'API root is not browser CLI root'
Check (-not (Test-ComparisonProfile '--user-data-dir="C:\a b\EBWebView" --user-data-dir=C:\other' 'C:\a b')) 'duplicate flags rejected'
foreach($file in @('Comparison.Common.ps1','Run-Comparison.ps1')) {
    $errors=$null; $tokens=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
    Check ($errors.Count -eq 0) ($file+' syntax')
}
Write-Output ('PASS: '+$count+' comparison checks. No process launched or installation changed.')
