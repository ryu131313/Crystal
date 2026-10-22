# Map grid memory (2026-10-22)

**Status: implemented, not committed. Measured on a scratch server with a full copy of the live data (463 maps). One read-only review found no Critical or High issues; its one Low is fixed (a hero spawning onto a wall cell is added before its `CurrentMap` is set, so `Map.AddObject` now swaps in the map-owned wall copy itself).** Started from the question "what uses the 5.1 GB working set of the live server?". A heap dump showed that almost all of it is static map data. 200 bots added only ~23 MB.

- **No behaviour change intended:** no DB, wire or `Envir.Version` change. Respawn cells come back in the same order, so a given random number still picks the same spawn cell.

## Before → after (idle heap, `dotnet-dump analyze -c "dumpheap -stat"`)

| Item | Before | After |
|---|---|---|
| **GC heap total** | **3,099.9 MB** (52.1M objects) | **823.9 MB** (11.0M objects) |
| `Cell` objects | 22,125,417 / 708.0 MB | 6,889,531 / 220.5 MB (walkable cells only) |
| `List<MapObject>` (`Cell.Objects`) | 22,125,417 / 708.0 MB | 75,098 / 2.4 MB (cells that have held an object) |
| `Point[]` (map + respawn walkable lists) | 6,763 / 758.8 MB | 468 / 55.1 MB |
| `Door[,]` | 463 / 177.0 MB | 0 (82 `Dictionary<int, Door>`, ~0.15 MB) |
| `MineSpot` + `MineSpot[,]` | 1,430,000 / 57.2 MB + 23 / 11.4 MB | 0 at idle (23 `MineGrid`, ~4 KB; one spot per mined cell) |
| `Int32[]` | 0.9 MB | 9.1 MB (+8.2 MB: respawn column tables + map column starts) |
| `Cell[,]` | 177.0 MB | 177.0 MB (unchanged, see Next) |
| Free (fragmentation) | 256.0 MB | 108.0 MB |

The scratch server's working set after start was **879 MB**. The live server runs at 5.1 GB. Before: `%TEMP%\mir2-mem\idle-stat.txt` (parent session, same data, pre-change build). After: `%TEMP%\mir2-mapmem\idle-after-stat.txt`.

**Load test:** 200 bots (ramp 20/s, 180 s) on the same scratch server: **PASS**. All 200 reached the game, with 0 errors, 0 decode errors and 0 disconnects; 85 kills, 37k walks, 559 quests accepted. The heap after the run was 853.9 MB (before: 3,122.9 MB with bots).
- `[Diag] Loop` over each server's whole log: total GC pause **803 ms vs 3,051 ms** before. Average busy was 7.3 % vs 6.1 %, but the two windows are not the same length or phase, so treat that as noise.
- The loop's per-second max stayed under 10 ms during the run. The only larger spikes were the first iteration after start (562 ms) and the 200 simultaneous logouts at the end (98 ms).
- Keepalive RTT averaged 1.8 ms; the parent session's pre-change run averaged 238 ms on the same setup. That run may have been under other machine load, so this is not claimed as a gain.

## What changed (`Server/MirEnvir/Map.cs`, `WalkableBox.cs`, `Server/MirDatabase/MineInfo.cs`)

1. **`Cell.Objects` is lazy.** The list is created on the first `Add` and kept after that, so readers never see it disappear. Until then the property returns one shared empty `List<MapObject>`. The existing `cell.Objects == null` checks are now always false. Readers that skipped the check stay safe.
   - Cost per access: one null-coalesce.
   - Nothing writes to `Objects` except `Cell.Add`/`Remove` (audited: the only other mutation, `RemoveSafeZoneSpells`, removes only from a non-empty list).
2. **Wall cells are shared.** There are 42 static instances: (LowWall, HighWall) × fishing value −1..19. `Cell.LowWall`/`HighWall` no longer allocate (this was Known debt).
   - The loaders set fishing values through `SetFishingAttribute`, which swaps in the shared wall that carries the value.
   - **Safety net:** an `Add` to a shared wall finds its map and cell through the object's `CurrentMap`/`CurrentLocation`. It replaces the shared instance with a map-owned copy and adds the object there. If the object is not at that cell, `Add` logs and does not add. Today every `Add` site passes the cell at the object's current location, or a walkable cell it is moving to.
   - Monsters on a map all run on that map's mob thread (`Map.Thread` → `SpawnThread`), so the swap is single-threaded like any other cell change.
3. **Respawn cells are a view (`WalkableBox`), not a copy.**
   - `Map.WalkableCells` is now built at its exact capacity, with a column index `WalkableColumnStart` (Width + 1 ints).
   - A respawn keeps two ints per non-empty column of its spread box. The indexer does a binary search over the columns: O(log columns) per spawn, against O(1) before, and spawns are rare. Load does O(columns × log) per respawn instead of O(map walkable cells), with no LINQ.
   - The Load LINQ (`Where(...).ToList()`) and LiveDb's `BuildRespawnCells` share one builder now.
   - A test checks element-for-element equality with the old filter on random maps and boxes (`Server.WebAdmin.Tests/MapGridTests.cs`).
4. **Doors are sparse.** `Map.GetDoor(x, y)` reads a `Dictionary<int, Door>` keyed `x * Height + y`. It is null on maps without doors (381 of 463). `CheckDoorOpen` (player moves, bounds already checked) does one lookup, and only on maps with doors. The dictionary is written only at load, so concurrent reads are safe.
5. **Mine spots are lazy (`MineGrid`).** A grid stores the map's base mine set and its zones, in order; a later zone wins, and each zone covers `[Location − Size, Location + Size)` as before.
   - A `MineSpot` is created on the first mine of a cell, with the load-time state (0 stones, regen tick 0). A cell with no mine set returns null, and the only caller (`HumanObject` mining) already returns on null.
   - Out-of-map indexes still throw.
   - Game thread only. LiveDb `RebuildMine` swaps the whole grid, as before.

## Next (not done)
- **`Cell[,]` (177 MB) and walkable `Cell` objects (220 MB)** are now the largest map items. A `byte` attribute grid plus cell objects created only for walkable cells that hold something would save most of the ~400 MB. It touches every `GetCell(...)` / `Cells[...]` site (~300, many in monster AI), so it needs its own task.
- `Point[]` 55 MB: `Map.WalkableCells` could store one `short` Y per cell per column (2 bytes instead of 8). Callers (`MapObject` random teleport, GM random teleport) would index through a view like `WalkableBox`.
