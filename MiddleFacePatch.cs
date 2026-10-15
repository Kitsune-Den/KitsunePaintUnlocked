using System.Collections.Generic;

/// <summary>
/// Makes the 7th face slot (BlockFace.Middle) paintable, so "paint all sides" covers it.
///
/// BlockShapeNew has seven face slots: the six sides plus Middle, for geometry that isn't on the
/// block's boundary - the frame parts of the window shapes, the inside of hollow counters,
/// logsHalfCorner (132 shape models, ~1600 blocks on V3.3). The paint brush's "paint all sides"
/// only loops faces 0-5, and chunk storage has no room for a 7th face anyway: vanilla packs
/// 6 x 8 bits into 48, PaintUnlocked 6 x 10 bits into 60 of 64. So those parts always showed the
/// block's default texture - one part left "unpainted" after paint-all, in vanilla too.
///
/// The 4 spare bits can't hold a 10-bit paint, but they can say which side Middle copies:
///   bits 60-62 = 0     Middle unpainted (block default) - what every existing block has
///   bits 60-62 = 1..6  Middle shows the same paint as face 0..5
///   bit 63             unused (kept 0: GetBlockFaceTexture shifts arithmetically)
///
/// Painting Middle points it at a side that already has that paint. After paint-all every side
/// does, so the postfix on paintBlock paints Middle last. Clicking a Middle face with a paint
/// no side has can't be stored, and is left as it is (vanilla can't paint it at all).
/// A Middle face follows its side: repaint that side and the Middle part changes with it.
/// </summary>
public static class MiddleFacePatch
{
    private const int SelectorShift = 60;
    private const long MiddleBits = unchecked((long)0xF000000000000000UL);

    private static readonly Dictionary<int, bool> _hasMiddle = new Dictionary<int, bool>();
    private static bool _loggedUnstorable;

    private static int SideIdx(long raw, int face) => (int)((raw >> (face * 10)) & 0x3FF);

    /// <summary>The paint Middle shows for this packed value (0 = block default).</summary>
    public static int ResolveMiddle(long raw)
    {
        int sel = (int)((raw >> SelectorShift) & 7);
        return sel >= 1 && sel <= 6 ? SideIdx(raw, sel - 1) : 0;
    }

    /// <summary>Prefix on Chunk.Value64FullToIndex (the renderer's read path).</summary>
    public static bool Value64Prefix(long _valueFull, BlockFace _blockFace, ref int __result)
    {
        if (_blockFace != BlockFace.Middle) return true;
        __result = ResolveMiddle(_valueFull);
        return false;
    }

    /// <summary>Prefix on Chunk.GetBlockFaceTexture (game logic, e.g. the brush's current-paint check).</summary>
    public static bool GetPrefix(Chunk __instance, int _x, int _y, int _z, BlockFace _face, int channel, ref int __result)
    {
        if (_face != BlockFace.Middle) return true;
        __result = ResolveMiddle(__instance.GetTextureFull(_x, _y, _z, channel));
        return false;
    }

    /// <summary>Prefix on Chunk.SetBlockFaceTexture: store Middle as "same paint as face N".</summary>
    public static bool SetPrefix(Chunk __instance, int _x, int _y, int _z, BlockFace _face, int _texture, int channel)
    {
        if (_face != BlockFace.Middle) return true;

        long raw = __instance.GetTextureFull(_x, _y, _z, channel);
        int sel = 0;
        if (_texture != 0)
        {
            for (int f = 0; f < 6; f++)
            {
                if (SideIdx(raw, f) == _texture) { sel = f + 1; break; }
            }
            if (sel == 0)
            {
                if (!_loggedUnstorable)
                {
                    _loggedUnstorable = true;
                    Log.Out($"[PaintUnlocked] Middle face can only take a paint one of the block's sides already has (paint {_texture} is on none) - left unchanged. Paint a side with it first, or use paint all sides.");
                }
                return false;
            }
        }

        raw = (raw & ~MiddleBits) | ((long)sel << SelectorShift);
        __instance.SetTextureFull(_x, _y, _z, raw, channel);
        return false;
    }

    /// <summary>
    /// Postfix on ItemActionTextureBlock.paintBlock: with "paint all sides" on, also paint the
    /// Middle face once the six sides are done, for shapes that have Middle geometry.
    /// </summary>
    public static void PaintBlockPostfix(ItemActionTextureBlock __instance, ref ItemActionTextureBlock.EPaintResult __result,
        ChunkCluster _cc, int _entityId, ItemActionTextureBlock.ItemActionTextureBlockData _actionData,
        Vector3i _blockPos, BlockValue _blockValue, ItemActionTextureBlock.ChannelMask _channelMask)
    {
        if (_actionData == null || !_actionData.bPaintAllSides) return;
        if (__result != ItemActionTextureBlock.EPaintResult.Painted && __result != ItemActionTextureBlock.EPaintResult.SamePaint) return;
        if (_blockValue.ischild || !HasMiddleGeometry(_blockValue.Block)) return;

        var middle = __instance.paintFace(_cc, _entityId, _actionData, _blockPos, BlockFace.Middle, _blockValue, _channelMask);
        if (middle == ItemActionTextureBlock.EPaintResult.Painted)
            __result = ItemActionTextureBlock.EPaintResult.Painted;
    }

    private static bool HasMiddleGeometry(Block block)
    {
        if (block == null || !(block.shape is BlockShapeNew shape)) return false;
        lock (_hasMiddle)
        {
            if (_hasMiddle.TryGetValue(block.blockID, out var cached)) return cached;
            bool has = false;
            var meshes = shape.visualMeshes;
            for (int i = (int)BlockFace.Middle; meshes != null && i < meshes.Length; i += 7)
                has |= meshes[i] != null && meshes[i].Vertices != null && meshes[i].Vertices.Count > 0;
            _hasMiddle[block.blockID] = has;
            return has;
        }
    }
}
