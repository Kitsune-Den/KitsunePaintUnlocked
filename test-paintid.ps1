<#
.SYNOPSIS
  One-command inner loop for testing PaintUnlocked paint-ID stability.

  Builds the mod, deploys the DLL to the server + TestingDen client, then dumps
  the current world's persistent paint-ID map and greps the logs for the paint-ID
  lines that prove the persistence + duplicate-name fixes fired.

.EXAMPLE
  .\test-paintid.ps1                # build, deploy, inspect
  .\test-paintid.ps1 -NoBuild       # skip build, just deploy + inspect
  .\test-paintid.ps1 -NoDeploy      # only inspect idmap + logs (game can stay running)
  .\test-paintid.ps1 -GameName MigrationTest01   # only show that world's idmap
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoDeploy,
    [int]$LogLines = 40,
    [string]$GameName
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$dll  = Join-Path $repo 'bin\Release\net48\PaintUnlocked.dll'

function Section($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }

# --- 1. Build -----------------------------------------------------------
if (-not $NoBuild) {
    Section "Build"
    dotnet build (Join-Path $repo 'PaintUnlocked.csproj') -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED - stopping." -ForegroundColor Red; exit 1 }
}
if (-not (Test-Path $dll)) { Write-Host "DLL not found at $dll - build first." -ForegroundColor Red; exit 1 }

# --- 2. Deploy ----------------------------------------------------------
if (-not $NoDeploy) {
    Section "Deploy"
    $targets = @(
        'F:\72D2D-Server\Mods\0_PaintUnlocked\PaintUnlocked.dll',
        'F:\7D2D\Custom\TestingDen\Mods\0_PaintUnlocked\PaintUnlocked.dll'
    )
    $srcHash = (Get-FileHash $dll).Hash.Substring(0,12)
    Write-Host "source $srcHash  $dll"
    foreach ($t in $targets) {
        if (-not (Test-Path (Split-Path $t))) { Write-Host "  skip (no dir): $t" -ForegroundColor DarkGray; continue }
        try {
            Copy-Item $dll $t -Force
            $h = (Get-FileHash $t).Hash.Substring(0,12)
            $ok = if ($h -eq $srcHash) { 'OK ' } else { 'MISMATCH ' }
            $color = if ($h -eq $srcHash) { 'Green' } else { 'Red' }
            Write-Host "  $ok$h  $t" -ForegroundColor $color
        } catch {
            # A copy that "succeeds" but leaves a stale hash means the game has the DLL locked.
            Write-Host "  FAIL (game running / file locked?): $t  $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# --- 3. Persistent paint-ID map for the loaded world --------------------
Section "Persistent paint-ID map"
$saveRoot = "$env:APPDATA\7DaysToDie\Saves"
if (-not (Test-Path $saveRoot)) {
    Write-Host "No save root at $saveRoot" -ForegroundColor Yellow
} else {
    $maps = Get-ChildItem $saveRoot -Recurse -Filter 'paintunlocked.idmap' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending
    if ($GameName) { $maps = $maps | Where-Object { $_.FullName -like "*\$GameName\*" } }

    if (-not $maps) {
        Write-Host "No paintunlocked.idmap found yet (world hasn't snapshotted, or not authoritative)." -ForegroundColor Yellow
    } else {
        $m = $maps | Select-Object -First 1
        $dir = Split-Path $m.FullName
        Write-Host ("World save: {0}" -f $dir)
        Write-Host ("idmap:      {0:yyyy-MM-dd HH:mm:ss}  {1} entries" -f $m.LastWriteTime,
            ((Get-Content $m.FullName | Where-Object { $_ -and $_[0] -ne '#' }).Count))
        $sentinel = Join-Path $dir 'paintunlocked.migrated'
        $sentStatus = if (Test-Path $sentinel) { 'present (10-bit, migrated)' } else { 'ABSENT' }
        Write-Host ("sentinel:   {0}" -f $sentStatus)
        Write-Host "--- idmap contents ---"
        Get-Content $m.FullName | ForEach-Object { Write-Host "  $_" }
        if ($maps.Count -gt 1) {
            Write-Host ("({0} idmaps total across worlds; showing newest. Use -GameName to pick one.)" -f $maps.Count) -ForegroundColor DarkGray
        }
    }
}

# --- 4. Relevant log lines ----------------------------------------------
Section "Recent PaintUnlocked paint-ID log lines (last $LogLines)"
$logGlobs = @(
    'F:\72D2D-Server\logs\*.log',
    'F:\72D2D-Server\logs\*.txt',
    'F:\72D2D-Server\7DaysToDieServer_Data\output_log*.txt',
    'F:\7D2D\Custom\TestingDen\output_log.txt',
    "$env:APPDATA\7DaysToDie\logs\*.log",
    "$env:APPDATA\7DaysToDie\logs\*.txt"
)
$pattern = 'reconcile|persistent paint|Duplicate paint name|collision|\[PaintAudit\]|snapshot|GetFreePaintID seeded|Saved persistent|deferring reconcile'

$logFile = Get-ChildItem $logGlobs -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $logFile) {
    Write-Host "No log file found in the usual locations." -ForegroundColor Yellow
} else {
    Write-Host ("log: {0:yyyy-MM-dd HH:mm:ss}  {1}" -f $logFile.LastWriteTime, $logFile.FullName) -ForegroundColor DarkGray
    $hits = Select-String -Path $logFile.FullName -Pattern $pattern -ErrorAction SilentlyContinue |
            Select-Object -Last $LogLines
    if (-not $hits) {
        Write-Host "  (no matching lines - has the world loaded with the new DLL yet?)" -ForegroundColor Yellow
    } else {
        foreach ($h in $hits) {
            $color = if ($h.Line -match 'Duplicate|collision|FAIL|Error|deferring') { 'Yellow' } else { 'Gray' }
            Write-Host ("  {0}" -f $h.Line.Trim()) -ForegroundColor $color
        }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
