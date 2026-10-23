# User data in SQLite (UserData.db) — plan (2026-10-23)

**Status: proposed 2026-10-23, reviewed three times 2026-10-23 (correctness, lean, then adversarial: no Critical, 2 High + 6 Medium + 5 Low folded in), not started.** The live account database may be wiped (only web client test accounts on it, user 2026-10-23); an importer is kept because it is nearly free.

## Decisions (user, 2026-10-23)
1. `Server.MirADB` is renamed `.imported` after import (likewise the `Guilds`, `Goods`, `Conquests` folders).
2. Guilds, goods and conquests move in now.
3. Stats: the list in "Player statistics" plus PvP stats.
4. Website: `worldofwooolcraft.uk`, a Cloudflare tunnel to WoOOLcraft's `WebPortalHost` (:5080) on **this machine**, serving static files from `C:\WoOOLcraft\wwwroot`. The export is whitelisted **JSON files** in a folder that host serves; no DB on the web side.
5. Journal mode `DELETE`, not WAL: nothing else opens the file, and WAL's extra files complicate restore/deploy. Revisit only if the web admin gets its own read connection.

## Lean execution plan

| Phase | Work | Smoke check | Who |
|---|---|---|---|
| **1. Store core** | `UserData.db` (`meta`, `world`, `accounts`, `guilds`, `goods`, `conquests`); a second `SqliteWriter` with a per-file open callback; one `SaveUserData` job = one transaction; load; goods held in memory; first-start import + rename; shutdown order; legacy save path removed. Plus the references that would break once `Server.MirADB` is gone: `SaveAccounts()` callers (offline saves), `Envir.AccountPath`, the `admins.dat` check, the manual backup, and the persistence test harness (`PersistenceHarness`, `RoundTripTests`, `FixtureTests`, `SqliteStoreTests`, `Oracle`) | `Server.Persistence.Tests` incl. one new test: import a copy → save → load gives an equal oracle; inject a commit failure (with a pending delete) → next save → reload still equal. Scratch server: log in, create a guild, sell to an NPC, restart | Opus 5.5 subagent |
| **2. Backups + remaining references** | `VACUUM INTO` backups, retention, restore; the rest of the reference list (below); one-shot export back to `Server.MirADB` + `.mgd/.msd/.mcd` (rollback); docs | `tools\verify.ps1`; restore one backup on a scratch server | Opus 5.5 subagent |
| **3. Stats + export** | `Statistics` dictionary, `PlayerStat`, `HideFromRankings` (one `Envir.Version` bump); stat hooks incl. PvP; whitelisted JSON export + the static pages | A kill increments the stat and appears in the exported JSON; whitelist test | Opus 5.5 subagent (pages: ask before editing WoOOLcraft) |
| **Review + fix** | One read-only reviewer over the diff; I check and fix | All builds + tests | Opus 5.5 subagent + me |

Live deploy is user-run (Runbook). **Phases 1 and 2 deploy together** (phase 1 alone has no restore points).

## Why
The binary file is already crash-safe, written off the game thread, and skips re-serialising offline accounts (P9-10..12). What it can't do: every save rewrites the whole 37.6 MB file **plus** a 37.6 MB `.bak` even when 3 accounts changed, and guilds/goods/conquests are separate files saved outside the account snapshot. Rows fix both; the stats dictionary makes new stats free of version bumps.

Unchanged: all user data loads into memory at startup; the game never reads the DB during play; the serialised bytes are the snapshot.

## Design

### Tables (`journal_mode=DELETE`, `synchronous=FULL`)
```
meta(key TEXT PRIMARY KEY, value)          -- schema_version, envir_version (same transaction as the rows)
world(id INTEGER PRIMARY KEY CHECK(id = 1), head BLOB, tail BLOB)
      -- head: Version, CustomVersion, Next*IDs, NextGuildID, HeroList; tail: auctions, NextMailID, GameshopLog, SavedSpawns, MarketSales
accounts(account_index INTEGER PRIMARY KEY, account_id TEXT NOT NULL, data BLOB NOT NULL)  -- data = AccountInfo.Save bytes
guilds(guild_index INTEGER PRIMARY KEY, data BLOB NOT NULL)      -- keyed by GuildIndex (stable), not list position
goods(npc_index INTEGER PRIMARY KEY, data BLOB NOT NULL)
conquests(conquest_index INTEGER PRIMARY KEY, data BLOB NOT NULL)
```
- Load runs today's reader over `world.head` + accounts `ORDER BY account_index` (AccountList only appends/removes) + `world.tail`, but the **account and guild counts come from the row counts**, never from a count saved in the head (`SaveAccounts` writes `AccountList.Count` and `GuildList.Count` there today, `Envir.cs:3600,3605`; a stale count would read a row blob as `NextAuctionID` and brick the start). The head/tail split is at the account list, so the saved count is simply not written. Guilds load `ORDER BY guild_index`, so `GuildRefreshNeeded` / `PruneSurplusGuildFiles` go away.
- A `UserData.db` without a `world` row is an error (fail closed), never an empty world.
- No `UNIQUE` on `account_id`: a constraint the in-memory data could violate would make every save fail; the blob is the truth.
- No per-row version: the load calls `InvalidateAll` (`Envir.cs:~4397`), so the first save after any load rewrites every row with the current `Envir.Version`; rows are never mixed.

### Save
`SaveUserData(full)` (game thread) replaces `BeginSaveAccounts` + `SaveGuilds` + `SaveGoods` + `SaveConquests`:
- Serialises as `BeginSaveAccountsOffThread` does today, recording a per-account (index, offset, length) list into the reused buffer (a reused `List`, no copies). **Rule: every account `Save` call this pass gets its row written** (fresh accounts and the rolling-compare window, so a repaired mismatch, `Envir.cs:~4080`, is written); accounts emitted from their cached blob are skipped. With `OfflineAccountCache=Off` that is every row every save (~37 MB per `SaveDelay`, no worse than today's file + `.bak`); turning the cache On (P9-12) makes it KBs.
- Guilds and conquests with `NeedSave` are serialised into the same job; their `NeedSave` is cleared at serialisation as today. Goods: NPC `NeedSave` is **never cleared today** (`NPCScript.cs:1228` only sets it), so every NPC that ever sold something is rewritten each save; the new code clears it at serialisation (failures are covered by the full save below).
- Posts **one** job: one transaction with the changed rows, `world`, `meta`, and the deletes. The job sets the failure flags (below) **before** its `finally { Saving = false; }`.
- **Failure (job exception, or the post refused):** call `AccountSaveCache.InvalidateAll()` (any-thread safe) and set a volatile `_forceNextSave`; the next save then serialises every account, guild and conquest and every NPC's goods (without `ProcessGoods(true)`, which moves buyback items and runs only at shutdown and in `ReloadNPCs`). Jobs never overlap (every save is gated on `Saving`). `SqliteWriter` gets a `TryPost` returning bool (today `Post` swallows refusals, `SqliteWriter.cs:62-78`).
- **Deletes:** never "delete what isn't in the list". Deleted account indexes (two paths: `AccountRoutes.cs:~1005`, WinForms `AccountInfoForm.cs:~647`, so a concurrent set) and guild indexes (`DeleteGuild`, `Envir.cs:~7569`) stay in a pending set; a job copies them, and the game thread removes them from the set only after that job succeeded (no `_forceNextSave` seen at the next save). A failed job's deletes are therefore retried; `DELETE` of an already-missing row is harmless.
- **A skipped save skips everything** (today guilds/goods/conquests save even when the account save is skipped, `Envir.cs:2481-2492`); their flags stay set. A skipped "Save now" keeps `_saveNowRequested` set and leaves `saveTime` unchanged, so it retries next tick (today both are cleared before the skip, `Envir.cs:2474-2476`; the web admin promises "saves on the next game tick").
- **Guard:** refuse to save while accounts are not loaded. An `_accountsLoaded` flag is cleared when `LoadAccounts` **starts** (it clears `AccountList` and can throw half-way, `Envir.cs:~4288`) and set only when it finishes; `ClearMainAccounts` / `MarkAccountsUnloaded` (`BackupRoutes.cs:530-537`) clear it too. The shutdown save runs only if this run's load completed (`StopEnvir` also runs on the `CanStartEnvir` failure path, `Envir.cs:2168-2172`, before any load). Not `Count == 0`: a stale head would write old `Next*ID`s and later reuse indexes over real rows.
- **Full save** (running world: shutdown, "Save now", web admin edits) = every account serialised fresh ignoring the cache + all guilds, conquests and goods. **Offline save** (world stopped: `PersistIfStopped`, `GmActions.cs:~967`, `CharacterRoutes.cs:~34`, `AccountInfoForm.cs:~480`) = **accounts + world only**, because after a restore `GuildList`/`ConquestList` are stale (`ReloadMainAccounts` reloads accounts only, `BackupRoutes.cs:~510`); it runs under `AccountLock`.
- **No buffer reuse while a job reads it:** any full or offline save first waits until the writer is idle (`Run(_ => 0)` on non-game threads; at shutdown the game thread waits for `!Saving`), instead of today's "wait 30 s then save anyway" (`Envir.cs:3509-3513`), which would let a pending job commit torn rows.

### Shutdown
Today `StopEnvir` saves goods then clears `MapList` (`Envir.cs:~5045-5047`) before `SaveAccounts` (`:~2582`), and the final guild/conquest saves aren't waited on (`:~2583-2584`). New: the final full `SaveUserData` runs **before** `MapList.Clear()` and the stopping thread waits with `Run` — a documented exception to "the game thread only Posts" (`SqliteWriter.cs:9`), allowed only at shutdown, inside ProcessExit's 10 s `Join` (`SqliteWriter.cs:127`); its time is logged.

### Goods in memory
`NPCScript.LoadGoods` reads a goods file at runtime, also during play from `ReloadNPCs` (`Envir.cs:~7437`, which first runs `SaveGoods(true)`) and for NPCs created live (web admin `LiveDbHooks`, conquest NPCs) (`NPCScript.cs:~108,180`). New:
- The goods rows are read into a dictionary (npc index → bytes) at **every world start**, before `StartEnvir` spawns NPCs, so a restore while stopped is picked up.
- Whenever a save serialises an NPC's goods, those bytes also replace its dictionary entry (game thread).
- `ReloadNPCs` runs `ProcessGoods(true)` and serialises every NPC's goods into the dictionary before reloading, then posts a save.
- `LoadGoods` reads only the dictionary. The game thread never queries the DB.

### First start
- `UserData.db` present → load it.
- Absent, old files present → load them as today, full save into `UserData.db` **created like `SqliteWorldStore.Create`** (`.tmp` file, integrity check, read back, then `File.Move`, `SqliteWorldStore.cs:~167`), check the row counts equal `AccountList.Count` / `GuildList.Count` / conquest count (a duplicate index would be silently replaced), then rename `Server.MirADB` and the three folders to `*.imported` and log it. A crash before the move leaves only the `.tmp` and the old files, so the import simply runs again.
- Goods import reads **every** `*.msd` file directly, keyed by file name (9999-header format, `NPCScript.cs:196-205`), not only those of spawned NPCs.
- `HasPriorDataEvidence` (`Envir.cs:3566-3570`) also counts `Back Up\Accounts\*.db` and `*.imported`, so a missing `UserData.db` with evidence of data fails closed.
- `PRAGMA integrity_check` on open.
- The legacy save path (`AccountSaveOffThread=False`, `BeginSaveAccountsLegacy`) is removed.

### Backups (phase 2)
`VACUUM INTO "Back Up\Accounts\Accounts <timestamp>.db"` on the writer after a periodic save, every `[Database] AccountBackupMinutes` (default = `SaveDelay`, same restore points as today's per-save `.bak`). `BackupRetention` prunes `*.db`. Restore (world stopped): the writer closes its connection, the file is swapped, the writer reopens.

**Rollback:** an older build finds `*.imported` and fails closed. Head + account rows + tail is byte-for-byte a `Server.MirADB` (with the counts re-inserted), so a one-shot export (a `MirDbExport`-style command) writes `Server.MirADB` and the `.mgd`/`.msd`/`.mcd` files back; documented in the Runbook.

### Threading notes
- `SqliteWriter.PostFailures` is static: its count now covers both files (diagnostics only; fine).
- Tests use a private UserData writer instance (a shared writer pins its path at creation, `SqliteWriter.cs:31`).

### References to switch (phase 1 takes the ones listed in its row; phase 2 the rest)
Web admin: `ServerControlRoutes.cs:394/403/432`, `BackupRoutes.cs:75-77, 164, 232, 356, 509`, `AccountRoutes.cs:49, 258`, `CharacterRoutes.cs:34-44`, `GmActions.cs:967-975`, `WebAdminHost.cs:339`, `WebAdminAdmins.cs:63-83`, `wwwroot/admin/admin-backups.js:286`, `admin-server.js:327`, `Server/WebAdmin/README.md`. WinForms: `AccountInfoForm.cs:480` (may use `Run`). Tools: `tools/deploy-with-backup.ps1:59`, `tools/start-scratch-server.ps1:95`, `tools/scratch-world/Program.cs:120`. Tests: PersistenceHarness (`:57-61`), RoundTrip, Fixture (v120 byte fixtures), SqliteStore (`:50`), Oracle, CrashSafe, AccountSaveOffThread, OfflineAccountCache, SideFileSave. Docs: Runbook (deploy, restore, wipe, L121), AGENTS.md, server.md.

## Player statistics (phase 3)
- `CharacterInfo.Statistics : Dictionary<PlayerStat, long>`, saved as count + (ushort, long) pairs; unknown ids are kept and written back. `PlayerStat` is a **server-only** persisted enum (explicit values, append-only). One `Envir.Version` bump (121, shared with the quest redesign if that lands first), together with `CharacterInfo.HideFromRankings`. After that a new stat needs no bump.
- Online accounts are re-serialised every save, so increments cost nothing extra; offline changes need `MarkAccountDirty`.
- **General:** monsters killed, deaths, gold earned/spent, items crafted, quests completed, highest damage hit.
- **PvP:** players killed, deaths to players, best kill streak (current streak runtime-only), murders (kills that add PK points), guild war kills, damage dealt to / taken from players, highest PK points reached.
- **Hooks:** `PlayerObject.Die` credits only a player `LastHitter` (`PlayerObject.cs:~1540`); add credit to a hero's owner and a pet's master (`HumanObject.cs:~7328`). Kill credit = last hit. Damage counters in the PvP per-hit paths, no allocation.

## Website export (phase 3)
- Every `[Website] ExportMinutes` (default 5; empty `ExportPath` = off), the game thread copies a small DTO list (name, class, gender, level, guild name, public stats) of eligible characters; the writer thread sorts it and writes `leaderboards/<stat>.json`, `characters/<name>.json`, `guilds.json` to a temp folder, then swaps it into `[Website] ExportPath` (proposed `C:\WoOOLcraft\wwwroot\crystal\`).
- **Never exported:** anything from `AccountInfo`, account index (links alts), last login, deleted characters, `HideFromRankings`, admin and banned accounts.
- Files are replaced one by one (`File.Replace` from a temp file; stale character files deleted afterwards), not a folder swap: on Windows a swap fails or gives a brief 404 while the web host holds a file open.
- `HideFromRankings` defaults to **false** (shown). Players set it with a `@` chat command (`@hiderank` / `@showrank`, server-only, no packet); a client toggle can come later.
- Heroes have no stats (`HeroInfo` overrides `Save`/`Load`); their kills credit the owner.
- Pages: static HTML/JS in the WoOOLcraft `wwwroot`, reading the JSON (ask before editing that codebase).
- No SQL projection tables: nothing needs SQL access yet; they are additive later if the web admin wants queries.

## Constraints (AGENTS.md / server.md)
- Game thread never waits on the writer except the shutdown exception; no LINQ, no per-tick allocation.
- Phases 1-2 change no `Save` layout (no version bump); phase 3 bumps once.
- `dotnet test Server.Persistence.Tests` and `Server.WebAdmin.Tests` after each phase; never write under `Build\`.
