using System;
using System.Collections;

/// <summary>
/// Postfix on the chunk providers' Init(World) coroutines (ChunkCluster.Init yields them,
/// so Unity runs whatever enumerator we hand back). The wrapper forwards every step of
/// the original coroutine unchanged; after the step that built the RegionFileManager it
/// runs EagerMigrator in ~100 ms slices, yielding a frame between slices and putting the
/// progress on the loading screen. Without this the whole migration ran inside the
/// RegionFileManager constructor and the game sat frozen on "Configuring splatmaps".
/// </summary>
public static class MigrationLoadingDriver
{
    private const double SliceMs = 100;

    /// <summary>ChunkProviderGenerateWorldFromRaw: migration runs during "Configuring splatmaps".</summary>
    public static void FromRawPostfix(ref IEnumerator __result)
    {
        __result = Wrap(__result, "uiLoadSplats");
    }

    /// <summary>Other providers that build a RegionFileManager in Init.</summary>
    public static void OtherPostfix(ref IEnumerator __result)
    {
        __result = Wrap(__result, null);
    }

    private static IEnumerator Wrap(IEnumerator inner, string restoreTextKey)
    {
        if (inner == null) yield break;
        EagerMigrator.LoadCoroutineActive = true;
        try
        {
            while (inner.MoveNext())
            {
                if (EagerMigrator.HasPendingWork)
                {
                    while (EagerMigrator.HasPendingWork)
                    {
                        EagerMigrator.Step(SliceMs);
                        ShowText(EagerMigrator.ProgressText);
                        yield return null;
                    }
                    ShowText(restoreTextKey != null ? GameCompat.Localize(restoreTextKey) : "PaintUnlocked: save upgrade complete");
                }
                yield return inner.Current;
            }
        }
        finally
        {
            EagerMigrator.LoadCoroutineActive = false;
        }

        // The RegionFileManager was built on the coroutine's last step.
        while (EagerMigrator.HasPendingWork)
        {
            EagerMigrator.Step(SliceMs);
            ShowText(EagerMigrator.ProgressText);
            yield return null;
        }
    }

    private static void ShowText(string text)
    {
        if (GameManager.IsDedicatedServer) return;
        try
        {
            var ui = LocalPlayerUI.primaryUI;
            if (ui != null) XUiC_ProgressWindow.SetText(ui, text, false);
        }
        catch (Exception)
        {
            // Loading-screen text is cosmetic; never let it break the world load.
        }
    }
}
