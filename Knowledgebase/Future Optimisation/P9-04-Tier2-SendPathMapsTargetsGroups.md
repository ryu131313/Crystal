# P9-04 — Tier 2: send path, map respawn gating, target search, group locations, partner lookups (2026-10-11)

**Status: written, compiled, tests green, two independent read-only reviews (threading/network; gameplay equivalence) — no Critical/High/Medium findings. NOT measured yet.**

Server-only; no `Shared`, DB or wire-format change. One new setting: `[Optimisation] MapRespawnCheckMs`.

## Changes

| # | Where | Before | After | Notes |
|---|---|---|---|---|
| 1 | `MirConnection.Process` send | `new List<byte>()` per flush, `AddRange` per packet (growth copies), `ToArray()` | Encode into a static game-thread scratch `List<byte[]>`, then one exact-size `byte[]` + `BlockCopy` | Same bytes, order, empty/null handling and profiler counts. `finally` clears the scratch so an isolated encode failure can't leak bytes into the next connection. `_sendList.Count` → `IsEmpty`. The array goes to the socket, so it isn't pooled. `SendDisconnect`/`ClientVersion` reject use `ToBytes` directly. |
| 2 | `Map.Process` | `ProcessRespawns()` + door scan for every map on every loop iteration (~700/s) | At most every `[Optimisation] MapRespawnCheckMs` (default 100, 0-1000, 0 = old) | `ActionList` and lightning/fire are unchanged. Tick respawns compare `<`, so no tick is missed; LiveDb-added respawns (`RespawnTime = 0`) spawn within 100 ms; doors close up to 100 ms later. |
| 3 | `MonsterObject.FindTarget` | Full `(2·ViewRange+1)²` scan even after a target was found (and a full no-op scan on the 1-in-3 re-search when it already had one) | Stops at the first target when no StoneTrap is alive anywhere | `StoneTrap.AnyAlive`: `Interlocked` count in `Spawned`/`Despawn`, `Volatile.Read` from mob threads, reset in `StartEnvir` (Main). Only a StoneTrap can replace a found target, so the result is identical at 0; a count left high only disables the shortcut. |
| 4 | Walk/Run/Pushed → `SendLocationToGroup` | `GetPlayerLocation()`: 2N+1 new `S.SendMemberLocation` per step (own location to each member + every member's to self + self twice) | One shared packet: own location to each member on a BigMap map, and to self | Full two-way exchange stays on group join, revive, teleport and map change. Encode-once encodes the shared packet once per pass. |
| 5 | Partner lookups (`ProcessBuffs`, `LevelMagic`, `MonsterObject.Attacked`, `PlayerObject.GainExp`) | `GetCharacterInfo` (linear over every character ever created) + `GetPlayer(name)` | `GetPlayer((uint)index)` (online players only) | Names are unique case-insensitively, so it is the same player; it also no longer throws when the partner's `CharacterInfo` is gone. `LevelMagic` gained a `player != null` check on the rewritten line (latent NRE, unreachable today per review). |

## Behaviour changes (accepted)
- Doors close 5 s + up to `MapRespawnCheckMs` after opening.
- A player standing on a map without a big map no longer pulls other members' big-map locations on each step; they refresh on the next map change/teleport/revive. Only visible if the big map dialog was already open before entering such a map and is browsed to another map (cosmetic; review finding Low).

## Considered and skipped
- **Separate game-thread object list** (the `obj` walk still visits every mob-thread monster node to skip it): `Master` is a field assigned in 36 places, some in monster AI on mob threads, so keeping a separate list in sync is a threading-model change. Needs its own design + two reviews: candidate P9-05.
- **Map exits lookup** (`PlayerObject.CheckMovement`): a few dozen `Point` compares per step; a lookup table would need invalidation for LiveDb movement edits and script-added `ActiveCoords`. Not worth it.
- Review Lows not applied: spreading each map's respawn-check phase (very low value); `Array.Empty<byte>()` for an all-null flush.

## Noticed, not fixed (Known defect candidate)
- `StoneTrap.ProcessAI` calls `MonsterObject.GetMonster(mInfo)` for every nearby monster on every AI tick, creating (and discarding) a full new `MonsterObject` per monster and setting *its* `Target` — so the lure never retargets the real monsters, and it allocates heavily while a trap is up.

## To measure
- `run-baseline.ps1 -Bots 500 -NoIdle` vs `-ExtraIni '[Optimisation]','MapRespawnCheckMs=0'` for #2 on a copy of real data (the synthetic world has one map, so `maps` ms/s is already ~0.4 there).
- #1: `alloc MB/s` and `ms/s conn` against the parent commit.
- #3: `ms/s mob` against the parent commit (bots get targeted constantly).
- #4 needs grouped bots (StressClient has no groups yet).
