using System.Collections.Generic;

/// <summary>
/// Pure (game-type-free) merge of a world's persisted paint name -> ID map with the
/// current session's assignment. Extracted from PaintIdSyncManager so the ID math — the
/// part where a subtle bug would silently move a painted block's texture — can be unit
/// tested without launching the game.
///
/// Invariants:
///  - Every name already in <paramref name="persisted"/> keeps its EXACT persisted ID
///    (an existing world is never reshuffled).
///  - A brand-new name keeps its natural session ID when that ID is free and >= floor;
///    otherwise it is relocated to the next free slot. It never overwrites a persisted ID.
///  - Names only in persisted (a removed pack) are retained, so their slot stays reserved.
///  - The result is deterministic for the same inputs (new names assigned in a stable
///    order), so every peer computes the same map.
/// </summary>
public static class PaintIdMerge
{
    /// <summary>
    /// Merge <paramref name="persisted"/> (authoritative for known names) with
    /// <paramref name="current"/> (this session's assignment). Returns the merged map and
    /// sets <paramref name="added"/> to the number of brand-new names that were assigned.
    /// </summary>
    public static Dictionary<string, ushort> Merge(
        IDictionary<string, ushort> persisted,
        IDictionary<string, ushort> current,
        ushort floor,
        out int added)
    {
        var merged = new Dictionary<string, ushort>(persisted);
        var usedIds = new HashSet<ushort>(merged.Values);

        ushort nextFree = floor;
        foreach (var id in usedIds)
            if (id >= nextFree) nextFree = (ushort)(id + 1);

        var newNames = new List<KeyValuePair<string, ushort>>();
        foreach (var kv in current)
            if (!merged.ContainsKey(kv.Key)) newNames.Add(kv);
        // Deterministic ordering so additions land on the same IDs on every peer.
        newNames.Sort((a, b) =>
            a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key, b.Key));

        added = 0;
        foreach (var kv in newNames)
        {
            ushort want = kv.Value;
            ushort assign;
            if (want >= floor && !usedIds.Contains(want))
            {
                assign = want; // first run: snapshot the existing world's IDs unchanged
            }
            else
            {
                while (usedIds.Contains(nextFree)) nextFree = (ushort)(nextFree + 1);
                assign = nextFree;
            }
            merged[kv.Key] = assign;
            usedIds.Add(assign);
            if (assign >= nextFree) nextFree = (ushort)(assign + 1);
            added++;
        }

        return merged;
    }
}
