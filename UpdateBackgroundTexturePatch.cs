using HarmonyLib;
using System.Reflection;

/// <summary>
/// Guards XUiC_ItemStack.updateBackgroundTexture against null refs when the
/// paint TextureID is out of range in the atlas. This happens when custom
/// paints are registered with TextureIDs beyond the vanilla atlas size.
///
/// The vanilla method tries to display a texture thumbnail for the selected
/// paint. If the atlas doesn't have a slot at that TextureID, the underlying
/// texture lookup returns null and the method crashes.
///
/// This patch catches the exception and silently skips the thumbnail display
/// for out-of-range paint IDs, allowing the UI to remain functional.
/// </summary>
[HarmonyPatch]
public static class UpdateBackgroundTexturePatch
{
    static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(XUiC_ItemStack), "updateBackgroundTexture");
    }

    [HarmonyPrefix]
    public static bool Prefix(XUiC_ItemStack __instance)
    {
        try
        {
            return true; // let original run
        }
        catch
        {
            return false; // swallow and skip
        }
    }

    [HarmonyFinalizer]
    public static System.Exception Finalizer(System.Exception __exception)
    {
        if (__exception != null)
        {
            // Silently swallow null refs in texture background update
            // These happen when a custom paint's TextureID is beyond the current atlas
            Log.Out($"[PaintUnlocked] updateBackgroundTexture swallowed: {__exception.GetType().Name}");
        }
        return null; // return null = suppress the exception
    }
}
