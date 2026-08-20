/// <summary>
/// Guards XUiC_MaterialStackGrid.SetMaterials against a stale selected-paint
/// index pointing at an empty BlockTextureData.list slot.
///
/// Vanilla does `selectedMaterial = BlockTextureData.list[newSelectedMaterial]`
/// and immediately reads `.Hidden` (IL_00e7) with no null check. The index
/// comes from the player's held paint tool (ItemActionTextureBlockData.idx) or
/// a held block's TextureFullArray, both of which persist in player save data.
/// If that index refers to a paint that is no longer registered ~ typically a
/// custom pack that failed to load, or stock OCB where packs above 255 never
/// register ~ the slot is null and every XUi frame throws:
///
///   ERR [XUi] Error while updating window group 'materials'
///   EXC Object reference not set ... XUiC_MaterialStackGrid.SetMaterials [0x000e7]
///
/// The exception also prevents IsDirty from clearing, so it repeats each frame
/// and the paint menu is dead. Vanilla can hit this too (idx 154-255 after
/// removing a pack), but PaintUnlocked widens the stored index range and grows
/// the list with null gaps (154-511, and above the last registered custom ID),
/// which makes a stale index far more likely to land on a null slot.
///
/// The guard rewrites an unresolvable index to -1 ("nothing selected") before
/// the original runs. The menu opens normally with no selection instead of
/// crashing; picking any paint writes a fresh valid index to the tool.
/// </summary>
public static class MaterialStackGridGuardPatch
{
    // Log once per distinct stale index, not per frame.
    private static int _lastLoggedIdx = -1;

    public static void SetMaterialsPrefix(ref int newSelectedMaterial)
    {
        if (newSelectedMaterial == -1) return;

        var list = BlockTextureData.list;
        if (list != null && newSelectedMaterial >= 0
            && newSelectedMaterial < list.Length
            && list[newSelectedMaterial] != null) return;

        if (newSelectedMaterial != _lastLoggedIdx)
        {
            _lastLoggedIdx = newSelectedMaterial;
            Log.Warning($"[PaintUnlocked] Paint menu: selected paint idx {newSelectedMaterial} " +
                        "has no registered texture (missing/failed paint pack?) — clearing selection.");
        }
        newSelectedMaterial = -1;
    }
}
