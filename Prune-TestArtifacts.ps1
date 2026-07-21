[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Root = (Join-Path $PSScriptRoot 'artifacts'),
    [double] $OlderThanHours = 24,
    [switch] $RemoveWorkDirectories
)

$resolvedRoot = (Resolve-Path -LiteralPath $Root -ErrorAction Stop).Path.TrimEnd('\')
$cutoff = if ($OlderThanHours -le 0) { [datetime]::MaxValue } else { (Get-Date).AddHours(-$OlderThanHours) }
$patterns = @(
    '*.lbdx',
    '*.lbdx-wal',
    '*.lbdx-shm',
    '*.sqlite',
    '*.sqlite-wal',
    '*.sqlite-shm',
    '*.db-wal',
    '*.db-shm'
)

$files = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File -ErrorAction Stop | Where-Object {
    $file = $_
    ($OlderThanHours -le 0 -or $file.LastWriteTime -lt $cutoff) -and
        ($patterns | Where-Object { $file.Name -like $_ } | Select-Object -First 1)
})

$outside = @($files | Where-Object {
    -not $_.FullName.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)
})
if ($outside.Count -ne 0)
{
    throw "Refusing cleanup because $($outside.Count) files resolve outside '$resolvedRoot'."
}

[long] $bytes = ($files | Measure-Object Length -Sum).Sum
[int] $removed = 0
foreach ($file in $files)
{
    if ($PSCmdlet.ShouldProcess($file.FullName, 'Delete generated test payload'))
    {
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        $removed++
    }
}

[int] $removedWorkDirectories = 0
[int] $matchedWorkDirectories = 0
if ($RemoveWorkDirectories)
{
    $activeTests = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like 'LibraDex.ShapeBench*' -or
            $_.Name -like 'LibraDex.Harness*' -or
            ($_.Name -like 'dotnet*' -and $_.CommandLine -and $_.CommandLine -match 'LibraDex(\.ShapeBench|\.Harness)')
    })
    if ($activeTests.Count -ne 0)
    {
        throw "Refusing work-directory cleanup while $($activeTests.Count) LibraDex test processes are active."
    }

    $workDirectories = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Directory -Filter 'work' -ErrorAction SilentlyContinue | Where-Object {
        $OlderThanHours -le 0 -or $_.LastWriteTime -lt $cutoff
    })
    $outsideWork = @($workDirectories | Where-Object {
        -not $_.FullName.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($outsideWork.Count -ne 0)
    {
        throw "Refusing cleanup because $($outsideWork.Count) work directories resolve outside '$resolvedRoot'."
    }

    $matchedWorkDirectories = $workDirectories.Count
    $bytes += ($workDirectories | ForEach-Object {
        (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    } | Measure-Object -Sum).Sum
    foreach ($directory in $workDirectories)
    {
        if ($PSCmdlet.ShouldProcess($directory.FullName, 'Delete completed or superseded test work directory'))
        {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction Stop
            $removedWorkDirectories++
        }
    }
}

if (-not $WhatIfPreference)
{
    $directories = @(Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending)
    foreach ($directory in $directories)
    {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue | Select-Object -First 1))
        {
            Remove-Item -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue
        }
    }
}

[pscustomobject]@{
    Root = $resolvedRoot
    MatchedFiles = $files.Count
    RemovedFiles = $removed
    MatchedWorkDirectories = $matchedWorkDirectories
    RemovedWorkDirectories = $removedWorkDirectories
    FreedGB = [math]::Round($bytes / 1GB, 3)
    OlderThanHours = $OlderThanHours
}
