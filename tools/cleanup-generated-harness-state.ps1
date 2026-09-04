[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$IncludeAcceptance,
    [switch]$IncludeLegacyFixtures,
    [switch]$IncludePackageStaging,
    [switch]$PruneSessionLogs
)

$reactorLocalRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'ReactorV'))
$ownedPrefix = $reactorLocalRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

$targets = @(
    (Join-Path $reactorLocalRoot 'Harness')
)
if ($IncludeAcceptance) {
    $targets += Join-Path $reactorLocalRoot 'Acceptance'
}
if ($IncludeLegacyFixtures) {
    $targets += Get-ChildItem `
        -LiteralPath $reactorLocalRoot `
        -Directory `
        -Filter 'LegacyCache-*' `
        -ErrorAction SilentlyContinue |
        ForEach-Object FullName
}

foreach ($target in $targets) {
    $fullTarget = [IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith(
            $ownedPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside ReactorV LocalAppData: $fullTarget"
    }
    if (-not (Test-Path -LiteralPath $fullTarget)) {
        continue
    }
    if ($PSCmdlet.ShouldProcess($fullTarget, 'Remove generated ReactorV test state')) {
        Remove-Item -LiteralPath $fullTarget -Recurse -Force -ErrorAction Stop
        Write-Output "Removed generated test state: $fullTarget"
    }
}

if ($IncludePackageStaging) {
    $repositoryRoot = [IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
    $packageStaging = [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot '.work'))
    if ([IO.Path]::GetDirectoryName($packageStaging) -ne $repositoryRoot -or
        [IO.Path]::GetFileName($packageStaging) -ne '.work') {
        throw "Refusing unsafe package-staging cleanup: $packageStaging"
    }
    if ((Test-Path -LiteralPath $packageStaging) -and
        $PSCmdlet.ShouldProcess(
            $packageStaging,
            'Remove generated ReactorV package staging')) {
        Remove-Item `
            -LiteralPath $packageStaging `
            -Recurse `
            -Force `
            -ErrorAction Stop
        Write-Output "Removed generated package staging: $packageStaging"
    }
}

if ($PruneSessionLogs -and (Test-Path -LiteralPath $reactorLocalRoot)) {
    $activeCutoff = [DateTime]::UtcNow.AddMinutes(-15)
    $sessionLogs = @(
        Get-ChildItem `
            -LiteralPath $reactorLocalRoot `
            -File `
            -Filter 'reactorv-session-*.log' `
            -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending
    )
    foreach ($sessionLog in ($sessionLogs | Select-Object -Skip 48)) {
        $fullLog = [IO.Path]::GetFullPath($sessionLog.FullName)
        if (-not $fullLog.StartsWith(
                $ownedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a log outside ReactorV LocalAppData: $fullLog"
        }
        if ($sessionLog.LastWriteTimeUtc -ge $activeCutoff) {
            continue
        }
        if ($PSCmdlet.ShouldProcess($fullLog, 'Remove old ReactorV session log')) {
            Remove-Item -LiteralPath $fullLog -Force -ErrorAction Stop
        }
    }
    Write-Output (
        'Retained the newest {0} ReactorV session logs.' -f `
            [Math]::Min(48, $sessionLogs.Count))
}
