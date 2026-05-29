Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("$env:APPDATA\7DaysToDie\Saves\Miseso County\LegacyTest02.zip")
$zip.Entries | ForEach-Object {
    Write-Host ("{0,10}  {1}" -f $_.Length, $_.FullName)
}
$zip.Dispose()
