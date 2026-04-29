using System;
using System.Diagnostics;

/// <summary>
/// Performs synchronous, up-front migration of every chunk on disk from vanilla
/// 8-bit-per-face paint storage (bytesPerVal=6) to PaintUnlocked's 10-bit-per-face
/// storage (bytesPerVal=8). Runs once when a legacy world is first loaded.
///
/// Why eager rather than lazy: 7DTD's chunk lifecycle includes auto-save-on-cache-drop.
/// During play, painted chunks may be migrated in memory, dropped, saved to disk at
/// bpv=8, then re-streamed back in. With lazy migration still active, the re-read
/// would flip bpv to 6 and read width-8 disk data as width-6 — past-end-of-stream error,
/// chunk lost, blank regeneration.
///
/// Eager migration loads every chunk on disk through the existing migration patches,
/// writes each one back at width 8 immediately, and only then marks the world as
/// migrated. After that, NeedsMigration is false for the rest of the session and the
/// per-chunk read patches become no-ops.
///
/// Hooked from RegionFileManager's constructor postfix — at that point the disk
/// manifest (chunksInLoadDir, chunksInSaveDir) is populated but normal chunk
/// streaming hasn't started yet.
/// </summary>
public static class EagerMigrator
{
    private static bool _hasRun = false;

    /// <summary>
    /// Reset per-world state so a second world load (after unload) re-runs the check.
    /// </summary>
    public static void Reset()
    {
        _hasRun = false;
        ChunkBlockChannelReadPatch.ResetDiagnostics();
    }

    /// <summary>
    /// Postfix on RegionFileManager constructor. Runs synchronously, blocking the
    /// world-load coroutine until migration completes.
    /// </summary>
    public static void OnRegionFileManagerConstructed(RegionFileManager __instance)
    {
        if (_hasRun) return;
        _hasRun = true;

        // Authoritative side only — pure clients receive chunks over the network at
        // PaintUnlocked's current width and never read them from disk.
        var cm = ConnectionManager.Instance;
        if (cm != null && !cm.IsServer)
        {
            Log.Out("[PaintUnlocked] EagerMigrator: pure client, skipping (chunks arrive via network).");
            return;
        }

        if (!WorldMigrationState.NeedsMigration)
        {
            Log.Out("[PaintUnlocked] EagerMigrator: NeedsMigration=false, nothing to do.");
            return;
        }

        try
        {
            RunMigration(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] EagerMigrator: top-level crash, sentinel NOT written: {ex.Message}");
            Log.Exception(ex);
        }
    }

    private static void RunMigration(RegionFileManager rfm)
    {
        var sw = Stopwatch.StartNew();

        long[] keys;
        try
        {
            keys = rfm.GetAllChunkKeys();
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] EagerMigrator: GetAllChunkKeys failed: {ex.Message}");
            return;
        }

        if (keys == null || keys.Length == 0)
        {
            Log.Out("[PaintUnlocked] EagerMigrator: no chunks on disk — marking world as migrated.");
            WorldMigrationState.MarkMigrated();
            return;
        }

        Log.Out($"[PaintUnlocked] EagerMigrator: starting scan of {keys.Length} chunks. World load will pause until complete.");

        int processed = 0, migrated = 0, skipped = 0, failed = 0;
        const int diagLimit = 5;

        for (int i = 0; i < keys.Length; i++)
        {
            long key = keys[i];
            int cX = WorldChunkCache.extractX(key);
            int cZ = WorldChunkCache.extractZ(key);
            int beforeMigCount = ChunkMigrationPatch.MigratedChunkCount;

            Chunk chunk = null;
            try
            {
                chunk = rfm.GetChunkSync(key);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[PaintUnlocked] EagerMigrator: GetChunkSync threw for chunk ({cX},{cZ}): {ex.Message}");
                processed++;
                continue;
            }

            if (chunk == null)
            {
                // The engine couldn't load this one (corrupt region, etc.). It will
                // already have written an error_backup_*.bak and removed the chunk
                // from the region file.
                failed++;
                if (i < diagLimit)
                    Log.Warning($"[PaintUnlocked] EagerMigrator[{i}]: chunk ({cX},{cZ}) returned NULL from GetChunkSync");
                processed++;
                continue;
            }

            // Detect whether ChunkMigrationPatch.ReadPostfix actually ran for this
            // chunk by comparing the migration counter. We can't trust chunk.isModified
            // because Chunk.load() resets it to false AFTER read returns.
            bool wasJustMigrated = ChunkMigrationPatch.MigratedChunkCount > beforeMigCount;

            if (i < diagLimit)
            {
                Log.Out($"[PaintUnlocked] EagerMigrator[{i}]: chunk ({cX},{cZ}) loaded. wasJustMigrated={wasJustMigrated}");
            }

            try
            {
                // Always flush in eager mode — for migrated chunks this writes the
                // bpv=8 form back; for already-bpv=8 chunks (idempotent re-runs) it
                // rewrites the same data. Cheap insurance.
                if (TryFlushChunkToDisk(rfm, chunk, key))
                {
                    if (wasJustMigrated) migrated++;
                    else skipped++;
                }
                else
                {
                    failed++;
                    if (i < diagLimit)
                        Log.Warning($"[PaintUnlocked] EagerMigrator[{i}]: chunk ({cX},{cZ}) flush returned false");
                }
                chunk.isModified = false;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[PaintUnlocked] EagerMigrator: chunk ({cX},{cZ}) flush failed: {ex.Message}");
            }

            processed++;
            if (processed % 100 == 0 || processed == keys.Length)
            {
                Log.Out($"[PaintUnlocked] EagerMigrator: {processed}/{keys.Length} ({migrated} migrated, {skipped} already-at-width-8, {failed} failed)");
            }
        }

        sw.Stop();
        Log.Out($"[PaintUnlocked] EagerMigrator: scan complete in {sw.ElapsedMilliseconds}ms. {processed}/{keys.Length} processed, {migrated} migrated, {skipped} skipped, {failed} failed.");

        if (failed == 0)
        {
            WorldMigrationState.MarkMigrated();
        }
        else
        {
            Log.Warning($"[PaintUnlocked] EagerMigrator: {failed} chunks failed — sentinel NOT written, migration will retry on next world load.");
        }
    }

    /// <summary>
    /// Snapshot the chunk and write it to its region file synchronously, bypassing
    /// the background save thread. Returns true on success.
    /// </summary>
    private static bool TryFlushChunkToDisk(RegionFileManager rfm, Chunk chunk, long key)
    {
        if (rfm.snapshotUtil == null)
        {
            Log.Warning("[PaintUnlocked] EagerMigrator: snapshotUtil is null — cannot flush chunks.");
            return false;
        }

        IRegionFileChunkSnapshot snapshot = null;
        try
        {
            snapshot = rfm.snapshotUtil.TakeSnapshot(chunk, true);
            if (snapshot == null) return false;

            int cX = WorldChunkCache.extractX(key);
            int cZ = WorldChunkCache.extractZ(key);
            rfm.snapshotUtil.WriteSnapshot(snapshot, rfm.saveDirectory, cX, cZ);
            return true;
        }
        finally
        {
            if (snapshot != null)
            {
                try { rfm.snapshotUtil.Free(snapshot); }
                catch (Exception ex) { Log.Warning($"[PaintUnlocked] EagerMigrator: snapshot free failed: {ex.Message}"); }
            }
        }
    }
}
