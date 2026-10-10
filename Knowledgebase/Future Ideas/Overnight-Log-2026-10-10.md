# Overnight run log — 2026-10-10

> Autonomous run through `roadmap.md` while the user slept. The user's instructions:
> - work through the suggested order;
> - document important decisions;
> - keep testing efficient;
> - skip and document anything stuck, or anything with 3+ test failures on the same piece of work.
>
> **Nothing here was deployed.** Agents never touch `Build\`. Every "live" gate in the roadmap (watch windows, deploys, operator actions) is listed under **Pending you**.

## Pending you (operator actions and decisions)
- P0 operator mitigations: the GM password, an off-box copy, pruning `Back Up\` (keep 48 h). Checklist in `Knowledgebase/Runbook.md` §5.
- A scratch copy of the live server data (decision #4). The harness has been run on synthetic data only. See P1.
- Every deploy and watch window: P2, P3, P4 in order, one per deploy (roadmap §6).
- Decisions still open: #3 (move build output off `Build\`), #5 (deploy window and backup location), #7, #8, #10, #11. Where I needed an answer to keep going, I used the roadmap's recommendation and recorded it below.

## Decisions made
| # | Decision | Why |
|---|---|---|
| D1 | `Directory.Build.props` defines `ServerTfm`, `ServerWinTfm`, `SharedTfm`, `ClientTfm`, `ToolsTfm`, plus `LangVersion 12`. Every `.csproj` uses them. | Roadmap P0. Verified that every project's `OutputPath` is unchanged. |
| D2 | `tools/verify.ps1` builds each project to `%TEMP%\mir2-verify\<key>`. | Full build of 8 projects plus tests takes ~21 s. Build and Components were confirmed untouched afterwards. |
| D3 | `tools/deploy-with-backup.ps1` deletes only files listed in the previous deploy's manifest, skips suspicious entries, and protects data paths. | Tested on fake folders in scratch, never against `Build\`. |
| D5 | Persistence harness runs on a **synthetic world** by default: accounts (including a zero-character one, which is dropped as expected), items, storage, mail, quests and a Concentration buff with `Buff.Data`. Real data runs via `MIR2_PERSISTENCE_DATA=<folder>`, using a temp copy that is deleted afterwards. | Decision #4 (live data) is still open. The synthetic world covers every load-time special case P2/P3 touch. |
| D6 | `Shared.Tests` pins a newly found latent defect: `S.ChatItemStats` with null `Stats` writes 8 bytes, but the reader always reads a `UserItem`. It is not fixed. | Never sent (the send is commented out at PlayerObject.cs ~7819). It goes in the C5 Shared batch; a Shared change now would force a client patch. |
| D7 | Added projects to the sln by hand with Any CPU configs only. `dotnet sln add` would have added x64/x86 solution platforms. | Keep the sln's Debug/Release Any CPU-only configuration. |
| D8 | P2 "fail closed" uses **evidence**, not a new config flag: a missing `Server.MirDB`/`Server.MirADB` is refused only when backups (`Back Up\Database` / `Back Up\Accounts`) or temp files (`*n`/`*o`) exist. A brand-new server, with neither, still creates empty files. | Decision #10 recommended "refuse + explicit init flag". This gives the same protection with no new Setup.ini key and no change to how a fresh server starts. |
| D9 | `LoadAccounts` throws (rather than returning false) when it refuses. | Keeps every caller correct with no signature change. `StartNetwork` runs outside the save tail, so a throw means nothing is saved over the data. The web admin restore (`ReloadMainAccounts`) already turns exceptions into "failed". |
| D10 | Crash handlers live in `Server.Library` (`Server.CrashReporter`) and only hook `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. `Application.ThreadException` is left alone. | Observe-only, as P2 requires: the ThreadException hook would change WinForms behaviour. Being in Server.Library means a future headless host reuses it. |
| D11 | The GM-password fix skips the chat-log line while `GMLogin` is pending; it does not redact it. | Smallest change: that line is only ever the password attempt. |
| D12 | P3 legacy branch: **skip and reset** (decision #11 recommendation). Pre-v118 BinaryFormatter `Buff.Data` blobs are skipped by length; Concentration's `Interrupted`/`InterruptTime` read back as default (not interrupted). No NRBF package. | No live data to count blobs. The state is a 3-second interrupt flag, so skipping costs nothing and avoids a new dependency. |
| D13 | Typed `Buff.Data` supports bool/byte/short/int/long/float/double/string/DateTime with an append-only `DataTag` byte. An unsupported value type is skipped on save, logged once, not fatal. | Concentration only uses bool + long. Failing every account save over a buff detail would be worse than losing that detail. |
| D14 | Committed synthetic **format fixtures** `Server.Persistence.Tests/Fixtures/v117` (written by the v117 code before the change) and `v118`. `.gitattributes` marks them `-text`. | P3 must prove that real v117 files with BinaryFormatter blobs still load, and v117 files can't be produced once the code changes. The v118 fixture serves the same purpose for P7. |
| D15 | The oracle now prints framework objects (`System.*`, non-collection) as `<Type>` instead of walking their private fields. The v117 oracle was **regenerated by the v117 code** in a scratch worktree at `15cffeb9`; the fixture DB files are unchanged. | On net10 the walk reached Regex internals in `QuestInfo` whose private field names differ between runtimes. They are runtime caches, not data. |
| D16 | WFO1000 (a .NET 9+ WinForms analyzer **error**) on `MineEntry.Range`, `RespawnEntry.Range` and `GuildItemForm.Guild` is fixed with `[DesignerSerializationVisibility(Hidden)]`, not a NoWarn. | No `.Designer.cs` serialises these runtime properties (checked), so no behaviour changes, and the analyzer stays on for new code. |
| D17 | P4 smoke without your hands: (a) a throwaway net10-windows program in `%TEMP%` loaded the prebuilt net472 `CustomFormControl.dll` and the CLR2 VB PowerPacks, built `ListViewNF` + `RectangleShape` and rendered off-screen; (b) the real net10 `Server.exe` ran from a scratch folder on port 17123 with the web admin off, built SMain, loaded the v118 DB and refused to start without maps, with no crash files. Only that process (PID 53376) was stopped. | Your live Server.exe (PID 49664, port 7000) was left alone. The full in-game end-to-end check (login, play, saves, every editor) still needs you. |
| D18 | **Isolation policy** (P5). A throwing connection is disconnected (reason 3, like the old world-crash path). A throwing **player** object is disconnected. Any other throwing object is **quarantined**: its `OperateTime` is pushed 10 min out and it stays where it is, with no despawn/kill logic run on possibly-corrupt state. `Map.Process`, `DragonSystem`, `Envir.Process` and each save step are isolated separately. `WorldErrors` counts and keeps the last 20 errors; logging is capped at 10/s. | The roadmap says "deactivate or despawn". Quarantine is the smallest action that cannot itself throw. Despawning half-mutated monsters would run more code on bad state. |
| D19 | Guild refresh resave no longer deletes every `.mgd` first. It rewrites all guilds (atomic swap per file) and prunes surplus indexes only after **every** refresh write succeeds (a counter in the IO callbacks). Goods/conquest/guild async saves share `CompleteSideFileSave` (File.Replace, logged failures). | `LoadGuilds` only reads `0..GuildCount-1`, so surplus files are harmless until pruned, while the old delete-first approach lost guilds on a crash. |
| D20 | Zero-character accounts are **kept** at load (decision #14 recommendation). | No more silent data loss. Reversible if you prefer pruning (it should be an explicit job). |
| D21 | `@LOGIN` is **not removed** (decision #12 recommended removing it). Instead it refuses while `GMPassword` is still the public default `C#Mir 4.0`, and logs the attempt. | Closes the security hole without taking away a GM workflow you might use. Removing it entirely is still on the table for P8. |
| D22 | `Settings.Save` asymmetry (GMPassword/MaxPacket/MonsterRecall never written): **not changed**. | Latent: the web admin writes those keys directly and re-writes staged values after the exit save. Changing `Save()` without the web admin flags could clobber staged edits. Left for a dedicated change. |
| D23 | **Backup retention** implemented but **off by default**. The `[Backup]` keys are `RetentionEnabled=False`, `KeepAllHours=48`, `KeepHourlyDays=14` and `KeepDailyDays=90` (the decision #13 recommendation). They are editable in the web admin Config page (Database group). It runs after each save, on a background task, and only touches files named `Accounts/Database yyyy-MM-dd HH-mm-ss.bak`. The newest file is always kept. Logs are not pruned yet. | It deletes files and the decision is still yours: turn it on when happy. |
| D24 | Tools retargeted to net10 (`ToolsTfm`). Dropped the redundant `System.Security.Cryptography.ProtectedData` package (NU1510: in-box on net10). AutoPatcherAdmin V1, the legacy HttpServer and PatcherWebSite were **not deleted**. | "Delete if unused" needs your confirmation (decision #15). |
| D25 | CI: `.github/workflows/verify.yml` runs `tools/verify.ps1` on windows-latest, **manual trigger only**. | Private repo: Windows runner minutes cost money. Add a push trigger when you want it. |
| D26 | Found and fixed a bug in my own `tools/verify.ps1`: `-Only A,B` through `powershell -File` arrived as one string, so nothing was built. Earlier `-Only` runs (P2, P5 part 1) only ran the tests. The full verify runs after P3/P4/P5 did build every project, and Server.MirForms didn't change in between, so nothing slipped through. | It now splits commas and refuses unknown keys. |
| D27 | Fixed **all 10 X1 Criticals** now rather than waiting for P8: X1-01 to X1-09 were one-packet world stops (P5 territory) and X1-10 is an item dupe. Each is a guard clause marked `// X1-NN`. Choices: `ChangeHero` still allows index 0; `RetrieveRentalItem` does **not** require a partner, so a stranded deposit can always be taken back; `MarketPage` rejects `page < 0` or a page whose `*10` would overflow. | Small, reviewable fixes. P5 isolation already turns them into single-client disconnects, but the dupe needed closing immediately. No automated tests: there is no handler-level harness yet (that comes with the StressClient in P6). |
| D28 | Started **P8's non-bot items** early (the roadmap allows it after P5). Fixed X1-11, 13, 14, 16, 21, 22, 23, 25, 26, 27, 28 and 29, plus X1-12 via the P6 retry cap. Two of these make **gameplay-visible** changes: game shop stock limits now apply (they were silently ignored by X1-11), and `@SETTIMER`/`@CLEARBUFFS` are GM-only. Door opening needs a door cell within 4 cells. | Each fix is a guard clause marked `// X1-NN` and none touches Shared. Left open with reasons: X1-15 (C5), X1-17 (guild rank rules need your design call), X1-18/19 (throttling design, tune with bots), X1-20 (retire the legacy HTTP service?), X1-30 and X1-24 (mail now validates every item before taking the stamp or any item) fixed afterwards (real IPv6 addresses, pruned IP tables). |
| D29 | **C1 client fixes done (code), not shipped.** Socket-thread decode errors are caught (logged to Error.txt, then disconnect) instead of killing the process; `ReceiveData` returns after disconnect; the keybind parse never throws (key part of "Ctrl + X" bindings compared); looping-sound replay restarts and stop/timeout dispose and remove; settings `MaxFPS` (new `[Graphics] MaxFPS`, min 10), `LevelEffect` default, `ExpandedHeroBuffWindow` save and `[Logs]` save fixed; client `AppDomain.UnhandledException` writes Error.txt. **These ship to players with the C2 launcher release** (roadmap C2). | No Shared change, so no server coupling. |
| D30 | Shaders copied (read-only) from `Build\Client\Debug\Data\Shaders` into `Client/Shaders/` as reference: `grayscale.ps` (ps_1_2, the only live shader) and the unused `shadow_ps.glsl`. `normal.ps`/`magic.ps` don't exist, which confirms those paths are dead. Not added to any build item. | You allowed reading the client folder. The C4 port needs the source. |
| D31 | `--rendertest` golden harness and frame profiler (rest of C1) **not started**. | It needs GPU runs from a scratch client build, plus decision #8 (may agents run the client on this GPU?), which is still open. |
| D32 | **C2 launcher integrity (code), backward compatible.** `PList.gz` gains an optional trailing section (`PatchListExtension`: magic "PLTX", per-file SHA-256 of the uncompressed file, cumulative removed-files list). AutoPatcherAdminV2 writes it. The new launcher verifies a hash before writing a file, rejects rooted/`..`/drive paths, and deletes only listed removed files (never inis, Error.txt or Screenshots, never a file still shipped). Old launchers read count + records and ignore the rest; a throwaway check confirmed this. | The format can't break existing installs. **Not done:** .NET 10 runtime detection (needs a new `ClientTextKeys` string, which is a Shared change, so it's batched); the HTTPS default host (I don't know your patch host, and changing upstream's mirfiles default without a host to test is pointless); retiring AutoPatcherAdmin V1 (decision #15). V1 does not write the section, so lists published with V1 have no hashes. |
| D33 | **Independent threading review** of P2/P5/P6 (`Future Ideas/Review-P2-P6-Threading.md`): no deadlocks or lost wakeups. **Fixed:** H1 (with one-pass, the game thread's own mob list starved: it now has its own full-pass-or-10 ms rule); M1 (`MirConnection.ForceClose` when an isolated Disconnect throws, so the account isn't stuck); M2 (mob deadline stamped from the stopwatch, not tick start); M3 (guild re-index flag cleared only after every write starts); `Saving` is volatile; timer resolution only with IdlePark and always released. **Left (Low, documented in the review):** shutdown doesn't wait for the last async side-file saves; the 10-min quarantine may freeze a boss on a one-off error; no reply to the client for a dropped retry packet. | The roadmap requires two reviews for threading changes: mine plus this one. |
| D34 | **StressClient + scratch world** (background agent, reviewed and committed): synthetic 200x200 map plus `Server.MirDB` generator, `tools/start-scratch-server.ps1` / `stop-scratch-server.ps1` (isolated port, web admin off, status port off, stops only its own PID), StressClient MVP. 200/200 bots reached the game with 0 errors. The scratch profile needs `EnforceDBChecks=False` (the server otherwise demands ~50 special monsters). | P6. Your live Server.exe (PID 49664) was never touched. |
| D35 | Fixed two server issues the bot runs found: (1) `ReceiveData` returned before `EndReceive` once disconnected, so an aborted receive became an unobserved task exception and a crash file on every disconnect storm; the accept callbacks had the same shape on shutdown. (2) The status port was hard-coded to 3100 and always on; it is now `[Network] StatusPortEnabled` / `StatusPort`, and the scratch profile turns it off. | Found by measurement, as intended. |
| D36 | **P7 exporter + independent verifier** (background agent, reviewed and committed): `tools/MirDbExport`, 16 tests, 0 mismatches on the v118 fixture. **Key finding:** `Envir.RespawnTick` (RespawnTimer: base rate, tick counter, options) is saved in Server.MirDB but not in the web admin registry, so a SQLite store of record must add tables for it. Microsoft.Data.Sqlite 9.0.0 pulls a SQLitePCLRaw with an advisory (NU1903): fine for an offline tool, but pin a newer bundle before the store-of-record step. | P7 step 1 only. The storage seam, shadow mode and flip need your live data and a burn-in. |
| D37 | **P8 password hashing:** `Crypto.cs` changed under your approved override. v1 is unchanged byte for byte (tested against the original algorithm). v2 is PBKDF2-SHA256 at 100k iterations, stored as `v2$...` in the same field, and is **always verified** but **only written when `[Security] PasswordHashV2=True` (default off)**, so verification ships before any v2 write. The **login password check now runs on a worker thread** and continues on the game thread via the admin queue (`CompleteLogin`). It re-checks the connection, the stage, a password change and a ban; `LoginPending` stops parallel attempts. Accounts upgrade v1 to v2 on their next successful login once the flag is on. **Before turning v2 on:** move NewAccount, ChangePassword and the storage-password checks off the game thread too; they are synchronous and only cheap while hashes are v1. | P8 order: off-thread verification before a slower hash. |
| D38 | **P9-01 encode-once** (server-only, `[Optimisation] EncodeOnce`, default on): a packet sent to many players in one connection pass is encoded once. Measured at 500 bots: **allocation 135 → 45 MB/s** in every run, with a lower worst iteration and 0 decode errors. The CPU gain (−37 % in the first pair) **could not be confirmed** because the desktop got busy during the second pair. A sampled guard logs any mutate-after-send, and an independent review was clean. Details and table: `Future Optimisation/P9-01-EncodeOnce.md`. | Started P9 with the server-only item: the roadmap says `Shared` items wait for C5, and this one needs no `Shared` change. **Rerun the A/B on a quiet machine** before quoting a CPU number. |
| D39 | **P8 remaining hash sites.** With v2 on, NewAccount and ChangePassword now hash and verify on a worker and finish on the game thread (`CompleteNewAccount` / `CompleteChangePassword`). They re-check that the account doesn't exist yet and that the password hasn't changed meanwhile. With v2 off, behaviour is unchanged. **Storage passwords always use v1:** their checks sit inside many synchronous game-thread handlers, and they are a second factor behind the login. The web admin's set-password goes through `RunAsync` on the game thread and costs one v2 hash per admin action, which I accepted as rare. The legacy `HttpServer` NewAccount runs on its own thread (Known debt). **Verified:** a 200-bot run with `PasswordHashV2=True` created 200 v2 accounts, all logged in, 0 errors, worst iteration 15.9 ms. | This clears the P8 precondition for turning v2 on. Turning it on is still your call (rollback note in D37). |
| D40 | **Follow-up session (user: "do all six").** (1) Knowledgebase drift fixed: server.md save mechanics, settings, threads, observability, tests; AGENTS.md hot path, status port, folder map; Runbook §7 (turning on v2). (2) **X1-15 fixed in Shared** under the approved server-only exception: `Packet.ReadCount` rejects element counts that cannot fit in the packet, no wire change, so the server can deploy alone. **X1-20:** path confinement + no exception echo (retiring the service stays your call). **X1-17:** options note `Future Ideas/X1-17-Guild-Rank-Options.md` (recommend A). | Items 3-6 of the follow-up list continue below. |
| D41 | **P9-02:** players and heroes ran `Process` on every loop iteration. They now run at most every `[Optimisation] HumanProcessMs` (default 10 ms), but never later than their earliest delayed action, so attack, spell and NPC timing stays exact. At 500 bots: **obj 103 → 45 ms/s, allocation 50 → 30 MB/s**, bot attacks and kills comparable. CPU within noise. Heroes' AI inherits the interval and isn't covered by the bots: check them in the smoke test. Details: `Future Optimisation/P9-02-HumanProcessInterval.md`. | Default on with a kill switch, like the other P9 items. |
| D4 | Decision #3 (move build output off `Build\`) **not done**. It is still open and changes your VS F5 folder. | Your call. Until then, `verify.ps1` is the safe path. |

## Every phase at the end of the night (done, or skipped with the reason)
| Phase | State | Why it stopped there / what unblocks it |
|---|---|---|
| P0 | Code done | Operator steps are yours (Runbook §5). |
| P1 | Done on synthetic data | A real-data run needs a copy of your DBs (decision #4). |
| P2, P3, P4 | Code done | **Skipped: deploys and watch windows are user-run only**, one per deploy, in order (roadmap §6). |
| P5 | Code done | Deploy + 7-day watch are yours. Lows from the threading review are documented (D33). |
| P6 | Tools + baseline done | **Skipped:** the 60-minute 500-bot soak (needs a quiet machine and your attention) and the admin synthetic-load card (the StressClient covers it). |
| P7 | Step 1 done (exporter + verifier) | **Skipped:** the storage seam, shadow mode and flip need your live data and weeks of burn-in (roadmap §4). |
| P8 | Code done (v2 hashes off by default) | X1-15 fixed (D40). **Open:** X1-17 needs your pick (options note), retiring the legacy HTTP service (X1-20) is yours, turning v2 on is yours. |
| P9 | P9-01 done and measured | **Skipped:** further items need a quiet machine for honest numbers (D38); `Shared` items wait for C5. The gate is unmet. |
| C1 | Fixes + shaders done | **Skipped:** goldens and the frame profiler. Decision #8 still has only my recommended answer (run `--rendertest` on this GPU from scratch builds), and recording goldens needs a seated, visible GPU session I can't verify unattended. |
| C2 | Integrity done | **Skipped:** .NET 10 runtime detection (a `Shared` string, batched with C5); the HTTPS host (I don't know your patch host); retiring V1 (decision #15). |
| C3 | Not started | **Skipped: blocked by the C1 goldens.** Its gate is "goldens byte-identical", so it can't be verified without them. |
| C4 | Not started | **Skipped: needs C3.** |
| C5 | Not started | **Skipped: needs C4** plus a coordinated client + server flag-day deploy. |
| C6 | Not started | **Skipped: needs C5 and the P9 gate.** |

## Progress by phase
### P0 — done (code parts)
- `Directory.Build.props`, `tools/verify.ps1`, `tools/deploy-with-backup.ps1`, `Knowledgebase/Runbook.md`.
- The AGENTS.md build rules were updated.
- The operator parts are pending you (see above).

### P1 — done (synthetic data); real-data run pending you
- `Shared.Tests` (net8 + net10, 6 tests, ~60 ms):
  - every packet ID has a factory case, with the known gaps asserted exactly (`C.PurchaseGuildTerritory`, `S.RefineCancel`);
  - indexes are unique;
  - **all 433 packet classes round-trip byte-identically**;
  - an append-only `packet-manifest.txt`.
- `Server.Persistence.Tests` (2 tests, ~0.3 s):
  - byte harness (the 2nd save is byte-identical to the 3rd);
  - oracle (two consecutive loads give the same dump, 7.5k lines);
  - a drop report;
  - a Concentration blob count.
- **To run it on your data:** `set MIR2_PERSISTENCE_DATA=<folder with a COPY of Server.MirDB + Server.MirADB [+ Configs]>`, then `dotnet test Server.Persistence.Tests`. The test copies the files to temp and deletes the copy afterwards.
- Background items: the X1 review and packet-mutation audit were handed to a background agent (output in `Future Ideas/X1-Security-Review.md` and `Packet-Mutation-Audit.md`). Still to do: the guild/goods/conquest oracle and model tests.

### P2 — code done; deploy and 48 h watch pending you
- **Account save:**
  - serialised to memory first;
  - written to `Server.MirADBn`, then `File.Replace` (the old file becomes the `Back Up\Accounts` .bak);
  - `Saving` is reset in `finally`;
  - failures are logged and counted (`Envir.AccountSaveFailures`).
- **Shutdown save:** the wait is bounded at 30 s.
- **`SaveDB`:** writes a temp file, then `File.Replace`.
- **Fail closed (see D8/D9):** `LoadDB`/`LoadAccounts` refuse to recreate an empty DB when there is evidence of prior data; `LoadAccounts` refuses out-of-range versions; `StartEnvir` throws when `LoadDB` fails.
- **Crash handlers:** `CrashReporter` (D10).
- **GM password:** no longer written to the chat log (D11).
- **Tests:** 7 fault-injection tests in `Server.Persistence.Tests/CrashSafeSaveTests.cs`. All suites green: 828 web admin, 6×2 Shared, 9 persistence.
- **Old item retired:** the old "process killed mid-save" scenario is covered by the stale-temp-file test. A real kill test needs a live process run, which is part of your deploy watch.
### P3 — code done; deploy and 72 h watch pending you (after P2's watch)
- `Envir.Version` 117 → 118.
- `BuffInfo.cs`: a typed encoding (D13) and a legacy skip branch (D12). Nothing in Server/Client calls BinaryFormatter any more; the Shared helpers stay until C5, so no client patch.
- `Server.Persistence.Tests` no longer sets `EnableUnsafeBinaryFormatterSerialization`.
- **Gate tests:**
  - the v117 fixture loads on v118 with the oracle identical apart from Buff.Data;
  - the upgraded save is v118 and byte-stable;
  - typed values round-trip, and an unsupported value is skipped.
- 13 persistence tests are green.
- **Rollback reminder:** once v118 is written, only a snapshot restore plus P2 binaries rolls back (Runbook §3).

### P4 — code done; deploy (after P3's watch) and 7-day watch pending you
- `Directory.Build.props`: `ServerTfm` net10.0, `ServerWinTfm` net10.0-windows7.0. Shared, Client and the tools stay on net8.
- New warnings reviewed: only SYSLIB0060 on Crypto.cs (left as the NEVER rule says) and pre-existing client MSB3277.
- WFO1000 fixed (D16).
- **Tests:** all suites green on net10:
  - 828 web admin;
  - 14 persistence, including **"v118 fixture written by net8 re-saves byte-identically on net10"**;
  - 6×2 Shared.
- **Smoke tests:** see D17.
- **Docs:** AGENTS.md, server.md and dotnet-upgrade.txt updated.
- **Before deploying:** install the .NET 10 Desktop Runtime x64 on the server host, and keep .NET 8 installed.

### C1 — client fixes and shaders done (code); goldens and frame profiler pending decision #8
- See D29 to D31. The client builds clean.

### C2 — integrity part done (code); runtime detection / host / V1 pending (D32)

### P9 — started
- P9-01 encode-once is done and measured (D38). The next candidates are listed in `Future Optimisation/P9-01-EncodeOnce.md`.
- The gate is not met yet: 2 h soak, 500 bots at ≤50 % of baseline busy, p99 < 50 ms on a quiet machine.

### P8 — hashing + all off-thread password paths done (code); v2 writes off by default
- See D37. `Server.Persistence.Tests/CryptoTests.cs`: v1 matches the original algorithm byte for byte, v2 round-trips, wrong passwords and an empty stored hash fail, `NeedsUpgrade` follows the flag. 26 persistence tests green.
- NewAccount and ChangePassword are off the game thread when v2 is involved, and storage passwords stay v1 (D39). 27 persistence tests are green. The 200-bot v2 run passed.
- **Not done:** X1-15 goes with C5, X1-17 needs a design decision, X1-20 is yours.

### P6 — tools and loop work done; baseline recorded
- Profiler, idle park, one-pass mob threads, load knobs, retry cap, StressClient, scratch world and the baseline script (`tools/run-baseline.ps1`) are done.
- **Gate result so far:** idle scratch server with Multithreaded on uses **0.5 % of one core** (gate < 5 %; about 200 % before).
- **Baseline recorded:** `Future Optimisation/Baseline-2026-10-10.md` (60 s windows; bots on the same machine). 50 / 200 / 500 bots: 0.5 / 9.4 / 36.8 % of one core, 0 errors, every bot in game. At 500 bots the game thread is ~46 % busy, connection processing is the largest slice (333 ms/s), allocation is ~130 MB/s and the worst iteration is 60 ms (others ~5 ms). These are the targets for P9.
- **Not done:** the admin synthetic-load card (the StressClient covers load), and a 60-minute 500-bot soak (the baseline uses 60 s windows; run longer when you can watch).

### P5 — in progress
- **Done:**
  - world-loop isolation and the mob-thread hang fix (D18);
  - atomic ObjectIDs;
  - isolated-error counters on the web admin `/status` JSON;
  - crash-safe guild/goods/conquest saves (D19);
  - the stale-guilds-on-restart fix;
  - the Disconnect/receive 24 h-ban race fix;
  - zero-character accounts kept (D20);
  - `@LOGIN` default-password guard (D21).
- **Tests:** 17 persistence tests (3 new side-file tests); all suites green.
- **Also done (part 2):**
  - backup retention, off by default (D23);
  - tools on net10 (D24);
  - manual CI workflow (D25);
  - verify.ps1 `-Only` fix (D26).
- **X1 review done** (`Future Ideas/X1-Security-Review.md`, 30 findings) and **all 10 Criticals fixed** (D27). The packet-mutation audit verdict is "safe to cache bytes per instance" (`Future Ideas/Packet-Mutation-Audit.md`).
- **Isolation fault injection** needs a running world. It is covered when the StressClient/scratch-server soak exists (P6); until then it is code-reviewed only.
- Background items: still to do (guild/goods/conquest oracle, model tests, X1 review, packet-mutation audit).
