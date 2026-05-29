using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Per-world persistence of the paint name -> ID mapping.
///
/// Paint IDs are assigned sequentially from 512 in texture-registration order
/// (OcbPaintLimitPatch). That order is NOT stable across server restarts — adding,
/// removing, or updating a pack (or even a dictionary-iteration shuffle) reshuffles the
/// IDs. Because painted blocks store the raw numeric ID in chunk data, a reshuffle makes
/// existing paint render as a different texture ("a few blocks changed, had to repaint").
///
/// Fix: snapshot the mapping to &lt;SaveDir&gt;/paintunlocked.idmap on first load and
/// restore it on every subsequent load, so a given world's IDs never move. Paints added
/// later are appended above the existing max; removed paints keep their slot reserved
/// (rendered via placeholder) so nothing else shifts into it.
///
/// The first snapshot captures the CURRENT session's assignment — it does not reorder an
/// existing world. That is what makes it save-compatible (unlike deterministic-by-sort,
/// which was reverted because it reshuffled pre-existing worlds).
/// </summary>
public static class PaintIdPersistence
{
    private const string MapFileName = "paintunlocked.idmap";

    public static string MapPath(string saveDir) =>
        string.IsNullOrEmpty(saveDir) ? null : Path.Combine(saveDir, MapFileName);

    public static bool Exists(string saveDir)
    {
        var path = MapPath(saveDir);
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    /// <summary>
    /// Load the persisted name -> ID map for a world. Returns an empty dictionary if no
    /// file exists yet (first run for this world).
    /// Format: one entry per line, "&lt;paintID&gt;\t&lt;textureName&gt;". Lines starting
    /// with '#' are comments.
    /// </summary>
    public static Dictionary<string, ushort> Load(string saveDir)
    {
        var map = new Dictionary<string, ushort>();
        var path = MapPath(saveDir);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return map;

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var idPart = line.Substring(0, tab).Trim();
                // Do NOT trim the name — it is authoritative exactly as written.
                var name = line.Substring(tab + 1);
                if (!ushort.TryParse(idPart, out var id)) continue;
                if (string.IsNullOrEmpty(name)) continue;
                map[name] = id; // last duplicate wins (file is author-controlled)
            }
            Log.Out($"[PaintUnlocked] Loaded persistent paint map: {map.Count} entries from {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to read persistent paint map {path}: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// Write the map to disk, sorted by ID for stable diffs. Written atomically (temp
    /// file + replace) so a crash mid-write cannot corrupt an existing world's map.
    /// </summary>
    public static void Save(string saveDir, Dictionary<string, ushort> map)
    {
        var path = MapPath(saveDir);
        if (string.IsNullOrEmpty(path))
        {
            Log.Warning("[PaintUnlocked] Cannot save persistent paint map — no save dir");
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var entries = new List<KeyValuePair<string, ushort>>(map);
            entries.Sort((a, b) => a.Value.CompareTo(b.Value));

            var sb = new StringBuilder();
            sb.Append("# PaintUnlocked persistent paint ID map for this world.\n");
            sb.Append("# Maps texture name -> paint ID. These IDs are written into painted\n");
            sb.Append("# blocks, so do NOT change the ID of a name that has already been painted.\n");
            sb.Append("# Format: <paintID>\\t<textureName>\n");
            foreach (var kv in entries)
                sb.Append(kv.Value).Append('\t').Append(kv.Key).Append('\n');

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            Log.Out($"[PaintUnlocked] Saved persistent paint map: {map.Count} entries to {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"[PaintUnlocked] Failed to write persistent paint map {path}: {ex.Message}");
        }
    }
}
