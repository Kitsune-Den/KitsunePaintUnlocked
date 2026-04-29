using System.Reflection;

/// <summary>
/// Patches ChunkBlockChannel.Read to handle legacy width-6 data in a width-8 channel.
///
/// Problem: CtorPrefix always upgrades bytesPerVal from 6 to 8. But legacy save files
/// contain width-6 data. ChunkBlockChannel.Read uses bytesPerVal to determine how many
/// bytes to consume per entry. Width-8 reading from width-6 data = "Attempted to read
/// past the end of the stream."
///
/// Fix: In the Read prefix, temporarily set bytesPerVal back to 6 for the duration
/// of the read. After Read completes, the channel has width-6 data at width-6 stride.
/// ChunkMigrationPatch.ReadPostfix then creates a fresh width-8 channel, repacks
/// the data from 8-bit to 10-bit face storage, and swaps it in.
///
/// We do NOT restore bytesPerVal after Read — the channel will be replaced entirely
/// by ReadPostfix. Leaving it at 6 also lets ReadPostfix identify which channels
/// were read from legacy data (bytesPerVal == 6 vs 8).
///
/// Only fires when: WorldMigrationState.NeedsMigration AND !_bNetworkRead.
/// Network-received chunks are already at width 8 from PaintUnlocked-enabled peers.
/// </summary>
public static class ChunkBlockChannelReadPatch
{
    private static FieldInfo _fBytesPerVal;
    private static bool _reflectionValid = false;

    static ChunkBlockChannelReadPatch()
    {
        _fBytesPerVal = typeof(ChunkBlockChannel).GetField("bytesPerVal",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _reflectionValid = _fBytesPerVal != null;

        if (_reflectionValid)
            Log.Out("[PaintUnlocked] ChunkBlockChannel.bytesPerVal field found for migration read patch");
        else
            Log.Warning("[PaintUnlocked] ChunkBlockChannel.bytesPerVal field NOT found — legacy migration will not work correctly");
    }

    /// <summary>
    /// Returns the current bytesPerVal for a ChunkBlockChannel instance.
    /// Used by ChunkMigrationPatch.ReadPostfix to detect which channels were
    /// legacy-read (bytesPerVal == 6) vs already at width 8.
    /// Returns -1 if reflection failed.
    /// </summary>
    public static int GetBytesPerVal(ChunkBlockChannel instance)
    {
        if (!_reflectionValid || instance == null) return -1;
        return (int)_fBytesPerVal.GetValue(instance);
    }

    private static int _diagFireCount = 0;
    private const int DiagLogLimit = 6;

    /// <summary>
    /// Prefix on ChunkBlockChannel.Read. For legacy disk reads, sets
    /// bytesPerVal from 8 back to 6 so Read consumes the correct number of bytes
    /// from the legacy-format stream.
    ///
    /// Does NOT restore bytesPerVal after — the channel will be replaced entirely
    /// by ReadPostfix. The residual bytesPerVal==6 serves as a marker for ReadPostfix.
    /// </summary>
    public static void ReadPrefix(ChunkBlockChannel __instance, bool _bNetworkRead)
    {
        if (!_reflectionValid) return;
        if (!WorldMigrationState.NeedsMigration) return;
        if (_bNetworkRead) return;

        int before = (int)_fBytesPerVal.GetValue(__instance);
        bool flipped = false;
        if (before == 8)
        {
            _fBytesPerVal.SetValue(__instance, 6);
            flipped = true;
        }

        // Diagnostic: confirm the prefix is firing at all. Only logs the first few to
        // avoid spam — if we see ANY of these in the log, the patch is wired correctly.
        if (_diagFireCount < DiagLogLimit)
        {
            _diagFireCount++;
            Log.Out($"[PaintUnlocked] ReadPrefix #{_diagFireCount}: bpv {before} -> {(flipped ? 6 : before)} (flipped={flipped})");
        }
    }

    public static void ResetDiagnostics()
    {
        _diagFireCount = 0;
    }
}
