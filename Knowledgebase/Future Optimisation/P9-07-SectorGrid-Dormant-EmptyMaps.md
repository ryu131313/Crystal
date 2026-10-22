# P9-07 — Player sector grid, dormant monsters, empty-map sleep (2026-10-21)

**Status: implemented, one read-only review (1 High, 2 Medium, 5 Low; the High and Mediums are fixed, and the Lows fixed or noted below), measured on scratch servers with bots. `SectorGridVerify` reported 0 mismatches in every run.** This is phase 2 of `P9-Plan-Events-and-Interest.md`. Server-only: no `Shared`, wire or DB change.

## Changes
| Switch (default) | What it does |
|---|---|
| `SectorGrid` (True, applies at world start) | `Map.Grid` (`MirEnvir/PlayerGrid.cs`) keeps the map's players in 8×8-cell sectors. The sector array is allocated on a map's first player.<br>**Membership** mirrors `Map.Players`: `AddObject`/`RemoveObject` add and remove, and the `HumanObject.CurrentLocation` setter moves the player.<br>**Mob-thread writes:** during a mob pass, every grid write is queued instead (a push, or a death script's map move). After the barrier, `PlayerGrid.Resync` puts the player where its current map's player list says; this runs before the deferred monster work (review M2).<br>**Readers:** `Map.PlayersNear` is an allocation-free struct enumerator. On maps with more than 16 players it walks the sectors covering the range, plus one sector of slack during a mob pass; otherwise it walks the whole list. It is used by `MapObject.Broadcast`, `Map.Broadcast`, `Map.BroadcastNPC`, `MonsterObject.CheckAlone`, `NPCObject.Show` and `SpellObject.BroadcastInfo`. |
| `SectorGridVerify` (False) | Once a second, compares every grid entry with the player's location and its map's player list, and logs any mismatches. For scratch servers. |
| `MonsterDormant` (True; needs `SectorGrid`) | A monster becomes dormant when all of these hold: alone, no player in its wake ring (review H1), no master, target or `EXPOwner`, no poison, buffs, pending actions or slaves, and its type has no AI loop of its own. A dormant monster checks for players every 10 s instead of 3 s.<br>When a player is added to a grid, or enters a new sector, alone monsters in the newly covered sectors of its 2 × DataRange ring are woken (`AloneTime = now`, `OperateTime = 0`). This happens on the game thread, outside the mob pass. A monster now reacts to an approaching player within about an iteration (before: up to 3 s). |
| `EmptyMapSleep` (True) | A map with no players or heroes checks respawns every 60 s instead of every second. The first player or hero to arrive wakes the map for its next `Process`. The four NPC-script `MonsterCount` reads call `Map.CatchUpRespawns()` first. Conquest maps and maps with tick-counter respawns never sleep. |

Monster AI teleports of players (`HornedMage`, `TurtleKing`, `GreatFoxSpirit`) run through `Envir.RunOutsideMobPass`: on the game thread right after the barrier, in the same tick, with the old retry, fallback and direction logic. This removes a cell race that existed before this change.

## Measurements
- **Setup:** `run-baseline.ps1`, 60 s windows, bots share the desktop. "Off" = `SectorGrid`, `MonsterDormant` and `EmptyMapSleep` False. Phase 1 switches were on in both.
- **"Spread" scenario:** 300 bots on `0.map` with `--no-director --roam-radius 100`, so players are spread over a large map, as in a real field.

| Run | busy | conn + send ms/s | max iter ms | players scanned/s | scanned per recipient | alone AI runs/s | mob ms/s |
|---|---|---|---|---|---|---|---|
| idle, 4 synthetic maps, off / on | 0.45 / 0.29 % | | 7.7 / 0.9 | | | **187 / 57** | **2.75 / 0.80** |
| spread 300 bots, off / on | **34.8 / 23.7 %** | 320 / 213 | **103 / 35** | **82k / 51k** | **1.77 / 1.12** | 10 / 3 | 1.7 / 0.9 |
| 500 bots, 4 synthetic maps, off / on | 16.0 / 21.4 % | 120 / 172 | 24 / 59 | 44k / 34k | 1.22 / 1.24 | 125 / 51 | 6.4 / 3.7 |

All runs had 0 decode errors, 0 isolated errors, 0 crash files and 0 `SectorGridVerify` mismatches, and every bot reached the game.

## Reading
- **The grid pays where players are spread out.** In the spread run, players checked per recipient dropped from 1.77 to 1.12, busy fell by about a third, and the worst iteration dropped from 103 to 35 ms.
- **Crowds show no gain.** On the crowded synthetic maps the scan ratio is already ~1.2, because everyone sees everyone, so the grid can't help. The busy/send difference there is in the noise seen in P9-06: packets out varied 36k vs 28k/s between the runs.
- **Dormancy works.** Idle alone-monster AI runs dropped 70 % (187 → 57/s) and the mob slice dropped from 2.75 to 0.8 ms/s. Together with P9-06's ceiling, that is 93/s for one synthetic map in P9-06's runs, against 57/s here for four maps.
- **Empty-map sleep can't be seen** in these scratch worlds (`maps` ≈ 0.1 ms/s with ≤ 4 maps). It pays on the real data's ~500 maps, where P9-05 measured the sweep at ~1.5 ms per 100 maps.

## Known limits (review Lows)
- **L2:** one sector of slack covers one push (≤ 5 cells). A player pushed twice in one pass, by two monsters, can be missed by mob-thread broadcasts until the barrier.
- **L3, sleeping maps:**
  - a respawn on a sleeping map restarts its delay from the catch-up time;
  - a LiveDb respawn added with tick counters waits for the next 60 s check before the map stops sleeping;
  - the world `MonsterCount` (dashboard, SMain) can lag by up to 60 s.
  - The plan's "N maps sleeping" note next to that count is not built. The setting's description warns about it instead.
- **L5:** a sector crossing scans 576-1088 cells for the dormant wake, and a player added to a map scans the whole ring (~5k cells). Deferred teleports allocate a closure per cast (rare).
- **Existing defect, not fixed:** `CompleteMapMovement` returns on the `RequiredGroup` gate after `RemoveObject`, leaving the player off the map's lists but with `CurrentMap` set. The grid follows the lists, so this is unchanged.

## Smoke check (real client, scratch copy)
- Walk between crowds on a busy map: everyone in view moves and attacks normally.
- Walk up to idle monsters from far away: they react when you come into view.
- A HornedMage or TurtleKing teleport.
- Enter an empty map: it is fully populated.
- An NPC that counts monsters on an empty map returns the full count.

## Lockstep fix (2026-10-22)
**Bug:** `SleepIfDormant` set `AloneTime = now + 10000` right after `CheckAlone` gave the first check its random phase, so every monster spawned on the same tick re-checked on the same ms forever. Live: ~72k `alone` runs in one second every 10 s (~5k in the others), and Tick Lateness 30-40 ms while the due scheduler worked the backlog off.

**Change (`MonsterObject.SleepIfDormant`, no new setting):**
- Delay = `delay / 2 + Envir.Random.Next(delay)`: 5-15 s (mean 10 s) with `DormantAloneDelay`; one cycle breaks the lockstep.
- On a map with **no players**, `delay = DormantEmptyMapDelay` (60 s): 30-90 s.
- `MonsterObject.Teleport` ends with `WakeFromAlone()`, so a dormant monster moved next to players re-checks at once instead of after its long sleep.
- Cost: one `Random.Next` per dormant check, no allocation, no lock.

**Wake audit (why the long empty-map sleep is safe).** `Map.Players` changes only in `Map.AddObject` / `RemoveObject`, and `AddObject` calls `Grid.Add`, which wakes the whole 2 × DataRange ring around the arrival (`WakeAround(-1, …)`). Every other move goes through the `CurrentLocation` setter → `Grid.Moved`, which wakes the newly covered sectors. During a mob pass both are deferred to `Resync` after the barrier, which then calls `Add` / `Moved`, so they wake too.

| Arrival path | Wakes? | Change |
|---|---|---|
| Login (`StartGame` → `temp.AddObject`) | yes, `Grid.Add` | — |
| Teleport, same or other map (`MapObject.Teleport`: Remove → set location → Add); recall, GM move, NPC `MOVE`, town teleport | yes, `Grid.Add` (and `Moved` on the old grid, harmless) | — |
| Map change by walking onto a movement (`CompleteMapMovement`) | yes, `Grid.Add` | — |
| Revive in place (`HumanObject.Revive`), town revive (`PlayerObject` bind-point respawn) | yes, Remove + Add | — |
| Walk, run or push inside a map | yes, `Grid.Moved` on a sector change (deferred push → `Resync`) | — |
| Approach from far away on a non-empty map | yes, each sector crossing wakes the new ring edge, which is wider than the 2 × DataRange "not alone" range | — |
| Hero spawn | n/a: heroes are not in `Map.Players` and never counted for `Alone` (unchanged) | — |
| Dormant monster teleported next to players (scripts, AI) | was **no** (kept its old `AloneTime`) | `WakeFromAlone()` at the end of `MonsterObject.Teleport` |

Other wakes checked: respawn creates a new monster (`AloneTime = 0`, first check random); a monster with a master, target, `EXPOwner`, poison, buffs, actions or slaves never goes dormant; anything a player does to a monster happens inside the player's wake ring, which keeps it out of dormancy. `SetOperateTime` already sleeps an alone monster until `AloneTime` (AloneCeiling), so the longer delay needs no scheduler change.

**Known limits:** a monster attacked by another monster (no player) on an empty map reacts only at its next check (up to 90 s, was 10 s); nobody sees it. Turning `MonsterDormant` off at run time stops the wakes, so sleeping monsters on empty maps resume within 90 s (was 10 s).

**Scratch measurement** (20 synthetic 600×600 maps, ~8,400 monsters, `[Diag] LogLoopProfile`; "before" = HEAD `bd87ac86` built from `git archive`):

| Phase | before (HEAD) `alone=` per second | after |
|---|---|---|
| Idle, all maps empty | 0, then **8,400 in one second every 10 s** (10:24:08, :18, :28) | 0 for the first 30 s, then flat 106-161/s (8,400 / 60 s mean = 140) |
| 80 bots spread over the 20 maps, 4 min | not run | flat: p50 604, p95 649 per second; no periodic burst |
| Bots leave (maps empty again) | not run | one 4 s transition (1.4k-2.3k/s, awake alone monsters taking their 30-90 s sleep), ~0 for 30 s, then flat ~140-200/s |

Bots: 80/80 reached the game, 0 errors, 721 kills, combat from the first seconds after arriving on maps whose monsters were in the 30-90 s sleep. Monster-initiated hits are not counted by the bots, so aggro on arrival rests on the wake audit above plus this combat.
