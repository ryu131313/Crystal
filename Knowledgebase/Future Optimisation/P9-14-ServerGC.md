# P9-14 — Server GC for Server.exe and StressClient (2026-10-22)

**Status: implemented (project setting), measured on a scratch server with 3000 crowded bots. Not soaked yet.** No wire change, no DB change, no `Setup.ini` switch. This answers the roadmap's open P9 decision "Mob threads and GC mode" for GC mode; the mob-thread part stays open.

## Change
| Project | Setting | Was |
|---|---|---|
| `Server.MirForms/Server.csproj` (Server.exe) | `<ServerGarbageCollection>true</ServerGarbageCollection>` | workstation GC (the WinForms default), concurrent |
| `StressClient/StressClient.csproj` | `<ServerGarbageCollection>true</ServerGarbageCollection>` | `false`, concurrent |

Concurrent (background) GC stays on in both. On .NET 10, server GC uses DATAS (dynamic adaptation to application sizes) by default, which starts with one heap and grows the heap count with load, so idle memory stays close to workstation GC.

**Rollback without a rebuild:** set `"System.GC.Server": false` in `Server.runtimeconfig.json` next to `Server.exe`, or start it with `DOTNET_gcServer=0`.

## Why
A StressClient fleet of 10k bots "got to 8k fast, then dripped in a few bots per second". On a scratch server with every bot on one spawn:
- **The server:** the game thread was 100% busy with 0.6-1.3 s iterations. Logins need about 6 round trips through that loop, and the bots' connect budget (25) holds a slot for the whole login, so joins fell to about 6/s.
- **The cause:** crowd broadcast. Every walk, run or attack goes to every player within `DataRange` (16). Deliveries/s = actions/s × viewers, so they grow with N² when everyone sees everyone.
- **The GC feedback loop:**
  - With ~1 s iterations, queued packets survive gen0, so every collection was at least gen1, with a gen2 about once a second. Workstation GC runs all of it on the game thread's core: **150-280 ms/s of GC pause** (`gcpause`).
  - The pauses lengthen the iteration, and longer iterations make more packets survive.
- **The bots:** the same loop on the client side. StressClient spent 2.66 s of every 5 s in GC pauses (5.6 GB gen2, 3.4 GB of it fragmented), and its outbound traffic collapsed to ~200 packets/s during them.

## Measurements
- **Setup:** scratch server (`start-scratch-server.ps1 -Maps 1 -MaxUser 12000`, synthetic 200×200 map, Multithreaded on, `[Diag] LogLoopProfile=True`).
- **Bots:** `StressClient --bots 3000 --ramp 100 --connect-budget 100 --duration 150 --map-folder …`. Every bot spawns in one safe zone and hunts within 20 cells, so all 3000 are in each other's view. Bots ran with server GC in both runs.
- **The A/B:** the server-GC run was started with `DOTNET_gcServer=1`, the same binary. The table compares saturated windows (`pout` ≥ 100k, the last 40-55 seconds of each run).

| Per second (median) | Workstation GC | Server GC |
|---|---|---|
| Game-thread GC pause (`gcpause`) | **237 ms** | **1.7 ms** |
| Worst iteration (`max`) | 1008 ms | 418 ms |
| Iterations (`it`) | 1 | 3 |
| Broadcast recipients (`recv`) | 1.99M | 2.58M |
| Handler + encode cost per recipient ((`chand`+`senc`)/`recv`) | 0.61 µs | 0.40 µs |
| Recipients per busy second | 2.15M | 2.57M |
| All 3000 bots in game after | ~115 s | 61 s |
| Allocation (`alloc`) | 85 MB/s | 98 MB/s |

Both runs: 0 decode errors, no isolated errors, every bot reached the game.

**StressClient** (10k bots, `--connect-budget 25`):
- With workstation GC, outbound traffic collapsed to 217-500 packets/s for whole seconds, and up to 810 in-game bots at a time had gone 5 s without a completed write.
- With server GC, it held at 1.8-2.7k packets/s through the run.
- The join rate did not change, because the server was then the only limit.

## Reading
- **The GC mode is a clear win under load.** Pause time on the game thread almost disappears, and each broadcast recipient costs a third less, because the loop no longer stalls while one core collects a large heap.
- **It does not change the crowd scaling.**
  - One game thread delivers about 2.5M recipient packets/s. Demand is actions/s × viewers, so about 1,500 mutually visible players at ~1 action/s each saturate it.
  - A packed real town (around 400 visible players) needs about 160k/s, roughly 6% of one thread.
- **Not measured:** a quiet machine. These runs shared the desktop with the bots, so CPU numbers are only comparable between these two runs.

## Memory (quick soak, 2026-10-22)
- **Setup:** two scratch servers side by side, server GC (`DOTNET_gcServer=1`) and workstation GC (`DOTNET_gcServer=0`), same binary.
  - Each had 4 synthetic maps and 300 bots at ramp 25/s for 12 minutes.
  - Server process sampled every 30 s.
  - Script: the session scratchpad's `quick-gc-soak.ps1`. It is not in `tools\`; `run-soak.ps1` covers the long run.

| Server process | Server GC | Workstation GC |
|---|---|---|
| Private MB (0.5 / 3 / 12 min) | 88.5 / 97.4 / 102.1 | 67.0 / 56.7 / 64.8 |
| Working set MB (0.5 / 12 min) | 140.4 / 154.2 | 127.2 / 124.9 |
| Private growth, 3 → 12 min | +4.7 MB | +8.1 MB |
| Threads (end) | 49 | 27 |
| Handles (start → end) | 821 → 896 | 751 → 824 |

- **Cost:** server GC costs about 35-40 MB more private memory and about 30 MB more working set at this load, plus about 20 GC threads.
- **No leak signal:** both modes grow by the same few MB over 9 minutes (the bots gather items and quests). Handles grow alike in both.
- **Health:** both runs passed, with every bot in game, 0 bot errors, 0 isolated errors, 0 FATAL lines and no crash files.
- **Not done:** the 60-minute `run-soak.ps1` (a longer slope and the loop profile over time).

## Remaining crowd options (not done)
1. **A cheaper per-recipient path:**
   - encode once inside `Broadcast` instead of the per-recipient `EncodedThisPass` lookup;
   - use a game-thread-only list instead of the `ConcurrentQueue` per connection for game-thread enqueues (mob threads would keep a concurrent path, and the order across phases must hold).

   Estimated 30-50% more recipients/s; still N².
2. **A per-viewer visibility cap** (nearest K players): the only change that removes the N² term. Visibility is range-based today, with no per-viewer visible sets, so it needs explicit sets, `AddObjects`/`RemoveObjects` rework and a gameplay decision about who drops out of view.
3. **`MoveBatch=True`** (P9-08): about 38% fewer bytes per recipient (socket and sender work). The per-recipient game-thread cost stays the same. It needs patched clients first (Runbook §11).
4. **Bot scenario:** spread fleets over more maps or start points (`-Maps N`) when the goal is server capacity rather than worst-case crowding.

## Profiling note
A CPU sample of the game thread (EventPipe `Microsoft-DotNETCore-SampleProfiler`, 20 s, workstation GC) put most of the time in:
- `MirConnection.ProcessPacket` → `Broadcast` → `Enqueue` (≈ 43%);
- `Flush` (≈ 32%), including the `EncodedThisPass` lookup (≈ 20%);
- `PollGC` (≈ 15%).

EventPipe samples only at safe points, so frames that cross into native code are over-counted (`RuntimeHelpers.GetHashCodeWorker`, `Stopwatch.GetTimestamp`, `PollGC`). Use the leaf percentages as hints, and the `[Diag] Loop` counters for numbers. log4net file appends on the game thread were about 4% of samples (login/spawn info lines; `FileAppender` with `MinimalLock` reopens the file per write).
