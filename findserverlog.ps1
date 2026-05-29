$candidates = @(
    'F:\72D2D-Server\7DaysToDieServer_Data\output_log*.txt',
    'F:\72D2D-Server\*.log',
    'F:\72D2D-Server\logs\*.txt',
    'F:\72D2D-Server\logs\*.log',
    "$env:APPDATA\7DaysToDie\logs\*.log",
    "$env:APPDATA\7DaysToDie\logs\*.txt"
)
foreach ($c in $candidates) {
    Get-ChildItem $c -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
        Write-Host ("  {0,10} KB  {1:yyyy-MM-dd HH:mm:ss}  {2}" -f [int]($_.Length/1024), $_.LastWriteTime, $_.FullName)
    }
}
