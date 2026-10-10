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

- Background items: still to do (guild/goods/conquest oracle, model tests, X1 review, packet-mutation audit).
