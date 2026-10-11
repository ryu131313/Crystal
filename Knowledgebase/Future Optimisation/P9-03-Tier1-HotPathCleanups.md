# P9-03 — Tier 1 hot-path clean-ups (2026-10-11)

**Status: written, compiled, tests green. NOT measured yet** — the P9 rule wants before/after numbers; see "To measure" below.

Server-only; no `Shared`, DB or wire change; no new settings.

## Changes

| # | Where | Before | After | How often it ran |
|---|---|---|---|---|
| 1 | `HumanObject.ProcessBuffs` | Newbie-guild members re-added the (Infinite) `Newbie` buff every process: `S.AddBuff` to self (+ broadcast if visible) and a full `RefreshStats()` | Added only when missing (`newbie` flag, like `gm`/`lover`) | ~100/s per newbie-guild player (750/s before P9-02); on by default |
| 2 | `HumanObject.ProcessBuffs` | While a mentor/spouse was offline: linear `GetCharacterInfo` over all characters + `GetPlayer` name scan, every process | Retried at most once a second (`PartnerBuffCheckTime`) | ~100/s per married/mentored player with partner offline |
| 3 | `PlayerObject.Broadcast` | `Array.Exists` closure per broadcast, then `foreach` over observers | Returns early when there are no observers; for-loops | every player broadcast |
| 4 | `GetInfo` of Monster/Hero/Player | `Buffs.Where().Select().ToList()` | `MapObject.GetVisibleBuffTypes()` loop (pre-sized list; the packet keeps it) | per object per viewer |
| 5 | `MapObject.HasAnyBuffs` | `Select().Except(new List).Intersect().Any()` | nested loop | buff expiry/removal of hide buffs |
| 6 | `HumanObject.Process` | `Pets.Count()` (LINQ) | `Pets.Count` | per player per process |
| 7 | `PlayerObject.WinExp` | `float[] partyExpRate` allocated per call | `static readonly` | per exp gain |
| 8 | Global drop notice (`MonsterObject.DropItem`, `HumanObject`/`PlayerObject` death drops) | localise + format once **per online player** | once per drop | per notify-flagged drop |
| 9 | `SepHighArcher` | `Buffs.Where().ToList().Count()` per attack | `HasBuff(BuffType.PoisonShot)` | per attack (mob thread) |

## Behaviour changes
- A mentor/spouse buff can appear up to 1 s after the partner logs in (was ~10 ms).
- Newbie-guild members no longer receive an `S.AddBuff` every tick. The buff is Infinite with no stat reset, so the re-add never changed its stats; leaving the guild still removes it (`PlayerObject` ~L3281, `NPCSegment`).

## Considered and skipped
- `MonsterObject.Process` → `RefreshNameColour()`: no allocation, broadcasts only on change; its colour depends on timers, so making it event-driven is not a quick win.
- `NPCObject` `Class.ToString()`: .NET 8+ returns the cached name for defined enum values (no allocation), and `ClassRequired` is live-editable.
- `[@_DIE(n)]` `string.Format` per death: one small string per death; caching needs a field on `MonsterInfo`.
- Mentor lookups per hit (`MonsterObject.Attacked`) and per exp gain: need an online-player index (§3.1.1) — Tier 2. Adjacent Known defect (mentor bonus checks the monster's `GroupMembers`) left as is.

## To measure
- The bots are not in a guild or married, so #1/#2 do not show in `run-baseline.ps1`. Use a scratch world whose bots join `NewbieGuild` (or set `NewbieGuild` to a guild the bots get), compare `ms/s obj`, `alloc MB/s` and `pkts out/s` against the parent commit.
- #3-#9 are small; expect them inside noise on the existing baseline.
