# Stats storage: packed sorted array + dense table (2026-10-22)

**Status: implemented, not committed.** Measured on a scratch server with a copy of the live data (the `%TEMP%\mir2-mem` world), before and after, same machine and same day.

**Change:** `Stats` (`Shared/Data/Stat.cs`) wrapped a `SortedDictionary<Stat, int>`: per object a dictionary, a `TreeSet`, a `KeyValuePairComparer` and one red-black node (48 B) per entry. It now keeps its entries itself:
- **Sorted mode** (up to `Stats.DenseThreshold` = 24 entries): one `long[]` of entries sorted by stat, each packed as `((long)stat << 32) | (uint)value`, binary-searched. No array until the first entry; capacity 4 → 8 → 16 → 24.
- **Dense mode** (past 24 entries: player, hero and some aggregate totals): one `int[264]`, values indexed by the stat byte plus a 256-bit presence mask. Reads are one array load. It never goes back to sorted mode.
- `Clear()` keeps the storage, so `RefreshStats` (Clear, then Add the base, items and buffs) no longer allocates.

**Format: byte-identical.** `Save` writes the entry count, then (byte stat, int value) in ascending stat order, as the dictionary did. The binary formats can hold explicit 0 entries (stream ctor, SQLite zero list). They are kept as before. The indexer setter still removes an entry on 0. `Shared.Tests/packet-manifest.txt` is unchanged, and the v117–v120 persistence fixtures upgrade and resave byte-identically.

**API:** `Stats.Values` is now a `Stats.StatValues` struct view, not a `SortedDictionary`. It keeps `Count`, the get indexer, `ContainsKey`, `TryGetValue`, `Remove`, ascending `foreach` (a struct enumerator, so no allocation) and `IEnumerable<KeyValuePair<Stat,int>>` for LINQ in tools. A struct returned from a property cannot take an indexer assignment, so a raw write that keeps zeros is now `Values.Set(stat, value)`. There were three callers: `SqliteWorldCodec` (zero list) and two tests. The `Values` setter, which had no users, is gone. Nothing may change a `Stats` while it is being enumerated. The dictionary threw in that case; the new storage gives undefined order. No caller did it, since it would have thrown before. A reader on another thread during a write gets a stale value, not an exception: `Find` reads the array and count once and clamps.

## Heap (`dotnet-dump analyze -c "dumpheap -stat"`, live-data copy, idle 60 s after start / after 200 bots)

| Item | Before idle | After idle | Before bots | After bots |
|---|---|---|---|---|
| Stats objects | 140,806 × 24 B = 3.4 MB | 141,286 × 40 B = 5.7 MB | 3.4 MB | 5.7 MB |
| SortedSet nodes | 656,783 = 31.5 MB | — | 31.7 MB | — |
| TreeSet + SortedDictionary + comparer | 15.8 MB | — | 15.8 MB | — |
| `long[]` entries | — | 91,546 = 11.5 MB | — | 10.7 MB |
| dense `int[264]` (Int32[] delta) | — | ≈ +0.1 MB (~120 sets) | — | ≈ 0 |
| **Stats total** | **50.7 MB** | **≈ 17.2 MB** | **50.9 MB** | **≈ 16.4 MB** |
| **Whole heap** | **434.8 MB** (4.16 M objects) | **399.2 MB** (3.28 M) | **459.9 MB** (4.34 M) | **416.1 MB** (3.41 M) |

That is −35.6 MB idle and −43.8 MB under bots, with about 880k fewer objects for the GC to mark. About 50k of the Stats are empty and now cost only their 40 B object.

## Per operation (microbenchmark, .NET 10 Release; old = the previous class copied verbatim)

| Entries | Read old → new | Recalc (Clear + Add base + 10 item sets) old → new | Recalc alloc old → new | Bytes per object old → new |
|---|---|---|---|---|
| 0 | | | | 143 → 47 |
| 3 | 6.7 → 2.0 ns | 1,959 → 673 ns | 2,408 → 0 B | 287 → 104 |
| 15 | 7.4 → 3.0 ns | 1,032 → 120 ns | 2,440 → 0 B | 862 → 199 |
| 40 (dense) | 9.5 → 1.3 ns | 1,480 → 235 ns | 2,936 → 0 B | 2,063 → 1,126 |

Every `RefreshStats` of a player, hero or monster allocated about 2.4–2.9 KB through the dictionary (re-created nodes after `Clear`, enumerators). It now allocates nothing.

## Load test (StressClient, 200 bots, ramp 20/s, 180 s, `--max-packet 50`)

| | Result | kills | walks | errors / decode / disconnects |
|---|---|---|---|---|
| Before (HEAD 661d57e7) | PASS, 200/200 in game | 222 | 31,857 | 0 / 0 / 0 |
| After | PASS, 200/200 in game | 224 | 31,656 | 0 / 0 / 0 |

These runs are smoke checks. The heap and microbenchmark tables are the measurement. The after build also carries other sessions' uncommitted web-admin/monitor hunks (not on the game path).

**Tests:** `Shared.Tests/StatsStorageTests.cs` pins the new class to a `SortedDictionary` model of the old one: random set/raw-set/remove/clear across both modes and all 256 byte values, `Add`, `Equals` (explicit zero entries count), plus byte-identical `Save`. `PacketTestSupport.Fill` leaves `Stats` alone (filling its private arrays with empty arrays made an invalid object). `Server.Persistence.Tests/Oracle.cs` prints Stats in the old `Values.Count` / `Values[Stat]` form so the stored fixture oracles still hold.

**Files:** `%TEMP%\mir2-stats\{before,after}\` (`idle-stat.u8`, `bots-stat.u8`, `bots.u8`, `measure.ps1` one level up). The microbenchmark is in this session's scratchpad (`bench/p.cs`).

**Not done:** the `Stats` object could drop to 32 B by sharing one storage field between the two modes. That would save about 1.1 MB, which is not worth a type check on every read.
