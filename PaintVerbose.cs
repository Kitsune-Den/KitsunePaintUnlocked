/// <summary>
/// Runtime switch for per-block diagnostic logging.
///
/// The per-block log sites (WritePrefix, ProcessPackagePrefix, ApplyTexture,
/// PaintChangeTracker) emit 3-4 formatted lines for EVERY painted block face.
/// A paint roller sweep across a wall produces thousands of lines per minute,
/// which is enough main-thread and log-I/O drag on a live server to compound
/// into client timeout kicks during heavy painting (observed 2026-07-18 on the
/// Kitsune Test Den: multiple players dropped with LiteNetLib Timeout while a
/// player mass-painted with extended-range textures).
///
/// Default OFF. Toggle in the console with: pu_debug verbose
/// </summary>
public static class PaintVerbose
{
    public static bool Enabled = false;
}
