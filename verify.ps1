Get-ChildItem -Path 'F:\72D2D-Server','F:\7D2D\Custom\TestingDen' -Filter 'PaintUnlocked.dll' -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    $h = (Get-FileHash $_.FullName).Hash.Substring(0,12)
    Write-Host ("{0}  {1:yyyy-MM-dd HH:mm:ss}  {2}" -f $h, $_.LastWriteTime, $_.FullName)
}
