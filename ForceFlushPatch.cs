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
    /// Prefix on World.Save. Iterate every chunk cluster, every loaded chunk
    /// in each, and force isModified = true so the save pass writes them all.
    /// </summary>
    public static void WorldSavePrefix(World __instance)
    {
        if (__instance == null) return;
        var clusters = __instance.ChunkClusters;
        if (clusters == null) return;

        int chunksMarked = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            if (cluster == null) continue;

            List<Chunk> chunks = null;
            try { chunks = cluster.GetChunkArrayCopySync(); }
            catch { chunks = null; }
            if (chunks == null) continue;

            for (int j = 0; j < chunks.Count; j++)
            {
                var ch = chunks[j];
                if (ch == null) continue;
                if (!ch.isModified)
                {
                    ch.isModified = true;
                    chunksMarked++;
                }
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
