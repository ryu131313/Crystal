# P9-08 — Event-driven player cadence, folded timers, movement batching (2026-10-21)

**Status: implemented, two read-only reviews (server safety; packet contract and clients), with no Critical or High findings. Their Mediums are fixed, and their Lows are fixed or noted below. Measured on scratch servers with bots.** This is phase 3 of `P9-Plan-Events-and-Interest.md`.
- **Wire change:** `S.ObjectMoveBatch`, id 301. It is off by default; Runbook §11 lists what to do before turning it on.
- **No DB change.**

## Changes
| Switch (default) | What it does |
|---|---|
| `HumanIdleCapMs` (250, range 0-1000, 0 = off) | **Which players:** players only. Heroes keep `HumanProcessMs`, because their AI runs inside `Process`. Players with urgent state also keep it: sneaking, reincarnation, stacking, a pending poison-status push, new mail or released parcels, a RequiredGroup map.<br>**When Process runs:** at the earliest timer it acts on, at the latest every 250 ms. `HumanObject.NextTimerDue` and the `PlayerObject` override cover regen, potion/heal/vampire ticks (only while an amount is pending, review M1), item expiry, torch, durability, flaming sword, counter attack, the run and step counters (`CellTime + 701`), mount loyalty, PK points, last hitter, EXP owner, brown name, the explosion debuff, poison ticks, buff ticks, friend-request expiry, group-status and friend-level pushes, rested bonus, fishing and the creature ticker. The ActionList is still folded.<br>**Wakes (`OperateTime = 0`):** after the player's connection handled any packet; `ApplyPoison`, `AddBuff`; Healing Circle; an NPC appearing on top of the player (stacking); Reincarnation; new mail. |
| (none) | `NPCObject.SetOperateTime` folds `VisTime`, `SpeechTime` (only while the NPC has speech) and `UsedGoodsTime`. `SpellObject` folds `StartTime`. `IntelligentCreatureObject.SetOperateTime` now assigns `CreatureTime`; it used to assign `RoamTime` (Known defect, fixed with the user's approval). |
| `MoveBatch` (**False**) | **What:** at flush, a run of two or more consecutive `ObjectTurn`/`ObjectWalk`/`ObjectRun` for one client becomes one `S.ObjectMoveBatch`, which takes the run's place, so no packet is reordered.<br>**Format:** `ushort count`, then per move `byte (Kind << 3 \| Direction)`, `uint ObjectID`, `ushort X`, `ushort Y`. That is 9 bytes per move instead of 17. A move with coordinates outside 0-65535 is sent as the single packet. At most 1000 moves per batch.<br>**Encoding:** `ObjectMoveBatch.ToFrame()` writes the frame straight into one array (`Shared.Tests` pins it byte-identical to `GetPacketBytes`). One reused game-thread scratch instance builds every batch, outside the encode cache.<br>**Receivers:** the desktop client (`GameScene.ObjectMoveBatch`), the web client (`server.ts` decoder, `game.ts` handler, which skips unknown kinds) and StressClient replay each move through the single packet's handler.<br>**Also done:** the codec fixture, `enums.generated.ts`, and the manifest line `S 301`. |

## Measurements
- **Setup:** `run-baseline.ps1`, 500 bots on the synthetic map and 300 spread over `0.map`, 60 s windows, bots on the same desktop.
- **"Off":** `HumanIdleCapMs=0`, `MoveBatch=False`.
- **Packet counts:** a batch counts as one packet in "pkts out", so bytes are compared per recipient instead.

| Run | obj ms/s | alloc MB/s | KB out/s | recipients/s | bytes per recipient | batched moves/s | conn + send ms/s | busy |
|---|---|---|---|---|---|---|---|---|
| 500 bots off | 35.2 | 14.5 | 2259 | 130k | 17.8 | 0 | 184 | 22 % |
| 500 bots, cap only | **8.7** | 12.2 | 2499 | 144k | 17.8 | 0 | 322 | 33 % |
| 500 bots, cap + batch (ToBytes, first version) | 9.6 | 21.9 | 1816 | 141k | 12.9 | 92k | 343 | 36 % |
| 500 bots, cap + batch (`ToFrame`) | **8.1** | **12.1** | 2178 | 197k | **11.0** | 164k | 344 | 35 % |
| spread 300 off / cap + batch (ToBytes) | 25.3 / **7.1** | 13.9 / 18.3 | 912 / 738 | 46k / 46k | 19.8 / 15.9 | 0 / 30k | 215 / 340 | 24 / 35 % |

All runs had 0 decode errors, 0 isolated errors and 0 crash files, and every bot reached the game.

## Reading
- **Player cadence: proven.** The objects slice dropped about 73 % in every pair (35 → 8-10, 25 → 7). That is the players' housekeeping no longer running every 10 ms.
- **Batching: proven.** Bytes per recipient fell 38 % with `ToFrame`.
  - The first version, which encoded through `GetPacketBytes`, added about 9 MB/s of allocation. `ToFrame` removed it: 12.1 MB/s, the same as without batching.
- **Send-phase time is not resolvable here.** Conn + send rose in every "on" run, including cap-only, where batching is off. The per-send cost (16-17 µs on, 9 µs off) is inside the 9-20 µs that P9-06 measured between identical runs.
  - It is socket-send time on a desktop shared with 500 bots, and the bots get faster when the server does.
  - Quote it only after the quiet-machine rerun. If it shows a real cost, `HumanIdleCapMs=0` and `MoveBatch=False` are live switches.
- **Worst iterations** were 140-200 ms in all 500-bot runs here, against 55-137 ms in P9-06's off runs. Watch them in the 2 h soak.

## Known limits (review Lows)
- **Late by up to the cap (250 ms), with no wake:**
  - the Concentration buff's interrupt time;
  - the partner-buff retry;
  - status flags set by other objects (group, poison status);
  - the poison owner's brown name;
  - NPC-script delayed actions added to a player outside that player's own packet.
  - None of these touches movement or attack checks: those are checked in the packet handlers, which wake the player.
- **Wake details:**
  - A resisted poison still wakes the player once.
  - A buff added inside the player's own `Process` leaves one stale scheduler entry.
  - Players on RequiredGroup maps stay at the 10 ms interval.
- **Not done:** the plan's view-entry batch (map change and teleport). Those packets are 40-100 bytes each, so batching would only save their 4-byte header, under 10 %, for more client code.
- **Old clients:** a client older than this build drops its whole receive buffer on packet 301, and the weak version check (client.md §3.5) won't stop it. So `MoveBatch` stays off until the patched desktop client and the published web client are out (Runbook §11).

## Smoke check (real client, scratch copy)
- Buffs, poison, regen and potions tick on time, while standing still and while moving.
- Fishing, mail arriving while idle, mounting, and a torch burning down.
- A hero fight.
- With `MoveBatch=True` on a patched client and the web client: a crowd walking and running past looks normal. Turning it off live changes nothing visible.
