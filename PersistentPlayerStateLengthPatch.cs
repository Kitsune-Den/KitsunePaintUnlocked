using HarmonyLib;

/// <summary>
/// Patches NetPackagePersistentPlayerState.GetLength to return a larger buffer size.
///
/// Vanilla returns 1000 bytes. With many custom paint IDs registered, the player's
/// PersistentPlayerData grows larger and overflows the 1000-byte limit, causing
/// stream desync and Unknown NetPackage ID errors after the player spawns.
///
/// We return 65536 (64KB) which is well above any realistic player data size.
/// The server allocates this buffer, reads data into it, then passes to read().
/// Returning a larger value is safe - unused buffer space is just zeroed.
/// </summary>
[HarmonyPatch(typeof(NetPackagePersistentPlayerState), "GetLength")]
public static class PersistentPlayerStateLengthPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref int __result)
    {
        if (__result == 1000)
        {
            __result = 65536;
            Log.Out("[PaintUnlocked] NetPackagePersistentPlayerState.GetLength patched: 1000 -> 65536");
        }
    }
}
