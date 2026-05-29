$pu = "$PSScriptRoot\bin\Release\net48\PaintUnlocked.dll"
$targets = @(
    'F:\72D2D-Server\Mods\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\7D2D\Custom\KitsuneCommand\Mods\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\7D2D\Custom\KitsuneCommand\ModConfig\Modlets\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\7D2D\Custom\TestingDen\Mods\0_PaintUnlocked\PaintUnlocked.dll'
)
foreach ($t in $targets) {
    try { Copy-Item $pu $t -Force -ErrorAction Stop } catch { Write-Host "FAIL: $t  $($_.Exception.Message)"; continue }
    if (Test-Path $t) { Write-Host ((Get-FileHash $t).Hash.Substring(0,12) + '  ' + $t) }
}
