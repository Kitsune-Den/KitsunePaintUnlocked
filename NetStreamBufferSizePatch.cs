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
/// Fix: let vanilla InitStreams run, then swap the "not full" buffers for ones
/// 8x wider (32 KiB -> 256 KiB). Vanilla's own capacity checks still apply, so
/// this only adds headroom -- it doesn't change when backpressure kicks in.
///
/// This used to be a prefix that replicated InitStreams and skipped the
/// original. 7D2D 3.3 broke that: its send path now serializes each package
/// into a new packageStagingStream (created in InitStreams) and only copies it
/// into the send stream if it fits, dropping the package if serialization
/// throws. The replica never created the staging stream, so every channel-0
/// package would have hit a null writer and been dropped. Running as a postfix
/// means whatever vanilla adds to InitStreams in future still gets initialized.
///
/// On 3.3+ the staging stream is widened too, keeping vanilla's gap between it
/// and the send stream (the reserve for frame headers): a staging stream left
/// at 32 KiB would drop any package the wider send stream could otherwise carry.
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
    // 3.3+ only; null on earlier versions
    private static FieldInfo _fPackageStagingStream;
    private static FieldInfo _fPackageStagingStreamWriter;
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
        _fPackageStagingStream = t.GetField("packageStagingStream", flags);
        _fPackageStagingStreamWriter = t.GetField("packageStagingStreamWriter", flags);

        _reflectionValid = _fReceiveStreamCompressed != null && _fReliableSendStreamUncompressed != null
            && _fReliableSendStreamWriter != null && _fUnreliableSendStreamUncompressed != null
            && _fUnreliableSendStreamWriter != null && _fWriterStream != null && _fFullConnection != null
            // the staging stream comes as a pair or not at all
            && (_fPackageStagingStream == null) == (_fPackageStagingStreamWriter == null);

        if (_reflectionValid)
            Log.Out("[PaintUnlocked] NetConnectionSimple stream fields resolved for buffer-size patch"
                + (_fPackageStagingStream != null ? " (with 3.3+ package staging stream)" : ""));
        else
            Log.Warning("[PaintUnlocked] NetConnectionSimple stream fields NOT fully resolved -- channel-0 buffer widening disabled, falling back to vanilla");
    }

    /// <summary>
    /// Postfix on NetConnectionSimple.InitStreams. After vanilla has built the
    /// "not full" streams, replaces them with wider ones. Leaves the "full" path
    /// (already 2 MiB) alone, as well as vanilla's early return when the
    /// connection was already full, and does nothing if reflection failed.
    /// </summary>
    public static void Postfix(NetConnectionSimple __instance, bool _full)
    {
        if (!_reflectionValid || _full) return;
        if ((bool)_fFullConnection.GetValue(__instance)) return; // vanilla returned early; streams are the 2 MiB set

        var vanillaReliable = _fReliableSendStreamUncompressed.GetValue(__instance) as MemoryStream;
        if (vanillaReliable == null || vanillaReliable.Capacity >= SmallBufferSize) return;

        var receiveStreamCompressed = NewFixedStream(SmallBufferSize);
        receiveStreamCompressed.SetLength(0L);
        _fReceiveStreamCompressed.SetValue(__instance, receiveStreamCompressed);

        var reliableSendStreamUncompressed = NewFixedStream(SmallBufferSize);
        _fReliableSendStreamUncompressed.SetValue(__instance, reliableSendStreamUncompressed);
        _fReliableSendStreamWriter.SetValue(__instance, NewWriter(reliableSendStreamUncompressed));

        var unreliableSendStreamUncompressed = NewFixedStream(SmallBufferSize);
        _fUnreliableSendStreamUncompressed.SetValue(__instance, unreliableSendStreamUncompressed);
        _fUnreliableSendStreamWriter.SetValue(__instance, NewWriter(unreliableSendStreamUncompressed));

        var writerStream = new MemoryStream(new byte[SmallBufferSize]);
        writerStream.SetLength(0L);
        _fWriterStream.SetValue(__instance, writerStream);

        string staging = "";
        if (_fPackageStagingStream != null
            && _fPackageStagingStream.GetValue(__instance) is MemoryStream vanillaStaging)
        {
            int reserve = vanillaReliable.Capacity - vanillaStaging.Capacity;
            if (reserve < 0) reserve = 0;
            var packageStagingStream = NewFixedStream(SmallBufferSize - reserve);
            packageStagingStream.SetLength(0L);
            _fPackageStagingStream.SetValue(__instance, packageStagingStream);
            _fPackageStagingStreamWriter.SetValue(__instance, NewWriter(packageStagingStream));
            staging = $", packageStagingStream.Capacity={packageStagingStream.Capacity}";
        }

        Log.Out($"[PaintUnlocked] InitStreams widened: reliableSendStreamUncompressed.Capacity={reliableSendStreamUncompressed.Capacity}{staging}");
    }

    private static MemoryStream NewFixedStream(int size)
    {
        var buffer = new byte[size];
        return new MemoryStream(buffer, 0, buffer.Length, true, true);
    }

    private static PooledBinaryWriter NewWriter(MemoryStream stream)
    {
        var writer = new PooledBinaryWriter();
        writer.SetBaseStream(stream);
        return writer;
    }
}
