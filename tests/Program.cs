using System;
using System.Collections.Generic;
using System.Linq;

// Standalone assertion harness for PaintIdMerge — the pure ID-merge math behind the
// per-world persistent paint map. Run with: dotnet run --project tests
// No game assemblies are referenced, so this runs headless on any machine.

static class Tests
{
    const ushort Floor = 512;
    static int _failures = 0;
    static int _checks = 0;

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

        Console.WriteLine();
        if (_failures == 0)
            Console.WriteLine($"PASS — all {_checks} checks passed across 8 cases.");
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
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

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
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

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
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

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
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

        Case("colliding new name is relocated");
        AssertAdded(added, 1);
        AssertId(merged, "blue", 514);        // persisted id untouched
        AssertNotId(merged, "newbie", 514);   // did not steal it
        Assert(merged["newbie"] >= 515, $"newbie relocated above max, got {merged["newbie"]}");
        AssertDistinctIds(merged);
    }

    // Two new names that want the same (taken) id are assigned in a stable order
    // (by want-id, then ordinal name) so every peer computes the same result.
    static void DuplicateWantIdsAssignedDeterministically()
    {
        var persisted = Map(("red", 512));            // 512 taken, so both new names collide
        var current = Map(("red", 512), ("zeb", 512), ("abe", 512));
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

        Case("duplicate want-ids deterministic");
        AssertAdded(added, 2);
        AssertId(merged, "abe", 513);   // "abe" sorts before "zeb"
        AssertId(merged, "zeb", 514);
        AssertDistinctIds(merged);
    }

    // Gaps left by removed packs are NOT backfilled by new names — new names go above the
    // current max, keeping removed slots reserved for if the pack returns.
    static void GapsAreNotBackfilled()
    {
        var persisted = Map(("red", 512), ("gone", 513), ("green", 514)); // 513 is a "gap"
        var current = Map(("red", 512), ("green", 514), ("fresh", 600));
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

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
        var first = PaintIdMerge.Merge(persisted, current, Floor, out _);
        var second = PaintIdMerge.Merge(persisted, current, Floor, out _);

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
        var merged = PaintIdMerge.Merge(persisted, current, Floor, out int added);

        Case("no two names share an id");
        AssertAdded(added, 5);
        AssertId(merged, "a", 512);
        AssertId(merged, "b", 513);
        AssertId(merged, "c", 520);
        AssertDistinctIds(merged);
    }

    // ---- helpers -------------------------------------------------------

    static Dictionary<string, ushort> Map(params (string name, int id)[] entries)
    {
        var d = new Dictionary<string, ushort>();
        foreach (var (name, id) in entries) d[name] = (ushort)id;
        return d;
    }

    static void Case(string name) => Console.WriteLine($"• {name}");

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
