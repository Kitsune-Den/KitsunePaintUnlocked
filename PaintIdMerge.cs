using System.Collections.Generic;

/// <summary>
/// Pure (game-type-free) merge of a world's persisted paint name -> ID map with the
/// current session's assignment. Extracted from PaintIdSyncManager so the ID math — the
/// part where a subtle bug would silently move a painted block's texture — can be unit
/// tested without launching the game.
///
/// Invariants:
///  - Every name already in <paramref name="persisted"/> keeps its EXACT persisted ID
///    (an existing world is never reshuffled), as long as that ID is storable (&lt;= maxId).
///  - A brand-new name keeps its natural session ID when that ID is free and in
///    [floor, maxId]. Otherwise it is relocated: first to the lowest free ID in
///    [floor, legacyFloor) — a band no release before the floor drop ever assigned, so it
///    is unpainted in every existing world — then above the current max. It never
///    overwrites a persisted ID.
///  - A name that fits nowhere at or below maxId gets no ID at all and is reported in
///    <c>dropped</c>; handing it an ID above maxId would save it as a different paint.
///  - A persisted ID above maxId (written by builds that didn't cap allocation) could never
///    be stored on a block, so nothing is painted with it: that name is placed again like a
///    new one, or forgotten if its pack is gone.
///  - Names only in persisted (a removed pack) are retained, so their slot stays reserved.
///  - The result is deterministic for the same inputs (new names assigned in a stable
///    order), so every peer computes the same map.
/// </summary>
public static class PaintIdMerge
{
    /// <summary>
    /// Merge <paramref name="persisted"/> (authoritative for known names) with
    /// <paramref name="current"/> (this session's assignment). Returns the merged map, sets
    /// <paramref name="added"/> to the number of names newly assigned an ID, and fills
    /// <paramref name="dropped"/> with the names that didn't fit under maxId.
    /// </summary>
    public static Dictionary<string, ushort> Merge(
        IDictionary<string, ushort> persisted,
        IDictionary<string, ushort> current,
        ushort floor,
        ushort legacyFloor,
        ushort maxId,
        out int added,
        out List<string> dropped)
    {
        var merged = new Dictionary<string, ushort>();
        foreach (var kv in persisted)
            if (kv.Value <= maxId) merged[kv.Key] = kv.Value;
        var usedIds = new HashSet<ushort>(merged.Values);

        int nextFree = floor;
        foreach (var id in usedIds)
            if (id >= nextFree) nextFree = id + 1;
        int bandNext = floor;

        var newNames = new List<KeyValuePair<string, ushort>>();
        foreach (var kv in current)
            if (!merged.ContainsKey(kv.Key)) newNames.Add(kv);
        // Deterministic ordering so additions land on the same IDs on every peer.
        newNames.Sort((a, b) =>
            a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key, b.Key));

        added = 0;
        dropped = new List<string>();
        foreach (var kv in newNames)
        {
            ushort want = kv.Value;
            int assign;
            if (want >= floor && want <= maxId && !usedIds.Contains(want))
            {
                assign = want; // first run: snapshot the existing world's IDs unchanged
            }
            else
            {
                while (bandNext < legacyFloor && usedIds.Contains((ushort)bandNext)) bandNext++;
                if (bandNext < legacyFloor)
                {
                    assign = bandNext;
                }
                else
                {
                    while (nextFree <= maxId && usedIds.Contains((ushort)nextFree)) nextFree++;
                    if (nextFree > maxId)
                    {
                        dropped.Add(kv.Key);
                        continue;
                    }
                    assign = nextFree;
                }
            }
            merged[kv.Key] = (ushort)assign;
            usedIds.Add((ushort)assign);
            if (assign >= nextFree) nextFree = assign + 1;
            added++;
        }

        return merged;
    }

    /// <summary>
    /// Rebuild the IDs a pre-1.6.0 build would have given this session's paints, for a world
    /// that release painted but never snapshotted (it predates the idmap, 1.3.0). Those
    /// builds handed out IDs one by one from <paramref name="legacyFloor"/> in registration
    /// order, which is the order of <paramref name="namesInIdOrder"/> (every registered
    /// custom paint by ascending session ID, duplicate names included, since each one used
    /// up an ID). A null or empty entry is a paint registered without a name (PyroPaints
    /// ships one): it used up an ID too, so it counts, but has nothing to map. A duplicate
    /// name keeps its first ID, matching BuildMapping. IDs past ushort range are left out;
    /// Merge relocates anything above its maxId anyway.
    /// </summary>
    public static Dictionary<string, ushort> LegacyAssignment(
        IList<string> namesInIdOrder,
        ushort legacyFloor)
    {
        var result = new Dictionary<string, ushort>();
        for (int i = 0; i < namesInIdOrder.Count; i++)
        {
            int id = legacyFloor + i;
            if (id > ushort.MaxValue) break;
            var name = namesInIdOrder[i];
            if (!string.IsNullOrEmpty(name) && !result.ContainsKey(name)) result[name] = (ushort)id;
        }
        return result;
    }
}
