using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// pu_selftest_middle: lists the block shapes that have geometry in the 7th face slot
// (BlockFace.Middle, visualMeshes[6 + 7k]); paint-all only paints faces 0-5. Then paints a Middle
// face directly and reads it back, to show what storage does with face 6.
public class ConsoleCmdMiddleFaceSelfTest : ConsoleCmdAbstract
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public override string[] getCommands() => new[] { "pu_selftest_middle" };
    public override string getDescription() => "PaintUnlocked local verification: shapes with Middle-face geometry";
    static void Out(string s) => Log.Out("[PUMiddle] " + s);

    static bool HasGeometry(object mesh)
    {
        if (mesh == null) return false;
        var verts = mesh.GetType().GetField("Vertices", F)?.GetValue(mesh) as System.Collections.ICollection;
        return verts != null && verts.Count > 0;
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            var fVisual = typeof(BlockShapeNew).GetField("visualMeshes", F);
            var fCollider = typeof(BlockShapeNew).GetField("colliderMeshes", F);
            var byModel = new Dictionary<string, List<string>>();
            int paintableShapes = 0;
            foreach (var block in Block.list)
            {
                if (block == null || !(block.shape is BlockShapeNew shape) || block.MeshIndex != 0) continue;
                paintableShapes++;
                var visual = fVisual.GetValue(shape) as Array;
                var collider = fCollider.GetValue(shape) as Array;
                bool middle = false;
                for (int i = 6; visual != null && i < visual.Length; i += 7) middle |= HasGeometry(visual.GetValue(i));
                if (collider != null && collider.Length > 6) middle |= HasGeometry(collider.GetValue(6));
                if (!middle) continue;
                string model = block.Properties.Values.ContainsKey("Model") ? block.Properties.Values["Model"] : "?";
                if (!byModel.TryGetValue(model, out var names)) byModel[model] = names = new List<string>();
                names.Add(block.GetBlockName());
            }
            int blocks = byModel.Values.Sum(v => v.Count);
            Out($"{blocks} of {paintableShapes} paintable blocks have Middle-face geometry, across {byModel.Count} shape models:");
            foreach (var kv in byModel.OrderByDescending(k => k.Value.Count))
                Out($"  {kv.Key}: {kv.Value.Count} blocks, e.g. {string.Join(", ", kv.Value.Take(3))}");

            // What storage does with face 6
            var chunk = MemoryPools.PoolChunks.AllocSync(true);
            foreach (var idx in new[] { 5, 200, 600 })
            {
                chunk.SetTextureFull(0, 10, 0, 0L, 0);
                for (int f = 0; f <= 5; f++) chunk.SetBlockFaceTexture(0, 10, 0, (BlockFace)f, idx, 0);
                chunk.SetBlockFaceTexture(0, 10, 0, BlockFace.Middle, idx, 0);
                long raw = chunk.GetTextureFull(0, 10, 0, 0);
                Out($"paint faces 0-6 with {idx}: sides read {chunk.GetBlockFaceTexture(0, 10, 0, BlockFace.East, 0)}, Middle reads {chunk.GetBlockFaceTexture(0, 10, 0, BlockFace.Middle, 0)} (renderer: {Chunk.Value64FullToIndex(raw, BlockFace.Middle)})  raw=0x{raw:X16}");
            }
            Out("RESULT DONE");
        }
        catch (Exception ex)
        {
            Out("RESULT ERROR " + ex);
        }
    }
}

// pu_selftest_middlepaint: the Middle-face fix end to end on a block that has Middle geometry.
// Runs PaintUnlocked's real paintBlock postfix (which calls the game's own paintFace for Middle),
// the client packet path for face 6, single-face edge cases and a save/load round trip.
public class ConsoleCmdMiddlePaintSelfTest : ConsoleCmdAbstract
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public override string[] getCommands() => new[] { "pu_selftest_middlepaint" };
    public override string getDescription() => "PaintUnlocked local verification: Middle face painting";
    static int _fails;
    static void Out(string s) => Log.Out("[PUMidPaint] " + s);

    static void Expect(string label, int got, int want)
    {
        if (got != want) _fails++;
        Out($"{label}: got {got}, want {want}  {(got == want ? "OK" : "FAIL")}");
    }

    static int Middle(Chunk c, int x, int y, int z) => c.GetBlockFaceTexture(x, y, z, BlockFace.Middle, 0);
    static int Rendered(Chunk c, int x, int y, int z) => Chunk.Value64FullToIndex(c.GetTextureFull(x, y, z, 0), BlockFace.Middle);

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        _fails = 0;
        try
        {
            var world = GameManager.Instance.World;
            var cc = world.ChunkCache;
            const int cx = 3, cz = 3, y = 100;
            var chunk = cc.GetChunkSync(WorldChunkCache.MakeChunkKey(cx, cz)) as Chunk;
            if (chunk == null)
            {
                chunk = MemoryPools.PoolChunks.AllocSync(true);
                typeof(Chunk).GetField("m_X", F).SetValue(chunk, cx);
                typeof(Chunk).GetProperty("Z", F).SetValue(chunk, cz);
                cc.AddChunkSync(chunk);
            }
            var bv = Block.GetBlockValue("woodShapes:counterHollowTop");
            Out($"block {bv.Block.GetBlockName()} (id {bv.type})");

            // The brush: an ItemActionTextureBlock with infinite paint and paint-all on.
            var action = (ItemActionTextureBlock)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ItemActionTextureBlock));
            for (var t = typeof(ItemActionTextureBlock); t != null; t = t.BaseType)
            {
                var fi = t.GetField("InfiniteAmmo", F);
                if (fi != null) { fi.SetValue(action, true); break; }
                var pi = t.GetProperty("InfiniteAmmo", F);
                if (pi != null && pi.CanWrite) { pi.SetValue(action, true); break; }
            }
            var data = (ItemActionTextureBlock.ItemActionTextureBlockData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ItemActionTextureBlock.ItemActionTextureBlockData));
            data.bPaintAllSides = true;
            data.channelMask = new ItemActionTextureBlock.ChannelMask(0);
            var postfix = Type.GetType("MiddleFacePatch, PaintUnlocked").GetMethod("PaintBlockPostfix", F);

            // Registered paint IDs only: the renderer path clamps unregistered IDs to 0.
            var list = BlockTextureData.list;
            int vanillaId = Enumerable.Range(100, 156).First(i => list[i] != null);
            var custom = Enumerable.Range(512, list.Length - 512).Where(i => list[i] != null).Take(2).ToArray();
            Out($"using paint IDs {vanillaId} (vanilla), {custom[0]} and {custom[1]} (custom)");

            var ids = new[] { vanillaId, custom[0] };
            for (int n = 0; n < ids.Length; n++)
            {
                int idx = ids[n];
                int lx = n;
                var pos = new Vector3i(cx * 16 + lx, y, cz * 16);
                chunk.SetBlockRaw(lx, y, 0, bv);
                chunk.SetTextureFull(lx, y, 0, 0L, 0);
                Expect($"[idx {idx}] before paint-all, Middle (logic)", Middle(chunk, lx, y, 0), 0);

                // 1. paint-all: brush loop over faces 0-5, then PaintUnlocked's postfix paints Middle
                data.idx = idx;
                for (int f = 0; f <= 5; f++) action.paintFace(cc, -1, data, pos, (BlockFace)f, bv, data.channelMask);
                var args = new object[] { action, ItemActionTextureBlock.EPaintResult.Painted, cc, -1, data, pos, bv, data.channelMask };
                postfix.Invoke(null, args);
                Expect($"[idx {idx}] paint-all, East side", chunk.GetBlockFaceTexture(lx, y, 0, BlockFace.East, 0), idx);
                Expect($"[idx {idx}] paint-all, Middle (logic)", Middle(chunk, lx, y, 0), idx);
                Expect($"[idx {idx}] paint-all, Middle (renderer)", Rendered(chunk, lx, y, 0), idx);
                Out($"[idx {idx}] raw=0x{chunk.GetTextureFull(lx, y, 0, 0):X16}  postfix result={args[1]}");
            }

            // 2. client packets: six sides then Middle, over the wire
            {
                const int lx = 2; var pos = new Vector3i(cx * 16 + lx, y, cz * 16);
                chunk.SetBlockRaw(lx, y, 0, bv); chunk.SetTextureFull(lx, y, 0, 0L, 0);
                foreach (var f in new[] { 0, 1, 2, 3, 4, 5, 6 }) ConsoleCmdPaintAllSelfTest_Send(pos, (BlockFace)f, custom[1]);
                Expect($"[packets {custom[1]}] Middle (renderer)", Rendered(chunk, lx, y, 0), custom[1]);
            }

            // 3. single-face Middle with a paint no side has -> unchanged; 4. Middle follows its side
            {
                const int lx = 3; var pos = new Vector3i(cx * 16 + lx, y, cz * 16);
                chunk.SetBlockRaw(lx, y, 0, bv); chunk.SetTextureFull(lx, y, 0, 0L, 0);
                for (int f = 0; f <= 5; f++) chunk.SetBlockFaceTexture(lx, y, 0, (BlockFace)f, 50 + f, 0);   // all sides different
                chunk.SetBlockFaceTexture(lx, y, 0, BlockFace.Middle, 999, 0);
                Expect("[single] Middle with a paint no side has stays", Middle(chunk, lx, y, 0), 0);
                chunk.SetBlockFaceTexture(lx, y, 0, BlockFace.Middle, 53, 0);                                  // = West's paint
                Expect("[single] Middle with West's paint", Middle(chunk, lx, y, 0), 53);
                chunk.SetBlockFaceTexture(lx, y, 0, BlockFace.West, 77, 0);
                Expect("[single] Middle follows West after repaint", Rendered(chunk, lx, y, 0), 77);
                chunk.SetBlockFaceTexture(lx, y, 0, BlockFace.Middle, 0, 0);
                Expect("[single] Middle cleared", Rendered(chunk, lx, y, 0), 0);
                Expect("[single] West untouched by Middle clear", chunk.GetBlockFaceTexture(lx, y, 0, BlockFace.West, 0), 77);
            }

            // 6. existing 10-bit data with no selector: Middle stays default
            {
                const int lx = 4;
                long legacy = 0;
                for (int f = 0; f < 6; f++) legacy |= 300L << (f * 10);
                chunk.SetTextureFull(lx, y, 0, legacy, 0);
                Expect("[existing block] Middle stays default", Rendered(chunk, lx, y, 0), 0);
            }

            // 5. save / load round trip
            var ms = new MemoryStream();
            var w = MemoryPools.poolBinaryWriter.AllocSync(true); w.SetBaseStream(ms); chunk.save(w); w.Flush(); MemoryPools.poolBinaryWriter.FreeSync(w);
            ms.Position = 0;
            var r = MemoryPools.poolBinaryReader.AllocSync(true); r.SetBaseStream(ms);
            var copy = MemoryPools.PoolChunks.AllocSync(true);
            uint ver = 0;
            foreach (var n in new[] { "CurrentSaveVersion", "cSaveVersion", "SaveVersion" }) { var fi = typeof(Chunk).GetField(n, F); if (fi != null) { ver = Convert.ToUInt32(fi.GetValue(null)); break; } }
            copy.load(r, ver); MemoryPools.poolBinaryReader.FreeSync(r);
            Expect($"[save/load] Middle (paint-all {custom[0]} block)", Rendered(copy, 1, y, 0), custom[0]);
            Expect($"[save/load] Middle (packets {custom[1]} block)", Rendered(copy, 2, y, 0), custom[1]);

            Out(_fails == 0 ? "RESULT PASS" : $"RESULT FAIL ({_fails} checks)");
        }
        catch (Exception ex)
        {
            Out("RESULT ERROR " + ex);
        }
    }

    static void ConsoleCmdPaintAllSelfTest_Send(Vector3i pos, BlockFace face, int idx)
    {
        var sent = NetPackageManager.GetPackage<NetPackageSetBlockTexture>().Setup(pos, face, idx, 1234, 0);
        var ms = new MemoryStream();
        var w = MemoryPools.poolBinaryWriter.AllocSync(true); w.SetBaseStream(ms); sent.write(w); w.Flush(); MemoryPools.poolBinaryWriter.FreeSync(w);
        ms.Position = ms.Length - 19;
        var r = MemoryPools.poolBinaryReader.AllocSync(true); r.SetBaseStream(ms);
        var received = NetPackageManager.GetPackage<NetPackageSetBlockTexture>();
        received.read(r); MemoryPools.poolBinaryReader.FreeSync(r);
        received.ProcessPackage(GameManager.Instance.World, GameManager.Instance);
    }
}

public class PUPaintAllSelfTestMod : IModApi
{
    public void InitMod(Mod _modInstance) { }
}

// pu_selftest_paintall: reproduces the paint brush's "paint all sides" on a dedicated server and
// reads every face back. A = the server-side SetBlockTextureServer loop (what the brush calls per
// face), B = six NetPackageSetBlockTexture packets serialized and processed the way the server
// receives them from a client, C = a chunk save/load round trip. Bound by reflection where V2.6 and
// V3.x differ, so one build runs on both.
public class ConsoleCmdPaintAllSelfTest : ConsoleCmdAbstract
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly int[] Ids = { 5, 200, 255, 256, 600, 1023 };
    static int _fails;

    public override string[] getCommands() => new[] { "pu_selftest_paintall" };
    public override string getDescription() => "PaintUnlocked local verification: paint all six faces and read them back";

    static void Out(string s) => Log.Out("[PUPaintAll] " + s);

    static string Faces(Chunk c, int x, int y, int z)
    {
        var faces = Enumerable.Range(0, 6).Select(f => c.GetBlockFaceTexture(x, y, z, (BlockFace)f, 0)).ToArray();
        return string.Join(",", faces) + $"  raw=0x{c.GetTextureFull(x, y, z, 0):X16}";
    }

    static void Check(string label, Chunk c, int x, int y, int z, int idx)
    {
        var faces = Enumerable.Range(0, 6).Select(f => c.GetBlockFaceTexture(x, y, z, (BlockFace)f, 0)).ToArray();
        var bad = Enumerable.Range(0, 6).Where(f => faces[f] != idx).Select(f => ((BlockFace)f).ToString()).ToArray();
        if (bad.Length > 0) _fails++;
        Out($"{label} idx={idx}: faces={Faces(c, x, y, z)}  {(bad.Length == 0 ? "OK" : "FAIL missing/wrong: " + string.Join(",", bad))}");
    }

    static void SetBlockTextureServer(Vector3i pos, BlockFace face, int idx)
    {
        var m = typeof(GameManager).GetMethods(F).First(x => x.Name == "SetBlockTextureServer");
        var p0 = m.GetParameters()[0].ParameterType;
        object first = p0 == typeof(Vector3i) ? (object)pos : p0.GetMethod("op_Implicit", new[] { typeof(Vector3i) }).Invoke(null, new object[] { pos });
        var args = new object[m.GetParameters().Length];
        args[0] = first; args[1] = face; args[2] = idx; args[3] = -1; args[4] = (byte)0;
        m.Invoke(GameManager.Instance, args);
    }

    static void SendAsClientPacket(Vector3i pos, BlockFace face, int idx)
    {
        var sent = NetPackageManager.GetPackage<NetPackageSetBlockTexture>().Setup(pos, face, idx, 1234, 0);
        var ms = new MemoryStream();
        var w = MemoryPools.poolBinaryWriter.AllocSync(true);
        w.SetBaseStream(ms);
        sent.write(w);
        w.Flush();
        MemoryPools.poolBinaryWriter.FreeSync(w);
        // write() emits the package id ahead of the 19-byte payload read() consumes
        ms.Position = ms.Length - 19;
        var r = MemoryPools.poolBinaryReader.AllocSync(true);
        r.SetBaseStream(ms);
        var received = NetPackageManager.GetPackage<NetPackageSetBlockTexture>();
        received.read(r);
        MemoryPools.poolBinaryReader.FreeSync(r);
        received.ProcessPackage(GameManager.Instance.World, GameManager.Instance);
    }

    static uint SaveVersion()
    {
        foreach (var n in new[] { "CurrentSaveVersion", "cSaveVersion", "SaveVersion" })
        {
            var f = typeof(Chunk).GetField(n, F);
            if (f != null) return Convert.ToUInt32(f.GetValue(null));
        }
        throw new Exception("Chunk save version constant not found");
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        _fails = 0;
        try
        {
            var world = GameManager.Instance.World;
            var cc = world.ChunkCache;
            const int cx = 2, cz = 2, y = 100;
            long key = WorldChunkCache.MakeChunkKey(cx, cz);
            var chunk = cc.GetChunkSync(key) as Chunk;
            if (chunk == null)
            {
                chunk = MemoryPools.PoolChunks.AllocSync(true);
                typeof(Chunk).GetField("m_X", F).SetValue(chunk, cx);
                typeof(Chunk).GetProperty("Z", F).SetValue(chunk, cz);
                cc.AddChunkSync(chunk);
                Out("using a fresh chunk added to the cache");
            }
            var bpv = typeof(ChunkBlockChannel).GetField("bytesPerVal", F).GetValue(chunk.chnTextures[0]);
            Out($"chunk ({cx},{cz}) texture channel bytesPerVal={bpv}");

            for (int i = 0; i < Ids.Length; i++)
            {
                int idx = Ids[i];
                int lx = i, lz = 0;
                var pos = new Vector3i(cx * 16 + lx, y, cz * 16 + lz);

                chunk.SetTextureFull(lx, y, lz, 0L, 0);
                for (int f = 0; f <= 5; f++) SetBlockTextureServer(pos, (BlockFace)f, idx);
                Check("A brush loop (SetBlockTextureServer)", chunk, lx, y, lz, idx);

                lz = 1; pos = new Vector3i(cx * 16 + lx, y, cz * 16 + lz);
                chunk.SetTextureFull(lx, y, lz, 0L, 0);
                for (int f = 0; f <= 5; f++) SendAsClientPacket(pos, (BlockFace)f, idx);
                Check("B client packets -> server", chunk, lx, y, lz, idx);
            }

            // C: save / load round trip of the same chunk
            var ms = new MemoryStream();
            var w = MemoryPools.poolBinaryWriter.AllocSync(true);
            w.SetBaseStream(ms);
            chunk.save(w);
            w.Flush();
            MemoryPools.poolBinaryWriter.FreeSync(w);
            ms.Position = 0;
            var r = MemoryPools.poolBinaryReader.AllocSync(true);
            r.SetBaseStream(ms);
            var copy = MemoryPools.PoolChunks.AllocSync(true);
            copy.load(r, SaveVersion());
            MemoryPools.poolBinaryReader.FreeSync(r);
            for (int i = 0; i < Ids.Length; i++)
                for (int lz = 0; lz <= 1; lz++)
                    Check($"C after save/load ({(lz == 0 ? "A" : "B")} block)", copy, i, y, lz, Ids[i]);

            Out(_fails == 0 ? "RESULT PASS" : $"RESULT FAIL ({_fails} checks)");
        }
        catch (Exception ex)
        {
            Out("RESULT ERROR " + ex);
        }
    }
}
