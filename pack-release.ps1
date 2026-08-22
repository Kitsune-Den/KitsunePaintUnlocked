# Pack release zips for KitsunePaintUnlocked.
#
# Reads from ./PaintUnlocked-<Version>/ (staging directory with the two mod folders).
# Writes three zips alongside it:
#   PaintUnlocked-<Version>.zip          - bundle, both mods (manual installers)
#   0_PaintUnlocked-<Version>.zip        - single mod, Vortex-friendly
#   OcbCustomTextures-<Version>.zip      - single mod, Vortex-friendly
#
# Each per-mod zip has the mod folder as its single top-level entry, which
# Vortex's 7DTD extension recognises as one mod (no double-folder nesting).
#
# Usage:
#   ./pack-release.ps1                # auto-detects highest PaintUnlocked-X.Y.Z/
#   ./pack-release.ps1 -Version 1.1.0
#   ./pack-release.ps1 -Version 1.1.0 -BundleOnly   # skip per-mod zips
#   ./pack-release.ps1 -Version 1.1.0 -PerModOnly   # skip bundle (don't touch existing)

param(
  [string]$Version,
  [switch]$BundleOnly,
  [switch]$PerModOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Auto-detect: highest PaintUnlocked-X.Y.Z/ dir.
if (-not $Version) {
  $dirs = Get-ChildItem -Path $root -Directory -Filter 'PaintUnlocked-*' |
          Where-Object { $_.Name -match '^PaintUnlocked-(\d+\.\d+\.\d+)$' } |
          Sort-Object {[Version]($_.Name -replace '^PaintUnlocked-','')} -Descending
  if (-not $dirs) { throw "No PaintUnlocked-X.Y.Z/ staging directory found in $root" }
  $Version = $dirs[0].Name -replace '^PaintUnlocked-',''
  Write-Host "Auto-detected version: $Version"
}

$staging = Join-Path $root "PaintUnlocked-$Version"
if (-not (Test-Path $staging)) { throw "Staging dir not found: $staging" }

$mods = @('0_PaintUnlocked','OcbCustomTextures')
foreach ($m in $mods) {
  $modDir = Join-Path $staging $m
  if (-not (Test-Path $modDir)) { throw "Missing mod folder: $modDir" }
  if (-not (Test-Path (Join-Path $modDir 'ModInfo.xml'))) {
    throw "Missing $m/ModInfo.xml - is this a complete build?"
  }
}

# Both mods are versioned together (see README "Versioning"). A mismatch here
# ships a fork whose in-game version line lies about which PaintUnlocked it
# pairs with, which is exactly what the INCOMPATIBLE-OCB diagnostic reads back
# to the user. Catch it before the zips exist.
function Get-ModInfoVersion {
  param([string]$ModName)
  $path = Join-Path (Join-Path $staging $ModName) 'ModInfo.xml'
  $node = ([xml](Get-Content $path -Raw)).xml.Version
  if (-not $node) { throw "$ModName/ModInfo.xml has no <Version> element" }
  $node.value
}

$puVersion = Get-ModInfoVersion '0_PaintUnlocked'
if ($puVersion -ne $Version) {
  throw "0_PaintUnlocked/ModInfo.xml says '$puVersion' but the release is '$Version' - bump ModInfo.xml"
}

# Fork scheme: {upstream base}.{encoded PaintUnlocked version}, four numeric
# components, e.g. 0.8.1.10402 = upstream 0.8.1 + PaintUnlocked 1.4.2. The
# fourth component is major*10000 + minor*100 + patch. It stays numeric so
# System.Version.TryParse succeeds: a non-numeric suffix like -pu1.4.2 loads
# fine but leaves Mod.Version null, which logs a warning on every boot and
# makes the mod_version() XML-patch condition read the fork as 0.0.
if ($Version -notmatch '^(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)$') {
  throw "Release version '$Version' is not X.Y.Z - cannot encode it into the fork version"
}
if ([int]$Matches['min'] -gt 99 -or [int]$Matches['pat'] -gt 99) {
  throw "Release version '$Version' has a minor or patch component above 99 - the fork version encoding (major*10000 + minor*100 + patch) would be ambiguous"
}
$encoded = [int]$Matches['maj'] * 10000 + [int]$Matches['min'] * 100 + [int]$Matches['pat']

$ocbVersion = Get-ModInfoVersion 'OcbCustomTextures'
if ($ocbVersion -notmatch '^(?<base>\d+\.\d+\.\d+)\.(?<enc>\d+)$') {
  throw "OcbCustomTextures/ModInfo.xml says '$ocbVersion' - expected four numeric components, '{upstream}.$encoded' (e.g. 0.8.1.$encoded)"
}
if ([int]$Matches['enc'] -ne $encoded) {
  throw "OcbCustomTextures/ModInfo.xml says '$ocbVersion' but the release is '$Version' - expected '$($Matches['base']).$encoded'"
}
Write-Host "Versions OK: PaintUnlocked $puVersion / OcbCustomTextures $ocbVersion"

function New-ZipFromFolder {
  param([string]$Source, [string]$Destination, [string]$TopFolderName)
  if (Test-Path $Destination) { Remove-Item $Destination -Force }
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
  New-Item -ItemType Directory -Force -Path $tmp | Out-Null
  try {
    Copy-Item $Source (Join-Path $tmp $TopFolderName) -Recurse
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
      $tmp, $Destination,
      [System.IO.Compression.CompressionLevel]::Optimal, $false
    )
  } finally {
    Remove-Item $tmp -Recurse -Force
  }
}

function Test-ZipTopLevel {
  param([string]$Path, [string[]]$ExpectedTops)
  $z = [System.IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $tops = @{}
    foreach ($e in $z.Entries) {
      $top = ($e.FullName -split '[\\/]')[0]
      if ($top) { $tops[$top] = $true }
    }
    $actual = ($tops.Keys | Sort-Object) -join ', '
    $expected = ($ExpectedTops | Sort-Object) -join ', '
    if ($actual -eq $expected) {
      $sizeKB = [math]::Round((Get-Item $Path).Length / 1024, 1)
      Write-Host ("  OK  {0,-40} top-level: {1}  ({2} KB)" -f (Split-Path -Leaf $Path), $actual, $sizeKB)
    } else {
      Write-Host "  FAIL $(Split-Path -Leaf $Path) - got [$actual] wanted [$expected]"
      throw "Zip structure mismatch in $Path"
    }
  } finally {
    $z.Dispose()
  }
}

# 1. Bundle - both mods at top level. This is the existing convention.
if (-not $PerModOnly) {
  $bundle = Join-Path $root "PaintUnlocked-$Version.zip"
  Write-Host "Building bundle: $(Split-Path -Leaf $bundle)"
  if (Test-Path $bundle) { Remove-Item $bundle -Force }
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $bundle,
    [System.IO.Compression.CompressionLevel]::Optimal, $false
  )
  Test-ZipTopLevel -Path $bundle -ExpectedTops $mods
}

# 2. Per-mod zips - Vortex-friendly.
if (-not $BundleOnly) {
  foreach ($m in $mods) {
    $modZip = Join-Path $root "$m-$Version.zip"
    Write-Host "Building per-mod: $(Split-Path -Leaf $modZip)"
    New-ZipFromFolder -Source (Join-Path $staging $m) -Destination $modZip -TopFolderName $m
    Test-ZipTopLevel -Path $modZip -ExpectedTops @($m)
  }
}

Write-Host ""
Write-Host "Release zips for v$($Version):"
Get-ChildItem -Path $root -Filter "*-$Version.zip" |
  Sort-Object Name |
  ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0,-44}  {1} MB" -f $_.Name, $sizeMB)
  }
