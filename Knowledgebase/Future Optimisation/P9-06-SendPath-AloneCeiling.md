# P9-06 — Send path and alone ceiling (2026-10-21)

**Status: implemented, one read-only review (no Critical or High findings; all Medium and Low findings applied or noted below), measured on scratch servers with bots.** This is phase 1 of `P9-Plan-Events-and-Interest.md`. Server-only: no `Shared`, wire or DB change. Every change has its own `[Optimisation]` switch, which is live and shown on the web admin settings page.

## Changes
| Switch (default) | What it does |
|---|---|
| `FlushAtEnd` (True) | In-game connections send in a new pass at the end of the loop iteration (`Envir.FlushEndOfTick`, profiler phase `Send`), after the mob pass, objects and maps. Packets the world made this iteration leave this iteration instead of at the next iteration's connection pass, about 1-2 ms sooner. Replies to packets a client sent in this pass still go out inside its `Process`, as before (review M1), so no reply waits for the world phase. Connections in any stage other than `Game` flush as before. |
| `SendFlushMs` (0, range 0-5) | With `FlushAtEnd`, above 0 every in-game connection sends on one shared tick every N ms. KeepAlive replies and more than 256 pending packets skip the wait. 0 = send every iteration. |
| `SendCoalesce` (True) | When a newer copy of `ObjectHealth`, `ObjectColourChanged`, `ObjectPoisoned` or `ObjectName` for the same object arrives while an older one is still pending, the older one is nulled in place. Each is a plain overwrite in the desktop client (`GameScene` 3371/3507/4429/4596) and in the web client (`game.ts` 1074/1192/1198/1323). |
| `AloneCeiling` (True) | `MonsterObject.SetOperateTime`: a monster that is alone (no master, no target, not dead) sleeps until its `AloneTime` instead of waking at the +2 s ceiling. Types that override `Process`, `ProcessAI` or `CheckAlone` keep 2 s; this is found by reflection once per type. Behaviour is identical, because an alone monster's `Process` does nothing until `CheckAlone` runs. |

Other changes:
- **Disconnect ordering:** before `S.Disconnect` (`SendDisconnect`, on the game thread) and before the wrong-version reply, the pending queue is flushed. A kick or ban message therefore arrives before the disconnect; before this change it went out after it. The pre-flush skips the encode-once cache (review L3). A WinForms caller off the game thread keeps the old order (Known debt).
- **Pending lists:** a connection's pending list and coalescing table give back their capacity after a burst (review L4).
- **Counters**, on the dashboard and appended to the `[Diag] Loop` line:
  - `send=` (ms/s of the send pass) and `sends=` (socket sends/s);
  - `bcast`/`scan`/`recv` (broadcast calls, players checked, players enqueued to);
  - `ehit`/`emiss` (encode-cache hits and real encodes);
  - `coal=` (dropped state packets) and `alone=` (alone AI runs).
  - Per-thread counters are summed while the mob threads are parked.
- **Scripts:** `run-baseline.ps1` adds a send-path table and `-Maps`/`-MapsFrom`/`-BotArgs`. The "town" scenario is `-Maps 1 -MapsFrom <server Maps> -BotArgs '--no-director --roam-radius 30'`: 300 bots on `0.map` around its start point. `run-soak.ps1` reports conn + send.
- **Not added (review M2):** duplicates by packet id and wakes by object type. The coalesced total and the alone-run counter answered this phase's questions.

## Measurements
- **Setup:** synthetic world, `run-baseline.ps1`, 60 s windows. Bots share the desktop with the server.
- **Switches:** "off" = `FlushAtEnd`, `SendCoalesce` and `AloneCeiling` all False.
- **Noise:** large. Packets out per second varied 2× between runs of the same scenario, because bot behaviour is random.

| Run | conn + send ms/s | sends/s | pkts out/s | busy | max iter ms |
|---|---|---|---|---|---|
| 500 bots off (3 runs) | 179 / 186 / 269 | 16.8k / 18.2k / 13.8k | 148k / 125k / 66k | 21 / 22 / 32 % | 55 / 92 / 137 |
| 500 bots on (3 runs) | 270 / 259 / 229 | 13.8k / 13.4k / 15.4k | 108k / 49k / 112k | 31 / 31 / 27 % | 82 / 169 / 116 |
| 500 bots on, `SendFlushMs=5` | 140 | 12.2k | 94k | 18 % | 44 |
| town 300 bots off / on | 219 / 318 | 24.3k / 20.9k | 53k / 50k | 24 / 34 % | 31 / 91 |
| idle off / on, alone AI runs/s | 93 / 47 | | | 0.5 / 0.35 % | |

All runs had 0 decode errors, 0 isolated errors and 0 crash files, and every bot reached the game.

## Reading
- **`AloneCeiling`: proven.** Alone-monster AI runs are halved (93 → 47/s), with identical behaviour.
- **`FlushAtEnd`: no CPU win shown.**
  - Per send it cost 15-20 µs, against 10-20 µs off.
  - The town pair was worse (219 → 318 ms/s), but that is a single pair.
  - Within this noise it is not proven to cost either. A real cost would be a few tens of ms/s at 500 bots, inside the budget.
  - It stays on for the delay gain the user asked for. That gain follows from the iteration order and isn't measurable with these tools.
  - **Watch it in the 2 h soak.** If a quiet-machine pair shows a real cost, set `FlushAtEnd=False` live.
- **`SendFlushMs=5`:** the cheapest run (140 ms/s, busy 18 %), but a single run, and it adds up to 5 ms of delay. It stays at 0, the user's preference for lower delay.
- **`SendCoalesce`:** dropped only 5-23 packets/s out of ~100k, which is negligible with bots. Bots don't create bursts of health or colour changes. It is kept on, because it's harmless and real fights (many hits on one boss) produce more duplicates.
- **Broadcasts:** players scanned per recipient was 1.1-2.2 here, including the town scenario. The bots crowd into one view, so phase 2's grid will show its gain only with players spread over a large map.

## Smoke check (real client, scratch copy)
- Log in, walk and fight in a busy spot: the ping should look normal.
- Kick yourself from the web admin: the message should show before the disconnect.
- Switch `FlushAtEnd` and `SendFlushMs` live.
