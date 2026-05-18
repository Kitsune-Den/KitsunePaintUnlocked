using HarmonyLib;

/// <summary>
/// Forces chunk.isModified = true after any paint operation.
///
/// Vanilla 7DTD has a long-standing intermittent bug where painted faces
/// occasionally don't save to disk. The most likely cause is that some
/// paint code paths set the face texture without flagging the chunk as
/// modified, so the save serializer skips writing the chunk. On reload,
/// the affected faces appear unpainted.
///
/// This patch hooks every entry point that modifies face textures and
/// unconditionally marks the chunk dirty afterward, so the save layer
/// always knows the chunk needs persisting.
///
/// Affects both vanilla and custom paints. Marked experimental ~ if it
/// causes false-positive saves or perf issues, revert.
/// </summary>
public static class ChunkPaintDirtyPatch
{
    /// <summary>
    /// Postfix on Chunk.SetBlockFaceTexture ~ the per-face paint entry point.
    /// </summary>
    public static void SetBlockFaceTexturePostfix(Chunk __instance)
    {
        if (__instance == null) return;
        __instance.isModified = true;
        ChunkSaveGuardPatch.NotePaintOp(__instance);
    }

    /// <summary>
    /// Postfix on Chunk.SetTextureFull ~ paints all 6 faces of a block at once.
    /// </summary>
    public static void SetTextureFullPostfix(Chunk __instance)
    {
        if (__instance == null) return;
        __instance.isModified = true;
        ChunkSaveGuardPatch.NotePaintOp(__instance);
    }

    /// <summary>
    /// Postfix on Chunk.GetSetTextureFullArray ~ swap-style array operation used
    /// during prefab placement. We already prefix this for 8→10 bit re-encoding;
    /// this postfix additionally ensures the dirty flag is set.
    /// </summary>
    public static void GetSetTextureFullArrayPostfix(Chunk __instance)
    {
        if (__instance == null) return;
        __instance.isModified = true;
        ChunkSaveGuardPatch.NotePaintOp(__instance);
    }
}
