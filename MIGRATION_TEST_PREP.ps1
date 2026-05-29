# ====================================================================
#  PREP SCRIPT — Run this ONCE before starting Phase 1
#  Makes sure everything is in the right state
# ====================================================================

Write-Host "=== PaintUnlocked Migration Test Prep ===" -ForegroundColor Cyan
Write-Host ""

# --- Check for running server/client processes ---
Write-Host "1. Checking for running processes..." -ForegroundColor Yellow
$server = Get-Process -Name "7DaysToDieServer" -ErrorAction SilentlyContinue
$client = Get-Process -Name "7DaysToDie" -ErrorAction SilentlyContinue
if ($server) { Write-Host "   !! SERVER IS RUNNING — stop it first!" -ForegroundColor Red } else { Write-Host "   OK: Server not running" -ForegroundColor Green }
if ($client) { Write-Host "   !! CLIENT IS RUNNING — close it first!" -ForegroundColor Red } else { Write-Host "   OK: Client not running" -ForegroundColor Green }
Write-Host ""

# --- Verify DLL hash ---
Write-Host "2. Checking PaintUnlocked.dll hashes..." -ForegroundColor Yellow
$expected = "20735AC6D05E"
$paths = @(
    "F:\72D2D-Server\Mods\0_PaintUnlocked\PaintUnlocked.dll",
    "F:\7D2D\Custom\TestingDen\Mods\0_PaintUnlocked\PaintUnlocked.dll"
)
foreach ($p in $paths) {
    if (Test-Path $p) {
        $hash = (Get-FileHash $p).Hash.Substring(0,12)
        if ($hash -eq $expected) {
            Write-Host "   OK: $hash  $p" -ForegroundColor Green
        } else {
            Write-Host "   !! WRONG HASH: $hash (expected $expected)  $p" -ForegroundColor Red
        }
    } else {
        # Check if it's in _disabled
        $disabled = $p -replace '\\Mods\\', '\Mods\_disabled\'
        if (Test-Path $disabled) {
            $hash = (Get-FileHash $disabled).Hash.Substring(0,12)
            if ($hash -eq $expected) {
                Write-Host "   OK: $hash  $disabled (currently disabled, that's fine for Phase 1)" -ForegroundColor Green
            } else {
                Write-Host "   !! WRONG HASH in _disabled: $hash (expected $expected)  $disabled" -ForegroundColor Red
            }
        } else {
            Write-Host "   !! NOT FOUND: $p" -ForegroundColor Red
        }
    }
}
Write-Host ""

# --- Create _disabled dirs if they don't exist ---
Write-Host "3. Creating _disabled folders if needed..." -ForegroundColor Yellow
$disabledDirs = @(
    "F:\72D2D-Server\Mods\_disabled",
    "F:\7D2D\Custom\TestingDen\Mods\_disabled"
)
foreach ($d in $disabledDirs) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host "   Created: $d" -ForegroundColor Green
    } else {
        Write-Host "   Exists:  $d" -ForegroundColor Green
    }
}
Write-Host ""

# --- Check current mod state ---
Write-Host "4. Current mod locations..." -ForegroundColor Yellow
$mods = @("0_PaintUnlocked", "OcbCustomTextures", "KitsunePaints", "PyroPaints", "ZZZ CK Textures N Paints")
$roots = @(
    @{ Label = "SERVER"; Base = "F:\72D2D-Server\Mods" },
    @{ Label = "CLIENT"; Base = "F:\7D2D\Custom\TestingDen\Mods" }
)
foreach ($root in $roots) {
    Write-Host "   --- $($root.Label) ---" -ForegroundColor Cyan
    foreach ($mod in $mods) {
        $active   = Join-Path $root.Base $mod
        $disabled = Join-Path $root.Base "_disabled\$mod"
        if (Test-Path $active)   { Write-Host "     ENABLED:   $mod" -ForegroundColor Green }
        elseif (Test-Path $disabled) { Write-Host "     DISABLED:  $mod" -ForegroundColor DarkGray }
        else                     { Write-Host "     MISSING:   $mod" -ForegroundColor Red }
    }
}
Write-Host ""

# --- Check serverconfig.xml GameName ---
Write-Host "5. Checking serverconfig.xml GameName..." -ForegroundColor Yellow
$cfg = "F:\72D2D-Server\serverconfig.xml"
if (Test-Path $cfg) {
    $match = Select-String -Path $cfg -Pattern 'name="GameName"'
    if ($match) {
        Write-Host "   $($match.Line.Trim())" -ForegroundColor White
        Write-Host "   (Change this to MigrationTest01 for the test)" -ForegroundColor DarkGray
    }
} else {
    Write-Host "   !! serverconfig.xml not found at $cfg" -ForegroundColor Red
}
Write-Host ""

# --- Check for stale saves ---
Write-Host "6. Existing test saves..." -ForegroundColor Yellow
$saveRoots = @(
    "$env:APPDATA\7DaysToDie\Saves"
)
foreach ($sr in $saveRoots) {
    if (Test-Path $sr) {
        Get-ChildItem $sr -Recurse -Directory -Filter "MigrationTest*" -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "   FOUND: $($_.FullName)" -ForegroundColor Yellow
        }
        Get-ChildItem $sr -Recurse -Directory -Filter "LegacyTest*" -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "   FOUND: $($_.FullName) (old test, can ignore)" -ForegroundColor DarkGray
        }
    }
}
$found = Get-ChildItem "$env:APPDATA\7DaysToDie\Saves" -Recurse -Directory -Filter "MigrationTest*" -ErrorAction SilentlyContinue
if (-not $found) { Write-Host "   None - clean slate" -ForegroundColor Green }
Write-Host ""

Write-Host "=== Prep check complete ===" -ForegroundColor Cyan
Write-Host "If everything is green, proceed to Phase 1 in MIGRATION_TEST_STEPS.txt" -ForegroundColor White
Write-Host ""
