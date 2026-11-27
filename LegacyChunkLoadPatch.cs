using System;
using System.Threading;

/// <summary>
/// Prefix + finalizer on Chunk.load (the disk read path; network chunks use Chunk.read
/// directly). Chunk data on disk doesn't record the width of its texture channel, so a
/// chunk can be on disk at a width the current load doesn't expect. Reading it at the
/// wrong width throws "Attempted to read past the end of the stream", and
/// ChunkSnapshotUtil.LoadChunk then deletes the chunk from the region file and the world
/// generates it again. This patch retries such a read once at the other width.
///
/// 1. While a legacy world is being migrated: tell ChunkBlockChannelReadPatch which chunk
///    it is reading, so chunks the migration journal lists as already converted are read
///    at width 8 instead of legacy width 6. A failed legacy read is retried at width 8:
///    up to 1.4.4 the eager migration only marked the world once every chunk was done, so
///    killing the game part-way left converted chunks behind with no journal.
///
/// 2. On a migrated world: a failed read is retried at vanilla width 6 and repacked. Those
///    are chunks the game saved while PaintUnlocked wasn't running (removed, disabled,
///    blocked by EAC). Without this they are thrown away a second time once PaintUnlocked
///    is back, and their error_backup file overwrites the backup of the original chunk.
///
/// If the retry fails too, the chunk is genuinely damaged and the engine handles it the
/// vanilla way (error backup + regenerate).
///
/// TryParse also lets LostChunkRestorer test chunk data at a chosen width without any
/// retry.
/// </summary>
public static class LegacyChunkLoadPatch
{
    [ThreadStatic] private static Chunk _loading;

    /// <summary>0 = normal; 6 or 8 = read every texture channel at this width, no retries.</summary>
    [ThreadStatic] private static int _forcedWidth;

    /// <summary>True inside TryParse: nothing is counted, journaled or logged.</summary>
    [ThreadStatic] private static bool _probing;

    private static int _recovered;
    private static int _vanillaRecovered;
    private const int RecoveredLogLimit = 10;

    public static int RecoveredCount => _recovered;

    /// <summary>Chunks on a migrated world that were saved at vanilla width and converted on load.</summary>
    public static int VanillaRecoveredCount => _vanillaRecovered;

    public static int ForcedWidth => _forcedWidth;
    public static bool Probing => _probing;

    /// <summary>
    /// True when the chunk whose channels are being read is already at width 8 on disk.
    /// Only meaningful inside Chunk.load (X/Z are read before any channel).
    /// </summary>
    public static bool CurrentChunkIsAtCurrentWidth()
    {
        if (_forcedWidth == 8) return true;
        var chunk = _loading;
        return chunk != null && WorldMigrationState.IsConverted(chunk.Key);
    }

    public static void Prefix(Chunk __instance, PooledBinaryReader stream, out long __state)
    {
        _loading = __instance;
        __state = -1;
        if (_forcedWidth != 0 || _probing) return;
        var s = stream?.BaseStream;
        if (s != null && s.CanSeek) __state = s.Position;
    }

    public static Exception Finalizer(Exception __exception, Chunk __instance, PooledBinaryReader stream, uint _version, long __state)
    {
        _loading = null;
        if (__exception == null || __state < 0 || _forcedWidth != 0 || _probing) return __exception;

        // Legacy world being migrated: the failed read was at width 6, so try 8.
        // Migrated world: the failed read was at width 8, so try vanilla's 6.
        int retryWidth = WorldMigrationState.NeedsMigration ? 8 : 6;
        try
        {
            _forcedWidth = retryWidth;
            stream.BaseStream.Position = __state;
            __instance.load(stream, _version);
        }
        catch (Exception)
        {
            ReplaceNarrowChannels(__instance);
            return __exception;
        }
        finally
        {
            _forcedWidth = 0;
            _loading = null;
        }

        if (retryWidth == 8)
        {
            WorldMigrationState.RecordConverted(__instance.Key);
            int n = Interlocked.Increment(ref _recovered);
            if (n <= RecoveredLogLimit)
                Log.Warning($"[PaintUnlocked] Chunk ({__instance.X},{__instance.Z}) was already converted by an earlier, interrupted migration - read it at the current width instead of discarding it.");
            else if (n == RecoveredLogLimit + 1)
                Log.Warning("[PaintUnlocked] More already-converted chunks recovered; further ones are counted in the migration summary.");
        }
        else
        {
            // Chunk.load clears isModified; set it again so the converted chunk is saved
            // back at width 8.
            __instance.isModified = true;
            int n = Interlocked.Increment(ref _vanillaRecovered);
            if (n <= RecoveredLogLimit)
                Log.Warning($"[PaintUnlocked] Chunk ({__instance.X},{__instance.Z}) was saved by the game while PaintUnlocked wasn't running - converted it instead of discarding it.");
            else if (n == RecoveredLogLimit + 1)
                Log.Warning("[PaintUnlocked] More chunks saved without PaintUnlocked were converted; not logging each one.");
            if (n == 1)
                LostChunkRestorer.WarnPlayedWithoutPaintUnlocked();
        }
        return null;
    }

    /// <summary>
    /// Read chunk data into <paramref name="chunk"/> with every texture channel at
    /// <paramref name="width"/> (6 = vanilla, 8 = PaintUnlocked), without retries or side
    /// effects on the counters. Returns false if the data doesn't parse at that width.
    /// </summary>
    public static bool TryParse(Chunk chunk, PooledBinaryReader reader, uint version, int width)
    {
        bool wasProbing = _probing;
        int wasForced = _forcedWidth;
        _probing = true;
        _forcedWidth = width;
        try
        {
            chunk.load(reader, version);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _probing = wasProbing;
            _forcedWidth = wasForced;
            _loading = null;
            ReplaceNarrowChannels(chunk);
        }
    }

    /// <summary>
    /// A read that failed part-way leaves texture channel objects at width 6, and pooled
    /// chunks keep their channel objects, so the next chunk loaded into this one would be
    /// read at the wrong width. Swap such channels for fresh width-8 ones.
    /// </summary>
    private static void ReplaceNarrowChannels(Chunk chunk)
    {
        var channels = chunk?.chnTextures;
        if (channels == null) return;
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i] != null && ChunkBlockChannelReadPatch.GetBytesPerVal(channels[i]) == 6)
                channels[i] = new ChunkBlockChannel(0L, 8);
        }
    }

    public static void ResetCounters()
    {
        _recovered = 0;
        _vanillaRecovered = 0;
    }
}
