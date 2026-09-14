param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use x64 Windows PowerShell 5.1.' }
if (-not [Environment]::Is64BitProcess) { throw 'Use x64 Windows PowerShell.' }
. (Join-Path $PSScriptRoot '..\ExternalHostComparison\Comparison.Common.ps1')
if (Get-Process -Name GTA5,GTA5_Enhanced,ReactorV.Preloader -ErrorAction SilentlyContinue) { throw 'Close game/host before this offline test.' }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh directory; results are never overwritten.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime=Join-Path $repo 'src\ReactorV.Preloader\bin\Release'
$fixture=Join-Path $OutputDirectory 'ProbeFixture.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 ("/out:"+$fixture) /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll ("/reference:"+(Join-Path $runtime 'Newtonsoft.Json.dll')) (Join-Path $PSScriptRoot 'ProbeFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $runtime 'Newtonsoft.Json.dll') -Destination $OutputDirectory
foreach ($name in @('success','quorum','malformed','exit','timeout','fallback','fallback-quorum','fallback-exit','fallback-malformed','fallback-real','startup-timeout','slow-startup','slow-gdi','ReactorV.Preloader')) { Copy-Item -LiteralPath $fixture -Destination (Join-Path $OutputDirectory ($name+'.exe')) }
$probe=Start-Process -FilePath $fixture -ArgumentList @(('"'+$runtime+'"'),('"'+$OutputDirectory+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$OutputDirectory\fixture-stdout.txt" -RedirectStandardError "$OutputDirectory\fixture-stderr.txt"
[void]$probe.Handle
try {
    if (-not $probe.WaitForExit(20000)) { throw 'Fixture did not finish within its test budget; process left untouched.' }
    if ($probe.ExitCode -ne 0) { throw ('Probe fixture failed: '+(Read-ComparisonText "$OutputDirectory\fixture-stderr.txt")) }
} finally { $probe.Dispose() }
$helperExe=Join-Path $OutputDirectory 'ReactorV.Preloader.exe'
foreach ($scenario in @('good','rogue')) {
    $tracker=New-ComparisonTracker "$OutputDirectory\collector-$scenario.jsonl"
    $tracker.expectedPreloaderPath=$helperExe; $tracker.expectedPreloaderHash=Get-ComparisonHash $helperExe
    $hostProcess=Start-Process -FilePath $helperExe -ArgumentList ('--collector-'+$scenario) -WindowStyle Hidden -PassThru
    try {
        Add-ComparisonProcess $tracker $hostProcess 'host' $PID
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do { Update-ComparisonTracker $tracker $hostProcess "$OutputDirectory\unused-profile"; Start-Sleep -Milliseconds 80 } while (-not $hostProcess.HasExited -and $clock.Elapsed.TotalSeconds -lt 8)
        Update-ComparisonTracker $tracker $hostProcess "$OutputDirectory\unused-profile"
        if (-not $hostProcess.HasExited -or $hostProcess.ExitCode -ne 0) { throw ('Fixture host did not exit cleanly: '+$scenario) }
        $helpers=@($tracker.records.Values|Where-Object role -eq 'desktop-probe')
        if ($scenario -eq 'good' -and ($helpers.Count -ne 1 -or $helpers[0].identityVerified -ne $true -or $helpers[0].exitCode -ne 0 -or $tracker.unexpectedPreloaders.Count -ne 0)) { throw 'Real helper classification failed.' }
        if ($scenario -eq 'rogue' -and ($helpers.Count -ne 0 -or $tracker.unexpectedPreloaders.Count -ne 1)) { throw 'Unexpected child role was not rejected.' }
    } finally {
        $tracker.writer.Dispose()
        foreach ($process in $tracker.processes.Values) { $process.Dispose() }
    }
}
Write-Output 'PASS: protocol/deadline tests, real desktop pixel checks, authenticated helper and rejected competing-role process. No installation changed.'
