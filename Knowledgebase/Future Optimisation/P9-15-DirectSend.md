# P9-15 — DirectSend: game-thread send list, mob outboxes, shared broadcast frames (2026-10-22)

**Status: implemented behind `[Optimisation] DirectSend` (default off), measured on scratch servers, one read-only review (no Critical/High; its Medium and one Low fixed).** No wire change, no DB change, no `Shared/` change. Plan: `Future Ideas/Cheaper-Fanout-Plan.md`.

## Change
| Switch (default) | What it does |
|---|---|
| `[Optimisation] DirectSend` (**False**; web admin "Cheaper packet fan-out", live) | **Game thread:** `MirConnection.Enqueue` appends straight to the connection's `_pending` list (`TryEnqueueOwned` → `AddOwned`, with SendCoalesce applied on the way in) instead of the `ConcurrentQueue` that `Flush` drained later. Anything still waiting in the queue goes first (review M1).<br>**Mob sub-threads during a pass:** append `(connection, packet)` to their own `MobThread.Outbox` (`[ThreadStatic] MobThread.Current`, set in `ThreadLoop` for `Id != 0`). `Envir.EndMobPass` replays the outboxes first, in thread order (`ReplayMobOutboxes` → `MirConnection.ReplayOutbox`), only for threads that are parked, dead or exited.<br>**Other threads** (accept thread, a WinForms kick, a mob thread past a bounded shutdown barrier): the concurrent queue, as before.<br>**Broadcasts** (`MapObject.Broadcast`, `Map.Broadcast`, `Map.BroadcastNPC`): every recipient of one call shares one `SharedFrame`. It is encoded at the first flush on the game thread and copied for the rest, with no per-recipient `EncodedThisPass` lookup. `EncodeOnce=False` still encodes per recipient. The no-cache flushes (disconnect, wrong client version) re-encode frames too (review L3). `CheckNotMutated` samples frame hits. |

`_pending` is now `List<PendingPacket>` (packet + frame); a superseded entry is `default`. Profiler fields appended to `[Diag] Loop`: `dsend` (direct enqueues/s), `obox` (outbox entries replayed/s), `fhit` / `fmiss` (frame copies / frame encodes per second).

**Ordering:** the same as before per connection, except packets that monster threads 1..N and thread 0 send in the **same** mob pass. Those used to interleave in real time; now thread 0's come first, then each sub-thread's in order. They already raced (two threads), so no order was guaranteed (review L2).

## Measurements
- **Setup:** scratch servers, server GC (P9-14), Multithreaded on (`ThreadLimit=2`), bots on the same desktop as the live server.
- **Crowd:** 3000 bots on one spawn (`crowd-ab.ps1` in the session scratchpad: `-Maps 1 -MaxUser 12000`, `--ramp 100 --connect-budget 100`, 150 s), saturated windows only (`pout` ≥ 100k).
- **Normal load:** `run-baseline.ps1 -Bots 500 -NoIdle`, 4 synthetic maps, 60 s.

**Crowd (saturated game thread), 3 on / 4 off runs:**

| Run | Recipients per busy second | Worst iteration, median of 1 s windows | All 3000 in game after |
|---|---|---|---|
| off 1 / 2 / 3 / 4 | 2.17M / 2.51M / 3.07M / 2.27M | 749 / 328 / 496 / 803 ms | 93 / 61 / 59 / 95 s |
| on 1 / 2 / 3 | 2.29M / 2.37M / 2.31M | **64 / 87** / 543 ms | 56 / 55 / 78 s |

- **Throughput:** no change. On averages 2.32M, off 2.50M (median 2.39M). The off runs alone spread 2.17-3.07M, so the difference is inside the noise. **The plan's target (≥ 30% more recipients per busy second) is not met.**
- **Why the target was missed:** the CPU profile that motivated the plan (P9-14 "Profiling note") over-counted the queue and hash frames. Per-recipient cost is mostly memory traffic on per-connection state, which this change does not reduce.
- **Tail:** in 2 of 3 on runs the loop settled into many short iterations (114-125 per second, worst ~64-87 ms) instead of a few second-long ones (2-5 per second). The third on run did not (543 ms). The mechanism is not pinned down; treat it as likely, not guaranteed.
- **Bot KeepAlive RTT** (median after 70 s): off 412 / 207 ms, on 44 / 214 ms. That includes the 3000 bots' own delays, so it is noisy.
- **Health:** all runs PASS: every bot in game, 0 decode errors, 0 bot errors, 0 isolated errors, 0 FATAL lines, 0 `EncodeOnce` warnings.

**Normal load (500 bots):**

| Run | Busy | conn + send ms/s | Send ms/s | Encode + copy ms/s | Recipients/s | Send µs per recipient |
|---|---|---|---|---|---|---|
| off (a) | 9.51 % | 79.8 | 32.7 | 14.4 | 198k | 0.166 |
| off (b) | 8.85 % | 72.3 | 28.7 | 13.2 | 149k | 0.193 |
| **on** | **7.25 %** | **59.3** | **15.5** | 10.7 | 162k | **0.096** |

- **Send phase:** about half the cost per recipient; the drain of the concurrent queue and the cache lookups are gone.
- **Loop busy:** about 20% lower than the mean of the two off runs. Those two off runs differ by 7%, which is the noise floor. Only one on run was made.
- **"off (b)":** a run that was meant to be "on". Through `powershell -File` the `-ExtraIni` array became a single line, `[Optimisation],DirectSend=True`. It is kept as a second off sample. Call `run-baseline.ps1` with `&` from PowerShell for an A/B.

## Reading
- **Crowd ceiling:** DirectSend does not raise it. Only a visibility cap changes the N² cost (P9-14 "Remaining crowd options").
- **Normal load:** a measurable win in the send phase (about half the cost per delivery) and a lower busy share.
- **Crowds:** often shorter worst iterations.
- **Risk:** no correctness findings above Low remain. Memory is the same (struct entries instead of queue segments; outbox capacity trimmed above 65 536 entries).
- **Default:** off, pending the user's decision. The plan's throughput gate failed, while its "normal load not worse" gate passed with room to spare. Recommendation: turn it on for a scratch soak, then live, watching `max` and `chand`.

## Review (read-only, 2026-10-22)
- **M1, fixed:** a direct entry could overtake older packets still in the concurrent queue, after a live off→on switch or from an off-thread sender. `AddOwned` now drains the queue first.
- **L2, accepted:** in one mob pass, thread 0's packets now precede the sub-threads'. They already raced; the catalog text says so.
- **L3, fixed:** frame bytes were reused on the no-cache flush paths. Those now re-encode.
- **L4, accepted (performance only):** a packet both broadcast (frame) and sent with `Enqueue(p)` in the same pass is encoded twice. One `SharedFrame` per broadcast with at least one recipient, also when an off-thread recipient falls back to the queue.
