# Roadmap phase notes

> **What this is:** the detailed working notes behind [`../roadmap.md`](../roadmap.md). The roadmap says *what* and *in what order*; this file holds the *how*, the traps and the evidence for each phase. When a phase starts, copy its section into its own design note in this folder (e.g. `Future Ideas/P2-Crash-Safe-Saves.md`) and grow it there. When the phase ships, move that note to `Archived and Completed/`.
> **Verified:** 2026-10-08 against `FuckingAround` (`5b6145f2`) by a 9-agent survey, a 3-draft / 2-judge design panel and a 5-agent adversarial fact-check. Line numbers are approximate (±30); re-check before relying on one.
> **Rules:** `AGENTS.md`, `Server/server.md` and `Client/client.md` govern. Items marked ⚠ override a named NEVER/ALWAYS rule and need the user's approval (roadmap §8).

**Terms used here**
- **Harness:** byte-level DB round trip. The second save is byte-identical to the first save.
- **Oracle:** a semantic JSON dump of every loaded collection, compared before and after a change. Format changes need it, since bytes legitimately change.
- **X1:** the systematic multi-agent security and crash review in P1.
- **Known debt / Known defect:** as defined in the child docs. Fixing either is a scheduled phase, never a drive-by change.

---

## P0 — Groundwork and operator mitigations

**Operator actions (no code):**
1. **GM password.** Change `GMPassword` from the default `C#Mir 4.0` (`Server/Settings.cs:45`) via the web admin Config page. That page writes `Setup.ini` directly, because the key is `savedBySave:false` in `SettingsCatalog.cs` ~178 and `Settings.Save()` never writes it. Two caveats:
   - Players already online keep the old password until they relog, because `PlayerObject` copies it at creation (`PlayerObject.cs:17`).
   - So change it at low population and follow up with item 4's full restart.
2. **Off-box copy.** Take it with the world stopped, after a manual save. While the world runs there is a window with no `Server.MirADB` on disk, and `SaveDB` truncates `Server.MirDB` in place. Copy:
   - `Server.MirDB` and `Server.MirADB`
   - `Guilds`, `Conquests` and `Archive` (`*.MirCA`)
   - `WebAdmin\admins.dat`
   - `Configs` and `Envir` (Goods lives in `Envir\Goods`)
   - `Maps`, if no other copy exists.
3. **Disk and backups.** Check free disk space and prune `Back Up\`. At defaults that is up to 576 `.bak` files a day: `SaveDelay` 5 min, with both a DB and an accounts backup each time. **Keep at least the last 48 h of `Back Up\Accounts` and `Back Up\Database`** until P2 is live. Those files are the only recovery copy if the P2 chain fires.
4. **Restarts.** Restart the whole process instead of using any in-process world restart: SMain Reboot or Stop→Start, or the web admin's reboot, restart countdown or stop/start (`ServerControlRoutes.cs` ~24-30, ~196-204). The reason: `StopEnvir` never clears `Envir.Guilds`, and the `GuildObject` ctor skips existing indexes, so guilds go stale.
5. **One editor surface at a time** (web admin or WinForms). Both write `Envir.Edit`, and the last writer wins.
6. **Optional:** set `Multithreaded=false` until P5. It needs a process restart.

**Git.**
- Check `Knowledgebase/`, `AGENTS.md` and `tools/` for sensitive content (hostnames, IPs, passwords), then commit them.
- Pre-create agent worktrees from the `FuckingAround` tip, because workflow worktrees branch from `master`.

**`tools/verify.ps1`.**
- Builds each project with `-o <scratch>`, never the `.sln`.
- Refuses any output path under `Build\`.
- Runs every test project.

**`tools/deploy-with-backup.ps1` (user-run only).**
1. Snapshot first.
2. Copy the new binary set.
3. Delete only files listed in the **previous deploy's binary manifest** (for example net10 `runtimeconfig`/`deps` files on a rollback to net8). **Never use a folder mirror.** Game data paths are cwd-relative. The web admin's files (`WebAdmin\admins.dat`, `Logs\WebAdmin`, `Logs\Chat`, `Back Up\WebAdmin`) are relative to the exe folder (`AppContext.BaseDirectory`). Both sets sit next to `Server.exe` when it runs from its own folder, which is the normal case.
4. Never touch:
   - `Server.MirDB*` and `Server.MirADB*`, including the `n`/`o` temp files
   - `Guilds`, `Conquests`, `Archive`, `Back Up`
   - `WebAdmin\admins.dat`, `Exports`
   - `Configs`, `Envir`, `Maps`, `Logs`.

**`Directory.Build.props`.**
- Add `ServerTfm`, `SharedTfm`, `ClientTfm` and `ToolsTfm` (all `net8.0` / `net8.0-windows7.0` today) and `LangVersion 12`.
- No output-path properties. Do not set `AppendTargetFrameworkToOutputPath` globally: `Server.WebAdmin.Tests` builds to `bin\Debug\net8.0`.
- Verify that every output path is unchanged.

**Build output (decision 3, if approved).** In its own commit, after the props change, move the Server, Client, LibraryEditor and LibraryViewer output to a gitignored `out\`. `deploy-with-backup.ps1` then copies from `out\` into `Build\`. Until then, VS F5 and a plain `dotnet build` of those projects still write into `Build\`. `verify.ps1` is the safe path for agents either way.

**Runbook.**
- Pre-deploy: manual save → stop → off-box snapshot → deploy script.
- Watch: ≥3 autosaves, 1 planned restart, and a log review.
- Rollback table: roadmap §6.
- Restore drill: restore a `.bak` into a scratch folder and start a scratch `Server.exe` on it.

**Hand-off pack** (one question round):
- A scratch copy of live data, kept outside the repo and `Build\`.
- The client's `Data\Shaders\*.ps` files, which are not in git.
- Optionally, the client `Data\` folder.
- Weekly hours.
- Answers to the P0–P4 and C1 decisions.

**Freeze list (until stated phase).**
- No new MirCraft game-data editing or live-sync until P7. Any mirror must resolve links in `Envir.Main`, never `Envir.Edit` (WoOOLcraft's H1 bug).
- No new client UI or effects on D3D9 until C4.
- Non-urgent `Shared/` changes wait for C5.
- Gameplay and content work may continue. It goes through the same gates: harness, `Shared.Tests` and a scratch end-to-end run.

---

## P1 — Minimum verification harness

**Deadline-path items (the byte harness gates P2, the oracle gates P3):**
- **`Server.Persistence.Tests`** (new, own `bin`).
  - Loads the scratch `Server.MirDB` and `Server.MirADB` through the real ctors.
  - Runs in an isolated cwd with `Configs\` copied, because the `Settings` static ctor reads `Setup.ini` and `AccountInfo`'s ctor writes `Archive\*.MirCA`.
  - Pins `Envir.Now`, since archiving depends on the clock.
  - Envir statics run in a dedicated xUnit collection.
  - Checks: the second save is byte-identical to the first.
  - Reports what the first load drops or moves:
    - zero-character accounts (silent, `Envir.cs` ~3020)
    - items whose `ItemInfo` is gone (silent, `AccountInfo.cs` ~184, `CharacterInfo.cs` ~198-220)
    - orphan quests (silent, `CharacterInfo.cs` ~273)
    - archived characters (logged, written to `Archive\`).
  - Counts Concentration buff blobs.
  - Documented exception: sets `EnableUnsafeBinaryFormatterSerialization` (test-only) until P3. On .NET 8, BinaryFormatter is disabled for non-WinForms projects.
- **Oracle for `Server.MirDB` and `Server.MirADB`:** a JSON dump of every collection. P3 is a format change, so bytes legitimately differ, and P3's gate compares oracles.

**Gates P4 and every `Shared/` change:**
- **`Shared.Tests`**, multi-targeting `net8.0;net10.0`. Reflection over every packet class checks that:
  - every `ServerPacketIds`/`ClientPacketIds` member has a `GetServerPacket`/`GetClientPacket` case;
  - `WritePacket` → `ReadPacket` round-trips with populated fields;
  - `Index` values are unique and match the enums;
  - private packets are `Observable=false`;
  - the ID enums are append-only against a checked-in manifest (ID → class → field-order hash).
- **Expected failures until C5:** ~~`C.PurchaseGuildTerritory`~~ (factory case added 2026-10-16, Scene Lab UI phase 2.1) and `S.RefineCancel` (no factory case), and the storage-password replies being observable.
- **AGENTS.md, same commit:** add `Server.Persistence.Tests` and `Shared.Tests` to the project table, the "compile every consumer after a `Shared/` change" list and the "ALWAYS run tests" list. AGENTS.md currently says `Server.WebAdmin.Tests` is the only test project.

**Background, agent-only (due before P5, or before P9 for the audit):**
- **Full oracle:** `Guilds\*.mgd`, `Envir\Goods\*.msd`, `Conquests\*.mcd`.
- **Model tests:** `Buff`, `UserItem`, `ItemInfo`, `Stats` and `GameShopItem`. Save, then read back via the ctor at the file version and at `int.MaxValue`. `GameShopItem` has a separate wire ctor/`Save(writer, packet)`.
- **X1 review:** bounded file slices plus adversarial verification. The prompts state the single-writer game-thread model. Scope:
  - `MirConnection` handlers and `ReadPacket` lengths
  - `PlayerObject`/`HumanObject` item, gold and trade paths
  - NPC scripts, saves and login
  - **the MirCraft web admin routes**: path handling, the scripts/files editor, backups/restore, config writes.

  Output is a findings doc, triaged: crash-the-world issues go to P5, server security to P8, protocol to C5.
- **Packet-mutation audit:** find every packet changed after `Enqueue`/`Broadcast`, including observer re-enqueues. This is a precondition for P9's encode-once caching.

---

## P2 — Crash-safe saves (no format change) · Known debt (server.md §3.3/§3.4)

**How account data is lost today** (`Server/MirEnvir/Envir.cs`):
- `BeginSaveAccounts` (~2788) runs on the game thread and moves `Server.MirADB` into `Back Up\Accounts\` (~2803) **before** `SaveAccounts(mStream)` (~2806).
- The bytes are then written async to `.MirADBn`. `EndSaveAccounts` (~2814) renames the file into place and swallows every exception.

Two failure paths follow:
1. **Serialisation throws.**
   1. The exception escapes into `WorkLoop`'s catch (~2189): every player is disconnected and the world stops, but `Saving` stays `true`.
   2. The shutdown tail then calls `SaveAccounts()`, which spins forever on `while (Saving)` (~2568).
   3. The game thread hangs. Any later `Stop()` also hangs, so the operator kills the process.
2. **The write fails, e.g. a full disk.** `EndSaveAccounts` swallows the error and `Saving` returns to false, but there is no `Server.MirADB` on disk until a later save succeeds.

Either way, the next start finds no `Server.MirADB`. `LoadAccounts` (~2981) **immediately writes an empty one and loads it**. The real data survives only as the newest `Back Up\Accounts\*.bak`, which later autosaves bury.

`LoadDB` (~2843) also creates an empty `Server.MirDB` when it is missing. `SaveDB` (~2467) truncates `Server.MirDB` in place on the game thread.

**Work:**
- **Account save:**
  - Serialise to memory first.
  - Write `.MirADBn`, then `File.Replace(new, live, backup)`, so a live file exists at every instant.
  - Reset `Saving` in a `finally`.
  - Log `EndSave*` errors and count them on the dashboard.
- **`SaveDB`:** write to a temp file, then `File.Replace`.
- **Fail closed:**
  - `LoadAccounts` and `LoadDB` refuse to create an empty DB when backups or `n`/`o` files exist. A brand-new server needs an explicit init flag.
  - `Main.StartEnvir` honours `LoadDB()`'s return value (~3440 ignores it today).
  - `LoadAccounts` refuses `LoadVersion > Version`. Only `LoadDB` does this today (~2859).
- **Crash handlers** that log and flush:
  - `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`.
  - `Application.ThreadException` is *not* observe-only: attaching it replaces WinForms' default dialog. Either decide `SetUnhandledExceptionMode` explicitly, or leave it out.
- **GM password logging.** `PlayerObject.Chat` (~1900) logs the chat line before the `@LOGIN` comparison (~1904). Move the log call below the check, or redact it.

**Gate:**
- The harness is byte-identical.
- Fault injection at every step of both saves (including a forced serialisation throw and a write failure) leaves the newest complete file loadable and never creates an empty DB.
- A forced throw leaves `Saving=false` and the live file intact.
- A v118-stamped file is refused.
- 48 h clean live.

---

## P3 — BinaryFormatter out, `Envir.Version` 117 → 118

**Facts:**
- `Functions.SerializeToBytes`/`DeserializeFromBytes` (`Shared/Functions/Functions.cs` ~435-456) wrap `BinaryFormatter` under `#pragma warning disable SYSLIB0011`, so builds won't warn.
- The only callers are `Server/MirDatabase/BuffInfo.cs` ~193 (load) and ~222 (save).
- The only `Buff.Data` writer is Archer Concentration (`HumanObject.cs`), which writes two entries. Concentration is not `RemoveOnExit`, so blobs persist in `Server.MirADB`.

**On .NET 9+ the in-box BinaryFormatter always throws `PlatformNotSupportedException`.** Only the unsupported `System.Runtime.Serialization.Formatters` compat package restores it, and this plan does not use it. The consequences:
- **Load:** any stored blob makes `LoadAccounts` fail, and the world won't start (`StartNetwork` calls it outside the inner try).
- **Save:** the first autosave with an active Concentration buff throws into the P2 chain.
- It works on .NET 8 only because the SDK keeps BinaryFormatter enabled for WinForms apps.

**Work:**
- **Typed encoding:** key + type tag + bool/int/long/string, in `BuffInfo.cs`.
- **Version gate:** `Envir.Version` 118, gated per server.md §4.3.
- **Legacy branch (<118):** skip the length-prefixed bytes and reset Concentration's `Interrupted`/`InterruptTime`, logging the count. Use `System.Formats.Nrbf` only if P1 found meaningful live state.
  - The legacy path never calls BinaryFormatter, so old backups stay loadable on net10 and by the P7 importer.
- **Leave the Shared helpers** until C5, so no client patch is needed.
- **Same commit:**
  - remove the test-only unsafe flag;
  - add a Buff round-trip test;
  - update the server.md version notes.

**Verify:**
- v117 → load → save as v118 → reload, and compare oracles: they must be equal apart from the documented Buff fields.
- Repeat on several older `.bak` files.

**Deploy:** at low population after a manual save and a snapshot. Archive the build as P4's same-format rollback.

**Rollback:** restore the pre-deploy snapshot with the P2 binaries. This loses everything written since the deploy; P2 refuses v118 files.

---

## P4 — Server side to .NET 10 · ⚠ AGENTS.md "do not retarget"

**TFM bump:**
- `ServerTfm` → `net10.0` / `net10.0-windows7.0` for Server.Library, Server.exe, Server.WebAdmin.Tests and Server.Persistence.Tests.
- `Shared` stays `net8.0`: a net10 app can reference a net8 library, and `Shared.Tests` on both TFMs proves the same IL behaves.
  - WoOOLcraft is **not** a precedent here. Its net10 port moved its server-side wire library to net10, and only the client-side mirror stayed net8.
- `LangVersion` stays 12.
- `Crypto.cs` is untouched; leave its SYSLIB0060 warning (server.md §3.4 NEVER).

**Things to review:**
- New analyzer and NuGet-audit warnings.
- .NET 9+ saturating float→int casts, as behaviour changes.

**Scratch validation:**
- The 474 web admin test methods (425 facts and 49 theories; about 810 cases as `dotnet test` reports them).
- The persistence harness and oracle.
- `Shared.Tests` on both TFMs.
- Net8 vs net10 saves byte-identical on the live copy.
- End to end with the current net8 client: login, play, 2 autosaves, a restart, and the web admin flows.

**Legacy binaries to check:**
- **Prebuilt net472 `Components\CustomFormControl.dll` (`ListViewNF`).** It backs the **SMain main window itself** (players and guild lists, `SMain.Designer.cs` ~49/56), plus `AccountInfoForm` and `PlayerInfoForm`. If it fails to load, Server.exe can't open its main window.
- **VB PowerPacks (CLR 2):** VisualMapInfo (`VForm`, `MineEntry`, `RespawnEntry`).

**Contingency:** point Server.exe at the `CustomFormControl` ProjectReference, and/or redraw the VisualMapInfo shapes with GDI+.

**Host:**
- Confirm the Windows version is supported.
- Install the .NET 10 Desktop Runtime x64 side by side.
- Keep .NET 8 through the rollback window.

**Docs (same commit):**
- AGENTS.md: the TFM table, prerequisites, and the "do not retarget" rule.
- `dotnet-upgrade.txt`.
- The server.md build sections.

---

## P5 — Safety net, tools to .NET 10, CI · Known debt (server.md §3.3) · ⚠ AGENTS.md "do not retarget" (tools)

**World-loop isolation:**
- **X1 crash-the-world findings** (triaged in P1), each with a fault-injection test.
- **Per connection:** try/catch around `Process`/`ProcessPacket`. Log the account, character and packet type, then disconnect that client only.
- **Per object, per `Map.Process`, per `Envir.Process` block:** log the identity, then deactivate or despawn the failing object. Never continue with a half-mutated object.
- **`ThreadLoop`.** The catch sits **outside** the `while` loop (~2237-2300), so the thread exits. Today the game thread then spins in `ThreadLoop(MobThreads[0])` waiting for a `Stop` that never comes.
  - Fix: a per-object try/catch **inside** the loop, so the thread survives.
  - On a fatal failure, mark the MobThread dead so `WorkLoop` leaves it out of the barrier, and have the game thread (or a restarted thread) take over its `ObjectsList`.
  - Setting `Stop=true` alone fails: the next iteration resets it.
- **`Interlocked` ObjectIDs.**
- **Dashboard:** an error counter plus the last N errors.
- **Docs:** server.md §3.3 turns this Known debt into a rule.

**Save windows:**
- `SaveGuilds` with `GuildRefreshNeeded` deletes every `.mgd` before resaving (~2636). Write first, then replace.
- The guild, goods and conquest saves (`EndSaveGuildsAsync`/`EndSaveGoodsAsync`/`EndSaveConquestsAsync`) move their files with `File.Move` (~2679, ~2742, ~2778) and swallow every exception. Use `File.Replace` and log failures.
- Apply the agreed retention policy to `Back Up\` and `Logs\`.

**Defects:**
- Stale `Envir.Guilds` after any in-process restart.
- Zero-character accounts dropped at load (decision needed).
- The Disconnect/`_receiveList` race.
- `@LOGIN`: remove it, or add an attempt limit (decision needed).
- **`Settings.Save` asymmetry** (`GMPassword`, `MaxPacket`, `MonsterRecall*` are read but never written). This is **latent**: the web admin writes these keys directly (`SettingsCatalog` `savedBySave:false`) and re-writes staged values after the exit-time `Settings.Save()`. Fix it together with those flags and `ConfigRoutes`' staged rewrite, or exit could overwrite staged web edits.
- Gameplay-changing fixes (e.g. the drop-line numerator, which makes `n/d` behave as `1/d`) need a separate balance decision.

**Tools (agent background):**
- Move AutoPatcherAdminV2, LibraryEditor (x64), LibraryViewer and CustomFormControl to net10.
- Bump or drop `ProtectedData`.
- Delete the legacy `HttpServer` if unused. It is opt-in (`StartHTTPService=false`), serves `/newaccount`, `/addnamelist` and `/broadcast`, and touches `Envir.Main` from the HttpListener thread.
- Delete the uncalled `Envir.HTTPLogin`.
- Move PatcherWebSite out of the sln.
- AutoPatcherAdmin V1 is owned by C2.
- Tools must reach net10 before C5, because LibraryEditor references `Shared`.

**CI:** GitHub Actions on a Windows runner calls `verify.ps1` and every test project, with synthetic fixtures only.

**AGENTS.md:** the test projects were added in P1. P6 adds the StressClient to the same lists.

---

## P6 — Measurement and load infrastructure

**Loop profiler.** Today there are only coarse counters: iterations/s (`LastCount`/`LastRealCount`), cycle delay (`LastRunTime`) and per-mob-thread `LastRunTime`. They are shown in SMain and the web admin.
- **Accumulators** (allocation-free):
  - an iteration histogram;
  - ms/s per phase: connections, mob barrier, objects, maps, `Envir.Process`, admin drain, saves;
  - packets and bytes, and queue depths;
  - allocation and GC-pause deltas;
  - save ms, including the account serialisation on the game thread.
- **Output:**
  - a gated once-per-second `[Diag] Loop` line, with fields only ever appended;
  - dashboard tiles;
  - a web admin health endpoint (error counters, save failures, backup count, free disk) that can replace the hard-coded status port 3100.
- **Logging** stays inside log4net 3.3.0 (server.md §5.2).

**⚠ Idle park and mob-barrier rework.** This overrides server.md §3.2 "NEVER add blocking I/O, Thread.Sleep or waits on the game thread". Amend §3.2 in the same commit.
- **Game loop:** waits at most 1 ms on an `AutoResetEvent` with `timeBeginPeriod(1)`, woken by receive, accept and `QueueAdminAction`.
- **Mob threads:** do one pass per tick, then wait on a barrier instead of re-walking until `Time + 10` (~2111).
- **Cadences:** respawns, doors and lightning run on cadences.
- **Today:** the `Thread.Sleep` is commented out (~2185), so idle burns about 2 cores with the defaults `Multithreaded=true`, `ThreadLimit=2`.
- **Review:** two independent reviews; it ships as its own deploy.

**Load-test knobs** (all scratch-only, default off):
- **Per-IP exemptions.** Make `IPBlockSeconds` configurable (hard-coded 5 s, never read from the ini). A loopback exemption covers:
  - the per-connect IP block;
  - `MaxIP`;
  - the account-per-IP block;
  - the **character-per-IP 24 h block** (`Envir` NewCharacter, >4 per IP per hour).
- **Throttles.** Keep `MaxPacket` **enforced**, at a raised loopback threshold, never exempt (server.md §3.1 NEVER let a per-connection queue grow unbounded). In this phase also cap `_receiveList` and `_retryList` (drop, then kick).
- **Decode-error ban.** Optionally exempt the 24 h decode-error ban on loopback.
- **Profile.** A documented `loadtest` Setup.ini profile:
  - raises `MaxUser` (default 50);
  - sets `AllowStartGame=true` and `CheckVersion` off, or a matching hash.
  - Bots create a character before any save, or the zero-character pruning deletes them.
- New keys go through `Settings.Load`/`Save` and `SettingsCatalog`.

**Admin synthetic-load card:** capped, off-by-default spawn/kill bursts via `QueueAdminAction`.

**StressClient MVP:**
- **Shape:** a net10 console tool on `Shared` (`Packet.IsServer=false`), in the sln, with its own output folder. It never hand-rolls packet bytes and is rebuilt after every `Shared/` change.
- **Features:**
  - a login state machine (`C.ClientVersion` → login or new account → new character → StartGame);
  - a ramp and a fleet connect budget;
  - a per-bot token bucket under the raised `MaxPacket`;
  - a roam-and-fight brain and deterministic identities;
  - a 1 Hz metrics line;
  - per-behaviour counters from iteration 0 (WoOOLcraft found its first bots mostly wandered);
  - detection of refused connections.
- **Target:** it drives a scratch Server.exe.
- **Size:** ~1.5–2.5k lines (WoOOLcraft's v1 was 3.7k).
- ⚠ Use WoOOLcraft StressClient patterns only with decision 6 (roadmap §8).
- When it lands, add a `Knowledgebase/StressClient/` doc, and add the StressClient to the AGENTS.md project, build and test lists.

**Baseline matrix** (after the idle park), stored in `Future Optimisation/`:
- idle, with Multithreaded on and off;
- 50/200/500/target bots, spread vs one map;
- a kill-heavy run;
- the save spike.

Add a regression script with relative thresholds.

---

## P7 — SQLite for system data · Known debt · ⚠ AGENTS.md "new saved fields bump `Envir.Version`"

> **Plan written 2026-10-20** (P7 step 1, the exporter and verifier, is done: `P7-SQLite-Export.md`). Four phases, each one deploy or one operation, each with one smoke check and at most two Opus 5.5 medium-effort subagents (memory: lean plans and testing). Line numbers checked 2026-10-20 against the working tree (±30). Reviewed the same day by one Opus 5.5 subagent; its findings are folded in.
>
> **No time gates (user, 2026-10-20).** The server isn't open to players yet, so no phase waits for days or weeks on live. Every gate below is a set of checks (tests, a scratch soak, a drill) that is done when it passes. This overrides the P7 waits in `roadmap.md` ("≥1 week shadow, ≥4 weeks burn-in") and its §7 soak classes for P7.
>
> **Status:** 7.1 done 2026-10-20 (`1d1b2403`). 7.2 done 2026-10-21 (`2da35a92`). 7.3 done 2026-10-21 (`4800c1a8`; the live flip is your deploy). 7.4 done 2026-10-21 (code, uncommitted).

### Decisions this plan relies on

**Already made:**
- **Scope (widened 2026-10-20).** The file-based design data comes in too: RandomItemStats, Mines, Routes, drops, quest bodies, recipes, exp and stat tables, the `*System.ini` files and the game half of `Setup.ini`. The inventory and the order are in `P7-SQLite-Export.md` § "Scope widened 2026-10-20". Accounts, guilds, goods and conquests stay binary. NPC scripts and `.map` files stay files.
- **Schema changes (⚠, roadmap §8).** A new system-data field becomes an additive column plus a `meta.schema_version` bump, with no `Envir.Version` bump. Accounts, guilds, goods and conquests keep the `Envir.Version` rule. This takes effect at the retire commit (7.3 step 5). Until then the schema is **frozen**, so `--export-binary` can always write everything back, and the old rule holds.
- **Provider and journal:** `Microsoft.Data.Sqlite`, rollback journal (not WAL), in Server.Library only.
- **Editors:** the WinForms editors keep working through the seam. Retire one only after a capability parity audit.
- **Load side effects** (game-shop drops, duplicate-spell drops, `FillMagicInfoList`, archiving): preserved inside the seam, unchanged.

**Proposed here (confirm at the start of the phase named):**

| # | Decision | Recommendation | Phase |
|---|---|---|---|
| D1 | Shadow check | The server only writes the mirror and records the SHA-256 of the binary bytes in its `meta`. The check is offline: `MirDbExport roundtrip` loads the mirror in its own process, writes binary and compares with the file (live or `Back Up\Database`) that has that hash. No online cell compare: it would compare a snapshot with itself. Entity work never runs on a worker thread, because the binary model ctors read `Envir.Main/Edit` lists and the static `Envir.LoadVersion` (`MapInfo.cs` ~63, `MonsterInfo.cs` ~62) | 7.2 |
| D2 | Child-row keys | `PRIMARY KEY (parent_index, position)` for the positional children; drop the `parent*10000+pos` synthetic key from the store schema (keep it in the step 1 export for its tests) | 7.2 |
| D3 | `MagicInfo` rows with `Spell == 0` | Keep them: one more row in `magic` with key 0. Dropping them is a silent defect fix | 7.2 |
| D4 | Full rewrite vs row diff per save | Full rewrite of the design tables in one transaction per save (the file is a few MB). The three linked lists are not in it: each has its own save job (they are process-wide statics, see 7.2). Row-level saves (`__rev`) are LiveDb-lite work, after P7 | 7.2 |
| D5 | Package version | Newest stable `Microsoft.Data.Sqlite` 10.0.x (clears NU1903), or 9.0.0 plus a pinned fixed `SQLitePCLRaw.bundle_e_sqlite3`. Move `tools/MirDbExport` to the same version | 7.2 |
| D6 | Old-backup importer | No reimplementation of the ~50 version gates. Keep `Envir.LoadDB` (the reader) for good and import through it offline, as `MirDbExport` already does. Only the `Envir.SaveDB` body is deleted; the models' `Save(BinaryWriter)` methods stay (the byte harness, `MirDbExport` and `--export-binary` need them) | 7.3 |
| D7 | Optional files (`DisabledChars.txt`, `LineMessage.txt`, `Notice.txt`, `SET [].txt`, GETRANDOMTEXT files) | Leave them as files; not in P7 | 7.4 |
| D8 | `Setup.ini` split | The `[Game]` and `[Items]` keys go to a `settings` table; everything else stays in the ini. The exact key list is agreed in 7.4 before any code | 7.4 |

### Rules this phase touches (extract before coding, AGENTS.md directive 3)

- **Threads (server.md §3.2):** the game thread never blocks on SQLite, never waits on the writer and never takes a new lock. It only posts to a queue. The writer thread is new: add it to the §3.2 async-zones table in the commit that adds it.
- **Lock order** stays `EditLock` → `LoadLock` → `AccountLock` (`Server/WebAdmin/README.md`). **Invariant:** the writer thread never takes `EditLock`, `LoadLock` or `AccountLock`, and never posts to the game thread. That makes it safe to wait for the writer while holding any of them (the web restore route holds `EditLock` and `LoadLock` around `ReloadEditDatabase`, `BackupRoutes.cs` ~413-424, and `LoadDB` takes `LoadLock` itself).
- **WinForms forms** call `SaveDB()` on the UI thread with no `EditLock` and no `RunFileIo` (e.g. `MapInfoForm.cs` ~44). After the flip they block the UI thread for one commit (milliseconds). Accepted; it isn't the game thread.
- **Persistence (server.md §3.4, §4.3):** binary layouts don't change in P7 (no `Envir.Version` bump), so the harness stays byte-identical in every phase. `Server.Persistence.Tests` after every phase.
- **Logging (server.md §5.2):** log4net 3.3.0 only. New lines are `[Diag] Shadow …` and `[Store] …`, with fields only appended.
- **Build (AGENTS.md):** `tools\verify.ps1` only, never into `Build\`. Deploys are user-run (`tools\deploy-with-backup.ps1`). Server.exe is AnyCPU, so the native `e_sqlite3.dll` (`runtimes\win-x64\native\`) must reach the run folder: `verify.ps1`'s server build fails if it's missing from the output, and the deploy script copies `runtimes\`.
- **Known debt fixed by name in this phase** (approve at phase start, directive 5): Main's periodic design `SaveDB`, `SystemDataStale`, `GameDataSave.RunFileIo`'s game-thread branch, the GTMap block, the File.Replace backup of `Server.MirDB`. Nothing else.

### Phase 7.1 — Storage seam (binary only, its own deploy)

**Goal:** the save/load code moves behind an interface with **no behaviour change**. After this, adding a backend is one class.

**Work:**
1. **`IWorldStore`** in `Server/MirEnvir/Storage/`:
   - `LoadDesign(Envir target)`, `SaveDesign(Envir source, string backupFileName)`, `SaveRuntime(Envir source)` (the respawn tick counter).
   - Accounts, guilds, goods and conquests are **not** in the interface. Their code stays in `Envir` (it stays binary for good).
2. **`BinaryWorldStore`:** moves `SaveDB(string)` (`Envir.cs` ~3082-3170) and `LoadDB` (~3573-3702, all of it system data; accounts are in `LoadAccounts`) out of `Envir` verbatim.
   - `SaveDB()` / `SaveDB(string)` / `LoadDB()` stay as one-line forwarders, so the 29 `SaveDB()` calls in 19 files (18 in 13 WinForms forms, 11 in 6 web admin files; tests and `tools/scratch-world` add 4) and SMain's Edit `LoadDB` don't change. Re-count them at phase start.
   - Serialise to a `MemoryStream`, then write the file (the P2 temp-file + `File.Replace` swap stays). The bytes are what shadow mode (7.2) fingerprints.
   - `LoadVersion` stays set under `LoadLock`. The F4 published respawn tick (`_publishedRespawnTick`, ~3138) moves with the code unchanged.
3. **`WorldBinder`:** the post-load bindings, called after `LoadDesign` for every backend, in today's order:
   - `FillMagicInfoList` / `UpdateMagicInfo`, `BindGameShop` (keeps dropping unbound items, now with a log line per dropped item), `ItemInfo.RandomStats`, `SafeZoneInfo.Info`, `Settings.LinkGuildCreationItems` (~3698), and the quest / drop / dragon `.txt` loads.
   - Write down the current call order (including where `Settings.LoadRandomItemStats` / `LoadMines` run relative to `LoadDB`) in a comment at the top of the class. 7.2 and 7.4 depend on it.
   - **Two traps to keep:** `BindGameShop` always binds to `Edit.ItemInfoList`, even for Main (`Envir.cs` ~5813, server.md ~443), so Edit must load before Main. `UpdateMagicInfo` is gated on the static `LoadVersion <= 70` (~3668), so a non-binary load must set `LoadVersion = Version` under `LoadLock`.
4. **Selection:** `Envir.Store` is set once at startup from `[Database] Backend` (only `Binary` accepted in 7.1; any other value refuses to start with a clear message).

**Tests and smoke check:**
- `tools\verify.ps1` (all keys). `Server.Persistence.Tests` byte harness and oracle unchanged on the v118 and v119 fixtures. `Server.WebAdmin.Tests`.
- New test: `WorldBinder` runs the same bindings in the same order for a fixture as the old `LoadDB` (oracle equal).
- **Smoke:** a scratch server (`tools\start-scratch-server.ps1`), one web Game Data save, one restart, oracle of the restarted world equals the oracle before.

**Subagents (1):** the main session does the move (it's mechanical). **Reviewer** (server persistence): diff the moved code against the old `Envir.cs` region line by line, list any behaviour change, and check that no `SaveDB`/`LoadDB` caller changed thread or lock.

**Gate and rollback:** the tests and the smoke check pass. Rollback: previous binaries.

**Done 2026-10-20 (uncommitted).** `Server/MirEnvir/Storage/{IWorldStore,BinaryWorldStore,WorldBinder}.cs`; `Envir.SaveDB(string)` / `LoadDB()` are forwarders; `Server.Persistence.Tests/WorldBinderTests.cs` (2 tests). `tools\verify.ps1` passed (all builds; WebAdmin 983, Shared 73 ×2, Persistence 38, MirDbExport 19). Smoke: a scratch server booted, stopped and rebooted on the moved load path (the scratch stop doesn't save, so the save path is proven by the persistence byte harness, not the smoke run). One reviewer: no must-fix; a partial-load note was added to `WorldBinder`. Differences from the plan above, all to keep 7.1 free of behaviour changes:
- `IWorldStore` has only `LoadDesign` and `SaveDesign`. `SaveRuntime` comes with the SQLite store (7.2), where it has a meaning.
- `Envir.Store` is a static `BinaryWorldStore`; the `[Database] Backend` key comes in 7.2 with its second value.
- The save still streams straight to the temp file (no `MemoryStream`): 7.2's shadow hash can be computed while writing.
- `BindGameShop` drops still aren't logged.
- The quest / drop / recipe / dragon `.txt` loads stay in `StartEnvir` (they were never in `LoadDB`); `WorldBinder` holds only what `LoadDB` bound.

### Phase 7.2 — `SqliteWorldStore` and shadow mode (deployed with `Backend=Binary`, `Shadow=true`)

**Goal:** the whole SQLite store is built and exercised on live data every save, while the binary file stays the store of record.

**Work:**
1. **Package (D5)** in Server.Library. Confirm the native DLL lands in a `verify.ps1` server build output.
2. **Schema v2** (`meta.schema_version = 2`), built from the step 1 schema:
   - The 19 registry tables, with D2's keys for the map and conquest children.
   - `respawn_tick (BaseSpawnRate)` and `respawn_tick_options (position, UserCount, DelayLoss)`: design data missing from step 1.
   - `runtime_state (name TEXT PRIMARY KEY, value ANY)`: `RespawnTick.CurrentTickcounter` (ulong; error if > `long.MaxValue`). Only Main writes it.
   - `index_counters` as loaded, never recomputed. `meta` as step 1, plus `source` (`import` / `save`).
   - **The three linked files** (rows point at them by position or name, so they move with the DB): `random_item_stats` (position = today's `RandomStatsId`), `mines` (keyed by the same position the mine zones use today), `routes` + `route_points` (keyed by route name for now; `RespawnInfo.RoutePath` stays a name until 7.4 replaces name links).
   - **Sentinels are NULL links, not errors:** `RandomStatsId = 255` (new items, `Envir.cs` ~5584) or any id past the list (`LoadDB` skips it, ~3638), mine index 0 ("no mine": `Map.cs` ~705 uses `MineIndex - 1`), and an empty or missing route (`MapRespawn.LoadRoutes` returns silently and skips bad lines, `Map.cs` ~2720-2732).
   - **The three lists are process-wide statics** (`Settings.RandomItemStatsList`, `MineSetList`), mutated in place from web threads (`BalanceRoutes.cs` ~530) and forms (`MiningInfoForm.cs` ~150) and read live by Main. Each gets its own save job, encoded on the editing thread. `Settings.Load` (`Settings.cs` ~684) runs before any store is open: either open the store first, or defer those loads until it is (decide from the auditor's list).
   - **Routes** are preloaded into memory at design load. `MapRespawn`'s ctor (`Map.cs` ~2701) and `LiveDbHooks.cs` ~478 read that cache; the game thread never queries the store.
   - The GTMap block is not carried (written, never read). Main writes a non-empty one (built in `StartEnvir`, ~4382-4403) and Edit an empty one, so the byte round trip compares up to the GT trailer.
3. **`SqliteWorldStore : IWorldStore`:**
   - **One writer thread** owns the only connection (`Pooling=False`, rollback journal, `synchronous=FULL`). Jobs come through a `BlockingCollection`. Each save job is one transaction: delete and re-insert the design tables (D4).
   - **Encode** on the calling thread, into a detached `WorldRows` snapshot (primitives, strings, `int[]`; no model references), using the step 1 encoding rules. The writer only sees `WorldRows`.
   - **Decode** builds the model objects directly (no binary ctors; the only static it sets is `LoadVersion = Version`, under `LoadLock`), then `WorldBinder` runs as for the binary store. Loads go through the writer thread too, so they are ordered after pending saves. Loud decode: an unknown column, a NULL in a NOT NULL column or a type mismatch fails the load with table, key and column. A link that is neither valid nor a sentinel is logged with table, key and column and counted, but doesn't fail the boot (today's loader skips it too).
   - **File rules:** refuse a missing or 0-byte file unless importing; `PRAGMA integrity_check` on open; `.tmp` + move for the first creation.
   - **Import** (`--import-binary`, or first boot with `Backend=Sqlite` and no `.db`): load `Server.MirDB` through `BinaryWorldStore`, save through `SqliteWorldStore`, run the verifier against it, and only then move the `.db` into place. `Server.MirDB` is left untouched as the rollback. **Automatic import only when there is no prior `.db` evidence** (no `.db` in `Back Up\Database`, no `.db.tmp`), mirroring `HasPriorDataEvidence` (`Envir.cs` ~3579). Otherwise refuse to start, so a lost `.db` never silently re-imports a stale `Server.MirDB`.
   - **Export** (`--export-binary <folder>`): load from SQLite, write `Server.MirDB` through `BinaryWorldStore` **and** the three linked files (`RandomItemStats.ini`, `Mines.ini`, the route `.txt` files) through their existing `Save*` code. Never overwrites existing files without `--force`.
   - **Backups and restore (code here, switched on at the flip):** `VACUUM INTO 'Back Up\Database\<name>.db'` on the writer thread, with today's retention (check that `BackupRetention`'s file patterns match `.db`). The web admin `BackupRoutes` backup asks the writer. Its restore route (`SwapIn`, `CheckBackupHeader`, `ReloadEditDatabase`, ~418-445) gets a store path: the writer closes the connection, the file is swapped, the writer reopens it and runs `integrity_check`, then Edit reloads. While `Backend=Binary`, both stay as they are today.
4. **Shadow mode** (`[Database] Shadow=true`, Backend=Binary):
   - After every binary design save (Main's periodic save, Edit saves from the web and from the forms), the saving thread builds `WorldRows` and posts it with the SHA-256 of the binary bytes. The linked lists post their own rows after their saves. Cost on the game thread: one encode, measured by the P6 profiler as part of `save ms`. Budget: under 5 ms p99. If it's over, shadow only Edit saves (with LiveDb, Main skips its design saves once Edit has saved anyway).
   - The writer writes `Server.MirDB.shadow.db` with the hash in `meta.source_sha256`. Output: a `[Diag] Shadow` line per save (encode ms, write ms, rows); a write failure logs at error level.
   - **The check (D1):** a new `MirDbExport roundtrip <folder>` loads the shadow `.db` through `SqliteWorldStore` in its own process, saves it through `BinaryWorldStore` to a temp file and compares the bytes (up to the GT trailer) with whichever of `Server.MirDB` and `Back Up\Database\*` has the recorded hash, so it never races Main's next save. It compares the linked files too. It runs at the end of the 7.2 smoke and the 7.3 soak; on a mismatch it lists table, key and column (the step 1 verifier's output).
5. **Byte-identical round trip** (from `P7-SQLite-Export.md` "Next steps" 3): binary → SQLite → load → binary must give the same bytes. Handle explicit `Stats` zero entries by storing them (a `stats_zero` flag per row is enough), not by normalising.

**Tests and smoke check:**
- `Server.Persistence.Tests`: round trip on the v118 and v119 fixtures and on `SyntheticWorld` with every child kind; the 10 corruption cases from step 1 now fail the **load** loudly; a 0-byte or missing file is refused; automatic import refuses when a `.db` backup exists; import then export gives the same bytes and linked files; sentinel links (255, mine 0, missing route) load as today; the SQLite decode calls no binary ctor.
- `tools/MirDbExport.Tests` on the new package version. `Server.WebAdmin.Tests` (BackupRoutes).
- **Smoke:** scratch server with `Shadow=true` under 50 StressClient bots for 30 min plus 5 web Game Data saves: `[Diag] Shadow` shows no write failures and the encode stays inside the budget; then `MirDbExport roundtrip` on the scratch folder exits 0.

**Subagents (2):**
- **Auditor (at the start, read-only):** list every reader and writer of the three linked files (Settings loaders, `ConfigRoutes`, WinForms forms, `MapRespawn.LoadRoutes`), the exact load order, and every `Envir` static that a model ctor or `Save` touches. The store and the binder are designed from this list.
- **Reviewer (at the end, server):** writer-thread ownership, lock order, the game thread never waiting, loud decode, and the import path never touching `Server.MirDB`.

**Gate:** the smoke passes with a restart and web Game Data saves, 0 shadow write failures, `roundtrip` exit 0, and the step 1 `verify` on a copy of the live data with 0 mismatches. Rollback: `Shadow=false` (no data depends on the shadow file).

**Done 2026-10-21.** Code: `Server/MirEnvir/Storage/{SqliteWorldCodec,SqliteWorldFile,SqliteWriter,SqliteWorldStore,ShadowMirror}.cs`; `Envir.Store` from `[Database] Backend`; `[Database] Backend` / `Shadow` in `Settings` + `SettingsCatalog`; `RunFileIo` inline in Sqlite mode; web backups/restore in Sqlite mode (`VACUUM INTO`, restore with the writer's connection closed); `Microsoft.Data.Sqlite` 10.0.12 in Server.Library and MirDbExport (pulls SQLitePCLRaw 2.1.12: NU1903 gone); `MirDbExport import / export-binary / roundtrip`; `verify.ps1` fails a server build without `runtimes\win-x64\native\e_sqlite3.dll`. Tests: `Server.Persistence.Tests/SqliteStoreTests.cs` (7), `tools/MirDbExport.Tests/StoreCommandsTests.cs` (2). `verify.ps1` all green (WebAdmin 983, Shared 73 ×2, Persistence 45, MirDbExport 21).
- **Evidence:** binary → SQLite → binary is byte-identical on the v118 and v119 fixtures, on a synthetic world with every child table, explicit zero stats, a `Spell.None` row, a non-BMP name and a uint above `int.MaxValue`, and **on a copy of the live `Server.MirDB`** (549,407 bytes, 11,779 rows; import then export-binary, `cmp` equal). Smoke: a scratch server with `Backend=Sqlite` imported on its first boot, loaded from SQLite on the second, left `Server.MirDB` untouched, and export-binary gave back its file byte for byte. Shadow: an Edit save mirrored and `roundtrip` exit 0 (test).
- **Step 1 `verify` can't run on the live data:** its exporter keys `maprespawns` by `RespawnIndex`, and the live file has 424 duplicate `RespawnIndex` values (several respawns share 0 and many share 1-119, 261-316, 689-718). The store keys rows by position, so it is unaffected; the byte round trip above replaces that gate. The duplicates are a data oddity worth a look: anything that finds a respawn by `RespawnIndex` (respawn save state, the web grid key) can pick the wrong one. **Fixed 2026-10-21:** opening the store renumbers them once (`SqliteWorldFile.FixDuplicateRespawnIndexes`: the first of each index keeps it, duplicates and 0s get new ones, `Pre-respawn-fix <time>.bak` first; on the live data 425 of 6,341, and the counter was 748 while indexes went to ~5,930, so it is raised past them too); the WinForms respawn and map paste no longer copy the index.
- **Differences from the plan above:**
  - **The three linked files stay files and move to 7.4** (first group). The audit found `RandomItemStatsList` and `MineSetList` are edited in place on the game thread, web workers and WinForms and only read by `Settings.Load`, and routes are edited as script files; moving them is file-data work. So `--export-binary` writes only Server.MirDB, and nothing about them changes in Sqlite mode.
  - **Shadow mirrors Edit saves only** (no game-thread encode, no budget needed); no health-endpoint counter, `ShadowMirror.Failures` and the `[Diag] Shadow` debug line instead.
  - **Keys:** every table is keyed `(parent_position, position)`, top-level tables included (parent_position 0), with the record's own index in `key`. A child links to its parent by the parent's position, so duplicate indexes (as in the live respawns) can't mis-attach rows.
  - **Import / export / roundtrip are `MirDbExport` commands**, not Server.exe flags; the server imports on its own only at the first `Backend=Sqlite` start.
  - **Backups** in Sqlite mode use the binary names (`Database yyyy-MM-dd HH-mm-ss.bak`, SQLite content), so `BackupRetention` prunes them unchanged; the web backup list shows them without a version (no binary header).
- **Review (1 reviewer) fixes:** `RespawnTimer.CurrentDelay` set on decode (0 made an empty server tick every loop); web backups/restore usable in Sqlite mode (no binary header read from SQLite files); a store older than `Server.MirDB` is refused (a Binary rollback in between); a leftover `.tmp` from a crashed first import no longer blocks starts; `UpdateMagicInfo` runs when importing a file ≤ v70; one exception type from `SqliteWriter.Run`. Checked and kept: `DragonInfo.Level` (both constructors set 1).
- **Known gaps carried to 7.3:** `MonsterTunerForm` / `DragonInfoForm` edit Main and call `Main.SaveDB()` when LiveDb is off; in Sqlite mode that save writes only the respawn counter, so their edits aren't stored (they already skip the save when LiveDb is on). Runbook steps (flip, rollback with `export-binary`, restoring a SQLite backup by hand) are written in 7.3 with the drill.

### Phase 7.3 — Flip and retire (an operation, then one deletion commit)

**Goal:** SQLite becomes the store of record for system data. Once the soak and the rollback drill pass, the binary writer goes.

**Steps:**
1. **Scratch soak:** `Backend=Sqlite` on a scratch copy of live data, 200 StressClient bots for 60 min, web edits during the run, one restart, one `--export-binary` and a binary boot from it (the rollback path), one `VACUUM INTO` backup restored.
2. **Snapshot** of the live folder (Runbook), then the user flips live: `Backend=Sqlite`. First boot imports. Not in the same deploy as a C5 or P9 change.
3. **What changes at the flip (code shipped in 7.2, switched by the flag):**
   - Edit is the only writer of design data. Editor and web saves go to the writer and wait for its commit; a failure reaches the web user and the form as an error, as file errors do today.
   - **`GameDataSave.RunFileIo` (~122) always runs inline on the caller's thread in this mode.** Today it runs the IO on the game thread whenever `!SystemDataStale`, and the reload route calls `RunFileIo(() => edit.LoadDB())` (`GameDataRoutes.cs` ~523). With SQLite that would make the game thread wait for the writer.
   - Main no longer saves design data. Its periodic save posts only `runtime_state` (no wait), so an editor save can't roll the respawn counter back and Main can't overwrite newer design data. `SystemDataStale` stops mattering in this mode.
   - Settings loaders for the three linked files read the store; their editors write the store. Backups switch to `VACUUM INTO` and the restore route to the store path.
4. **Rollback drill** on a scratch copy: `--export-binary`, boot `Backend=Binary`, oracle equal. The live log shows 0 decode errors and 0 `[Store]` save failures after the flip.
5. **Retire (one commit, after the drill):**
   - Delete the `Envir.SaveDB` body (the server never writes `Server.MirDB` again), Main's design save path, `SystemDataStale`, `RunFileIo`'s game-thread branch and the shadow code. Keep `BinaryWorldStore`'s **reader** for import and old backups, and the models' `Save(BinaryWriter)` methods (D6).
   - `--export-binary` stays, served from `tools/MirDbExport`, outside the server. The schema unfreezes: additive columns from here on.
6. **Lift the freeze** on new MirCraft game-data editing. Rewrite server.md §2.6, §3.4, §4.3, the AGENTS.md new-saved-fields rule and the Runbook restore section. Update `roadmap.md` status.

**Tests and smoke check:** the 7.2 suites, minus the shadow tests. **Smoke:** after the retire commit, a scratch server boots from a `VACUUM INTO` backup and passes one web save and one restart.

**Subagents (1):** **Reviewer** of the retire commit: nothing still writes `Server.MirDB` on the server, `SaveAccounts` and the guild/goods/conquest saves are untouched, and every deleted call site has a store replacement.

**Done 2026-10-21 (code; not committed).** The live flip is the operator's next deploy (Runbook §10): the first start imports `Server.MirDB`.
- **Soak** on a scratch copy of the live data (Configs, Envir, Maps, Guilds, Conquests, both DBs; 1.1 GB, read-only copy from `Build\Server\Release`) with `Backend=Sqlite` and `SaveDelay=1`: the import took 0.36 s; 200 StressClient bots for 10 min, all in game, 0 errors / decode errors / disconnects / refusals (`RESULT: PASS`); 0 isolated errors in the server log; `runtime_state` advanced (Main's runtime-only saves); `integrity_check` ok. No web edits in the soak: the scratch profile has the web admin off and the copy holds real accounts (no test credentials on real data); Edit saves are covered by `SqliteStoreTests`.
- **Drill** (on the 7.2 build, which still had Binary): restart on SQLite (the counter carried over); manual backup restore (an edited price came back); `export-binary` → boot Binary → Main saved `Server.MirDB`; back on SQLite the stale store was **refused**; moving it away then **also** refused the re-import, because a SQLite backup existed. Fixed: only a SQLite backup **newer** than `Server.MirDB` counts as a lost store (after a rollback the binary build wrote `Server.MirDB` after every backup). Re-run on the retire build: re-import, then load from the store.
- **Retire:**
  - `Envir.Store` is always `SqliteWorldStore` (an internal setter lets tests and tools pick `BinaryWorldStore`); `[Database] Backend` / `Shadow` and `ShadowMirror` are gone.
  - Main's periodic save always runs and only posts the respawn counter.
  - `GameDataSave.RunFileIo` runs inline under `LoadLock` (the game-thread branch is gone); `BackupRoutes` lost the binary Server.MirDB backup and header paths.
  - `MonsterTunerForm` / `DragonInfoForm` never call `Main.SaveDB` (their edits last until a restart; they always say so).
  - `BinaryWorldStore` keeps the reader (import) and the writer (only for `MirDbExport export-binary`, the scratch generator and the persistence tests).
  - **`SystemDataStale` is kept as an informational flag** (five web status responses use it to say "Main's data is older than the store until a reboot"); it gates nothing.
- **Additive schema (replaces the `Envir.Version` bump for system data):** `SqliteWorldFile.Migrate` adds missing tables and columns on open, with the fresh model's value as the default (a new one-row table gets the fresh model's row), after a `VACUUM INTO` copy to `Back Up\Database\Pre-migrate <time>.bak`. A column the code doesn't know, or a missing index counter, still fails the load. Rule text: server.md §4.3, AGENTS.md "New saved fields".
- **Ops:** `deploy-with-backup.ps1` protects `SystemData.db*`; scratch `-Reset` removes it; the scratch generator writes `Server.MirDB` through `BinaryWorldStore`; Runbook §10 (flip, backups, restore, rollback, roll forward, refusals). The freeze on MirCraft game-data editing is lifted (roadmap §3 / §6).
- **Tests:** `verify.ps1` all green (WebAdmin 983, Shared 73 ×2, Persistence 46, MirDbExport 20). New: the migration test; the store test covers the roll-forward re-import.
- **Review (1 reviewer) fixes:** the web save guard checked `Server.MirDB` instead of the store (an empty editor copy could have been saved over a failed load); Migrate's singleton tables, per-column defaults only for missing columns, and the pre-migrate backup; stale messages in `ServerControlRoutes` (`/save` text, start error, `paths.database`), `SystemsRoutes`, `Envir` comments; Runbook: touch the file after a manual restore, and drop a leftover `Backend=Sqlite` before rolling back to 2da35a92.
- **Left as is (noted):** `GameDataRoutes` still has a now-unreachable `GameThreadTimeoutException` catch and comments describing the game-thread path; `DragonInfoForm` shows its "not saved" box on every close; `Server/WebAdmin/README.md` still describes Server.MirDB and `SystemDataStale` gating (it has other uncommitted edits; update it with them).

**Gate:** the soak and the rollback drill pass, 0 decode errors. Rollback before the retire commit: `--export-binary` (DB and linked files; nothing is lost because the schema is frozen), then the previous build with `Backend=Binary`.

### Phase 7.4 — The remaining file-based design data (one group per deploy)

**Goal:** the design data in loose files moves into the store, with name links replaced by Index links.

**Groups, in order** (each: tables + exporter/verifier specs + loader switch + its editors + `--export-files` for rollback):
0. **The linked files (moved from 7.2):** `RandomItemStats.ini`, `Mines.ini` and `Envir\Routes\*.txt`, with the 7.2 review's rules: sentinels are NULL links (`RandomStatsId = 255` or past the list, mine 0, an empty or missing route), the two lists are process-wide statics edited in place (`BalanceRoutes` / `SystemsRoutes` on the game thread, `BalanceConfigForm` / `MiningInfoForm` on the UI thread) and saved through their own jobs, `Settings.Load` reads them before any store is open, and routes are preloaded so the game thread never queries the store. `export-binary` then also writes the three files.
1. **Drops:** `Envir\Drops\<Monster>.txt`, plus Fishing, Awakening, Strongbox, Blackstone and `DragonItem.txt` (`Envir.cs` ~6855-6883, `DragonInfo.cs` ~91). Linked to monsters by Index; every name that doesn't resolve is listed at import and fails the import (no silent skip).
2. **Quest bodies:** `Envir\Quests\*.txt` (`QuestInfo.LoadInfo`): tasks, rewards, carry items and text, as child tables of `quests`.
3. **Recipes, exp and stat tables:** `Envir\Recipe\*.txt` (`RecipeInfo.cs`); `ExpList`, `OrbsExpList`, `HeroExpList`, `BaseStats<Job>`, `HeroBaseStats<Job>`.
4. **Feature settings:** the `*System.ini` files, `GuildSettings`, `HeroSettings`, `MonsterRarity`, `WorldMap` (`Settings.cs` ~1028-1830), then the `Setup.ini` `[Game]` / `[Items]` keys (D8). Each becomes a `settings_<group>` key/value table with typed values; `ConfigRoutes.cs` and `ConfigForm` write the store for these keys and the ini for the rest.
5. Then `RespawnInfo.RoutePath` becomes a route Index.

**Per group:**
- The text loader stays the importer and the source of truth for the row shape. `--export-files` writes the original text format back, byte-compared with the source on the fixtures.
- Shadow per group on a scratch server: the file stays of record and the table is mirrored for the smoke run, then switch the group (`[Database] Files=<group list>`).
- Tests: import → export-files gives the same text on synthetic fixtures; a dangling name fails loudly.
- **Smoke:** scratch server with the group switched: kill one monster per drop file kind, complete one quest, craft one recipe, level one character, as relevant to the group.

**Subagents (2):** **Builder:** the exporter/verifier specs and `--export-files` for the group, in `tools/MirDbExport` only (no file overlap with the main session's loader work). **Reviewer:** the loader switch and the name-to-Index import.

**Gate per group:** the shadowed smoke has 0 mismatches and the switched smoke has 0 decode errors.

**Done 2026-10-21 (all groups at once, user's choice "structured tables, everything"; uncommitted).** No per-group shadow (shadow mode went in 7.3): the gate is `MirDbExport check-files` on a copy of the live data plus a store-only start.
- **Ini layer** (`Server/MirEnvir/Storage/Design/DesignIni.cs`): every Configs ini Settings reads (RandomItemStats, Mines, Exp/OrbsExp/HeroExp, BaseStats<Job>, HeroBaseStats<Job>, Guild/Hero settings, the eight `*System.ini`, MonsterRarity, WorldMap) is rows of `ini_lines` (file, position, section, key, value; non key=value lines verbatim), so a file comes back exactly and is queryable by section and key. `Settings.cs` reads and writes through `DesignIni.Open/Exists/Delete` (34 + 14 + 14 sites, mechanical); `Settings.Reader` is a `SetupIniReader`: `[Game]` and `[Items]` live in the store, the rest of Setup.ini stays in the file (D8: whole sections). `Shared/Functions/IniReader.cs` gained a protected lines constructor and a virtual `Save` (no behaviour change for the client).
- **Text formats** (`DesignTexts.cs`, `DesignTextFormats.cs`): `route_points` (x, y, delay), `drop_lines` (kind, chance, separator, item **Index**, gold, group, rest), `quest_lines` / `recipe_lines` (section, item/monster **Index** on the first token, rest). A line is typed only if rendering the cells gives the line back exactly; everything else (comments, blanks, `{ }`, `#INSERT`, unresolved names, odd spacing) is kept verbatim, and `design_files` keeps each file's line ending, final newline and BOM. A rename shows in every typed line (the names view follows Edit's saves); file names (Drops\<Monster>.txt, Recipe\<Item>.txt) stay names, as before.
- **Runtime:** the game's own parsers read the rendered text through `DesignFiles` (path facade over `File.*`), from an in-memory cache, so the game thread never queries the store; writes from the game thread (a default drop file created at runtime) update the cache and post the store write. Lazy import per kind / per ini on first use (an older store gets the tables from `Migrate`). Names resolve as the game does (`GetItemInfo`: case-insensitive, spaces removed).
- **Editors:** the web Scripts editor (list/read/save/create/delete/rename/restore/search, etags, backups as files), rename propagation (`GameDataRename`, parsing with the editor copy's names), the drop class toggle and the script validators all go through `DesignFiles` (`ScriptFileStore.WriteAtomic` / `ReadAllBytesShared` are store-aware). WinForms: DropBuilder, RecipeInfoForm, ConfigForm's drop toggle, Item/MonsterInfoFormNew rename propagation, QuestInfoForm; "open in editor" opens a working copy in %TEMP%\mir2-design-edit whose saves go back to the store (`DesignEditing`).
- **Tools:** `MirDbExport check-files <folder>` (import into a scratch store and compare every file), `import-files <store> <folder>`, `export-files <store> <out>` (the rollback: every ini and text file in its own bytes; Setup.ini's design sections merged into the out folder's Setup.ini).
- **Evidence:** `check-files` on a copy of the live data: **1,939 design files byte for byte** (25 routes, 1,640 drop files, 161 quests, 79 recipes, 34 ini); 61,639 drop lines typed with Index links, 8,534 drop lines and 16 quest lines name items/monsters that don't exist (kept as text; the game skips them as before). Smoke on the live-data scratch copy: a 7.3 store upgraded on first start (tables added, everything imported), then with every design file removed from the scratch folder the world started from the store alone (79 recipes, drops loaded, the same warnings). Tests: `DesignStoreTests` (ini round trip, Setup split, text round trips, renames follow, facade list/move/delete), `StoreCommandsTests` (check/import/export byte for byte, which caught export writing CRLF). `verify.ps1` all green (WebAdmin 983, Shared 73 ×2, Persistence 49, MirDbExport 21).
- **Differences from the plan above:** no per-group deploys or shadow (no time gates; shadow mode was retired in 7.3); `RespawnInfo.RoutePath` stays a route **name** (group 5 not done: changing a Server.MirDB-only model's field type contradicts the new §4.3 rule; the route table is keyed by name and renames go through the Scripts editor); the D7 optional files stay files.
- **Review (1 reviewer) fixes:** names that differ only in case reuse the stored row (SQLite keys are case-sensitive; a second row would have brought the old lines back after a restart); the web Config page verifies `[Game]`/`[Items]` against the store, not Setup.ini; ini saves (InIReader saves after every key) are posted and coalesced, never waited for, so a caller holding the settings lock can't stall the game thread behind the writer; a store that can't be opened at `Settings.Load` is logged once, the files are read instead and the world start refuses; after a web restore of SystemData.db, design-setting saves are blocked until a restart (Settings in memory are older); files changed on disk after the import are reported once per kind / file, and an ini recorded as missing is imported when it appears; drop `Gold` / `GROUP` match case-insensitively as the game does. Left: duplicate item/monster names may resolve to a different one of the duplicates than the game would (only with duplicate names); quest rewards the game resolves through its `(M)`/`(F)` fallback count as unresolved in the log; the program-folder vs working-folder difference of `RecipeInfoForm` / `ConfigForm` / `ScriptFiles` (existing debt) now decides store vs disk, so run the server with its folder as the working directory (as today).
- **Known gaps:** an operator editing the files on disk after the import changes nothing (the store is the record); there is no "re-import this kind from disk" yet (move SystemData.db away to re-import everything, Runbook §10). A web restore of SystemData.db reloads the design texts but not Settings already in memory (restart for ini changes, as today). Rollback: `--export-files`, then drop the group from `Files=`.

### Not in P7

Row-level Game Data saves with `__rev` (LiveDb-lite), `Envir\Values`, `Envir\NameLists` (they wait for table-backed NPC commands), `Archive\*.MirCA`, the optional files (D7), `BuffInfo` (code), and the unsaved runtime state listed in `P7-SQLite-Export.md` ("Not saved at all").

### WoOOLcraft lessons (built into the steps above)

- **Enums by name plus a swallowed catch** zeroed `MonsterInfo.Image` within a week → integer enums, loud decode (7.2).
- **Recomputed `max(Index)`** reuses deleted indexes → `index_counters` as loaded (7.2).
- **Two in-memory copies** with a live mirror caused H1, even after SQLite. The single writer is what fixed it, not the file format → Edit is the only design writer, Main writes `runtime_state` only (7.3).
- **A 0-byte file** silently became an empty DB → refuse it (7.2).
- **Unpruned backups** reached 12 GB → `VACUUM INTO` with today's retention (7.2).

---

## P8 — Security hardening

**X1 Critical/High fixes**, worst first, each with a regression test where testable.

**Pre-auth and flood:**
- The flood guard counts packets, not socket reads.
- Prune `IPBlocks` and `ConnectionLogs`.
- The accept callback's `while (Connections.Count >= MaxUser) Thread.Sleep(1)` poll parks a pool thread; the status port has the same poll.
- The unlocked `Connections` scan in the accept callback (`Disconnect` removes under lock).
  - `_sessionID` is already serialised by the single outstanding accept. Make it `Interlocked` only if accepts are ever parallelised.
- IPv6-safe IP parsing.
- A per-IP login throttle with a generic failure message.
- Version-skew bans: a stale client that passes the weak apphost-MD5 check gets a 24 h IP ban as soon as one of its packets fails to decode, because any `ReceiveData` exception bans for 24 h (`MirConnection.cs` ~173). Soften this for decode errors.
- Tune thresholds with the StressClient.

**`ReadPacket` wire lengths:**
- **The packets.** Three allocate attacker-chosen sizes pre-login: `C.ClientVersion` (`ReadBytes(wire length)`, the very first packet), `C.CraftItem` (`new int[wire length]`) and `C.ReportIssue` (`ReadBytes(wire length)`).
- **The fix.** Fix `ClientVersion` first and add a `Shared.Tests` case for each.
- **⚠ Deploy.** The fix is wire-compatible and runs only on the server (`Packet.IsServer`), so it can ship server-only. That is a recorded exception to AGENTS.md's "deploy client and server from the same commit after any `Shared/` change", with user approval. Otherwise it goes in the C5 batch.

**Password hashing off the game thread.** Every `Crypto.HashPassword` call goes through one worker-backed verifier:
- the Login, NewAccount and ChangePassword paths;
- the account `Password` setter;
- storage password set/unlock/remove (`AccountInfo.cs` ~40, ~292);
- web admin sign-in (`WebAdminAuth.cs` ~36) and the offline `admins.dat` (`WebAdminAdmins.cs` ~155).

The worker receives copies only (password, salt, stored hash) and posts results through a `ConcurrentQueue` (server.md §3.2). Add it to the §3.2 async-zones table.

**⚠ Hash v2** (server.md §3.4 NEVER change `Crypto.cs`):
- PBKDF2-SHA256 with ≥100k iterations, stored with an algorithm prefix in the existing string field. That also fixes the lossy UTF-8 storage.
- The account hash upgrades on next login.
- The storage hash upgrades on the next successful unlock (it has its own salt since v117).
- Web admin hashes go through the same verifier.
- Only after a 200-login storm proves off-thread verification.
- **Rollback safety:** ship v2 *verification* one deploy before any v2 *write*, so the rollback build can still verify upgraded accounts, storage passwords and `admins.dat`.

**Client credentials:** remembered launcher/game passwords are stored in plaintext in `Mir2Config.ini`. Protect them with DPAPI, or remove the option, in C5 (item 6), or earlier in C2 if added to C2's work.

---

## P9 — Measured server optimisation

**Rules:**
- Rank by P6 numbers.
- Make one change at a time.
- Run the regression script before every merge.
- Get two reviews for every threading or network change.
- Re-measure any WoOOLcraft fix before porting it: its Stats boxing win was a .NET Framework effect.

**Candidate backlog** (a backlog, not an order). Move it to `Future Optimisation/` when P9 starts:
- **Object processing:**
  - an object scheduler and player cadence;
  - ground items out of the tick walk;
  - `DelayedActions` drained only where present.
- **Visibility and AI:**
  - spatial buckets for `Broadcast`, `CheckAlone` and NPC Show, which today scale with players on the map;
  - `GetInfo` caching;
  - a `FindTarget` early exit (it scans 225 cells);
  - lazy cell lists (an upstream change gave every cell its own List).
- **Connections:**
  - process only connections that have work;
  - narrow `lock(Connections)`, which covers packet handling.
- **Logging:** move it off the game thread with a buffering appender, staying inside log4net 3.3.0 (server.md §5.2). Today's appenders write synchronously.
- **Account save:** snapshot on the game thread, then serialise and write off it. Today every account is serialised on the game thread every `SaveDelay`.
- **Runtime settings:** Mob-thread model (`Multithreaded` on/off) and GC mode (workstation vs server/DATAS).
- **After the P7 flip:** system-data save I/O via the SQLite writer.
- **After the C5 handshake** (`Shared`-touching items):
  - Encode-once packets with pooled buffers and a receive ring buffer. Today each broadcast is serialised per recipient, with two MemoryStreams per packet.
    - WoOOLcraft did this first for kill-time spikes, without numbers, so re-measure.
    - It needs the P1 mutation audit.
  - A fixed-array `Stats` (a Shared wire/DB type: keep the identical `Save`/ctor layout).

---

## C1 — Client fixes, goldens, frame baseline · Known defects (client.md §3.4/§3.5)

**Crash and defect fixes:**
- **Process-exit and crash paths:**
  - An `InvalidDataException` on the socket thread exits the process.
  - `ReceiveData` continues after Disconnect.
  - The keybind `Enum.Parse` outside its try.
  - The looping-sound duplicate key (`SoundManager.cs` ~113). This is caught by `Application_Idle` and logged, so it is a stall, not an exit; `StopSound`/`CheckSoundTimeOut` leak entries.
- **Settings ini:**
  - MaxFPS is neither loaded nor saved.
  - `[Logs]` is loaded but never saved.
  - LevelEffect loads with the wrong default (`Effect`).
  - ExpandedHeroBuffWindow saves `ExpandedBuffWindow`'s value.
- **New:** a client crash handler.

**`Client.exe --rendertest`.** Offscreen, compared byte-exact, with a `--record` mode, from scratch builds only. Scenarios:
- the 4 `SetOpacity`/`SetBlend` recipes;
- the DrawLights multiply composite;
- opacity over zero-alpha texels (WoOOLcraft's G19 trap);
- grayscale;
- nested render targets (scene, then control);
- `MirLabel` outline text and a line border;
- a scaled draw;
- the −0.5 radar dot.

⚠ Adapt WoOOLcraft's `RenderTestRunner` if decision 6 allows (roadmap §8).

**Frame profiler** in the debug overlay:
- average and max frame ms;
- draw and flush counts;
- live texture and render-target counts.

**Housekeeping:**
- Put `Data\Shaders\*.ps` into the repo. `SetNormal` and `SetBlendMagic` have no callers, so drop normal/magic if they are confirmed dead.
- Document what players run: Windows version, GPU, and how they get .NET, VC++ 2010 and DirectX June 2010 today.

---

## C2 — Launcher release

**Facts:**
- **Default host.** `P_Host` defaults to `http://mirfiles.com/mir2/cmir/patch/` (`Client/Settings.cs` ~192). But `Settings.Save()` always writes `[Launcher] Host`, so existing installs keep their saved host.
- **List format.** `PList.gz` is an int32 count followed by sequential `FileInformation` records:
  - the record is defined in `Client/Utils/FileHelper.cs`;
  - the count is read in `Client/Forms/AMain.cs` GetOldFileList (~169);
  - it is written by `AutoPatcherAdminV2/AMain.cs` (~449).
- **Change detection.** `CheckFile` re-downloads on any Length or LastWriteTime mismatch, so downgrades work.
- **Cleanup.** `CleanUp` runs **only** after the player clicks "Clean Files" (`Config.cs`). It then deletes **every** unlisted file except `Mir2Config.ini` and `Screenshots\`, including `KeyBinds.ini` and `Data\UserData\`.
  - The intended exe exemption compares against `AppDomain.CurrentDomain.FriendlyName`. On .NET 8 that is `Client`, with no extension, so it never matches.
  - `Client.exe` and `Client.dll` survive only because the list names them.

**Work:**
- **Hashes.** AutoPatcherAdminV2 writes a per-file SHA-256 as a **trailing section** after the counted records. Old launchers stop after `count` records.
- **Removed-files section.** A published section that the new launcher uses to delete only the named files. Without it, C5's SlimDX removal leaves `SlimDX.dll` on disks.
- **Launcher checks:**
  - verify the hash before replacing a file;
  - reject rooted and `..` names;
  - use one shared `HttpClient`.
- **Runtime detection.** Detect a missing .NET 10 Desktop Runtime and WebView2, and warn ahead of C5.
- **Host default.** Make it empty or HTTPS, **and migrate saved hosts at load** (`Settings.cs` ~310 already rewrites `mirfiles.co.uk`).
- **Retire AutoPatcherAdmin V1.** It stores its FTP password in plaintext, and it would publish hashless lists.
- **Ships with:** the C1 fixes, still on net8/SlimDX/D3D9.

**Gate:** on a scratch patch host:
- an old launcher self-updates;
- the new one adds a DLL and removes a listed-as-removed DLL, without touching user files;
- it rejects a tampered file;
- it downgrades to the previous `Client.exe`/DLL set.

---

## C3 — Render seam on D3D9 · Known debt adjacent; ⚠ client.md §3.3

**The 8 files with raw device use:**
- `CMain.cs`
- `MirControl.cs`
- `MirScene.cs`
- `MirLabel.cs`
- `MirTextBox.cs`
- `GameScene.cs`: the `MapControl` region ~10275-11280, including `DrawLights`' raw `SetRenderState` calls and `Sprite.End/Begin`
- `MLibrary.cs`: `MImage` textures, plus `MLibrary`'s `Sprite.Transform` scaled draw
- `DXManager.cs`

SlimDX is referenced from 16 client files. Some import it only for `Vector2/3`, and `QuestDialogs.cs` has an unused `using`.

**Work:**
- **Seam.** `IRenderBackend` + `D3D9Backend` behind the unchanged static `DXManager` facade; gameplay `Draw*` call sites don't change.
- **Device-lost.** `IsDeviceLost` replaces `catch Direct3D9Exception`. This amends client.md §3.3 "ALWAYS let RenderEnvironment own device-lost recovery: set DXManager.DeviceLost on Direct3D9Exception" in the same commit.
- **Neutral types.** Math moves to `System.Numerics`/`System.Drawing`. `MImage` keeps a CPU pixel buffer for `VisiblePixel`.
- **What stays.** `SlimDX.Configuration.EnableObjectTracking` (`CMain.cs` ~90) moves into the backend.
- **Exclusions.** No D3D11 code. No drive-by fixes (DrawLights state leak, integer-division tint).

---

## C4 — D3D11 backend (Vortice) behind a toggle · ⚠ WoOOLcraft reuse (decision 6)

**Why not a D3D9 rebind:**
- Crystal uses D3DX for sprites (`ID3DXSprite`), lines (`ID3DXLine`), shader assembly and screenshots (`Surface.ToStream`), and Vortice has no D3DX. Replacing those is most of the D3D11 work.
- Device-lost handling could be avoided only with D3D9Ex, which has its own pool restrictions.

**Backend spec (D3D11):**
- **Device.** Feature level 11_0 with a 10_x fallback; **compile shaders to ps_4_0** so the fallback works.
- **Swap chain.** Flip-discard with `ALLOW_TEARING` on the swap chain, on Present and on every `ResizeBuffers` (WoOOLcraft's Win11 60 fps cap). Borderless fullscreen only; disable DXGI's own Alt+Enter.
- **SpriteBatch2D.** Strict FIFO: flush on every state change, never sort, no depth buffer. Keep every existing `Sprite.Flush`; they encode layering.
- **Blend states: 5, immutable.**
  - alpha: SrcAlpha/InvSrcAlpha;
  - opacity: BlendFactor/InvBlendFactor;
  - additive: SrcAlpha/One;
  - INVLIGHT: BlendFactor/InvSourceColor;
  - the DrawLights multiply: Zero/SourceColor.

  **Each alpha slot uses the alpha form of its colour factor.** D3D9 never sets `SeparateAlphaBlendEnable`, so the `SourceBlendAlpha` writes at `DXManager.cs` ~340/347 are no-ops. WoOOLcraft's gate went from a 96.6 % diff to byte-exact with this.
- **Opacity alpha test.** A pixel-shader discard emulates `ID3DXSprite.Begin(AlphaBlend)`'s session alpha test under the opacity recipe (WoOOLcraft's black minimap and damage numbers, 26a7235).
- **Other draw details:**
  - point sampling;
  - line quads offset +0.5;
  - grayscale ported to HLSL;
  - staging-texture screenshots;
  - device-removed handling.
- **Teardown.** `ClearState` → `Flush` → release, with an `Environment.Exit` backstop (WoOOLcraft's unkillable process, 8217390).
- **Mouse input.** Scale coordinates once the backbuffer is stretched.

**QA checklist:**
- Win10 and Win11.
- Vsync on, and off uncapped.
- Alt+Enter and resolution changes; focus in and out.
- Close and kill with no hung process.
- DXVK.
- A clean debug layer.
- A 10-min soak with flat texture/RT counts.
- A gameplay walk: night lights, grayscale on death, hidden monsters, minimap, chat box.

**Packaging:** Vortice, SharpGen.Runtime(.COM), the System.Text.Json deps and `deps.json` ship through the C2 manifest. WoOOLcraft's stale deploy failed on System.Text.Json.

---

## C5 — Flag-day

**Needs:** C2 released ≥1 cycle earlier; C4 the default for ≥1 cycle; P4; P5's tool retargets; and the P6 StressClient (it must be rebuilt).

**Contingency:** if C4 isn't the default by ~2026-12-15, the user runs a SlimDX-on-net10 smoke test. A pass allows an interim net10 client patch, shipped after C2's runtime detection.

**Contents:**
1. **SlimDX exit.**
   - Delete `D3D9Backend` and the toggle.
   - `CMain : RenderForm` → `Form`, copying its paint suppression.
   - Remove `Components\SlimDX.*`, listed as removed in the C2 manifest.
   - The VC++ 2010 and DirectX June 2010 prerequisites go away.
2. **Runtime.**
   - `Shared` retargets `net8.0` → `net10.0`, and the client follows (⚠ AGENTS.md "do not retarget"). Test projects drop their net8 targets.
   - Delivery is framework-dependent with C2 detection and ≥2 weeks' notice, or self-contained win-x64 (+100–150 MB).
   - Review the client NuGet packages: `Microsoft.AspNet.WebApi.Client` 6.0.0 (used only for `ProgressMessageHandler`), WebView2 and NAudio.
3. **Handshake.** A `Shared` build id or manifest hash in `C.ClientVersion` replaces the apphost-MD5 check.
4. **Batched `Shared` fixes:**
   - ~~`C.PurchaseGuildTerritory`: review its handler, then enable or remove.~~ **Enabled 2026-10-16 (Scene Lab UI phase 2.1)** after a handler review and hardening (server.md §3.5 Known defects).
   - `S.RefineCancel`: add the case or remove the packet.
   - `Observable=false` on the storage-password replies.
   - The remaining `ReadPacket` length checks.
   - Delete the BinaryFormatter helpers.
   - The X1 protocol findings.
   - **The client's replace-by-Index handling for `S.NewItemInfo`.** It is harmless on its own, and it means a later live-edit engine needs no second flag-day.
5. **Optional TLS** (`SslStream`, pinned self-signed cert), only if public. Re-run the P6 baseline afterwards.
6. **Client credentials.** DPAPI for remembered client passwords, if not already shipped.
7. **Optional code signing** for the new launcher/apphost and native DLLs, to reduce SmartScreen/AV false positives on a forced update.
8. **Docs.** AGENTS.md and client.md in the same commit.

---

## C6 — Polish

**Fullscreen and DPI.** Borderless scaled fullscreen with letterboxing, mouse mapping and PerMonitorV2 DPI. Remove the exact-display-mode exit (`Program.cs` ~163-175). Today every alt-tab or resolution change frees every texture via `CleanUp`.

**Redraw cost:**
- Today any UI change re-renders the whole UI texture.
- The map texture rebuilds ≥10×/s, with LINQ.
- Labels are GDI-drawn, at 5 draws per outlined label.
- Add frame pacing.

**⚠ Texture streaming** (client.md §3.2 NEVER touch the device/textures/`CheckImage` off the UI thread; amend the rule for D3D11 only):
- Cull before decode.
- Decode in the background.
- Create textures off-thread on the free-threaded D3D11 device; the immediate context stays on the UI thread.
- Replace the fixed expiry with a size-budgeted LRU.

**Widescreen:**
- **Today.** The client already supports 1024/1280/1366/1920 (`eSupportedResolution.cs`; `MainDialogs.cs` picks a wide bar above 1024), capped by the server's `[Permission] MaxResolution` (default 1024; editable in the web admin).
  - Caveat: 1280 is enumerated as `w1280h720`, which is the mode the display check looks for, but `SelectScene` sets 1280×800. Include it in the layout audit.
- **The real gap is range.** At 1920 the screen shows 20 cells each side (`MapControl.OffSetX` = 1920/2/48), and `MapControl` draws 26 (`ViewRangeX` = `OffSetX` + 6). `Globals.DataRange` is 16, which equals `ViewRangeX` at 1024, so objects 17–26 cells away are never sent.
- **Rule.** Keep `MaxResolution` at 1024 until P9.
- **After P9:**
  1. Raise `DataRange` to cover the view.
  2. Audit the wide layout.
  3. Raise `MaxResolution`.

  Client and server ship from one commit (`DataRange` is in `Shared`).

---

## Decisions for later phases (P5 onward)

| Phase | Question | Recommendation |
|---|---|---|
| P5 | `@LOGIN` with a shared GM password | Remove it in favour of `AdminAccount` and the web admin |
| P5 | Backup/log retention (deletes files) | Keep everything 48 h, hourly copies 14 days, daily copies 90 days; off-box copies are outside the policy |
| P5 | Zero-character accounts dropped at load | Stop the silent drop; make pruning an explicit, logged job |
| P5 | Legacy `HttpServer`, PatcherWebSite, LibraryEditor legacy import (v0/v1/WTL via NativeSquish, VC++ 2008) still used? CI OK? | Delete what is unused; GitHub Actions on Windows with synthetic fixtures |
| P5 | Drop-line numerator (`n/d` behaves as `1/d`) | Keep it documented until a balance pass |
| P6 | Loopback knobs and the StressClient location | Opt-in, scratch-only, confirmed off live; StressClient in the sln, on net10 |
| P7 | SQLite provider, journal, scope, shadow/burn-in | `Microsoft.Data.Sqlite`; rollback journal; `Server.MirDB` only; ≥1 week shadow, ≥4 weeks burn-in |
| P7 | WinForms editors | Keep them through the seam; retire only after a capability parity audit |
| P7 | Load side effects (archiving, dropped items and accounts) | Preserve them inside the seam; turn them into logged jobs later |
| C5 (or C2) | Client "remember password" | DPAPI |
| P9 | Mob threads and GC mode | Decide from measurements |
| C2 | Patch host and runtime delivery | HTTPS with AutoPatcherAdminV2 only; framework-dependent with detection and ≥2 weeks' notice |
| C4 | Fallback cycle and shader compile | ≥1 two-week D3D9 fallback cycle; embedded HLSL compiled at runtime (`d3dcompiler_47` ships with Windows 10+) or offline `.cso` |
| C5 | TLS; `PurchaseGuildTerritory` | TLS **in**: the server will face the internet (user, 2026-10-09), and TLS is part of the go-public gate. Enable `PurchaseGuildTerritory` only after its handler is reviewed (**done 2026-10-16**, Scene Lab UI phase 2.1: reviewed, hardened, enabled) |
| C6 | Widescreen ambition | Decide after P9 |
