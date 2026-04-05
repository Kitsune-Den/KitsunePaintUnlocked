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
    /// </summary>
    public static void Prefix(int _x, int _y, int _z, ref long _texturefull, int channel)
    {
        if (_texturefull == 0L) return;
        _texturefull = Repack8to10(_texturefull);
    }

    /// <summary>
    /// Prefix on GetSetTextureFullArray: re-encode the TextureFullArray values
    /// from 8-bit to 10-bit before they're swapped into the chunk.
    /// TextureFullArray stores Int64 values indexed by channel (0 = textures).
    /// </summary>
    public static void GetSetPrefix(int _x, int _y, int _z, ref TextureFullArray _texturefullArray)
    {
        // Re-encode each channel's value in the TextureFullArray
        // Channel 0 is the main texture channel
        for (int ch = 0; ch < 1; ch++) // only 1 texture channel
        {
            long val = _texturefullArray[ch];
            if (val != 0L)
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
