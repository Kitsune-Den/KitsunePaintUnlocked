using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Puts back chunks the game threw away while this world was loaded without PaintUnlocked.
///
/// A migrated world stores every chunk's texture channel at width 8. Loaded without
/// PaintUnlocked (removed, disabled, blocked by EAC), the game reads it at width 6, the
/// read fails, and ChunkSnapshotUtil.LoadChunk copies the raw region record to
/// Region/error_backup_X_Z.comp.bak (plus the inflated data as .uncomp.bak), removes the
/// chunk and generates it again from the world: player builds vanish.
///
/// Runs from the RegionFileManager constructor postfix, before any chunk streams in, on
/// worlds an earlier PaintUnlocked session already played. A backup is written back to the
/// region file only when
///   - its data parses at PaintUnlocked's width 8 and is the chunk its name says, and
///   - the chunk in the region file now is missing, or is at vanilla width 6 (the game
///     regenerated and saved it without PaintUnlocked), or can't be read at all.
/// A width-8 chunk in the region file is left alone: PaintUnlocked saved it after the loss.
///
/// The chunk that gets replaced is kept as paintunlocked_replaced_X_Z.comp.bak, and the
/// restored backup is renamed to paintunlocked_restored_X_Z.*.bak so it isn't used twice
/// and the game can't overwrite it with a later error_backup of the same chunk.
/// </summary>
public static class LostChunkRestorer
{
    private static readonly Regex BackupName = new Regex(@"^error_backup_(-?\d+)_(-?\d+)\.comp\.bak$", RegexOptions.IgnoreCase);
    private const int LogLimit = 10;

    private static bool _warned;

    public static void ResetWarning() => _warned = false;

    /// <summary>One loud warning per world load that the world was played without PaintUnlocked.</summary>
    public static void WarnPlayedWithoutPaintUnlocked()
    {
        if (_warned) return;
        _warned = true;
        Log.Warning("[PaintUnlocked] ==================================================================");
        Log.Warning("[PaintUnlocked] This world was loaded WITHOUT PaintUnlocked at some point (mod removed,");
        Log.Warning("[PaintUnlocked] disabled, or blocked by EAC). The game can't read PaintUnlocked's chunks on");
        Log.Warning("[PaintUnlocked] its own, so it threw them away and generated them again. PaintUnlocked puts");
        Log.Warning("[PaintUnlocked] back what it can. Keep PaintUnlocked installed (EAC off) for this world.");
        Log.Warning("[PaintUnlocked] ==================================================================");
    }

    public static void Run(RegionFileManager rfm)
    {
        string dir = rfm?.saveDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        string[] backups;
        try { backups = Directory.GetFiles(dir, "error_backup_*.comp.bak"); }
        catch (Exception ex)
        {
            Log.Warning($"[PaintUnlocked] Lost-chunk check: could not list {dir}: {ex.Message}");
            return;
        }
        if (backups.Length == 0) return;

        if (!(rfm.snapshotUtil is ChunkSnapshotUtil snapshotUtil) || snapshotUtil.chunkReader == null || rfm.regionFileAccess == null)
        {
            Log.Warning($"[PaintUnlocked] Lost-chunk check: {backups.Length} chunk backup(s) found, but this platform's region storage isn't supported - not restoring.");
            return;
        }

        Log.Out($"[PaintUnlocked] Lost-chunk check: {backups.Length} chunk backup(s) in {dir}");
        int restored = 0, notPaintUnlocked = 0, stillFine = 0, failed = 0;
        uint timestamp = NewestTimestamp(rfm);

        foreach (var compPath in backups)
        {
            var m = BackupName.Match(Path.GetFileName(compPath));
            if (!m.Success) continue;
            int x = int.Parse(m.Groups[1].Value);
            int z = int.Parse(m.Groups[2].Value);
            try
            {
                switch (TryRestore(rfm, snapshotUtil, dir, compPath, x, z, timestamp))
                {
                    case Outcome.Restored:
                        restored++;
                        if (restored <= LogLimit)
                            Log.Out($"[PaintUnlocked] Restored chunk ({x},{z}) from its backup.");
                        break;
                    case Outcome.NotPaintUnlocked: notPaintUnlocked++; break;
                    case Outcome.CurrentIsFine: stillFine++; break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"[PaintUnlocked] Lost-chunk check: chunk ({x},{z}) failed: {ex.Message}");
            }
        }

        if (restored > 0)
            WarnPlayedWithoutPaintUnlocked();
        Log.Out($"[PaintUnlocked] Lost-chunk check done: {restored} restored, {stillFine} left alone (a newer PaintUnlocked save is there)," +
                $" {notPaintUnlocked} not PaintUnlocked chunks (left for the game), {failed} failed.");
    }

    private enum Outcome { Restored, NotPaintUnlocked, CurrentIsFine }

    private static Outcome TryRestore(RegionFileManager rfm, ChunkSnapshotUtil snapshotUtil, string dir,
                                      string compPath, int x, int z, uint timestamp)
    {
        string uncompPath = compPath.Substring(0, compPath.Length - ".comp.bak".Length) + ".uncomp.bak";
        if (!File.Exists(uncompPath)) return Outcome.NotPaintUnlocked;

        byte[] comp = File.ReadAllBytes(compPath);
        if (comp.Length < 8 || comp[0] != 't' || comp[1] != 't' || comp[2] != 'c' || comp[3] != 0)
            return Outcome.NotPaintUnlocked;
        uint version = BitConverter.ToUInt32(comp, 4);

        // 1. The backup must be this chunk, readable at PaintUnlocked's width.
        if (!ParsesAt(File.ReadAllBytes(uncompPath), version, 8, x, z))
            return Outcome.NotPaintUnlocked;

        // 2. What is in the region file now?
        var current = ReadCurrent(snapshotUtil, dir, x, z, out uint currentVersion);
        if (current != null && ParsesAt(current, currentVersion, 8, x, z))
            return Outcome.CurrentIsFine;

        // Keep whatever is there now (vanilla-width regenerated chunk, or garbage).
        if (current != null)
            CopyRecord(rfm.regionFileAccess, dir, x, z, Path.Combine(dir, $"paintunlocked_replaced_{x}_{z}.comp.bak"));

        using (var output = rfm.regionFileAccess.GetOutputStream(dir, x, z, "7rg"))
        {
            output.Write(comp, 0, comp.Length);
            output.Close();
        }

        long key = WorldChunkCache.MakeChunkKey(x, z);
        lock (rfm.chunksInSaveDir)
        {
            if (!rfm.chunksInSaveDir.ContainsKey(key))
                rfm.SetChunkTimestamp(key, timestamp);
        }

        MoveReplacing(compPath, Path.Combine(dir, $"paintunlocked_restored_{x}_{z}.comp.bak"));
        MoveReplacing(uncompPath, Path.Combine(dir, $"paintunlocked_restored_{x}_{z}.uncomp.bak"));
        return Outcome.Restored;
    }

    /// <summary>The inflated chunk data now in the region file, or null if there is none.</summary>
    private static byte[] ReadCurrent(ChunkSnapshotUtil snapshotUtil, string dir, int x, int z, out uint version)
    {
        version = 0;
        PooledBinaryReader reader;
        try { reader = snapshotUtil.chunkReader.readIntoLoadStream(dir, x, z, "7rg", out version); }
        catch (Exception) { return new byte[0]; }   // a record that isn't a chunk: treat as unreadable
        if (reader == null) return null;
        var s = reader.BaseStream;
        s.Position = 0;
        var data = new byte[s.Length];
        int read = 0;
        while (read < data.Length)
        {
            int n = s.Read(data, read, data.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return data;
    }

    private static bool ParsesAt(byte[] data, uint version, int width, int x, int z)
    {
        if (data == null || data.Length == 0) return false;
        var chunk = MemoryPools.PoolChunks.AllocSync(_bReset: true);
        var reader = new PooledBinaryReader();
        try
        {
            reader.SetBaseStream(new MemoryStream(data, false));
            return LegacyChunkLoadPatch.TryParse(chunk, reader, version, width) && chunk.X == x && chunk.Z == z;
        }
        finally
        {
            MemoryPools.PoolChunks.FreeSync(chunk);
        }
    }

    private static void CopyRecord(RegionFileAccessAbstract rfa, string dir, int x, int z, string target)
    {
        using (var input = rfa.GetInputStream(dir, x, z, "7rg"))
        {
            if (input == null) return;
            if (input.CanSeek) input.Position = 0;
            if (File.Exists(target)) File.Delete(target);
            using (var output = File.Create(target))
                input.CopyTo(output);
        }
    }

    private static void MoveReplacing(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }

    private static uint NewestTimestamp(RegionFileManager rfm)
    {
        uint newest = 0;
        lock (rfm.chunksInSaveDir)
        {
            foreach (var ts in rfm.chunksInSaveDir.Values)
                if (ts > newest) newest = ts;
        }
        return newest;
    }
}
