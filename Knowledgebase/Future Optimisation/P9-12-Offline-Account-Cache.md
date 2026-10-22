# P9-12 — Offline account cache (2026-10-22)

**Status: implemented on branch `acct-save-writer` (not merged), flag `[Optimisation] OfflineAccountCache` default `Off`.** Phase 3 of the lean plan in `Future Ideas/Offline-Account-Cache-Plan.md` (the plan is authoritative; this file records what was built and measured). Follows P9-11 (`P9-11-AccountSave-BufferWriter.md`).
- **No DB change:** Server.MirADB stays byte-identical to a full save (no `Envir.Version` bump, no `Save` signature change).

## What it does
The periodic off-thread save (P9-10/P9-11) still serialises the header, heroes and the global tail every time, but each **offline, unchanged** account reuses its bytes from an earlier save. The game thread builds a segment list (ranges of the reused `BufferBinaryWriter` buffer + references to immutable per-account blobs); the pool thread writes the segments in order through a 256 KB `FileStream` buffer.

## Settings (Setup.ini `[Optimisation]`, web admin Settings › Optimisation)
| Key | Default | Meaning |
|---|---|---|
| `OfflineAccountCache` | `Off` | `Off` = the P9-11 path exactly (and any blobs are dropped); `On`; `Verify` = On + a round-robin window of accounts serialised fresh and compared on every save. Needs `AccountSaveOffThread=True` (the legacy path never caches). |
| `OfflineAccountCacheFullEvery` | 12 | every Nth cached save serialises **every** cached account and compares it with its blob (equal: keep the old array; different: count, log, repair). 12 = hourly at `SaveDelay=5`. |
| `OfflineAccountCacheVerifyPerSave` | 200 | Verify mode window size; ≥ the account count = verify all (soak mode). |

The enum is read by `SetupIniReader.ReadEnum` and written as its name; the web admin catalogue shows it as a select (`enumMembers`). `ConfigTests` now allows the `enum` kind.

## Design as built
- **Data:** `AccountInfo.SaveBlob` (byte[], never mutated, only replaced/nulled), `SaveBlobEpoch`, `SaveHot` (all `internal`, runtime-only; the persistence oracle skips them). `Server/MirEnvir/Storage/AccountSaveCache.cs` holds the thread-safe part (no Envir dependency): the epoch (`InvalidateAll`, Interlocked), the WinForms suspension (`Suspend`/`Resume`), and the published `AccountCacheStats` snapshot.
- **Marks:** `Envir.MarkAccountDirty(AccountInfo)` (static, null-safe, never throws: `SaveBlob = null`); `Envir.InvalidateAccountCache()` (any thread).
- **Hot:** online = `Connection != null` or any character `Player != null`; sticky `SaveHot` set in `CompleteLogin` at the `Connection` assignment, cleared only by a save that serialised the account while it was offline. Hot accounts are serialised fresh and never cached.
- **Timed:** any character with `CollectTime > Envir.Time` (refine in progress) or an intelligent creature with `Expire != DateTime.MinValue` → serialised fresh, never cached (`timed=` counter).
- **Epoch rule (H3):** a pass reads `AccountSaveCache.Epoch` once (Volatile) before its loop, then `Suspended`; blobs are valid only while their tag equals the current epoch; stored blobs get the snapshot. Off-thread callers bump after mutating; `Suspend` increments the suspension before bumping, `Resume` bumps before decrementing.
- **Full saves (H1/H2):** `SaveAccounts()` (shutdown, web admin while stopped, WinForms, LoadAccounts when the file is missing) bumps the epoch when it finishes; `LoadAccounts` bumps at the start and the end; `RequestSaveNow` bumps first, so the operator's save serialises every account.
- **Web admin:** `WebAdminHost.InvokeRouteAsync` sets the AsyncLocal `WebAdminGame.InMutatingRequest` for admin POST/PUT/PATCH/DELETE routes; every delegate such a request runs through `RunAsync`, `RunOrInlineAsync` (both paths) or `Post` bumps the epoch in a `finally` right after it ran, on the thread that mutated (game thread, or inline under AccountLock while stopped). This is the one path every web admin write takes; no route has its own mark. Also `BackupRoutes.ClearMainAccounts`. Side effect: any admin write (config, LiveDb, scripts) makes the next save a full one — rare operator actions.
- **WinForms (Known debt: UI thread edits live accounts):** `AccountInfoForm` and `PlayerInfoForm` call `AccountSaveCache.Suspend()` in `OnLoad` and `Resume()` in `OnFormClosed`: while one is open no blob is used or stored. `GameShop.cs` → `ClearGameshopLog` bumps itself (before and after).

## Mark sites
| Site | Scope |
|---|---|
| `Envir.SaveAccounts()` end, `LoadAccounts` start + end, `RequestSaveNow` | all |
| `Envir.ProcessNewDay` (daily quests of every character) | all |
| `Envir.ClearGameshopLog` (start + end; also from WinForms) | all |
| `Envir.ChangePassword`: ban reset (only when a field changes, L4), v1 password set; `CompleteChangePassword` | account |
| `Envir.Login`: ban reset (only on change, L4) | account |
| `Envir.CompleteLogin`: v2 hash upgrade, wrong-password ban, `SaveHot = true` + mark at the `Connection` assignment | account |
| `Envir.NewCharacter`, `MirConnection.DeleteCharacter`, `MirConnection.StartGame` ban reset | account (hot anyway) |
| `Envir.ReturnRentalItem` (renter + owner, before anything can throw; covers `ProcessRentedItems` and the death-drop returns) | both |
| `MailInfo.Send` (all senders: players, system `MailCharacter`, NPC, auction win, outbid refund, SellNow, get-back, rental return, GameShop gift, web admin market) | recipient |
| `PlayerObject.HideSentMail` | `SentMailEntry.Recipient` |
| `PlayerObject.Die` PK to `LastHitter` (stale reference) | hitter |
| GM `CHANGEGENDER` / `CHANGECLASS` (before the Known defect `data.Player.Name` dereference) | target |
| GM `ARCHIVEPLAYER` (before the removal), `RESTOREPLAYER` (first thing) | all |
| Friend accept (requester), block (their entry), `RemoveFriend` (their entry) | other side |
| `NPCDivorce` (spouse, before the Known `lover` null deref), `MarriageReply` / `DivorceReply` (stored proposer reference) | other side |
| `MentorBreak` (partner, before the Known defect `MentorExp` lines), `LogoutMentor` | partner / mentor |
| Web admin: every admin write (`WebAdminGame`), `ClearMainAccounts` | all |
| WinForms `AccountInfoForm`, `PlayerInfoForm` | suspend while open |

## Phase 0 audit results
- **Stale `PlayerObject` / `CharacterInfo` references:** grep of writes through `x.Info.*`, `x.PKPoints`, `x.WinExp/GainExp/GainGold/GainCredit`, `EXPOwner`, proposals, rental partner, NPC timers and delayed actions. Found and marked: `LastHitter` PK, `MarriageReply`, `DivorceReply` (as the plan listed). Checked clear: NPC scripts act on the triggering (online) player; global NPC timers only expire; player `DelayedAction`s and timers run only while the player processes; group gold / `GroupMembers` are online-only; `ItemRentalPartner` / trade partners are cancelled at logout; `MentorReply` uses `GetPlayer` (online). **Residual risk:** `Sep*` monster AIs call `expOwner.WinExp` without the `Node != null` check `MonsterObject.Die` has; `EXPOwner` is cleared within seconds of the owner's logout, so a write after the next save is unlikely but not impossible (not marked: hot monster path; the hourly compare would log it).
- **WinForms ban buttons (`AccountInfoForm` Day/Week/Perm ban):** confirmed they only set `Banned`/`ExpiryDate`; they do **not** disconnect an online account (unchanged; the suspension covers the cache).
- **`HTTPLogin`** (no callers) and the legacy `HTTPNewAccount` (a new account has no blob, M5) need nothing.
- Known defects left as they are (marks placed before them): Friends count/skip mismatch, `ProcessRentedItems` null `rentingPlayer`, GM `CHANGEGENDER`/`CHANGECLASS` offline dereference, `MentorBreak` zeroing the wrong `MentorExp`.

## Diagnostics
- `[Diag] Save … accounts=N` gains (appended, only when the save used the cache) ` cached= fresh= timed= verified= mismatch= epoch=` and ` compareAll` on a compare pass.
- A mismatch logs once per account (Server log): `[Diag] AccountCache mismatch account=<index> (<id>)…`; the fresh bytes are used and become the new blob.
- Web admin `/status` JSON: `accountCache` = the last `AccountCacheStats` (null until a cached save ran), incl. `MismatchTotal`.

## Verification
- **`Server.Persistence.Tests` 73/73** (new `OfflineAccountCacheTests`, synthetic world + 40 offline accounts): cached = full save byte-identical after mail (`MailInfo.Send`, `MailCharacter`), rental return (`ProcessRentedItems`), auction win (`ProcessAuction`), login-screen ban reset (`Envir.Login`; a second attempt does not mark), friend removal and mentor exp (same writes + mark as the PlayerObject handlers); online, logged-out-since-last-save and select-screen-only sessions; refine/creature accounts never cached; full save, `RequestSaveNow`, `LoadAccounts`, invalidate-all and WinForms suspension reset the cache; an off-thread "editor" mutating + bumping during 40 saves loses nothing; Verify-all and the compare pass catch an unmarked write and repair it; 60 rounds of random operation mixes (incl. full saves and invalidations) with 0 mismatches. A deliberately removed `MailInfo.Send` mark fails 3 of them. Oracle skips the three cache fields.
- **`Server.WebAdmin.Tests` 1035/1035** (new `AccountCacheHookTests`): admin writes bump after each delegate (RunAsync, RunOrInline running and inline, Post, and when the delegate throws), reads / anonymous posts / no-request calls do not, every mutating route except `/api/auth/login|logout` is an admin route, and the host dispatches through `InvokeRouteAsync` (source check).
- **`tools\verify.ps1 -Only ServerLibrary,Server,MirDbExport,ScratchWorld,StressClient,WebRelay`** (scratch build dir): green; Shared 88×2, MirDbExport 21, StressClient 32.
- **Scratch soak** (port 17412, `Verify` with verify-all, `SaveDelay=1`, two waves of 50 bots × 150 s, the second while the first was offline, then idle saves; 7 cached saves): 0 mismatches; the last save verified all 100 offline accounts (`verified=100 mismatch=0`).

## Measurements
Copy of the live `Server.MirADB` (2026-10-22 10:48: 11,812 accounts, 11,730 characters, 5 heroes, 37.59 MB), Release, idle desktop, `OfflineAccountCacheTests.Bench_real_accounts_cached` (`MIR2_ACCOUNT_SAVE_BENCH=<folder>`, `_ROUNDS=30`, `_HOT=400`). Game-thread ms of `BeginSaveAccounts` (serialisation + segment list + queueing the write); the file of each scenario was checked against a full save.

| Scenario | Median ms | Min | Max | First save |
|---|---|---|---|---|
| P9-11 (cache Off) | 13.49 | 11.83 | 93.00 | 92.28 (cold) |
| On, 0 online (11,811 cached, 1 timed) | **1.80** | 1.30 | 7.94 | 50.69 (builds every blob) |
| On, 400 online (11,412 cached, 400 fresh) | **2.02** | 1.52 | 9.36 | 1.64 |
| P9-11 (cache Off) again, 400 online | 12.91 | 11.76 | 17.19 | 11.39 |
| Verify 200/save, 400 online | 2.07 | 1.64 | 2.82 | 38.05 (rebuild after Off) |

- The ~1.8 ms floor is the walk over 11.8k accounts (online/timed checks touch every `AccountInfo` and character) plus ~23.6k segment entries; it is cache-miss bound, not serialisation.
- The first cached save after a start, a full save, a restore, "Save now" or any web admin write costs about one P9-11 save plus copying every blob (~51 ms cold here); then steady state.
- Memory: ~37.6 MB of blobs (small arrays, no references) + the segment list (~570 KB, reused) on top of P9-11's buffer.

## Not done / deliberately left
- **Heroes** are serialised in full every save (v1 plan; 5 heroes live).
- **Precise web admin marks**: one invalidate-all per admin write instead (plan "coarse invalidation"); any admin POST, including non-account ones, makes the next save full.
- **Login path through real `CompleteLogin`** is not driven by tests (needs a live `MirConnection`); the tests reproduce its two writes (`Connection`, `SaveHot`).
- **Stale `EXPOwner` in `Sep*` monsters** (above): not marked.
- Default stays **Off** until the user runs the plan's Phase 3/4 soak and live trial.

## Risks
- A future write to another (possibly offline) account without `MarkAccountDirty` is invisible to cached saves until the next full save/compare pass, and can make the file inconsistent across accounts (lost or duplicated items after a crash: plan "What a missed mark costs"). Mitigations: server.md §3.4 rule, hourly compare (logged), Verify mode, full saves on shutdown/Save now/web admin edits.
- `AccountInfoForm`/`PlayerInfoForm` left open disable the cache (correct, but the saves are full-cost meanwhile).
