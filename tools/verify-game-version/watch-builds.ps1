# Watch the 7DTD dedicated-server branch table on Steam and audit any build we haven't seen.
#
#   .\watch-builds.ps1                 # check; on change, pull the build if needed and audit it
#   .\watch-builds.ps1 -NoDownload     # check only, report changes without pulling/auditing
#
# Compares every branch's buildid against the last run (state in _work\build-watch.json). The
# first run only records a baseline. When `public`, `latest_experimental` or any `vX.Y...` branch
# changes (or a new `vX.Y...` branch appears), the build is installed under -RefRoot unless an
# install of that exact buildid is already known, its game version is read from
# Constants.cVersionInformation, and audit-game-version.ps1 runs against it with the previous
# build of the same branch as the IL-diff baseline. It audits twice: the newest shipped bundle
# (what users run) and a fresh build of origin/main (what the next release would be), built the
# standard README way from a clean export. A report lands in _work\build-watch-<date>.md.
#
# Last line of output is machine-readable for the scheduled task:
#   WATCH-RESULT NO_CHANGE | WATCH-RESULT BASELINE
#   WATCH-RESULT CHANGED audits=<n> failed=<n> main_failed=<n> report=<path>
param(
    [string]$SteamCmd = 'C:\Users\adain\IdeaProjects\PackRelay\data\tools\steamcmd\steamcmd.exe',
    [string]$RefRoot = 'E:\7d2d-refs',
    [switch]$NoDownload
)
$ErrorActionPreference = 'Stop'
$app = 294420
$scratch = Join-Path $PSScriptRoot '_work'
New-Item -ItemType Directory -Force $scratch | Out-Null
$statePath = Join-Path $scratch 'build-watch.json'

function Get-Branches {
    $out = (& $SteamCmd +login anonymous +app_info_update 1 +app_info_print $app +quit) -join "`n"
    $start = $out.IndexOf('"branches"')
    $end = $out.IndexOf('"privatebranches"', [Math]::Max($start, 0))
    if ($start -lt 0 -or $end -lt 0) { throw "Could not find the branches block in steamcmd output" }
    $block = $out.Substring($start, $end - $start)
    $branches = [ordered]@{}
    foreach ($m in [regex]::Matches($block, '"([^"]+)"\s*\{([^{}]*)\}')) {
        $body = $m.Groups[2].Value
        $id = [regex]::Match($body, '"buildid"\s*"(\d+)"').Groups[1].Value
        if (-not $id) { continue }
        $branches[$m.Groups[1].Value] = [ordered]@{
            buildid = $id
            description = [regex]::Match($body, '"description"\s*"([^"]*)"').Groups[1].Value
        }
    }
    if ($branches.Count -eq 0) { throw "Parsed no branches from steamcmd output" }
    $branches
}

function Get-GameVersion([string]$install) {
    $cecil = Get-ChildItem "$env:USERPROFILE\.nuget\packages\mono.cecil\*\lib\net40\Mono.Cecil.dll" | Sort-Object FullName | Select-Object -Last 1
    Add-Type -Path $cecil.FullName
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$install\7DaysToDieServer_Data\Managed\Assembly-CSharp.dll")
    $ins = ($asm.MainModule.GetType('Constants').Methods | Where-Object Name -eq '.cctor').Body.Instructions
    for ($i = 0; $i -lt $ins.Count; $i++) {
        if ($ins[$i].OpCode.Code -eq 'Stsfld' -and $ins[$i].Operand.Name -eq 'cVersionInformation') {
            # newobj VersionInformation(releaseType, major, minor, build) - the four loads before it
            $args4 = @($ins[($i - 5)..($i - 2)] | ForEach-Object {
                if ($null -ne $_.Operand) { [int]$_.Operand } else { [int]($_.OpCode.Name -replace '^ldc\.i4\.', '' -replace '^m1$', '-1') }
            })
            return "V$($args4[1]).$([Math]::Floor($args4[2] / 10)) b$($args4[3]) (raw $($args4[1])/$($args4[2])/$($args4[3]))"
        }
    }
    'unknown'
}

function Watched([string]$name) { $name -eq 'public' -or $name -eq 'latest_experimental' -or $name -match '^v\d' }

# Build origin/main the standard way (README: dotnet build PaintUnlocked.csproj -c Release against
# 7dtd-binaries\) from a clean export, so the working tree and current branch are never touched.
# The export lives outside the repo: a checkout whose csproj predates the tools\ exclusion would
# otherwise compile these copies into the normal build.
function Build-Main {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $dir = Join-Path $env:LOCALAPPDATA 'KitsunePaintUnlocked\main-build'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    git -C $repo fetch -q origin
    $sha = (git -C $repo rev-parse --short origin/main).Trim()
    git -C $repo archive --format=zip -o "$dir\src.zip" origin/main
    Expand-Archive "$dir\src.zip" "$dir\src"
    Copy-Item "$repo\7dtd-binaries" "$dir\src\7dtd-binaries" -Recurse
    $log = dotnet build "$dir\src\PaintUnlocked.csproj" -c Release --nologo -v q 2>&1
    $dll = "$dir\src\bin\Release\net48\PaintUnlocked.dll"
    [pscustomobject]@{
        sha = $sha
        ok = ($LASTEXITCODE -eq 0 -and (Test-Path $dll))
        dll = $dll
        fork = "$repo\7dtd-binaries\CustomTextures.dll"
        log = ($log | Select-Object -Last 5) -join "`n"
    }
}

$now = Get-Date
$branches = Get-Branches

if (-not (Test-Path $statePath)) {
    # Seed with the reference installs that already exist so they aren't re-downloaded.
    $state = [ordered]@{
        branches = $branches
        installs = [ordered]@{
            '24994542' = 'C:\Users\adain\IdeaProjects\7d2d-v3.2-ref'
            '25542643' = 'C:\Users\adain\IdeaProjects\7d2d-v3.3-exp-ref'
        }
        lastCheck = $now.ToString('o')
    }
    $state | ConvertTo-Json -Depth 5 | Set-Content $statePath -Encoding utf8
    $branches.GetEnumerator() | Where-Object { Watched $_.Key } | ForEach-Object { Write-Host "baseline $($_.Key) = $($_.Value.buildid) $($_.Value.description)" }
    Write-Host "WATCH-RESULT BASELINE"
    exit 0
}

$state = Get-Content $statePath -Raw | ConvertFrom-Json
$installs = @{}
foreach ($p in $state.installs.PSObject.Properties) { if (Test-Path $p.Value) { $installs[$p.Name] = $p.Value } }

$changes = @()
foreach ($kv in $branches.GetEnumerator()) {
    if (-not (Watched $kv.Key)) { continue }
    $old = $state.branches.PSObject.Properties[$kv.Key]
    $oldId = if ($old) { $old.Value.buildid } else { $null }
    if ($oldId -ne $kv.Value.buildid) {
        $changes += [pscustomobject]@{ branch = $kv.Key; from = $oldId; to = $kv.Value.buildid; description = $kv.Value.description }
    }
}

if ($changes.Count -eq 0) {
    $state.lastCheck = $now.ToString('o')
    $state | ConvertTo-Json -Depth 5 | Set-Content $statePath -Encoding utf8
    Write-Host "No branch changes (public = $($branches['public'].buildid), latest_experimental = $($branches['latest_experimental'].buildid))"
    Write-Host "WATCH-RESULT NO_CHANGE"
    exit 0
}

$report = Join-Path $scratch ("build-watch-{0}.md" -f $now.ToString('yyyy-MM-dd-HHmm'))
$lines = @("# 7DTD build watch - $($now.ToString('yyyy-MM-dd HH:mm'))", '', '| Branch | Was | Now | Description |', '|---|---|---|---|')
$changes | ForEach-Object { $lines += "| $($_.branch) | $(if ($_.from) { $_.from } else { '(new)' }) | $($_.to) | $($_.description) |" }
$lines += ''

$audits = 0; $failedAudits = 0; $mainFailed = 0
if (-not $NoDownload) {
    $main = Build-Main
    if ($main.ok) { $lines += "Fresh build of origin/main ($($main.sha)) succeeded; audited alongside the shipped bundle.", '' }
    else { $lines += "**Build of origin/main ($($main.sha)) FAILED** - only the shipped bundle was audited:", '', '```', $main.log, '```', ''; $mainFailed++ }
    # one audit per distinct build, whichever branches point at it
    foreach ($group in ($changes | Group-Object to)) {
        $buildId = $group.Name
        $branch = ($group.Group | Sort-Object { if ($_.branch -eq 'public') { 0 } elseif ($_.branch -match '^v') { 1 } else { 2 } } | Select-Object -First 1).branch
        $install = $installs[$buildId]
        if (-not $install) {
            $install = Join-Path $RefRoot "7d2d-$buildId"
            New-Item -ItemType Directory -Force $RefRoot | Out-Null
            Write-Host "Installing build $buildId (branch $branch) into $install"
            # -beta is sticky in steamcmd: always pass it explicitly
            & $SteamCmd +force_install_dir $install +login anonymous +app_update $app -beta $branch validate +quit | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
            $manifest = Get-Content "$install\steamapps\appmanifest_$app.acf" -Raw
            $got = [regex]::Match($manifest, '"buildid"\s*"(\d+)"').Groups[1].Value
            if ($got -ne $buildId) {
                $lines += "## Build $buildId - download mismatch", '', "steamcmd installed build $got instead (branch moved again, or sticky -beta). Not audited.", ''
                $failedAudits++
                continue
            }
            $installs[$buildId] = $install
        }
        $version = Get-GameVersion $install
        # IL-diff baseline: the build this branch pointed at before, if we have it installed
        $prevId = ($group.Group | Where-Object { $_.from -and $installs[$_.from] } | Select-Object -First 1).from
        if (-not $prevId) { $prevId = $state.branches.public.buildid }
        $baseArgs = @{}
        if ($installs[$prevId]) { $baseArgs.Baseline = "$($installs[$prevId])\7DaysToDieServer_Data\Managed\Assembly-CSharp.dll" }
        $lines += "## Build $buildId ($(($group.Group.branch) -join ', ')) - $version", ''
        $lines += "- install: ``$install``"
        $lines += "- IL-diff baseline: $(if ($baseArgs.Baseline) { "build $prevId" } else { 'repo 7dtd-binaries (3.1)' })"

        # shipped bundle = what users have installed; origin/main = what the next release would be
        $targets = @([pscustomobject]@{ label = 'shipped bundle'; tag = 'shipped'; dlls = $null })
        if ($main.ok) { $targets += [pscustomobject]@{ label = "origin/main $($main.sha)"; tag = 'main'; dlls = @($main.dll, $main.fork) } }
        $diffLines = @()
        foreach ($t in $targets) {
            $auditLog = Join-Path $scratch "audit-$buildId-$($t.tag).txt"
            $modArgs = @{}
            if ($t.dlls) { $modArgs.ModDlls = $t.dlls }
            & (Join-Path $PSScriptRoot 'audit-game-version.ps1') -Managed "$install\7DaysToDieServer_Data\Managed" @baseArgs @modArgs *>&1 | Out-File $auditLog -Encoding utf8
            $fails = $LASTEXITCODE
            $audits++
            if ($fails -ne 0) { $failedAudits++; if ($t.tag -eq 'main') { $mainFailed++ } }
            $result = (Select-String -Path $auditLog -Pattern '== Result:' | Select-Object -Last 1).Line
            $lines += "- audit, $($t.label): $result - log ``$auditLog``"
            $failLines = @(Select-String -Path $auditLog -Pattern '^\s+FAIL' | ForEach-Object { $_.Line.Trim() })
            if ($failLines) { $failLines | ForEach-Object { $lines += "  - $_" } }
            if (-not $diffLines) { $diffLines = @(Select-String -Path $auditLog -Pattern '^\s+DIFF' | ForEach-Object { $_.Line.Trim() }) }
        }
        if ($diffLines) { $lines += '', 'Types the mods touch that changed:', ''; $diffLines | ForEach-Object { $lines += "- $_" } }
        $lines += ''
    }
} else {
    $lines += 'Download/audit skipped (-NoDownload).', ''
}
$lines | Set-Content $report -Encoding utf8

$state.branches = $branches
$state.installs = $installs
$state.lastCheck = $now.ToString('o')
$state | ConvertTo-Json -Depth 5 | Set-Content $statePath -Encoding utf8

Get-Content $report | ForEach-Object { Write-Host $_ }
Write-Host "WATCH-RESULT CHANGED audits=$audits failed=$failedAudits main_failed=$mainFailed report=$report"
exit 0
