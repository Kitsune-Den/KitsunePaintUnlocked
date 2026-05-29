$pu = "$PSScriptRoot\bin\Release\net48\PaintUnlocked.dll"
$targets = @(
    'F:\72D2D-Server\Mods\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\72D2D-Server\Mods\_disabled\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\7D2D\Custom\TestingDen\Mods\0_PaintUnlocked\PaintUnlocked.dll',
    'F:\7D2D\Custom\TestingDen\Mods\_disabled\0_PaintUnlocked\PaintUnlocked.dll'
)
$deployed = 0
foreach ($t in $targets) {
    if (Test-Path (Split-Path $t)) {
        Copy-Item $pu $t -Force
        $h = (Get-FileHash $t).Hash.Substring(0,12)
        Write-Host "$h  $t"
        $deployed++
    }
}
if ($deployed -eq 0) { Write-Host "ERROR: no valid targets found!" }
