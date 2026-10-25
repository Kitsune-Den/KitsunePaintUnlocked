using HarmonyLib;
using System.Reflection;

/// <summary>
/// Patches OpaqueTextures.GetFreePaintID() to ensure server and client assign
/// identical custom paint IDs by using a fixed floor (PaintIdLimits.CustomIdFloor).
///
/// Problem: stock OCB hands out the first empty slot, so custom IDs depend on
/// what else was loaded. Without a fixed floor, custom paint IDs diverge between
/// server and client, causing painted blocks to show wrong textures on the client.
///
/// Solution: Both sides start custom paints at the floor (256), leaving 0-255 to
/// vanilla (0-183) and painting.xml mods. Custom packs then get identical IDs on
/// both server and client. The floor deliberately ignores builtinOpaques: that is
/// the client's GPU texture-array depth (407), not a paint count, and a paint's
/// texture slot (TextureID) is independent of its paint ID.
///
/// IDs above PaintIdLimits.MaxPaintId are still handed out so OCB's registration
/// loop doesn't crash; PaintIdSyncManager drops those paints right after
/// InitOpaqueConfig, since a block face can't store them.
/// </summary>
[HarmonyPatch(typeof(OpaqueTextures), "GetFreePaintID")]
public static class OcbPaintLimitPatch
{
    private const int CustomIdFloor = PaintIdLimits.CustomIdFloor;

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

            // Detect world reload: if BlockTextureData.list was recreated (our entries are gone),
            // reset the counter so IDs start from the floor again. Without this, quit-to-menu +
            // reload on listen servers assigns IDs past the first batch, causing empty paint menus.
            if (_nextId >= CustomIdFloor && BlockTextureData.list != null
                && (_nextId >= BlockTextureData.list.Length || BlockTextureData.list[CustomIdFloor] == null))
            {
                Log.Out($"[PaintUnlocked] GetFreePaintID: world reload detected (list[{CustomIdFloor}] is null), resetting counter");
                _nextId = -1;
            }

            if (_nextId < 0)
            {
                // Wait until builtinOpaques is set (>= 0, including 0 for dedicated server)
                if (builtin < 0)
                    return true; // vanilla init not done yet

                // Use fixed floor so server and client always match
                _nextId = CustomIdFloor;
                Log.Out($"[PaintUnlocked] GetFreePaintID seeded at {_nextId} (custom paints get IDs {CustomIdFloor}-{PaintIdLimits.MaxPaintId}).");
            }

            // Never hand out a slot something already holds, e.g. a painting.xml mod that
            // defines its own paint at a fixed ID inside the custom range.
            var list = BlockTextureData.list;
            while (list != null && _nextId < list.Length && list[_nextId] != null)
                _nextId++;

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

    /// <summary>
    /// Resets the sequential counter to a new value.
    /// Called by PaintIdSyncManager after remapping client IDs to match the server,
    /// so any further allocations continue from the correct slot.
    /// </summary>
    public static void ResetCounterTo(int newNextId)
    {
        lock (_lock)
        {
            _nextId = newNextId;
            Log.Out($"[PaintUnlocked] GetFreePaintID counter reset to {newNextId}");
        }
    }
}
