using System.IO;
using System.Reflection;

/// <summary>
/// Widens NetConnectionSimple's channel-0 ("not full") send/receive buffers.
///
/// Vanilla allocates fixed, non-expandable MemoryStream buffers per connection:
/// 2 MiB for "full" connections, 32 KiB for the smaller "not full" path used for
/// every real player's channel-0 connection. PaintUnlocked widens chunk texture
/// storage from 48-bit to 64-bit unconditionally (even with zero custom paints
/// loaded), making every chunk-texture payload ~33% bigger. That's enough,
/// combined with the connection/spawn burst (localization + paint-ID map + POI
/// decoration writes), to occasionally overrun the 32 KiB buffer mid-write,
/// throwing "Memory stream is not expandable" and corrupting the send queue --
/// the next package written after that (often our own GetSetTextureFullArray
/// re-encode) desyncs the client's read cursor and gets it disconnected.
///
/// Fix: replicate NetConnectionSimple.InitStreams exactly, but with the
/// "not full" buffers widened 8x (32 KiB -> 256 KiB). The existing soft
/// pre-write capacity check (preCompressMaxBufferSize, untouched) still fires
/// well before the new, bigger buffer's real capacity, so this only adds
/// headroom -- it doesn't change when backpressure/requeueing kicks in.
/// </summary>
public static class NetStreamBufferSizePatch
{
    private const int SmallBufferSize = 32768 * 8; // 256 KiB, was 32 KiB

    private static FieldInfo _fReceiveStreamCompressed;
    private static FieldInfo _fReliableSendStreamUncompressed;
    private static FieldInfo _fReliableSendStreamWriter;
    private static FieldInfo _fUnreliableSendStreamUncompressed;
    private static FieldInfo _fUnreliableSendStreamWriter;
    private static FieldInfo _fWriterStream;
    private static FieldInfo _fFullConnection;
    private static bool _reflectionValid;

    static NetStreamBufferSizePatch()
    {
        var t = typeof(NetConnectionSimple);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        _fReceiveStreamCompressed = t.GetField("receiveStreamCompressed", flags);
        _fReliableSendStreamUncompressed = t.GetField("reliableSendStreamUncompressed", flags);
        _fReliableSendStreamWriter = t.GetField("reliableSendStreamWriter", flags);
        _fUnreliableSendStreamUncompressed = t.GetField("unreliableSendStreamUncompressed", flags);
        _fUnreliableSendStreamWriter = t.GetField("unreliableSendStreamWriter", flags);
        _fWriterStream = t.GetField("writerStream", flags);
        _fFullConnection = t.GetField("fullConnection", flags);

        _reflectionValid = _fReceiveStreamCompressed != null && _fReliableSendStreamUncompressed != null
            && _fReliableSendStreamWriter != null && _fUnreliableSendStreamUncompressed != null
            && _fUnreliableSendStreamWriter != null && _fWriterStream != null && _fFullConnection != null;

        if (_reflectionValid)
            Log.Out("[PaintUnlocked] NetConnectionSimple stream fields resolved for buffer-size patch");
        else
            Log.Warning("[PaintUnlocked] NetConnectionSimple stream fields NOT fully resolved -- channel-0 buffer widening disabled, falling back to vanilla");
    }

    /// <summary>
    /// Prefix on NetConnectionSimple.InitStreams. Skips the original for the
    /// "not full" path and replicates it with a wider buffer size. Falls through
    /// to vanilla for the "full" path (already 2 MiB, not the overflow path) and
    /// whenever reflection failed to resolve the backing fields.
    /// </summary>
    public static bool Prefix(NetConnectionSimple __instance, bool _full)
    {
        if (!_reflectionValid || _full) return true;

        var fullConnection = (bool)_fFullConnection.GetValue(__instance);
        if (fullConnection) return false; // vanilla no-ops here too; already initialized

        var array = new byte[SmallBufferSize];
        var receiveStreamCompressed = new MemoryStream(array, 0, array.Length, true, true);
        receiveStreamCompressed.SetLength(0L);
        _fReceiveStreamCompressed.SetValue(__instance, receiveStreamCompressed);

        var array2 = new byte[SmallBufferSize];
        var reliableSendStreamUncompressed = new MemoryStream(array2, 0, array2.Length, true, true);
        _fReliableSendStreamUncompressed.SetValue(__instance, reliableSendStreamUncompressed);
        var reliableWriter = new PooledBinaryWriter();
        reliableWriter.SetBaseStream(reliableSendStreamUncompressed);
        _fReliableSendStreamWriter.SetValue(__instance, reliableWriter);

        var array3 = new byte[SmallBufferSize];
        var unreliableSendStreamUncompressed = new MemoryStream(array3, 0, array3.Length, true, true);
        _fUnreliableSendStreamUncompressed.SetValue(__instance, unreliableSendStreamUncompressed);
        var unreliableWriter = new PooledBinaryWriter();
        unreliableWriter.SetBaseStream(unreliableSendStreamUncompressed);
        _fUnreliableSendStreamWriter.SetValue(__instance, unreliableWriter);

        var writerStream = new MemoryStream(new byte[SmallBufferSize]);
        writerStream.SetLength(0L);
        _fWriterStream.SetValue(__instance, writerStream);

        var readBack = (MemoryStream)_fReliableSendStreamUncompressed.GetValue(__instance);
        Log.Out($"[PaintUnlocked] InitStreams patched: reliableSendStreamUncompressed.Capacity={readBack.Capacity}");

        return false;
    }
}
