using System.Collections.Generic;

/// <summary>
/// Debug console command to dump BlockTextureData for a specific paint ID.
/// Usage: pu_debug 512
/// </summary>
public class ConsoleCmdPaintDebug : ConsoleCmdAbstract
{
    public override string[] getCommands() => new[] { "pu_debug" };
    public override string getDescription() => "PaintUnlocked: dump BlockTextureData for a paint ID. Usage: pu_debug <id>";
    public override bool IsExecuteOnClient => true;
    public override bool AllowedInMainMenu => false;

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        if (_params.Count == 0)
        {
            Log.Out("[PaintDebug] Usage: pu_debug <paintID>");
            return;
        }

        if (!int.TryParse(_params[0], out int id))
        {
            Log.Out("[PaintDebug] Invalid ID: " + _params[0]);
            return;
        }

        var list = BlockTextureData.list;
        if (list == null) { Log.Out("[PaintDebug] BlockTextureData.list is null!"); return; }
        Log.Out($"[PaintDebug] BlockTextureData.list.Length = {list.Length}");

        if (id < 0 || id >= list.Length)
        {
            Log.Out($"[PaintDebug] ID {id} out of range (0-{list.Length-1})");
            return;
        }

        var data = list[id];
        if (data == null)
        {
            Log.Out($"[PaintDebug] list[{id}] is NULL");
            return;
        }

        Log.Out($"[PaintDebug] list[{id}]: Name={data.Name} ID={data.ID} TextureID={data.TextureID}");

        // Now check uvMapping
        var opaque = MeshDescription.meshes[MeshDescription.MESH_OPAQUE];
        if (opaque == null) { Log.Out("[PaintDebug] MESH_OPAQUE is null"); return; }
        var atlas = opaque.textureAtlas as TextureAtlasBlocks;
        if (atlas == null) { Log.Out("[PaintDebug] Atlas is null"); return; }

        Log.Out($"[PaintDebug] atlas.uvMapping.Length = {atlas.uvMapping.Length}");

        var tid = data.TextureID;
        if (tid >= atlas.uvMapping.Length)
        {
            Log.Out($"[PaintDebug] TextureID {tid} is OUT OF RANGE for uvMapping (len={atlas.uvMapping.Length})!");
            return;
        }

        var uv = atlas.uvMapping[tid];
        Log.Out($"[PaintDebug] uvMapping[{tid}].index = {uv.index} (GPU slot)");

        // Check atlas texture
        if (opaque.TexDiffuse is UnityEngine.Texture2DArray diff)
            Log.Out($"[PaintDebug] atlas diffuse: name={diff.name} depth={diff.depth}");
        else
            Log.Out($"[PaintDebug] atlas diffuse: not a Texture2DArray (type={opaque.TexDiffuse?.GetType().Name ?? "null"})");
    }
}
