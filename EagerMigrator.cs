using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Performs up-front migration of every chunk on disk from vanilla 8-bit-per-face paint
/// storage (bytesPerVal=6) to PaintUnlocked's 10-bit-per-face storage (bytesPerVal=8).
/// Runs once when a legacy world is first loaded.
///
/// Why eager rather than lazy: 7DTD's chunk lifecycle includes auto-save-on-cache-drop.
/// During play, painted chunks may be migrated in memory, dropped, saved to disk at
/// bpv=8, then re-streamed back in. With lazy migration still active, the re-read
/// would flip bpv to 6 and read width-8 disk data as width-6 — past-end-of-stream error,
/// chunk lost, blank regeneration.
///
/// Hooked from RegionFileManager's constructor postfix — at that point the disk
/// manifest (chunksInLoadDir, chunksInSaveDir) is populated but normal chunk
/// streaming hasn't started yet.
///
/// A lived-in save has tens of thousands of chunks (~4 ms each on a fast machine), so
/// doing them all inside the constructor froze the game on "Configuring splatmaps" for
/// minutes with Windows reporting it as not responding. When the world-load coroutine is
/// wrapped by MigrationLoadingDriver, the constructor only plans the work and the driver
/// runs it in ~100 ms slices between frames, showing progress on the loading screen.
/// Without the driver (unrecognised chunk provider) it still runs synchronously.
///
/// Crash safety: each converted chunk is appended to the journal in WorldMigrationState
/// right after its write, so a run that is killed resumes where it stopped instead of
/// re-reading converted chunks as legacy. Before the first chunk is touched, the save's
/// Region folder is copied to Region.pre-paintunlocked.
/// </summary>
public static class EagerMigrator
{
    private const int ProgressLogEvery = 2000;
    private const string BackupSuffix = ".pre-paintunlocked";
    private const long BackupFreeSpaceMargin = 512L * 1024 * 1024;

    private static bool _hasRun = false;

    /// <summary>Set by MigrationLoadingDriver while it is running a world-load coroutine.</summary>
    public static bool LoadCoroutineActive;

    private static RegionFileManager _rfm;
    private static long[] _pending;
    private static int _next;
    private static Stopwatch _sw;
    private static int _migrated, _rewritten, _dropped, _failed, _alreadyConverted;

    private static List<string> _backupFiles;
    private static int _backupNext;
    private static string _backupPartialDir, _backupDir;
    private static bool _backupMade;

    /// <summary>True from the moment migration is planned until it has finished.</summary>
    public static bool HasPendingWork => _rfm != null;

    /// <summary>
    /// Reset per-world state so a second world load (after unload) re-runs the check.
    /// </summary>
    public static void Reset()
    {
        _hasRun = false;
        _rfm = null;
        _pending = null;
        _backupFiles = null;
        ChunkBlockChannelReadPatch.ResetDiagnostics();
        LegacyChunkLoadPatch.ResetCounters();
    }

    /// <summary>
    /// Postfix on RegionFileManager constructor. Plans the migration; runs it right away
    /// unless MigrationLoadingDriver will step it from the loading coroutine.
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
            Begin(__instance);
            if (!HasPendingWork) return;

            if (LoadCoroutineActive)
            {
                Log.Out("[PaintUnlocked] EagerMigrator: running in slices from the world-load coroutine (progress is shown on the loading screen).");
                return;
            }

            Log.Out("[PaintUnlocked] EagerMigrator: no load-coroutine driver for this world type — running synchronously. World load will pause until complete.");
            while (HasPendingWork) Step(double.MaxValue);
        }
        catch (Exception ex)
        {
            _rfm = null;
            Log.Error($"[PaintUnlocked] EagerMigrator: top-level crash, sentinel NOT written: {ex.Message}");
            Log.Exception(ex);
        }
    }

    private static void Begin(RegionFileManager rfm)
    {
        if (rfm.snapshotUtil == null)
        {
            Log.Warning("[PaintUnlocked] EagerMigrator: snapshotUtil is null — cannot flush chunks.");
            return;
        }

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

        var pending = new List<long>(keys.Length);
        foreach (var key in keys)
        {
            if (WorldMigrationState.IsConverted(key)) continue;
            pending.Add(key);
        }

        _pending = pending.ToArray();
        _next = 0;
        _alreadyConverted = keys.Length - _pending.Length;
        _migrated = _rewritten = _dropped = _failed = 0;
        _backupMade = false;

        // Back up only before a migration's first write. On a resumed run the Region folder
        // already holds converted chunks, so a copy would not be the original any more.
        _backupFiles = null;
        if (WorldMigrationState.ConvertedCount == 0)
            PlanBackup(rfm.saveDirectory);

        _rfm = rfm;
        _sw = Stopwatch.StartNew();

        Log.Out($"[PaintUnlocked] EagerMigrator: starting scan of {_pending.Length} chunks" +
                (_alreadyConverted > 0 ? $" ({_alreadyConverted} already converted by an interrupted earlier run)." : "."));
    }

    /// <summary>
    /// Do up to <paramref name="budgetMs"/> of work. Finishes (and clears HasPendingWork)
    /// once every chunk has been handled.
    /// </summary>
    public static void Step(double budgetMs)
    {
        if (_rfm == null) return;
        var slice = Stopwatch.StartNew();
        try
        {
            while (slice.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (_backupFiles != null)
                {
                    StepBackup();
                    continue;
                }
                if (_next >= _pending.Length)
                {
                    Finish();
                    return;
                }
                MigrateOne(_pending[_next++]);
            }
        }
        catch (Exception ex)
        {
            _rfm = null;
            Log.Error($"[PaintUnlocked] EagerMigrator: crashed after {_next} chunks, sentinel NOT written (converted chunks are journaled, the next load resumes): {ex.Message}");
            Log.Exception(ex);
        }
    }

    /// <summary>Loading-screen text for the current state.</summary>
    public static string ProgressText
    {
        get
        {
            if (_backupFiles != null)
                return $"PaintUnlocked: backing up this save before its one-time upgrade\n{_backupNext} / {_backupFiles.Count} region files";
            int total = _pending?.Length ?? 0;
            return $"PaintUnlocked: upgrading this save (one time only)\n{_next:N0} / {total:N0} chunks";
        }
    }

    private static void MigrateOne(long key)
    {
        int cX = WorldChunkCache.extractX(key);
        int cZ = WorldChunkCache.extractZ(key);
        int beforeMigCount = ChunkMigrationPatch.MigratedChunkCount;

        Chunk chunk;
        try
        {
            chunk = _rfm.GetChunkSync(key);
        }
        catch (Exception ex)
        {
            _failed++;
            Log.Warning($"[PaintUnlocked] EagerMigrator: GetChunkSync threw for chunk ({cX},{cZ}): {ex.Message}");
            return;
        }

        if (chunk == null)
        {
            // The engine couldn't read it at either width (damaged region data). It has
            // already written an error_backup_*.bak and removed the chunk, which will
            // regenerate like any vanilla corrupt chunk. Nothing left to convert.
            _dropped++;
            return;
        }

        // Detect whether ChunkMigrationPatch.ReadPostfix actually ran for this chunk by
        // comparing the migration counter. We can't trust chunk.isModified because
        // Chunk.load() resets it to false AFTER read returns.
        bool wasJustMigrated = ChunkMigrationPatch.MigratedChunkCount > beforeMigCount;

        try
        {
            // Always flush — for migrated chunks this writes the bpv=8 form back; for
            // chunks that were already at width 8 it rewrites the same data.
            if (TryFlushChunkToDisk(chunk, key))
            {
                WorldMigrationState.RecordConverted(key);
                if (wasJustMigrated) _migrated++;
                else _rewritten++;
            }
            else
            {
                _failed++;
                Log.Warning($"[PaintUnlocked] EagerMigrator: chunk ({cX},{cZ}) flush returned false");
            }
            chunk.isModified = false;
        }
        catch (Exception ex)
        {
            _failed++;
            Log.Warning($"[PaintUnlocked] EagerMigrator: chunk ({cX},{cZ}) flush failed: {ex.Message}");
        }

        if (_next % ProgressLogEvery == 0 || _next == _pending.Length)
            Log.Out($"[PaintUnlocked] EagerMigrator: {_next}/{_pending.Length} ({_migrated} migrated, {_failed} failed)");
    }

    private static void Finish()
    {
        _sw.Stop();
        int recovered = LegacyChunkLoadPatch.RecoveredCount;
        Log.Out($"[PaintUnlocked] EagerMigrator: scan complete in {_sw.ElapsedMilliseconds}ms. {_pending.Length} processed, {_migrated} migrated, {_rewritten} already at width 8" +
                $" ({recovered} recovered from an interrupted earlier run), {_alreadyConverted} skipped (journaled), {_dropped} unreadable (removed by the engine), {_failed} failed.");

        if (_failed == 0)
        {
            WorldMigrationState.MarkMigrated();
        }
        else
        {
            Log.Warning($"[PaintUnlocked] EagerMigrator: {_failed} chunks failed — sentinel NOT written. The next load retries only those; converted chunks are journaled and are not read as legacy again.");
        }

        if (_backupMade)
            Log.Out($"[PaintUnlocked] The save's region files from before the upgrade are kept in {_backupDir} - delete that folder once you're happy with the world.");

        _rfm = null;
        _pending = null;
    }

    private static void PlanBackup(string regionDir)
    {
        try
        {
            if (string.IsNullOrEmpty(regionDir) || !Directory.Exists(regionDir)) return;
            var parent = Path.GetDirectoryName(regionDir.TrimEnd('\\', '/'));
            _backupDir = Path.Combine(parent, Path.GetFileName(regionDir.TrimEnd('\\', '/')) + BackupSuffix);
            _backupPartialDir = _backupDir + ".partial";

            if (Directory.Exists(_backupDir))
            {
                Log.Out($"[PaintUnlocked] EagerMigrator: a pre-upgrade backup already exists at {_backupDir} - leaving it as is.");
                return;
            }
            if (Directory.Exists(_backupPartialDir))
                Directory.Delete(_backupPartialDir, true);

            var files = new List<string>(Directory.GetFiles(regionDir));
            long size = 0;
            foreach (var f in files) size += new FileInfo(f).Length;
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(regionDir))).AvailableFreeSpace;
            if (free < size + BackupFreeSpaceMargin)
            {
                Log.Warning($"[PaintUnlocked] EagerMigrator: not enough free disk space to back up the region files ({size / (1024 * 1024)} MB needed, {free / (1024 * 1024)} MB free) - upgrading without a backup.");
                return;
            }

            Directory.CreateDirectory(_backupPartialDir);
            _backupFiles = files;
            _backupNext = 0;
            Log.Out($"[PaintUnlocked] EagerMigrator: backing up {files.Count} region files ({size / (1024 * 1024)} MB) to {_backupDir}");
        }
        catch (Exception ex)
        {
            _backupFiles = null;
            Log.Warning($"[PaintUnlocked] EagerMigrator: could not prepare the pre-upgrade backup ({ex.Message}) - upgrading without one.");
        }
    }

    private static void StepBackup()
    {
        try
        {
            if (_backupNext < _backupFiles.Count)
            {
                var src = _backupFiles[_backupNext++];
                File.Copy(src, Path.Combine(_backupPartialDir, Path.GetFileName(src)));
                return;
            }
            Directory.Move(_backupPartialDir, _backupDir);
            _backupMade = true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[PaintUnlocked] EagerMigrator: pre-upgrade backup failed ({ex.Message}) - upgrading without one.");
            try { Directory.Delete(_backupPartialDir, true); } catch { }
        }
        _backupFiles = null;
    }

    /// <summary>
    /// Snapshot the chunk and write it to its region file synchronously, bypassing
    /// the background save thread. Returns true on success.
    /// </summary>
    private static bool TryFlushChunkToDisk(Chunk chunk, long key)
    {
        IRegionFileChunkSnapshot snapshot = null;
        try
        {
            snapshot = _rfm.snapshotUtil.TakeSnapshot(chunk, true);
            if (snapshot == null) return false;

            int cX = WorldChunkCache.extractX(key);
            int cZ = WorldChunkCache.extractZ(key);
            _rfm.snapshotUtil.WriteSnapshot(snapshot, _rfm.saveDirectory, cX, cZ);
            return true;
        }
        finally
        {
            if (snapshot != null)
            {
                try { _rfm.snapshotUtil.Free(snapshot); }
                catch (Exception ex) { Log.Warning($"[PaintUnlocked] EagerMigrator: snapshot free failed: {ex.Message}"); }
            }
        }
    }
}
