# X1 — Server security and crash review (roadmap P1 background)

> **Status:** X1-01 to X1-10 (all Criticals) **fixed 2026-10-10** in the overnight run: guard clauses, each marked `// X1-NN` in source. Also fixed 2026-10-10: X1-11, 12 (retry cap 64), 13, 14, 16, 21, 22, 23, 25, 26 (door within 4 cells), 27 (server-side direction check), 28, 29. **Still open:** X1-15 (Shared length checks, C5), X1-17 (guild rank authority: needs a design check), X1-18/19 (connection/login throttling), X1-20 (legacy HTTP: retire or confine, your decision), X1-24 (mail half-commit refactor) and X1-30 (IP parsing/pruning).
> Originally review only. Written 2026-10-10 against branch `FuckingAround`, HEAD `36e422aa`.
> **Scope:** `Shared/ClientPackets.cs` `ReadPacket` lengths; `Server/MirNetwork/MirConnection.cs`; client-reachable `PlayerObject`/`HumanObject`/`NPCScript`/`GuildObject` paths; economy paths; the MirCraft web admin (`Server/WebAdmin/**`); the legacy `Server/Utils/HttpServer.cs`.
> **Triage targets:** **P5** = crash-the-world / isolation. **P8** = server security. **C5** = needs a `Shared/` or protocol change.
> **Line numbers** are from HEAD `36e422aa`. Other agents were editing during the review (commit `0d8b56d7` moved `Envir.cs` by about 90 lines), so search by method name if a line has moved.

## Threat model and the single-writer assumption

- `ProcessPacket` and every handler it reaches run on the **game thread** (`Envir.WorkLoop` → `Connections[i].Process()`, `Envir.cs` ~L2077–2083). The game thread is the only writer of world state. A "race" finding is real only if a second thread is involved: the IOCP socket thread (`ReceiveData`/`ReadPacket`), the mob threads (`ThreadLoop`), the web admin HTTP threads, the legacy `HttpServer` thread, or the WinForms UI thread. None of the findings below needs a race. Each one is a deterministic single-thread input bug.
- **Any exception in a handler stops the world** (server.md §3.3). The single try/catch sits around the whole `while (Running)`. It sends `SendDisconnect(3)` to everyone and runs the shutdown tail. So an exception that any logged-in client can trigger is a remote world-stop. Those findings are **Critical**.
- `ReadPacket` runs on the IOCP thread **before login** for every packet ID, because `Packet.ReceivePacket` decodes whatever ID arrives. A `ReadPacket` exception IP-blocks the sender for 24 h. It does not stop the world, but large allocations made there are a memory and GC attack on the whole process.

## Summary

| ID | Sev | Area | One-line | Triage |
|---|---|---|---|---|
| X1-01 | **Critical** (fixed) | Rankings | `C.GetRanking{RankType=6}` → `RankClass[5]` IndexOutOfRange → world stop (also reachable in Observer stage) | P5 |
| X1-02 | **Critical** (fixed) | Market | `C.MarketPage{Page=-1}` after `C.MarketSearch{MarketType=GameShop}` (no NPC needed), or `Page=214748365` → `Search[negative]` → world stop | P5 |
| X1-03 | **Critical** (fixed) | Guild territory | `C.GuildTerritoryPage{Page=306783379}` → `page*7` overflows → `GTMapList[negative]` → world stop | P5 |
| X1-04 | **Critical** (fixed) | Heroes | `C.ChangeHero{ListIndex=-1}` → `Info.Heroes[-1]` → world stop (despawns the hero first) | P5 |
| X1-05 | **Critical** (fixed) | Quests | `C.AcceptQuest{QuestIndex=<unknown>}` → `info.NpcIndex` on null → world stop | P5 |
| X1-06 | **Critical** (fixed) | World map | `C.RequestMapInfo{MapIndex=<unknown>}` → `CheckMapInfo(null)` → `mapInfo.Index` NRE → world stop | P5 |
| X1-07 | **Critical** (fixed) | Crafting | `C.CraftItem{UniqueID=<no recipe>}` while any NPC dialog is open → `recipe.Item` NRE → world stop | P5 |
| X1-08 | **Critical** (fixed) | Crafting | `C.CraftItem` with `Slots=[Inventory.Length]` → off-by-one `slot > Length` → IndexOutOfRange → world stop (needs a recipe NPC) | P5 |
| X1-09 | **Critical** (fixed) | Item rental | `C.DepositRentalItem` with no rental partner, then `C.ItemRentalLockItem` → `ItemRentalPartner.Enqueue` NRE → world stop | P5 |
| X1-10 | **Critical** (fixed) | Items (dupe) | `C.MergeItem` with `IDFrom == IDTo` on a stack of more than half its stack size → the stack becomes full. Infinite item duplication | P8 |
| X1-11 | High (fixed) | Game shop | Purchase counters are read by `Info.Index` and written by `GIndex`: stock limits never apply, and `+=` on a missing key can throw `KeyNotFoundException` (world stop, data-dependent) | P5/P8 |
| X1-12 | High (fixed) | Flood / DoS | Walk/Run/Turn/Attack/Magic packets that arrive "too early" go into `_retryList` with no cap and are re-run **every loop pass**. One logged-in client can queue ~80k per 5 s and stall the game thread | P8 |
| X1-13 | High (fixed) | Item rental (scam) | `ConfirmItemRental` proceeds when **either** side is locked (`&&` should be `||`), and a deposit change does not unlock the partner. The lender can bait-and-switch the item after the renter locks the fee | P8 |
| X1-14 | High (fixed) | NPC scripts | `NPCDelayed` stays `true` after a `TIMERECALL`/`TIMERECALLGROUP` with no page. The next `CallNPC` to **any** NPC in range skips button validation, so the player can call arbitrary script pages (Plausible: depends on scripts) | P8 |
| X1-15 | High (fixed 2026-10-10) | Pre-login decode | `C.ClientVersion` `ReadBytes(ReadInt32())`, `C.CraftItem` `new int[ReadInt32()]` and `C.ReportIssue` `ReadBytes(ReadInt32())` allocate an attacker-chosen size (up to 2 GB / 8 GB) on the IOCP thread before login | C5 |
| X1-16 | Medium (fixed) | Chat commands | `@SETTIMER` has no GM check. A player can create any personal timer, which makes script `CHECKTIMER` checks pass and grows `Envir.Timers` without bound | P8 |
| X1-17 | Medium (options: `X1-17-Guild-Rank-Options.md`) | Guild ranks | The `GuildObject.ChangeRank` authority comparison is inverted. An officer with `CanChangeRank` can move or demote **leaders** but cannot manage juniors | P8 |
| X1-18 | Medium (fixed) | Connections | Pre-login connections live forever on `KeepAlive`. 10 IPs × `MaxIP`=5 fill `MaxUser`=50, and then the accept loop parks an IOCP thread (`while (Count >= MaxUser) Sleep(1)`) | P8 |
| X1-19 | Medium (fixed) | Login | Anyone can lock any account for 2 min with 6 bad passwords, repeatable forever. Login attempts per connection are not rate-limited, which allows password spraying | P8 |
| X1-20 | Medium (path + echo fixed 2026-10-10; retiring the service is your call) | Legacy HTTP | `HttpServer` `/addnamelist?fileName=` does `Path.Combine(NameListPath, fileName)` with no confinement. A GET from a browser on the server box passes the trusted-IP check (opt-in service, off by default) | P8 |
| X1-21 | Low (fixed) | Login / select | `C.StartGame` does not check `CharacterInfo.Deleted`, so deleted characters can still be played until they are archived | P8 |
| X1-22 | Low (fixed) | Map spam | `C.DropGold{Amount=0}` is allowed, also while dead and on `NoThrowItem` maps. It spawns free 0-gold `ItemObject`s | P8 |
| X1-23 | Low (fixed) | Items | `RemoveSlotItem` clears the socket/mount slot **before** checking the target cell. If the cell is occupied, the item is destroyed | P5 |
| X1-24 | Low (fixed) | Mail | `SendMail` removes items from the inventory one by one and then `return`s on a later `NoMail`/`DontTrade` item. Earlier items and the stamp are lost | P5 |
| X1-25 | Low (fixed) | Items | A Strongbox `UseItem` consumes the box twice, and when the stack was 1 it can wipe the reward placed in the freed slot | P5 |
| X1-26 | Low (fixed) | Doors | `C.Opendoor` has no range check, so any door on the current map can be opened remotely | P8 |
| X1-27 | Low (fixed) | Wire values | `MirDirection` from the client is never range-checked (Turn/Walk/Magic `dir`). The server is safe, but invalid values are broadcast to nearby clients (Plausible client crash) | C5 |
| X1-28 | Low (fixed) | Inspect | `Envir.Inspect(uint)` uses `Players.SingleOrDefault`, which throws if two players match. Only possible with the known duplicate-ObjectID defect (Plausible) | P5 |
| X1-29 | Low (fixed) | Chat commands | `@CLEARBUFFS` has no GM check: a player can clear all of their own buffs (including script-applied penalties) | P8 |
| X1-30 | Low (fixed) | Network | `IPBlocks`/`ConnectionLogs` grow without bound. The IP is `RemoteEndPoint.ToString().Split(':')[0]`, so every IPv6 client shares the key `"["` (one IPv6 offender blocks all of IPv6) | P8 |

Counts: **Critical 10 · High 5 · Medium 5 · Low 10.**

---

## Findings

### X1-01 — Critical — `GetRanking` RankType 6 → world stop
- **Where:** `Server/MirNetwork/MirConnection.cs` ~L2182 `GetRanking` (allows `Game` **and** `Observer` stage) → `Server/MirEnvir/Envir.cs` ~L5412 `GetRanking`.
- **Input:** `C.GetRanking { RankType = 6, RankIndex = 0 }`.
- **Why real:** `if (RankType > 6) return; List<RankCharacterInfo> listings = RankType == 0 ? RankTop : RankClass[RankType - 1];` and `RankClass = new List<RankCharacterInfo>[5]` (`Envir.cs` L177). `RankType - 1 == 5` is out of range. `OnlineRankingCount` (`new int[6]`) would also be indexed at `[6]`.
- **Fix:** `if (RankType > 5) return;` (or `>= RankClass.Length + 1`). Bounds-check `OnlineRankingCount` as well.

### X1-02 — Critical — `MarketPage` negative or overflowing page → world stop
- **Where:** `PlayerObject.cs` L8341 `MarketPage`, L8373 `listings.Add(Search[i + page * 10])`. `MirConnection.MarketSearch` (~L1681) copies `p.MarketType` into `Player.MarketPanelType` without validation.
- **Input (no NPC needed):** `C.MarketSearch { MarketType = GameShop }`, wait `Globals.SearchDelay`, then `C.MarketPage { Page = -1 }`. With `GameShop`, `MarketPage` skips both the NPC check and the `page <= PageSent` check.
  **Input (at a market NPC):** `C.MarketPage { Page = 214748365 }`. `page * 10` wraps to a negative number, which passes `page > PageSent`.
- **Why real:** `if (i + page * 10 >= Search.Count) break;` is the only guard. A negative index is never `>= Count`, so `List<T>` indexing throws `ArgumentOutOfRangeException`.
- **Fix:** `if (page < 0 || page > (Search.Count - 1) / 10) return;` and compute in `long`. Validate `MarketType` against the NPC page (only allow `GameShop` from the game-shop UI path).

### X1-03 — Critical — `GetGuildTerritories` integer overflow → world stop
- **Where:** `PlayerObject.cs` L10452, loop at L10461.
- **Input:** `C.GuildTerritoryPage { Page = 306783379 }`.
- **Why real:** `if (page < 0) return; var max = Math.Min(Envir.GTMapList.Count, (page + 1) * 7); for (int i = page * 7; i < max; i++) tempList.Add(Envir.GTMapList[i]...)`. `page*7` = -2147483643 and `(page+1)*7` = -2147483636, so the loop runs with a negative `i` → `ArgumentOutOfRangeException`. This happens even when `GTMapList` is empty.
- **Fix:** `if (page < 0 || page > Envir.GTMapList.Count / 7) return;` (or use `long` arithmetic).

### X1-04 — Critical — `ChangeHero` negative index → world stop
- **Where:** `PlayerObject.cs` L9691–9703. `C.ChangeHero.ListIndex` is `int` (`ClientPackets.cs` L1279).
- **Input:** `C.ChangeHero { ListIndex = -1 }`.
- **Why real:** `if (Info.Heroes.Length <= index) return;` checks only the upper bound. `DespawnHero()` runs first, then `HeroInfo temp = Info.Heroes[index];` throws `IndexOutOfRangeException`.
- **Fix:** `if (index <= 0 || index >= Info.Heroes.Length) return;` (index 0 is the current hero, so swapping it with itself is a no-op). Do this before any despawn. Also consider requiring a hero NPC or a cooldown (design).

### X1-05 — Critical — `AcceptQuest` unknown quest → world stop
- **Where:** `PlayerObject.cs` L11265; NRE at L11277.
- **Input:** `C.AcceptQuest { QuestIndex = 999999 }`.
- **Why real:** `QuestInfo info = Envir.QuestInfoList.FirstOrDefault(d => d.Index == index);` is never null-checked, and the NPC loop then reads `info.NpcIndex`. The handler (`MirConnection` ~L1916) only checks the stage.
- **Fix:** `if (info == null) return;` right after the lookup.

### X1-06 — Critical — `RequestMapInfo` unknown index → world stop
- **Where:** `PlayerObject.cs` L7582 `RequestMapInfo` → L1010 `CheckMapInfo`; NRE at L1020 `Envir.GetMap(mapInfo.Index)`.
- **Input:** `C.RequestMapInfo { MapIndex = -5 }` (any index not in `MapInfoList`).
- **Why real:** `Envir.GetMapInfo` returns `FirstOrDefault` → null. `Connection.SentMapInfo.Contains(null)` is false, so execution reaches `mapInfo.Index`. The sibling handlers (`RequestItemInfo`/`RequestMonsterInfo`/`RequestNPCInfo`, L7552–7580) null-check. This one does not.
- **Fix:** `if (info == null) return;` in `RequestMapInfo`, plus `if (mapInfo == null) return;` at the top of `CheckMapInfo`.

### X1-07 — Critical — `CraftItem` with no matching recipe → world stop
- **Where:** `PlayerObject.cs` ~L7984 `CraftItem` (checks only `NPCPage != null` and NPC range, for **any** NPC) → `NPC/NPCScript.cs` L1242 `Craft`; NRE at L1255 `UserItem goods = recipe.Item;`.
- **Input:** Open any NPC dialog, then send `C.CraftItem { UniqueID = 0, Count = 1, Slots = [] }`.
- **Why real:** `recipe` stays null when no `CraftGoods[i].Item.UniqueID` matches. The `goods == null` check that follows comes too late.
- **Fix:** `if (recipe == null) { player.Enqueue(p); return; }`. Also require `NPCPage.Key == CraftKey` in `PlayerObject.CraftItem`.

### X1-08 — Critical — `Craft` slot off-by-one → world stop
- **Where:** `NPC/NPCScript.cs` L1282, L1320, L1383, L1408: `if (slot < 0 || slot > player.Info.Inventory.Length) continue;`.
- **Input:** At a crafting NPC with any recipe that has tools or ingredients: `C.CraftItem { UniqueID = <recipe item>, Count = 1, Slots = [<Inventory.Length>] }`.
- **Why real:** `slot == Length` passes the guard, and `player.Info.Inventory[slot]` throws.
- **Fix:** Use `>=` in all four places. Also reject duplicate slots up front.

### X1-09 — Critical — Rental lock without a partner → world stop
- **Where:** `PlayerObject.cs` L14126 `DepositRentalItem` (no `ItemRentalPartner` check) → L14329 `ItemRentalLockItem`, NRE at L14339 `ItemRentalPartner.Enqueue(...)`.
- **Input:** `C.DepositRentalItem { From = <slot with an item>, To = 0 }` with no rental session, then `C.ItemRentalLockItem`.
- **Why real:** Deposit succeeds whenever `ItemRentalDepositedItem == null`. `UpdateRentalItem` quietly returns when the partner is null, so the lock path dereferences the null partner. Side effect: the deposited item is stranded in `ItemRentalDepositedItem`, and `CancelItemRental` returns early when the partner is null.
- **Fix:** Require `ItemRentalPartner != null` in `DepositRentalItem`, `RetrieveRentalItem`, `ItemRentalLockFee`, `ItemRentalLockItem` and `SetItemRentalPeriodLength`. Return a stranded deposit on logout or cancel.

### X1-10 — Critical — `MergeItem` self-merge duplication
- **Where:** `PlayerObject.cs` L6487 `MergeItem`; arithmetic at L6685–6694.
- **Input:** Take a stackable item with stack size S and count c > S/2 (for example 51 of 100). Send `C.MergeItem { GridFrom = Inventory, GridTo = Inventory, IDFrom = X, IDTo = X }`.
- **Why real:** Nothing rejects `fromID == toID`, so `tempFrom` and `tempTo` are the same object. The else branch runs `tempFrom.Count -= (S - c)` (→ 2c-S) and then `tempTo.Count = S`, so the stack ends at S. To repeat: split off S-c and merge the remainder with itself again. With c ≤ S/2 the item is instead deleted (`arrayFrom[index] = null` after doubling). The same applies to Storage, HeroInventory and Fishing slots.
- **Fix:** `if (gridFrom == gridTo && fromID == toID) { Enqueue(p); return; }`, plus `ReferenceEquals(tempFrom, tempTo)`. Audit item logs for `ItemMerged` events with identical from/to indexes to detect past abuse.

### X1-11 — High — Game shop counter key mismatch (stock bypass; possible world stop)
- **Where:** `PlayerObject.cs` L13739 `GameshopBuy`. Reads: L13777/13781/13854/13865 `TryGetValue(Product.Info.Index, ...)`. Writes: L13857–13861 `Info.GSpurchases[Product.GIndex]`, L13868–13872 `Envir.GameshopLog[Product.GIndex]`. `GetGameShop` and `GameShopStock` also read by `Info.Index` (~L13724, L13940).
- **Input:** Repeated `C.GameshopBuy` for a limited-stock product.
- **Why real:** Purchases are stored under `GIndex` but counted under `Info.Index`, so per-player and server stock limits never apply (economy). Worse, if `GameshopLog[Info.Index]` is non-zero because some other product's `GIndex` equals this product's `Info.Index`, the code takes the `+=` branch on `GameshopLog[Product.GIndex]`, which may not exist → `KeyNotFoundException` → world stop. Whether this happens depends on the data, hence High rather than Critical.
- **Fix:** Use one key (`GIndex`) everywhere, with `dict[key] = dict.GetValueOrDefault(key) + q`. Also compute costs in `ulong` (`CreditPrice * Quantity`).

### X1-12 — High — Unbounded `_retryList` → game-thread DoS from one client
- **Where:** `MirConnection.cs` L1160/1169/1178 (Turn/Walk/Run), L1459/1468/1477 (Attack/RangeAttack/Harvest), L1578 (Magic). Re-queue at L264. The flood guard at L183 counts **receive calls** (≤ 8 KB each), not packets.
- **Input:** A logged-in client sends 8 KB TCP segments full of `C.Walk` (5 bytes each, ~1,600 per segment) 50 times per 5 s, i.e. ~80k packets per 5 s, all below `MaxPacket=50`.
- **Why real:** Each packet that arrives before `ActionTime` goes into `_retryList`. After the receive loop, `while (_retryList.Count > 0) _receiveList.Enqueue(_retryList.Dequeue());`, so the whole backlog is re-dispatched on **every** `Process()` pass (each ms the loop runs). Only one action is consumed per `ActionTime`, so the backlog never drains and game-thread CPU is O(backlog) per ms for every player. `TimeOutTime` is refreshed per processed packet, so the connection never times out.
- **Fix:** Cap `_receiveList` and `_retryList` (for example 64 pending movement/attack packets; drop the excess or disconnect). Count packets as well as reads in the flood guard. Keep only the newest movement packet.

### X1-13 — High — Item rental bait-and-switch
- **Where:** `PlayerObject.cs` L14363 `ConfirmItemRental`, guard at L14404: `if (!ItemRentalItemLocked && !ItemRentalPartner.ItemRentalFeeLocked) return;`. L14192 `RetrieveRentalItem` (no lock check). L14126 `DepositRentalItem` checks only `ItemRentalItemLocked`.
- **Input:** The lender deposits a valuable item but does not lock it. The renter sees it and locks the fee. In one burst, the lender sends `RetrieveRentalItem`, `DepositRentalItem` (a junk item) and `ConfirmItemRental`.
- **Why real:** The guard passes when *either* side is locked. Neither retrieve nor deposit calls `ItemRentalRemoveLocks()` (trade calls `TradeUnlock()` on every change), so the renter's fee lock survives the swap. The renter pays for the junk item.
- **Fix:** Require `ItemRentalItemLocked && ItemRentalPartner.ItemRentalFeeLocked`. Reject retrieve/deposit while locked, and call `ItemRentalRemoveLocks()` on any change of item or fee.

### X1-14 — High (Plausible) — Sticky `NPCDelayed` skips NPC button validation
- **Where:** `PlayerObject.cs` L4400–4420 `CompleteNPC` sets `NPCDelayed = true` and clears it only by calling a page (`if (page.Length > 0)`). `NPC/NPCSegment.cs` ~L3775–3803 (`TimeRecall`/`TimeRecallGroup`) queues the action with `tempString = ""` when the script gives no page. `NPC/NPCScript.cs` L831: `if (!player.NPCDelayed) { ...button / NPC-id validation... } else player.NPCDelayed = false;`.
- **Input:** Use any script that runs `TIMERECALL <secs>` with no page (or be in a group whose leader triggered `TIMERECALLGROUP`). After the recall, walk to any NPC and send `C.CallNPC { ObjectID = <npc>, Key = "[@ANY_PAGE]" }`.
- **Why real:** With the flag set, the NPC-id equality check and the button whitelist are both skipped. `PlayerObject.CallNPC` picks `ob.ScriptID` because `objectID != NPCObjectID`, so any page of that NPC's script runs. Pages that rely on checks made on a previous page (reward pages) become callable. Exploitability depends on the server's scripts.
- **Fix:** Clear `NPCDelayed` in `CompleteNPC` when `page.Length == 0`. Better, replace the flag with the expected `(npcId, pageKey)` pair and accept only that exact key.

### X1-15 — High — Attacker-sized allocations in pre-login `ReadPacket`
- **Where:** `Shared/ClientPackets.cs` L16 `VersionHash = reader.ReadBytes(reader.ReadInt32())` (`C.ClientVersion`); L929 `Slots = new int[reader.ReadInt32()]` (`C.CraftItem`); L2490 `Image = reader.ReadBytes(reader.ReadInt32())` (`C.ReportIssue`). Other count-driven loops are bounded by the stream because elements are read one at a time: L306 `C.Chat` `LinkedItems`, L1732 `C.EditGuildNotice` lines. `ReadString` is chunked and also bounded.
- **Input:** Before login, send a 12-byte packet with ID `ClientVersion` and length field `0x7FFFFFC0`, or ID `CraftItem` with count `0x1FFFFFFF`.
- **Why real:** `BinaryReader.ReadBytes(n)` allocates `new byte[n]` before reading (then trims), and `new int[n]` allocates n×4 bytes. The packet body is ≤ 65,531 bytes (`ushort` length), but the allocation is up to ~2 GB / 8 GB on the IOCP thread. Usually the result is an `OutOfMemoryException`, caught by `ReceiveData`'s catch (24 h IP block). But several connections from different IPs (`MaxIP` per IP) can each force multi-GB LOH allocations. An OOM, or GC pressure that lands on the **game thread**, stops the world.
- **Fixed 2026-10-10** under the approved server-only exception (roadmap §8): `Packet.ReadCount(reader, elementSize)` rejects a negative count or one that cannot fit in the bytes left, used at the three sites; test `Oversized_element_counts_are_rejected_before_allocation`. Wire format unchanged (manifest test green), so the server can ship it alone; clients pick it up with C5.
- **Original fix proposal:** In `ReadPacket`, reject a length that is negative or larger than `reader.BaseStream.Length - reader.BaseStream.Position` (or a fixed cap: hash ≤ 64 bytes, slots ≤ 64, image chunk ≤ 8 KB). This is a Shared change but not a format change. Optional cheap server-side guard: drop packets whose ID is not allowed for the current `Stage` before decoding (needs the stage on the IOCP side).

### X1-16 — Medium — `@SETTIMER` open to every player
- **Where:** `PlayerObject.cs` L4139 (`case "SETTIMER"`, no `IsGM`) → L14479 `SetTimer` → `Envir.Timers[Name + "-" + key]`. Consumer: `NPC/NPCSegment.cs` ~L2802 `CheckTimer`: `if (timer != null) { remainingTime = ...; break; }`. The `break` skips `failed = ...`, so **any existing timer makes the check pass**.
- **Input:** Chat `@SETTIMER DailyBoss 0 0` (any key a script tests with `CHECKTIMER`).
- **Why real:** A player can create or overwrite their own timers and so pass script timer checks. Each new key adds an `Envir.Timers` entry, which grows without bound (chat rate-limited only by `ChatTime`).
- **Fix:** Add `if (!IsGM) return;`. Separately, fix the `CheckTimer` `break`, which is a script-logic bug.

### X1-17 — Medium — Inverted authority check in `GuildObject.ChangeRank`
- **Where:** `Server/MirObjects/GuildObject.cs` L264: `if ((MemberRank.Index >= self.MyGuildRank.Index) && (self.MyGuildRank.Index != 0)) return false;`.
- **Input:** An officer (rank index ≥ 1) with `CanChangeRank` sends `C.EditGuildMember { ChangeType = 2, Name = <a leader>, RankIndex = <any> }`.
- **Why real:** The check refuses members of equal or *lower* authority (higher index) and allows changes to *higher*-authority members (lower index). So officers can demote or move leaders (subject only to the "at least 3 leaders / one other online" rule) but cannot manage juniors. `rankIndex` is also not limited to ranks below the caller.
- **Fix:** `if (self.MyGuildRank.Index != 0 && (MemberRank.Index <= self.MyGuildRank.Index || rankIndex <= self.MyGuildRank.Index)) return false;`. Also review `ChangeRankOption`'s `^=` (toggles instead of clearing).

### X1-18 — Medium — Pre-auth connection slot exhaustion
- **Where:** `MirConnection.cs` L257 (`TimeOutTime` refreshed per processed packet, any stage) and L873 `ClientKeepAlive` (accepted in `GameStage.None`). `Envir.cs` L3768 `Connection`: per-IP `MaxIP` (default 5); `finally { while (Connections.Count >= Settings.MaxUser) Thread.Sleep(1); ... }` at L3827 (`MaxUser` default 50).
- **Input:** From 10 IPs, open 5 connections each (one per 5 s per IP, because of the per-connect `IPBlockSeconds`) and send `C.KeepAlive` every few seconds without logging in.
- **Why real:** Nothing limits how long a connection may stay pre-login. Once 50 slots are full, the accept callback parks an IOCP thread in a sleep loop and no real player can connect.
- **Fix:** Add a login deadline (for example 30 s in `None`/`Login` stage, not refreshed by `KeepAlive`). Use a separate pre-auth cap that is lower than `MaxUser`. Do not block an IOCP thread when full; accept and close instead.

### X1-19 — Medium — Login lockout DoS and no per-connection attempt limit
- **Where:** `Envir.cs` `Login` ~L4100: `if (account.WrongPasswordCount++ >= 5) { account.Banned = true; ... ExpiryDate = Now.AddMinutes(2); }`.
- **Input:** Repeated `C.Login { AccountID = victim, Password = "wrong..." }`. Many logins fit in one 8 KB read, so the read-based flood guard barely applies.
- **Why real:** Anyone who knows an account name can keep it locked out indefinitely. The lockout sets `Banned = true` with `TooManyWrongLoginAttempts` on the account itself. Password spraying across many accounts from one connection is not throttled. Each attempt runs PBKDF2 on the game thread; it is cheap at 50 iterations but still unbounded.
- **Fix:** Throttle per connection and per IP (for example 5 attempts per minute → disconnect plus IP block). Keep the lockout separate from `Banned`. Consider an exponential delay per IP rather than per account.

### X1-20 — Medium — Legacy `HttpServer` path traversal and CSRF (opt-in)
- **Where:** `Server/Utils/HttpServer.cs` L67–72 and L98–113 (`AddNameList`: `fileName = Path.Combine(Settings.NameListPath, fileName); Directory.CreateDirectory(...); File.AppendText(...)`). The only gate is `HttpService.cs` L59 `clientIp != Settings.HTTPTrustedIPAddress` (default 127.0.0.1). `/newaccount` and `/broadcast` are unauthenticated GETs; `/broadcast` calls `Envir.Main.Broadcast` from the HTTP thread (known debt). The error response echoes the exception (`"request error: " + error`).
- **Input:** With `StartHTTPService=True`, any web page opened in a browser **on the server machine** loads `http://127.0.0.1:5679/addnamelist?id=X&fileName=..\..\..\anything.txt`, or an absolute `C:\...` path.
- **Why real:** `Path.Combine` with a rooted second argument returns it unchanged, and `..` is not filtered. The request comes from 127.0.0.1 and so passes the trusted-IP check. The attacker gets arbitrary directory creation and line append anywhere the server account can write. The service is off by default.
- **Partly fixed 2026-10-10:** `AddNameList` confines the file to `Settings.NameListPath` (full-path prefix check), rejects empty ids and names with line breaks, and errors are logged instead of echoed. Still open: GET + trusted-IP only (CSRF from a local browser can still append names inside the folder), `/broadcast` from the HTTP thread (Known debt). Retiring the service is the full fix.
- **Original fix proposal:** Retire the legacy service in favour of MirCraft, or confine `fileName` with `NamelistStore.FullPathOf`/`ScriptPaths`. Require a POST with a shared secret, and stop echoing exceptions.

### X1-21 — Low — Deleted characters can still be started
- **Where:** `MirConnection.cs` L1069 `StartGame`: picks `Account.Characters[i].Index == p.CharacterIndex` without `!Deleted`. Deleted characters stay in `Account.Characters` until `ArchiveDeletedCharacterAfterMonths` (`AccountInfo.cs` L143).
- **Input:** `C.DeleteCharacter` (or an earlier deletion), then `C.StartGame { CharacterIndex = <deleted index> }`.
- **Fix:** `if (info == null || info.Deleted) { Enqueue(new S.StartGame { Result = 2 }); return; }`.

### X1-22 — Low — Free 0-gold drops
- **Where:** `PlayerObject.cs` L7487 `DropGold`: `if (Account.Gold < gold) return;` is the only check (no `gold > 0`, no `Dead`, no `CurrentMap.Info.NoThrowItem`, no rate limit).
- **Input:** Repeated `C.DropGold { Amount = 0 }`. This fills cells within 5 tiles with gold `ItemObject`s, broadcast to everyone in range, until `ItemTimeOut`.
- **Fix:** `if (gold == 0 || Dead || CurrentMap.Info.NoThrowItem) return;`.

### X1-23 — Low — `RemoveSlotItem` destroys the item when the target is occupied
- **Where:** `PlayerObject.cs` L5281 `temp.Slots[index] = null;` runs before `if (array[to] == null)` (L5286). The fall-through only sends a failure packet.
- **Input:** `C.RemoveSlotItem` with `To` = an occupied inventory cell.
- **Fix:** Check `array[to] == null` before clearing the slot.

### X1-24 — Low — `SendMail` half-commit
- **Where:** `PlayerObject.cs` L11741. The stamp is consumed at L11784–11800. The item loop at L11804–11837 nulls `Info.Inventory[i]` per item and `return`s on a later `DontTrade`/`NoMail`/rental item. Items already removed are in `giftItems`, which is then discarded. Gold is not charged.
- **Fix:** Validate all items first, then commit (server.md §3.3: compute → validate → commit).

### X1-25 — Low — Strongbox consumed twice / reward wiped
- **Where:** `PlayerObject.cs` L6206–6216 (`case 25` consumes the box, `Enqueue(p)`, `StrongboxRewardItem`, then `break`) falls through to the common tail at ~L6340–6351, which consumes again and enqueues `p` a second time. When the stack was 1, the tail's `Info.Inventory[index] = null` can wipe the reward that `GainItem` placed in the freed slot.
- **Fix:** `return;` at the end of the Strongbox case (as the BlackStone case does).

### X1-26 — Low — Remote door opening
- **Where:** `PlayerObject.cs` L7832 `Opendoor` → `Map.OpenDoor(byte)` (`Map.cs` L61). There is no distance check, and the code has a "todo: add check for sw doors" note.
- **Fix:** Require the door cell within 1–2 tiles of `CurrentLocation`.

### X1-27 — Low (Plausible client impact) — Unchecked `MirDirection`
- **Where:** `PlayerObject.Turn` L4278 (`Direction = dir`), `HumanObject.Magic` (~L3456 `Direction = dir`), Walk/Run. `Functions.PointMove` treats unknown values as "no move", so the server is safe. The invalid value is stored and broadcast in `S.ObjectTurn`/`ObjectWalk`/`ObjectMagic` to every client in range.
- **Fix:** Clamp or reject `dir > MirDirection.UpLeft` at the handler (server-only). The client-side effect of out-of-range directions should be verified under C1/C5.

### X1-28 — Low (Plausible) — `Inspect` `SingleOrDefault`
- **Where:** `Envir.cs` ~L5283 `Players.SingleOrDefault(x => x.ObjectID == id || x.Pets.Count(...) > 0)`. It throws `InvalidOperationException` if two players match. That only happens with the known duplicate-ObjectID defect (server.md §3.8, non-atomic `++_objectID` on mob threads).
- **Fix:** `FirstOrDefault`.

### X1-29 — Low — `@CLEARBUFFS` without a GM check
- **Where:** `PlayerObject.cs` L2424. It flags all of the caller's buffs for removal, which may include script- or GM-applied penalties.
- **Fix:** GM-only, or skip non-removable buff types.

### X1-30 — Low — IP keying and unbounded IP tables
- **Where:** `Envir.cs` L81 `IPBlocks` (`ConcurrentDictionary`, never pruned), `ConnectionLogs`, and IP extraction in `Envir.Connection` L3784 / `MirConnection` L81 (`RemoteEndPoint.ToString().Split(':')[0]`).
- **Why:** On an IPv6 or dual-stack listener every client maps to `"["`, so `MaxIP` and 24 h blocks apply to all IPv6 users together. The tables grow by one entry per distinct IP forever.
- **Fix:** Use `((IPEndPoint)RemoteEndPoint).Address` (with `MapToIPv4` where applicable) and prune expired entries.

---

## Checked and fine (no finding)

- **Packet framing** (`Shared/Packet.cs` `ReceivePacket`): `ushort` length; `length > rawBytes.Length || length < 2` handled. A length of 2–3 throws and is caught (IP block, no world impact). An unknown ID drops the buffer. `_rawData` growth is bounded by about 64 KB + 8 KB. `ReadString` reads in chunks and is bounded by the stream. `C.Chat.LinkedItems` and `C.EditGuildNotice` loops read element by element, so they are bounded. `EditGuildNotice` caps 200 lines.
- **`MirConnection` stage gating:** every handler from ~L1155 onward starts with a stage check (`Chat` checks length first, then stage). Observer stage reaches only `Chat` (`@OBSERVE`), `Inspect`, `Observe`, `GetRanking` (X1-01) and `LogOut`. Storage-password handlers check `Stage`, `Player` and `Account`. `NewCharacter`/`DeleteCharacter`/`StartGame` require `Select` (Account is set by `Login`). `PlayerObject.AddObserver` refuses self-observe and stops the observer's own game, so observer cycles (recursive `Enqueue`) cannot form.
- **Item grid moves:** `MoveItem`, `StoreItem`, `TakeBackItem`, `EquipItem`, `TakeBackHeroItem`, `TransferHeroItem`, `DepositTradeItem`/`RetrieveTradeItem`, `DepositRefineItem`/`RetrieveRefineItem`, `GuildStorageItemChange` (store/retrieve/move), `EquipSlotItem` and `RemoveItem` all bounds-check `from`/`to` against the right array (and `IsValidStorageIndex` for storage) before indexing.
- **Counts:** `SplitItem` (`count < temp.Count`, `count ≥ 1`), `DropItem` (`1 ≤ count ≤ temp.Count`), `DeleteItem` (clamped), `SellItem` (`count ≤ temp.Count`), NPC `Buy` (`count ≤ StackSize`; gold and pearl cost checked).
- **Trade:** the swap happens only when both sides are locked. Every deposit, retrieve, gold or merge change calls `TradeUnlock()`. `TradeGold` deducts at deposit time. `CanGainItems`/`CanGainGold` are checked for both sides before the swap.
- **Gold overflow guards:** `GuildStorageGoldChange` (`(ulong)` check), `CanGainGold`, `SetItemRentalFee`, `SendMail` (`gold > totalGold` catches wrap), `CollectMail` clamp.
- **Market:** `MarketBuy` (sold, expired, own-auction, `Envir.Auctions.Contains`, gold checks), `MarketSellNow`, `MarketGetBack`. `ConsignItem` checks the price range and fee but accepts any NPC (minor; not listed).
- **Magic:** `HumanObject.Magic` requires a learned spell (`GetMagic`), cooldown, MP and target race filter. `MagicKey` only edits the caller's or hero's magic keys.
- **NPC scripts:** `NPCScript.Call` validates the key against the buttons of the current page and the NPC id (except X1-14). `CallNPC` caps the key at 30 characters and requires NPC range and visibility.
- **Rankings/inspect:** `GetRanking` checks `RankIndex` range. `Inspect(int)` null-checks.
- **Web admin (MirCraft, `Server/WebAdmin`):**
  - **Paths:** `ScriptPaths.TryNormalize` + `Combine` + `CrossesReparsePoint` (no rooted paths, `..`, `:`, device names or `~`; prefix check with separator; reparse points rejected). `BackupStore.Resolve` follows the same rules, is `.bak`-only, and walks reparse points. `LogRoutes.Resolve` (single file name, folder whitelist). `NamelistStore.FullPathOf`. `ScriptRoutes.ConfinedPath` (confined to `Envir`). Static file serving (`WebAdminHost` L101).
  - **Auth/CSRF:** every non-`/api/auth/*` route defaults to `RouteAuth.Admin`. `HandleApiAsync` enforces a session plus the double-submit CSRF (header == cookie == session, constant-time) on POST/PUT/PATCH/DELETE. Login has a `Sec-Fetch-Site`/`Origin`/content-type guard and a rate limiter. No GET route mutates state (only `restart-countdown` reads).
  - **Restore:** `BackupRoutes.Restore` requires the typed name as confirmation and a stopped world (`EnsureStoppedForRestore`), and takes locks in `EditLock → LoadLock`. Script backups cannot be restored through this route.
  - Known debt already in server.md §3.7: `GameDataRoutes.ApplyLiveMonsters` echoes `ex.Message`, and `WebAdminAuth.VerifyPassword` uses non-constant-time compare.
- **Already-known items, not re-listed:** the `Disconnect()`/`_receiveList` race (server.md §3.8), observer `Enqueue` iterating `Observers` unsynchronised, the accept loop counting `Connections` without the lock, and `C.PurchaseGuildTerritory` having no `GetClientPacket` case (its handler, including `PurchaseGuildTerritory`'s `MyGuildRank.Index` use, is unreachable today). **Update 2026-10-16 (Scene Lab UI phase 2.1):** the factory case was added, so the handler is reachable; it was hardened in the same change (alive + `[@GUILDTERRITORY]` page + `DataRange`, `MyGuildRank` null check, no payout or `EndGT` for owner `"None"`, gold clamp, `NeedSave`): server.md §3.5.

## Suggested order for P5/P8

1. **One-line guards** (P5, each with a fault-injection test that sends the packet to a scratch server and asserts the world keeps running): X1-01, -02, -03, -04, -05, -06, -07, -08, -09.
2. **The dupe and scam:** X1-10, X1-13. Then X1-11 (key fix plus a data-migration note for `GSpurchases`/`GameshopLog`).
3. **Flood/DoS hardening** (P8): X1-12, X1-18, X1-19, X1-22.
4. **Protocol caps** (C5): X1-15, X1-27.
5. **Script/command logic:** X1-14, X1-16, X1-29; then guild X1-17; then the Low cleanups.
6. **Isolation backstop (P5 design):** a per-connection try/catch around `MirConnection.Process()`/`ProcessPacket` that logs and disconnects only that client turns every Critical above into a single-player disconnect. It does not replace the guards, because a handler that throws mid-mutation can leave state half-committed (server.md §3.3).
