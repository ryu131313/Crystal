# Tick spikes: dormant monster lockstep + offline account save — plan (2026-10-22)

**Status: done 2026-10-22** (phases 1-3 + review fixes merged into FuckingAround; `OfflineAccountCache` ships **Off** — turn it on via web admin Settings › Optimisation after a live deploy). Results: `Future Optimisation/P9-07-SectorGrid-Dormant-EmptyMaps.md` § Lockstep fix, `P9-11-AccountSave-BufferWriter.md`, `P9-12-Offline-Account-Cache.md`. Follow-up named in `Future Optimisation/P9-10-AccountSave-OffThread.md` ("Known limits"). Roadmap P9 backlog.

## Lean execution plan
Two causes of the Tick Lateness spikes on live (400 bots on ~5 of ~500 maps):

1. **Dormant monster lockstep (every 10 s).** `MonsterObject.SleepIfDormant` sets `AloneTime = Envir.Time + DormantAloneDelay` (fixed 10 s). It runs right after `CheckAlone` gives a first check its random phase and overwrites it, so every monster spawned on the same tick (all maps at world start, each respawn wave) re-checks on the same ms forever: ~72k `alone` runs in one second, ~5k in the other nine. The due scheduler works that backlog off over many passes, so lateness reaches 30-40 ms although no single pass is slow (max 3-6 ms).
2. **Account save (every 5 min):** 25-56 ms of serialisation on the game thread for 11,812 accounts (the rest of this document).

| Phase | Work | Smoke check | Who |
|---|---|---|---|
| **1. Dormant spread + empty-map sleep** (done 2026-10-22; results in `Future Optimisation/P9-07-SectorGrid-Dormant-EmptyMaps.md` § Lockstep fix) | `SleepIfDormant`: delay `DormantAloneDelay / 2 + Envir.Random.Next(DormantAloneDelay)` (5-15 s, mean 10 s), so one cycle breaks the lockstep. On a map with **no players**, sleep `DormantEmptyMapDelay` (60 s, also randomised) — after confirming every way a player arrives on a map (login, teleport, map change, recall, revive/respawn) wakes the monsters near the arrival point (`PlayerGrid.WakeAround`), and adding the wake where one is missing. No new per-call allocation; one `Random.Next` per dormant check. | Scratch server, bots on 1-2 maps, `[Diag] LogLoopProfile`: `alone=` flat per second (no 10 s burst); arriving on an empty map aggroes monsters as before | Opus 5.5 subagent |
| **2. Faster account serialiser** (done: byte-identical, ~19 → ~16 ms on the live copy — gate not met) | A `BinaryWriter` subclass writing straight into its own growable `byte[]` (overrides `int`/`long`/`short`/`bool`/`byte`/`double`/`float`/`string`/span with the same UTF-8 7-bit length-prefix encoding), used by `BeginSaveAccountsOffThread` instead of `BinaryWriter` over `MemoryStream`. File byte-identical; no `Save` signature change. | `Server.Persistence.Tests` byte-identity (existing + the new writer on random data/strings), bench on a copy of the live `Server.MirADB` (11.8k accounts): game-thread save ms before/after | Opus 5.5 subagent (parallel with 1, own worktree) |
| **Gate** | If phase 2 brings the live-sized save to ≤ ~10 ms, stop: phase 3 stays on file. Otherwise continue. | — | me |
| **3. Offline account cache** (done: ~13 → ~3 ms at 400 online, rolling compare instead of the hourly full compare; review: no Critical/High, 2 Medium + 6 Low fixed) | The design below (Phases 0-2 of "Phases"), flag `[Optimisation] OfflineAccountCache` default **Off**, Verify mode, full saves reset the cache, hourly compare. | Persistence tests (byte-identical after random mutation sequences), short scratch soak in Verify with 0 mismatches | Opus 5.5 subagent |
| **Review + fix** | One read-only reviewer over the whole diff; I check and fix its findings. `tools\verify.ps1`. | All builds + tests | Opus 5.5 subagent + me |

Live deploy is user-run (Runbook).

## Why
Since P9-10 the file write runs on a pool thread, but the game thread still serialises **every** account on every periodic save (the bytes are the consistent snapshot). That cost grows with all accounts, offline ones included:

- Live, 2026-10-22 06:03:25: `[Diag] Save accounts=55.84ms … bytes=37500908 accounts=11812`. That pass was 56.5 ms (`saves=56.39`), against 0.4-7 ms for every other second in the window. About 4.7 µs per account, the same rate P9-10 measured under bot load.
- Most of those 11,812 accounts are offline (bot accounts from stress runs), and their bytes have not changed since the last save.

**Goal:** the periodic save serialises only accounts that are online or changed; offline unchanged accounts reuse their bytes from the previous save. Target: game-thread save cost proportional to online accounts (≈ 2 ms at 400 online), **with a byte-identical `Server.MirADB`**.

## Constraints (from AGENTS.md / server.md)
- **No format change.** The file stays byte-identical to a full save, so no `Envir.Version` bump and no loader change. Anything that would need a format change (see "Time-dependent bytes") is excluded from caching instead.
- Game thread only for the cache and for every dirty mark. No locks, no LINQ, no per-tick allocation.
- Shutdown saves and the operator's **Save now** stay full saves (no cache). Note (review H1): today "Save now" (`RequestSaveNow` → `_saveNowRequested`) and web admin edits on a running world go through the **periodic** path, so they must be made full explicitly (see Safety nets). A missed dirty path can then lose data only on a crash or until the next full save, never on a clean stop.
- Behind `[Optimisation] OfflineAccountCache` = `Off | On | Verify` (default `Off` until the soak below passes), web admin Settings › Optimisation, like `AccountSaveOffThread`.
- `dotnet test Server.Persistence.Tests` after the change (touches `Envir` save code).

## What the audit found (2026-10-22)
Full report: subagent audit in this session; summary of what drives the design.

### Save layout
- `Envir.SaveAccounts`: global header → **all heroes** (`HeroList`, not nested in accounts; `CharacterInfo` writes only hero indexes) → **all accounts** (`AccountInfo.Save` nests its `CharacterInfo`s, storage, gold) → auctions, mail id, GameShop log, saved spawns, market sales.
- The cache unit is therefore **one `AccountInfo` blob**. Heroes are a second, smaller list; v1 keeps serialising them in full (measure their share first; cache them in v2 keyed by owner if it matters — `HeroInfo` has no owner link).

### "Online" is not one thing
- Account online = `AccountInfo.Connection != null` (select screen counts, no `PlayerObject`).
- Character online = `CharacterInfo.Player != null`; cleared only at the very end of `StopGame` / `CleanUp`, after the logout writes (LastIP, LastLogoutDate, pets, buffs, CastTime, relationship logout).
- **Rule:** an account is **hot** if it has a connection, or any character has a `Player`, or it was hot at any point since the last save (sticky bit set where `Connection` is assigned — `CompleteLogin` ~5238, not `StartGame`, so a select-screen-only session leaves a trace; cleared only by a save that serialised it while it was cold). Hot accounts are always serialised fresh and never cached.

### Time-dependent bytes (bytes change with no mutation)
- **Refine:** `CharacterInfo.Save` writes `CollectTime - Envir.Time` (and assigns `RefineTimeRemaining`).
- **Intelligent creatures:** `Save` writes `(Expire - Envir.Now).TotalSeconds` unless `Expire == DateTime.MinValue` (permanent, written as -9999).
- Caching those would extend the timer by however long the blob was cached. **Rule:** an account with any character where `CollectTime > Envir.Time` or a creature with `Expire != MinValue` is never cached (treated as hot). Count them in the diag line; if they turn out common, a format change (absolute dates) is a separate, versioned task.

### Bytes that depend on other accounts
- **Friends:** `Save` writes `Friends.Count` but skips entries whose lazily resolved `Info` is null (the friend's character left `CharacterList`). That is an existing corruption risk (count ≠ entries) **and** it makes one account's bytes depend on another character's existence. Review L1: `FriendInfo._Info` is kept once resolved and every save resolves it, so a removal does not change bytes already resolved; bytes change when a never-resolved entry starts resolving again (GM `RESTOREPLAYER` re-adding an archived character). **Rule:** invalidate the whole cache on any character re-add/restore, and (cheap, harmless) on removal. Fixing the count bug is a separate task (Known defect; recommended before or with this change). Side cost: each unresolved friend is an O(CharacterList) scan per save (`GetCharacterInfo(int)`, ~6132) — not profiled.
- **Mail objects** are reachable from the sender (`Envir.SentMailIndex`) as well as the recipient; any mail mutation must dirty `mail.RecipientInfo`'s account.
- **Auction items:** a won bid mails the same `UserItem` instance that the `AuctionInfo` still holds.

### Offline mutation paths found so far (each needs a dirty mark)
| Area | Path | Dirty |
|---|---|---|
| Pre-login packets | `Envir.Login` (ban-field reset before the password check), `ChangePassword` / `CompleteChangePassword` (`HTTPLogin` has no callers) | account — only when a field actually changes, so unauthenticated clients cannot force blob churn |
| Login | `CompleteLogin`: v2 hash upgrade, wrong-password ban, LastDate/LastIP | account |
| Account/character creation | `AccountList.Add` (packet, editor, web admin), `NewCharacter`, delete character, `ChangeStoragePassword` (select screen) | account (new blob) |
| Game start | `MirConnection.StartGame` clears character ban fields before the `PlayerObject` exists | account (and it goes hot) |
| Mail | `MailInfo.Send` (any sender: players, system, NPC, auction, rental return, GameShop gift?) | recipient's account |
| Daily reset | `ProcessNewDay` → `ClearDailyQuests` over every character | **all** (rare) |
| Rental | `ProcessRentedItems` / `ReturnRentalItem`: borrower slot cleared, lender mail + RentedItems | borrower + lender |
| Auction | `ProcessAuction`: won item mailed to the buyer | buyer |
| GameShop | `ClearGameshopLog` (ResetGS) clears `GSpurchases` everywhere | **all** |
| Archive/restore | `PlayerObject` GM archive/restore paths (CharacterList / Characters edits) | **all** (removal) |
| Web admin | character delete/restore, password, ban, RequirePasswordChange, account delete, direct `SaveAccounts()` calls | per route; account delete = **all** |
| Load / restore | `LoadAccounts` (start and web admin restore) replaces every list | **all** (cache built from a fresh save, never from file bytes: load drops archived characters, unbound items, orphan quests) |
| Shutdown | `CleanUp` (mentor/marriage/mail trim for deleted characters) runs before the final save | covered by the full shutdown save |
| Logout | `PlayerObject.StopGame` writes Info before `Player` is cleared; `LogoutMentor` adds to the **mentor's** `MentorExp` (mentor may be offline) | own account (via the sticky hot bit) + mentor |
| Mail senders | letters/parcels (`PlayerObject` ~13109, 13163), GameShop gift (~15636, 15860), outbid refund (~9845 → `MailCharacter`) — all through `MailInfo.Send` | recipient (one mark in `Send`) |
| Friends | block / `RemoveFriend` remove the entry on the other side (~14232, 14258); accept adds it (requester online) | other side |
| Marriage / mentor | NPC `FORCEDIVORCE` → `NPCDivorce` (~14870-14897: spouse fields + RingL WeddingRing); `MentorBreak` (~15346-15415, also from LevelUp ~1889) | spouse / partner |
| GM commands | `CHANGEGENDER` (~3214), `CHANGECLASS` (~4405) on an offline target; `ARCHIVEPLAYER` / `RESTOREPLAYER` (above). Other GM commands use `GetPlayer` (online only); NPC `SENDMAIL` targets the runner | target account; archive = **all** |
| Web admin `AccountRoutes` | create, profile, ban/unban, AdminAccount, RequirePasswordChange, password, clear storage password, gold/credit; **delete account** (also writes lovers'/mentors' fields, refund mail to bidders, removes from every list) | account; delete = **all** |
| Web admin `CharacterRoutes` | rename (offline only; also guild/rank copies), gender, level, PK, hair, gold/credit, soft delete/restore, flags, quest reset, magic set/remove | account |
| Web admin `CharacterItemRoutes` | add to storage/offline inventory, remove, **edit a `UserItem` in place** (Count, Dura, AddedStats) | the item's owning account (route knows the character) |
| Web admin `GmActions` | chat ban/unban, set level, set PK | account |
| Web admin `MarketRoutes` | `MailCharacter` to seller/buyer with gold or the auction item | recipient (via `MailInfo.Send`) |
| Web admin backup restore | `BackupRoutes` restore / `ClearMainAccounts` → `LoadAccounts` | **all** |
| WinForms (UI thread, no `QueueAdminAction` — Known debt) | `AccountInfoForm` (ID, profile, ban, password, admin, storage password, wipe characters, `AccountList.Remove`), `PlayerInfoForm` (name, level, PK, gold, credit, chat ban, account ban, flags, hero name/level), `GameShop.cs` → `ClearGameshopLog` | **all**, via the Interlocked epoch (these already race with serialisation, cache or not) |

Not account data (no mark): market buy/bid changes only the `AuctionInfo` (global section; `AccountInfo.Auctions` is not saved); guild kick and web admin guild member removal write `GuildIndex` only for an online member (offline members are fixed at their next login); `QuestReload` only re-points `Info` references.

**Rule that follows:** there is no setter layer, so per-site marks will be missed eventually. Use per-account marks only on the hot game paths (mail send, login screens, rental, auction, friends, mentor/marriage, GM commands) and **invalidate all** on every web admin user-data write, every WinForms write and every lifecycle event. Those are rare; each costs one full save.

**Later audit pass (same day) — added paths:**
| Area | Path | Dirty |
|---|---|---|
| Sent mail | `HideSentMail` (`PlayerObject` ~1091-1109) sets `SenderHidden` on the MailInfo in the **recipient's** box | `SentMailEntry.Recipient` |
| More `MailCharacter` callers | SellNow (~9932), get-back refund (~9982); rental return on death drops (`PlayerObject` ~1661, 1709; `HumanObject` ~1556, 1634) | recipient (covered by the mark in `MailInfo.Send`) |
| Mentor | `MentorBreak` with an offline partner also does `partner.Experience += MentorExp` (~15407); triggered by LevelUp, login expiry (~2148), `C.MentorBreak` | partner |
| Stale `PlayerObject` refs | `MarriageReply` / `DivorceReply` write the proposer through a stored `PlayerObject` with no online check (~15162, 15272); PK points to `LastHitter` (~1549) | written character — mark **at the write site**, because the sticky hot bit only covers writes before the account goes cold |
| Heroes | `SealHero` turns a hero into an item; whoever uses the item gets the `HeroInfo` (~7304, `AddHero` ~16626), so ownership moves between accounts; `DeleteHero` (~16607) leaves it orphaned in `HeroList` | heroes stay uncached in v1; a v2 hero cache needs its own flag, not the owner's |

Checked and clear: market seller proceeds are paid only while the seller is online (`MarketGetBack`); the phase 2.5-2.8 social handlers other than HideSentMail and friend block/remove touch only the caller; guild gold/storage live in the `.mgd` files; guild storage, trade, parcels, NPC buyback and rental returns move a `UserItem`, they never share it between accounts (consignment/auction wins share it only with the global Auctions section, which is not cached).

**Remaining audit risk (Phase 0):** WinForms `AccountInfoForm` ban buttons may not disconnect online accounts (unconfirmed); re-grep for writes through stored `PlayerObject` / `CharacterInfo` references (the stale-ref class above) — it is the category most likely to hide more cases.

**Existing defects found on the way (not fixed by this plan):** the Friends count/skip mismatch (above); `ProcessRentedItems` dereferences `rentingPlayer` without a null check (~6295); GM `CHANGEGENDER` / `CHANGECLASS` dereference `data.Player.Name` for an offline target after the change is made (~3234, ~4421); GM `LOADPLAYER` is a no-op; `MentorBreak` zeroes `Info.MentorExp` instead of `partner.MentorExp` (~15400); the `CleanUp` mail-trim condition (`DateOpened > Now`) is never true; `CompleteLogin` upgrades the v2 hash before the gate / RequirePasswordChange refusal; `Envir.Login` and `ChangePassword` reset ban fields before the password check (any unauthenticated client can do it for any account).

## Design

### Data
- `AccountInfo`: `byte[] SaveBlob` (immutable once stored), `int SaveBlobEpoch`, `bool SaveHot` (sticky).
- `Envir`: `int _accountCacheEpoch` (bumped by "invalidate all"), counters for the diag line.
- One entry point: `Envir.MarkAccountDirty(AccountInfo a)` → `a.SaveBlob = null`; null-safe (`HeroInfo` is a `CharacterInfo` with `AccountInfo == null`). Character paths call `MarkAccountDirty(info.AccountInfo)`.
- "Invalidate all" = `Interlocked.Increment(ref _accountCacheEpoch)`, safe from the known-debt off-thread callers (WinForms UI thread, legacy `HttpServer` thread).
- **Epoch ordering rule (review H3):** the save reads `_accountCacheEpoch` once (`Volatile.Read`) before its loop and tags every blob stored in that pass with that snapshot; an entry is valid only if its tag equals the current epoch. Off-thread callers bump **after** mutating (WinForms forms: before and after), so a pass that raced the edit stores blobs under an epoch that is already stale.

### Save
In `SaveAccounts(stream)` (periodic, `On` only), per account in order:
1. If `SaveHot`, no blob, stale epoch, or time-dependent (refine / creature rule): serialise fresh into the reused buffer.
   - If the account is cold now: copy that range into a new `byte[]`, store it with the pass's epoch snapshot, clear `SaveHot`.
   - If hot: keep `SaveHot` set if still online (login sets it again anyway).
2. Else: reference the cached blob.

**Write as a segment list from the start (review M4).** Copying ~37 MB of blobs into the buffer (~4 ms, more under memory-bandwidth contention) would be the dominant cost at 400 online (fresh serialisation ≈ 2 ms). Instead the game thread builds a list of segments — buffer ranges plus references to immutable blobs — and the pool thread writes them in order. `AccountSaveJob` already carries one array, so this is a small change. Blobs are never mutated, only replaced, so the pool thread's references stay valid.

Memory: ~3 KB per offline account as small (non-LOH) arrays, about the file size (37 MB live), on top of the reused buffer (up to 2× the file). Arrays hold no references, so gen2 marking cost is small.

### Safety nets
1. **Full saves stay full and reset the cache (review H1, H2):**
   - Shutdown, `LoadAccounts`, backup/restore and every direct `SaveAccounts()` never use the cache, **and bump the epoch when they finish**, so the next periodic save cannot write an older blob over what they wrote.
   - `RequestSaveNow` (operator "Save now", `MarketRoutes`) becomes a full save: a `_saveNowFull` flag, or bump the epoch first.
   - Web admin writes: put the invalidate-all bump on the one path every write route takes (the audit / `work()` wrapper, ~`AccountRoutes` L230; the `CharacterRoutes`, `CharacterItemRoutes`, `GmActions` equivalents), not in each route, plus a test that every write handler reaches it. Today only 3 of `AccountRoutes`' 10 POST routes call `PersistIfStopped`.
2. **Hourly rebuild that verifies (review M1):** every `[Optimisation] OfflineAccountCacheFullEvery` saves (default 12 = 1 hour at `SaveDelay=5`), serialise every cached account fresh and **compare** with its blob: keep the old array if equal (no 37 MB re-allocation), count and log a mismatch if not, then use the fresh bytes. That is a complete hourly verify at a cost already paid, and it turns a missed dirty path into a logged event instead of a silent heal.
3. **Verify mode** (`Verify`, for the soak): inline (review M2) — for each sampled cached account (`OfflineAccountCacheVerifyPerSave`, round-robin), serialise fresh into the buffer instead of using the blob, compare, then replace the blob on mismatch (so it does not repeat every pass). Counts `mismatch` and logs `[Diag] AccountCache mismatch account=<index>` once per account. Verify-all is the soak mode; on live, the hourly rebuild already covers every account.
4. **Coarse invalidation where precision is hard:** any web admin / editor action on account data, any character re-add/restore or removal, new day, GameShop reset, load/restore → invalidate all. These are rare; each costs one full save. Marks go **before** any code that can throw after mutating (GM `CHANGEGENDER` / `CHANGECLASS`).

### What a missed mark costs (review H4)
Not just stale data: the file becomes **internally inconsistent across accounts**. A mail sent by an online player to an offline recipient whose mark was missed is gone from the sender's fresh bytes and missing from the recipient's cached blob — the item is lost after a crash. The reverse (a rental return with a cached borrower blob) duplicates the item. This is the cross-account atomicity P9-10 refused to break, and rotated `Back Up\Accounts` files and manual backups (`BackupRoutes` ~236) copy it as is. Hence: full saves reset the cache, the hourly rebuild compares, and the flag stays `Off` until the soak shows zero mismatches.

### Diagnostics
Extend the `[Diag] Save` line (append-only fields): `cached=<n> fresh=<n> timed=<n> verified=<n> mismatch=<n> epoch=<n>`; expose the same counters in the web admin status JSON. `timed` includes accounts kept uncacheable by a creature whose `Expire` passed while offline (costs time, not correctness).

## Phases
- **Phase -1 — cheaper first, then re-measure (review: simpler alternative):**
  - Delete the stale bot accounts on live / run bot tests on scratch (no code).
  - A faster serialiser with **no invalidation risk**: a `BinaryWriter` subclass writing straight into its own `byte[]` (its primitive writes are virtual: override `int`/`long`/`bool`/`byte`/`string`/span with the same UTF-8 length-prefix encoding), no `Save` signature changes. Estimate 2-4× (not measured); byte identity checked by the existing `AccountSaveOffThreadTests`. P9-10's own numbers (1.4 µs idle vs 4.7 µs per account under load) suggest per-call overhead dominates.
  - Re-measure; continue with the phases below only if the save is still a visible spike at the expected account count.
- **Phase 0 — finish the audit:** grep for writes through stored `PlayerObject` / `CharacterInfo` references after logout (delayed actions, NPC timers); confirm the WinForms ban buttons. Decide the Friends count bug: fix first (separate change) or rely on invalidate-all on restore.
- **Phase 1 — plumbing + tests, flag Off:** fields, `MarkAccountDirty`, epoch snapshot, the save loop, segment-list write, diag fields. Tests in `Server.Persistence.Tests`:
  - On vs full save byte-identical after random mutation sequences on the synthetic world (each audited path driven through its real entry point where possible);
  - login/logout between two saves, select-screen-only sessions, refine and creature accounts never cached, restore/removal invalidates all, full save resets the cache, epoch bump from another thread mid-save, buffer reuse across saves, every web admin write route reaches the invalidate hook.
- **Phase 2 — dirty marks** at every audited path (one call each, game thread). `Verify` mode, hourly compare.
- **Phase 3 — scratch soak:** bots + `Verify` with verify-all, `SaveDelay=1`, mail/rental/auction/market/friends/GM/web admin edits driven while owners are offline. Pass = 0 mismatches over several hours.
- **Phase 4 — live:** `On` with the hourly compare for a few days (watch `mismatch`). Record before/after in `Future Optimisation/P9-NN-Offline-Account-Cache.md`.

## Alternatives considered
- **Delete bot accounts / run bots on scratch:** fixes today's 56 ms at once with no code; does not help a real server with many offline players. Phase -1.
- **Faster serialiser:** no correctness risk; Phase -1.
- **Spread the serialisation over several ticks:** breaks cross-account atomicity (P9-10 Design).
- **Deep-copy snapshot then serialise off-thread:** more allocation and time than serialising (P9-10 Design).
- **Per-account files / SQLite user data:** the real long-term answer (roadmap P7 covers system data only); a format change far bigger than this.

## Review (2026-10-22)
One read-only review against the code: no Critical. High: H1 admin "Save now" and web admin edits went through the cached periodic path; H2 a full save did not reset the cache; H3 epoch race with off-thread callers; H4 a missed mark breaks cross-account atomicity. Medium: the hourly rebuild should compare (M1), inline Verify (M2), `HideSentMail` confirmed (M3), go straight to the segment list (M4), off-thread `HTTPNewAccount` (M5, safe: new accounts have no blob). Low: friends rationale (L1), null-safe mark (L2), sticky bit at `CompleteLogin` (L3), login marks only on change (L4). All folded in above. It found no clock-dependent bytes beyond refine and creature `Expire` (magic CastTime, buffs, quests, mail, pets, rentals, `UserItem`, `GSpurchases`, `Stats` are all clock-free).

## Open questions
- Hero list size on live; is caching heroes ever worth it?
- How many accounts are "timed" (active refine / expiring creature)?
- What does Phase -1 (faster serialiser, fewer accounts) leave? It may make the cache unnecessary.
