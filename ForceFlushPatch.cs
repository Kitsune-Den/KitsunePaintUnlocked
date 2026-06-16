using HarmonyLib;
using System.Collections.Generic;

/// <summary>
/// Force-flush all loaded chunks on critical save transitions.
///
/// Vanilla 7DTD has an intermittent bug where painted faces occasionally
/// don't save. The chunk dirty-flag postfix (ChunkPaintDirtyPatch) handles
/// the common case where paint operations don't mark the chunk dirty.
///
/// This patch is a second layer of defense: on World.Save (the explicit
/// `saveworld` console command + automatic periodic saves) we mark EVERY
/// loaded chunk as modified before the save runs. The save then writes
/// all of them regardless of dirty flag state, catching any paint that
/// slipped past the dirty-flag tracking due to async/buffer/race issues.
///
/// Cost: some chunks that weren't actually modified get re-serialized.
/// On modern hardware this is negligible (small fraction of a second for
/// typical loaded chunk counts). Tradeoff is worth it for guaranteed
/// paint persistence.
///
/// Apply via: harmony.Patch(World.Save, prefix = MarkAllChunksDirty)
/// </summary>
public static class ForceFlushPatch
{
    private static int _flushCount = 0;

    /// <summary>
    /// Prefix on World.Save. Iterate every loaded chunk and force
    /// isModified = true so the save pass writes them all.
    ///
    /// v3.0 removed World.ChunkClusters / ChunkClusterList (and ChunkCluster
    /// lost GetChunkArrayCopySync). Loaded chunks are now enumerated via
    /// ChunkManager.GetActiveChunkSet() — chunk keys — each resolved to a Chunk
    /// through World.GetChunkSync(long), which returns the IChunk for that key.
    /// </summary>
    public static void WorldSavePrefix(World __instance)
    {
        if (__instance == null) return;
        var chunkManager = __instance.m_ChunkManager;
        if (chunkManager == null) return;

        int chunksMarked = 0;
        foreach (long key in chunkManager.GetActiveChunkSet())
        {
            if (!(__instance.GetChunkSync(key) is Chunk ch)) continue;
            if (!ch.isModified)
            {
                ch.isModified = true;
                chunksMarked++;
            }
        }

        // Log only occasionally to avoid spam on autosaves
        _flushCount++;
        if (chunksMarked > 0 && (_flushCount <= 3 || _flushCount % 20 == 0))
        {
            Log.Out($"[PaintUnlocked] ForceFlush: marked {chunksMarked} chunks dirty before World.Save (call #{_flushCount})");
        }
    }
}
