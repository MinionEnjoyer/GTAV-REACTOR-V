param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Use x64 Windows PowerShell 5.1.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh output directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime = Join-Path $repo 'src\ReactorV.Preloader\bin\Release'
$fixture = Join-Path $OutputDirectory 'PaintFixture.exe'
& "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 ("/out:"+$fixture) /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'PaintFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
& $fixture $runtime | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Compiled PNG validation failed.' }
