Write-Host "=== Reset MigrationTest01 ==="

# Delete the old (corrupted) save
$save = "$env:APPDATA\7DaysToDie\Saves\Miseso County\MigrationTest01"
if (Test-Path $save) {
    Remove-Item $save -Recurse -Force
    Write-Host "DELETED: $save"
} else {
    Write-Host "Already gone: $save"
}

# Verify mods are disabled for Phase 1
$mods = @("0_PaintUnlocked", "OcbCustomTextures", "PyroPaints")
$roots = @("F:\72D2D-Server\Mods", "F:\7D2D\Custom\TestingDen\Mods")
Write-Host ""
Write-Host "=== Mod status ==="
foreach ($root in $roots) {
    Write-Host "--- $root ---"
    foreach ($mod in $mods) {
        $active   = Join-Path $root $mod
        $disabled = Join-Path $root "_disabled\$mod"
        if (Test-Path $active)       { Write-Host "  !! ENABLED (needs disabling): $mod" -ForegroundColor Red }
        elseif (Test-Path $disabled)  { Write-Host "  OK DISABLED: $mod" -ForegroundColor Green }
        else                          { Write-Host "  MISSING: $mod" -ForegroundColor Yellow }
    }
}
Write-Host ""
Write-Host "Ready for Phase 1: start server, connect, paint wall, saveworld, stop server."
