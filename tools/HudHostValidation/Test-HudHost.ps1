param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name GTA5,GTA5_Enhanced,ReactorV.Preloader,HudHostValidation -ErrorAction SilentlyContinue) {
    throw 'Close the game, Reactor host and previous HUD fixtures before this offline test.'
}
$fixture = Join-Path $PSScriptRoot 'bin\Release\net48\HudHostValidation.exe'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh evidence directory.' }
if (-not (Test-Path -LiteralPath $fixture)) { throw 'Build HudHostValidation Release first.' }
$process = Start-Process -FilePath $fixture -ArgumentList ('"'+$OutputDirectory+'"') -WindowStyle Hidden -PassThru
try {
    [void]$process.Handle
    if (-not $process.WaitForExit(30000)) {
        # This exact child belongs to this run. Its pipe-server child receives
        # stdin EOF if the parent exits; never enumerate/kill unrelated hosts.
        $process.Kill()
        [void]$process.WaitForExit(3000)
        throw 'Owned HUD fixture exceeded its 30-second budget.'
    }
    $process.Refresh()
    $result = Join-Path $OutputDirectory 'results.txt'
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result) -or
        -not (Select-String -LiteralPath $result -Pattern '^RESULT PASS checks=64$' -Quiet)) {
        throw "HUD host regression failed. Inspect $result"
    }
    @('HudHostValidation.exe','RageWebUI.Runtime.dll','ReactorV.Preloader.exe','RageWebUI.Core.dll') |
        ForEach-Object { Get-FileHash -LiteralPath (Join-Path (Split-Path $fixture) $_) -Algorithm SHA256 } |
        Format-List Algorithm,Hash,Path |
        Out-File -LiteralPath (Join-Path $OutputDirectory 'binaries.txt') -Encoding utf8
    Write-Output 'PASS 64 native HUD/pipe/window-state/input-configuration checks. No GTA, desktop input or installation changes.'
} finally { $process.Dispose() }
