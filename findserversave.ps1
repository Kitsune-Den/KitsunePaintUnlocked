$roots = @('F:\72D2D-Server', 'F:\72D2D-Server\Saves', 'F:\72D2D-Server\UserData')
foreach ($r in $roots) {
    if (Test-Path $r) {
        Write-Host "=== $r ==="
        Get-ChildItem $r -Recurse -Filter 'LegacyTest*' -ErrorAction SilentlyContinue -Directory | ForEach-Object {
            Write-Host ("  DIR  {0}" -f $_.FullName)
        }
        Get-ChildItem $r -Recurse -Filter 'paintunlocked.migrated' -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host ("  SENTINEL  {0:yyyy-MM-dd HH:mm:ss}  {1}" -f $_.LastWriteTime, $_.FullName)
        }
    }
}
# Also check serverconfig.xml for the SaveGameFolder setting
$cfg = 'F:\72D2D-Server\serverconfig.xml'
if (Test-Path $cfg) {
    Write-Host ""
    Write-Host "=== serverconfig.xml save paths ==="
    Select-String -Path $cfg -Pattern 'UserDataFolder|SaveGameFolder|GameName|GameWorld' | ForEach-Object {
        Write-Host ("  {0}" -f $_.Line.Trim())
    }
}
