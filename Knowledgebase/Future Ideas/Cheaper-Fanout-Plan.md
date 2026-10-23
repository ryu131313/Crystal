# Plan — cheaper per-delivery send path (P9-15, proposed 2026-10-22)

**Done 2026-10-22, results in `Future Optimisation/P9-15-DirectSend.md`.** The crowd throughput target was not met; normal-load send cost about halved. Default left off for the user's decision. The ready marks kept `MarkReady` (already one volatile read per packet) instead of adding a game-thread ready list.

**Goal:** more broadcast deliveries per game-thread second in crowds. Baseline (P9-14, server GC, 3000 bots on one spawn): **0.40 µs per recipient** ((`chand`+`senc`)/`recv`), **2.57M recipients per busy second**. Target: ≥ 30% more recipients per busy second. This still grows with the square of the crowd; it raises the ceiling, it does not remove it (that is the visibility cap, not in this plan).

No wire change, no DB change, no `Shared/` change. One live switch, so A/B runs in one build.

## What a delivery costs today (verified in code)
1. `MapObject.Broadcast` → `HumanObject.Enqueue` → `MirConnection.Enqueue` per recipient:
   - a `ConcurrentQueue<Packet>.Enqueue` (an interlocked op, and segment allocation as the queue grows);
   - `MarkReady` (a volatile read, plus one interlocked op and a `ConcurrentQueue` enqueue on the first packet per pass);
   - a `Stage`/`FlushAtEnd` check, and `p.Observable` plus an `Observers` walk.
2. `Flush`:
   - `TryDequeue` each packet into `_pending` (a second pass over every packet), with `Supersede` when SendCoalesce is on;
   - per packet, `Encode(p)` → `EncodedThisPass.TryGetValue` (a reference hash: `RuntimeHelpers.GetHashCode` per recipient);
   - `BlockCopy` into the send array.

The concurrent queue exists because mob threads enqueue too. They do so **only inside the mob barrier**: `WaitForSubThreads` is unbounded while running, so outside `[arm .. EndMobPass]` every world packet comes from the game thread. Exceptions: the accept thread's `S.Connected` in the constructor, and anything rare off-thread. Login password workers and the web admin go through `QueueAdminAction`.

## Design
### A. Game-thread send list (`[Optimisation] DirectSend`, default off until measured)
**Ownership:** `_pending` becomes the queue for the game thread. `Enqueue` on the game thread outside a mob pass (`Environment.CurrentManagedThreadId == DesignStore.GameThreadId && !Envir.InMobPass`) appends straight to `_pending`, running `Supersede` at append time. There is no concurrent queue and no second pass.

**Mob-pass enqueues, sub-threads:** each sub-thread (`InMobPass`, not the game thread) appends `(MirConnection, Packet)` to its own `MobThread.Outbox` (a plain list, single writer, no interlocked op).

**Replay:** `EndMobPass` replays the outboxes in thread order through the direct path, **before** the deferred list ops and actions. So mob packets land between the handler packets from before the barrier and the object-pass packets after it, which is the order today's single queue gives.

**Thread 0 on the game thread during the pass:** it writes direct. Its packets interleave freely with the sub-threads', the same as today (they run concurrently).

**Other threads (rare):** they keep the `ConcurrentQueue` (`_sendList`). `Flush` drains it into `_pending` first. Those packets are concurrent with the game thread, so they have no ordering guarantee today either.

**Ready marks:** with a direct enqueue, the game thread adds the connection to a game-thread `List<MirConnection>` ready list (plain `bool` mark). Off-thread enqueues keep `ReadyToFlush`. `FlushEndOfTick` takes both.

**Unchanged:**
- `Observers` mirroring, the `Stage`/`MarkProcessReady` rule, `KeepReadyIfPending`;
- disconnect cleanup (`_sendList = null` → also clear `_pending` and the mark);
- `PendingTrimAbove` trimming, applied to the outboxes too.

**Live switch:** flipping it at any time is safe. Both queues are always drained, and only the enqueue side looks at the switch.

### B. Shared encoded bytes per broadcast (same switch)
`MapObject.Broadcast` (any thread) wraps the packet once in a `SharedFrame { Packet P; byte[] Bytes; }` box: one allocation per broadcast, not per recipient. It then enqueues the box's entry.

`_pending` becomes a `List<OutEntry>` (struct: `Packet`, `SharedFrame`).

At `Flush` (game thread): `bytes = entry.Frame?.Bytes ??= ToBytes(p)`. The first recipient encodes, the rest copy, with no hash lookup.
- `Supersede` and `TryBatchMoves` read `entry.Packet` as now. A batched move never encodes its box.
- Single sends with no box keep `EncodedThisPass`.
- `CheckNotMutated` sampling also covers box hits, so the "never change a packet after enqueue" guard stays.
- The box is encoded lazily on the game thread, so mob threads never encode. No thread-safety change for `GetPacketBytes`.

## Phases (one smoke check each)
1. **A + B behind `DirectSend`:**
   - add the switch to `Settings`, `Setup.ini` read/write and the web admin `SettingsCatalog`;
   - extend the `[Diag] Loop` fields (append only): `dsend` = direct enqueues, `obox` = replayed mob-outbox entries, `fhit` = box hits;
   - **Smoke:** `tools\verify.ps1`, then 200 bots on a scratch server with the switch on: 0 decode errors, 0 isolated errors, every bot in game, `[Optimisation] EncodeOnce` mutation warnings unchanged (none).
2. **Measure and decide:**
   - **A/B:** the P9-14 crowd run (3000 bots, one spawn, server GC), switch off vs on, same build; plus `run-baseline.ps1 -Bots 500` off/on for normal load.
   - **Report:** µs per recipient, recipients per busy second, `max`, `alloc`, `gcpause` in `Future Optimisation/P9-15-DirectSend.md`.
   - **Default:** on if the target holds and normal load is not worse.
   - **Docs:** server.md §2.5 Send path, §3.2 thread rules (mob sub-threads enqueue into `MobThread.Outbox`), §3.1.
   - **Review:** one read-only server review (ordering across the barrier, disconnect, live switch flip), then fix.

## Risks and how the plan covers them
| Risk | Cover |
|---|---|
| Reordering a monster's packets against a game-thread packet for the same object (e.g. walk vs died) | Replay at `EndMobPass` keeps phase order; the review checks this explicitly |
| An unknown off-thread enqueue taking the direct path | The thread check is exact (`GameThreadId` and `!InMobPass`); anything else falls back to the concurrent queue |
| A `Packet` changed after `Broadcast` (the box encodes on the first flush) | Same contract as `EncodeOnce` today; the sampled guard covers boxes |
| Outbox growth in a huge mob pass | Trim like `_pending`; `PendingTrimAbove` |
| `GetPacketBytes` on a mob thread | Never: boxes encode on the game thread only |
| Gains smaller than estimated (EventPipe over-counts native frames) | Phase 2 measures before the default flips; the switch stays as rollback |

## Not in scope
- the visibility cap;
- `MoveBatch` default (needs patched clients);
- log4net on the game thread (Known debt);
- StressClient changes.
