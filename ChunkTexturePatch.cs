using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

/// <summary>
/// Patches Chunk methods to use 10 bits per face instead of 8 bits,
/// raising the paint index limit from 255 to 1023.
///
/// Methods patched:
/// - SetBlockFaceTexture: stores one face's paint index (10-bit write)
/// - GetBlockFaceTexture: retrieves one face's paint index (10-bit read)
/// - Value64FullToIndex: extracts face paint index from Int64 for renderer (10-bit read + clamp)
///
/// Requires ChunkBlockChannel.bytesPerVal to be 8 (64 bits) instead of vanilla's 6 (48 bits).
/// The bytesPerVal patch is applied via ChunkStorageWidthPatch.
/// </summary>
public static class ChunkTexturePatch
{
    private const int NewMask = 0x3FF;   // 10-bit = 1023 max
    private const int NewShiftMultiplier = 10;  // 6 faces × 10 = 60 bits (fits in 64-bit with bytesPerVal=8)

    [HarmonyPatch(typeof(Chunk), "SetBlockFaceTexture")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> PatchSet(IEnumerable<CodeInstruction> instructions)
    {
        return PatchMaskAndShift(instructions, "SetBlockFaceTexture");
    }

    [HarmonyPatch(typeof(Chunk), "GetBlockFaceTexture")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> PatchGet(IEnumerable<CodeInstruction> instructions)
    {
        return PatchMaskAndShift(instructions, "GetBlockFaceTexture");
    }

    [HarmonyPatch(typeof(Chunk), "Value64FullToIndex")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> PatchValue64ToIndex(IEnumerable<CodeInstruction> instructions)
    {
        return PatchMaskAndShift(instructions, "Value64FullToIndex");
    }

    /// <summary>
    /// Clamps result to valid BlockTextureData.list range.
    /// Handles old 8-bit world data read with 10-bit decoder (garbage indices).
    /// </summary>
    [HarmonyPatch(typeof(Chunk), "Value64FullToIndex")]
    [HarmonyPostfix]
    public static void ClampValue64Result(ref int __result)
    {
        var list = BlockTextureData.list;
        if (list != null && (__result < 0 || __result >= list.Length || list[__result] == null))
        {
            __result = 0;
        }
    }

    private static IEnumerable<CodeInstruction> PatchMaskAndShift(
        IEnumerable<CodeInstruction> instructions, string methodName)
    {
        var codes = new List<CodeInstruction>(instructions);
        int patched = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_8)
            {
                codes[i] = new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)NewShiftMultiplier);
                patched++;
            }
            else if (codes[i].opcode == OpCodes.Ldc_I4 && codes[i].operand is int val && val == 0xFF)
            {
                codes[i] = new CodeInstruction(OpCodes.Ldc_I4, NewMask);
                patched++;
            }
        }

        if (patched > 0)
            Log.Out($"[PaintUnlocked] {methodName}: patched {patched} constants (8-bit -> 10-bit)");
        else
            Log.Warning($"[PaintUnlocked] {methodName}: no constants patched - check IL");

        return codes;
    }
}

/// <summary>
/// Widens ChunkBlockChannel storage from 6 bytes (48 bits) to 8 bytes (64 bits) per block.
/// This is required for 10-bit face storage (6 faces × 10 bits = 60 bits).
///
/// The ChunkBlockChannel.Get/Set methods already use bytesPerVal dynamically —
/// we just need to change the value from 6 to 8 at construction time.
/// </summary>
public static class ChunkStorageWidthPatch
{
    /// <summary>
    /// Prefix on ChunkBlockChannel constructor. Changes bytesPerVal from 6 to 8
    /// for texture channels, widening storage from 48 to 64 bits per block.
    /// </summary>
    public static void CtorPrefix(ref int _bytesPerVal)
    {
        if (_bytesPerVal == 6)
        {
            _bytesPerVal = 8;
            Log.Out("[PaintUnlocked] ChunkBlockChannel: bytesPerVal 6 -> 8 (48-bit -> 64-bit storage)");
        }
    }
}
