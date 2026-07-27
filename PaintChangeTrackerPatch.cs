using HarmonyLib;

/// <summary>
/// Diagnostic: logs every paint change on a block face.
///
/// Tracks every call to Chunk.SetBlockFaceTexture and logs:
///   - World position of the block
///   - Which face changed
///   - Old paint ID -> new paint ID
///
/// Helps identify "random paint shifts" by letting us correlate a visual
/// glitch with a specific log entry showing what code path mutated the paint.
///
/// To investigate a suspicious block:
///   1. Note its rough world position (look in inventory / debug menu)
///   2. Grep the output log for "[PaintChange]" lines matching that position
///   3. Each entry has a timestamp; correlate with player actions / quest events
///
/// Log is throttled at 5000 entries per session to avoid runaway log size
/// during heavy paint activity (mass paint, prefab placement).
/// </summary>
public static class PaintChangeTrackerPatch
{
    private const int LogLimit = 5000;
    private static int _logCount = 0;
    private static bool _limitReached = false;

    /// <summary>
    /// Prefix on Chunk.SetBlockFaceTexture: read the current paint ID before
    /// the vanilla method overwrites it, stash it for the postfix.
    /// </summary>
    public static void TrackPrefix(Chunk __instance, int _x, int _y, int _z, BlockFace _face,
        int channel, out int __state)
    {
        __state = 0;
        if (!PaintVerbose.Enabled) return;
        if (__instance == null) return;
        try
        {
            __state = __instance.GetBlockFaceTexture(_x, _y, _z, _face, channel);
        }
        catch
        {
            __state = -1;
        }
    }

    /// <summary>
    /// Postfix on Chunk.SetBlockFaceTexture: compare old vs new and log if changed.
    /// </summary>
    public static void TrackPostfix(Chunk __instance, int _x, int _y, int _z, BlockFace _face,
        int _texture, int channel, int __state)
    {
        if (!PaintVerbose.Enabled) return;
        if (__instance == null) return;
        if (_limitReached) return;

        // Only log actual changes (skip no-ops where the value didn't change)
        if (__state == _texture) return;

        // Compute world position from chunk-local + chunk world coords
        int wx = __instance.X * 16 + _x;
        int wz = __instance.Z * 16 + _z;

        Log.Out($"[PaintChange] pos=({wx},{_y},{wz}) face={_face} old={__state} new={_texture}");

        _logCount++;
        if (_logCount >= LogLimit)
        {
            _limitReached = true;
            Log.Out($"[PaintChange] Log limit ({LogLimit}) reached; further changes suppressed for this session.");
        }
    }
}
