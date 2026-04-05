using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;

/// <summary>
/// Removes the conv.u1 (byte truncation) in XUiC_MaterialStack.SetSelectedTextureForItem
/// that truncates paint IDs above 255 before storing them in itemValue.Meta.
///
/// The game does: Meta = (byte)textureData.ID — which destroys bits 8+ of the paint ID.
/// Paint 514 becomes Meta=2, Paint 600 becomes Meta=88, etc.
/// The toolbar then reads BlockTextureData.list[truncated_id] and shows the wrong texture.
///
/// Fix: replace conv.u1 with nop. Meta is Int32, no byte conversion needed.
/// </summary>
public static class MetaTruncationPatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int patched = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            // Look for: ldfld ID → conv.u1 → stfld Meta
            // Replace conv.u1 with nop
            if (codes[i].opcode == OpCodes.Conv_U1
                && i + 1 < codes.Count
                && codes[i + 1].opcode == OpCodes.Stfld
                && codes[i + 1].operand?.ToString()?.Contains("Meta") == true)
            {
                codes[i] = new CodeInstruction(OpCodes.Nop);
                patched++;
                Log.Out($"[PaintUnlocked] SetSelectedTextureForItem: removed conv.u1 before Meta assignment (IL[{i}])");
            }
        }

        if (patched == 0)
            Log.Warning("[PaintUnlocked] SetSelectedTextureForItem: conv.u1 before Meta not found - check IL");

        return codes;
    }
}
