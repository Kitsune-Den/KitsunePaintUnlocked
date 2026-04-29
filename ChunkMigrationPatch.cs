/// <summary>
/// Migrates chunks from vanilla's 8-bit-per-face paint storage (ChunkBlockChannel
/// at bytesPerVal=6) to PaintUnlocked's 10-bit-per-face storage (bytesPerVal=8).
///
/// Flow for a legacy world:
///   1. WorldMigrationState.NeedsMigration = true (set at world load from sentinel check)
///   2. Chunk constructor creates chnTextures at width 8 (CtorPrefix always upgrades 6->8).
///   3. ChunkBlockChannelReadPatch.ReadPrefix fires BEFORE each channel's Read():
///      temporarily sets bytesPerVal from 8 back to 6, so Read() consumes the correct
///      number of bytes from the legacy-format stream.
///   4. After Read(), the channel has width-6 data. bytesPerVal is still 6 (not restored).
///   5. ReadPostfix fires AFTER Chunk.read completes:
///      - For each chnTextures channel where bytesPerVal == 6 (i.e. was legacy-read):
///        constructs a fresh width-8 channel, repacks 8-bit→10-bit, swaps it in.
///      - Marks chunk modified so it persists at width 8 on next save.
///
/// Fresh worlds (NeedsMigration == false) skip all migration logic via early returns.
///
/// Performance: 16 x 256 x 16 = 65,536 Get/Set operations per chunk per channel.
/// Runs once per chunk, lazily as chunks stream in. Imperceptible at normal exploration rates.
/// </summary>
public static class ChunkMigrationPatch
{
    // Chunk dimensions are fixed at 16x256x16 in 7DTD
    private const int ChunkX = 16;
    private const int ChunkY = 256;
    private const int ChunkZ = 16;

    private static int _migratedChunkCount = 0;
    private static int _migratedNonZeroBlocks = 0;

    public static int MigratedChunkCount => _migratedChunkCount;
    public static int MigratedNonZeroBlocks => _migratedNonZeroBlocks;

    /// <summary>
    /// Postfix on Chunk.read. When migration is active, checks each texture channel's
    /// bytesPerVal. Channels that were legacy-read will have bytesPerVal == 6
    /// (set by ChunkBlockChannelReadPatch and NOT restored). These get upgraded
    /// to width-8 with 8-bit→10-bit repacking.
    ///
    /// Network reads are skipped — network traffic already flows at PaintUnlocked's
    /// current width, so chunks received over the wire are never legacy-format.
    /// </summary>
    public static void ReadPostfix(Chunk __instance, bool _bNetworkRead)
    {
        if (!WorldMigrationState.NeedsMigration) return;
        if (_bNetworkRead) return;
        if (__instance == null) return;
        if (__instance.chnTextures == null || __instance.chnTextures.Length == 0) return;

        int nonZeroCount = 0;
        bool anyMigrated = false;

        for (int ch = 0; ch < __instance.chnTextures.Length; ch++)
        {
            var oldChannel = __instance.chnTextures[ch];
            if (oldChannel == null) continue;

            // Check if this channel was legacy-read.
            // ChunkBlockChannelReadPatch set bytesPerVal to 6 before Read() and
            // did NOT restore it. So bytesPerVal == 6 means this channel contains
            // legacy width-6 data that needs repacking.
            int bpv = ChunkBlockChannelReadPatch.GetBytesPerVal(oldChannel);
            if (bpv != 6) continue;

            anyMigrated = true;

            // Construct a fresh channel at width 8. CtorPrefix upgrades 6→8,
            // but we pass 8 explicitly so it's a no-op.
            var newChannel = new ChunkBlockChannel(0L, 8);

            for (int y = 0; y < ChunkY; y++)
            {
                for (int z = 0; z < ChunkZ; z++)
                {
                    for (int x = 0; x < ChunkX; x++)
                    {
                        long vanillaValue = oldChannel.Get(x, y, z);
                        if (vanillaValue == 0L) continue;

                        long repacked = TextureFullRepackPatch.Repack8to10(vanillaValue);
                        newChannel.Set(x, y, z, repacked);
                        nonZeroCount++;
                    }
                }
            }

            __instance.chnTextures[ch] = newChannel;
        }

        if (anyMigrated)
        {
            __instance.isModified = true;

            _migratedChunkCount++;
            _migratedNonZeroBlocks += nonZeroCount;

            if (_migratedChunkCount <= 5 || _migratedChunkCount % 50 == 0)
            {
                Log.Out($"[PaintUnlocked] Migrated chunk #{_migratedChunkCount} ({nonZeroCount} painted blocks, total painted: {_migratedNonZeroBlocks})");
            }
        }
    }

    /// <summary>
    /// Reset counters. Called when a new world loads.
    /// </summary>
    public static void ResetCounters()
    {
        _migratedChunkCount = 0;
        _migratedNonZeroBlocks = 0;
    }
}
