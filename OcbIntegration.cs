using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

/// <summary>
/// All Harmony registration that touches OcbCustomTextures types, isolated
/// behind one guarded entry point.
///
/// Why this is its own class: everything here resolves <c>OpaqueTextures</c>,
/// which lives in the third-party CustomTextures.dll. The JIT resolves a
/// method's type references when that method is first compiled, so as long as
/// these calls sat inline in <c>PaintUnlockedMod.InitMod</c>, a missing or
/// incompatible CustomTextures.dll took the whole of InitMod down with it —
/// every patch registered after the failure point was silently lost and the
/// game logged only a raw MonoMod IL error with no hint at the cause:
///
///     Failed initializing ModAPI instance on mod 'PaintUnlocked'
///     IL Compile Error ---> Unexpected null in DMD&lt;OpaqueTextures::InitOpaqueConfig&gt;
///       @ IL_0132: call System.String Localization::Get(System.String,System.Boolean)
///
/// That signature is the tell for a pre-V3.0 OcbCustomTextures: V3.0 replaced
/// <c>Localization.Get(string, bool)</c> with <c>Get(string, bool, string)</c>,
/// so a V2.x-era CustomTextures.dll carries a member reference that no longer
/// resolves. Harmony has to copy the original method body to build the patch,
/// hits the dangling reference, and throws.
///
/// With the OCB-dependent work behind <see cref="Register"/> (marked
/// NoInlining so its type references are resolved on call, not when InitMod is
/// jitted), the failure is caught, explained in the log, and the rest of the
/// mod still installs.
/// </summary>
public static class OcbIntegration
{
    /// <summary>
    /// Registers the OCB-dependent patches. Returns false (after logging an
    /// actionable diagnostic) if OcbCustomTextures is missing or incompatible.
    /// </summary>
    public static bool TryRegister(Harmony harmony)
    {
        try
        {
            Register(harmony);
            return true;
        }
        catch (Exception ex)
        {
            OcbForkCheck.ReportIntegrationFailure(ex);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register(Harmony harmony)
    {
        // === Layer 2: Paint ID allocation floor ===
        // Seed GetFreePaintID at 512 so server and client allocate identical
        // custom paint IDs despite loading different numbers of vanilla paints.
        var getFreePaintID = AccessTools.Method(typeof(OpaqueTextures), "GetFreePaintID");
        if (getFreePaintID != null)
        {
            harmony.Patch(getFreePaintID,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(OcbPaintLimitPatch), "GetFreePaintIDPrefix")));
            Log.Out("[PaintUnlocked] OpaqueTextures.GetFreePaintID prefix registered (ID floor 512)");
        }
        else Log.Warning("[PaintUnlocked] OpaqueTextures.GetFreePaintID not found — paint ID floor disabled");

        // === Server-authoritative paint ID sync ===
        // After InitOpaqueConfig: build the server's ID mapping
        var initOpaqueConfig = AccessTools.Method(typeof(OpaqueTextures), "InitOpaqueConfig");
        if (initOpaqueConfig != null)
        {
            harmony.Patch(initOpaqueConfig,
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PaintIdSyncManager), "OnInitOpaqueConfigDone")));
            Log.Out("[PaintUnlocked] InitOpaqueConfig postfix registered for paint ID mapping");
        }
        else Log.Warning("[PaintUnlocked] InitOpaqueConfig not found — paint ID sync disabled");

        // On client connect: send the mapping before chunks flow
        var requestToEnter = AccessTools.Method(typeof(NetPackageRequestToEnterGame), "ProcessPackage");
        if (requestToEnter != null)
        {
            harmony.Patch(requestToEnter,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PaintIdSyncManager), "OnRequestToEnterGamePrefix")));
            Log.Out("[PaintUnlocked] RequestToEnterGame prefix registered for paint ID sync");
        }
        else Log.Warning("[PaintUnlocked] NetPackageRequestToEnterGame.ProcessPackage not found — paint ID sync disabled");
    }

    /// <summary>
    /// The loaded CustomTextures assembly, or null if OcbCustomTextures is not
    /// installed. Deliberately reflection-only: if the OCB types cannot be
    /// loaded at all there is nothing to report but the absence.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Assembly TryGetOcbAssembly()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "CustomTextures") return asm;
        }
        catch (Exception) { /* diagnostics must never throw */ }
        return null;
    }

}
