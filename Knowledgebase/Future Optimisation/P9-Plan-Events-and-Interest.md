# P9 plan — event-driven scheduling and interest management (2026-10-21)

**Status: all three phases implemented, reviewed and committed 2026-10-21** (notes `P9-06`, `P9-07`, `P9-08`; deviations from this plan are listed there). Still open: the quiet-machine reruns, the 2 h soak, and the real-client smoke checks. Reviewed once (read-only), with every finding applied; the user's decisions are at the end. Three phases:
1. Send path
2. Interest grid and sleeping world
3. Event-driven players and wire batching

Phases 1-2 are server-only. Phase 3 changes `Shared` (approved to ship before C5). There is no DB change.

The request had two parts:
1. Schedule work by events instead of polling every object every tick.
2. Interest management with batched per-client updates.

P9-05 already shipped most of (1). This plan covers the rest.

## How the work runs
- **Main session:** the main session does the work. No workflows.
- **Reviewers:** read-only reviewer subagents, all Opus 5.5 at medium effort (`model: 'opus', effort: 'medium'`). Each one runs after its phase is written.
  - **One** reviewer each for phases 1 and 2. Each covers threading and gameplay.
  - **Two** for phase 3, because it is a protocol phase: one for server safety, one for the packet contract plus the web client and StressClient codecs.
  - **Total: 4 subagents.**
- **Findings:** no per-finding judges. The main session checks each finding against the code, then fixes it or skips it.
- **Checks in every phase:**
  - `tools\verify.ps1`.
  - The phase's measurements (below).
  - A short smoke check with a real client on a scratch copy of the live data.
  - Build only through `verify.ps1`, never into `Build\`. Bots run only against scratch servers.
- **Switches:** every change gets its own `[Optimisation]` switch, with the old path kept behind it, so a phase can be bisected by switch. A switch defaults to on once the phase's numbers are in. All switches appear on the web admin settings page.
- **Rule override (approved by the user, 2026-10-21):** the roadmap's P9 rules are "one change at a time" and "two reviews for every threading or network change". This plan instead groups changes per phase, each behind a switch, with the review counts above.

## Where we are (verified in code)
- **Scheduler already in place.** P9-05 runs objects and maps from due-time heaps (`ObjectScheduler.cs`, `Map.NextProcessTime`). Idle real data went from 135.6 % to 3.7 % of one core.
- **Alone monsters still wake for nothing.** They wake 2 times every 3 s: the hard ceiling `Envir.Time + 2000` (MonsterObject.cs:1256) plus `AloneTime` (3 s).
  - Their `Process` does nothing while `Alone` (L1747).
  - They notice an approaching player up to 3 s late (the `CheckAlone` gate, L1759).
- **Broadcasts check everyone on the map.** `MapObject.Broadcast` (MapObject.cs:454), `Map.Broadcast` and `BroadcastNPC` (Map.cs:2584-2612), `CheckAlone`, NPC show/hide and 39 `CurrentMap.Players` scans all check every player on the map.
- **Sends are small and frequent.** A busy client gets about 220 `BeginSend`s per second at 500 bots: one per connection pass that has packets. `conn` (~330 ms/s) is the largest slice under load. Repeated state packets for one object all go out.
- **Players and heroes run every 10 ms** (P9-02), whatever their timers say. Some timers are checked only inside `Process` (NPC `VisTime`/`SpeechTime`, `SpellObject.StartTime`, monster-subclass timers, poison/buffs added from outside).
- **Empty maps still sweep respawns** every 1 s (phased).
- **Constraints:**
  - The client animates from action packets, in order. Batching never drops or reorders an action.
  - Mob threads move players inline: pushes of ≤ 5 cells, and teleports anywhere on the map (`HornedMage`, `TurtleKing`, `GreatFoxSpirit`).
  - The uncommitted dashboard change touches the same files, so it lands first.

---

## Phase 1 — Measure, then the send path
1. **Counters in `LoopProfiler`.** Game-thread counters use plain fields; mob-thread counters use per-thread fields summed at the barrier. They count:
   - sends/s, and packets per send;
   - players scanned vs recipients enqueued, per broadcast;
   - duplicate state packets per flush, by id;
   - object wakes/s by type, and `Process` calls that did nothing;
   - the encode-cache hit rate.
2. **"Town" bot scenario.** A StressClient option pins 300 bots to a 60×60 area of a large real map (ScratchWorld `--maps-from`).
   - Then a **quiet-machine baseline**: idle real data, 300 bots on 4 maps, town, and 500 on one map. P9-01 and P9-02 never got a clean CPU number.
   - This is the "before" for all three phases.
3. **`[Optimisation] FlushAtEnd`: flush at the end of the iteration** (lowers delay, no window).
   - **Today:** the connection pass runs first in each `WorkLoop` iteration. It receives, handles and flushes in one go (Envir.cs ~L2225), and only then do the mob pass, objects and maps run. Every packet they create (monster moves and attacks, delayed damage, spells, map actions) waits for the *next* iteration's flush. That wait is up to one iteration plus the ≤ 1 ms idle park.
   - **Change:** split `MirConnection.Process` in two:
     - receive + handle stays where it is;
     - the flush moves to one pass over all connections at the end of the iteration (after saves, before the idle park), followed by `EndEncodePass`.
   - **Effect:** responses to client packets go out in the same iteration, as today. Packets made by the world go out about 1-2 ms sooner.
     - There is still one send per connection per iteration, and the bytes are identical.
     - EncodeOnce now covers everything enqueued in the whole iteration, so its hit rate can only rise.
   - **Unchanged:** direct `BeginSend` paths (`SendDisconnect` MirConnection.cs:1027, the wrong-version reply L1060, the shutdown drain) flush the pending queue first, so `S.Disconnect` never overtakes queued packets. Any stage other than `Game` still flushes inside the connection pass.
4. **`[Optimisation] SendFlushMs`** (live, 0-5; default 0 = flush every iteration).
   - **Cap:** at most 5 ms (user, 2026-10-21: reduce delay, 5 ms at most).
   - **Shared tick:** above 0, Game-stage connections all flush on the same end-of-iteration pass, the one where `Envir.Time` crosses the next multiple of the window. EncodeOnce still encodes each broadcast once.
   - **Flush at once:** more than 256 pending packets; the `KeepAlive` reply.
   - **Expected:** a busy client gets ~220 sends/s today, about one every 4-5 ms, so a 5 ms window cuts sends only modestly (−10 to −30 %, to be measured).
     - Its main value is giving step 5 (coalescing) a window to work in.
     - Keep it at 0 if the numbers don't pay for the delay.
   - **Watch:** max and p99 iteration, because all sends land in one pass.
5. **`[Optimisation] SendCoalesce`.**
   - **What:** within one flush window, an older pending copy of an idempotent state packet for the same object is nulled in place. The flush already skips nulls (MirConnection.cs:380). The newer copy keeps its later position, so nothing moves earlier.
   - **Allow-list:** `ObjectHealth`, `ObjectColourChanged`, `ObjectPoisoned` and `ObjectName`. All are plain overwrites in `GameScene.ProcessPacket` (L4596, 3371, 3507, 4429). Check the same handlers in the web client.
   - **How:** each pass drains `_sendList` into a per-connection pending list of `Packet` references, deduplicated with a `(packet id, object id)` table that is reset each flush. Encoding still happens at flush. Without a window it only catches duplicates from the same iteration; with `SendFlushMs` = 5 it catches more.
6. **`[Optimisation] AloneCeiling`.**
   - **What:** in `MonsterObject.SetOperateTime`, while `Alone && !MonsterProcessWhenAlone`, the ceiling becomes `AloneTime` instead of +2000.
   - **Exceptions:** subclasses that act while alone (spawners, summoners, timed bosses) keep 2 s. Find them by grepping the `Process`/`ProcessAI` overrides in `Monsters/`; the list is reused in phase 2.
   - **Effect:** behaviour is identical, and idle wakes are halved.

**Measure:** the four baseline scenarios, with the phase switches off vs on.

**Smoke check:**
- Log in, walk and fight in a busy spot, and watch the ping.
- Kick a player: the message must arrive before the disconnect.
- Try `SendFlushMs` at 0 and 5 live, and check that combat feels no slower at 5.

**Review:** one Opus 5.5 medium reviewer.

## Phase 2 — Interest grid and a sleeping world
1. **`[Optimisation] SectorGrid`.**
   - **Index:** one lazily allocated player list per 8×8 (or 16×16) block per map, holding players and heroes.
   - **Updates:** the sector is updated in the `HumanObject.CurrentLocation` setter (HumanObject.cs:36-40), the map-change path and `AddObject`/`RemoveObject`. That covers all ~40 movement sites without touching them.
   - **Teleports:** monster-AI **teleports** of players are deferred through `Envir.RunOutsideMobPass`, so they run in the same tick, after the barrier. This also fixes today's cell race.
     - Decided 2026-10-21: the user left it to Claude. Chosen because the player still moves in the same tick and a cross-thread write goes away.
     - The phase 2 review checks each AI's follow-up code: `HornedMage`, `TurtleKing`, `GreatFoxSpirit`.
   - **Pushes:** pushes stay inline. While `InMobPass`, the setter queues a sector fix-up, and readers scan one sector of slack (a push moves ≤ 5 cells).
   - **Readers:** `MapObject.Broadcast`, `Map.Broadcast`, `BroadcastNPC`, `BroadcastHealthChange`, `CheckAlone`, **NPC show/hide**, **`SpellObject` area checks**, and the other `Players` scans that phase 1's counters rank.
     - They use the grid only above ~16 players on the map; below that, a linear scan is cheaper.
     - The final `InRange` always uses the real location.
2. **`[Optimisation] SectorGridVerify`** (scratch only, default off).
   - Once a second, it compares each player's sector with their real location and logs mismatches once per window.
   - It stays on for every bot run in this phase.
3. **`[Optimisation] MonsterDormant`.**
   - **Dormant condition:** an alone monster with no master, route, target, `EXPOwner`, poison, timed buffs, pending `ActionList` or slaves, and not on the phase 1 subclass list.
   - **Sleep:** its `AloneTime` grows from 3 s to 10 s.
   - **Wake:** when a player's sector changes on the game thread, monsters in the newly covered cells of the 2 × DataRange ring get `OperateTime = 0` **and** `AloneTime = Envir.Time`. They then react within about an iteration (today: up to 3 s).
     - `AloneTime` must be non-zero, because 0 re-randomises the delay (L1763).
     - A crossing caused by a mob-thread push defers its wake through `RunOutsideMobPass`.
4. **`[Optimisation] EmptyMapSleep`.**
   - **What:** a map with no players or heroes, an empty `ActionList` and no open door stops its respawn sweep. Its `NextProcessTime` becomes the earliest of lightning/fire, the next timed map event, or a 60 s safety tick.
   - **Catch-up:** respawns run on three occasions:
     - when the first player arrives, by waking the map for its next `Process`, so the spawn burst stays out of the player's packet handler;
     - **before any read of `Map.MonsterCount`**: the `MONSTERCOUNT()` and mob-count checks (NPCSegment.cs:1300, 1796) and any other reader found by grep call `map.WakeForCount()` first;
     - on the 60 s safety tick.
   - **Exempt maps** (keep the current sweep): maps with tick-counter respawns (`RespawnTick`), and conquest maps.
   - **Web admin:** the world monster count (`Envir.MonsterCount`, status page, dashboard sampler) shows "N maps sleeping", so a low number doesn't read as a bug.

**Measure:**
- town vs baseline: scanned ÷ enqueued ≤ 2;
- idle real data: the remaining `mob` bursts;
- 300 bots on 4 maps.

**Smoke check:**
- A busy area where players walk in and out of view.
- Doors, hide, and a monster that teleports or pushes you.
- Monsters reacting when you walk up.
- Enter an empty map: it should be fully populated.
- An NPC script that counts monsters on an empty map should return the full count.

**Review:** one Opus 5.5 medium reviewer.

## Phase 3 — Event-driven players and wire batching
1. **Fold the `Process`-only timers into `SetOperateTime`:**
   - NPC `VisTime`/`SpeechTime`;
   - `SpellObject.StartTime`;
   - monster-subclass timers, per family;
   - poison/buffs added from outside: `ApplyPoison`/`AddBuff` set a new due time.
   - **Known defect fix:** `IntelligentCreatureObject.SetOperateTime` compares `CreatureTime` but assigns `RoamTime` (L98-108). Approved by the user's instruction to include these items (2026-10-21: "include the later/cut items as I think they will definitely be worth doing").
2. **`HumanProcessMs` becomes a cap** (e.g. 250 ms).
   - `HumanObject.SetOperateTime` computes the next due time from its own timers: buffs, poison, regen/potions, rested, torch/durability, mail, fishing, creature timers, and the `ActionList` head.
   - Heroes stay at 10 ms, because their AI runs inside `Process`.
   - **Expected:** an idle-standing player drops from 100 `Process` calls/s to about 2-10.
3. **Wire-level batching** (`Shared` change):
   - **`S.ObjectMoveBatch`:** many walk/run steps in one packet, as per-object deltas.
   - **A view-entry batch** on map change and teleport. Today these send one `S.ObjectPlayer`/`S.ObjectMonster` per visible object.
   - **The server decides per flush:** it batches only when several moves are pending for one client, and the old packets stay valid.
   - **Packet rule** (AGENTS.md):
     - IDs appended at the end;
     - `ReadPacket`/`WritePacket` with identical field order;
     - the `Packet.cs` case;
     - client handlers in `GameScene.ProcessPacket`;
     - the TS port and `WebRelay/Fixtures.cs`, then `npm run codec:regen` + `codec:test`;
     - `WebRelay --emit-ts`;
     - the StressClient decode;
     - the manifest regenerated with `MIR2_REGEN_PACKET_MANIFEST=1`;
     - `Shared.Tests`;
     - client and server from one commit, shipped through the in-game patcher.
   - **Rule override (approved by the user, 2026-10-21: "those dates are arbitrary, proceed"):** the roadmap holds P9 `Shared` items until C5. This item ships before C5 instead, with client and server from one commit, through the in-game patcher.

**Measure:**
- 500 bots on one map and 300 on 4 maps: `obj` ms/s, packets out/s, KB out/s;
- 0 decode errors on bots and the web client.

**Smoke check:**
- Buffs, poison and regen expire on time.
- Fishing, mail and a hero fight.
- Watch a crowd walk past on the desktop client and on the web client.
- Teleport into a busy map.

**Review:** two Opus 5.5 medium reviewers: server safety, and packet contract plus web client.

## Done when
- The roadmap P9 gate: 500 bots on one map at ≤ 50 % of baseline busy, p99 iteration under 50 ms, and a clean 2 h soak.
- Phase targets: world-made packets out ~1-2 ms sooner, with sends and encode cost no worse (phase 1), town scanned ÷ enqueued ≤ 2 (phase 2), and idle-player `Process` calls/s down at least 10× (phase 3).
- One note per phase (`P9-06`, `P9-07`, `P9-08`) with the numbers, the review findings and their fixes.

## Rules for implementers
- **server.md §3.1:**
  - no LINQ, closures, boxing or per-call allocations in `Broadcast`, the setter, `SetOperateTime`, `CheckAlone`, the flush or the coalescing table;
  - index loops;
  - reused, pre-sized lists;
  - one `BeginSend` per connection per flush;
  - bounded pending lists.
- **server.md §3.2:**
  - no locks, no `async`, no new waits;
  - mob-thread code never writes the sector index or another owner's `AloneTime`: defer it with `RunOutsideMobPass`;
  - wakes use the P9-05 path.
- **server.md §3.5:** phases 1-2 make no packet change. Coalescing nulls only allow-listed duplicates and never reorders. Observers keep `BroadcastObservePackets`.
- **server.md §5.2:** no per-tick string formatting.
- **Packet rule and Shared rules (phase 3):** as listed in phase 3.
- **Known debt left as is:** inline mob-thread pushes, per-cell lists, and the `Players` scans the counters don't rank.

## Decisions (answered 2026-10-21)
1. Grouping changes per phase, with the review counts above: **approved**.
2. Wire batching before C5: **proceed**. The user said the dates are arbitrary.
3. Flush delay: **reduce delay; 5 ms at most**. Phase 1 now moves the flush to the end of the iteration (lower delay). The window option is capped at 5 ms and stays at 0 unless the numbers pay for it.
4. Deferring monster teleports of players: **left to Claude**. Deferred, same tick (phase 2).
