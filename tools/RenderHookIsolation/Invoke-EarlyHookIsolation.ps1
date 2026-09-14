param(
    [Parameter(Mandatory=$true)][ValidateSet('Preflight','Apply','Verify','Restore')][string]$Action,
    [Parameter(Mandatory=$true)][string]$RunDirectory
)
# One-variable local diagnostic. No game launch, runtime settings, injector
# replacement, forced process termination or changes to an older test package.
$ErrorActionPreference='Stop'
$gameRoot='D:\Programs\Steam\steamapps\common\Grand Theft Auto V Enhanced'
$dataRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) 'ReactorV-Diagnostics'
$RunDirectory=[IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
if (-not $RunDirectory.StartsWith($dataRoot+'\corefx-early-hook-isolation-',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use a fresh corefx-early-hook-isolation directory under ReactorV-Diagnostics.' }
$original=Join-Path $gameRoot 'ReactorV.RenderHook.asi'
$disabled=Join-Path $gameRoot 'ReactorV.RenderHook.asi.corefx-isolation-disabled'
$backup=Join-Path $RunDirectory 'ReactorV.RenderHook.asi.original'
$receiptPath=Join-Path $RunDirectory 'receipt.json'
$expectedHook='2d4d77090555fe285b8b87b8a0a8913560ed21a5b21a81e969b96186a3d3e173'
function Assert-SafePath([string]$Path) {
    $node=[IO.Path]::GetFullPath($Path)
    while ($node) {
        if ((Test-Path -LiteralPath $node) -and ([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse point is not allowed: '+$node) }
        $node=[IO.Path]::GetDirectoryName($node)
    }
}
function Require-Stopped {
    if (Get-Process GTA5,GTA5_Enhanced,PlayGTAV,ReactorV.Preloader,ProviderFixture,ProbeFixture,BrowserPresentationValidation,GameplayHangFixture,procdump,procdump64,procdump64a -ErrorAction SilentlyContinue) { throw 'Close GTA, its launch stub, Reactor and diagnostic fixtures/collectors first. No process will be terminated.' }
}
function Hash([string]$Path) {
    Assert-SafePath $Path
    if (-not [IO.File]::Exists($Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Save-Receipt($Record) {
    Assert-SafePath $receiptPath
    $temp=$receiptPath+'.new-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temp,($Record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($receiptPath)) { [IO.File]::Replace($temp,$receiptPath,[Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp,$receiptPath) }
}
function Read-Receipt {
    Assert-SafePath $receiptPath
    if ((Get-Item -LiteralPath $receiptPath).Length -gt 1MB) { throw 'Oversized receipt.' }
    $record=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    if ($record.schema -ne 1 -or $record.kind -cne 'corefx-early-renderhook-only' -or $record.gameRoot -ine $gameRoot -or
        $record.runDirectory -ine $RunDirectory -or $record.hookSha256 -cne $expectedHook -or $record.phase -notin @('Prepared','Applied','Restored')) { throw 'Receipt identity mismatch.' }
    return $record
}
function Protected-Changes($Record) {
    foreach($entry in $Record.protected) {
        $path=[IO.Path]::GetFullPath((Join-Path $gameRoot $entry.relative))
        if (-not $path.StartsWith($gameRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Protected path escapes game root.' }
        if ((Hash $path) -cne $entry.sha256) { $entry.relative }
    }
}
foreach($path in @($gameRoot,$RunDirectory,$original,$disabled,$backup)) { Assert-SafePath $path }
if ($Action -in @('Preflight','Apply')) {
    Require-Stopped
    if (Test-Path -LiteralPath $RunDirectory) { throw 'Use a fresh run directory; do not reuse a consumed control.' }
    if ((Hash $original) -cne $expectedHook -or (Test-Path -LiteralPath $disabled)) { throw 'Render hook identity/layout is not the expected baseline.' }
    $paths=@('GTA5_Enhanced.exe','ReactorV.Bootstrap.asi','ReactorV.ScriptProbe.asi',
        'plugins/ReactorV/RageWebUI.Native.dll','plugins/ReactorV/RageWebUI.Runtime.dll','plugins/ReactorV/ReactorV.Preloader.exe','plugins/ReactorV/RageWebUI.Core.dll',
        'scripts/ReactorV/RageWebUI.Script.dll','scripts/ReactorV/RageWebUI.Core.dll','scripts/ReactorV/ReactorV.json','scripts/ALLIN1.dll',
        'dxgi.dll','renodxshaderloader.addon64','reshade.ini','ReShade2.ini','ReShadePreset.ini')
    foreach($folder in @('CustomShaders','reshade-shaders')) {
        $root=Join-Path $gameRoot $folder
        if (Test-Path -LiteralPath $root) {
            Assert-SafePath $root
            $items=@(Get-ChildItem -LiteralPath $root -Recurse -Force)
            if (@($items|Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Reparse point in shader tree.' }
            $paths+=@($items|Where-Object { -not $_.PSIsContainer }|ForEach-Object { $_.FullName.Substring($gameRoot.Length+1) })
        }
    }
    $protected=@($paths|Sort-Object -Unique|ForEach-Object { [pscustomobject]@{relative=$_;sha256=(Hash (Join-Path $gameRoot $_))} })
    if ($Action -eq 'Preflight') { Write-Output ('PASS preflight: one hook, '+$protected.Count+' protected file paths; no changes.'); return }
    [void][IO.Directory]::CreateDirectory($RunDirectory)
    Copy-Item -LiteralPath $original -Destination $backup
    if ((Hash $backup) -cne $expectedHook) { throw 'Backup verification failed.' }
    # Preserve preceding sessions before a new game overwrites/reuses its logs.
    $evidence=Join-Path $RunDirectory 'before'; [void][IO.Directory]::CreateDirectory($evidence)
    foreach($relative in @('ReShade.log','asiloader.log','scripts/ReactorV/ReactorV.RenderHook.log','scripts/ReactorV/ReactorV.NativeLifecycle.log')) {
        $path=Join-Path $gameRoot $relative; Assert-SafePath $path
        if ([IO.File]::Exists($path)) { Copy-Item -LiteralPath $path -Destination (Join-Path $evidence ($relative.Replace('/','_'))) }
    }
    $record=[ordered]@{schema=1;kind='corefx-early-renderhook-only';phase='Prepared';gameRoot=$gameRoot;runDirectory=$RunDirectory;hookSha256=$expectedHook;createdUtc=[DateTime]::UtcNow.ToString('o');protected=$protected;gameLaunched=$false}
    Save-Receipt $record
    Require-Stopped
    if ((Hash $original) -cne $expectedHook -or (Test-Path -LiteralPath $disabled) -or @(Protected-Changes $record).Count) { throw 'Files changed during staging; no hook renamed.' }
    # Both fully resolved targets are single files in the verified GTA root.
    Move-Item -LiteralPath $original -Destination $disabled
    if ((Hash $disabled) -cne $expectedHook -or (Test-Path -LiteralPath $original)) { throw 'Disabled layout verification failed; preserve receipt and backup.' }
    $record.phase='Applied'; Save-Receipt $record
} else { $record=Read-Receipt }
if ($Action -eq 'Restore') {
    Require-Stopped
    if ($record.phase -eq 'Restored') {
        if ((Hash $original) -cne $expectedHook -or (Test-Path -LiteralPath $disabled)) { throw 'Restored layout changed; refusing overwrite.' }
    } else {
        if ((Hash $backup) -cne $expectedHook -or (Hash $disabled) -cne $expectedHook -or (Test-Path -LiteralPath $original)) { throw 'Restore conflict; no file overwritten.' }
        Move-Item -LiteralPath $disabled -Destination $original
        if ((Hash $original) -cne $expectedHook) { throw 'Restore hash verification failed.' }
        $record.phase='Restored'; Save-Receipt $record
    }
}
$changes=@(Protected-Changes $record)
$layout=if($record.phase -eq 'Restored') { (Hash $original) -ceq $expectedHook -and -not (Test-Path -LiteralPath $disabled) } else { (Hash $disabled) -ceq $expectedHook -and -not (Test-Path -LiteralPath $original) }
[pscustomobject]@{phase=$record.phase;hookLayoutMatches=$layout;protectedCount=$record.protected.Count;protectedChanged=$changes;receipt=$receiptPath;gameLaunched=$false}|ConvertTo-Json -Depth 4
if (-not $layout -or $changes.Count) { throw 'Control differs from its recorded state. Unrelated files were left untouched.' }
