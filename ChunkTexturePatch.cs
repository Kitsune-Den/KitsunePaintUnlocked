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
/// The clamp on Value64FullToIndex handles backward compatibility with old 8-bit world data:
/// if the decoded value is out of BlockTextureData.list range, it clamps to 0 (default paint).
/// </summary>
public static class ChunkTexturePatch
{
    private const int NewMask = 0x3FF;
    private const int NewShiftMultiplier = 10;

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
    /// Postfix on Value64FullToIndex: clamps result to valid BlockTextureData.list range.
    /// This handles old world data stored with 8-bit encoding - when read with 10-bit decoder
    /// the result may be garbage (e.g. 728, 2048). Clamping to list bounds prevents crashes.
    /// Old paints (0-255) stored correctly will still decode correctly since bits 8-9 were 0.
    /// </summary>
    [HarmonyPatch(typeof(Chunk), "Value64FullToIndex")]
    [HarmonyPostfix]
    public static void ClampValue64Result(ref int __result)
    {
        var list = BlockTextureData.list;
        if (list != null && (__result < 0 || __result >= list.Length || list[__result] == null))
        {
            __result = 0; // default to paint 0 (vanilla first paint)
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
