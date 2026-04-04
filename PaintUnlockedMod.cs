using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;

public class PaintUnlockedMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var harmony = new Harmony("com.adainthelab.paintunlocked");

        var netPkgType = typeof(NetPackageSetBlockTexture);

        var setupMethod = AccessTools.Method(netPkgType, "Setup");
        var setupPostfix = AccessTools.Method(typeof(PaintIndexWidenerPatch), "SetupPostfix");
        harmony.Patch(setupMethod, postfix: new HarmonyMethod(setupPostfix));

        var writeMethod = AccessTools.Method(netPkgType, "write");
        var writePrefix = AccessTools.Method(typeof(PaintIndexWidenerPatch), "WritePrefix");
        harmony.Patch(writeMethod, prefix: new HarmonyMethod(writePrefix));

        var readMethod = AccessTools.Method(netPkgType, "read");
        var readPrefix = AccessTools.Method(typeof(PaintIndexWidenerPatch), "ReadPrefix");
        harmony.Patch(readMethod, prefix: new HarmonyMethod(readPrefix));

        var processMethod = AccessTools.Method(netPkgType, "ProcessPackage");
        var processPrefix = AccessTools.Method(typeof(PaintIndexWidenerPatch), "ProcessPackagePrefix");
        harmony.Patch(processMethod, prefix: new HarmonyMethod(processPrefix));

        var getFreePaintID = AccessTools.Method(typeof(OpaqueTextures), "GetFreePaintID");
        var getFreePaintIDPrefix = AccessTools.Method(typeof(OcbPaintLimitPatch), "GetFreePaintIDPrefix");
        harmony.Patch(getFreePaintID, prefix: new HarmonyMethod(getFreePaintIDPrefix));

        var setBlockFaceTex = AccessTools.Method(typeof(Chunk), "SetBlockFaceTexture");
        var setTranspiler = AccessTools.Method(typeof(ChunkTexturePatch), "PatchSet");
        harmony.Patch(setBlockFaceTex, transpiler: new HarmonyMethod(setTranspiler));

        var getBlockFaceTex = AccessTools.Method(typeof(Chunk), "GetBlockFaceTexture");
        var getTranspiler = AccessTools.Method(typeof(ChunkTexturePatch), "PatchGet");
        harmony.Patch(getBlockFaceTex, transpiler: new HarmonyMethod(getTranspiler));

        // Patch Value64FullToIndex to read 10-bit paint indices
        // Also add postfix clamp to handle old 8-bit world data gracefully
        var v64ToIdx = typeof(Chunk).GetMethod("Value64FullToIndex", BindingFlags.Public | BindingFlags.Static);
        if (v64ToIdx != null)
        {
            harmony.Patch(v64ToIdx, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "PatchValue64ToIndex")));
            harmony.Patch(v64ToIdx, postfix: new HarmonyMethod(AccessTools.Method(typeof(ChunkTexturePatch), "ClampValue64Result")));
        }
        else Log.Warning("[PaintUnlocked] Value64FullToIndex not found!");



        var updateBg = AccessTools.Method(typeof(XUiC_ItemStack), "updateBackgroundTexture");
        var updateBgFinalizer = AccessTools.Method(typeof(UpdateBackgroundTexturePatch), "Finalizer");
        harmony.Patch(updateBg, finalizer: new HarmonyMethod(updateBgFinalizer));

        Log.Out("[PaintUnlocked] Loaded - paint texture limit removed (byte -> ushort, chunk storage 8-bit -> 10-bit).");
        Log.Warning("[PaintUnlocked] IMPORTANT: This mod uses 10-bit chunk storage. Existing worlds painted with the vanilla 8-bit format will show default textures on previously painted blocks. A fresh world is required for correct operation.");
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

    private static readonly BindingFlags _methodFlags =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo _writeInt =
        typeof(PooledBinaryWriter).GetMethod("Write", _methodFlags, null, new[] { typeof(int) }, null);
    private static readonly MethodInfo _writeByte =
        typeof(PooledBinaryWriter).GetMethod("Write", _methodFlags, null, new[] { typeof(byte) }, null);

    private static bool _reflectionValid = false;

    static PaintIndexWidenerPatch()
    {
        _reflectionValid = _fIdx != null && _fBlockPos != null && _fBlockFace != null
                        && _fPlayerId != null && _fChannel != null
                        && _writeInt != null && _writeByte != null;
        if (!_reflectionValid)
            Log.Warning($"[PaintUnlocked] Reflection check FAILED");
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

    private static void WriteInt32(System.IO.Stream s, int v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 24) & 0xFF));
    }

    public static bool WritePrefix(NetPackageSetBlockTexture __instance, PooledBinaryWriter _bw)
    {
        if (!_reflectionValid) return true;

        // Only intercept overflow indices -- let vanilla handle 0-255 natively
        var idx = LoadIdx(__instance);
        if (idx <= 255) return true;

        try
        {
            var blockPos  = (Vector3i)_fBlockPos.GetValue(__instance);
            var blockFace = (BlockFace)_fBlockFace.GetValue(__instance);
            var playerId  = (int)_fPlayerId.GetValue(__instance);

            byte channelWire = (byte)(OverflowFlag | ((idx >> 8) & 0x7F));

            Log.Out($"[PaintUnlocked] WritePrefix: idx={idx} pos={blockPos} face={blockFace} -> overflow channelWire=0x{channelWire:X2}");

            // Write directly to underlying stream to bypass PooledBinaryWriter
            // which may use `new` to hide BinaryWriter.Write (not override)
            var s = _bw.BaseStream;
            WriteInt32(s, blockPos.x);
            WriteInt32(s, blockPos.y);
            WriteInt32(s, blockPos.z);
            s.WriteByte((byte)blockFace);
            WriteInt32(s, playerId);
            s.WriteByte(channelWire);
            s.WriteByte((byte)(idx & 0xFF));

            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] WritePrefix failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Runs before ProcessPackage to decode overflow encoding that vanilla read() stored literally.
    /// This is the reliable server-side decode path -- ReadPrefix may not fire due to virtual dispatch.
    /// If ReadPrefix already decoded (channel won't have overflow flag), this is a no-op.
    ///
    /// For overflow packets, this replaces ProcessPackage entirely (returns false) because the
    /// vanilla code reads the byte-sized idx field which can only hold 0-255. We must call
    /// SetBlockFaceTexture ourselves with the full decoded index.
    /// </summary>
    public static bool ProcessPackagePrefix(NetPackageSetBlockTexture __instance, World _world)
    {
        if (!_reflectionValid) return true;

        try
        {
            var channel = (byte)_fChannel.GetValue(__instance);

            // If ReadPrefix already fired and decoded, channel won't have the flag -- let vanilla run
            if ((channel & OverflowFlag) == 0)
            {
                // But check _idxMap in case ReadPrefix decoded an overflow for us
                var fullIdx = LoadIdx(__instance);
                if (fullIdx <= 255) return true; // normal packet, let vanilla handle it

                // ReadPrefix decoded it -- we still need to apply it ourselves since idx field is byte
                var blockPos2  = (Vector3i)_fBlockPos.GetValue(__instance);
                var blockFace2 = (BlockFace)_fBlockFace.GetValue(__instance);
                var playerId2  = (int)_fPlayerId.GetValue(__instance);

                Log.Out($"[PaintUnlocked] ProcessPackagePrefix: applying ReadPrefix-decoded idx={fullIdx} at {blockPos2} face={blockFace2}");
                ApplyTexture(_world, blockPos2, blockFace2, fullIdx, playerId2);
                return false;
            }

            // Overflow flag still set -- ReadPrefix didn't fire (server virtual dispatch issue)
            var idxByte = (byte)_fIdx.GetValue(__instance);
            ushort decodedIdx = (ushort)(((channel & 0x7F) << 8) | idxByte);

            var blockPos  = (Vector3i)_fBlockPos.GetValue(__instance);
            var blockFace = (BlockFace)_fBlockFace.GetValue(__instance);
            var playerId  = (int)_fPlayerId.GetValue(__instance);

            Log.Out($"[PaintUnlocked] ProcessPackagePrefix: server-side overflow decode channel=0x{channel:X2} idx=0x{idxByte:X2} -> fullIdx={decodedIdx} at {blockPos} face={blockFace}");

            ApplyTexture(_world, blockPos, blockFace, decodedIdx, playerId);
            return false; // skip vanilla ProcessPackage
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] ProcessPackagePrefix failed: {ex.Message}");
            return true; // fall through to vanilla on error
        }
    }

    private static void ApplyTexture(World _world, Vector3i blockPos, BlockFace blockFace, ushort idx, int playerId)
    {
        // Mirror what vanilla ProcessPackage does: set the texture on the block face
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
        // Let vanilla read() handle the bytes. We decode overflow in ProcessPackagePrefix.
        // ReadPrefix was unreliable on dedicated server due to virtual dispatch anyway.
        return true;
    }
}
