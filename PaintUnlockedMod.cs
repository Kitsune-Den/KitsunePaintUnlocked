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

        // Patch NetPackagePersistentPlayerState buffer size (1000 -> 65536)
        var getPpsLen = AccessTools.Method(typeof(NetPackagePersistentPlayerState), "GetLength");
        var getPpsLenPostfix = AccessTools.Method(typeof(PersistentPlayerStateLengthPatch), "Postfix");
        harmony.Patch(getPpsLen, postfix: new HarmonyMethod(getPpsLenPostfix));

        Log.Out("[PaintUnlocked] Loaded - paint texture limit removed (byte -> ushort, chunk storage 8-bit -> 10-bit).");
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

    public static bool WritePrefix(NetPackageSetBlockTexture __instance, PooledBinaryWriter _bw)
    {
        if (!_reflectionValid) return true;

        try
        {
            var blockPos  = (Vector3i)_fBlockPos.GetValue(__instance);
            var blockFace = (BlockFace)_fBlockFace.GetValue(__instance);
            var playerId  = (int)_fPlayerId.GetValue(__instance);
            var channel   = (byte)_fChannel.GetValue(__instance);
            var idx       = LoadIdx(__instance);

            if (idx > 255)
                Log.Out($"[PaintUnlocked] WritePrefix: idx={idx} pos={blockPos} face={blockFace} channel={channel} -> encoding as overflow");
            else
                Log.Out($"[PaintUnlocked] WritePrefix: idx={idx} pos={blockPos} face={blockFace} channel={channel} -> encoding as byte");

            _writeInt.Invoke(_bw, new object[] { blockPos.x });
            _writeInt.Invoke(_bw, new object[] { blockPos.y });
            _writeInt.Invoke(_bw, new object[] { blockPos.z });
            _writeByte.Invoke(_bw, new object[] { (byte)blockFace });
            _writeInt.Invoke(_bw, new object[] { playerId });

            if (idx > 255)
            {
                byte channelWire = (byte)(OverflowFlag | ((idx >> 8) & 0x7F));
                _writeByte.Invoke(_bw, new object[] { channelWire });
                _writeByte.Invoke(_bw, new object[] { (byte)(idx & 0xFF) });
                Log.Out($"[PaintUnlocked] WritePrefix: channelWire=0x{channelWire:X2} idxByte=0x{(idx & 0xFF):X2}");
            }
            else
            {
                _writeByte.Invoke(_bw, new object[] { channel });
                _writeByte.Invoke(_bw, new object[] { (byte)idx });
            }

            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] WritePrefix failed: {ex.Message}");
            return true;
        }
    }

    public static bool ReadPrefix(NetPackageSetBlockTexture __instance, PooledBinaryReader _br)
    {
        if (!_reflectionValid) return true;

        try
        {
            _fBlockPos.SetValue(__instance, new Vector3i(_br.ReadInt32(), _br.ReadInt32(), _br.ReadInt32()));
            _fBlockFace.SetValue(__instance, (BlockFace)_br.ReadByte());
            _fPlayerId.SetValue(__instance, _br.ReadInt32());

            var channelByte = _br.ReadByte();
            var idxByte     = _br.ReadByte();

            if ((channelByte & OverflowFlag) != 0)
            {
                _fChannel.SetValue(__instance, (byte)0);
                ushort idx = (ushort)(((channelByte & 0x7F) << 8) | idxByte);
                Log.Out($"[PaintUnlocked] ReadPrefix: overflow decode channelByte=0x{channelByte:X2} idxByte=0x{idxByte:X2} -> idx={idx}");
                StoreIdx(__instance, idx);
            }
            else
            {
                _fChannel.SetValue(__instance, channelByte);
                Log.Out($"[PaintUnlocked] ReadPrefix: normal decode channelByte={channelByte} idxByte={idxByte}");
                StoreIdx(__instance, idxByte);
            }

            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] ReadPrefix failed: {ex.Message}");
            return false;
        }
    }
}
