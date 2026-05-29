$roots = @(
    'F:\72D2D-Server',
    "$env:APPDATA\7DaysToDie\Saves",
    'F:\7D2D'
)
foreach ($r in $roots) {
    if (Test-Path $r) {
        Get-ChildItem $r -Recurse -Filter 'r.-1.-1.7rg' -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host ("{0}  {1} bytes" -f $_.FullName, $_.Length)
        }
    }
}
