using System;
using System.Threading;

/// <summary>
/// Prefix + finalizer on Chunk.load (the disk read path; network chunks use Chunk.read
/// directly). Two jobs while a legacy world is being migrated:
///
/// 1. Tell ChunkBlockChannelReadPatch which chunk it is reading, so chunks the migration
///    journal lists as already converted are read at width 8 instead of legacy width 6.
///
/// 2. Recover chunks that are at width 8 on disk although nothing records it. Up to 1.4.4
///    the eager migration rewrote chunks one by one and only marked the world once all of
///    them were done; killing the game part-way (it looked frozen) left converted chunks
///    behind with no sentinel and no journal. Reading those at width 6 throws "Attempted
///    to read past the end of the stream", and ChunkSnapshotUtil.LoadChunk then deletes
///    the chunk from the region file. Here a failed legacy read is retried once at width 8
///    from the same stream position; if that parses, the exception is swallowed (the
///    engine never sees it) and the chunk is journaled as converted.
/// </summary>
public static class LegacyChunkLoadPatch
{
    [ThreadStatic] private static Chunk _loading;
    [ThreadStatic] private static bool _retryingAtCurrentWidth;

    private static int _recovered;
    private const int RecoveredLogLimit = 10;

    public static int RecoveredCount => _recovered;

    /// <summary>
    /// True when the chunk whose channels are being read is already at width 8 on disk.
    /// Only meaningful inside Chunk.load (X/Z are read before any channel).
    /// </summary>
    public static bool CurrentChunkIsAtCurrentWidth()
    {
        if (_retryingAtCurrentWidth) return true;
        var chunk = _loading;
        return chunk != null && WorldMigrationState.IsConverted(chunk.Key);
    }

    public static void Prefix(Chunk __instance, PooledBinaryReader stream, out long __state)
    {
        _loading = __instance;
        __state = -1;
        if (!WorldMigrationState.NeedsMigration || _retryingAtCurrentWidth) return;
        var s = stream?.BaseStream;
        if (s != null && s.CanSeek) __state = s.Position;
    }

    public static Exception Finalizer(Exception __exception, Chunk __instance, PooledBinaryReader stream, uint _version, long __state)
    {
        _loading = null;
        if (__exception == null || __state < 0 || _retryingAtCurrentWidth) return __exception;

        try
        {
            _retryingAtCurrentWidth = true;
            stream.BaseStream.Position = __state;
            __instance.load(stream, _version);
        }
        catch (Exception)
        {
            // Not a width-8 chunk either: genuinely damaged. Let the engine handle it the
            // vanilla way (error backup + regenerate).
            return __exception;
        }
        finally
        {
            _retryingAtCurrentWidth = false;
            _loading = null;
        }

        WorldMigrationState.RecordConverted(__instance.Key);
        int n = Interlocked.Increment(ref _recovered);
        if (n <= RecoveredLogLimit)
            Log.Warning($"[PaintUnlocked] Chunk ({__instance.X},{__instance.Z}) was already converted by an earlier, interrupted migration - read it at the current width instead of discarding it.");
        else if (n == RecoveredLogLimit + 1)
            Log.Warning("[PaintUnlocked] More already-converted chunks recovered; further ones are counted in the migration summary.");
        return null;
    }

    public static void ResetCounters()
    {
        _recovered = 0;
    }
}
