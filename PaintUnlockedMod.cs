using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

public class PaintUnlockedMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("com.adainthelab.paintunlocked");

        // === Layer 5: Widen ChunkBlockChannel storage from 48-bit to 64-bit ===
        // Must be patched BEFORE chunks are created. bytesPerVal=6 → 8.
        var cbcCtor = typeof(ChunkBlockChannel).GetConstructor(new[] { typeof(long), typeof(int) });
        if (cbcCtor != null)
        {
            harmony.Patch(cbcCtor, prefix: new HarmonyMethod(AccessTools.Method(typeof(ChunkStorageWidthPatch), "CtorPrefix")));
        }
        else Log.Warning("[PaintUnlocked] ChunkBlockChannel constructor not found!");

        // === Layer 4: Widen chunk face storage from 8-bit to 10-bit ===
        var setBlockFaceTex = AccessTools.Method(typeof(Chunk), "SetBlockFaceTexture");
        harmony.Patch(setBlockFaceTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchSet")));

        var getBlockFaceTex = AccessTools.Method(typeof(Chunk), "GetBlockFaceTexture");
        harmony.Patch(getBlockFaceTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchGet")));

        var v64ToIdx = typeof(Chunk).GetMethod("Value64FullToIndex", BindingFlags.Public | BindingFlags.Static);
        if (v64ToIdx != null)
        {
            harmony.Patch(v64ToIdx, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchValue64ToIndex")));
            harmony.Patch(v64ToIdx, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "ClampValue64Result")));
        }
        else Log.Warning("[PaintUnlocked] Value64FullToIndex not found!");

        // === Prefab/bulk texture re-encoding (8-bit → 10-bit) ===
        var setTexFull = AccessTools.Method(typeof(Chunk), "SetTextureFull");
        if (setTexFull != null)
        {
            harmony.Patch(setTexFull, prefix: new HarmonyMethod(AccessTools.Method(typeof(TextureFullRepackPatch), "Prefix")));
            Log.Out("[PaintUnlocked] SetTextureFull: prefix added for 8-bit → 10-bit re-encoding");
        }
        else Log.Warning("[PaintUnlocked] SetTextureFull not found!");

        // Also patch GetSetTextureFullArray to check if prefabs go through here
        var getSetTexFullArr = AccessTools.Method(typeof(Chunk), "GetSetTextureFullArray");
        if (getSetTexFullArr != null)
        {
            harmony.Patch(getSetTexFullArr, prefix: new HarmonyMethod(AccessTools.Method(typeof(TextureFullRepackPatch), "GetSetPrefix")));
            Log.Out("[PaintUnlocked] GetSetTextureFullArray: diagnostic prefix added");
        }

        // === Layer 2: Paint ID allocation floor ===
        var getFreePaintID = AccessTools.Method(typeof(OpaqueTextures), "GetFreePaintID");
        var getFreePaintIDPrefix = AccessTools.Method(typeof(OcbPaintLimitPatch), "GetFreePaintIDPrefix");
        harmony.Patch(getFreePaintID, prefix: new HarmonyMethod(getFreePaintIDPrefix));

        // === Layer 1: Network packet encoding for indices > 255 ===
        var netPkgType = typeof(NetPackageSetBlockTexture);

        var setupMethod = AccessTools.Method(netPkgType, "Setup");
        harmony.Patch(setupMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "SetupPostfix")));

        var writeMethod = AccessTools.Method(netPkgType, "write");
        harmony.Patch(writeMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "WritePrefix")));

        var readMethod = AccessTools.Method(netPkgType, "read");
        harmony.Patch(readMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "ReadPrefix")));

        var processMethod = AccessTools.Method(netPkgType, "ProcessPackage");
        harmony.Patch(processMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIndexWidenerPatch), "ProcessPackagePrefix")));

        // === UI protection ===
        var updateBg = AccessTools.Method(typeof(XUiC_ItemStack), "updateBackgroundTexture");
        harmony.Patch(updateBg, finalizer: new HarmonyMethod(AccessTools.Method(typeof(UpdateBackgroundTexturePatch), "Finalizer")));

        // === Fix paint ID byte truncation in SetSelectedTextureForItem ===
        // The game does conv.u1 (byte cast) on textureData.ID before storing in Meta,
        // truncating paint IDs above 255. Remove the conv.u1 to preserve full ID.
        var setSelTex = AccessTools.Method(typeof(XUiC_MaterialStack), "SetSelectedTextureForItem");
        if (setSelTex != null)
        {
            harmony.Patch(setSelTex, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MetaTruncationPatch), "Transpiler")));
        }
        else Log.Warning("[PaintUnlocked] SetSelectedTextureForItem not found!");

        Log.Out("[PaintUnlocked] Loaded - paint limit raised to 1023 (10-bit chunk storage, 64-bit ChunkBlockChannel).");
        Log.Warning("[PaintUnlocked] Fresh world required. Existing 8-bit painted blocks will show default textures.");
    }
}

public static class PaintIndexWidenerPatch
{
    private const byte OverflowFlag = 0x80;

    private static readonly Dictionary<int, ushort> _idxMap = new Dictionary<int, ushort>();
    private static readonly object _idxLock = new object();

    private static readonly BindingFlags _fieldFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo _fIdx =
        typeof(NetPackageSetBlockTexture).GetField("idx", _fieldFlags);
    private static readonly FieldInfo _fBlockPos =
        typeof(NetPackageSetBlockTexture).GetField("blockPos", _fieldFlags);
    private static readonly FieldInfo _fBlockFace =
        typeof(NetPackageSetBlockTexture).GetField("blockFace", _fieldFlags);
    private static readonly FieldInfo _fPlayerId =
        typeof(NetPackageSetBlockTexture).GetField("playerIdThatChanged", _fieldFlags);
    private static readonly FieldInfo _fChannel =
        typeof(NetPackageSetBlockTexture).GetField("channel", _fieldFlags);

    private static bool _reflectionValid = false;

    static PaintIndexWidenerPatch()
    {
        _reflectionValid = _fIdx != null && _fBlockPos != null && _fBlockFace != null
                        && _fPlayerId != null && _fChannel != null;
        if (!_reflectionValid)
            Log.Warning("[PaintUnlocked] Reflection check FAILED - network patches disabled");
        else
            Log.Out("[PaintUnlocked] Reflection check passed - all fields found.");
    }

    private static void StoreIdx(NetPackageSetBlockTexture instance, ushort value)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
        lock (_idxLock) { _idxMap[key] = value; }
        _fIdx?.SetValue(instance, (byte)(value & 0xFF));
    }

    private static ushort LoadIdx(NetPackageSetBlockTexture instance)
    {
        var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
        lock (_idxLock) { if (_idxMap.TryGetValue(key, out var v)) return v; }
        return _fIdx != null ? (byte)_fIdx.GetValue(instance) : (byte)0;
    }

    public static void SetupPostfix(NetPackageSetBlockTexture __instance, int _idx)
    {
        if (!_reflectionValid) return;
        StoreIdx(__instance, (ushort)_idx);
    }

    /// <summary>
    /// Field mutation prefix: for overflow indices, modify channel/idx fields on instance
    /// BEFORE vanilla write() runs. Vanilla serializes our values through PooledBinaryWriter.
    /// Does NOT skip original — PooledBinaryWriter internal state must be maintained.
    /// </summary>
    public static void WritePrefix(NetPackageSetBlockTexture __instance)
    {
        if (!_reflectionValid) return;

        var idx = LoadIdx(__instance);
        if (idx <= 255) return;

        byte channelWire = (byte)(OverflowFlag | ((idx >> 8) & 0x7F));
        byte idxLow = (byte)(idx & 0xFF);

        _fChannel.SetValue(__instance, channelWire);
        _fIdx.SetValue(__instance, idxLow);
    }

    /// <summary>
    /// Decodes overflow encoding in ProcessPackage (reliable on both client and server).
    /// ReadPrefix is unreliable on dedicated server due to virtual method dispatch.
    /// </summary>
    public static bool ProcessPackagePrefix(NetPackageSetBlockTexture __instance, World _world)
    {
        if (!_reflectionValid) return true;

        try
        {
            var channel = (byte)_fChannel.GetValue(__instance);

            if ((channel & OverflowFlag) == 0)
            {
                var fullIdx = LoadIdx(__instance);
                if (fullIdx <= 255) return true;

                var blockPos2  = (Vector3i)_fBlockPos.GetValue(__instance);
                var blockFace2 = (BlockFace)_fBlockFace.GetValue(__instance);
                var playerId2  = (int)_fPlayerId.GetValue(__instance);
                ApplyTexture(_world, blockPos2, blockFace2, fullIdx, playerId2);
                return false;
            }

            var idxByte = (byte)_fIdx.GetValue(__instance);
            ushort decodedIdx = (ushort)(((channel & 0x7F) << 8) | idxByte);

            var blockPos  = (Vector3i)_fBlockPos.GetValue(__instance);
            var blockFace = (BlockFace)_fBlockFace.GetValue(__instance);
            var playerId  = (int)_fPlayerId.GetValue(__instance);

            Log.Out($"[PaintUnlocked] ProcessPackagePrefix: overflow decode -> fullIdx={decodedIdx} at {blockPos} face={blockFace}");
            ApplyTexture(_world, blockPos, blockFace, decodedIdx, playerId);
            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] ProcessPackagePrefix failed: {ex.Message}");
            return true;
        }
    }

    private static void ApplyTexture(World _world, Vector3i blockPos, BlockFace blockFace, ushort idx, int playerId)
    {
        var cc = _world.ChunkClusters[0];
        if (cc == null) return;

        var chunk = (Chunk)cc.GetChunkFromWorldPos(blockPos);
        if (chunk == null) return;

        var localPos = World.toBlock(blockPos);
        chunk.SetBlockFaceTexture(localPos.x, localPos.y, localPos.z, blockFace, idx);
        chunk.isModified = true;
    }

    public static bool ReadPrefix(NetPackageSetBlockTexture __instance, PooledBinaryReader _br)
    {
        return true; // passthrough — ProcessPackagePrefix handles decoding
    }
}
