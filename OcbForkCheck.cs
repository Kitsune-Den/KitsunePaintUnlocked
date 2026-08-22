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

    /// <summary>
    /// Called when registering the OcbCustomTextures-dependent patches threw.
    /// The raw exception ("Unexpected null in DMD&lt;OpaqueTextures::InitOpaqueConfig&gt;
    /// @ IL_0132: call System.String Localization::Get(System.String,System.Boolean)")
    /// tells a user nothing, so translate it into the actual problem and the fix.
    ///
    /// The dominant cause is an outdated CustomTextures.dll: 7 Days to Die V3.0
    /// changed Localization.Get(string, bool) to Get(string, bool, string), so a
    /// V2.x-era OcbCustomTextures still references an overload that no longer
    /// exists. Harmony copies the original method body to build a patch, hits
    /// the dangling member reference, and throws. Vortex users hit this by
    /// installing 0_PaintUnlocked-X.Y.Z.zip on its own and keeping whatever
    /// OcbCustomTextures they already had.
    /// </summary>
    public static void ReportIntegrationFailure(System.Exception ex)
    {
        // Never let a diagnostic be the thing that breaks mod load.
        try
        {
            // The mod DLL is loaded from bytes, so Assembly.Location is empty ~
            // ask ModManager where the folder actually is instead.
            string where = null, version = null;
            var ocbAssembly = OcbIntegration.TryGetOcbAssembly();
            if (ocbAssembly != null)
            {
                try
                {
                    var mod = ModManager.GetModForAssembly(ocbAssembly);
                    if (mod != null) { where = mod.Path; version = mod.VersionString; }
                }
                catch (System.Exception) { /* fall through to the assembly name */ }
                if (string.IsNullOrEmpty(where)) where = ocbAssembly.FullName;
            }

            // Harmony nests the real cause; Message alone is just
            // "IL Compile Error (unknown location)".
            string chain = DescribeChain(ex);
            bool preV3Localization = MentionsPreV3Localization(chain);

            Log.Error("[PaintUnlocked] ================================================");
            Log.Error("[PaintUnlocked] INCOMPATIBLE OcbCustomTextures ~ paint sync disabled");
            Log.Error("[PaintUnlocked]");

            if (ocbAssembly == null)
            {
                Log.Error("[PaintUnlocked] CustomTextures.dll is not loaded. PaintUnlocked");
                Log.Error("[PaintUnlocked] requires the PaintUnlocked-compatible");
                Log.Error("[PaintUnlocked] OcbCustomTextures fork bundled in its release.");
            }
            else
            {
                Log.Error($"[PaintUnlocked] Loaded from: {where}");
                if (!string.IsNullOrEmpty(version))
                    Log.Error($"[PaintUnlocked] Reported version: {version}");
            }

            if (preV3Localization)
            {
                Log.Error("[PaintUnlocked]");
                Log.Error("[PaintUnlocked] That CustomTextures.dll was built for 7 Days to Die");
                Log.Error("[PaintUnlocked] V2.x. V3.0 replaced Localization.Get(string, bool)");
                Log.Error("[PaintUnlocked] with Get(string, bool, string), so the old build");
                Log.Error("[PaintUnlocked] calls a method that no longer exists. It cannot");
                Log.Error("[PaintUnlocked] register custom paints on this game version, with");
                Log.Error("[PaintUnlocked] or without PaintUnlocked.");
            }

            Log.Error("[PaintUnlocked]");
            Log.Error("[PaintUnlocked] Fix: delete the OcbCustomTextures folder from Mods/ and");
            Log.Error("[PaintUnlocked] replace it with the OcbCustomTextures-X.Y.Z.zip that");
            Log.Error("[PaintUnlocked] shipped alongside this version of PaintUnlocked. Both");
            Log.Error("[PaintUnlocked] mods are versioned together and must match.");
            Log.Error("[PaintUnlocked] Vortex users: install BOTH per-mod zips, not just");
            Log.Error("[PaintUnlocked] 0_PaintUnlocked-X.Y.Z.zip.");
            Log.Error("[PaintUnlocked]");
            Log.Error("[PaintUnlocked] The rest of PaintUnlocked is still active, but custom");
            Log.Error("[PaintUnlocked] paints above 255 will not work until OCB is updated.");
            Log.Error($"[PaintUnlocked] Underlying error: {chain}");
            Log.Error("[PaintUnlocked] ================================================");
        }
        catch (System.Exception inner)
        {
            Log.Warning($"[PaintUnlocked] OCB incompatibility report failed ({inner.Message}).");
        }
    }

    /// <summary>
    /// Flattens an exception and its InnerException chain into one line. The
    /// detail that identifies the incompatibility (the unresolvable member
    /// reference) is always in an inner exception, never in the outer Message.
    /// </summary>
    private static string DescribeChain(System.Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (int depth = 0; ex != null && depth < 8; depth++, ex = ex.InnerException)
        {
            if (sb.Length > 0) sb.Append(" ---> ");
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        }
        return sb.ToString();
    }


    /// <summary>
    /// True if the exception chain names the pre-V3.0 two-argument
    /// Localization.Get overload. Two runtimes word the same failure
    /// differently, so match both spellings:
    ///
    ///   client (MonoMod DMD, from the Cecil MethodReference):
    ///     Unexpected null in DMD&lt;OpaqueTextures::InitOpaqueConfig&gt;
    ///     @ IL_0132: call System.String Localization::Get(System.String,System.Boolean)
    ///   dedicated server (Mono JIT):
    ///     MissingMethodException: Method not found: string .Localization.Get(string,bool)
    /// </summary>
    private static bool MentionsPreV3Localization(string chain)
    {
        if (string.IsNullOrEmpty(chain)) return false;
        string flat = chain.Replace(" ", "");
        return flat.Contains("Localization.Get(string,bool)")
            || flat.Contains("Localization::Get(System.String,System.Boolean)");
    }

}
