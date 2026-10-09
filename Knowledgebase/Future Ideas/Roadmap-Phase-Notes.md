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
- **Expected failures until C5:** `C.PurchaseGuildTerritory` and `S.RefineCancel` (no factory case), and the storage-password replies being observable.
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

**Schema changes (⚠, roadmap §8).** A new system-data field becomes an additive column plus a `meta.schema_version` bump, with no `Envir.Version` bump. Accounts, guilds, goods and conquests keep the `Envir.Version` rule.

**Exporter and independent verifier** (agent background from P4):
- **Schema source:** columns come from `Server/WebAdmin` `GameDataRegistry`/`GameDataCodec`.
- **Encoding rules:**
  - enums stored as their underlying INTEGER;
  - links stored as INTEGER Index;
  - positional child rows (map, conquest children) keyed by `(ParentIndex, Position)`;
  - `meta` (schema_version) and `index_counters` tables (MapIndex…RespawnIndex, never recomputed from `max(Index)`);
  - total, loud decode.
- **Verifier:** re-implements the encoding (never calls the codec).
- **Round trip:** binary → SQLite → load → SQLite must be byte-identical.
- **Delivery:** an offline CLI in `tools/`.

**Storage seam (its own deploy).** Move the save/load code (`Envir.cs` ~2452-3205, about 750 lines including the account, guild, goods and conquest code that stays binary) into `BinaryWorldStore` behind `IWorldStore`.
- Entry points that stay unchanged:
  - the 16 WinForms `SaveDB` call sites. 14 of them are on `Envir.Edit`. `DragonInfoForm` and `MonsterTunerForm` call `Envir.Main.SaveDB()` from the UI thread (Known debt) and must be **rerouted**.
  - `AccountInfoForm`'s `SaveAccounts`, and SMain's Edit `LoadDB`.
  - `GameDataSave.RunFileIo` (`GameDataSave.cs` ~107) and the web routes.
- `LoadVersion` stays set under `LoadLock`.
- No silent defect fixes.

**Shadow mode.**
- After each save, take a snapshot on the game thread.
- Write the SQLite mirror and run the diff **off the game thread** (server.md §3.2).
- Run ≥1 week, covering a restart and a web Game Data save.

**`SqliteWorldStore` of record** (`[Database] Backend=Binary|Sqlite`):
- **Library and file:**
  - `Microsoft.Data.Sqlite` in Server.Library only.
  - Rollback journal (not WAL).
  - Refuse a 0-byte or missing file.
- **Migration:**
  - The first boot imports the binary and leaves it as the rollback.
  - `--export-binary` writes it back.
  - Drop the GTMap block, which is written but never read.
- **One writer thread owns the file.**
  - Editor and web saves go to the design tables.
  - Main's runtime state (the respawn tick counter) goes to a separate `runtime_state` table, so editor saves can't roll it back.
  - `Main` no longer saves design data, which retires the periodic Main `SaveDB` and `SystemDataStale` in this mode.
- **Backups and restore:** use `VACUUM INTO` or the backup API in the same change, never `File.Copy` of an open DB (today's web admin `BackupRoutes` copies the file).
- **Post-load bindings** must run identically on both backends: `BindGameShop`, `ItemInfo` random stats and `LinkGuildCreationItems`.

**Flip, burn-in, retire:**
1. Scratch soak under StressClient load, with web edits.
2. Snapshot.
3. Flip (never in the C5 week).
4. ≥4 weeks of burn-in, with one rollback drill.
5. Delete the binary system-data writer and `SystemDataStale`.
6. Keep a standalone importer for old `Server.MirDB` backups, with the system-data subset (about 50) of Crystal's 111 version gates.
7. Add row-level Game Data saves from the grids (`__rev`).
8. Lift the freeze.
9. Rewrite server.md §2.6/§3.4/§4.3 and the AGENTS.md new-saved-fields rule.

**WoOOLcraft lessons:**
- **Enums by name plus a swallowed catch** zeroed `MonsterInfo.Image` within a week.
- **Recomputed `max(Index)`** reuses deleted indexes.
- **Two in-memory copies** with a live mirror caused H1, even after SQLite. The single writer is what fixed it, not the file format.
- **A 0-byte file** silently became an empty DB.
- **Unpruned backups** reached 12 GB.

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
   - `C.PurchaseGuildTerritory`: review its handler, then enable or remove.
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
| C5 | TLS; `PurchaseGuildTerritory` | TLS **in**: the server will face the internet (user, 2026-10-09), and TLS is part of the go-public gate. Enable `PurchaseGuildTerritory` only after its handler is reviewed |
| C6 | Widescreen ambition | Decide after P9 |
