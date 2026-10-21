# P9-09 — Socket sends off the game thread (2026-10-21)

**Status: implemented, measured on scratch servers with bots, one read-only threading/network review (2 High lost-wakeup races, 1 Medium, 3 Low; Highs, the Medium and L1 fixed, L2/L3 noted below).** No wire change, no DB change.

## Why
P9-06 moved in-game sends to an end-of-iteration pass (`FlushEndOfTick`, profiler phase `Send`). At 2k bots the dashboard showed Send at 70-80 % of the game thread. The profiler now splits each flush into encode + copy (`senc`) and the socket call (`scall`). The first run (500 bots, synthetic map) settled it:

| | ms/s |
|---|---|
| Send phase | 280 |
| encode + copy (all flushes) | 16 |
| `Socket.BeginSend` (all flushes) | **241** (14.7k calls/s ≈ 16 µs each) |

So the cost is the syscall per connection per iteration, not encoding.

## Change
| Switch (default) | What it does |
|---|---|
| `[Optimisation] SendThreads` (**1**, range 0-4, live; web admin catalog "Sender threads") | **Game thread:** unchanged up to the finished `byte[]` (encode-once cache stays game-thread only). `MirConnection.BeginSend` then calls `QueueSend`: per-connection `ConcurrentQueue<byte[]>` outbox + Interlocked counters; the first array of an idle connection puts it on its sender's ready queue (`SessionID % SendThreads`).<br>**Sender thread** (`SocketSender`, "Mir2 sender N", background): drains the ready queue; `PumpSends` issues `Socket.SendAsync` with a reused `SocketAsyncEventArgs` (`Buffer` for one array, `BufferList` for up to 64 that queued up meanwhile). One send in flight per connection; the IO completion continues the pump. Spins up to 50 µs for more work before sleeping, so a flush pass costs about one wake signal, not one per connection.<br>**Order:** one FIFO and one owner per connection (`_sendState` handed off with full fences). `SendDisconnect` and the `ClientVersion` reject flush and then send through the same path, so `S.Disconnect` stays last. After switching to 0, a connection with arrays still queued or in flight keeps using the queue until they are gone, so a direct send never overtakes.<br>**Teardown:** `Disconnect` with queued sends hands the socket to `SocketSender.CloseWhenDrained`: the sender disposes it once they finish, at most 2 s later.<br>**Bounds and faults:** > 4 MB queued for one client, or a failed/short send, sets `_sendFaulted`; nothing more is queued and the game thread sets `Disconnecting` in `Process`. Ready/closing queues hold each connection at most once.<br>**0 = old path:** `Socket.BeginSend` on the calling thread. |

Profiler (`[Diag] Loop`, appended): `senc`, `scall` (game thread, ms/s), `ssend` (sender socket sends/s), `sarr` (flush arrays they carried/s), `sbusy` (sender thread busy ms/s, without completions on IO threads). `tools/run-baseline.ps1` reports them and takes `-Scratch` so it can run beside another scratch server.

## Measurements
`run-baseline.ps1`, 500 bots, 45 s windows, Multithreaded on, bots and server on the same desktop (the live server and its own bot fleet were also running). Off = `SendThreads=0`, on = `SendThreads=1`. Town = `--no-director --roam-radius 30` (crowd at the start point); spread = 4 synthetic maps.

| Run | busy | it/s | Send phase ms/s | conn + send ms/s | encode + copy | send call | sender sends/s (arrays/s) | max iter ms | CPU % of 1 core |
|---|---|---|---|---|---|---|---|---|---|
| default off | 31.7 % | 656 | 244.3 | 305.0 | 15.1 | 214.8 | — | 158 | 29.8 |
| default **on** | **10.3 %** | 862 | **35.0** | **90.7** | 10.1 | **3.8** | 12.1k (16.9k) | 129 | 24.2 |
| town off | 29.1 % | 729 | 222.6 | 281.5 | 11.7 | 197.0 | — | 179 | 27.8 |
| town **on** | **10.6 %** | 1019 | **37.9** | **93.7** | 10.2 | **5.5** | 18.9k (24.6k) | 135 | 31.9 |
| spread off | 18.7 % | 810 | 129.0 | 176.1 | 5.1 | 112.9 | — | 64 | 14.1 |
| spread **on** | **8.3 %** | 899 | **28.1** | **69.3** | 3.8 | **3.3** | 9.8k (10.8k) | 55 | 15.3 |

All six runs: every bot reached the game, 0 decode errors, 0 unknown packets, 0 disconnects, 0 isolated errors, 0 crash files, no sender faults in the server log.

## Reading
- **Send phase −78 to −86 %, game-thread busy −55 to −67 %** at the same load. The socket call on the game thread fell from 113-215 ms/s to 3-6 ms/s (queueing only). Iterations/s rose because each one is shorter; the worst iteration also dropped (158 → 129, 179 → 135, 64 → 55 ms).
- **What remains in Send** (28-38 ms/s) is the per-connection flush work: dequeue, coalesce, MoveBatch, encode + copy (4-10 ms/s), and the queue hand-off.
- **Latency:** a sender thread picks an array up within microseconds (it spins 50 µs before sleeping; a wake from sleep is one kernel signal). No added delay against the ~5 ms ceiling.
- **Coalescing:** 1.1-1.4 flush arrays per socket send, so the sender also does fewer syscalls than the old path did.
- **Whole-process CPU** is about the same (the syscalls moved, they didn't disappear; the sender's spin adds a little). The goal was the game thread's budget, not total CPU.
- 2k bots were not run here (scratch `MaxUser` 600); the shape at 500 scales with connections × iterations.

## Known limits (review Lows)
- **L2, shutdown:** sender threads are background threads. `StopNetwork` gives every connection ~500 ms before `Disconnect`, which drains the outboxes in practice; a process exit right after the WorkLoop crash handler could still drop a last `S.Disconnect` that the old path would already have handed to the kernel.
- **L3, stuck owner:** if an exception escapes a pump (caught and logged), that connection keeps `_sendState` = 1; `_sendFaulted` disconnects it and the close deadline disposes the socket after 2 s. Self-healing.
- **Not exercised in these runs:** a live 1 → 0 switch under load (the ordering argument is in the review), and a real slow client hitting the 4 MB cap.
- `SocketAsyncEventArgs` per connection is left to the GC (disposing it while a send may still be pending is unsafe); sent arrays are released after each send.
