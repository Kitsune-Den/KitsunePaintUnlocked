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
/// <see cref="Verify"/> NEVER changes state, blocks load, or alters behaviour.
/// It only writes an actionable warning to the log. Worst case if something
/// is off, the check silently no-ops ~ it can't make anything worse.
///
/// <see cref="CheckBoundOcbIsFork"/> is the one exception: it runs at mod
/// load, and its verdict decides whether the OCB patches get registered at
/// all, because on stock OCB those patches are what crash painting.xml.
/// </summary>
public static class OcbForkCheck
{
    // Run the check once per game session. Repeating it on every world load
    // would just spam the log with the same line.
    private static bool _done = false;

    private const string OcbAssemblyName = "CustomTextures";
    private const string OcbModName = "OcbCustomTextures";

    /// <summary>
    /// Mod-load-time check that the CustomTextures.dll PaintUnlocked binds to is
    /// the fork. Returns false when it is stock OCB, in which case the caller
    /// must NOT register the OCB-dependent patches.
    ///
    /// Why this can't wait for <see cref="Verify"/>: with stock OCB, the ID-512
    /// floor hands stock InitOpaqueConfig a paint ID that its 256-slot
    /// BlockTextureData.list can't hold, so painting.xml dies with
    /// "Index was outside the bounds of the array" INSIDE InitOpaqueConfig ~
    /// the postfix that runs Verify never fires and the user gets no hint.
    /// Players hit this when another mod quietly bundles its own stock copy
    /// of OCB: it either loads first and shadows the fork ("Mod with same
    /// name ... ignoring"), or sits in a differently named folder and loads
    /// a second CustomTextures.dll side by side.
    ///
    /// Skipping the floor on stock OCB turns the crash into a working game
    /// with custom paints capped at 255, plus an error that names the folder.
    /// All mods' assemblies are loaded before any InitMod runs, so everything
    /// this inspects is already in place.
    /// </summary>
    public static bool CheckBoundOcbIsFork()
    {
        try
        {
            var bound = OcbIntegration.TryGetBoundOcbAssembly();
            if (bound == null) return true; // OCB missing: TryRegister reports that

            var copies = new System.Collections.Generic.List<System.Reflection.Assembly>();
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == OcbAssemblyName) copies.Add(asm);

            var ignored = new System.Collections.Generic.List<string>();
            foreach (var mod in ModManager.GetFailedMods(Mod.EModLoadState.DuplicateModName))
                if (mod != null && mod.Name == OcbModName) ignored.Add(mod.Path);

            bool boundIsFork = IsFork(bound);
            if (boundIsFork && copies.Count <= 1 && ignored.Count == 0)
            {
                Log.Out($"[PaintUnlocked] OCB fork detected at {Describe(bound)}.");
                return true;
            }

            Log.Error("[PaintUnlocked] ================================================");
            if (!boundIsFork)
            {
                Log.Error("[PaintUnlocked] NON-FORKED OcbCustomTextures DETECTED");
                Log.Error("[PaintUnlocked]");
                Log.Error("[PaintUnlocked] You're using a non-forked OcbCustomTextures. PaintUnlocked");
                Log.Error("[PaintUnlocked] needs the OCB fork that ships in its own zip; the stock");
                Log.Error("[PaintUnlocked] one crashes painting.xml with 'Index was outside the");
                Log.Error("[PaintUnlocked] bounds of the array' as soon as custom paints load.");
                Log.Error("[PaintUnlocked]");
                Log.Error($"[PaintUnlocked] Stock OCB in use: {Describe(bound)}");
            }
            else
            {
                Log.Error("[PaintUnlocked] MORE THAN ONE OcbCustomTextures INSTALLED");
                Log.Error("[PaintUnlocked]");
                Log.Error("[PaintUnlocked] Every loaded copy registers the same custom paints, so");
                Log.Error("[PaintUnlocked] painting.xml can fail (e.g. 'No more free Paint IDs')");
                Log.Error("[PaintUnlocked] until only the fork is left.");
                Log.Error("[PaintUnlocked]");
                Log.Error($"[PaintUnlocked] Fork in use: {Describe(bound)}");
            }

            foreach (var asm in copies)
                if (asm != bound)
                    Log.Error($"[PaintUnlocked] Also loaded ({(IsFork(asm) ? "fork" : "stock")}): {Describe(asm)}");
            foreach (var path in ignored)
                Log.Error($"[PaintUnlocked] Ignored by the game (same mod name, loaded later): {path}");

            Log.Error("[PaintUnlocked]");
            Log.Error("[PaintUnlocked] Another mod probably bundles its own copy of OCB. Search");
            Log.Error("[PaintUnlocked] your Mods folder for CustomTextures.dll: there must be");
            Log.Error("[PaintUnlocked] exactly one, inside the OcbCustomTextures folder from the");
            Log.Error("[PaintUnlocked] PaintUnlocked zip. Delete every other OCB copy; the paints");
            Log.Error("[PaintUnlocked] from those mods still load through the fork.");
            if (!boundIsFork)
            {
                Log.Error("[PaintUnlocked]");
                Log.Error("[PaintUnlocked] Until then PaintUnlocked leaves stock OCB alone so the");
                Log.Error("[PaintUnlocked] game still loads, but custom paints stay capped at 255");
                Log.Error("[PaintUnlocked] and paint ID sync is off.");
            }
            Log.Error("[PaintUnlocked] ================================================");

            _done = true; // Verify's list-length check would only repeat this
            return boundIsFork;
        }
        catch (System.Exception ex)
        {
            // A diagnostic must never break anything. Fall back to the old
            // behaviour (register the patches) and move on.
            Log.Warning($"[PaintUnlocked] OCB fork check skipped ({ex.Message}).");
            return true;
        }
    }

    /// <summary>
    /// Every fork build since 1.0.1 carries these two members on OpaqueTextures
    /// (the BlockTextureData.list grow logic); stock OCB has neither.
    /// </summary>
    private static bool IsFork(System.Reflection.Assembly asm)
    {
        const System.Reflection.BindingFlags any =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static;
        var opaque = asm.GetType("OpaqueTextures", false);
        if (opaque == null) return false;
        return opaque.GetNestedType("BlockTextureDataInitPatch", any) != null
            || opaque.GetMethod("EarlyResizeBlockTextureList", any) != null;
    }

    private static string Describe(System.Reflection.Assembly asm)
    {
        try
        {
            var mod = ModManager.GetModForAssembly(asm);
            if (mod != null)
                return string.IsNullOrEmpty(mod.VersionString) ? mod.Path : $"{mod.Path} (v{mod.VersionString})";
        }
        catch (System.Exception) { /* fall through to the assembly name */ }
        return asm.FullName;
    }

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
