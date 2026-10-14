using System.Collections.Generic;
using System.IO;

/// <summary>
/// Tracks whether the currently loaded world needs to be migrated from vanilla's
/// 8-bit-per-face paint storage (bytesPerVal=6) to PaintUnlocked's 10-bit-per-face
/// storage (bytesPerVal=8).
///
/// Detection uses a sentinel file at the save root:
///   &lt;SaveDir&gt;/paintunlocked.migrated
///
/// - Sentinel MISSING + chunk files exist → legacy world, migration is active.
///   Chunks load at width 6, then a postfix repacks them to width 8.
/// - Sentinel MISSING + no chunk files     → brand-new world. Sentinel is written
///   immediately so subsequent loads (which will have width-8 chunks on disk)
///   don't get mis-flagged as legacy.
/// - Sentinel PRESENT → world is already migrated. Normal PaintUnlocked operation,
///   chunks load at width 8 directly.
///
/// On a real legacy world, the sentinel is written after the first successful save
/// following at least one chunk migration.
/// </summary>
public static class WorldMigrationState
{
    private const string SentinelFileName = "paintunlocked.migrated";

    /// <summary>
    /// Append-only list of chunk keys (Int64, little-endian) that the eager migration has
    /// already rewritten at width 8. Chunk data on disk doesn't record its own width, so
    /// without this a migration that was interrupted (game killed while it looked frozen)
    /// re-reads the already-converted chunks as legacy width 6 on the next load, they fail
    /// with "read past the end of the stream" and the engine deletes them.
    /// Deleted once the sentinel is written.
    /// </summary>
    private const string JournalFileName = "paintunlocked.migrating";

    private static bool _needsMigration = false;
    private static bool _flagLoaded = false;
    private static string _saveDir = null;

    private static readonly HashSet<long> _converted = new HashSet<long>();
    private static FileStream _journal;

    /// <summary>
    /// True when chunks loading from disk should be treated as legacy 8-bit and
    /// repacked to 10-bit. Reset on every world load.
    /// </summary>
    public static bool NeedsMigration => _needsMigration;

    /// <summary>
    /// Absolute path to the save folder for the currently loaded world, or null
    /// if no world has been loaded yet.
    /// </summary>
    public static string SaveDir => _saveDir;

    /// <summary>
    /// Read the sentinel file for the given save directory and cache the result.
    /// Called on world load. Safe to call multiple times — only the first call
    /// per world actually hits the filesystem.
    /// </summary>
    public static void LoadFlag(string saveDir)
    {
        if (string.IsNullOrEmpty(saveDir))
        {
            _needsMigration = false;
            _flagLoaded = true;
            _saveDir = null;
            return;
        }

        _saveDir = saveDir;
        var sentinelPath = Path.Combine(saveDir, SentinelFileName);

        try
        {
            if (File.Exists(sentinelPath))
            {
                _needsMigration = false;
                Log.Out($"[PaintUnlocked] Migration sentinel found — world is 10-bit, no migration needed: {sentinelPath}");
                _flagLoaded = true;
                return;
            }

            // Fresh-world detection. A save dir that doesn't exist yet, or exists with
            // no .ttc chunk files anywhere under it, is a brand-new world. Chunks
            // generated this session will be written at width 8. We must mark the
            // world as migrated NOW so the next load doesn't see a missing sentinel
            // and try to read those width-8 chunks as legacy width-6 (corruption).
            if (!Directory.Exists(saveDir) || !HasAnyChunkFile(saveDir))
            {
                _needsMigration = false;
                try
                {
                    if (!Directory.Exists(saveDir))
                        Directory.CreateDirectory(saveDir);
                    File.WriteAllText(sentinelPath, "PaintUnlocked: fresh world, no migration needed.\n");
                    Log.Out($"[PaintUnlocked] Fresh world detected (no chunk files on disk) — sentinel written, migration skipped.");
                }
                catch (System.Exception ex)
                {
                    // Couldn't write the sentinel yet (save dir may not exist until
                    // first save). It's safe to leave it unwritten: we'll re-detect
                    // as fresh on next load too, since chunks won't be on disk if
                    // this save was never persisted.
                    Log.Warning($"[PaintUnlocked] Fresh world sentinel write deferred: {ex.Message}");
                }
                _flagLoaded = true;
                return;
            }

            _needsMigration = true;
            Log.Warning($"[PaintUnlocked] Migration sentinel NOT found — treating world as legacy 8-bit. Chunks will be repacked as they load.");
            Log.Warning($"[PaintUnlocked] Sentinel path: {sentinelPath}");
            LoadJournal();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to check migration sentinel: {ex.Message}");
            _needsMigration = false; // Fail safe: assume already migrated
        }

        _flagLoaded = true;
    }

    /// <summary>
    /// Detect whether this save dir contains chunk data on disk that would need
    /// migration. We check the Region/ subdirectory for any file — 7DTD writes
    /// region files there as r.X.Z.7rg (current format). Using directory presence
    /// rather than a specific extension makes this robust to format changes.
    ///
    /// On error, returns true so we err toward running migration. Migration is
    /// idempotent for already-width-8 chunks (bytesPerVal==6 is the per-channel marker).
    /// </summary>
    private static bool HasAnyChunkFile(string saveDir)
    {
        try
        {
            var regionDir = Path.Combine(saveDir, "Region");
            if (!Directory.Exists(regionDir)) return false;

            using (var enumerator = Directory.EnumerateFiles(regionDir, "*", SearchOption.AllDirectories).GetEnumerator())
            {
                return enumerator.MoveNext();
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[PaintUnlocked] HasAnyChunkFile scan failed for {saveDir}: {ex.Message} — assuming legacy");
            return true;
        }
    }

    /// <summary>
    /// Write the sentinel file to mark the current world as fully migrated.
    /// Called after a clean save cycle so we know every loaded chunk has been
    /// touched and persisted at the new width.
    /// </summary>
    public static void MarkMigrated()
    {
        if (!_flagLoaded || string.IsNullOrEmpty(_saveDir))
        {
            Log.Warning("[PaintUnlocked] MarkMigrated called before LoadFlag — ignoring");
            return;
        }

        if (!_needsMigration)
        {
            return; // Already migrated, nothing to do
        }

        var sentinelPath = Path.Combine(_saveDir, SentinelFileName);

        try
        {
            File.WriteAllText(sentinelPath, "PaintUnlocked migration complete.\nThis world has been migrated from 8-bit to 10-bit paint storage.\n");
            _needsMigration = false;
            Log.Out($"[PaintUnlocked] Migration complete. Sentinel written: {sentinelPath}");
            CloseJournal();
            lock (_converted) _converted.Clear();
            var journalPath = Path.Combine(_saveDir, JournalFileName);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to write migration sentinel: {ex.Message}");
        }
    }

    /// <summary>
    /// For a fresh world where LoadFlag couldn't write the sentinel yet (e.g. save
    /// dir hadn't been created), retry now that a save cycle has presumably created
    /// the directory. Only fires when the world was identified as non-legacy
    /// (NeedsMigration == false) — for true legacy worlds, MarkMigrated handles it
    /// after at least one chunk has been migrated.
    /// </summary>
    public static void EnsureSentinelExistsForFreshWorld()
    {
        if (!_flagLoaded || string.IsNullOrEmpty(_saveDir)) return;
        if (_needsMigration) return; // legacy world — MarkMigrated path owns the write

        var sentinelPath = Path.Combine(_saveDir, SentinelFileName);
        if (File.Exists(sentinelPath)) return;

        try
        {
            if (!Directory.Exists(_saveDir))
                Directory.CreateDirectory(_saveDir);
            File.WriteAllText(sentinelPath, "PaintUnlocked: world saved at 10-bit storage.\n");
            Log.Out($"[PaintUnlocked] Sentinel written after first save: {sentinelPath}");
        }
        catch (System.Exception ex)
        {
            Log.Warning($"[PaintUnlocked] EnsureSentinelExistsForFreshWorld failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset state. Called when unloading a world so the next load starts fresh.
    /// </summary>
    public static void Reset()
    {
        CloseJournal();
        lock (_converted) _converted.Clear();
        _needsMigration = false;
        _flagLoaded = false;
        _saveDir = null;
    }

    /// <summary>Number of chunks the journal says are already at width 8.</summary>
    public static int ConvertedCount
    {
        get { lock (_converted) return _converted.Count; }
    }

    /// <summary>
    /// True when this chunk was already rewritten at width 8 by an earlier (possibly
    /// interrupted) eager migration, so it must be read at width 8 even though the world
    /// as a whole is still legacy.
    /// </summary>
    public static bool IsConverted(long chunkKey)
    {
        lock (_converted) return _converted.Contains(chunkKey);
    }

    /// <summary>
    /// Record that a chunk is now on disk at width 8. Call only after its write returned.
    /// Flushed to the OS per call, so it survives the process being killed.
    /// </summary>
    public static void RecordConverted(long chunkKey)
    {
        lock (_converted)
        {
            if (!_converted.Add(chunkKey)) return;
            try
            {
                if (_journal == null)
                    _journal = new FileStream(Path.Combine(_saveDir, JournalFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = System.BitConverter.GetBytes(chunkKey);
                _journal.Write(bytes, 0, bytes.Length);
                _journal.Flush();
            }
            catch (System.Exception ex)
            {
                Log.Error($"[PaintUnlocked] Failed to append to migration journal: {ex.Message}");
            }
        }
    }

    private static void LoadJournal()
    {
        lock (_converted)
        {
            _converted.Clear();
            var journalPath = Path.Combine(_saveDir, JournalFileName);
            if (!File.Exists(journalPath)) return;
            try
            {
                var bytes = File.ReadAllBytes(journalPath);
                // A torn trailing entry (killed mid-write) is dropped; its chunk is re-read
                // at width 6, which is what it still is unless the kill landed in the
                // microseconds between the region write and this append.
                for (int i = 0; i + 8 <= bytes.Length; i += 8)
                    _converted.Add(System.BitConverter.ToInt64(bytes, i));
                Log.Warning($"[PaintUnlocked] Resuming an interrupted migration: {_converted.Count} chunks are already converted ({journalPath}).");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[PaintUnlocked] Failed to read migration journal {journalPath}: {ex.Message}");
            }
        }
    }

    private static void CloseJournal()
    {
        lock (_converted)
        {
            try { _journal?.Dispose(); } catch { }
            _journal = null;
        }
    }
}
