# P9-13 — Send and connection passes visit only connections with work (2026-10-22)

**Status: implemented, measured on a scratch server with 500 bots. Not reviewed yet.** No wire change, no DB change.

## Why
Live dashboard (2026-10-22, live server + its bot fleet): Send phase 230-250 ms/s, but encode + copy (`senc`) was ~25 ms/s and the hand-off to the sender thread (`scall`) ~16 ms/s, so about 200 ms/s was not encoding or sending. The loop runs ~800 iterations/s (idle park ≤ 1 ms), and both the connection pass (`Process`) and the end-of-iteration send pass (`FlushEndOfTick`) visited **every** connection on every pass, while only ~65 connections per iteration had anything to send. The cost was connections × iterations (one cold `MirConnection` + its queues per visit).

## Change
| Switch (default) | What it does |
|---|---|
| `[Optimisation] FlushReadyOnly` (**on**, live; web admin catalog "Send pass skips idle players") | `MirConnection.Enqueue` (any world thread) puts the connection on a static `ConcurrentQueue<MirConnection> ReadyToFlush` the first time it queues a packet after its last visit (`_readyQueued`, Interlocked 0→1), so each connection is in the queue at most once. `Envir.FlushEndOfTick` drains the queue (`TakeReady`, clears each mark with a full fence before the visit) and flushes only those connections. A connection still holding unsent packets off the shared `SendFlushMs` tick goes back on the queue (`KeepReadyIfPending`, in game and connected only). The queue is always drained, also with the switch off (full sweep as before) and with `FlushAtEnd` off, so marks never go stale across a live switch. Disconnected connections in the queue are skipped (`Stage != Game`). |

Profiler (`[Diag] Loop`, appended): `cvis` (connections visited by the connection pass /s), `fvis` (by the send pass /s), `chand` (ms/s in client packet handlers, the game-logic part of `conn`). `tools/run-baseline.ps1` reports them in a "Connection visits" table.

Hot-path cost: per `Enqueue` one volatile read (plus one Interlocked + one queue enqueue for the first packet per connection per pass); no allocation in steady state (`_readyScratch` is a reused field). The send pass becomes O(connections with output) instead of O(connections).

## Measurements
`run-baseline.ps1 -Bots 500 -Seconds 45 -NoIdle`, synthetic map, Multithreaded on, bots and server on the same desktop as the live server and its bot fleet. Two "on" runs (the first pair accidentally ran both sides on, see the note below), one "off".

| Run | busy | it/s | send ms/s | encode+copy | send call | send pass visits/s | send minus encode and call | pkts out/s |
|---|---|---|---|---|---|---|---|---|
| off | 14.6 % | 862 | **50.7** | 12.8 | 5.5 | 269.9k | **32.4** | 178k |
| on (a) | 10.5 % | 882 | **19.6** | 9.3 | 5.0 | 13.9k | **5.3** | 95k |
| on (b) | 11.2 % | 854 | **21.9** | 10.3 | 5.2 | 15.2k | **6.4** | 131k |

All runs: every bot reached the game, 0 decode errors, 0 disconnects, 0 isolated errors, 0 crash files.

Connection pass, same runs (`conn` / handlers / rest; visits/s): off 81.0 / 34.0 / 47.0 (269.9k); on (a) 69.6 / 21.1 / 48.5 (269.5k); on (b) 76.2 / 27.5 / 48.7 (268.4k).

## Reading
- **Send-pass overhead outside encode and the send call fell from 32 to 5-6 ms/s (−80 to −84 %)**, visits from 270k/s to 14-15k/s (≈ sends/s). That overhead was ~120 ns per idle visit. Packet output differed between runs (bots crowd differently), so compare the overhead column, not the raw send column.
- **The connection pass has the same shape:** handlers are only 21-34 ms/s of it; ~47-49 ms/s is the per-connection sweep (~175 ns × 270k visits/s), unchanged by this switch. Step 3 below.
- On the live server (more connections) the unexplained ~200 ms/s of Send should mostly go; check `fvis` vs `sends` and the Send phase after a deploy.

## Step 3 — connection pass processes only connections with input (`[Optimisation] ConnSweepMs`)
| Switch (default) | What it does |
|---|---|
| `[Optimisation] ConnSweepMs` (**50**, 0-1000, live; web admin catalog "Full connection check interval (ms)") | A second static queue, `MirConnection.ReadyToProcess` (`_processQueued`, at most once per connection), is marked by: the receive thread after decoding packets; `Enqueue` when the connection is not in stage Game or `FlushAtEnd` is off (there `Process` is what sends); the accept thread right after `Connections.Add` (`Listed = true`, so `S.Connected` leaves on the next pass). Between sweeps the connection pass drains the queue and calls `Process` only for those; a connection not yet `Listed` is re-marked for the next pass (it could otherwise be disconnected before the accept thread adds it, and then stay in `Connections`). After `Process`, a connection whose receive queue is still non-empty (retries moved back, or a loop cut short by `Disconnecting`) is re-marked, as the full pass revisited it every pass. Every N ms the old full pass runs (timeouts incl. the 500 ms `Disconnecting` grace, pre-login limit, closed sockets, `_sendFaulted`), and also drains the queue, so marks never go stale across a live switch. A `Time` reset (world restart) forces a sweep. 0 = old behaviour (every connection every pass). |

Cost: one Interlocked + enqueue per receive callback and per non-game `Enqueue` (first one per pass); no steady-state allocation (`_processScratch` reused). Housekeeping latency: a dropped or timed-out connection is noticed up to N ms later (50 ms on top of a 500 ms / 10 s timer).

### Measurements (same setup, 500 bots, 45 s, FlushReadyOnly on, runs alternated 0 / 50 / 0 / 50)
| Run | busy | it/s | conn ms/s | handlers | conn minus handlers | connection pass visits/s | conn + send ms/s | max iter ms |
|---|---|---|---|---|---|---|---|---|
| ConnSweepMs=0 (a) | 9.4 % | 879 | 65.3 | 14.9 | **50.4** | 271.1k | 79.6 | 175 |
| ConnSweepMs=50 (a) | 6.2 % | 951 | 29.8 | 17.0 | **12.8** | 8.5k | 47.0 | 155 |
| ConnSweepMs=0 (b) | 11.4 % | 862 | 78.0 | 27.1 | **50.9** | 270.3k | 100.1 | 97 |
| ConnSweepMs=50 (b) | 6.9 % | 936 | 35.3 | 22.2 | **13.1** | 8.5k | 54.0 | 119 |

All four runs: every bot reached the game (so the accept/`Listed` path and login/select-stage sends work), 0 decode errors, 0 disconnects, 0 isolated errors, 0 crash files.

- **Connection-pass overhead outside handlers −74 % (50 → 13 ms/s), visits 270k/s → 8.5k/s, game-thread busy −30 to −40 %.** Together with FlushReadyOnly the two sweeps went from ~83 ms/s (32 + 50) to ~19 ms/s at 500 bots; both scaled with connections × iterations, so the live server (more connections) should gain more.
- What remains in `conn` besides handlers (~13 ms/s) is mostly the reply flushes `Process` does for handled packets (encode + hand-off), the 20 sweeps/s, `AdjustLights` and the status connections.

## Test note
`run-baseline.ps1 -ExtraIni` must get a real array: `powershell -File run-baseline.ps1 -ExtraIni '[Optimisation]','X=1'` passes **one** comma-joined string, so the override is silently not applied. Use `powershell -Command "& 'tools\run-baseline.ps1' -ExtraIni @('[Optimisation]','X=1') ..."` or call it from a PowerShell session, and check `Configs\Setup.ini` in the scratch run folder.
