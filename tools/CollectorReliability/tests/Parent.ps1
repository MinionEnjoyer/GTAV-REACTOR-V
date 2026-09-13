param([Parameter(Mandatory=$true)][string]$OutputDirectory, [switch]$DenyBreakaway)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\CollectorHealth.ps1')
Add-Type -Path (Join-Path $PSScriptRoot '..\ProcessLifetime.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'FixtureJob.cs')
$job = [ReactorV.Diagnostics.Tests.FixtureJob]::new((-not [bool]$DenyBreakaway))
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runId = [Guid]::NewGuid()
$directory = Assert-CollectorOutput $OutputDirectory
[void][IO.Directory]::CreateDirectory($directory)
$args = @('-NoProfile','-NonInteractive','-ExecutionPolicy','RemoteSigned','-File',(Join-Path $PSScriptRoot 'Fixture.ps1'),
    '-OutputDirectory',(Join-Path $directory 'independent'),'-RunId',$runId.ToString('N'),'-Seconds','30')
if ($DenyBreakaway) {
    try { $child = [ReactorV.Diagnostics.ProcessLifetime]::StartDetached($shell, $args, $PSScriptRoot); throw 'Unexpected launch permitted.' }
    catch {
        if ($_.Exception.ToString() -notmatch 'does not permit independent collectors') { throw }
        Write-CollectorJson (Join-Path $directory 'parent.json') @{blocked=$true;parentPid=$PID;error=$_.Exception.Message}
    }
    exit 0
}
$child = [ReactorV.Diagnostics.ProcessLifetime]::StartDetached($shell, $args, $PSScriptRoot)
$ordinary = Start-Process -FilePath $shell -ArgumentList '-NoProfile -NonInteractive -Command "[System.Threading.Thread]::Sleep(30000)"' -WindowStyle Hidden -PassThru
[void]$ordinary.Handle
Write-CollectorJson (Join-Path $directory 'parent.json') @{
    parentPid=$PID;runId=$runId.ToString('N');independentPid=$child.Id;independentStartTicks=$child.StartTime.ToUniversalTime().Ticks;
    ordinaryPid=$ordinary.Id;ordinaryStartTicks=$ordinary.StartTime.ToUniversalTime().Ticks;
    independentInJob=[ReactorV.Diagnostics.ProcessLifetime]::InJob($child);ordinaryInJob=[ReactorV.Diagnostics.ProcessLifetime]::InJob($ordinary)
}
# Close the owning job by exiting this disposable parent. The inherited child is
# deliberately terminated by Windows; the independent fixture must survive.
$child.Dispose(); $ordinary.Dispose()
exit 0
