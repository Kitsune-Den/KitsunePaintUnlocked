# Compile the OcbPaintUnlocked fork and then PaintUnlocked against the managed assemblies of a
# given dedicated-server install, without touching the repo's 7dtd-binaries\. Everything lands
# under tools\verify-game-version\_work\ (gitignored).
#
#   .\build-against-refs.ps1 -Install <ref-install> [-OcbSource <OcbCustomTextures checkout>]
#
# The fork's legacy csproj needs .NET Framework 4.8 reference assemblies and a Roslyn-capable
# MSBuild; the SDK-style PaintUnlocked build restores those reference assemblies into the NuGet
# cache, so we run the fork through `dotnet msbuild` pointed at that copy.
param(
    [Parameter(Mandatory)][string]$Install,
    [string]$OcbSource = (Join-Path $PSScriptRoot '..\..\..\OcbCustomTextures')
)
$ErrorActionPreference = 'Stop'
$managed = "$Install\7DaysToDieServer_Data\Managed"
$harmony = "$Install\Mods\0_TFP_Harmony\0Harmony.dll"
$scratch = Join-Path $PSScriptRoot '_work'
$refs = "$scratch\refs"
$fxRefs = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.netframework.referenceassemblies.net48\*\build\.NETFramework\v4.8" -Directory | Sort-Object FullName | Select-Object -Last 1
if (-not $fxRefs) { throw ".NET Framework 4.8 reference assemblies not in the NuGet cache - build PaintUnlocked.csproj once first" }
New-Item -ItemType Directory -Force $refs | Out-Null
Copy-Item "$managed\*.dll" $refs -Force
Copy-Item $harmony $refs -Force

# 1) OcbCustomTextures fork against the target refs
$ocbSrc = (Resolve-Path $OcbSource).Path
$ocb = "$scratch\build-ocb"
if (Test-Path $ocb) { Remove-Item $ocb -Recurse -Force }
New-Item -ItemType Directory -Force $ocb | Out-Null
foreach ($d in 'Harmony','Library','TilingTools','Utils') { Copy-Item "$ocbSrc\$d" "$ocb\$d" -Recurse }
Copy-Item "$ocbSrc\CustomTextures.csproj" $ocb
# neutralise the PostBuildEvent copy so nothing lands outside the scratch dir
(Get-Content "$ocb\CustomTextures.csproj" -Raw) -replace '(?s)<PostBuildEvent>.*?</PostBuildEvent>', '' | Set-Content "$ocb\CustomTextures.csproj" -Encoding utf8
Write-Host "`n### Building OcbCustomTextures fork against $managed"
dotnet msbuild "$ocb\CustomTextures.csproj" /p:Configuration=Release /p:PATH_7D2D_MANAGED=$refs "/p:FrameworkPathOverride=$($fxRefs.FullName)" /nologo /v:m
if ($LASTEXITCODE -ne 0) { throw "OCB build failed" }
$ocbDll = "$ocb\build\bin\Release\CustomTextures.dll"

# 2) PaintUnlocked against the target refs + freshly built fork
$puSrc = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pu = "$scratch\build-pu"
if (Test-Path $pu) { Remove-Item $pu -Recurse -Force }
New-Item -ItemType Directory -Force "$pu\7dtd-binaries" | Out-Null
Copy-Item "$puSrc\*.cs" $pu
Copy-Item "$puSrc\PaintUnlocked.csproj" $pu
foreach ($f in 'Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','LogLibrary.dll') { Copy-Item "$refs\$f" "$pu\7dtd-binaries\" }
Copy-Item $harmony "$pu\7dtd-binaries\"
Copy-Item $ocbDll "$pu\7dtd-binaries\CustomTextures.dll"
Write-Host "`n### Building PaintUnlocked against $managed"
Push-Location $pu
dotnet build PaintUnlocked.csproj -c Release --nologo -v m
$rc = $LASTEXITCODE
Pop-Location
if ($rc -ne 0) { throw "PaintUnlocked build failed" }
Write-Host "`nOCB: $ocbDll"
Write-Host "PU : $pu\bin\Release\net48\PaintUnlocked.dll"
