using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;

public class PUStreamSelfTestMod : IModApi
{
    public void InitMod(Mod _modInstance) { }
}

// pu_selftest_streams: builds bare NetConnectionSimple instances, runs the (Harmony-patched)
// InitStreams on both paths and pushes a paint package through the 3.3 staging stream.
public class ConsoleCmdStreamSelfTest : ConsoleCmdAbstract
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public override string[] getCommands() => new[] { "pu_selftest_streams" };
    public override string getDescription() => "PaintUnlocked local verification: InitStreams + staging stream";

    static object Get(object o, string f) => typeof(NetConnectionSimple).GetField(f, F)?.GetValue(o) ?? typeof(NetConnectionAbs).GetField(f, F)?.GetValue(o);
    static string Cap(object o, string f) { var s = Get(o, f) as MemoryStream; return s == null ? "null" : s.Capacity.ToString(); }
    static void Out(string s) => Log.Out("[PUSelfTest] " + s);

    static NetConnectionSimple NewConn()
    {
        var c = (NetConnectionSimple)FormatterServices.GetUninitializedObject(typeof(NetConnectionSimple));
        var fStats = typeof(NetConnectionAbs).GetField("stats", F);
        if (fStats != null) fStats.SetValue(c, Activator.CreateInstance(fStats.FieldType, true));
        return c;
    }

    static void Dump(string label, NetConnectionSimple c)
    {
        Out($"{label}: full={Get(c, "fullConnection")} reliableSend={Cap(c, "reliableSendStreamUncompressed")} unreliableSend={Cap(c, "unreliableSendStreamUncompressed")} " +
            $"receive={Cap(c, "receiveStreamCompressed")} writer={Cap(c, "writerStream")} staging={Cap(c, "packageStagingStream")} " +
            $"stagingWriter={(Get(c, "packageStagingStreamWriter") == null ? "null" : "set")}");
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        var init = typeof(NetConnectionSimple).GetMethod("InitStreams", F);
        var stage = typeof(NetConnectionSimple).GetMethod("WriteToPackageStagingStream", F);
        try
        {
            var small = NewConn();
            init.Invoke(small, new object[] { false });
            Dump("not-full", small);

            var full = NewConn();
            init.Invoke(full, new object[] { true });
            Dump("full", full);
            init.Invoke(full, new object[] { false });
            Dump("full, then InitStreams(false)", full);

            if (stage == null) { Out("WriteToPackageStagingStream not present (pre-3.3) - staging check skipped"); Out("RESULT PASS"); return; }

            var pkg = NetPackageManager.GetPackage<NetPackageSetBlockTexture>().Setup(new Vector3i(1, 2, 3), BlockFace.Top, 600, -1, 0);
            int size = (int)stage.Invoke(small, new object[] { pkg });
            Out($"staged NetPackageSetBlockTexture(idx 600) on not-full connection: size={size}");

            // a package bigger than vanilla's 32256-byte not-full staging stream must now fit
            var big = NetPackageManager.GetPackage<NetPackageSignDataResponse>();
            var fData = typeof(NetPackageSignDataResponse).GetField("data", F);
            fData.SetValue(big, new byte[100 * 1024]);
            int bigSize = (int)stage.Invoke(small, new object[] { big });
            Out($"staged 100 KiB NetPackageSignDataResponse on not-full connection: size={bigSize}");
            Out("RESULT PASS");
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            Out($"RESULT FAIL: {inner.GetType().Name}: {inner.Message}");
            Log.Exception(inner);
        }
    }
}
