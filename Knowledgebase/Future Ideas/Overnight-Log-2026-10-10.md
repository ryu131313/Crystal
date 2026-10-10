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
| D28 | Started **P8's non-bot items** early (the roadmap allows it after P5). Fixed X1-11, 13, 14, 16, 21, 22, 23, 25, 26, 27, 28 and 29, plus X1-12 via the P6 retry cap. Two of these make **gameplay-visible** changes: game shop stock limits now apply (they were silently ignored by X1-11), and `@SETTIMER`/`@CLEARBUFFS` are GM-only. Door opening needs a door cell within 4 cells. | Each fix is a guard clause marked `// X1-NN` and none touches Shared. Left open with reasons: X1-15 (C5), X1-17 (guild rank rules need your design call), X1-18/19 (throttling design, tune with bots), X1-20 (retire the legacy HTTP service?), X1-24 (mail refactor), X1-30. |
| D4 | Decision #3 (move build output off `Build\`) **not done**. It is still open and changes your VS F5 folder. | Your call. Until then, `verify.ps1` is the safe path. |

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
