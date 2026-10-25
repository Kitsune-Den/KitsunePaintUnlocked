using System;
using System.Collections.Generic;
using System.Linq;

// Standalone assertion harness for PaintIdMerge — the pure ID-merge math behind the
// per-world persistent paint map. Run with: dotnet run --project tests
// No game assemblies are referenced, so this runs headless on any machine.

static class Tests
{
    // The real limits. Most fixtures use IDs 512+ because that's what every world saved
    // before the floor dropped to 256 contains.
    const ushort Floor = PaintIdLimits.CustomIdFloor;
    const ushort Legacy = PaintIdLimits.LegacyIdFloor;
    const ushort Max = PaintIdLimits.MaxPaintId;
    static int _failures = 0;
    static int _checks = 0;
    static int _cases = 0;

    static void Main()
    {
        FirstRunIsIdentity();
        PackAddedKeepsExistingIds();
        PackRemovedRetainsReservedSlot();
        NewNameCollidingWithPersistedIdIsRelocated();
        DuplicateWantIdsAssignedDeterministically();
        GapsAreNotBackfilled();
        DeterministicAcrossRuns();
        NoTwoNamesShareAnIdEver();
        FreshWorldUsesFullRange();
        BandFillsBeforeGoingAboveMax();
        NothingIsAssignedAboveMax();
        PersistedIdAboveMaxIsRescued();
        WantAboveMaxIsRelocated();
        FullLegacyWorldGainsTheBand();
        LegacyAssignmentRecountsFromOldFloor();

        Console.WriteLine();
        if (_failures == 0)
            Console.WriteLine($"PASS — all {_checks} checks passed across {_cases} cases.");
        else
            Console.WriteLine($"FAIL — {_failures} of {_checks} checks failed.");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // ---- cases ---------------------------------------------------------

    // First load of a world (no persisted map): output must equal current exactly —
    // existing paint must NOT be reshuffled.
    static void FirstRunIsIdentity()
    {
        var persisted = Map();
        var current = Map(("red", 512), ("blue", 513), ("green", 514));
        var merged = Merge(persisted, current, out int added);

        Case("first run is identity");
        AssertAdded(added, 3);
        AssertSameMap(merged, current);
        AssertDistinctIds(merged);
    }

    // A pack was added: every previously-persisted name keeps its EXACT id; new names
    // that don't collide keep their natural id.
    static void PackAddedKeepsExistingIds()
    {
        var persisted = Map(("red", 512), ("blue", 513));
        var current = Map(("red", 512), ("blue", 513), ("teal", 514), ("gold", 515));
        var merged = Merge(persisted, current, out int added);

        Case("pack added keeps existing ids");
        AssertAdded(added, 2);
        AssertId(merged, "red", 512);
        AssertId(merged, "blue", 513);
        AssertId(merged, "teal", 514);
        AssertId(merged, "gold", 515);
        AssertDistinctIds(merged);
    }

    // A pack was removed: its names are gone from current but stay in the merged map at
    // their persisted ids, so the slot stays reserved and nothing shifts into it.
    static void PackRemovedRetainsReservedSlot()
    {
        var persisted = Map(("red", 512), ("blue", 513), ("green", 514));
        var current = Map(("red", 512), ("blue", 513)); // green's pack uninstalled
        var merged = Merge(persisted, current, out int added);

        Case("pack removed retains reserved slot");
        AssertAdded(added, 0);
        AssertId(merged, "green", 514);   // still reserved
        AssertCount(merged, 3);
        AssertDistinctIds(merged);
    }

    // A brand-new name whose natural session id is already taken by a persisted name must
    // be relocated — never overwrite the persisted entry.
    static void NewNameCollidingWithPersistedIdIsRelocated()
    {
        var persisted = Map(("red", 512), ("blue", 514));
        // "newbie" naturally wants 514, which belongs to persisted "blue".
        var current = Map(("red", 512), ("blue", 514), ("newbie", 514));
        var merged = Merge(persisted, current, out int added);

        Case("colliding new name is relocated");
        AssertAdded(added, 1);
        AssertId(merged, "blue", 514);        // persisted id untouched
        AssertNotId(merged, "newbie", 514);   // did not steal it
        AssertId(merged, "newbie", Floor);    // into the never-used band below 512
        AssertDistinctIds(merged);
    }

    // Two new names that want the same (taken) id are assigned in a stable order
    // (by want-id, then ordinal name) so every peer computes the same result.
    static void DuplicateWantIdsAssignedDeterministically()
    {
        var persisted = Map(("red", 512));            // 512 taken, so both new names collide
        var current = Map(("red", 512), ("zeb", 512), ("abe", 512));
        var merged = Merge(persisted, current, out int added);

        Case("duplicate want-ids deterministic");
        AssertAdded(added, 2);
        AssertId(merged, "abe", Floor);       // "abe" sorts before "zeb"
        AssertId(merged, "zeb", Floor + 1);
        AssertDistinctIds(merged);
    }

    // Gaps left by removed packs are NOT backfilled by new names — new names go above the
    // current max, keeping removed slots reserved for if the pack returns.
    static void GapsAreNotBackfilled()
    {
        var persisted = Map(("red", 512), ("gone", 513), ("green", 514)); // 513 is a "gap"
        var current = Map(("red", 512), ("green", 514), ("fresh", 600));
        var merged = Merge(persisted, current, out int added);

        Case("gaps are not backfilled");
        AssertAdded(added, 1);
        AssertId(merged, "gone", 513);    // reserved gap retained
        AssertId(merged, "fresh", 600);   // natural id free → kept, not pushed into the 513 gap
        AssertDistinctIds(merged);
    }

    // Same inputs → same output, every time (no reliance on dictionary iteration order).
    static void DeterministicAcrossRuns()
    {
        var persisted = Map(("a", 512), ("b", 600));
        var current = Map(("a", 512), ("b", 600), ("c", 512), ("d", 600), ("e", 700));
        var first = Merge(persisted, current, out _);
        var second = Merge(persisted, current, out _);

        Case("deterministic across runs");
        AssertSameMap(first, second);
        AssertDistinctIds(first);
    }

    // The core safety invariant, on a messy mix: no two names ever resolve to the same id.
    static void NoTwoNamesShareAnIdEver()
    {
        var persisted = Map(("a", 512), ("b", 513), ("c", 520));
        var current = Map(
            ("a", 512), ("b", 513),               // existing
            ("x", 512), ("y", 513), ("z", 520),   // new, all colliding with persisted
            ("w", 700), ("v", 701));              // new, free
        var merged = Merge(persisted, current, out int added);

        Case("no two names share an id");
        AssertAdded(added, 5);
        AssertId(merged, "a", 512);
        AssertId(merged, "b", 513);
        AssertId(merged, "c", 520);
        AssertDistinctIds(merged);
    }

    // A new world on this release: custom paints start at 256 and the snapshot keeps them.
    static void FreshWorldUsesFullRange()
    {
        var current = Map(("red", Floor), ("blue", Floor + 1), ("top", Max));
        var merged = Merge(Map(), current, out int added);

        Case("fresh world uses 256-1023 as assigned");
        AssertAdded(added, 3);
        AssertSameMap(merged, current);
    }

    // Relocation fills 256-511 first and only then goes above the persisted max.
    static void BandFillsBeforeGoingAboveMax()
    {
        // Persisted occupies 512..519. 256 new names all collide (want 512), so they take the
        // whole 256-511 band; the 257th has to go above the max, to 520.
        var persisted = new Dictionary<string, ushort>();
        for (int i = 0; i < 8; i++) persisted[$"old{i}"] = (ushort)(Legacy + i);
        var current = new Dictionary<string, ushort>(persisted);
        for (int i = 0; i < 257; i++) current[$"new{i:D3}"] = Legacy;
        var merged = Merge(persisted, current, out int added);

        Case("band 256-511 fills before going above the max");
        AssertAdded(added, 257);
        AssertId(merged, "new000", Floor);
        AssertId(merged, "new255", Legacy - 1);
        AssertId(merged, "new256", Legacy + 8);
        AssertDistinctIds(merged);
    }

    // With every ID up to 1023 taken, a new name gets nothing rather than an ID a block
    // face can't store.
    static void NothingIsAssignedAboveMax()
    {
        var persisted = new Dictionary<string, ushort>();
        for (int id = Floor; id <= Max; id++) persisted[$"p{id}"] = (ushort)id;
        var current = new Dictionary<string, ushort>(persisted) { ["late"] = Max };
        var merged = PaintIdMerge.Merge(persisted, current, Floor, Legacy, Max, out int added, out var dropped);

        Case("nothing is assigned above 1023");
        AssertAdded(added, 0);
        Assert(!merged.ContainsKey("late"), "late has no id");
        Assert(dropped.Count == 1 && dropped[0] == "late", $"late reported dropped (got [{string.Join(", ", dropped)}])");
        Assert(merged.Values.All(v => v <= Max), "every id <= 1023");
    }

    // Builds before the cap could persist IDs above 1023. Nothing can be painted with those,
    // so the name is placed again like a new one; one whose pack is gone is forgotten.
    static void PersistedIdAboveMaxIsRescued()
    {
        var persisted = Map(("red", 512), ("big", 1100), ("ghost", 1200));
        var current = Map(("red", 512), ("big", 300));
        var merged = Merge(persisted, current, out int added);

        Case("persisted id above 1023 is rescued");
        AssertAdded(added, 1);
        AssertId(merged, "red", 512);
        AssertId(merged, "big", 300);
        Assert(!merged.ContainsKey("ghost"), "ghost (pack gone, unstorable id) forgotten");
    }

    // A session ID above 1023 is never kept as-is, even when free.
    static void WantAboveMaxIsRelocated()
    {
        var merged = Merge(Map(("red", 512)), Map(("red", 512), ("huge", 1500)), out int added);

        Case("want above 1023 is relocated");
        AssertAdded(added, 1);
        AssertId(merged, "huge", Floor);
    }

    // The case the floor drop is for: a world whose 512-1023 range is already full gets
    // 256 more slots, and only what doesn't fit even then is dropped.
    static void FullLegacyWorldGainsTheBand()
    {
        var persisted = new Dictionary<string, ushort>();
        for (int id = Legacy; id <= Max; id++) persisted[$"p{id}"] = (ushort)id;
        var current = new Dictionary<string, ushort>(persisted);
        for (int i = 0; i < 300; i++) current[$"ck{i:D3}"] = (ushort)(Legacy + i); // all collide
        var merged = PaintIdMerge.Merge(persisted, current, Floor, Legacy, Max, out int added, out var dropped);

        Case("full legacy world gains the 256-511 band");
        AssertAdded(added, 256);
        Assert(dropped.Count == 44, $"44 dropped (got {dropped.Count})");
        AssertCount(merged, 768);
        Assert(merged.Values.All(v => v >= Floor && v <= Max), "every id in 256-1023");
        AssertDistinctIds(merged);
    }

    // Reconstructing a pre-idmap world's IDs: one per registered paint from 512, duplicate
    // names included in the count (each used up an ID) but keeping their first ID.
    static void LegacyAssignmentRecountsFromOldFloor()
    {
        // null = a paint registered without a name (PyroPaints' PP_paintedPlaster_04): it
        // still used up 516, so "d" must land on 517, not 516.
        var legacy = PaintIdMerge.LegacyAssignment(new List<string> { "a", "b", "a", "c", null, "d" }, Legacy);

        Case("legacy assignment recounts from 512");
        AssertId(legacy, "a", 512);
        AssertId(legacy, "b", 513);
        AssertId(legacy, "c", 515);
        AssertId(legacy, "d", 517);
        AssertCount(legacy, 4);
    }

    // ---- helpers -------------------------------------------------------

    static Dictionary<string, ushort> Merge(
        Dictionary<string, ushort> persisted, Dictionary<string, ushort> current, out int added) =>
        PaintIdMerge.Merge(persisted, current, Floor, Legacy, Max, out added, out _);

    static Dictionary<string, ushort> Map(params (string name, int id)[] entries)
    {
        var d = new Dictionary<string, ushort>();
        foreach (var (name, id) in entries) d[name] = (ushort)id;
        return d;
    }

    static void Case(string name) { _cases++; Console.WriteLine($"• {name}"); }

    static void Assert(bool cond, string msg)
    {
        _checks++;
        if (cond) { Console.WriteLine($"    ok: {msg}"); }
        else { _failures++; Console.WriteLine($"    FAIL: {msg}"); }
    }

    static void AssertId(Dictionary<string, ushort> m, string name, int expected) =>
        Assert(m.TryGetValue(name, out var v) && v == expected,
            $"{name} == {expected} (got {(m.ContainsKey(name) ? m[name].ToString() : "absent")})");

    static void AssertNotId(Dictionary<string, ushort> m, string name, int notExpected) =>
        Assert(!m.TryGetValue(name, out var v) || v != notExpected, $"{name} != {notExpected}");

    static void AssertAdded(int actual, int expected) =>
        Assert(actual == expected, $"added == {expected} (got {actual})");

    static void AssertCount(Dictionary<string, ushort> m, int expected) =>
        Assert(m.Count == expected, $"count == {expected} (got {m.Count})");

    static void AssertDistinctIds(Dictionary<string, ushort> m) =>
        Assert(m.Values.Distinct().Count() == m.Count,
            $"all {m.Count} ids distinct (got {m.Values.Distinct().Count()} unique)");

    static void AssertSameMap(Dictionary<string, ushort> a, Dictionary<string, ushort> b)
    {
        bool same = a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
        Assert(same, $"maps equal ({Show(a)} vs {Show(b)})");
    }

    static string Show(Dictionary<string, ushort> m) =>
        "{" + string.Join(", ", m.OrderBy(k => k.Value).Select(kv => $"{kv.Key}:{kv.Value}")) + "}";
}
