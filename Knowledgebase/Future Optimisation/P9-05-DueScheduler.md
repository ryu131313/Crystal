# P9-05 — Due-time object scheduler (2026-10-11)

**Status: implemented, two independent read-only reviews (threading; per-object timing) with all findings applied, measured on a copy of the live data and with bots.** Server-only; no `Shared`, DB or wire change. Switch: `[Optimisation] DueScheduler` (default True, read at world start; also on the web admin settings page).

## Problem
The live server used ~1.3 cores with nobody online. The game thread walked every object in `Envir.Objects` every iteration (skipping mob-thread monsters node by node), and each mob thread walked its whole monster list every tick, although an idle monster is due only every ~2 s. With the real world loaded one walk took ~6 ms, so iterations never finished in < 1 ms and the P6 idle park never fired. The synthetic baseline (one 200×200 map) never showed it.

## Change
- `Server/MirEnvir/ObjectScheduler.cs`: one min-heap per owner (game thread, each mob thread) keyed by due = `OperateTime + 1`, plus a `ConcurrentQueue` of wakes. Each pass pops only due objects → `Process(); SetOperateTime();` → re-scheduled at `max(OperateTime + 1, Time + 1)`.
- `MapObject.OperateTime` is now a property: a write that makes the object due earlier than its entry (the ~60 `OperateTime = 0` sites, from any thread) wakes the owner. Write + full fence + read `ScheduledDue` pairs with the owner's publish + fence + re-read, so no early write is lost.
- Entries are lazy: live only while `due == ScheduledDue`, `Node != null` and `SpawnedGeneration` is the current world run. `Spawned()` resets `ScheduledDue` so every spawn is scheduled; stale entries are skipped and compacted when the heap exceeds twice its live size.
- Ownership is the old split (unowned monsters on `MobThreads[SpawnThread]`, everything else on the game thread), checked when an entry pops; a `Master` change is forwarded at the next due time, like the old walk. A dead mob thread's objects move to the game thread (the old walk froze them).
- Barrier: mob threads are woken only when their scheduler has work; thread 0 waits with `SpinWait` instead of re-walking its list.
- "Cycle delay" (`Envir.LastRunTime`, `MobThread.LastRunTime`) now = worst lateness of a processed object in the last second (was full-walk time).
- `DueScheduler=False` keeps the old walk unchanged.

## Measurements
**Copy of the live data** (`Build\Server\Release` read-only → scratch, port 17100, no players, `real-idle` harness, 45 s settle, 60 s window):

| DueScheduler | CPU % of one core | it/s | busy | mob ms/s | obj ms/s | maps ms/s | park ms/s | max iter |
|---|---|---|---|---|---|---|---|---|
| False (old) | **135.6** | ~100 | 100 % | ~360 | ~625 | ~15 | ~0 | 12-18 ms |
| True | **3.4** | ~685 | 3-6 % | 3-31 | 0.4-0.8 | ~25 | ~950 | 0.6-4.9 ms |

**Synthetic world + bots** (`run-baseline.ps1`, same machine, bots on it too):

| Scenario | CPU % off → on | busy off → on | mob ms/s off → on | obj ms/s off → on |
|---|---|---|---|---|
| idle, Multithreaded on | 1.1 → 0.7 | 2.05 → 0.28 | 13.9 → 0.9 | 4.5 → 0.14 |
| 200 bots | 16.9 → 7.0 | 23.3 → 13.2 | 18.7 → 2.1 | 41.7 → 22.5 |
| 500 bots | 57.8 → 51.9 | 62.3 → 55.7 | 6.7 → 2.6 | 65.9 → 53.7 |

500 bots are dominated by `conn` (~500 ms/s, packet handling + sends), so the scheduler matters less there; max iteration was noisy (111 vs 164 ms, the machine also ran the bots). Every run: 0 decode errors, 0 isolated errors, 0 crash files, all bots in game.

Combat sanity (200 bots, 60 s): off 534 attacks / 118 kills, on 708 attacks / 103 kills (random bot behaviour; monsters target, fight, die and respawn in both).

## Behaviour differences (accepted)
- A wake (`OperateTime = 0`) takes effect on the owner's next pass (~1 ms) instead of possibly later in the same walk.
- Objects run in due order, not spawn-list order; ties have no fixed order.
- An object whose `OperateTime` is already past runs at most once per ms (the old walk could run a human twice in one ms).
- Timers checked only inside `Process` and not folded into `SetOperateTime` fire as late as before (the old walk had the same gate). Reviewers listed them (NPC `VisTime`/`SpeechTime`, `SpellObject.StartTime`, many monster-subclass timers, poison/buffs added from outside); unchanged, not regressions.

## Follow-up: map scheduler (same day)
After the object scheduler, `maps` was the largest idle slice (~25 ms/s): every one of ~500 maps was checked every iteration, and the respawn sweep cost ~1.5 ms per 100 ms.
- With `DueScheduler`, maps run from a game-thread min-heap keyed by `Map.NextProcessTime` (= earliest of respawn check, lightning, fire, an open door's 5 s close, and the earliest queued map action). `Map.ActionList` is now a `MapActionList` whose `Add` (hiding `List.Add`; all ~55 call sites use the field) wakes the map through a `ConcurrentQueue`, so delayed map actions still run on time. Maps added at runtime are picked up by count; maps of an earlier world run are ignored (`ScheduledGeneration`).
- `[Optimisation] MapRespawnCheckMs` default 100 → **1000** (max 5000), with a random per-map phase (re-picked when the interval changes), so the sweeps don't land in one iteration. A respawn added live spawns within 1 s.
- An exception in `Map.Process` retries the map next tick, as the old loop did.
- Two reviews (threading/wakes; gameplay of every `DelayedType`, respawns, doors, weather): no lost wakes, no late actions; their Lows applied (door close time folded in, re-phase on interval change, retry on exception). Accepted: an action added during a map's own `Process` costs one extra `Process` call; a busy map keeps up to ~1 s of stale heap entries (bounded).

| Real data, idle, DueScheduler on | CPU % of one core | busy | mob ms/s | maps ms/s | park ms/s |
|---|---|---|---|---|---|
| object scheduler only | 3.4-6.3 | 3-6 % | 3-48 | 15-25 | 935-980 |
| + map scheduler | **3.7** | **0.5-3.4 %** | 2-31 | **0.75** | 966-995 |

What is left at idle is real work: the remaining `mob` bursts are monsters' own ~2 s housekeeping (`CheckAlone`, regen), which arrives in waves because the world spawns them together.

200 bots (synthetic) on the final build: CPU 5.2 %, busy 9.6 %, 833 attacks / 143 kills, 0 errors.

## Left for later
- `IntelligentCreatureObject.SetOperateTime` compares `CreatureTime` but assigns `RoamTime` (existing bug, unchanged).
- A 2 h soak on the live-data copy with bots, and a check of the 2.8 GB GC heap (most likely map cell arrays).
