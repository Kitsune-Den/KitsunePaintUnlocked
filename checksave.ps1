$save = "$env:APPDATA\7DaysToDie\Saves\Miseso County\LegacyTest02"
if (-not (Test-Path $save)) { Write-Host "NOT FOUND: $save"; exit }
Write-Host "Save dir: $save"
Write-Host ""
Write-Host "Top-level files:"
Get-ChildItem $save -File | ForEach-Object {
    Write-Host ("  {0,10}  {1:yyyy-MM-dd HH:mm:ss}  {2}" -f $_.Length, $_.LastWriteTime, $_.Name)
}
Write-Host ""
$sentinel = Join-Path $save 'paintunlocked.migrated'
if (Test-Path $sentinel) {
    $s = Get-Item $sentinel
    Write-Host ("SENTINEL PRESENT: {0} bytes, written {1:yyyy-MM-dd HH:mm:ss}" -f $s.Length, $s.LastWriteTime)
    Write-Host "Contents:"
    Get-Content $sentinel | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host "SENTINEL ABSENT"
}
Write-Host ""
Write-Host "Region files:"
Get-ChildItem (Join-Path $save 'Region') -File -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  {0,10}  {1:yyyy-MM-dd HH:mm:ss}  {2}" -f $_.Length, $_.LastWriteTime, $_.Name)
}
