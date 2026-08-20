using System.Reflection;

/// <summary>
/// Fixes NetPackageSignDataResponse.GetLength() -- vanilla hardcodes it to
/// always return 0, regardless of the package's real payload. write() emits
/// 5 + data.Length bytes (1 byte isLastBatch + 4 byte int length + the data),
/// so any caller that trusts GetLength() to budget buffer space sees this
/// package as free.
///
/// NetConnectionSimple.WriteToStream's soft pre-write capacity check is
/// exactly that caller: `position + package.GetLength() >= preCompressMaxBufferSize`
/// gates whether to gracefully requeue a package for the next flush instead of
/// writing it now. Because GetLength() always says 0, a SignDataResponse can
/// slip through this check no matter how full the stream already is, and the
/// actual write() call then overflows the fixed-capacity MemoryStream, throwing
/// "Memory stream is not expandable" -- which corrupts the send queue for
/// whatever's written right after it (in our case, one of PaintUnlocked's own
/// GetSetTextureFullArray-driven packages), desyncing the client and
/// disconnecting it.
///
/// This is a genuine pre-existing vanilla bug, unrelated to paint. PaintUnlocked
/// just makes it far more likely to bite: its NetPackageDecoUpdate payloads are
/// large (500KB+ per prefab), so the shared reliable stream is often already
/// most of the way to its 2MB cap by the time a sign-data batch needs to go
/// out alongside it. Fixing GetLength() to report the truth lets the existing
/// soft check do its job -- requeue for the next flush instead of overflowing.
///
/// 7D2D 3.1 fixed this bug upstream: GetLength() now returns
/// `7 + (data?.Length ?? 0)` instead of the hardcoded 0. The postfix therefore
/// only substitutes a value when vanilla still reports 0, so on 3.1+ the game's
/// own (correct) figure is left alone and this patch self-disables. Overwriting
/// it there would have replaced 7+len with a smaller 5+len -- under-reporting is
/// the one direction that breaks a capacity check.
///
/// The 7 vs 5: write() emits isLastBatch (1) + an int length (4) + the data,
/// and the base NetPackage.write() emits the 2-byte package id ahead of that.
/// TFP's 7 counts the package id; we match it so both paths agree, and because
/// over-reporting by 2 is the safe direction for a pre-write capacity check.
/// </summary>
public static class SignDataResponseLengthPatch
{
    private static FieldInfo _fData;
    private static bool _reflectionValid;

    static SignDataResponseLengthPatch()
    {
        _fData = typeof(NetPackageSignDataResponse).GetField("data",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        _reflectionValid = _fData != null;

        if (_reflectionValid)
            Log.Out("[PaintUnlocked] NetPackageSignDataResponse.data field found for GetLength() fix");
        else
            Log.Warning("[PaintUnlocked] NetPackageSignDataResponse.data field NOT found -- GetLength() fix disabled");
    }

    /// <summary>
    /// Postfix on NetPackageSignDataResponse.GetLength(): on game versions that
    /// still return the hardcoded 0, substitute the real serialized size write()
    /// will actually emit. On 3.1+, where vanilla computes a real length itself,
    /// leave the result untouched.
    /// </summary>
    public static void Postfix(NetPackageSignDataResponse __instance, ref int __result)
    {
        if (!_reflectionValid) return;
        if (__result != 0) return;   // 3.1+ reports a real length -- don't clobber it
        var data = (byte[])_fData.GetValue(__instance);
        __result = 7 + (data?.Length ?? 0);
    }
}
