/// <summary>
/// Harmony patches on World.LoadWorld, World.Save, and World.UnloadWorld that
/// manage the PaintUnlocked migration sentinel file lifecycle.
///
///   LoadWorld prefix    → WorldMigrationState.LoadFlag(GameIO.GetSaveGameDir())
///   Save postfix        → WorldMigrationState.MarkMigrated() (if migration was active)
///   UnloadWorld prefix  → WorldMigrationState.Reset() + ChunkMigrationPatch.ResetCounters()
///
/// LoadWorld is an IEnumerator coroutine — prefix runs once when the coroutine is
/// created, before the first yield. That's early enough because chunks don't read
/// from disk until much later in the load sequence.
/// </summary>
public static class WorldLifecyclePatch
{
    public static void LoadWorldPrefix(string _sWorldName)
    {
        try
        {
            // Pure clients receive chunks over the network at PaintUnlocked's
            // current width. Enabling migration on a client would cause
            // ChunkStorageWidthPatch to leave network-received channels at the
            // legacy width, desyncing NetPackageChunk deserialization → instant DC.
            // Migration only runs on the authoritative side (host / dedicated server).
            var cm = ConnectionManager.Instance;
            bool isAuthoritative = cm == null || cm.IsServer;
            if (!isAuthoritative)
            {
                Log.Out($"[PaintUnlocked] LoadWorld('{_sWorldName}') — pure client, skipping migration (network chunks use current width).");
                WorldMigrationState.Reset();
                ChunkMigrationPatch.ResetCounters();
                EagerMigrator.Reset();
                PaintIdSyncManager.ResetWorldState(); // client gets IDs from server via pu_sync
                return;
            }

            var saveDir = GameIO.GetSaveGameDir();
            Log.Out($"[PaintUnlocked] LoadWorld('{_sWorldName}') — checking migration sentinel at {saveDir}");
            WorldMigrationState.LoadFlag(saveDir);
            ChunkMigrationPatch.ResetCounters();
            EagerMigrator.Reset();

            // Now that the save dir is known, lock this world's paint IDs to the persistent
            // map (no-op until InitOpaqueConfig has registered the custom paints — that
            // postfix re-triggers it if it runs after this point).
            PaintIdSyncManager.ResetWorldState();
            PaintIdSyncManager.TryReconcilePersistent();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] LoadWorldPrefix failed: {ex.Message}");
        }
    }

    public static void SavePostfix()
    {
        try
        {
            if (WorldMigrationState.NeedsMigration && ChunkMigrationPatch.MigratedChunkCount > 0)
            {
                Log.Out($"[PaintUnlocked] World.Save complete — {ChunkMigrationPatch.MigratedChunkCount} chunks migrated ({ChunkMigrationPatch.MigratedNonZeroBlocks} painted blocks). Writing sentinel.");
                WorldMigrationState.MarkMigrated();
            }
            else
            {
                // Fresh-world catch-up: if LoadFlag identified this as a fresh world
                // but couldn't write the sentinel (save dir didn't exist yet), do it now.
                WorldMigrationState.EnsureSentinelExistsForFreshWorld();
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] SavePostfix failed: {ex.Message}");
        }
    }

    public static void UnloadWorldPrefix()
    {
        try
        {
            if (WorldMigrationState.NeedsMigration && ChunkMigrationPatch.MigratedChunkCount > 0)
            {
                Log.Warning($"[PaintUnlocked] World unloading with migration still active. {ChunkMigrationPatch.MigratedChunkCount} chunks were migrated in-memory but the sentinel was not written (no save cycle occurred). They will be re-migrated on next load.");
            }
            WorldMigrationState.Reset();
            ChunkMigrationPatch.ResetCounters();
            EagerMigrator.Reset();
            PaintIdSyncManager.ResetWorldState();
        }
        catch (System.Exception ex)
        {
            Log.Error($"[PaintUnlocked] UnloadWorldPrefix failed: {ex.Message}");
        }
    }
}
