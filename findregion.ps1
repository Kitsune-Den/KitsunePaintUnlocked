Get-ChildItem 'F:\72D2D-Server\Saves' -Recurse -Filter 'r.-1.-1.7rg' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("{0}  {1} bytes  {2:yyyy-MM-dd HH:mm:ss}" -f $_.FullName, $_.Length, $_.LastWriteTime)
}
$sentinels = Get-ChildItem 'F:\72D2D-Server\Saves' -Recurse -Filter 'paintunlocked.migrated' -ErrorAction SilentlyContinue
if ($sentinels) {
    Write-Host "`nSentinel files:"
    foreach ($s in $sentinels) { Write-Host ("  {0:yyyy-MM-dd HH:mm:ss}  {1}" -f $s.LastWriteTime, $s.FullName) }
} else {
    Write-Host "`nNo paintunlocked.migrated sentinel found anywhere."
}
