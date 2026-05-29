$mods = @("0_PaintUnlocked", "OcbCustomTextures", "PyroPaints")
$roots = @(
    "F:\72D2D-Server\Mods",
    "F:\7D2D\Custom\TestingDen\Mods"
)
foreach ($root in $roots) {
    Write-Host "--- $root ---"
    foreach ($mod in $mods) {
        $disabled = Join-Path $root "_disabled\$mod"
        $active   = Join-Path $root $mod
        if (Test-Path $disabled) {
            Move-Item $disabled $active -Force
            Write-Host "  ENABLED: $mod"
        } elseif (Test-Path $active) {
            Write-Host "  ALREADY: $mod"
        } else {
            Write-Host "  MISSING: $mod"
        }
    }
}
