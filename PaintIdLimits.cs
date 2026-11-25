/// <summary>
/// The custom paint ID range, in one place. Pure (no game types) so the unit tests can
/// link it alongside PaintIdMerge.
///
///   0 .. 255      vanilla + anything a painting.xml mod defines with a fixed id. Vanilla
///                 uses 0-183 (156 paints, unchanged from V2.6 through V3.3); pre-PaintUnlocked
///                 paint mods and stock OCB put their paints in the rest of this range.
///   256 .. 1023   custom paints registered through the OCB fork.
///
/// 1023 is the hard ceiling: chunk storage keeps 10 bits per face (ChunkTexturePatch), so a
/// higher ID would be saved as id &amp; 0x3FF, i.e. a different paint.
/// </summary>
public static class PaintIdLimits
{
    /// <summary>First ID handed to a custom paint.</summary>
    public const ushort CustomIdFloor = 256;

    /// <summary>
    /// The floor every release before 1.6.0 used. No build ever assigned an ID in
    /// [CustomIdFloor, LegacyIdFloor), so that band is guaranteed unpainted in every
    /// existing world and safe to hand out to new paints.
    /// </summary>
    public const ushort LegacyIdFloor = 512;

    /// <summary>Highest paint ID a block face can store (10-bit chunk storage).</summary>
    public const ushort MaxPaintId = 1023;

    /// <summary>How many custom paints fit: 768.</summary>
    public const int CustomCapacity = MaxPaintId - CustomIdFloor + 1;
}
