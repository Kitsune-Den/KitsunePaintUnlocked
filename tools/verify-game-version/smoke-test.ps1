# Headless smoke test: boot a dedicated-server install with the given mod folders deployed,
# wait for the world, run pu_audit over telnet, then a graceful 'shutdown' (which exercises the
# World.Save / UnloadWorld patches - a process kill would skip them). Run it twice against the
# same install to also cover the paintunlocked.idmap reload path ("N persisted, 0 new").
#
#   .\smoke-test.ps1 -Install <ref-install> -Mods <bundle>\0_PaintUnlocked,<bundle>\OcbCustomTextures,<a paint pack>
#
# Deployed mods and the generated serverconfig_smoke.xml are removed afterwards unless
# -KeepDeployed is set. Saves and logs land in _work\ (gitignored). Then grep the log for
# '[PaintUnlocked]' / '[PaintAudit]' and confirm the 'Loaded assembly PaintUnlocked (in ...)'
# line points at this install (-UserDataFolder is what stops %APPDATA% mods loading first).
param(
    [Parameter(Mandatory)][string]$Install,
    [string[]]$Mods = @(),
    [switch]$KeepDeployed,
    [string]$Tag = 'run1',
    [int]$Port = 26950,
    [int]$Telnet = 8099,
    [int]$WaitForWorldSec = 600
)
$ErrorActionPreference = 'Stop'
$scratch = Join-Path $PSScriptRoot '_work'
$userData = "$scratch\userdata"
New-Item -ItemType Directory -Force $userData | Out-Null
$log = "$scratch\smoke-$Tag.log"
if (Test-Path $log) { Remove-Item $log }

$deployed = @()
foreach ($m in $Mods) {
    $name = Split-Path $m -Leaf
    $dest = "$Install\Mods\$name"
    if (Test-Path $dest) { throw "$dest already exists - refusing to overwrite a mod in the reference install" }
    Copy-Item $m $dest -Recurse
    $deployed += $dest
    Write-Host "Deployed $name"
}

# smoke config derived from the shipped serverconfig.xml
[xml]$cfg = Get-Content "$Install\serverconfig.xml"
function SetProp($name, $value) {
    $n = $cfg.ServerSettings.property | Where-Object { $_.name -eq $name }
    if ($n) { $n.value = "$value" } else { $e = $cfg.CreateElement('property'); $e.SetAttribute('name',$name); $e.SetAttribute('value',"$value"); $cfg.ServerSettings.AppendChild($e) | Out-Null }
}
SetProp 'ServerPort' $Port
SetProp 'ServerVisibility' 0
SetProp 'EACEnabled' 'false'
SetProp 'TelnetEnabled' 'true'
SetProp 'TelnetPort' $Telnet
SetProp 'TelnetPassword' ''
SetProp 'GameWorld' 'Navezgane'
SetProp 'GameName' 'PUSmoke'
SetProp 'WebDashboardEnabled' 'false'
SetProp 'ServerMaxPlayerCount' 2
$cfgPath = "$Install\serverconfig_smoke.xml"
$cfg.Save($cfgPath)

function Cleanup {
    if ($KeepDeployed) { return }
    foreach ($d in $deployed) { Remove-Item $d -Recurse -Force }
    Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Launching server (log: $log)"
$p = Start-Process -FilePath "$Install\7DaysToDieServer.exe" -WorkingDirectory $Install -PassThru -ArgumentList @(
    "-logfile", "`"$log`"", "-quit", "-batchmode", "-nographics", "-configfile=serverconfig_smoke.xml", "`"-UserDataFolder=$userData`"", "-dedicated")

# wait for the world to come up
$deadline = (Get-Date).AddSeconds($WaitForWorldSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if ($p.HasExited) { Write-Host "Server exited early (code $($p.ExitCode))"; break }
    if (Test-Path $log) {
        $tail = Get-Content $log -Tail 200 -ErrorAction SilentlyContinue
        if ($tail -match 'StartGame done|GameServer.Init successful|Dedicated server only build') { $ready = $true; break }
    }
}
if (-not $ready) { Write-Host "World did not come up in time"; if (-not $p.HasExited) { $p.Kill() }; Cleanup; exit 2 }
Start-Sleep -Seconds 10

# drive over telnet (localhost connects without auth when TelnetPassword is empty)
function Telnet-Send([string[]]$cmds) {
    $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $Telnet)
    $s = $c.GetStream(); $s.ReadTimeout = 3000
    $w = New-Object System.IO.StreamWriter($s); $w.AutoFlush = $true
    $r = New-Object System.IO.StreamReader($s)
    Start-Sleep -Seconds 1
    try { while ($s.DataAvailable) { $null = $r.ReadLine() } } catch {}
    foreach ($cmd in $cmds) { $w.WriteLine($cmd); Start-Sleep -Seconds 3 }
    try { while ($s.DataAvailable) { Write-Host ("  telnet> " + $r.ReadLine()) } } catch {}
    $c.Close()
}
Write-Host "Running pu_audit + shutdown over telnet"
Telnet-Send @('pu_audit')
Start-Sleep -Seconds 3
Telnet-Send @('shutdown')
if (-not $p.WaitForExit(180000)) { Write-Host "Shutdown timed out, killing"; $p.Kill() }
Write-Host "Server exited with $($p.ExitCode)"
Cleanup
exit $p.ExitCode
