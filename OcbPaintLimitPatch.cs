using HarmonyLib;
using System.Reflection;

/// <summary>
/// Patches OpaqueTextures.GetFreePaintID() to ensure server and client assign
/// identical custom paint IDs by using a fixed floor of 512.
///
/// Problem: Server loads ~155 vanilla paints, client loads ~407. Without a fixed
/// floor, custom paint IDs diverge between server and client, causing painted
/// blocks to show wrong textures on the client side.
///
/// Solution: Both sides skip to ID 512 before assigning any custom paint IDs,
/// leaving 0-511 as vanilla reserved. Custom packs then get identical IDs on
/// both server and client regardless of how many vanilla paints each side loaded.
/// </summary>
[HarmonyPatch(typeof(OpaqueTextures), "GetFreePaintID")]
public static class OcbPaintLimitPatch
{
    private const int CustomIdFloor = 512;

    private static readonly FieldInfo _fBuiltinOpaques =
        typeof(OpaqueTextures).GetField("builtinOpaques",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

    private static readonly FieldInfo _fOpaqueConfigs =
        typeof(OpaqueTextures).GetField("OpaqueConfigs",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

    // -1 = not yet seeded, >= 0 = our counter is active
    private static int _nextId = -1;
    private static readonly object _lock = new object();

    [HarmonyPrefix]
    public static bool GetFreePaintIDPrefix(ref int __result)
    {
        if (_fOpaqueConfigs == null || _fBuiltinOpaques == null)
            return true;

        lock (_lock)
        {
            int builtin = (int)_fBuiltinOpaques.GetValue(null);

            if (_nextId < 0)
            {
                // Wait until builtinOpaques is set (>= 0, including 0 for dedicated server)
                if (builtin < 0)
                    return true; // vanilla init not done yet

                // Use fixed floor so server and client always match
                _nextId = System.Math.Max(builtin, CustomIdFloor);
                Log.Out($"[PaintUnlocked] GetFreePaintID seeded at {_nextId} (vanilla count: {builtin}, floor: {CustomIdFloor}). Custom paint limit removed.");
            }

            if (_nextId >= 65535)
            {
                Log.Error("[PaintUnlocked] Paint ID space exhausted (65535 slots).");
                __result = -1;
                return false;
            }

            __result = _nextId;
            _nextId++;

            return false;
        }
    }
}
