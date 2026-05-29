using System.Collections.Generic;

/// <summary>
/// Console command: report custom paints (ID &gt;= 512) that share the same Name across
/// loaded packs. Duplicate names break the name-keyed paint ID sync — the same block can
/// render as a different texture for different players, and which paint "wins" varies by
/// machine. This lists every collision so the offending packs can be renamed.
///
/// Usage: pu_audit
/// </summary>
public class ConsoleCmdPaintAudit : ConsoleCmdAbstract
{
    private const int CustomIdFloor = 512;

    public override string[] getCommands() => new[] { "pu_audit" };
    public override string getDescription() => "PaintUnlocked: list custom paints that share a name (duplicate-name collisions).";
    public override bool IsExecuteOnClient => true;
    public override bool AllowedInMainMenu => false;

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        var list = BlockTextureData.list;
        if (list == null) { Log.Out("[PaintAudit] BlockTextureData.list is null."); return; }

        // Group IDs by paint name.
        var byName = new Dictionary<string, List<int>>();
        int custom = 0;
        for (int i = CustomIdFloor; i < list.Length; i++)
        {
            var entry = list[i];
            if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
            custom++;
            if (!byName.TryGetValue(entry.Name, out var ids))
            {
                ids = new List<int>();
                byName[entry.Name] = ids;
            }
            ids.Add(entry.ID);
        }

        Log.Out("[PaintAudit] ==== PaintUnlocked duplicate-name audit ====");
        Log.Out($"[PaintAudit] {custom} custom paint(s) registered (ID >= {CustomIdFloor}), {byName.Count} unique name(s).");

        int collisions = 0;
        foreach (var kv in byName)
        {
            if (kv.Value.Count <= 1) continue;
            collisions++;
            kv.Value.Sort();
            Log.Out($"[PaintAudit]   COLLISION: name '{kv.Key}' used by {kv.Value.Count} paints at IDs [{string.Join(", ", kv.Value.ConvertAll(x => x.ToString()).ToArray())}]");
        }

        if (collisions == 0)
            Log.Out("[PaintAudit] No duplicate names — paint sync will be consistent across players.");
        else
            Log.Out($"[PaintAudit] {collisions} duplicate-name collision(s). Rename the duplicates in their pack so each <opaque> has a unique name. Until then only the lowest-ID paint of each name syncs reliably; others may show the wrong texture for some players.");

        // Persistent-map status for the loaded world (authoritative side only).
        var saveDir = WorldMigrationState.SaveDir;
        if (!string.IsNullOrEmpty(saveDir))
        {
            Log.Out(PaintIdPersistence.Exists(saveDir)
                ? $"[PaintAudit] Persistent paint map: present at {PaintIdPersistence.MapPath(saveDir)} (IDs are locked for this world)."
                : $"[PaintAudit] Persistent paint map: not yet written for this world (will snapshot on next save).");
        }

        Log.Out("[PaintAudit] ==== end audit ====");
    }
}
