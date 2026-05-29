/// <summary>
/// Diagnostic-only check that the loaded OcbCustomTextures is the
/// PaintUnlocked-compatible fork, not stock OCB.
///
/// Why this exists: stock OcbCustomTextures never grows BlockTextureData.list
/// past its vanilla default of 256 entries. The PaintUnlocked fork resizes it
/// to hold custom paint IDs (which start at 512). When a user installs stock
/// OCB by mistake ~ e.g. they grabbed it from its own mod page instead of
/// using the fork bundled with PaintUnlocked ~ custom paints above 255
/// silently fail to register. The visible symptoms are subtle and confusing:
/// the texture dropper/eyedropper grabs nothing, high paints don't apply,
/// painting.xml may error. Multiple users have hit this "wrong OCB" footgun
/// in different ways, and the root cause is invisible without this check.
///
/// This is a BEHAVIOURAL check, not a version-string parse: it inspects
/// BlockTextureData.list.Length after OCB's own InitOpaqueConfig has run.
/// That directly measures the thing that actually matters (did the list get
/// resized) rather than trusting a ModInfo string.
///
/// It NEVER changes state, blocks load, or alters behaviour. It only writes
/// an actionable warning to the log. Worst case if something is off, the
/// check silently no-ops ~ it can't make anything worse.
/// </summary>
public static class OcbForkCheck
{
    // Run the check once per game session. Repeating it on every world load
    // would just spam the log with the same line.
    private static bool _done = false;

    /// <summary>
    /// Called once, as part of the InitOpaqueConfig postfix. By this point
    /// the fork (if installed) has already resized BlockTextureData.list.
    /// </summary>
    public static void Verify()
    {
        if (_done) return;
        _done = true;

        try
        {
            var list = BlockTextureData.list;
            int len = list?.Length ?? 0;

            // Vanilla BlockTextureData.InitStatic() allocates exactly 256.
            // The PaintUnlocked fork resizes well past that (to >=768) so it
            // can hold custom IDs at 512+. So len <= 256 means the fork's
            // resize never happened ~ the user is on stock OCB (or OCB is
            // missing / failed to init).
            if (len > 256)
            {
                Log.Out($"[PaintUnlocked] OCB fork check passed (BlockTextureData.list = {len} slots).");
                return;
            }

            Log.Error("[PaintUnlocked] ================================================");
            Log.Error("[PaintUnlocked] WRONG OcbCustomTextures DETECTED");
            Log.Error($"[PaintUnlocked] BlockTextureData.list is only {len} slots ~ the");
            Log.Error("[PaintUnlocked] PaintUnlocked-compatible OCB fork did not resize it.");
            Log.Error("[PaintUnlocked] You are most likely running STOCK OcbCustomTextures.");
            Log.Error("[PaintUnlocked]");
            Log.Error("[PaintUnlocked] Consequence: custom paints above 255 will not register.");
            Log.Error("[PaintUnlocked] The texture dropper/eyedropper grabs nothing, and high");
            Log.Error("[PaintUnlocked] paints will not apply.");
            Log.Error("[PaintUnlocked]");
            Log.Error("[PaintUnlocked] Fix: delete the OcbCustomTextures folder from Mods/ and");
            Log.Error("[PaintUnlocked] replace it with the fork bundled in the PaintUnlocked");
            Log.Error("[PaintUnlocked] download.");
            Log.Error("[PaintUnlocked] ================================================");
        }
        catch (System.Exception ex)
        {
            // A diagnostic must never break anything. Swallow and move on.
            Log.Warning($"[PaintUnlocked] OCB fork check skipped ({ex.Message}).");
        }
    }
}
