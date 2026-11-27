using System.Collections.Generic;

// pu_chunktest mark|paint|check|remove <worldX> <worldZ> [paint id, default 300]
// Marks a chunk with a 4x4 plate of blocks at y=200, paints the plate's top faces with paint
// id 300, and reads both back. Used to prove a chunk survives (or is restored after) a load
// without PaintUnlocked. The chunk must be loaded first (chunkobserver add).
public class ConsoleCmdChunkLossSelfTest : ConsoleCmdAbstract
{
    const int Y = 200;
    public override string[] getCommands() => new[] { "pu_chunktest" };
    public override string getDescription() => "PaintUnlocked local verification: mark/paint/check a chunk";
    static void Out(string s) => Log.Out("[PUChunkTest] " + s);

    // Deletes the 4x4 chunks a size-1 chunkobserver at this spot loads (cx-2..cx+1) from the
    // save's region files, the way ChunkSnapshotUtil.LoadChunk does after a failed read. Run it
    // while those chunks are NOT loaded, so nothing saves them back.
    static void Remove(World world, int cx, int cz)
    {
        RegionFileManager rfm = null;
        var provider = world.ChunkCache.ChunkProvider;
        for (var t = provider.GetType(); t != null && rfm == null; t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                if (typeof(RegionFileManager).IsAssignableFrom(f.FieldType)) { rfm = (RegionFileManager)f.GetValue(provider); break; }
        if (rfm == null) { Out("no RegionFileManager found"); return; }
        int n = 0;
        for (int x = cx - 2; x <= cx + 1; x++)
            for (int z = cz - 2; z <= cz + 1; z++)
            {
                rfm.regionFileAccess.Remove(rfm.saveDirectory, x, z);
                lock (rfm.chunksInSaveDir) rfm.chunksInSaveDir.Remove(WorldChunkCache.MakeChunkKey(x, z));
                n++;
            }
        Out($"removed {n} chunks around ({cx},{cz}) from {rfm.saveDirectory}");
    }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (_params.Count < 3) { Out("usage: pu_chunktest mark|paint|check <worldX> <worldZ>"); return; }
        int wx = int.Parse(_params[1]), wz = int.Parse(_params[2]);
        var world = GameManager.Instance.World;
        int cx = World.toChunkXZ(wx), cz = World.toChunkXZ(wz);
        int Paint = _params.Count > 3 ? int.Parse(_params[3]) : 300;
        if (_params[0] == "remove") { Remove(world, cx, cz); return; }
        var chunk = (Chunk)world.GetChunkFromWorldPos(new Vector3i(wx, Y, wz));
        if (chunk == null) { Out($"chunk ({cx},{cz}) not loaded"); return; }
        int bx = cx * 16, bz = cz * 16;
        var bv = Block.GetBlockValue("cobblestoneShapes:cube");
        if (bv.isair) bv = Block.GetBlockValue("terrStone");
        if (bv.isair) { Out("no marker block found"); return; }

        switch (_params[0])
        {
            case "mark":
                for (int x = 4; x < 8; x++)
                    for (int z = 4; z < 8; z++)
                        world.SetBlockRPC(new Vector3i(bx + x, Y, bz + z), bv);
                Out($"chunk ({cx},{cz}) marked with {bv.Block.GetBlockName()}");
                break;
            case "paint":
                for (int x = 4; x < 8; x++)
                    for (int z = 4; z < 8; z++)
                        chunk.SetBlockFaceTexture(x, Y, z, BlockFace.Top, Paint, 0);
                chunk.isModified = true;
                Out($"chunk ({cx},{cz}) painted with {Paint}");
                break;
            case "check":
                int blocks = 0, painted = 0, firstPaint = -1;
                for (int x = 4; x < 8; x++)
                    for (int z = 4; z < 8; z++)
                    {
                        if (chunk.GetBlock(x, Y, z).type == bv.type) blocks++;
                        int p = chunk.GetBlockFaceTexture(x, Y, z, BlockFace.Top, 0);
                        if (firstPaint < 0) firstPaint = p;
                        if (p == Paint) painted++;
                    }
                Out($"chunk ({cx},{cz}) check: {blocks}/16 marker blocks, {painted}/16 painted {Paint} (first top paint {firstPaint})");
                break;
            default:
                Out("unknown subcommand " + _params[0]);
                break;
        }
    }
}
