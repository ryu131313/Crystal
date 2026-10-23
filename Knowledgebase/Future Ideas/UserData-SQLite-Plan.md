# User data in SQLite (UserData.db) — plan (2026-10-23)

**Status: proposed 2026-10-23, reviewed twice 2026-10-23 (correctness review, then a lean review that cut machinery), not started.** The live account database may be wiped (only web client test accounts on it, user 2026-10-23); an importer is kept because it is nearly free.

## Decisions (user, 2026-10-23)
1. `Server.MirADB` is renamed `.imported` after import (likewise the `Guilds`, `Goods`, `Conquests` folders).
2. Guilds, goods and conquests move in now.
3. Stats: the list in "Player statistics" plus PvP stats.
4. Website: `worldofwooolcraft.uk`, a Cloudflare tunnel to WoOOLcraft's `WebPortalHost` (:5080) on **this machine**, serving static files from `C:\WoOOLcraft\wwwroot`. The export is whitelisted **JSON files** in a folder that host serves; no DB on the web side.
5. Journal mode `DELETE`, not WAL: nothing else opens the file, and WAL's extra files complicate restore/deploy. Revisit only if the web admin gets its own read connection.

## Lean execution plan

| Phase | Work | Smoke check | Who |
|---|---|---|---|
| **1. Store core** | `UserData.db` (`meta`, `world`, `accounts`, `guilds`, `goods`, `conquests`); a second `SqliteWriter` with a per-file open callback; one `SaveUserData` job = one transaction; load; goods held in memory; first-start import + rename; shutdown order; legacy save path removed | One persistence test: import a copy → save → load gives an equal oracle; inject a commit failure → next save → reload still equal. Scratch server: log in, create a guild, sell to an NPC, restart | Opus 5.5 subagent |
| **2. References + backups** | Switch every old-file reference (list below); `VACUUM INTO` backups, retention, restore; docs | `tools\verify.ps1`; restore one backup on a scratch server | Opus 5.5 subagent |
| **3. Stats + export** | `Statistics` dictionary, `PlayerStat`, `HideFromRankings` (one `Envir.Version` bump); stat hooks incl. PvP; whitelisted JSON export + the static pages | A kill increments the stat and appears in the exported JSON; whitelist test | Opus 5.5 subagent (pages: ask before editing WoOOLcraft) |
| **Review + fix** | One read-only reviewer over the diff; I check and fix | All builds + tests | Opus 5.5 subagent + me |

Live deploy is user-run (Runbook).

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
- Load concatenates `world.head` + accounts `ORDER BY account_index` (AccountList only appends/removes) + `world.tail` and runs today's reader. Guilds load `ORDER BY guild_index`; `GuildCount` comes from the row count, so `GuildRefreshNeeded` / `PruneSurplusGuildFiles` go away. All goods rows go into an in-memory dictionary that `NPCScript.LoadGoods` reads (it runs during play from `ReloadNPCs`, `NPCScript.cs:188`, `Envir.cs:~7437`).
- No `UNIQUE` on `account_id`: a constraint the in-memory data could violate would make every save fail; the blob is the truth.
- No per-row version: the load calls `InvalidateAll` (`Envir.cs:~4397`), so the first save after any load rewrites every row with the current `Envir.Version`; rows are never mixed.

### Save
`SaveUserData(full)` (game thread) replaces `BeginSaveAccounts` + `SaveGuilds` + `SaveGoods` + `SaveConquests`:
- Serialises as `BeginSaveAccountsOffThread` does today, recording a per-account (index, offset, length) list into the reused buffer (a reused `List`, no copies). **Rule: every account serialised fresh this pass gets its row written**; cached accounts are skipped. With `OfflineAccountCache=Off` that is every row every save (~37 MB per `SaveDelay`, no worse than today's file + `.bak`); turning the cache On (P9-12) makes it KBs.
- Guilds/goods/conquests with `NeedSave` are serialised into the same job; `NeedSave` is cleared at serialisation as today.
- Posts **one** job: one transaction with the changed rows, `world`, `meta`, and the queued deletes. `Saving` is cleared in the job's `finally`.
- **Failure (job exception, or the post refused):** call `AccountSaveCache.InvalidateAll()` (any-thread safe) and set a volatile `_forceNextSave`; the game thread then serialises and writes everything at the next save. Jobs never overlap (every save is gated on `Saving`), so the next save covers whatever the failed one held. `SqliteWriter` gets a `TryPost` returning bool (today `Post` swallows refusals, `SqliteWriter.cs:62-78`).
- **A skipped save skips everything.** Today guilds/goods/conquests save even when the account save is skipped (`Envir.cs:2481-2492`); now their `NeedSave` stays set until a save actually runs.
- **Deletes:** never "delete what isn't in the list". A small set of deleted account indexes (two paths: `AccountRoutes.cs:~1005`, `AccountInfoForm.cs:~647`) and guild indexes is applied in the next transaction.
- **Guard:** refuse to save while accounts are not loaded (an "accounts loaded" flag, set by `LoadAccounts`, cleared by `ClearMainAccounts` / `MarkAccountsUnloaded`, `BackupRoutes.cs:530-537`). Not `Count == 0`: a stale head would write old `Next*ID`s and later reuse indexes over real rows.
- **Full saves** (shutdown, "Save now", web admin edits) serialise every account fresh, ignoring the cache, as `SaveAccounts()` does today.

### Shutdown
Today `StopEnvir` saves goods then clears `MapList` (`Envir.cs:~5045-5047`) before `SaveAccounts` (`:~2582`), and the final guild/conquest saves aren't waited on (`:~2583-2584`). New: the final full `SaveUserData` runs **before** `MapList.Clear()` and the stopping thread waits with `Run` — a documented exception to "the game thread only Posts" (`SqliteWriter.cs:9`), allowed only at shutdown, inside ProcessExit's 10 s `Join` (`SqliteWriter.cs:127`); its time is logged.

### First start
- `UserData.db` present → load it.
- Absent, old files present → load them as today (goods import handles the 9999-header format, `NPCScript.cs:196-205`), full save into `UserData.db`, rename `Server.MirADB` and the three folders to `*.imported`, log it.
- `HasPriorDataEvidence` (`Envir.cs:3566-3570`) also counts `Back Up\Accounts\*.db` and `*.imported`, so a missing `UserData.db` with evidence of data fails closed.
- `PRAGMA integrity_check` on open.
- The legacy save path (`AccountSaveOffThread=False`, `BeginSaveAccountsLegacy`) is removed.

### Backups (phase 2)
`VACUUM INTO "Back Up\Accounts\Accounts <timestamp>.db"` on the writer after a periodic save, every `[Database] AccountBackupMinutes` (default = `SaveDelay`, same restore points as today's per-save `.bak`). `BackupRetention` prunes `*.db`. Restore (world stopped): the writer closes its connection, the file is swapped, the writer reopens.

### References to switch (phase 2)
Web admin: `ServerControlRoutes.cs:394/403/432`, `BackupRoutes.cs:75-77, 164, 232, 356, 509`, `AccountRoutes.cs:49, 258`, `CharacterRoutes.cs:34-44`, `GmActions.cs:967-975`, `WebAdminHost.cs:339`, `WebAdminAdmins.cs:63-83`, `wwwroot/admin/admin-backups.js:286`, `admin-server.js:327`, `Server/WebAdmin/README.md`. WinForms: `AccountInfoForm.cs:480` (may use `Run`). Tools: `tools/deploy-with-backup.ps1:59`, `tools/start-scratch-server.ps1:95`, `tools/scratch-world/Program.cs:120`. Tests: CrashSafe, AccountSaveOffThread, OfflineAccountCache, SideFileSave. Docs: Runbook (deploy, restore, wipe, L121), AGENTS.md, server.md.

## Player statistics (phase 3)
- `CharacterInfo.Statistics : Dictionary<PlayerStat, long>`, saved as count + (ushort, long) pairs; unknown ids are kept and written back. `PlayerStat` is a **server-only** persisted enum (explicit values, append-only). One `Envir.Version` bump (121, shared with the quest redesign if that lands first), together with `CharacterInfo.HideFromRankings`. After that a new stat needs no bump.
- Online accounts are re-serialised every save, so increments cost nothing extra; offline changes need `MarkAccountDirty`.
- **General:** monsters killed, deaths, gold earned/spent, items crafted, quests completed, highest damage hit.
- **PvP:** players killed, deaths to players, best kill streak (current streak runtime-only), murders (kills that add PK points), guild war kills, damage dealt to / taken from players, highest PK points reached.
- **Hooks:** `PlayerObject.Die` credits only a player `LastHitter` (`PlayerObject.cs:~1540`); add credit to a hero's owner and a pet's master (`HumanObject.cs:~7328`). Kill credit = last hit. Damage counters in the PvP per-hit paths, no allocation.

## Website export (phase 3)
- Every `[Website] ExportMinutes` (default 5; empty `ExportPath` = off), the game thread copies a small DTO list (name, class, gender, level, guild name, public stats) of eligible characters; the writer thread sorts it and writes `leaderboards/<stat>.json`, `characters/<name>.json`, `guilds.json` to a temp folder, then swaps it into `[Website] ExportPath` (proposed `C:\WoOOLcraft\wwwroot\crystal\`).
- **Never exported:** anything from `AccountInfo`, account index (links alts), last login, deleted characters, `HideFromRankings`, admin and banned accounts.
- Pages: static HTML/JS in the WoOOLcraft `wwwroot`, reading the JSON (ask before editing that codebase).
- No SQL projection tables: nothing needs SQL access yet; they are additive later if the web admin wants queries.

## Constraints (AGENTS.md / server.md)
- Game thread never waits on the writer except the shutdown exception; no LINQ, no per-tick allocation.
- Phases 1-2 change no `Save` layout (no version bump); phase 3 bumps once.
- `dotnet test Server.Persistence.Tests` and `Server.WebAdmin.Tests` after each phase; never write under `Build\`.
