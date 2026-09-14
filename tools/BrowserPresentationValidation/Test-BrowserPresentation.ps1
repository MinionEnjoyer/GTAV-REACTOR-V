param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(1,10)][int]$Runs = 2,
    [switch]$FlipBackdrop
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name GTA5,GTA5_Enhanced,ReactorV.Preloader,BrowserPresentationValidation -ErrorAction SilentlyContinue) {
    throw 'Close the game, Reactor host and other browser fixtures before this offline test.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime = Join-Path $repo 'src\ReactorV.Preloader\bin\Release'
$fixture = Join-Path $PSScriptRoot 'bin\Release\net48\BrowserPresentationValidation.exe'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh results directory.' }
if (-not (Test-Path -LiteralPath $fixture)) { throw 'Build the browser fixture first.' }
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'ReactorV.Preloader.exe'))) { throw 'Build Preloader Release first.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
# Pin exactly what was tested, without copying installed binaries or game data.
$hashes = @('RageWebUI.Runtime.dll','ReactorV.Preloader.exe','RageWebUI.Core.dll') |
    ForEach-Object { Get-FileHash -LiteralPath (Join-Path $runtime $_) -Algorithm SHA256 }
$hashes | Format-List Algorithm,Hash,Path | Out-File -LiteralPath (Join-Path $OutputDirectory 'binaries.txt') -Encoding utf8
Get-FileHash -LiteralPath $fixture,(Join-Path $PSScriptRoot 'Program.cs'),(Join-Path $PSScriptRoot 'FlipBackdrop.cs'),$PSCommandPath |
    Format-List Algorithm,Hash,Path | Out-File -LiteralPath (Join-Path $OutputDirectory 'fixture.txt') -Encoding utf8
$expectedChecks=84
if ($FlipBackdrop) { $expectedChecks=97 }
for ($run = 1; $run -le $Runs; $run++) {
    foreach ($mode in @('software','gpu')) {
        if ($FlipBackdrop) { $mode='flip-'+$mode }
        $result = Join-Path $OutputDirectory ("$mode-$run")
        $process = Start-Process -FilePath $fixture -ArgumentList ('"'+$runtime+'" "'+$result+'" '+$mode) -WindowStyle Hidden -PassThru
        [void]$process.Handle
        try {
            if (-not $process.WaitForExit(45000)) {
                # This Process object/handle belongs to the child we created;
                # never search by name and kill unrelated hosts or browsers.
                $process.Kill()
                [void]$process.WaitForExit(5000)
                throw "Owned fixture exceeded its 45-second budget: $result"
            }
            if ($process.ExitCode -ne 0) { throw "Browser presentation regression failed: $result\results.txt" }
            $text = Get-Content -LiteralPath (Join-Path $result 'results.txt') -Raw
            if ($text -notmatch ('(?m)^COMPLETE checks='+$expectedChecks+' installation_changed=False game_launched=False input_ownership_tested=False\s*$')) {
                throw "Fixture exited without completing every assertion: $result"
            }
            Write-Output "PASS $mode run $run : $expectedChecks checks"
        } finally { $process.Dispose() }
    }
}
Write-Output 'PASS browser presentation regression. No game launched or installation changed.'
