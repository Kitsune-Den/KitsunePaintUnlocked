using HarmonyLib;
using System.Collections.Generic;

/// <summary>
/// Save guard: resolve the async chunk-save race condition that loses paints.
///
/// The race:
///   1. Background save thread starts Chunk.save() → calls write() to serialize
///   2. MAIN THREAD paints a face mid-save → SetBlockFaceTexture stores data,
///      our ChunkPaintDirtyPatch sets isModified = true
///   3. Background thread finishes write() → vanilla code sets isModified = false
///   4. The paint from step 2 is in memory (chnTextures) but wasn't in the
///      written stream. Next save cycle sees isModified = false → skips chunk.
///   5. Paint is lost until something else marks the chunk dirty.
///
/// This patch detects paint operations that happen during a chunk's save and
/// forces isModified back to true after vanilla's clear, so the next save
/// cycle picks up the missed paint.
///
/// Per-chunk paint counter:
///   - SetBlockFaceTexture / SetTextureFull / GetSetTextureFullArray postfixes
///     increment the chunk's paint counter
///   - Chunk.save prefix records the counter at save start
///   - Chunk.save postfix checks if counter changed during save; if so,
///     re-marks chunk dirty (overriding vanilla's isModified = false)
/// </summary>
public static class ChunkSaveGuardPatch
{
    // Per-chunk paint operation counter (chunk Key -> count)
    private static readonly Dictionary<long, int> _paintCounter = new Dictionary<long, int>();

    // Counter value captured at start of each chunk's save
    private static readonly Dictionary<long, int> _saveStartCounter = new Dictionary<long, int>();

    private static readonly object _lock = new object();

    private static int _raceDetectedCount = 0;

    /// <summary>
    /// Called from ChunkPaintDirtyPatch postfixes when any paint op fires.
    /// Increments the chunk's paint counter so we can detect mid-save paints.
    /// </summary>
    public static void NotePaintOp(Chunk chunk)
    {
        if (chunk == null) return;
        lock (_lock)
        {
            _paintCounter.TryGetValue(chunk.Key, out var current);
            _paintCounter[chunk.Key] = current + 1;
        }
    }

    /// <summary>
    /// Prefix on Chunk.save ~ snapshot the chunk's paint counter so the postfix
    /// can detect any paint ops that happen during the save.
    /// </summary>
    public static void SavePrefix(Chunk __instance)
    {
        if (__instance == null) return;
        lock (_lock)
        {
            _paintCounter.TryGetValue(__instance.Key, out var current);
            _saveStartCounter[__instance.Key] = current;
        }
    }

    /// <summary>
    /// Postfix on Chunk.save ~ vanilla code just set isModified = false.
    /// If any paint op happened during the save (counter changed from snapshot),
    /// the paint data didn't make it into the written stream. Override the
    /// false back to true so the next save cycle catches the missed paint.
    /// </summary>
    public static void SavePostfix(Chunk __instance)
    {
        if (__instance == null) return;
        lock (_lock)
        {
            _paintCounter.TryGetValue(__instance.Key, out var current);
            _saveStartCounter.TryGetValue(__instance.Key, out var atStart);

            if (current != atStart)
            {
                __instance.isModified = true;
                _raceDetectedCount++;
                if (_raceDetectedCount <= 5 || _raceDetectedCount % 25 == 0)
                {
                    Log.Out($"[PaintUnlocked] SaveGuard: race detected on chunk ({__instance.X},{__instance.Z}), forcing dirty (total caught: {_raceDetectedCount})");
                }
            }

            // Clear the save-start snapshot to free memory
            _saveStartCounter.Remove(__instance.Key);
        }
    }
}
