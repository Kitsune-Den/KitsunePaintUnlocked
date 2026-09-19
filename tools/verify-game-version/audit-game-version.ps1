# Static audit of the PaintUnlocked + OcbPaintUnlocked-fork Harmony patch surface against a
# game build's Assembly-CSharp.dll. Checks every target method/field the mods bind to, the
# parameter names Harmony resolves by name, the IL shapes the transpilers expect, and then
# IL-diffs every touched type against a baseline assembly so unrelated churn is visible too.
#
#   .\audit-game-version.ps1 -Managed <install>\7DaysToDieServer_Data\Managed
#
# Exit code = number of failures. Needs Mono.Cecil in the NuGet cache (any project that
# restored it, e.g. tools\OcbDiagnostic, puts it there).
param(
    [Parameter(Mandatory)][string]$Managed,
    [string]$Baseline = (Join-Path $PSScriptRoot '..\..\7dtd-binaries\Assembly-CSharp.dll')
)
$cecil = Get-ChildItem "$env:USERPROFILE\.nuget\packages\mono.cecil\*\lib\net40\Mono.Cecil.dll" | Sort-Object FullName | Select-Object -Last 1
if (-not $cecil) { throw "Mono.Cecil.dll not found in the NuGet cache - run 'dotnet restore' in tools\OcbDiagnostic first" }
Add-Type -Path $cecil.FullName

function Load([string]$dll, [string]$dir) {
    $r = New-Object Mono.Cecil.DefaultAssemblyResolver
    $r.AddSearchDirectory($dir)
    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $r
    [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll, $rp)
}
$asm  = Load "$Managed\Assembly-CSharp.dll" $Managed
$base = Load $Baseline (Split-Path $Baseline)
$mod  = $asm.MainModule

$script:fail = 0; $script:pass = 0
function OK($m)   { $script:pass++; Write-Host "  PASS  $m" }
function BAD($m)  { $script:fail++; Write-Host "  FAIL  $m" -ForegroundColor Red }
function INFO($m) { Write-Host "  info  $m" -ForegroundColor DarkGray }

function GetType([Mono.Cecil.ModuleDefinition]$m, [string]$name) { $m.GetType($name) }
function FindMethod($type, [string]$name, [string[]]$params) {
    $out = @()
    foreach ($m in $type.Methods) {
        if ($m.Name -ne $name) { continue }
        if ($params) {
            if ($m.Parameters.Count -ne $params.Count) { continue }
            $match = $true
            for ($k = 0; $k -lt $params.Count; $k++) { if ($m.Parameters[$k].ParameterType.Name -ne $params[$k]) { $match = $false } }
            if (-not $match) { continue }
        }
        $out += $m
    }
    $out
}
function FindField($type, [string]$name) {
    $t = $type
    while ($t) {
        $f = $t.Fields | Where-Object { $_.Name -eq $name }
        if ($f) { return $f }
        if (-not $t.BaseType) { break }
        try { $t = $t.BaseType.Resolve() } catch { break }
    }
    $null
}
function ILText($method) {
    if (-not $method.HasBody) { return '' }
    ($method.Body.Instructions | ForEach-Object {
        $op = $_.Operand
        $os = if ($null -eq $op) { '' } elseif ($op -is [Mono.Cecil.Cil.Instruction]) { "IL_{0:x4}" -f $op.Offset } elseif ($op -is [Mono.Cecil.Cil.VariableDefinition]) { "V_$($op.Index)" } else { "$op" }
        "$($_.OpCode.Name) $os"
    }) -join "`n"
}

# ---- Method checks: type, name, [param types], expected param names (name-bound Harmony args)
$methods = @(
    @{ t='ChunkBlockChannel'; m='.ctor'; p=@('Int64','Int32'); names=@('_bytesPerVal') },
    @{ t='ChunkBlockChannel'; m='Read'; names=@('_bNetworkRead') },
    @{ t='NetConnectionSimple'; m='InitStreams'; p=@('Boolean') },
    @{ t='NetPackageSignDataResponse'; m='GetLength' },
    @{ t='Chunk'; m='save'; p=@('PooledBinaryWriter') },
    @{ t='Chunk'; m='read'; p=@('PooledBinaryReader','UInt32','Boolean'); names=@('_bNetworkRead') },
    @{ t='World'; m='LoadWorld'; names=@('_levelName') },
    @{ t='World'; m='Save' },
    @{ t='World'; m='UnloadWorld' },
    @{ t='RegionFileManager'; m='.ctor'; p=@('String','String','Int32','Boolean') },
    @{ t='Chunk'; m='SetBlockFaceTexture'; names=@('_x','_y','_z','_face','_texture','channel'); il8ff=3 },
    @{ t='Chunk'; m='GetBlockFaceTexture'; names=@('_x','_y','_z','_face','channel'); il8ff=2 },
    @{ t='Chunk'; m='SetTextureFull' },
    @{ t='Chunk'; m='Value64FullToIndex'; static=$true; il8ff=2 },
    @{ t='Chunk'; m='GetSetTextureFullArray'; names=@('_x','_y','_z','_texturefullArray') },
    @{ t='NetPackageSetBlockTexture'; m='Setup'; names=@('_idx') },
    @{ t='NetPackageSetBlockTexture'; m='write' },
    @{ t='NetPackageSetBlockTexture'; m='read'; names=@('_br') },
    @{ t='NetPackageSetBlockTexture'; m='ProcessPackage'; names=@('_world') },
    @{ t='NetPackageSetBlockTexture'; m='GetLength' },
    @{ t='NetPackageRequestToEnterGame'; m='ProcessPackage' },
    @{ t='XUiC_ItemStack'; m='updateBackgroundTexture' },
    @{ t='XUiC_MaterialStack'; m='SetSelectedTextureForItem'; convu1meta=$true },
    @{ t='XUiC_MaterialStackGrid'; m='SetMaterials'; names=@('newSelectedMaterial'); hidden=$true },
    # OcbCustomTextures fork
    @{ t='BlocksFromXml'; m='CreateBlocks'; names=@('_xmlFile') },
    @{ t='MeshDescription'; m='Unload'; names=@('tex') },
    @{ t='BlockTexturesFromXML'; m='CreateBlockTextures'; names=@('_xmlFile'); enumerator=$true },
    @{ t='MeshDescription'; m='loadSingleArray'; enumerator=$true },
    @{ t='BlockTextureData'; m='Init' }
)
Write-Host "`n== Methods ($($asm.Name.Version)) =="
foreach ($c in $methods) {
    $type = GetType $mod $c.t
    if (-not $type) { BAD "type $($c.t) missing"; continue }
    $found = FindMethod $type $c.m $c.p
    $label = "$($c.t).$($c.m)" + $(if ($c.p) { "(" + ($c.p -join ',') + ")" } else { '' })
    if ($found.Count -eq 0) { BAD "$label not found"; continue }
    if ($found.Count -gt 1 -and -not $c.names) { INFO "$label has $($found.Count) overloads" }
    $meth = $found[0]
    if ($c.static -and -not $meth.IsStatic) { BAD "$label is not static"; continue }
    OK "$label exists ($(($meth.Parameters | % { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '))"
    if ($c.names) {
        foreach ($n in $c.names) {
            if ($meth.Parameters.Name -contains $n) { OK "  param '$n' present" } else { BAD "  param '$n' MISSING (name-bound Harmony arg) - actual: $($meth.Parameters.Name -join ',')" }
        }
    }
    if ($c.il8ff) {
        $cnt = @($meth.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldc_I4_8' -or ($_.OpCode.Code -eq 'Ldc_I4' -and $_.Operand -eq 255) }).Count
        if ($cnt -eq $c.il8ff) { OK "  transpiler constants: $cnt (expected $($c.il8ff))" } else { BAD "  transpiler constants: $cnt (expected $($c.il8ff))" }
    }
    if ($c.convu1meta) {
        $ins = $meth.Body.Instructions; $hit = 0
        for ($i=0; $i -lt $ins.Count-1; $i++) { if ($ins[$i].OpCode.Code -eq 'Conv_U1' -and $ins[$i+1].OpCode.Code -eq 'Stfld' -and "$($ins[$i+1].Operand)".Contains('Meta')) { $hit++ } }
        if ($hit -ge 1) { OK "  conv.u1 to stfld Meta present ($hit)" } else { BAD "  conv.u1 to stfld Meta NOT found (MetaTruncation transpiler would no-op)" }
    }
    if ($c.hidden) {
        $ins = $meth.Body.Instructions; $hit = $false
        for ($i=0; $i -lt $ins.Count; $i++) {
            if ($ins[$i].OpCode.Code -eq 'Ldfld' -and "$($ins[$i].Operand)".EndsWith('::Hidden')) {
                for ($j=[Math]::Max(0,$i-6); $j -lt $i; $j++) { if ($ins[$j].OpCode.Code -eq 'Ldelem_Ref') { $hit = $true } }
            }
        }
        if ($hit) { OK "  unguarded list[idx].Hidden still present (guard still needed)" } else { INFO "  ldelem.ref .. ldfld Hidden pattern not found - vanilla may have added its own guard" }
    }
    if ($c.enumerator) {
        $stateMachine = $meth.CustomAttributes | Where-Object { $_.AttributeType.Name -eq 'IteratorStateMachineAttribute' }
        if ($stateMachine) { OK "  iterator state machine: $($stateMachine.ConstructorArguments[0].Value.Name)" } else { BAD "  no IteratorStateMachineAttribute - AccessTools.EnumeratorMoveNext would fail" }
    }
    if ($c.m -eq 'GetLength') { INFO ("  body: " + ((ILText $meth) -replace "`n", " | ")) }
}

# ---- Field checks (walk base chain)
$fields = @(
    @('ChunkBlockChannel','bytesPerVal'), @('Chunk','chnTextures'),
    @('NetConnectionSimple','receiveStreamCompressed'), @('NetConnectionSimple','reliableSendStreamUncompressed'),
    @('NetConnectionSimple','reliableSendStreamWriter'), @('NetConnectionSimple','unreliableSendStreamUncompressed'),
    @('NetConnectionSimple','unreliableSendStreamWriter'), @('NetConnectionSimple','writerStream'), @('NetConnectionSimple','fullConnection'),
    @('NetPackageSignDataResponse','data'),
    @('NetPackageSetBlockTexture','idx'), @('NetPackageSetBlockTexture','blockPos'), @('NetPackageSetBlockTexture','blockFace'),
    @('NetPackageSetBlockTexture','playerIdThatChanged'), @('NetPackageSetBlockTexture','channel'),
    @('BlockTextureData','list'), @('BlockTextureData','ID'), @('BlockTextureData','Hidden'),
    @('MeshDescription','TexDiffuse'), @('MeshDescription','TexNormal'), @('MeshDescription','TexSpecular'),
    @('MeshDescription','meshes'), @('MeshDescription','textureAtlas'),
    @('TextureAtlasBlocks','diffuseTexture'), @('TextureAtlasBlocks','normalTexture'), @('TextureAtlasBlocks','specularTexture')
)
Write-Host "`n== Fields =="
foreach ($f in $fields) {
    $type = GetType $mod $f[0]
    if (-not $type) { BAD "type $($f[0]) missing"; continue }
    $fd = FindField $type $f[1]
    if ($fd) { OK "$($f[0]).$($f[1]) : $($fd.FieldType.Name) (declared on $($fd.DeclaringType.Name))" } else { BAD "$($f[0]).$($f[1]) MISSING" }
}

# ---- IL diff vs baseline for every type the mods touch
$types = @('Chunk','ChunkBlockChannel','NetConnectionSimple','NetConnectionAbs','NetPackageSetBlockTexture','NetPackageSignDataResponse',
           'NetPackageRequestToEnterGame','NetPackage','World','RegionFileManager','XUiC_MaterialStack','XUiC_MaterialStackGrid','XUiC_ItemStack',
           'BlockTextureData','BlockTexturesFromXML','BlocksFromXml','MeshDescription','TextureAtlasBlocks','TextureAtlas','PooledBinaryWriter','PooledBinaryReader',
           'GameManager','TextureFullArray','BlockFace')
Write-Host "`n== IL diff vs baseline ($($base.Name.Version)) =="
foreach ($tn in $types) {
    $a = GetType $mod $tn; $b = GetType $base.MainModule $tn
    if (-not $a -or -not $b) { BAD "$tn missing in one side (new=$([bool]$a) base=$([bool]$b))"; continue }
    $ma = @{}; foreach ($m in $a.Methods) { $ma[$m.FullName] = $m }
    $mb = @{}; foreach ($m in $b.Methods) { $mb[$m.FullName] = $m }
    $added   = @($ma.Keys | Where-Object { -not $mb.ContainsKey($_) })
    $removed = @($mb.Keys | Where-Object { -not $ma.ContainsKey($_) })
    $changed = @($ma.Keys | Where-Object { $mb.ContainsKey($_) -and ((ILText $ma[$_]) -ne (ILText $mb[$_])) })
    $fa = @($a.Fields | % { "$($_.FieldType.FullName) $($_.Name)" }); $fb = @($b.Fields | % { "$($_.FieldType.FullName) $($_.Name)" })
    $fdiff = @(Compare-Object $fa $fb)
    if ($added.Count -eq 0 -and $removed.Count -eq 0 -and $changed.Count -eq 0 -and $fdiff.Count -eq 0) { OK "$tn IL-identical" }
    else {
        Write-Host "  DIFF  $tn : +$($added.Count) methods, -$($removed.Count) methods, ~$($changed.Count) bodies, fields +-$($fdiff.Count)" -ForegroundColor Yellow
        $added   | % { Write-Host "          + $_" }
        $removed | % { Write-Host "          - $_" }
        $changed | % { Write-Host "          ~ $_" }
        $fdiff   | % { Write-Host "          field $($_.SideIndicator) $($_.InputObject)" }
    }
}
Write-Host "`n== Result: $script:pass pass, $script:fail fail =="
exit $script:fail
