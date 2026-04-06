using HarmonyLib;
using System.Reflection;

/// <summary>
/// Re-encodes raw Int64 texture values from vanilla 8-bit face packing to 10-bit packing.
///
/// Prefab placement uses GetSetTextureFullArray which passes pre-packed Int64 values
/// where each face's paint index occupies 8 bits at positions 0,8,16,24,32,40.
/// Our 10-bit patched GetBlockFaceTexture/Value64FullToIndex reads at 0,10,20,30,40,50.
///
/// The fix: re-encode the TextureFullArray values from 8-bit to 10-bit before the
/// swap operation writes them into the chunk.
/// </summary>
public static class TextureFullRepackPatch
{
    private static bool _loggedOnce = false;

    /// <summary>
    /// 8-bit packing uses bits 0-47 (6 faces x 8 bits). Bits 48-63 are always zero.
    /// 10-bit packing uses bits 0-59 (6 faces x 10 bits). Bits 48-59 can be non-zero.
    /// If bits 48+ are set, the data is already 10-bit and must NOT be re-encoded.
    /// </summary>
    private static bool IsAlready10Bit(long value)
    {
        return (value & unchecked((long)0xFFFF000000000000UL)) != 0L;
    }

    /// <summary>
    /// Re-packs an 8-bit packed Int64 to 10-bit packed format.
    /// </summary>
    public static long Repack8to10(long value)
    {
        if (value == 0L) return 0L;
        long repacked = 0;
        for (int face = 0; face < 6; face++)
        {
            int idx = (int)((value >> (face * 8)) & 0xFF);
            repacked |= ((long)(idx & 0x3FF) << (face * 10));
        }
        return repacked;
    }

    /// <summary>
    /// Prefix on SetTextureFull: re-encode from 8-bit to 10-bit.
    /// Skips values that are already in 10-bit format (defense against double-encoding
    /// if this method is ever called with already-packed chunk data on save reload).
    /// </summary>
    public static void Prefix(int _x, int _y, int _z, ref long _texturefull, int channel)
    {
        if (_texturefull == 0L) return;
        if (IsAlready10Bit(_texturefull)) return;
        _texturefull = Repack8to10(_texturefull);
    }

    /// <summary>
    /// Prefix on GetSetTextureFullArray: re-encode the TextureFullArray values
    /// from 8-bit to 10-bit before they're swapped into the chunk.
    /// Skips values that are already in 10-bit format.
    /// </summary>
    public static void GetSetPrefix(int _x, int _y, int _z, ref TextureFullArray _texturefullArray)
    {
        for (int ch = 0; ch < 1; ch++)
        {
            long val = _texturefullArray[ch];
            if (val != 0L && !IsAlready10Bit(val))
            {
                if (!_loggedOnce)
                {
                    Log.Out($"[PaintUnlocked] GetSetTextureFullArray re-encoding: ch={ch} val=0x{val:X16} at ({_x},{_y},{_z})");
                    _loggedOnce = true;
                }
                _texturefullArray[ch] = Repack8to10(val);
            }
        }
    }
}
