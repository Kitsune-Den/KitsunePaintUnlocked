$mods = @("0_PaintUnlocked", "OcbCustomTextures", "PyroPaints")
$roots = @(
    "F:\72D2D-Server\Mods",
    "F:\7D2D\Custom\TestingDen\Mods"
)
foreach ($root in $roots) {
    Write-Host "--- $root ---"
    $disabled = Join-Path $root "_disabled"
    if (-not (Test-Path $disabled)) { New-Item -ItemType Directory -Path $disabled -Force | Out-Null }
    foreach ($mod in $mods) {
        $active = Join-Path $root $mod
        $target = Join-Path $disabled $mod
        if (Test-Path $active) {
            Move-Item $active $target -Force
            Write-Host "  DISABLED: $mod"
        } elseif (Test-Path $target) {
            Write-Host "  ALREADY:  $mod"
        } else {
            Write-Host "  MISSING:  $mod"
        }
    }
}
Write-Host ""
Write-Host "Done. Start the server, connect, paint some blocks, saveworld, stop server."
Write-Host "Then run enable_mods.ps1 for Phase 2."
