# Codebase review, 2026-10-24

Read-only review of committed code (HEAD `a76705de`, branch FuckingAround) by seven parallel reviewers (Opus 5.5, medium effort), one per area. Excluded: the dungeon proof of concept, everything already listed as Known debt / Known defect in `server.md` / `client.md` / `webclient.md`, and findings in `X1-Security-Review.md`, `Packet-Mutation-Audit.md`, `Review-P2-P6-Threading.md`. Nothing was fixed.

**Verified** = checked against the code by the coordinating session; the rest are reviewer findings with the reviewer's confidence. Line numbers are HEAD and approximate.

## Fix next

| # | Finding | Severity | Status |
|---|---|---|---|
| 1 | Friend save writes `Friends.Count` but skips unresolved entries: `Server.MirADB` corrupt after any archive/delete of a befriended character | critical | verified |
| 2 | `S.ReceiveMail` can exceed 65,535 bytes: any player can lock any character out at every login | high | verified |
| 3 | Player death (incl. `[@Die]` NPC script) runs on the mob thread when a monster kills a player | high | partly verified |
| 4 | Client patcher defaults to `http://mirfiles.com/...` and installs new program files directly | high | verified (default) |
| 5 | Rented items can be consigned/auctioned (and maybe moved to the hero), so the lender never gets them back | high | partly verified |
| 6 | Rental expiry crashes the 5-minute world pass when the renter or lender was archived/deleted | medium | reviewer, high conf. |
| 7 | Guild / conquest / goods saves at shutdown and reboot are queued but never awaited | medium | reviewer, medium conf. |
| 8 | Refine: second request overwrites the first; logout with full bag deletes refine-grid items. Disassemble deletes the source when the reward doesn't fit | medium | reviewer, high conf. |
| 9 | Storage password guesses are unlimited (cheap v1 hash, no failure count) | medium | reviewer, high conf. |
| 10 | Public relay account/character quotas bypassed by pipelining requests | medium | reviewer, high conf. |
| 11 | Web admin GM actions that time out after starting are not audited, and a retry applies mass grants twice | medium | reviewer, high conf. |

#1 and #6 share a cause: archive (`AccountInfo` ctor, `@ARCHIVEPLAYER`) and the web admin account delete remove characters from `CharacterList` but leave friend and rental references to them. Fixing both together (prune at load + null checks) is natural.

## Details

### 1. Friend count mismatch corrupts the account file (critical, verified)
`Server/MirDatabase/CharacterInfo.cs:557-562`: `writer.Write(Friends.Count)`, then `if (Friends[i].Info == null) continue;`. `FriendInfo.Info` is null when the friend's character left `CharacterList` (inactive/deleted archive at load, `@ARCHIVEPLAYER`, web admin account delete in `AccountRoutes.cs` ~977, which unlinks spouse and mentor but not friends). The reader then parses `RentedItems`/`Married`/… as friend records; the next `LoadAccounts` throws or loads garbage, and every backup after the trigger carries it. Already noted in `Offline-Account-Cache-Plan.md` but not as a Known defect. Fix: write the count of entries actually written, and prune unresolvable friends at load.

### 2. Mail packet over 64 KB locks a character out (high, verified)
`PlayerObject.cs` `SendMail` ~13154/13168 (message `Length > 500` counts UTF-16 chars; box check `Mail.Count > 50` allows 51), `GetMail` 13543 sends up to `MailCapacity` mails in one uncompressed `S.ReceiveMail`. 51 mails of 500 CJK chars ≈ 79 KB; the 16-bit length wraps, the client misreads and disconnects at every login (web client too). Free, about 8.5 min with the 10 s cooldown, persists until an admin deletes mail. Fix: split `S.ReceiveMail` by measured size (as `S.MapDetails` / `LiveDbPush` do) or compress it, and cap messages by UTF-8 bytes.

### 3. Player death on the mob thread (high, partly verified)
`MonsterObject` attacks call `target.Attacked(this, …)` (`MonsterObject.cs:1414/1425`) inside the mob pass; `HumanObject.Attacked` → `ChangeHP` → `Die()` → `PlayerObject.Die` (~1521) has no `RunOutsideMobPass`/`InMobPass` handling anywhere in `HumanObject`, unlike monster death and monster NPC calls. Inside the pass run `CallDefaultNPC(Die)` (MOVE to another thread's map, GIVE/TAKE, GLOBALMESSAGE over `Envir.Players`), hero/creature despawn, `DeathDrop`, `WebAdminHooks.RaisePlayerDied`. `NPCSegment` already guards `CatchUpRespawns` with `!Envir.InMobPass`, so scripts were known to run there. Not yet verified: that no wrapper higher up defers player damage. Fix: defer `Die` (or at least the script, despawns and hook) with `Envir.RunOutsideMobPass` when `InMobPass`, also for `HeroObject.Die`.

### 4. Patcher default host and direct install of new program files (high, verified default)
`Client/Settings.cs:229,232`: `P_Patcher = true`, `P_Host = "http://mirfiles.com/mir2/cmir/patch/"`. A client with no `[Launcher] Host` (`.ini` is on the deny list, so the patcher never ships one) asks upstream's domain over plain HTTP. `PatchService.Fetch` (~905) installs an `.exe`/`.dll`/`.json` the folder does not have yet (e.g. `version.dll`, loaded by the apphost next start); the SHA-256 comes from the same untrusted manifest. Same risk for any `http://` host via MITM. Fix: default the host to empty (or `P_Patcher` false), require https, stage new program files.

### 5. Rented items escape return (high, partly verified)
`ConsignItem` (`PlayerObject.cs:9525`) checks `temp.Info.Bind.HasFlag(DontSell)` only, not `RentalInformation.BindingFlags` (which sell, mail, trade and storage check). `ProcessRentedItems` (`Envir.cs` ~6724) only scans the renter's inventory and equipment, so a consigned/auctioned item is never returned and the renter keeps the gold. Medium confidence: `TransferHeroItem` (~6757) has no rental check either. Fix: refuse when `RentalInformation?.BindingFlags` has DontSell/DontTrade.

### 6–11
- **6. Rental expiry NRE** — `Envir.cs` ~6740 (`rentingPlayer.Inventory`) and ~6828 (`owner.RentedItems`) after `GetCharacterInfo` returns null; throws every 5 min and skips the rest of that `Envir.Process` pass (auctions, respawns, timers). Death-drop `ReturnRentalItem` (~1667/1715) too. Fix: null-check, drop the entry.
- **7. Unawaited shutdown saves** — `Envir.cs` ~2582 (`SaveGuilds(true); SaveConquests(true)`) and `SaveGoods(true)` ~5045 queue `BeginWrite`s; `File.Replace` happens later on a pool thread. `Reboot()` reloads `.mgd`/`.mcd` before they finish (stale data, or replace fails on the open handle); shutdown can exit first. Guild storage/gold roll back against freshly saved accounts → item loss/dupe. Fix: drain outstanding side-file writes with a timeout, or write synchronously when forced.
- **8. Refine / disassemble** — `RefineItem` (~14589) has no `CurrentRefine != null` guard (ingredients lost, gold charged again); `RefineCancel` (~14551, from logout ~1314) does nothing when the bag is full and `Info.Refine` is not saved, so items vanish (mail them like `TradeCancel`). This is separate from the known dead `S.RefineCancel` packet. `DisassembleItem` (~10239) gains without `CanGainItem` and does not clamp `Count` to `StackSize`.
- **9. Storage password brute force** — `MirConnection.cs` 1737/1774/1814: wrong guesses are free; v1 PBKDF2-SHA1 50 iterations on the game thread. Fix: count failures and disconnect/block like `CountFailedLogin`.
- **10. Relay quota race** — `WebRelay/RelaySession.cs:133-142` checks, `:163-170` counts on the server reply; a burst of `C.NewAccount` all pass. Same for login failures across 8 parallel sessions. Fix: reserve a pending slot at forward time.
- **11. Unaudited timed-out GM actions** — `Server/WebAdmin/GmActions.cs` ~738: audit runs only after a successful return; a 504 after the delegate started still applies it. `AccountRoutes.AuditIfTimedOutAsync` exists but only Account/Security use it; CharacterItem, Guild and Market routes have the same gap. Fix: use it, and show "outcome unknown" on 504.

## Other findings by area

### Network and packets
- **IP block shortened** (medium/low) — `Envir.cs` ~4785 `UpdateIPBlock` overwrites the expiry; 5 bad logins on a still-open connection turn a 24 h or operator block into 1 min. Keep the later expiry.
- **Chat item link with unknown ID** (low) — `ProcessChatItems` leaves `item` at the last array element when nothing matches, so the sender's last item is linked. Set `item = null` after the loop.

### Player economy
- **Rental period changed after fee lock** (medium) — `SetItemRentalPeriodLength` (~16115) does not clear the partner's `ItemRentalFeeLocked` (same class as X1-13). Call `ItemRentalRemoveLocks()`.
- **NPC services usable from anywhere** (low-medium) — refine, check/collect refine, disassemble, downgrade awakening, reset added item, replace wedding ring, awakening check only `NPCPage.Key`, which is never cleared. Add the NPC map/range check `SellItem` uses.
- **Storage split into a locked page** (low) — `SplitItem` (~7425) places from `BeltSize`; with expired storage expansion the new stack lands at index ≥ 80. Place at `nindex`.
- **Sell gold-overflow guard never fires** (low) — ~9078 adds in `uint`. Cast to `ulong`.
- **Crafting cost overflow** (low, needs odd data) — `NPCScript.cs` ~1268/1449 `recipe.Gold * count` in `uint`.

### World and threads
- **SnakeTotem ghost corpse / leak** (medium) — `Monsters/SnakeTotem.cs:52` calls `Despawn()` without `CurrentMap.RemoveObject(this)`; the object stays in its cell forever and is sent to players who walk by.
- **NPC `BREAK` leaks to the next caller** (low-medium) — `NPCPage.BreakFromSegments` is shared and only reset before the next segment; a `BREAK` in the last segment makes the page do nothing for the next caller. Reset after the loop or make it local.
- **`ProcessSpecial` NRE** (low) — `NPCScript.cs:944` `sentGoods.AddRange(callingNPC.UsedGoods)` outside the null check (BUY pages from default/item scripts).
- **`CHECK` flag off by one** (low) — `NPCSegment.cs:2340` uses `>`; `CHECK [1999]` throws, reachable via `%INPUTSTR`.
- **Guild recall null rank** (low) — `NPCSegment.cs` ~4316/4360 read `MyGuildRank.Index` unchecked; `GTAllRecall` also moves dead members.
- **Behemoth / HornedCommander** (low) — add slaves when spawn failed; pruned next tick, no crash.

### Persistence
- **No flush before replace** (medium, on power loss) — `WriteAccountsFile` (~3947), `EndSaveAccounts` (~4195), sync `SaveAccounts` (~3517), `CompleteSideFileSave` (~3720) dispose without `Flush(true)`; a power cut can leave a zero-filled live file. Add `Flush(flushToDisk: true)`.
- **Guild re-index not crash-consistent** (low) — `Envir.cs` ~3648-3687 renames guild files by position after a deletion; a crash mid-swap loads the deleted guild and drops the last one.
- **Shutdown save vs stuck off-thread save** (low) — ~3506-3520: after the 30 s wait, `File.Create` of `Server.MirADBn` fails while the old write still holds it; the final save is lost (logged only).

### Web admin
- **Mass grants in one tick** (medium) — `GmActions.cs` ~1592-1655 (`massitem`, `masslevel`, `massallskills`, `massgold`) do all online players in one game-thread step; batch via `AddAdminTickJob` like the teleports.
- **Backup download in memory** (low-medium) — `BackupRoutes.cs` ~250-270 buffers up to 1 GiB twice per request, 32 requests allowed; stream it instead.
- **Monitor events go stale** (low) — `WebAdminMonitor.cs` drains only on a snapshot poll; with no page open the queue fills and new events are dropped. Drop oldest, or drain from the 5 s sampler.
- **Exception text returned** (low) — `BackupRoutes.cs:450` appends `ex.Message` (paths) to the response, against server.md §3.7.
- **Bot stop blocks WorkLoop; bot password on the command line** (low) — `WebAdminTools.cs:41-45` writes stdin synchronously at world stop; `StressBotConfig.BuildArgs` passes `--password` visibly in the process list.

### Desktop client
- **Patcher hash not validated** (medium) — `PatchData.Parse` (~33-41) accepts any `h`; `PartPath` (~1112) builds `TempDir + h + ...part`, so `..\` escapes `Patch\Temp`; a non-hex hash makes `PatchState.Save` throw, which skips `Settings.Save()` and the staged-update apply on every exit. Require 64 hex chars and `s ≥ 0`.
- **Huge library index from the manifest** (medium) — `ComputeHighest` (~397) has no bound; `Data/Monster/20261024.Lib` makes `InitLibrary`/`GrowArrays` allocate millions of `MLibrary` (OOM on the loader thread), repeated every start from the cached `Manifest.bin`. Cap by the enum/band range.
- **Two instances roll back a good build** (medium, medium conf.) — `PatchApply.Startup` (~60-80) runs before the `patch.lock` check; a second instance during the proving start rolls back and rejects the build permanently. Take the lock first.
- **D3D11 device re-create leaves stale textures** (medium, medium conf.) — after `D3D11Backend.Reset` (~1060), textures not in DXManager's lists (`MapControl.Atmosphere` dot/ring, `CharacterWindow._bodies`, `NpcWindow` `FlatShadow`) keep SRVs from the old device. D3D9/D3D11 divergence. Register them or add a device generation.
- **Install second move fails** (low) — `PatchService.Install` (~1006): after `local → Trash` succeeds and `part → local` fails, the file is missing until next start. Use `File.Move(..., overwrite)` / `File.Replace`.

### WebRelay and web client
- **`/data/` serves the operator's `QuestTracking.ini`** (low) — `AssetHost.cs:66`, `RelayHost.cs:163-171`; `start-public-webrelay.ps1` points `--client-data` at the live client. Apply the patch host's deny list (`Data/UserData/**`, `*.ini`, `*.txt`).
- **One handler throw drops the queued frames** (low-medium) — `WebClient/src/game/session.ts:93-97` replays a spliced backlog without try/catch.
- **Module alias cache entries** (low) — `PageBuild.cs:141-143` keys on the raw URL, so case/`./`/`//` variants each get a fresh Brotli pass and cache slot. Key on the resolved path.
- **Patch file changed between check and read** (low) — `PatchIndex.cs:251-265`, `AssetHost.cs:141`: with `--patch-auto` on a live build folder, a rebuild mid-serve sends new bytes under the old immutable `?v=` URL. Hash before caching / re-check after streaming.

## Checked and found fine (summary)
Packet Read/Write symmetry across all loop/conditional packets (only the known `ChatItemStats` case), factory cases, receive `ReadCount` guards, `SocketSender`/batching/DirectSend; trade, mail send/collect, GameShop, market, guild storage, merge/stack/sort, sell/repair batches, drop/pickup; mob-pass barrier, deferred monster death, cross-map monster teleports, interlocked counters; Save/ctor order and version gates for all user-data models up to v120, offline account cache invalidation, SQLite writer and first import; web admin auth/CSRF/Origin, static file confinement, tool process arguments, `EditLock` ordering, frontend escaping; patcher path safety (`PatchPaths`), `MLibrary` locking, new model-only client handlers; relay file serving, framing limits, teardown, Origin, enum and TS codec spot checks.
