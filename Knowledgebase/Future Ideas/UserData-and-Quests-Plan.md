# User data in SQLite + quest system redesign: combined plan (2026-10-24)

**Status: combined 2026-10-24; wave 0 done 2026-10-24 (quest enums in `Shared/Enums.cs` + emitted TS), wave 1 done 2026-10-24 (on `FuckingAround` at 99502ac0, reviewed + fixed), D1 pending (user-run, Runbook §13).** Merged from `UserData-SQLite-Plan.md` (proposed 2026-10-23, reviewed three times) and `Quest-System-Redesign.md` (proposed 2026-10-23, reviewed three times); both files are gone, their history is in git. **Part A** (user data) and **Part B** (quests) are the two designs, changed only where combining them changed something (one version bump, one wipe, ordering). The **execution plan** below replaces both plans' phase tables (reviewed twice 2026-10-24, findings folded in); Part B keeps its phase table as the reference for what each quest phase contains. **Combined plan reviewed 2026-10-24** (one reviewer per part, claims checked against the code): 13 must-fix and 11 smaller findings folded in (goods on the import run, offline deletes vs restore, conquest row versions, shutdown position and bounded wait, `.bak` naming and `VACUUM INTO` copies, import count checks; quest validation scope, turn-in pending step, shared-table live edits and the sent set, NPC icons in the client lanes, old clients refused at D2, rate multipliers, flag semantics).

## Decisions

**User data (user, 2026-10-23)**
1. `Server.MirADB` is renamed `.imported` after import (likewise the `Guilds`, `Goods`, `Conquests` folders).
2. Guilds, goods and conquests move in now.
3. Stats: the list in "Player statistics" plus PvP stats.
4. Website: `worldofwooolcraft.uk`, a Cloudflare tunnel to WoOOLcraft's `WebPortalHost` (:5080) on **this machine**, serving static files from `C:\WoOOLcraft\wwwroot`. The export is whitelisted **JSON files** in a folder that host serves; no DB on the web side.
5. Journal mode `DELETE`, not WAL: nothing else opens the file, and WAL's extra files complicate restore/deploy. Revisit only if the web admin gets its own read connection.

**Quests (user, 2026-10-24)**
6. The user DB is wiped when quests V2 ship (the server is not public): no conversion of saved quest progress, no legacy protocol.
7. Quest edits go live through a LiveDb "quest replace" hook (Part B, Live edits).
8. Quest text is in the server's language only; `texts` keeps a `Lang` column (always `""`) for later.
9. Resets: daily 00:00, weekly Monday 00:00 (UTC, like `Envir.Now`).

**From combining (2026-10-24)**
10. **One `Envir.Version` bump, 121**, carrying the quest state block, `Statistics` and `HideFromRankings` together. The "122 if the other plan ships first" wording in both designs is gone.
11. **One wipe**, at the quest ship (deploy D2). Deploy D1 still imports the live `Server.MirADB` into `UserData.db`, so the store and its importer run on real data for a while before the wipe.
12. **One early deploy, one player-facing ship.** D1 (user-data store + quest definition tables + the new quest enums) changes nothing players see, but carries a client patch because `Shared/` changed; everything players see ships once, in D2.
13. **Lean shape** (2026-10-24): at most five lanes at once; the UIs and bots start once the engine's packets work on scratch.

## Execution plan

**Principles**
- **Lean shape (user, 2026-10-24).** At most five lanes at once. UIs and bots are built against the real server, not fixtures, so they start only once the engine's packets work on scratch. Each lane ends with `tools\verify.ps1 -Only <its keys>` and its one smoke check. One read-only reviewer per wave end (two for wave 3, which is server-heavy). Subagents are Opus 5.5 medium.
- **One writer per set of files.** Each lane runs in its own worktree, pre-created from the right commit (workflow worktrees otherwise branch from `master`). It owns the files listed for it and merges back. Where two lanes must touch one file, the row says who merges first, and the other merges that result into its worktree (never a mid-wave rebase).
- **`Shared/` has one owner (me), with one exception.** Only the wave 0 and wave 2 kick-off steps change `Shared/`. A lane that needs another change stops and asks. **Exception:** any lane may *add* keys to `ClientTextKeys` / `ServerTextKeys` (`Shared/Language.cs`) and to its own side's `Localization/*.json`. Keys are looked up by name, so an addition never renumbers anything. Each lane adds its keys in its own block (a comment header per lane), so merges conflict only trivially.
- **Branches.**
  - Waves 0 and 1 land on `FuckingAround` (deploy D1).
  - Everything behind the quest ship lands on `quests-v2`. It is created from `FuckingAround` after D1 and rebased onto it at every wave end.
  - **Version 121 is reserved for this plan:** no other user-data bump on `FuckingAround` until D2.
  - The 121 layout may still change on `quests-v2` before D2 with no further bump, only regenerated fixtures. Nothing at 121 exists outside scratch until D2.
  - **The packet-ID tail is reserved too:** no new packet IDs on `FuckingAround` between D1 and D2, so the V2 ordinals never shift under a rebase. Baseline (checked 2026-10-24): last `ServerPacketIds` member `DungeonMap` (302), last `ClientPacketIds` member `SetAllowToggle` (172); the V2 packets start at 303 / 173. Re-check at the kick-off.
  - **Rebase only at wave boundaries, with no lane worktree open** (wave 1 end, end of wave 2, before wave 3). Mid-wave fixes from `FuckingAround` are merged in, not rebased, so open lanes are not forced to rebase.
  - At D2, `quests-v2` is merged into `FuckingAround` (fast-forward after the last rebase); wave 4 works there.
- **The quest definition schema freezes at D1.** D1 writes the quest enum values and the new columns into the live `SystemData.db`. From then on a change found by F or E must be an appended enum value or an additive column. A rename or removal needs a written store migration with its pre-migration copy (`SqliteWorldFile.cs` ~L185-210), as phase 4's removals do.

**Waves**

| Wave | Lane | Branch | Work | Owns (files) | Smoke check |
|---|---|---|---|---|---|
| **0. Enums** (sequential, me) | — | `FuckingAround` | The new quest enums in `Shared/Enums.cs` (explicit values, append-only: Part B, Enum notes), which lane B's tables and importer store. No packet change, so no manifest regen. Then `WebRelay --emit-ts` | `Shared/Enums.cs`, the emitted TS enums | Every consumer compiles; `Shared.Tests` |
| **1** (two lanes in parallel; A may start beside wave 0, it does not need the enums) | **A. User-data store + backups** (Part A phases 1+2) | `FuckingAround` | Part A as written, plus **`tools/deploy-with-backup.ps1`**: add `UserData.db` (prefix, so `-journal` and `.tmp` are covered) to the protected prefixes and `Guilds.imported` / `Conquests.imported` (root folders, `Settings.cs` ~L23-24) to the protected dirs, so a binary-only rollback never copies an old `UserData.db` over the live one; Runbook §2 protected list updated. **Before D1:** run the importer on a copy of the live data (`MIR2_PERSISTENCE_DATA`) and fix or document whatever the count checks find (Part A, First start) | `Envir.cs` save/load region (incl. splitting `StopEnvir`); `SqliteWriter.cs` (the per-file open callback goes here, not in `SqliteWorldFile.cs`); new user-data Storage files; `NPCScript.LoadGoods` and the goods restore in `LiveDbHooks.cs` (~L292-306, whose comment assumes a file re-read); `BackupRetention.cs`; `Settings.cs` (`AccountBackupMinutes`, `AccountSaveOffThread` removed; the wave 2 kick-off owns this file later); web admin backup/account/server routes and the offline-save call sites; `AccountInfoForm`; the existing persistence harness and test files (`PersistenceHarness`, `RoundTrip`, `Fixture`, `SqliteStoreTests`, `Oracle`, `SyntheticWorld`, …); `tools/*.ps1` | Part A phases 1 and 2 smoke checks |
| | **B. Quest definitions + importer** (Part B phase 1) | `FuckingAround` | Part B phase 1 as written, incl. the dry-run report. **Disables the WinForms `QuestInfoForm` add/remove** (it resets `Envir.QuestIndex = 0` when the list empties, `QuestInfoForm.cs:56`, which would reuse quest ids that importer rows, `LegacySource` and later `QuestHistory` key on) | `QuestInfoForm.cs`; `SqliteWorldCodec.cs` / `SqliteWorldFile.cs` (specs, counters migration); `GameDataRegistry` (read-only entries); `LiveDbClassification`; new `Server/MirEnvir/Quests/` definition + importer files. In `Envir.cs`, only the `partial` keyword and one start-up call; everything else goes in a new `Envir.Quests.cs`. **Tests only in new test files** (e.g. `QuestDefinitionTests.cs`, `QuestImporterTests.cs`); the round trip of the new tables is added to `SyntheticWorld` by lane A after B merges | Part B phase 1 smoke check |
| **1 end** (me + 1 reviewer) | — | `FuckingAround` | **Merge B, then A** (A merges B in over B's `Envir.cs` lines and adds the quest tables to `SyntheticWorld`). One reviewer over the A+B diff; fix. Docs: Runbook **"D1" section** (deploy + rollback below), server.md §2.6 (user-data store), AGENTS.md persistence lines. **D1 (user-run):** server; client patch (wave 0 changed `Shared/`, so the same-commit rule applies; the in-game patcher makes it cheap); WebRelay rebuild + `tools\publish-webclient.ps1` (enums changed), **inside the server stop window**: desktop players also connect through the relay (`[Network] Relay`, Runbook §9). Live `Server.MirADB` imported into `UserData.db`; quest tables created and imported on the live `SystemData.db`; dry-run report written into Part B | — | `tools\verify.ps1`; D1 per Runbook |
| **2 kick-off** (sequential, me; waits for D1, so D1 fixes need no second rebase) | — | `quests-v2` (created here) | **The contract, once:** the V2 packets and the changed packets (Part B, Wire protocol) in all four places; manifest regen; TS port, `WebRelay/Fixtures.cs`, `--emit-ts`, `codec:regen`. Compile fixes for removing `S.ObjectNPC.QuestIDs` and the `S.GameDataChanged` quest list: `Client/MirScenes/GameScene.cs` (~L1758), `Server/MirObjects/NPCObject.cs` (~L408), the LiveDb push that builds `GameDataChanged`, `GameDataChangedTests`, `WebClient/src/game/gameData.ts` / `quests.ts`. The legacy quest flow is dead on this branch from here; the branch only ships with V2. **The 121 save block, once:** quest state records (Part B, Character state), `Statistics` + `PlayerStat`, `HideFromRankings`, the `< 121` reader that skips the legacy quest bytes **with its own field reads, without `QuestProgressInfo` or `Envir.BindQuest`** (today's load uses both, `CharacterInfo.cs` ~L275-284; wave 4 deletes the class but keeps this reader); fixtures, `SyntheticWorld` and the oracle updated. **`CharacterInfo.CurrentQuests` / `CompletedQuests` stay as unsaved in-memory members** (always empty after load) until lane F has moved each consumer off them; wave 4 deletes them. **The version check:** the V2 `S.ObjectNPC` layout crashes an old desktop client on its socket thread, so D2 must refuse old executables at login; the kick-off makes sure `[General] CheckVersion` / `VersionPath` (`Settings.cs` ~L44-45, hashed at start) cover the D2 client and documents the refresh in the Runbook D2 section. **Stubs that F and G would otherwise both edit:** `[Quests]` and `[Website]` settings in `Server/Settings.cs`; empty `@hiderank` / `@showrank` cases next to `SETQUEST` in the chat-command switch; the `Quests` appender in `Server.MirForms/log4net.config`; **`PlayerStatistics.Add(PlayerStat, long)`** as a stub, so F can call it at turn-in before G merges | `Shared/`, `WebClient/src/protocol/`, `WebRelay/Fixtures.cs`, `CharacterInfo`, `Envir.Version`, `Settings.cs`, `log4net.config`, `Server.Persistence.Tests`, and the compile-fix sites listed | All consumers compile; `Shared.Tests` + `codec:test`; `Server.Persistence.Tests` (round trip at 121; v120 fixtures load with empty quest state and no stats); `Shared.Tests` again for `GameDataChangedTests` (it lives there, `Shared.Tests/GameDataChangedTests.cs`); `Server.WebAdmin.Tests` (the LiveDb push and `LiveDbTests` changed) |
| **2** (two lanes) | **F. Quest engine** (Part B phase 2) | `quests-v2` | `PlayerQuests`, event pipeline, index, conditions, rewards, availability, accept/turn-in handlers, revision migration API (used in wave 3), `ResetClock`, consumer inventory (`CHECKQUEST`, recipe gates, `QuestInfo.CanAccept` (`QuestInfo.cs` ~L356), `Envir.ClearDailyQuests` (~L7088), `ScriptRoutes.cs` ~L497, the `LiveDbMainApplier` quest reloads (~L767, ~L1138), `PlayerInfoForm`, `CharacterDtos` + character quest routes, GM `SETQUEST`), party drop clones, the `Quests` log lines, the "quests completed" stat at turn-in. **Milestone F1:** accept → kill → turn in works on scratch through the packets (tested with a throwaway console probe on the StressClient codec). F1 starts wave 2b. Keeps the `PlayerObject` members the legacy oracle needs (`SetTimer`, `ActionList`: `QuestProgressInfo.cs` ~L8-10, ~L553-557) | `PlayerObject` / `HumanObject` quest regions; new `PlayerQuests` files; `MonsterObject` raise points; `NPCSegment`; `RecipeInfo`; `MirConnection` quest handlers; `Envir.Quests.cs`; web admin character quest routes | **Differential test** (needs no packets, so it works on this branch). Oracle: legacy `QuestProgressInfo` with `Owner = null` (handled, `QuestProgressInfo.cs` ~L540, ~L563) for progress and completion; for rewards, the reward selection is first extracted from `PlayerObject.FinishQuest` (~L12856-12965) into a pure helper over the `QuestInfo` reward lists, **including the `Settings.DropRate` / `Settings.ExpRate` multipliers on gold and exp** (~L12962-12963), kept with the oracle; **expected differences**, listed in the test: the gender-reward fix and the flag change (Part B, Rules: Flag objectives); party fan-out is compared by feeding each member's events, since `CheckGroupQuestKill` / `CheckGroupQuestItem` are replaced |
| | **G. Stats + website export** (Part A phase 3, without the save layout) | `quests-v2` | Stat hooks incl. PvP, the `@hiderank` / `@showrank` bodies, JSON export writer; the WoOOLcraft pages (ask before editing that codebase) | New `PlayerStatistics` / export files. **Hook sites (one-line calls into `PlayerStatistics`), listed so F leaves them alone:** `PlayerObject.Die` + the `HumanObject` PvP damage paths, gold gain/spend (`GainGold` / `TakeGold` and the NPC buy/sell, trade and market paths), craft completion, highest hit, and the player kill credit in `MonsterObject.Die`. **Both F's kill-credit raise and G's kill hook go inside the existing `Envir.RunOutsideMobPass` lambda** (`MonsterObject.cs` ~L997-1011); a hook outside it would change `CharacterInfo` from a mob thread. A stat credited to a character that is offline (a stale `LastHitter` who logged out, an offline pet master or hero owner) calls `MarkAccountDirty`, as the `PKPoints` change already does (`PlayerObject.cs` ~L1556). **G merges before F**, and F merges `quests-v2` in over those lines (no mid-wave rebase) | Part A phase 3 smoke check |
| **2b** (after F1; three lanes beside F, five at most) | **C. Desktop client quest UI** (Part B phase 3, client part) | `quests-v2` | Modern `QuestsWindow` / `TrackerHud` (bars, steps, client-built text, failed state, timers, progress toasts), classic V2 adapter, `ClientTextKeys`; **NPC quest icons and NPC quest lists from `S.QuestAvailability`** (replacing the client-side `CanAccept` over `GameScene.QuestInfoList`). **No map markers** (Part B phase 5) | `Client/` quest UI, `Client/MirObjects/NPCObject.cs` (`UpdateBestQuestIcon`, `GetAvailableQuests`, ~L61, ~L383-398), classic `NPCDialogs.cs` and the `MainDialogs.cs` minimap, modern `NpcWindow.cs` (~L273) and `MinimapHud.cs` (~L419), `Client/Localization/*.json`, `UiTestRunner` quest fixtures | `tools\uitest.ps1` screenshots; accept → kill → turn in on scratch |
| | **D. Web client quest UI** (Part B phase 3, web part) | `quests-v2` | Modern and classic web quest UI (same scope as lane C, incl. toasts and NPC icons / quest lists); the web quest model and `GameDataChanged` consumer | `WebClient/src/ui/**` quest files, `WebClient/src/game/quests.ts` + `gameData.ts` (after the kick-off's compile fixes), `game/npcSession.ts`, `ui/modern/windows/npc.ts`, `ui/npcWindow.ts`, the minimap and big-map quest icons | `codec:test`; accept → kill → turn in on scratch |
| | **H. StressClient V2 quests** | `quests-v2` | Bots take offers from `S.QuestAvailability` + `S.QuestDefinition` instead of `S.NewQuestInfo` (`VirtualPlayer.cs` ~L657), track `S.QuestStates` / `S.QuestProgress`, turn in | `StressClient/`, `StressClient.Tests` | Scratch run with quest bots: no crash, no tick lateness (`[Diag] LogLoopProfile`) |
| **3. Editor, integration, ship** (me + up to 2 reviewers) | **E. Web admin Quests editor** (Part B, Authoring), then me | `quests-v2` | E: the Quests page, pickers, condition builder, rewards, texts, validation list, "where used", JSON import/export. Then me: LiveDb quest-replace hook (needs B, F's migration API, E), new tables moved off RestartOnly; reviewers over the `quests-v2` diff; fix. Docs: AGENTS.md (packet rule "current last members", Version 121 text), server.md, client.md, webclient.md, Runbook **"D2" section** (wipe list + rollback below) and §3 entry for 121. **D2 (user-run), the ship:** `UserData.db` + `SystemData.db` backups; last legacy re-import; the D2 wipe; server + patch root from one commit, with the version check refreshed so old executables are refused (wave 2 kick-off); web client cache-busted by the publish; WebRelay rebuild + `publish-webclient.ps1`; `log4net.config` deployed; legacy quest bodies read-only; then `quests-v2` merged into `FuckingAround` | E: new `Server/WebAdmin/Quests/` files, `wwwroot/admin` quests page, `Server.WebAdmin.Tests` | E: `Server.WebAdmin.Tests` + author/save one quest on scratch. Ship: accept → kill → turn in with desktop modern, desktop classic and web; a live quest edit reaches an online player |
| **4. Legacy removal** (one lane, after D2 is stable) | — | `FuckingAround` | Part B phase 4, plus: the differential test and its extracted reward helper are deleted with `QuestProgressInfo`; `CharacterInfo.CurrentQuests` / `CompletedQuests` deleted; `Shared.Tests` updated (`Known*FactoryGaps`); `--emit-ts`; AGENTS.md packet notes. **D3 (user-run):** server + client patch (packet classes leave `Shared/`), WebRelay rebuild + `publish-webclient.ps1` | as Part B phase 4 | `tools\verify.ps1`; scratch smoke as in wave 3 |
| **Later** | — | — | Part B phase 5 (new content features incl. map markers; open decisions 4-6 first), phase 6 (shared quests, own plan) | — | Part B |

**Critical path:** wave 0 → the slower of lanes A and B → wave 1 review + D1 → wave 2 kick-off → lane F → wave 3 → D2. Lane A is the largest lane, so it is probably the one that sets the pace. Lanes G, C, D, H and E run beside the path.

**Where each original phase went**

| Original | Combined |
|---|---|
| User data phase 1 (store core) + phase 2 (backups, references) | Lane A, deployed in D1 (they already had to deploy together) |
| User data phase 3 (stats + export) | Save layout at the wave 2 kick-off; the rest is lane G; ships in D2 |
| Quest phase 1 (definitions + importer) | Wave 0 (enums) + lane B, deployed in D1 |
| Quest phase 2 (engine + save format) | Packets + save layout at the wave 2 kick-off; engine = lane F; bots = lane H |
| Quest phase 3 (clients + editor, ship) | Clients = lanes C and D; editor = lane E; LiveDb hook + ship = wave 3 |
| Quest phase 4 (legacy removal) | Wave 4 |
| Quest phases 5, 6 | Later |

**Effort saved compared with running the plans one after the other** (real, but modest):
- One version bump, one fixture regeneration and one oracle update, instead of two of each.
- The persistence harness is rewritten once (lane A) before the 121 fixtures are written on it.
- One wipe, and one player-facing deploy with its client patch.
- The "quests completed" stat is built in, not added afterwards.
- The V2 wire contract is regenerated once (the kick-off); wave 4's removal still needs its own manifest update.
- The number of reviews is about the same as two plans' worth. The saving is in rework, not reviews.

**Deploy and rollback** (each written into the Runbook in the wave that ships it)
- **D1 rollback:**
  - **Run the Part A one-shot export with the D1 build before swapping binaries back.** It writes `Server.MirADB` and the guild/goods/conquest files.
  - Move `UserData.db` and the `*.imported` files aside.
  - Restore the automatic pre-migration `VACUUM INTO` copy of `SystemData.db` and set its `LastWriteTime` to now, or the build refuses it as older than `Server.MirDB` (Runbook §10). Design edits made since are lost.
  - Put back the previous server build, the previous client build in the patch root, and the previous WebRelay + web client publish.
  - The old build starts because the export recreated `Server.MirADB`. Without the export it would refuse to start only because old `.bak` files exist: its `HasPriorDataEvidence` (`Envir.cs` ~L3596-3600) ignores `*.imported`.
- **D2 wipe:**
  - Move aside (never delete) `UserData.db` (+ `-journal`), the **whole `Back Up\Accounts\` folder** (legacy binary and new SQLite `*.bak` files both live there and both count as evidence, `Envir.cs` ~L3596-3600), `Server.MirADBn` / `Server.MirADBo` if present, and every `*.imported`. All of these count as prior-data evidence, and the server refuses to create an empty store while any is present (Part A, First start).
  - Also move `Archive\` aside, as Runbook §3.2 does: it is not evidence, but it holds old characters whose `UniqueID`s would collide with the new world (`Envir.cs` ~L3470-3501).
  - Keep the pre-ship backups somewhere outside the server folder.
- **D2 rollback:** put back the files moved aside by the wipe; restore the pre-ship `UserData.db` + `SystemData.db` backups (`SystemData.db` `LastWriteTime` set to now); the D1 server build; the D1 client build in the patch root; the D1 WebRelay + web client publish. Player progress since the ship is lost.
- **D3 rollback:** phase 4 removes the legacy `quests` columns and `LegacySource` with a written store migration, so the D2 build refuses the migrated store. Restore the pre-migration copy of `SystemData.db` (`LastWriteTime` set to now), the D2 server build, the D2 client build in the patch root, and the D2 WebRelay + web client publish. Design edits made since are lost.

**Open decisions:** none block waves 0-4. Part B open decisions 4, 5 and 6 are needed before Part B phase 5.

---

# Part A: user data in SQLite (`UserData.db`)

Lane A (phases 1 and 2 below), and lane G plus the wave 2 kick-off (phase 3), in the execution plan. The live account data is imported at D1 and wiped at D2 (decision 11); the importer stays because it is nearly free and keeps scratch servers and restores of old backups working.

### Phases (reference; scheduling is in the combined execution plan)

| Phase | Work | Smoke check | Who |
|---|---|---|---|
| **1. Store core** | `UserData.db` (`meta`, `world`, `accounts`, `guilds`, `goods`, `conquests`); a second `SqliteWriter` with a per-file open callback; one `SaveUserData` job = one transaction; load; goods held in memory; first-start import + rename; shutdown order; legacy save path removed. Plus the references that would break once `Server.MirADB` is gone: `SaveAccounts()` callers (offline saves), `Envir.AccountPath`, the `admins.dat` check, the manual backup, and the persistence test harness (`PersistenceHarness`, `RoundTripTests`, `FixtureTests`, `SqliteStoreTests`, `Oracle`) | `Server.Persistence.Tests` incl. new tests: import a copy → save → load gives an equal oracle, **with NPC goods equal to the `*.msd` files after the import run's save**; inject a commit failure (with a pending delete) → next save → reload still equal; an offline delete then a restore does not remove a restored row; the import stops on a count mismatch. Scratch server: log in, create a guild, sell to an NPC, restart | Opus 5.5 subagent |
| **2. Backups + remaining references** | `VACUUM INTO` backups, retention, restore; the rest of the reference list (below); one-shot export back to `Server.MirADB` + `.mgd/.msd/.mcd` (rollback); docs | `tools\verify.ps1`; restore one backup on a scratch server | Opus 5.5 subagent |
| **3. Stats + export** | `Statistics` dictionary, `PlayerStat`, `HideFromRankings` (in the shared 121 bump, written at the wave 2 kick-off); stat hooks incl. PvP; whitelisted JSON export + the static pages | A kill increments the stat and appears in the exported JSON; whitelist test | Opus 5.5 subagent (pages: ask before editing WoOOLcraft) |
| **Review + fix** | One read-only reviewer over the diff; I check and fix | All builds + tests | Opus 5.5 subagent + me |

Live deploy is user-run (Runbook). **Phases 1 and 2 deploy together** (phase 1 alone has no restore points): deploy D1. The "Review + fix" row is the wave 1 end review.

### Why
The binary file is already crash-safe, written off the game thread, and skips re-serialising offline accounts (P9-10..12). What it can't do: every save rewrites the whole 37.6 MB file **plus** a 37.6 MB `.bak` even when 3 accounts changed, and guilds/goods/conquests are separate files saved outside the account snapshot. Rows fix both; the stats dictionary makes new stats free of version bumps.

Unchanged: all user data loads into memory at startup; the game never reads the DB during play; the serialised bytes are the snapshot.

### Design

#### Tables (`journal_mode=DELETE`, `synchronous=FULL`)
```
meta(key TEXT PRIMARY KEY, value)          -- schema_version, envir_version (same transaction as the rows)
world(id INTEGER PRIMARY KEY CHECK(id = 1), head BLOB, tail BLOB)
      -- head: Version, CustomVersion, Next*IDs, NextGuildID, HeroList; tail: auctions, NextMailID, GameshopLog, SavedSpawns, MarketSales
accounts(account_index INTEGER PRIMARY KEY, account_id TEXT NOT NULL, data BLOB NOT NULL)  -- data = AccountInfo.Save bytes
guilds(guild_index INTEGER PRIMARY KEY, data BLOB NOT NULL)      -- keyed by GuildIndex (stable), not list position
goods(npc_index INTEGER PRIMARY KEY, data BLOB NOT NULL)
conquests(conquest_index INTEGER PRIMARY KEY, data BLOB NOT NULL)
```
- Load runs today's reader over `world.head` + accounts `ORDER BY account_index` (AccountList only appends/removes) + `world.tail`, but the **account and guild counts come from the row counts**, never from a count saved in the head (`SaveAccounts` writes `AccountList.Count` and `GuildList.Count` there today, `Envir.cs` ~L3630, ~L3635; a stale count would read a row blob as `NextAuctionID` and brick the start). The head/tail split is at the account list, so the saved count is simply not written. Guilds load `ORDER BY guild_index`, so `GuildRefreshNeeded` / `PruneSurplusGuildFiles` go away.
- A `UserData.db` without a `world` row is an error (fail closed), never an empty world.
- No `UNIQUE` on `account_id`: a constraint the in-memory data could violate would make every save fail; the blob is the truth.
- No per-row version, so **every load sets `_forceNextSave`**: the first save after any load (start, restore) rewrites every account, guild, conquest and goods row with the current `Envir.Version`, and rows are never mixed. `InvalidateAll` (`LoadAccounts`, `Envir.cs` ~L4399) alone is not enough: it covers accounts only, and `ConquestGuildInfo` has no version header of its own (its sub-records read with `Envir.LoadVersion`, `ConquestGuildInfo.cs` ~L114), so a conquest row without `NeedSave` would otherwise be read at the next start with a newer version than it was written with. Guilds (own header, `GuildInfo.cs` ~L86-100) and goods (9999 header) would survive, but are rewritten too for one rule.
- `meta.envir_version` and the `Version` in `world.head` are written in the same transaction; load checks they are equal and fails closed if not. The head's value is the one the readers use.

#### Save
`SaveUserData(full)` (game thread) replaces `BeginSaveAccounts` + `SaveGuilds` + `SaveGoods` + `SaveConquests`:
- Serialises as `BeginSaveAccountsOffThread` does today, recording a per-account (index, offset, length) list into the reused buffer (a reused `List`, no copies). **Rule: every account `Save` call this pass gets its row written** (fresh accounts and the rolling-compare window, so a repaired mismatch, `Envir.cs:~4239`, is written); accounts emitted from their cached blob are skipped. With `OfflineAccountCache=Off` (the default) that is every row every save (~37 MB per `SaveDelay`, no worse than today's file + `.bak`). Turning the cache On (P9-12) leaves the changed accounts **plus `world`**, which is not small: the head holds the whole `HeroList` (`Envir.cs` ~L3633-3635) and the tail every auction, `GameshopLog` and `MarketSales` entry, all rewritten each save. Splitting those into rows is a later, separate change.
- Guilds and conquests with `NeedSave` are serialised into the same job; their `NeedSave` is cleared at serialisation as today. Goods: NPC `NeedSave` is **never cleared today** (`NPCScript.cs:1228` only sets it), so every NPC that ever sold something is rewritten each save; the new code clears it at serialisation (failures are covered by the full save below).
- Posts **one** job: one transaction with the changed rows, `world`, `meta`, and the deletes. The job sets the failure flags (below) **before** its `finally { Saving = false; }`.
- **Failure (job exception, or the post refused):** call `AccountSaveCache.InvalidateAll()` (any-thread safe) and set a volatile `_forceNextSave`; the next save then serialises every account, guild and conquest and every NPC's goods (without `ProcessGoods(true)`, which moves buyback items and runs only at shutdown and in `ReloadNPCs`). Jobs never overlap (every save is gated on `Saving`). `SqliteWriter` gets a `TryPost` returning bool (today `Post` swallows refusals, `SqliteWriter.cs:62-78`).
- **Deletes:** never "delete what isn't in the list". Deleted account indexes (two paths: `AccountRoutes.cs:~1005`, WinForms `AccountInfoForm.cs:~647`, so a concurrent set) and guild indexes (`DeleteGuild`, `Envir.cs:~7789`) stay in a pending set; a job copies them, and the game thread removes them from the set only after that job succeeded (no `_forceNextSave` seen at the next save). A failed job's deletes are therefore retried; `DELETE` of an already-missing row is harmless.
  - **While the world is stopped** an account delete persists through `PersistIfStopped` (`AccountRoutes.cs` ~L226, ~L248), with no game-thread save to drain the set. The offline save job (a synchronous `Run`) therefore includes the pending deletes too and removes them from the set when it succeeds.
  - **A restore** (`BackupRoutes.cs` ~L368-378) clears both pending sets and `_forceNextSave` after the swap, so a stale pending delete can never remove a row the restore brought back. The load that follows sets `_forceNextSave` again (Tables, above).
- **A skipped save skips everything** (today guilds/goods/conquests save even when the account save is skipped, `Envir.cs:2481-2492`); their flags stay set. A skipped "Save now" keeps `_saveNowRequested` set and leaves `saveTime` unchanged, so it retries next tick (today both are cleared before the skip, `Envir.cs:2474-2476`; the web admin promises "saves on the next game tick").
- **Guard:** refuse to save while accounts are not loaded. An `_accountsLoaded` flag is cleared when `LoadAccounts` **starts** (it clears `AccountList` and can throw half-way, `Envir.cs:~4397`) and set only when it finishes; `ClearMainAccounts` / `MarkAccountsUnloaded` (`BackupRoutes.cs:530-537`) clear it too. The shutdown save runs only if this run's load completed (`StopEnvir` also runs on the `CanStartEnvir` failure path, `Envir.cs:2168-2172`, before any load). Not `Count == 0`: a stale head would write old `Next*ID`s and later reuse indexes over real rows.
- **Full save** (running world: shutdown, "Save now", web admin edits) = every account serialised fresh ignoring the cache + all guilds, conquests and goods. **Offline save** (world stopped: `PersistIfStopped`, `GmActions.cs:~967`, `CharacterRoutes.cs:~34`, `AccountInfoForm.cs:~480`) = **accounts + world only**, because after a restore `GuildList`/`ConquestList` are stale (`ReloadMainAccounts` reloads accounts only, `BackupRoutes.cs:~510`); it runs under `AccountLock`.
- **No buffer reuse while a job reads it:** any full or offline save first waits until the writer is idle (`Run(_ => 0)` on non-game threads; at shutdown the game thread waits for `!Saving`), instead of today's "wait 30 s then save anyway" (`Envir.cs:3509-3513`), which would let a pending job commit torn rows.

#### Shutdown
Today the final save runs in the `WorkLoop` teardown on the game thread (`Envir.cs` ~L2568-2588): `WaitForSubThreads` / `EndMobPass`, `CloseAdminQueue`, `StopNetwork`, `WaitForSideFileSaves`, then `StopEnvir` (which saves goods with `SaveGoods(true)` and clears `MapList`, ~L5221-5225), then `SaveAccounts`, `SaveGuilds(true)`, `SaveConquests(true)`, `WaitForSideFileSaves`. `Reboot` reloads right after it. New:
- `StopEnvir` is split: a first part runs `ProcessGoods(true)` on every NPC and `CleanUp()` (it edits account data: mentor/marriage links of deleted characters, mail over capacity; found in the wave 1 review); then the final full `SaveUserData` runs (after `StopNetwork`, so no packet can change state after it); then the rest of `StopEnvir` (`MapList.Clear()` onwards).
- The game thread waits for that job with a **bounded** wait (default 60 s, logged with its time; a timeout is logged as an error and the shutdown continues, so a hung writer cannot hang shutdown or `Reboot`; the data is then as safe as the last good save). `SqliteWriter.Run` has no timeout today, so this needs a `Run(job, timeout)` overload. It is a documented exception to "the game thread only Posts" (`SqliteWriter.cs:9`), allowed only here.
- The save runs only if `_accountsLoaded` (Save, Guard): `StopEnvir` also runs on the `CanStartEnvir` failure path (~L2166-2172), before any load.
- `WaitForSideFileSaves` and `_sideFileWrites` are removed (no side files are left).

#### Goods in memory
`NPCScript.LoadGoods` reads a goods file at runtime, also during play from `ReloadNPCs` (`Envir.cs:~7655`, which first runs `SaveGoods(true)`) and for NPCs created live (web admin `LiveDbHooks`, conquest NPCs) (`NPCScript.cs:~108,180`). New:
- The goods dictionary (npc index → bytes) is filled at **every world start, before `StartEnvir` spawns NPCs**, so a restore while stopped is picked up. This is earlier than the account load, which runs in `StartNetwork` after `StartEnvir` (`Envir.cs` ~L2166 vs ~L2189, ~L5199-5204). So it is its own step: open `UserData.db` and read the `goods` rows; **if `UserData.db` does not exist yet (the import run), read every `*.msd` file instead** (First start). Otherwise NPCs would spawn with empty goods on the import run, and the next full save would write those empty goods over the real ones.
- Whenever a save serialises an NPC's goods, those bytes also replace its dictionary entry (game thread).
- `ReloadNPCs` runs `ProcessGoods(true)` and serialises every NPC's goods into the dictionary before reloading, then posts a save.
- `LoadGoods` reads only the dictionary. The game thread never queries the DB.

#### First start
- `UserData.db` present → load it.
- Absent, no old files, **no prior-data evidence** → create an empty `UserData.db` (like `SqliteWorldStore.Create`), as today's start creates an empty `Server.MirADB` when there is no evidence (`Envir.cs` ~L4423-4425). This is the path after the D2 wipe, so the wipe must move every evidence file aside (execution plan, D2 wipe).
- Absent, old files present → load them as today, full save into `UserData.db` **created like `SqliteWorldStore.Create`** (`.tmp` file, integrity check, read back, then `File.Move`, `SqliteWorldStore.cs:~167`), check the row counts, then rename `Server.MirADB` and the three folders to `*.imported` and log it. A crash before the move leaves only the `.tmp` and the old files, so the import simply runs again.
  - **Where:** the import runs in `StartNetwork` after `LoadConquests` (conquests need the maps, which `StartEnvir` loads). The goods were already read from the `*.msd` files before `StartEnvir` (Goods in memory).
  - **Goods rows come from the files, never from NPC objects:** every `*.msd` file, keyed by file name (9999-header format, `NPCScript.cs:196-205`), including those of NPCs not spawned.
  - **Count checks against the sources, not the lists:** accounts against the head's saved count; guilds against the number of `Guilds\*.mgd` files; conquests against the number of `Conquests\*.mcd` files; goods against the number of `*.msd` files. Today's loaders drop data silently: `LoadConquests` skips a conquest whose map is missing (`tempMap == null → continue`, `Envir.cs` ~L4623), and `GuildObject` already de-duplicates `GuildIndex` (`GuildObject.cs` ~L107). A mismatch **stops the import** (nothing renamed) and logs each missing or duplicate index. A conquest on a missing map is kept as a row (its file bytes, unparsed) so the rollback export does not lose it. A duplicate `GuildIndex` is an operator decision (which file wins); the D1 dry run (lane A) finds any on the live data first.
- `HasPriorDataEvidence` (`Envir.cs` ~L3596-3600) also counts SQLite `Back Up\Accounts\*.bak` files and `*.imported`, so a missing `UserData.db` with evidence of data fails closed.
- **`Server.MirADB` present and SQLite account backups present** (a roll forward again after a D1 rollback, or a lost `UserData.db`): use P7's rule from `HasPriorSqliteEvidence` (`SqliteWorldStore.cs` ~L200-210): import only if no SQLite backup is newer than `Server.MirADB`; otherwise fail closed and let the operator restore.
- `PRAGMA integrity_check` on open.
- The legacy save path (`AccountSaveOffThread=False`, `BeginSaveAccountsLegacy`) is removed.

#### Backups (phase 2)
`VACUUM INTO "Back Up\Accounts\Accounts <timestamp>.bak"` on the writer after a periodic save, every `[Database] AccountBackupMinutes` (default = `SaveDelay`, same restore points as today's per-save `.bak`). Restore (world stopped): the writer closes its connection, the file is swapped, the writer reopens.
- **Naming follows P7** (`Database <ts>.bak`, content checked with `SqliteWorldFile.LooksLikeSqlite`, `SqliteWorldStore.cs` ~L98-104, `SqliteWorldFile.cs` ~L352), so `BackupRetention` (which only considers `<Accounts|Database> <ts>.bak`, `BackupRetention.cs` ~L10-15, ~L80) and the web admin backup list (keyed to `.bak`, `BackupRoutes.cs` ~L662) need no new name. Legacy binary `.bak` files can sit in the same folder: restore checks the content (`LooksLikeSqlite`) and refuses a legacy binary backup with a message pointing to the import path (move it in as `Server.MirADB` with `UserData.db` moved aside).
- **Every copy of `UserData.db` is a `VACUUM INTO` on its writer**, never a file copy: the manual backup (`BackupRoutes.cs` ~L232-238 uses `BackupStore.StableCopy` of the live file today), the pre-restore copy and any deploy-time copy. With `journal_mode=DELETE` a plain copy of a live file can be torn.
- **The live file's version is read from `meta`** through the writer, not from file bytes: `TryReadHeader` (`BackupRoutes.cs` ~L164) and `ReadFileVersion` (`ServerControlRoutes.cs` ~L394) switch.

**Rollback:** an older build does **not** recognise `*.imported` (its `HasPriorDataEvidence` checks only temp files and `*.bak`, `Envir.cs` ~L3596-3600); it fails closed only because old `.bak` backups exist. So the rollback always runs the export below first (execution plan, D1 rollback). Head + account rows + tail is byte-for-byte a `Server.MirADB` (with the counts re-inserted), so a one-shot export (a `MirDbExport`-style command) writes `Server.MirADB` and the `.mgd`/`.msd`/`.mcd` files back; documented in the Runbook.

#### Threading notes
- `SqliteWriter.PostFailures` is static: its count now covers both files (diagnostics only; fine).
- Tests use a private UserData writer instance (a shared writer pins its path at creation, `SqliteWriter.cs:31`).

#### References to switch (phase 1 takes the ones listed in its row; phase 2 the rest)
Web admin: `ServerControlRoutes.cs:394/403/432`, `BackupRoutes.cs:75-77, 164, 232, 356, 509`, `AccountRoutes.cs:49, 258`, `CharacterRoutes.cs:34-44`, `GmActions.cs:967-975`, `WebAdminHost.cs:339`, `WebAdminAdmins.cs:63-83`, `wwwroot/admin/admin-backups.js:286`, `admin-server.js:327`, `Server/WebAdmin/README.md`. WinForms: `AccountInfoForm.cs:480` (may use `Run`). Tools: `tools/deploy-with-backup.ps1:50-60` (protected prefixes and dirs; see lane A), `tools/start-scratch-server.ps1:95`, `tools/scratch-world/Program.cs:120`. Tests: PersistenceHarness (`:57-61`), RoundTrip, Fixture (v120 byte fixtures), SqliteStore (`:50`), Oracle, CrashSafe, AccountSaveOffThread, OfflineAccountCache, SideFileSave. Docs: Runbook (deploy, restore, wipe, L121), AGENTS.md, server.md.

### Player statistics (phase 3)
- `CharacterInfo.Statistics : Dictionary<PlayerStat, long>`, saved as count + (ushort, long) pairs; unknown ids are kept and written back. `PlayerStat` is a **server-only** persisted enum (explicit values, append-only). Part of the single `Envir.Version` 121 bump (decision 10), written at the wave 2 kick-off together with `CharacterInfo.HideFromRankings` and the quest state block. After that a new stat needs no bump.
- Online accounts are re-serialised every save, so increments cost nothing extra; offline changes need `MarkAccountDirty`.
- **General:** monsters killed, deaths, gold earned/spent, items crafted, quests completed, highest damage hit.
- **PvP:** players killed, deaths to players, best kill streak (current streak runtime-only), murders (kills that add PK points), guild war kills, damage dealt to / taken from players, highest PK points reached.
- **Hooks:** `PlayerObject.Die` credits only a player `LastHitter` (`PlayerObject.cs:~1540`); add credit to a hero's owner and a pet's master (`HumanObject.cs:~7328`). Kill credit = last hit. Damage counters in the PvP per-hit paths, no allocation.

### Website export (phase 3)
- Every `[Website] ExportMinutes` (default 5; empty `ExportPath` = off), the game thread copies a small DTO list (name, class, gender, level, guild name, public stats) of eligible characters; an export thread of its own (never the UserData writer, so the export cannot delay a save) sorts it and writes `leaderboards/<stat>.json`, `characters/<name>.json`, `guilds.json` to a temp folder, then swaps it into `[Website] ExportPath` (proposed `C:\WoOOLcraft\wwwroot\crystal\`).
- **Never exported:** anything from `AccountInfo`, account index (links alts), last login, deleted characters, `HideFromRankings`, admin and banned accounts.
- Files are replaced one by one (`File.Replace` from a temp file; stale character files deleted afterwards), not a folder swap: on Windows a swap fails or gives a brief 404 while the web host holds a file open.
- `HideFromRankings` defaults to **false** (shown). Players set it with a `@` chat command (`@hiderank` / `@showrank`, server-only, no packet); a client toggle can come later.
- Heroes have no stats (`HeroInfo` overrides `Save`/`Load`); their kills credit the owner.
- Pages: static HTML/JS in the WoOOLcraft `wwwroot`, reading the JSON (ask before editing that codebase).
- No SQL projection tables: nothing needs SQL access yet; they are additive later if the web admin wants queries.

### Constraints (AGENTS.md / server.md)
- Game thread never waits on the writer except the shutdown exception; no LINQ, no per-tick allocation.
- Phases 1-2 change no `Save` layout (no version bump); phase 3's fields are in the shared 121 bump.
- `dotnet test Server.Persistence.Tests` and `Server.WebAdmin.Tests` after each phase; never write under `Build\`.

---

# Part B: quest system redesign

Lanes B, F, E, C, D, H and waves 0, 3 and 4 in the execution plan. This is a design written as if we built the quest system from scratch, then fitted to Crystal (SQLite system data since P7, `UserData.db` for user data after Part A, game thread plus mob threads). It replaces `QuestInfo` / `QuestProgressInfo` and the quest packets. No code exists yet.

### Why

What we have today (all upstream Crystal):

| Area | Today | Where |
|---|---|---|
| Definition | `QuestInfo`: one SQLite row (name, group, file name, level range, one `RequiredQuest`, class, `QuestType`, 4 messages, time limit) plus a quest body with `[@KILLTASKS]`, `[@ITEMTASKS]`, `[@FLAGTASKS]`, `[@CARRYITEMS]`, rewards, descriptions. Since P7 phase 7.4 the bodies are design-text rows in `SystemData.db` (quests kind, `QuestFormat` / `quest_lines`), edited in the web admin Scripts editor and reloaded live by `QuestReload` | `Server/MirDatabase/QuestInfo.cs`, `QuestInfo.LoadInfo`, `Storage/Design/DesignTextFormats.cs`, `WebAdmin/GameData/QuestReload.cs` |
| Tasks | Kill, collect (into the 40-slot quest bag), carry, global flag (number `0 … FlagIndexCount − 1000`, inclusive). All tasks at once, no steps. `//TODO: ZoneTasks`, `//TODO: EscortTasks` | `QuestInfo.cs` |
| Targets | Resolved **by name** at load (`Envir.GetItemInfo(name)`). An unknown name returns null and the line is silently dropped. At runtime a kill task matches **every monster whose name starts with the target's name** (`StartsWith`, ignore case: "Wolf" also counts "Wolf2"); item progress counts items by `Info.Name ==`. Rewards: `ParseReward` adds **both** the `(M)` and `(F)` item when the name has gender variants, and `FinishQuest` gives every fixed reward without a gender check (a bug) | `ParseKill`, `ParseItem`, `ParseReward`; `QuestProgressInfo.NeedKill` / `ProcessKill` / `ProcessItem` |
| Givers | NPC script `[QUESTS]` section → `NPCObject.Quests`; positive ids = start, negative = finish. `QuestInfo.NpcIndex` / `FinishNpcIndex` hold the NPC's **runtime `ObjectID`**, not `NPCInfo.Index`; the last script loaded wins, so a quest has one giver. An unknown quest id `return`s and drops the **rest** of that `[QUESTS]` section | `NPCScript.ParseQuests` (~L685-717) |
| Progress (saved) | `CharacterInfo.CurrentQuests` → `QuestProgressInfo.Save`: index, start/end time, lists of (MonsterID, count), (ItemID, count), (flag, state) **matched by target id**, not by task | `QuestProgressInfo.cs`, `CharacterInfo.cs` ~L512 |
| Completion | `CompletedQuests: List<int>`, no dates or counts. Daily = `Envir.ClearDailyQuests` (~L7088) removes the indexes from `ProcessNewDay` (the loop skips the next element after `RemoveAt`, which matters only for back-to-back duplicates). The new day fires when `Envir.Now.Day` changes (`Envir.Process`); `Now` runs on a UTC clock, so the reset is **00:00 UTC**, there is no setting for it, and a server that is down over midnight skips that day's reset. `ProcessNewDay` also invalidates the whole offline account cache. Repeatable = never added | `Envir.cs` ~L3316, ~L7088 |
| Flags | `CharacterInfo.Flags: bool[Globals.FlagIndexCount]`, global and shared with every NPC script | `CharacterInfo.cs` ~L94 |
| Wire | `S.NewQuestInfo` (whole `ClientQuestInfo`), `S.ChangeQuest` (`ClientQuestProgress` with `TaskList: List<string>`, already formatted server-side text), `S.CompleteQuest`, `S.ShareQuest`, `S.GainedQuestItem`, `S.DeleteQuestItem`; `C.AcceptQuest`, `C.FinishQuest`, `C.AbandonQuest`, `C.ShareQuest` | `Shared/Enums.cs` ~L1637 / ~L1847 |
| Runtime | Each kill / pickup walks `CurrentQuests` with LINQ (`CheckNeedQuestKill`, `CheckNeedQuestItem`); kill credit already deferred out of mob passes (`MonsterObject.Die` → `Envir.RunOutsideMobPass`). Only player kills of monsters without a master count (hero kills give no quest credit). A quest drop never lands on the ground: it goes straight into the quest bag of **every** party member in range who needs it | `PlayerObject.cs` ~L12945-13040, `MonsterObject.cs` ~L1004-1011, ~L1157 |
| Live edits | `S.GameDataChanged` pushes changed `ClientQuestInfo` on LiveDb edits; `QuestReload` re-points online characters' progress and refuses edits that change a quest's task shape. GM `SETQUEST` and the web admin character quest routes edit progress directly | `ServerPackets.cs` ~L6903, `QuestReload.cs`, `PlayerObject.cs` ~L4792, `WebAdmin/CharacterRoutes.cs` ~L602 |
| Clients | Desktop classic `QuestDialogs.cs`, modern `QuestsWindow` + `TrackerHud` (5 tracked, progress parsed from strings with a regex, objective bars hidden), web `quests.ts` / `tracker.ts` | `Client/MirScenes/...`, `WebClient/src/ui/modern/...` |

Each feature we want (steps, branches, area/talk/interact tasks, weekly or cooldown repeats, multiple prerequisites, reputation, map markers, progress bars, shared quests) hits one of these limits. Patching each limit one at a time leaves us with four half-models. This design replaces them in one go.

### Goals and non-goals

**Goals**
1. One data model in which quests, steps, objectives, conditions and rewards are typed rows with stable ids, and every target is an id that is checked when the quest is saved.
2. Character state recorded **per objective id**, so a quest can be edited without corrupting the progress of players who are on it.
3. Repeat rules (daily, weekly, cooldown, max completions) worked out from completion history when needed. No bulk reset job.
4. Progress driven by game events through a per-player index: O(1) per event, no LINQ, game thread only.
5. Progress sent to clients as structured data (objective id, value, target). Clients format the text, draw bars and place map markers.
6. One condition system and one reward system, reusable later by NPC dialogue, the GameShop and events.
7. Authoring in the web admin with validation. A file format that is easy to diff, for export and import.

**Non-goals (for this design)**
- Moving user data out of `Server.MirADB` into SQLite. That is Part A; the character-state *records* here are written by `CharacterInfo.Save`, so they land in the account blobs of `UserData.db` (lane A deploys at D1, before the quest ship). **Every `Server.MirADB` mention below (backups, rollback, test fixtures) means `UserData.db`.** One `Envir.Version` bump, 121, covers this part's state block and Part A's statistics (decision 10). The `PlayerStat` "quests completed" counter is incremented at turn-in (`PlayerQuests.TurnIn`).
- Phasing/instancing (per-player world state). The model leaves room for it (conditions on spawns), but the runtime is not part of this work.
- Voice, cutscenes, camera work.
- Rewriting the NPC script language. Scripts keep working; they gain a few quest checks and actions.

### Design principles

1. **Definition vs state.** Definitions are system data (`SystemData.db`, edited through `Envir.Edit`, saved by `GameDataSave` and applied to the live world by the LiveDb quest-replace hook). State is user data (`UserData.db`, Part A). State never stores anything that can be read from the definition.
2. **Stable ids, never reused.** Quests, steps and objectives each get an integer id from a counter that only goes up, like `QuestIndex` today. Deleting an objective retires its id for good. A string `key` (e.g. `bichon.wolves.1`) exists for authors and export files only, never for saved state.
3. **Ids, not names.** Every target (monster, item, NPC, map) is an id, checked when the quest is saved. Display names come from the target's info.
4. **Declarative first, script as escape hatch.** Conditions, objectives and rewards are data. Per-step script hooks exist for the 5% that data cannot express, and call NPC script pages, not C#.
5. **Text by key.** Quest text lives in one text table, keyed by `(Key, Lang)`. Definitions hold keys. Only the server's language is used (decided 2026-10-24); the `Lang` column exists so per-player language can be added later as a data change.
6. **The game thread owns all quest state.** Events raised elsewhere are passed to the game thread, as kill credit already is.

### Definition model (system data, `SystemData.db`)

Tables in the existing P7 store (`SqliteWorldCodec` specs + `GameDataRegistry`). `quests` stays a top-level table; the rest are child tables of `quests` (via `parent_position`, as `maprespawns` is of `maps`), except `conditions`, `texts` and `storylines`, which are top-level so other systems can use them.

The SQLite store has no SQL foreign keys today; integrity is checked in code (see Validation). The `→` arrows below are logical references.

**Codec constraints** (`SqliteWorldCodec.cs`, `SqliteWorldFile.cs`), which shape the tables below:
- Every table spec needs a `GameDataRegistry` entry with an entity type, and every registry table needs a spec (checked both ways at start).
- Child tables are one level deep under a top-level parent, keyed `(parent_position, position)`, where `parent_position` is the parent's **list position**, not its `Index`. So steps, objectives, branches, endpoints and rewards are all direct children of `quests` (objectives carry a `Step` id column; they are not children of steps).
- The row key is an INTEGER (`Func<object,int>`), so every table has an int `Index`; string keys such as `texts.Key` are ordinary unique columns.
- **New index counters are not added by the additive migration** (`CounterNames` is a fixed list; Decode refuses a store without a counter). `QuestStepIndex`, `QuestObjectiveIndex`, `QuestConditionIndex`, `QuestGroupIndex`, `QuestTextIndex`, `QuestVarIndex`, `StorylineIndex` each need a written store migration in phase 1.
- **Any new column makes the file unreadable to an older build**: `ReadAll` fails `CheckColumns` on columns it does not know (`SqliteWorldFile.cs` ~L160, ~L327-342; the Migrate comment ~L179-185 says so). So phase 1 cannot roll back by flag; it rolls back by restoring the automatic pre-migration `VACUUM INTO` copy of `SystemData.db`.
- Removing columns (the legacy `quests` columns in phase 4) also needs a written migration, for the same reason.
- `MirDbExport export-binary` (SQLite → `Server.MirDB`, `tools/MirDbExport/StoreCommands.cs` ~L123) cannot carry the new tables; a binary rollback file has legacy quests only.
- `WriteAll` rewrites every table on every save, so per-quest change detection (`Revision`) is done in code: a **pre-save step on `Envir.Edit`** (`QuestDefinitions.PrepareForSave`) recomputes `ShapeHash` / `Revision` on **every** save path (Quests editor, JSON import, importer, any generic `GameDataSave`), so no path can skip it. It also runs validation, but **validation blocks only the quests the Quests editor or a JSON import changed in that save**; on every other path (an item, monster or NPC save, the importer) it only reports, so legacy quest data can never block an unrelated design save (see Validation).
- **Registry entries are read-only grids until the phase 3 editor exists.** A registry entry gives a generic Game Data grid; edits there would skip quest-level checks and be overwritten by the next re-import. Phase 1 registers every new table read-only in the generic grid; from phase 3 the Quests editor (and the importer, until phase 4) are the only writers.
- **LiveDb classification:** every new table and `quests` column gets a `LiveDbClassification` entry in phase 1 (the classification test fails otherwise): **RestartOnly** in phases 1-2, while legacy `QuestInfo` is still the runtime; moved to the quest-replace path in phase 3 (see Live edits). The position-keyed child tables are never applied row by row through LiveDb (server.md §2.7: position-keyed children diverge); a quest is always replaced whole.

#### `quests`
| Column | Type | Notes |
|---|---|---|
| `Index` | int PK | Stable; existing quest indexes are kept |
| `Key` | text unique | Author key for export/import |
| `NameKey`, `SummaryKey`, `CompleteKey` | text → `texts` | `CompleteKey` = turn-in text |
| `Category` | byte enum `QuestCategory` | Story / Side / Daily / Weekly / Repeatable / Event / Guild / Hidden (replaces `QuestType`; see Enum notes) |
| `StorylineIndex`, `StorylineOrder` | int → `storylines`, int | 0 = none |
| `LevelHint` | int | For "recommended" colouring; not a gate |
| `AcceptCondition` | int → `conditions` | 0 = always. Replaces `RequiredMinLevel/MaxLevel/RequiredQuest/RequiredClass` |
| `VisibleCondition` | int → `conditions` | When the "!" shows, if different from accept (e.g. show at level − 2, grey) |
| `RepeatPolicy` | byte enum | Once / Daily / Weekly / Cooldown / Unlimited |
| `CooldownSeconds` | int | For Cooldown |
| `MaxCompletions` | int | 0 = unlimited (for repeatable policies) |
| `ShareMode` | byte enum | None / Offer (today's share) / AutoParty |
| `CreditMode` | byte enum | Solo / PartyInRange (today) / Contribution (shared quests) |
| `TimeLimitSeconds` | int | 0 = none; counted from accept |
| `FailPolicy` | byte enum | Abandon / Fail (stays in log as failed, retake allowed) / Restart step |
| `AbandonAllowed` | bool | Story quests may forbid it |
| `Revision` | int | Bumped when the quest's **shape hash** changes: on save, `Envir.Edit` hashes the quest's steps, objectives (ids, types, targets, counts) and branches, compares it with `ShapeHash`, and increments `Revision` only on a difference. Text and reward edits do not bump it. Used by state migration |
| `ShapeHash` | long | See `Revision` |
| `PoolIndex` | int → `questpools` | 0 = none. Daily/weekly rotation: only the pool's current picks are available |
| `StartItemConsumed` | bool | An `ItemUse` start endpoint uses up the item on accept |
| `Flags` | int bitmask | AutoAccept, AutoComplete (no turn-in), HiddenInLog, Account-wide (reserved) |
| `GroupName` | text | Legacy `QuestInfo.Group`, imported as is; log grouping and filter until storylines are authored |

#### `queststeps` (child of `quests`)
| Column | Type | Notes |
|---|---|---|
| `Index` | int | Stable step id (global counter `QuestStepIndex`) |
| `Order` | int | Display and default next-step order |
| `TitleKey`, `TextKey`, `ReadyTextKey` | text → `texts` | Tracker line and log text for this step; `ReadyTextKey` replaces them while the quest is Ready on this step (optional) |
| `CompletionMode` | byte enum | All / Any / NOf |
| `NRequired` | int | For NOf |
| `NextStep` | int | 0 = the next by `Order`; −1 = ready to turn in |
| `TimeLimitSeconds` | int | 0 = none; counted from entering the step |
| `FailCondition` | int → `conditions` | The step fails when it becomes true (left the map, died, escort target dead via a `Counter`); 0 = never. Applies the quest's `FailPolicy` |
| `OnEnterPage`, `OnCompletePage` | text | Optional NPC script page names (the escape hatch) |

#### `questobjectives` (child of `quests`, with `Step` column)
| Column | Type | Notes |
|---|---|---|
| `Index` | int | Stable objective id (global counter `QuestObjectiveIndex`), **never reused** |
| `Step` | int → step `Index` | |
| `Order` | int | |
| `Type` | byte enum `QuestObjectiveType` | Designed now: Kill, Collect, Deliver, Talk, ReachArea, UseItem, Craft, Gather (mine/fish), ReachLevel, UseSkill, Counter (script-driven), Flag (done while global `Flags[TargetId]` is set; the import target of legacy `[@FLAGTASKS]`). **Reserved values only** (no runtime, no `Params` schema until a later plan): Interact, Escort, Defend, PvpWin, Equip |
| `PartyCredit` | byte enum | Each (every member in range gets +n, today's behaviour) / Shared (one counter for the party; reserved until shared quests) / Self (killer only) |
| `TargetKind` | byte enum | None, Monster, MonsterGroup, Item, ItemGroup, Npc, MapObject, Map, Spell, Flag |
| `TargetId` | int | id in the table that `TargetKind` names |
| `Count` | int | Required amount (1 for binary objectives) |
| `Map`, `X`, `Y`, `Radius` | int | Area for ReachArea / Defend, and the **marker hint** for every type (0 = no marker) |
| `Optional` | bool | Bonus objective; does not block the step |
| `Hidden` | bool | Not shown until it has progress |
| `Condition` | int → `conditions` | Counts only while true (night only, in party, wearing X) |
| `TextKey` | text → `texts` | Optional override; default text is built on the client from type + target + count |
| `DropChance` | int (per 10,000) | Collect objectives: the quest drop chance, replacing quest drops defined in item drop files |
| `Params` | text (JSON) | Reserved for the later types (escort route, defend waves). Empty for every designed type |
| `LegacySource` | text | Importer only: the legacy line this row came from (quest + section + target), so a re-import keeps the same objective id. Empty for authored rows; dropped in phase 4. Rewards and endpoints carry the same column |

#### `questgroups` (top-level) and `questgroupmembers` (child)
Named sets of monsters or items ("any wolf", "any ore") for `MonsterGroup` / `ItemGroup` targets. `{ Index, Key, Kind }` + `{ TargetId }`. The importer creates one group per legacy kill task holding every monster whose name starts with the task's name (today's `StartsWith` rule), **without overlap inside a quest**: a monster goes only to the first task (in file order) whose prefix matches it. That is what legacy does: `ProcessKill` credits only the first matching task, even when that task is already complete, then returns (`QuestProgressInfo.cs` ~L384-392). Without it, "Wolf" and "WolfKing" tasks in one quest would both count a WolfKing kill. Tasks left with an empty group, or two tasks on the same monster (legacy saves key progress by MonsterID, so they collapse at load), are listed by the dry-run report.

#### `questpools` (top-level) and `questpoolmembers` (child)
`{ Index, Key, Picks, RotatePolicy (Daily | Weekly) }` + `{ Quest, Weight }`. At each reset boundary the server picks `Picks` quests by weight with a seed derived from the pool and the boundary date, so every player sees the same board and nothing needs saving.

#### `questbranches` (child of `quests`)
| `Step` | `ChoiceKey` | `TextKey` | `NextStep` | `Outcome` | `Condition` |
|---|---|---|---|---|---|

A step with branches ends with a choice (made at an NPC or in the window). `Outcome` (int, per quest) is recorded in history and can be checked by later conditions ("sided with the miners").

#### `questendpoints` (child of `quests`)
| `Role` | `Kind` | `TargetId` | `Map` |
|---|---|---|---|
| Start / Finish | Npc / ItemUse / AreaEnter / Auto (Board reserved) | `NPCInfo.Index` / item id / … | map (for Area) |

This replaces the NPC script `[QUESTS]` section for giving out quests. A quest can have several givers. Npc endpoints store the **`NPCInfo.Index`** (stable), not the runtime `ObjectID` the legacy fields hold. `Auto` = offered when the accept condition becomes true (level-up quests).

#### `questrewards` (child of `quests`)
| Column | Notes |
|---|---|
| `Scope` | Quest (on turn-in) / Step (on step completion) / Objective (bonus objective) / Outcome (branch outcome) / **Accept** (given on accept into the quest bag, `Kind = Item` only; taken back at turn-in or abandon, like legacy `[@CARRYITEMS]`) |
| `ScopeId` | step / objective / outcome id; 0 for Quest |
| `Kind` | Exp, Gold, Credit, Item, Reputation, Title, Spell, Unlock (feature or map flag), Buff, Currency, ScriptPage |
| `TargetId`, `Amount` | e.g. item id + count, faction id + points |
| `Scaling` | Flat / ByLevel (formula id). **On top of `Scaling`, Exp and Gold keep the server rate multipliers** (`Settings.ExpRate`, `Settings.DropRate`), as `FinishQuest` applies them today (`PlayerObject.cs` ~L12962-12963), for imported and authored rewards alike |
| `ChoiceGroup` | 0 = always given; n = "pick one from group n" |
| `Condition` | → `conditions` (class/gender filters; replaces `(M)`/`(F)` name suffixes). **Deliberate bug fix:** today both gendered items are given; after import each gets a Gender condition, so a player gets only theirs |
| `FirstTimeOnly` | bool: only on the first completion of a repeatable quest |

#### `conditions` (top-level, shared)
One table, a small expression tree:

| Column | Notes |
|---|---|
| `Index` | Stable id |
| `Op` | And / Or / Not / Leaf |
| `Parent` | parent condition id (0 = root); children ordered by `Order` |
| `Leaf` | Level, Class, Gender, QuestStatus (quest, status), QuestCompletedCount, QuestOutcome, ItemCount, Equipped, Reputation, Guild, GuildRank, InParty, PartySize, Flag (legacy global flag), QuestVar, TimeOfDay, DayOfWeek, MapIs, Married, HasHero |
| `Arg1`, `Arg2` | ints (ids, values) |
| `Cmp` | Eq / Ne / Lt / Le / Gt / Ge |

A quest's accept rule is one root id. Each condition row has an `OwnerQuest` column (0 = shared, for use by other systems): the editor deletes a quest's owned conditions with it, and validation forbids a quest referencing a subtree owned by another quest.

#### `questvars` (child of `quests`)
`{ Index, Key, Default }`: the quest-scoped variables used by `QuestVar` state, the `QuestVar` condition leaf and `SETQUESTVAR` / `CHECKQUESTVAR`. `Index` is a stable id from the global counter `QuestVarIndex` (never reused); `Key` is the author name used in scripts and export files. No legacy data maps to it.

#### `texts` (top-level, shared)
`{ Index, Key, Lang, Body }`, unique on `(Key, Lang)`, with the existing quest markup (`{text/Colour}`, `[ITEM|MONSTER|NPC:n]` links) kept as is. **Decided 2026-10-24: server language only.** Every row has `Lang = ""` (the server's language) and the server sends that text to everyone, as today. The column exists so per-player language can be added later as data plus a client → server language field, with no table migration and no change to `S.QuestDefinition` (it still carries one string per key). The editor shows one text box per field.

#### `storylines` (top-level)
`{ Index, Key, NameKey, Order }`: a chapter list for the storyline view.

#### Enum notes
- **New** enums (`QuestCategory`, `QuestObjectiveType`, `QuestTargetKind`, `QuestRepeatPolicy`, `QuestRewardKind`, `QuestConditionLeaf`, …) are stored by value in `SystemData.db` and in user data: explicit values, append-only, never renumbered (AGENTS.md persisted-enum rule).
- `QuestType` stays in `Shared/Enums.cs` until the legacy packets are removed (phase 4); `Category` is set from it by the importer.
- The new state record is named `QuestRunState`, not `QuestState`: `Shared/Enums.cs` already has `enum QuestState { Add, Update, Remove }` (~L178).

#### Validation (on every save through `Envir.Edit`)
- Every reference resolves (target ids exist for their kind; steps and objectives belong to the same quest; `NextStep` and branch targets exist; no step can be reached from no path; no loop without a branch).
- Condition trees: no cycles, leaves have valid args, `Not` has one child.
- Objective `Type` and `TargetKind` are compatible (Kill → Monster/MonsterGroup, Collect → Item/ItemGroup, Talk → Npc, ReachArea → Map with radius > 0, Flag → Flag with a number `0 … FlagIndexCount − 1000` inclusive, as legacy `ParseFlag` (`QuestInfo.cs` ~L340) and `ScriptValidation` accept, …).
- Warning (not an error): two quests that can be active together `Collect` the same item, since they share the quest-bag count (see Rules).
- Every `*Key` text exists (with `Lang = ""`).
- Retired objective ids are not reused (checked against the counter).
- Every quest variable referenced by a condition or script keyword exists in that quest's `questvars`.
- Size: the quest's `S.QuestDefinition` encodes within 65,535 bytes.
- **When failures block:** only for quests changed in that save by the Quests editor or a JSON import (phase 3 on); those saves are refused with a list of every error (the WinForms editor is not extended; see Authoring). On every other save path, including all saves in phases 1-2 and the importer, failures are **reported** (log + the web admin validation list) and the save goes ahead, so imported legacy data with real errors (expected; the dry-run report lists them) never blocks an item, monster or NPC save.
- **Deleting a referenced target** (a monster, item, NPC or map that a quest objective, endpoint, reward, group or condition points at): from phase 3 the generic delete is refused with the "where used" list, so the author fixes the quests first. In phases 1-2 (importer data only) it is allowed and the dangling reference is reported; the next re-import drops it.

### Character state (user data, `UserData.db` account blobs)

Records saved by `CharacterInfo.Save` in a new block, written from `Envir.Version` 121 on (the single bump shared with Part A, decision 10). The layout is written as tables because that is what it is; in the binary file each "table" is a count followed by records.

**No conversion of old saves (wipe decided 2026-10-24).** Loading a file older than 121 reads the legacy `CurrentQuests` / `CompletedQuests` fields and discards them, so the character starts with no quest state. That keeps restored old backups and the `Server.Persistence.Tests` v120 fixtures loadable with no converter to maintain; `MinVersion` stays 60. The wipe itself is the Runbook's wipe procedure at ship, not code.

```text
QuestRunState     per active or failed quest
  Quest        int      quest Index
  Status       byte     Active | Failed (Ready is derived, never saved; see Rules)
  Step         int      current step id, or −1 = turn-in pending (every step done;
                        reached through NextStep = −1 or the migration below)
  Revision     int      definition revision when last migrated
  AcceptedAt   long     DateTime.ToBinary (UTC)
  ExpiresAt    long     0 = no limit
  StepExpiresAt long    0 = no step limit; set on entering a step with TimeLimitSeconds
  SharedFrom   int      character index of the sharer, 0 = none
  Branch       int      outcome chosen in this run, 0 = none

ObjectiveState    per objective with progress (no row = 0); never for Collect,
                  Deliver or Flag, which are read from the bag / inventory / Flags[]
  Quest        int
  Objective    int      objective id (NOT monster or item id)
  Value        int      count, or a bitmask for multi-spot objectives

QuestHistory      per quest ever completed (replaces CompletedQuests: List<int>)
  Quest        int
  Times        int
  FirstAt      long
  LastAt       long
  LastOutcome  int

QuestVar          quest-scoped variables (new; the global Flags[] stays for scripts)
  Quest        int      (Storyline/Global scopes reserved: Global would duplicate Flags[])
  Key          int      variable id from the definition, not a string
  Value        int
```

Tracking stays client-side (`Settings.TrackedQuests`, `QuestTracking.ini`), as today; the server only sends a "track on accept" hint.

**Rules**
- **Availability** = `AcceptCondition` true **and** the repeat policy allows it: `Once` → no history row; `Daily`/`Weekly` → `LastAt` before the last reset boundary; `Cooldown` → `now − LastAt ≥ CooldownSeconds`; `MaxCompletions` → `Times < Max`. Boundaries come from new settings `[Quests] DailyResetHour` (default 0) and `WeeklyResetDay` (default Monday; both decided 2026-10-24), in UTC like `Envir.Now`; the boundary maths lives in a shared `ResetClock` helper, not in quest code, because instance lockouts will use the same boundaries (today's reset is a hard-coded 00:00 UTC day change). No bulk clear job, so `Envir.ClearDailyQuests` goes away. Side benefits: a server that was down over the reset no longer skips it, and the new day no longer has to invalidate the whole offline account cache for quests.
- **Quest items stay in the quest bag** (`QuestInventory`, 40 slots, cannot be traded, dropped or sold), with today's party rule: a quest drop goes straight into the bag of every party member in range whose objective needs it (`PartyCredit = Each`). **Each member gets their own clone with a new `UniqueID`**: today `CheckGroupQuestItem` hands the same `UserItem` instance to every member and `AddQuestItem` stores it as is, and a partial stack does `item.Count -= …` on the shared object (`PlayerObject.cs` ~L13015-13035, ~L8813-8821, ~L1911-1935; server.md §3.8 Known defect).
- **Collect, Deliver and Flag progress is read, not saved.** `Collect` = the quest-bag count of the target, `Deliver` = the main-inventory count, `Flag` = `Flags[n]`. Two active quests collecting the same item both see the same bag count, and turning in one takes the items, so the other's count drops (legacy behaviour too); validation warns about such pairs. This keeps progress unbuyable through trade, adds no main-inventory pressure and needs no `UserItem` change (a `UserItem` field would be a wire change, because `UserItem.Save` serves packets and the DB). `Collect` counts what is in the quest bag; `Deliver` counts ordinary items in the main inventory, recounted once per tick from a dirty flag set by the inventory-change paths, because inventory slots are assigned in many places with no single hook.
- **Ready is derived.** A quest is Ready when `Step = −1` (turn-in pending, saved), or when its current step's required objectives are met and that step's `NextStep` is −1 (the last step). This is worked out at load, after migration, and whenever an event, a quest-bag change or a flag change touches one of its objectives; the result is sent in `S.QuestStates`. It is never saved, because Collect/Flag progress is read live and can go back down (another quest's turn-in takes the shared items, a script clears a flag), which would leave a stored Ready stale.
- **Flag objectives (deliberate change).** Legacy only ever *raises* flag progress: `NPCSegment` calls `CheckNeedQuestFlag` only when a flag is set (`NPCSegment.cs` ~L3602 `if (flagIsOn)`), `ProcessFlag` never clears a task, and its loop `j < Flags.Length - 1000` never reaches the top flag number `ParseFlag` accepts (`QuestProgressInfo.cs` ~L414 vs `QuestInfo.cs` ~L340), so a task on that one flag can never complete. **V2: a Flag objective is met while `Flags[n]` is set, read live**, so a script that clears the flag afterwards makes the quest not Ready again until it is set again; the top flag number works. The importer's dry-run report lists every quest whose flag is ever cleared by a script (`RESET` / `SET … 0` on that number), so an author can check it; the differential test lists both differences as expected.
- **Time limits** are stored as `ExpiresAt` / `StepExpiresAt` (absolute), so they keep counting while offline, like today (`StartDateTime` + limit). On load, an expired quest is applied with its `FailPolicy`.
- **Concurrent cap** stays `Globals.MaxConcurrentQuests` (20), counted over `Active` runs (Ready quests are Active runs).

#### Definition edits vs saved state
On login, and when the LiveDb quest-replace hook applies a quest edit (online characters, on the game thread), each `QuestRunState` whose `Revision` differs from the definition is migrated:
1. Drop `ObjectiveState` rows whose objective no longer exists.
2. If `Step` (≥ 0) no longer exists: move to the first remaining step by `Order`; if none remain, set `Step = −1` (turn-in pending; Ready follows from it).
3. Clamp `Value` to the new `Count`.
4. Re-run step completion (an edit can make a step complete) and re-derive Ready.
5. If the step changed, reset `StepExpiresAt` from the new step's limit.
6. Set `Revision` and send the client the new state.

Since objective ids are never reused, an id that still exists still means the same task.

### Instances (not designed here)

Instances come in a later plan. The server already has procedural dungeon instances (server.md §4.12): each run is a runtime `MapInfo` with an index from `DungeonSystem.IndexBase` (1 900 000 000) up, never in `MapInfoList`, and with no template. New enum values (an instance objective type, an `InDungeon` leaf, a dungeon endpoint kind, a lockout leaf) can be appended later at no cost, so nothing is reserved. Fixed now, because it shapes definitions and saved state:

1. **Map references mean the template map.** `questobjectives.Map`, `questendpoints.Map` and the `MapIs` leaf hold the `MapInfo.Index` of a design map, never a runtime map. `QuestEvent.Map` carries the **template index** of the player's map: the map's own index for a normal map, the source map's index for a copied instance, 0 for a generated dungeon (matched later by profile, not by map).
2. **Saved quest state never holds a runtime map index** (anything in the `IndexBase` band). Nothing in the state records does today; a later field must not either.
3. **Party credit "in range" compares the map object** (`player.CurrentMap == CurrentMap`, as legacy does), never the map index, so two copies of one dungeon stay apart.
4. **`ResetClock`** (daily/weekly boundaries) is shared with future instance lockouts (see Rules).
5. **Shared quests** get an `Instance` scope (see below).

Left for the instance plan: instance objectives (clear, boss, timed run), dungeon endpoints and lockout conditions, markers inside generated dungeons (positions exist only after generation, so they would be sent at runtime), difficulty-scaled rewards.

### Shared quests (not designed here)

Party, guild, world and instance quests with contribution scoring are a separate plan (phase 6). Scopes: party, guild, world, and **instance** (one counter per dungeon run, dropped when the instance closes). This design only reserves the enum values (`CreditMode.Contribution`, `PartyCredit.Shared`, `ShareMode.AutoParty`) so that plan needs no renumbering. Expected shape: an Envir-level state (quest, scope, scope id, step, objective values) plus a contribution score per character, with rewards scaled by score above a minimum.

### Live edits

**Decided 2026-10-24: live, through a LiveDb "quest replace" hook** (not reboot-only). `GameDataSave` only writes `Envir.Edit` to disk; Main changes at runtime only through LiveDb (`LiveDbMainApplier`: before-images, rollback, classification, push) or at a reboot (`WebAdminPending`). Quest edits get their own LiveDb path, from phase 3:
1. The Quests editor (or JSON import) saves the quest on `Envir.Edit` under `EditLock` (pre-save validation, `ShapeHash` / `Revision`), then queues a **quest replace** for the changed quest indexes.
2. On the game thread (`Envir.QueueAdminAction`), the hook builds a **fresh Main copy of the whole quest graph** (quest row, steps, objectives, branches, endpoints, rewards, quest vars, its owned conditions and its texts) from the saved data, never by copying Edit objects into Main (server.md: NEVER share Edit objects with Main). Referenced monster/item/NPC/map ids are resolved against Main; an id missing in Main fails the replace for that quest.
3. It swaps the quest into Main's lookup tables in one step, keeping the old graph as the before-image so a failure rolls back like other LiveDb applies.
4. For every online character with a run of that quest: revision migration (below), rebuild that quest's entries in the objective index, mark availability dirty.
5. Push `S.QuestDefinition` to connections that were sent it (and clear it from the connection's sent set otherwise), plus `S.QuestStates` / `S.QuestProgress` for migrated characters; refresh `S.QuestAvailability` for players near affected givers.
6. Deleting a quest: online runs of it are dropped (logged), history rows stay (harmless, ignored).

**Every holder of a definition reference** that the swap refreshes (so nothing keeps pointing into the old graph): each online player's objective index (`ObjectiveRef`s) for that quest, their availability cache, their due-time timer (step and quest expiry), the NPC endpoint lookup (NPC index → quests offered / finished), the pool picks, and the per-connection sent set. **The sent set is keyed by quest `Index`** (a `HashSet<int>`), not by object: today `Connection.SentQuestInfo` is a `List<QuestInfo>` compared by reference (`MirConnection.cs` ~L89, `LiveDbHooks.cs` ~L635-671), which a fresh graph would break.

**Shared top-level tables** (`questgroups` + members, shared `conditions` with `OwnerQuest = 0`, `texts`, `questpools` + members, `storylines`) have no row-by-row live path. An edit to one of them **replaces every quest that references it**, found through a reverse index (row → quest indexes) rebuilt from Main at each replace; `texts` and `storylines` changes replace no quest but re-send `S.QuestDefinition` to connections that hold an affected quest. Until the reverse index exists (it ships with the hook in phase 3) these tables stay RestartOnly.

Offline characters migrate at their next login. Until phase 3 the new tables are RestartOnly (see Codec constraints). The V2 replacements of today's live-edit paths:
- `S.GameDataChanged` today embeds `List<ClientQuestInfo>` (`ServerPackets.cs` ~L6921, TS `server.ts` ~L536). Its quest list is **removed** from that packet (a wire change: manifest regen, TS port, `WebRelay/Fixtures.cs`, `GameDataChangedTests`); quest changes are pushed by step 5 above instead.
- `QuestReload` (today re-points online progress and refuses shape-changing edits) is replaced by the revision migration above, which handles shape changes instead of refusing them.
- GM `SETQUEST` and the web admin character quest routes (`CharacterRoutes.cs`) are rewritten against `QuestRunState` / `ObjectiveState` / `QuestHistory` (set step, set objective value, complete, reset history) in phase 2. For an online character the web admin route runs on the game thread through `WebAdminGame.RunAsync` and calls the same `PlayerQuests` methods as the GM command, so the in-memory index and the client stay in step. For an offline character (or a stopped world, where there is no `PlayerQuests` and Main may hold no definitions) the route validates against `Envir.Edit`'s definitions, runs the revision migration on the loaded `CharacterInfo` first, then edits the records.

### Runtime

#### Event pipeline (game thread)
Game code raises typed events on `PlayerObject` through one entry point:

```csharp
// one struct, no allocation per event
internal readonly struct QuestEvent { public readonly QuestObjectiveType Type; public readonly QuestTargetKind Kind;
                                      public readonly int TargetId; public readonly int Amount; public readonly int Map, X, Y; }
player.Quests.Raise(in QuestEvent e);
```

Raise points: monster death (already deferred with `RunOutsideMobPass`; party credit fans out from there), quest drop into the quest bag (`CheckGroupQuestItem`, as today), main-inventory change (sets the Deliver dirty flag), item used, NPC talk (on opening an NPC dialog), map enter and cell change (ReachArea: only when the player crosses into a watched map, then per step through a per-map list of watched areas), craft, mine/fish result, level-up, spell cast, script `QUESTCOUNTER` action, script flag `SET` (where `NPCSegment` calls `CheckNeedQuestFlag` today, ~L3602; raises `Flag` objectives, both on and off).

**Never raise from a mob thread.** A raise point that can run in a mob pass (anything under `MonsterObject.Process`) uses `Envir.RunOutsideMobPass`, like kill credit today.

#### Per-player objective index
`PlayerQuests` (new, owned by `PlayerObject`) keeps
`Dictionary<long, List<ObjectiveRef>>` keyed by `(Type, TargetKind, TargetId)` packed into a long, plus a small list for target-less types (ReachLevel, UseSkill without a target). Group targets add one key per group member. The index is rebuilt for one quest when it is accepted, changes step, or is migrated. Events look up one key: no walk over all quests, no LINQ. Lists are reused, no allocation per event.

`Collect` objectives count what the quest bag holds (as `ProcessItem` does today). `Deliver` objectives count the main inventory, recounted once per tick when the player's inventory dirty flag is set. Kill credit keeps today's rule (player kills of monsters without a master; hero kills could be added later as a deliberate change).

#### Availability and NPC icons
Which quests an NPC offers depends on conditions (level, items, other quests). Each player caches available-quest results, marks the cache dirty on events that can change a condition leaf (level, class change, quest state, inventory of an item a condition watches, guild, party), and re-evaluates only the dirty quests when the player next opens or sees a giver. `S.QuestAvailability` sends, for the NPCs in view, each NPC's `ObjectID`, its existing `QuestIcon` value (`Shared/Enums.cs` ~L166; **the server picks the highest-priority icon** the way the client's `UpdateBestQuestIcon` does today, `NPCObject.cs` ~L383-398) and the quest ids it offers or finishes for this player (for the NPC dialog's quest list). It replaces today's per-NPC `QuestIDs` list plus the client-side `CanAccept` checks on desktop (classic and modern NPC windows, minimaps) and web (`npcSession.ts`, NPC windows, minimap, big map).

**Rewards in `S.QuestDefinition` are filtered by class and gender**, and the definition is cached per connection, so a class or gender change (e.g. a gender-change item or GM command) clears the connection's sent set and re-sends the definitions of the player's active quests and of givers in view.

#### Accept / complete / turn in
- Accept: endpoint check (NPC in range and visible to the player, as today with `npc.VisibleLog`, `PlayerObject.cs` ~L12775; item use; area; auto), `AcceptCondition`, availability, cap → create state, give `Scope = Accept` rewards (carry items into the quest bag), rebuild index, evaluate read objectives (`Collect`, `Deliver`, `Flag` already met count at once), send state.
- Step complete: rewards with `Scope = Step`, `OnCompletePage`, branch or `NextStep`, rebuild index, send state.
- Turn in: finish endpoint check, bag space for all rewards (as `CanGainItems` today), take objective items (`Deliver` from the inventory, `Collect` and carry items from the quest bag), give rewards, write history, run `DefaultNPCType.OnFinishQuest` (kept), send state.
- `AutoComplete` quests turn in when they become Ready.
- Step timers and `FailCondition`: checked from a per-player due time (the earliest `ExpiresAt` of any quest or step) and when an event dirties a condition leaf, not every tick.

#### Telemetry
Each accept, step complete, fail, abandon and turn-in writes one line to a quest log (log4net logger `Quests`, like the other server logs) with character, quest, step and seconds since accept. The web admin can later show funnels (accepted → step n → done) from it. No per-progress logging. The logger needs a `Quests` appender in `Server.MirForms/log4net.config`; that file lands in `Build\` only through the user-run deploy, so the phase that adds it says so in its deploy notes.

#### NPC script integration
Kept, with new internals: `CHECKQUEST` (`NPCSegment.cs` ~L2522-2537 reads `CurrentQuests` / `CompletedQuests` today; **rewritten** in phase 2 against `QuestRunState` / `QuestHistory`, same syntax and meaning), `[QUESTS]` (imported as `Npc` endpoints, then the section is ignored with a warning after phase 4), `OnAcceptQuest` / `OnFinishQuest` default NPC calls, global flags. Added: checks `CHECKQUESTSTEP q s`, `CHECKQUESTVAR`, `CHECKQUESTOUTCOME`, `CHECKCONDITION id`; actions `QUESTCOUNTER q objective n`, `SETQUESTVAR`, `QUESTSTEP q s` (force a step), `OFFERQUEST q` (opens the accept prompt). Script validation in the web admin (`ScriptValidation.cs`) learns the new keywords.

### Wire protocol

New packets, **ids appended at the end** of `ServerPacketIds` / `ClientPacketIds` (all four places plus the Shared.Tests manifest, the TS port and `WebRelay/Fixtures.cs`):

| Packet | Direction | Content |
|---|---|---|
| `S.QuestDefinition` | S→C | One quest's definition in the server's language (decided 2026-10-24; one string per text key): steps, objectives (type, target kind/id, count, marker map/x/y/radius, optional, hidden), rewards (filtered by the player's class/gender), endpoints, texts. Sent **on demand**, once per connection (the sent set, keyed by quest `Index`; see Live edits): for quests the player has active or failed (at login), for quests offered by givers in view (with `S.QuestAvailability`), and for a history quest when the client opens it in the Completed tab (`C.QuestDefinitionRequest`). The Completed tab itself needs only names, which `S.QuestHistory` carries, so a long history is no login burst. Not every quest at login as `GetQuestInfo` does today (`PlayerObject.cs` ~L2704-2708). The targets' `ItemInfo`/`MonsterInfo`/`NPC` names follow the existing on-demand rules |
| `S.QuestStates` | S→C | Batch of `(Quest, Status, Step, ExpiresAt, StepExpiresAt, TrackHint)` on login and on every change; `TrackHint` only on accept (the client decides, as today) |
| `S.QuestProgress` | S→C | Batch of `(Quest, Objective, Value)`; coalesced per tick per player, so a 10-kill AoE sends one packet |
| `S.QuestHistory` | S→C | `(Quest, Name, Times, LastAt)` for the log's Completed tab and repeat timers |
| `S.QuestAvailability` | S→C | Per NPC in view: `ObjectID`, `QuestIcon` (chosen by the server), quest ids offered / finished for this player |
| `S.QuestOffer` | S→C | Accept prompt from a non-NPC endpoint (item, area, auto, share) |
| `C.QuestAccept` / `C.QuestTurnIn` / `C.QuestAbandon` / `C.QuestShare` / `C.QuestChoose` | C→S | quest id (+ endpoint NPC `ObjectID` on accept / turn-in / choose, since a quest can have several givers, as legacy `C.AcceptQuest` sends it; + reward choice group picks, + branch `ChoiceKey`) |
| `C.QuestDefinitionRequest` | C→S | quest id; answered with `S.QuestDefinition` only for a quest in the player's history or runs |

All ≤ 65,535 bytes: `S.QuestDefinition` for a very large quest is checked by validation. Packets are private (`Observable => false`) and **none is hero-forwarded** (not added to the `HeroObject.Enqueue` whitelist). Per the server's encode-once rule (server.md), packets are built from snapshot copies of the definition and state values, never by handing Main's records to a packet.

**Existing packets that change** (same commit, manifest regen, TS port, fixtures):
- `S.ObjectNPC.QuestIDs` (`ServerPackets.cs` ~L2861, TS `server.ts` ~L687, `WebRelay/Fixtures.cs` ~L58): field removed; `S.QuestAvailability` replaces it.
- `S.GameDataChanged`: its `ClientQuestInfo` list is removed (see Live edits).

**No legacy protocol.** Server and both clients ship V2 together, from the same commit (AGENTS.md rule after a `Shared/` change), with the patch root updated before the server starts (Runbook §9). There is no `Protocol` flag and no projection into `ClientQuestInfo`. **An unpatched client cannot play at all**, not just without quests: `S.ObjectNPC` changes layout, which every client decodes, and an old desktop client misreads it on its socket thread and crashes (AGENTS.md packet rule); unknown V2 ids would also make it discard its receive buffer. So D2 refuses old executables at login through the version check (`[General] CheckVersion` / `VersionPath`, `Settings.cs` ~L44-45; set up at the wave 2 kick-off), and the web client publish at D2 cache-busts the bundle. The legacy quest packets go unused from the ship and are removed in phase 4. Because ids are implicit ordinals and the Shared.Tests manifest is keyed by side + id + **name** (append-only), removal means: the `ServerPacketIds` / `ClientPacketIds` members **stay, with their original names, marked `[Obsolete]`**; only the packet classes and their `Packet.cs` factory cases go, and the names are added to `KnownServerFactoryGaps` / `KnownClientFactoryGaps`; then re-run `WebRelay --emit-ts`. **`S.GainedQuestItem` and `S.DeleteQuestItem` stay**: they update the quest bag, which V2 keeps.

### Clients (desktop modern + web)

- Text built on the client from `(Type, Target, Count, Value)` with `ClientTextKeys` (e.g. `QuestObjKill` = "Kill {0}: {1}/{2}"), unless the objective has a `TextKey`.
- Objective bars and counts in `QuestsWindow` and `TrackerHud` (the "objective bars hidden" note goes away); step list with done/current/future; failed quests in the log.
- Map markers: big map and minimap draw the marker hint of every objective of each tracked quest's current step (area circles for ReachArea), plus the turn-in NPC when Ready. The Map button centres on the next marker.
- Available tab, NPC quest icons (overhead, minimap, big map) and the NPC dialog's quest list driven by `S.QuestAvailability` (no client-side copy of `CanAccept`).
- Storyline view: chapters → quests with done/current/locked.
- Repeat timers on daily/weekly/cooldown quests in the Completed tab.
- Progress toasts (a short bar on progress, a completion banner).
- Classic UIs (desktop `QuestDialogs.cs`, web classic): a minimal V2 adapter in phase 3 (current step's objectives as client-built text lines, accept / turn in / abandon), no bars, markers or new classic features.

### Authoring

- Web admin **Quests** editor (new page, `Server/WebAdmin` + `wwwroot/admin`): quest list with filters; quest form; step/objective tree with pickers for monsters/items/NPCs/maps (existing lookup endpoints); condition builder; reward table; one text box per text field (server language; see `texts`); the full validation list on save; "where used" (which quests reference this monster/item/NPC). Edits are saved on `Envir.Edit` under `EditLock` and go live through the LiveDb quest-replace hook (see Live edits).
- **Export/import:** one JSON file per quest (by `Key`, references written as keys/names *and* ids, ids authoritative), for diffs and bulk work. Import validates and shows a diff before applying.
- **No content freeze.** Until the ship, the legacy quest bodies stay the source of truth: editable in the Scripts editor, applied live by `QuestReload`, and the V2 tables are a derived copy that the importer rebuilds (same ids through `LegacySource`). At the ship the importer runs one last time, then the legacy bodies become read-only in the web admin and `QuestReload` stops. From then on quests are edited only in the Quests editor, which ships in the same release. The WinForms quest editor is not extended; it is disabled for V2 quests (Known debt: WinForms editors touch `Envir.Edit` directly; not changed here).

### Quest phases (reference; scheduling is in the combined execution plan)

These phases describe the content; the combined execution plan schedules them as lanes and waves (see its "Where each original phase went" table). Lean phases, one short smoke check each (see `lean-plans-testing`). Phase 1 can ship alone and changes nothing for players. Phases 2 and 3 are built on the feature branch `quests-v2` (rebased onto `FuckingAround` at every wave end) and **ship together, with the user DB wipe**. Phase 4 is clean-up right after that ship.

| Phase | Work | Smoke check |
|---|---|---|
| **1. Definition model + importer** | New tables, registry entries, `Envir` lists; **written store migration for the new counters** (the additive migration cannot add counters); validation. Importer from legacy `QuestInfo` + quest bodies: each quest → 1 step; kill tasks → `Kill` objectives on non-overlapping `MonsterGroup`s (see `questgroups`); item tasks → `Collect`; flag tasks → `Flag`; carry items → rewards with `Scope = Accept`; rewards → `questrewards` (`(M)`/`(F)` pairs → one reward each with a Gender condition: deliberate fix); requirements → a condition tree; `[QUESTS]` → `Npc` endpoints: legacy `NpcIndex` / `FinishNpcIndex` are runtime `ObjectID`s that exist only in Main after scripts load (`NPCScript.cs` ~L711-714), so the importer instead **reads each `NPCInfo` row's script text** (design-text store, resolving `#INSERT` / `#INCLUDE` like the script loader) and parses its `[QUESTS]` section itself (positive = start, negative = finish). One script file shared by several `NPCInfo` rows gives one endpoint per row, so a quest may now have several givers. The unused `NPCInfo.CollectQuestIndexes` / `FinishQuestIndexes` (only the web admin reads them, `GameDataRegistry.cs` ~L224/~L308) are merged in as endpoints when they name a quest, and dropped in phase 4; text → `texts` and keys as in the mapping table below; `QuestType` → `Category` + `RepeatPolicy`. The importer works **on `Envir.Edit` followed by `SaveEdit`** (never on Main); when Edit is not loaded at start (`EditLooksLoaded` false) it loads it first and logs that it did. It is **idempotent** (`LegacySource` keeps ids stable) and runs automatically at world start when the V2 tables are empty (so scratch servers get it: ScratchWorld writes `Server.MirDB` through `BinaryWorldStore`, `tools/scratch-world/Program.cs` ~L128, which the first start imports into `SystemData.db`), plus a web admin "Re-import legacy quests" action. Its **dry-run report** (written into this doc on the live copy): quests, legacy lines that fail to parse today (silently dropped), `[QUESTS]` sections cut short by an unknown id, quests with several would-be givers (last script wins today), quests no NPC gives, kill tasks with an empty or overlapping group, duplicate tasks on one monster, quests with `(M)`/`(F)` reward pairs, flags used by `[@FLAGTASKS]` (and which of them a script ever clears; see Rules: Flag objectives), NPCs with no `NPCInfo` row, validation errors (reported only; see Validation). Legacy `QuestInfo` stays the runtime. Additive SQLite tables: **no `Envir.Version` bump** (P7 rule). New tables read-only in the generic grid, all new tables/columns classified RestartOnly in `LiveDbClassification`. | Import on a copy of the live `SystemData.db`: 0 validation errors or a listed set of real data errors from the report; a second import gives identical rows and ids; round trip SQLite → export JSON → import gives identical rows; `Server.Persistence.Tests` |
| **2. Server engine + save format (branch)** | `PlayerQuests`, event pipeline, index, conditions, rewards, availability, accept/turn-in, revision migration, written against the new model and replacing the legacy runtime paths. `CharacterInfo` writes the new state block at `Envir.Version` 121 (wave 2 kick-off, with Part A's stats); `< 121` loads discard the legacy quest fields. Handlers for the V2 packets (the packets themselves are defined at the wave 2 kick-off). GM `SETQUEST` and the web admin character quest routes on the new state. Party quest drops cloned per member. **Every consumer of `CurrentQuests` / `CompletedQuests` moved to the new state** (inventory, grep before starting): `CHECKQUEST` (`NPCSegment.cs` ~L2522-2537), recipe quest gates (`RecipeInfo.cs` ~L214-218), `Server.MirForms/Account/PlayerInfoForm.cs`, `WebAdmin/CharacterDtos.cs` + `CharacterRoutes.cs`, `Client/RenderTest/UiTestRunner.*` fixtures (lane C) (`Server.Persistence.Tests` is updated at the wave 2 kick-off), StressClient (takes quest offers from `S.NewQuestInfo`, `VirtualPlayer.cs` ~L657; ported to V2 in lane H once the engine's packets work). Legacy `QuestProgressInfo` stays in the tree, unused at runtime, only as the oracle for the differential test; the `PlayerObject` members it uses (`SetTimer`, `ActionList`) stay until phase 4 deletes the test with it. | **Differential test** (deterministic, no server, no randomness): for each imported quest, one scripted event sequence (kills incl. overlapping names, bag gains, flag sets, turn-in) fed to legacy `QuestProgressInfo` and to `PlayerQuests`; same completion and same rewards, except the gender-reward fix. `Server.Persistence.Tests`: round trip at the new version; v120 fixtures load with empty quest state. Scratch server + StressClient quest bots (lane H) as a crash and tick-lateness check only (`[Diag] LogLoopProfile`) |
| **3. Clients + editor (branch), then ship** | (The wire contract, TS port and `WebRelay/Fixtures.cs` are done at the wave 2 kick-off.) Desktop modern + web modern UI (bars, step list, client-built text, failed state, timers), minimal V2 adapter for both classic UIs, `ClientTextKeys`. Web admin Quests editor (everything the importer produces: quest form, objectives with pickers, condition builder, rewards, texts, validation) and JSON import/export. **LiveDb quest-replace hook** (see Live edits); the new tables move from RestartOnly to it. **Ship:** take `SystemData.db` + `UserData.db` backups, last legacy re-import, the D2 wipe (execution plan), deploy server, patch root, WebRelay and web client from one commit, legacy quest bodies become read-only. | `tools\uitest.ps1` screenshots of the window and tracker; one manual accept → kill → turn in on scratch with each client (desktop modern, desktop classic, web) |
| **4. Remove legacy** | Right after the ship: drop legacy quest packet classes and factory cases (enum members stay, `[Obsolete]`, names in the `Known*FactoryGaps` lists; `S.GainedQuestItem` / `S.DeleteQuestItem` stay; see Wire protocol), `NPCInfo.CollectQuestIndexes` / `FinishQuestIndexes`, `QuestInfo` body parsing and the quests design-text kind, `QuestProgressInfo`, the legacy `quests` columns and `LegacySource` (written store migration), `QuestType`, `ClearDailyQuests`, `QuestReload`, `[QUESTS]` parsing (warning only), the re-import action. The quest bag and the `< 121` discard reader stay. | All builds + tests (`tools\verify.ps1`); scratch smoke as in phase 3 |
| **5. New content features** | Objective types beyond the imported ones (ReachArea, Talk, Interact, UseItem, Craft, Gather, ReachLevel, UseSkill, PvpWin), steps/branches/outcomes in the editor, weekly/cooldown policies, reputation reward kind (Open decision 4), map markers and storyline view in both modern clients, NPC script keywords. | One authored test chain on scratch covering each objective type, a branch and a weekly reset (server clock moved); markers visible on big map/minimap |
| **6. Shared quests (separate project)** | Party/guild/world quests and contribution on the fixed model; event UI. | Its own plan |

Each phase ends with `tools\verify.ps1`. Deploys are user-run (`tools\deploy-with-backup.ps1`). **Rollback:** phase 1 → restore the pre-migration `VACUUM INTO` copy of `SystemData.db` (an older build cannot read the new columns; design edits made since are lost). The ship (phases 2-3) → the D2 rollback in the execution plan.

#### Importer field mapping

| Legacy (`QuestInfo.cs` ~L46-65, quest body sections ~L165-177) | V2 |
|---|---|
| `Name` | `texts` row → `quests.NameKey` |
| `Group` | `quests.GroupName` (storylines are authored by hand later) |
| `Description` | `texts` → `quests.SummaryKey` |
| `TaskDescription` | `texts` → the single step's `TextKey` |
| `ReturnDescription` | `texts` → the step's `TitleKey` once Ready (a `ReadyTextKey` text on the step; shown when Ready) |
| `CompletionDescription` | `texts` → turn-in text (`quests.CompleteKey`) |
| `[@KILLTASKS]` / `[@ITEMTASKS]` / `[@FLAGTASKS]` line + its per-task `Message` (`ParseKill` / `ParseItem` / `ParseFlag`) | objective + `texts` → objective `TextKey` when a message is set (else the client builds the text) |
| `GotoMessage` / `KillMessage` / `ItemMessage` / `FlagMessage` (the dialog link words) | `texts`, keys referenced by the step (kept as the classic adapter's link labels) |
| `[@CARRYITEMS]` | rewards, `Scope = Accept` |
| `[@FIXEDREWARDS]`, `[@SELECTREWARDS]`, `[@EXPREWARD]`, `[@GOLDREWARD]`, `[@CREDITREWARD]` | `questrewards` (`ChoiceGroup` 1 for select rewards; `(M)`/`(F)` → Gender condition) |
| Level range, `RequiredQuest`, `RequiredClass` | condition tree → `AcceptCondition` |
| `QuestType` | `Category` + `RepeatPolicy` |
| `TimeLimitInSeconds` | `quests.TimeLimitSeconds` |

Text keys are generated as `q<Index>.<field>` (`q12.name`, `q12.obj3`).

#### What the importer cannot do automatically
- Quests whose real logic lives in NPC scripts (flags set by scripts, `CHECKQUEST` gates, `[@_DIE]` hooks) import as `Flag` objectives (raised by the script's `SET`) and keep working, but become proper objectives only by hand. The dry-run report lists them.
- Quest drops: today quest items drop through the normal drop files and `CheckGroupQuestItem`. The importer keeps that path (an item drop that matches a `Collect` objective goes to the player who needs it), so `DropChance` stays 0 for imported quests until an author moves them.
- Lines dropped silently today (unknown names), and `[QUESTS]` sections cut short by an unknown id, are reported, not guessed.
- NPCs that exist only at runtime with no `NPCInfo` row (if the dry-run report finds any) cannot become endpoints; they are reported.

### Threading, performance and rules this must respect
- Game thread owns `PlayerQuests` and all quest state. Raise points under a mob pass defer with `Envir.RunOutsideMobPass`. No locks, no LINQ, no per-event allocation in `Raise` (server.md §3.2, §3.1).
- Web admin edits: `Envir.Edit` under `EditLock`; the LiveDb quest-replace hook applies them to Main and migrates online characters on the game thread (`Envir.QueueAdminAction`), never sharing Edit objects with Main (server.md §2.7, §3.7).
- Packet rule: four places + manifest + TS port in one commit; private packets; ≤ 65,535 bytes.
- New enums: explicit values, append-only.
- Version bump (121, shared with Part A) with `LoadVersion` gates; `CustomVersion` stays 0. No conversion of old quest state (wipe).
- Client: UI thread only; text through `ClientTextKeys` in `Client/Localization/*.json`; server-side messages through `ServerTextKeys`.

### Open decisions
No open decision blocks phases 1-4; decisions 4, 5 and 6 are needed before phase 5.

1. ~~**Is the user DB wiped at launch?**~~ **Decided 2026-10-24: yes** (server not public). No conversion of saved quest progress, no in-memory layer, no legacy protocol; folded into the text above.
2. ~~**Quest text language**~~ **Decided 2026-10-24: server language only, with a `Lang` column** in `texts` (every row `Lang = ""`), so per-player language can be added later without a table migration.
3. ~~**Reset times**~~ **Decided 2026-10-24: daily 00:00, weekly Monday 00:00** (`DailyResetHour = 0`, `WeeklyResetDay = Monday`, UTC like `Envir.Now`). The Daily NPC call stays on the same 00:00 boundary, now through `ResetClock`.
4. **Reputation/factions** (before phase 5): a separate small model (`factions` table + per-character values) needed for the Reputation reward and condition leaf. In scope of phase 5 or its own plan?
5. **Account-wide quests/history** (before phase 5) (alts sharing storyline progress): keep the flag reserved, or design it now?
6. **Phasing:** out of scope here; confirm before phase 5.
7. ~~**Live edits: reboot-only or live?**~~ **Decided 2026-10-24: live**, through a LiveDb quest-replace hook (see Live edits).

### Never/Always rules (for whoever implements this, both parts)
From AGENTS.md: packet rule, persisted-enum rule, Build & Output rules (no solution build; scratch `-o`; never `Build\`), compile every consumer after a `Shared/` change, `Shared.Tests` / `Server.Persistence.Tests` / `Server.WebAdmin.Tests` as listed above, user-data fields bump `Envir.Version`, system-data fields are additive SQLite columns. server.md §3 and client.md §3 apply to their phases; check their Known debt sections before touching `PlayerObject` quest code (LINQ per event and `FinishNPC`'s `Envir.NPCs.Single` are Known debt replaced here, not drive-by fixes).

### Dry-run report (live copy, 2026-10-24)

Lane B ran the importer on a `VACUUM INTO` copy of the live `SystemData.db` (read-only connection on `Build\Server\Release\SystemData.db`, copied to `%TEMP%\mir2-laneB-live` with the `Envir` folder minus `Previews`) through `QuestImporterTests.Live_copy_dry_run` (`MIR2_QUEST_LIVE_COPY`). On that copy the writer's open step added the V2 tables, columns and counters after its `Pre-migrate` copy; two imports gave identical rows and ids; a save through the real store, a reload and the JSON export / import of the quest tables gave identical rows.

**Result:** 154 quests → 154 steps, 148 objectives, 76 monster groups, 394 rewards, 360 Npc endpoints, 562 condition nodes, 617 texts. **1 validation error, 3 warnings**, all real data (below). Nothing blocks: phase 1 validation only reports.

| Check | Count | Entries |
|---|---|---|
| Quests with no body file | 0 | |
| Legacy lines that fail to parse today (dropped silently) | 5 | 45 `[@FIXEDREWARDS] BoxofHolyWater(3)` and 139 `BoxofHolyWater(1)` (no such item); 132 `[@KILLTASKS] ElecMinotaur 20` and 139 `DarkDevouer 3` (no such monster); 150 `[@ITEMTASKS] EvilHeart 1` (no such item) |
| `[QUESTS]` sections cut short by an unknown id | 1 | `BichonProvince/BichonWall/Clothing` stops at 156 (its last line, so nothing after it is lost) |
| Extra `[QUESTS]` sections the loader ignores / `#INSERT`/`#INCLUDE` problems / NPCInfo rows with a missing script | 0 / 0 / 0 | |
| Quests with several would-be givers (last script wins today) | 6 | 42 Way to Mongchon (NPCs 33, 135); 82 White Serpent, 108 Antique Book, 109 Find More Antique Books, 124 Skull Collector, 136 Shipping Relics (each NPCs 283-293, one shared script) |
| Quests no NPC gives (no start endpoint) | 28 | 22, 23, 24, 25, 26, 27, 36, 37, 38, 43, 46, 47, 50, 51, 65, 75, 91, 92, 93, 96, 104, 113, 122, 123, 125, 143, 145, 146 (spot checks: 43 and 143 appear only as finish ids, e.g. Perry `-143`; 22-24 appear only in `GM/GM-Mount.txt`, outside a `[QUESTS]` section) |
| Kill tasks with an empty group / overlapping groups / duplicate tasks on one monster or item | 0 / 0 / 0 | |
| Quests with `(M)`/`(F)` reward pairs (now one reward each with a Gender condition) | 3 quests, 11 pairs | 33 Cure the Insomnia (SolidArmour); 92 Deep into the Woods (ThickArmour, FireMagicRobe, TaoArmour, SpiritRobe, LeatherArmour); 128 Returning a Book (FineIronArmour, FireRobe, YingYangRobe, FlamingRedArmour, HardTemperedArmour) |
| Flags used by `[@FLAGTASKS]` | 13 | 521-523 (quest 111), 524 (105), 525 (135), 527-528 (143), 529 (145), 530 (146), 531 (147), 533-535 (153) |
| …of which a script clears (`SET [n] 0`) | 0 | |
| Runtime NPCs with no `NPCInfo` row that give quests (conquest flags / control points) | 0 | |
| Validation errors | 1 | quest 74 Free the Souls from Zombies: `RequiredQuest` 255 does not exist |
| Validation warnings | 3 | quest 74: `RequiredClass` 0 allows no class, imported as an empty Or (nobody can accept it, as today); quests 25 and 52 both collect item 866; quests 46 and 141 both collect item 1169 (shared quest-bag count when active together) |

**Real data errors to fix in the legacy data (not in the importer):** quest 74 (`RequiredQuest` 255, `RequiredClass` 0), the five unknown names above, and a decision on the 28 quests without a giver.

#### Phase 1 as built (lane B, 2026-10-24): deviations from the text above
- The spec's `Key` columns are **`AuthorKey`** (quests, questgroups, questpools, questvars, storylines) and **`TextKey`** (texts): every store table already has a `key` column, and SQLite column names ignore case.
- The quest V2 columns live on **`QuestInfo.Definition`** (`QuestDefinition`, namespace `Server.MirEnvir.Quests`, which also holds the child lists), not as fields of `QuestInfo`, so the binary persistence oracle and its fixtures stay unchanged. The `quests` registry columns read it and the store restores it through `GameDataColumn.StoreSet`.
- A ninth counter, **`QuestPoolIndex`** (pools need stable ids too), added by the same written migration. Every migrated counter starts at the highest key of its table.
- **`LegacySource` on steps too** (the text lists it on objectives, rewards and endpoints). Objective sources are `<section> <target name>` (`#n` for repeats); groups keep their ids through `AuthorKey` `q<n>.kill.<monster>`, owned conditions through their build order, texts through their key.
- The classic link labels (`GotoMessage` … `FlagMessage`) are texts `q<n>.goto` / `.kill` / `.item` / `.flag`, found by key: steps have no column for them.
- NPC scripts are read from disk (`Envir\NPCs` is not a design-text kind), with `#INSERT` / `#INCLUDE` resolved as `NPCScript` does.
- The packet-size check is an estimate until `S.QuestDefinition` exists (wave 2 kick-off).
- The re-import is `POST /api/admin/quests/reimport` (`dryRun` supported) and the last validation list `GET /api/admin/quests/validation`; there is no panel button or grid tab yet (the Game Data page lists its tables by hand; the Quests page arrives in phase 3).
- The Server.MirDB exporter / verifier (`tools/MirDbExport`) skips the V2 tables and columns (`GameDataRegistry.InBinaryFormat`).

#### Wave 2 kick-off as built (2026-10-24, branch `quests-v2`)
- **Packet ids:** `ServerPacketIds` 303 `QuestDefinition`, 304 `QuestStates`, 305 `QuestProgress`, 306 `QuestHistory`, 307 `QuestAvailability`, 308 `QuestOffer`; `ClientPacketIds` 173 `QuestAccept`, 174 `QuestTurnIn`, 175 `QuestAbandon`, 176 `QuestShare`, 177 `QuestChoose`, 178 `QuestDefinitionRequest`. All server packets private; none hero-forwarded. Layouts: `Shared/ServerPackets.cs` / `ClientPackets.cs` (end of file), nested record classes (`S.QuestDefinition.Step` / `Objective` / `Reward` / `Endpoint` / `Branch`, `S.QuestStates.Entry`, ...), lists read with `ReadCount`. New enum `QuestRunStatus` (`None 0, Active 1, Ready 2, Failed 3, Completed 4`), emitted to TS.
- **Field names:** the definition's id field is `Quest` (not `Index`, which every packet class already has as its packet id); enum-typed fields use the V2 enums (`QuestCategory`, `QuestRepeatPolicy`, `QuestFailPolicy`, `QuestFlags`, ...) and go on the wire as the bytes / int of the brief. The `S.QuestAvailability` list is `Npcs`, `S.QuestStates` `States`, `S.QuestProgress` `Progress`, `S.QuestHistory` `History`.
- **Changed packets:** `S.ObjectNPC.QuestIDs` and `S.GameDataChanged.Quests` removed (`GameDataChanged.FixedOverhead` 9 → 8 counts). The LiveDb push no longer builds per-connection quest snapshots (`LiveDbPushConnection.SentQuests` / `QuestSnapshots` and `LiveDbHooks.QuestsFor` removed); `LiveDbPushChanges.Quests` is still filled and still counts against `MaxPushRows`, and quest rows still reload through `QuestReload` (legacy runtime) until the wave 3 quest-replace hook.
- **Stubs:** server `PlayerObject` became `partial`; the six handlers are in `Server/MirObjects/Player/PlayerQuestsV2.cs` (`MirConnection` gates on `GameStage.Game`). Desktop handlers: `Client/MirScenes/GameScene.QuestsV2.cs` (empty). `[Quests] DailyResetHour` / `WeeklyResetDay` are `Settings.QuestDailyResetHour` / `QuestWeeklyResetDay`; `[Website] ExportMinutes` / `ExportPath` are `Settings.WebsiteExportMinutes` / `WebsiteExportPath`, all in the web admin settings catalog (groups Quests, Website). **Deviation:** `ExportMinutes = 0` also means off (0-1440), because the settings page cannot store an empty string, so an operator could not switch the export off again once `ExportPath` was set. `@hiderank` / `@showrank` are empty cases next to `SETQUEST`. log4net logger `Quests` (`Logs\Quests\`) plus `LogType.Quests`; not yet in the web admin log viewer's folder list (`WebAdminLogFiles.Folders`, lane F).
- **121 save block:** the new block is written at the **end** of the `CharacterInfo` record (after the v120 chat ban); the legacy lists are simply not written at 121, and a pre-121 load skips them at their old positions (`CharacterInfo.SkipLegacyCurrentQuests` / `SkipLegacyCompletedQuests`). Records in `Server/MirDatabase/QuestRunState.cs`; `PlayerStatistics` also has `Set`, `Count` and a read-only `Values` view (export / web admin / tests). `HeroInfo` overrides `Load`/`Save` without calling the base, so heroes neither write nor read the block. `UserData.db` needed nothing: the store keeps whole account blobs, a load of an older head is re-saved in full at 121 (`_userDataRowsVersion`), and the v117-v120 fixture tests import their `Server.MirADB` into `UserData.db` and reload from it.
- **Version check (code change):** the apphost `Client.exe` already hashes differently per commit (`ProductVersion` carries the commit sha), but WebRelay rewrote **every** `C.ClientVersion`, including desktop clients on `[Network] Relay`, so old desktop clients would have passed. The relay now rewrites only the browser's all-zero hash. D2 steps: Runbook §14 (draft).
- **Not done here (by design):** no V2 runtime, no UI, StressClient untouched (it compiled unchanged: it never read `QuestIDs`).

#### Lane F as built (quest engine, 2026-10-25, branch `qv2-engine`)
- **Runtime:** `Server/MirObjects/Player/PlayerQuests*.cs` (owned by `PlayerObject.Quests`) over `Envir.QuestCatalog` (`Server/MirEnvir/Quests/QuestCatalog.cs`, built lazily from Main; `InvalidateQuestCatalog()` for wave 3). `ResetClock` and `QuestEvent` live in the same file. The wave 3 hook calls `PlayerQuests.ApplyDefinitionChange(quests)` for online players; offline records migrate with the static `PlayerQuests.MigrateRecords(def, run, objectives, now)` (also used by the web admin quest edit route on a copy of `Envir.Edit`'s definition).
- **Deviations:** the last step stays the current step and Ready is derived from it (`Step = -1` only by migration, GM or `QUESTSTEP`); Deliver and ReachArea are re-checked on `Process` while indexed (no inventory/movement hooks); step `FailCondition` is evaluated at most once a second while a current step has one; a quest with no Finish endpoint is handed in at its Start NPCs; a choice group with no valid pick gives nothing; reward kinds without a model (Reputation, Title, Spell, Unlock, Buff, Currency, ScriptPage) are skipped and ByLevel scaling is Flat; `QuestStatus` / `QuestCompletedCount` / `QuestOutcome` / `ItemCount` / `QuestVar` leaves compare Arg2, the other leaves Arg1; the importer's `RequiredQuest` leaf is `QuestCompletedCount quest ≥ 1` (it was `≥ 0`, always true).
- **Script keywords:** `CHECKQUESTSTEP quest step`, `CHECKQUESTVAR quest var op value`, `CHECKQUESTOUTCOME quest outcome`, `CHECKCONDITION id`, `QUESTCOUNTER quest objective [n]`, `SETQUESTVAR quest var value`, `QUESTSTEP quest step`, `OFFERQUEST quest` (var = id or `AuthorKey`); `ScriptValidation` checks placement and argument counts. GM `@SETQUEST q 0|1|2` = reset | complete | start.
- **Scratch servers:** the world-start import writes `Envir.Edit` only, so `tools\start-scratch-server.ps1` starts a fresh store twice (Runbook §6). The scratch world has a second quest, "Scratch Signal" (a Flag objective set by the merchant's `@SIGNAL` page).

#### Lane E as built (web admin Quests editor, 2026-10-25, branch `qv2-editor`)
- **Code:** `Server/WebAdmin/Quests/` (`QuestEditorRoutes`, `QuestEditorService` + `QuestEditorGate`, `QuestEditorDtos`, `QuestWhereUsed`, `QuestEditNotifier`), page `wwwroot/admin/admin-quests.js` / `.css`. Routes under `/api/admin/quests`: list, `meta`, `{index}`, `save` (`dryRun` = Check), `{index}/delete`, `where-used`, `{index}/export`, `import` (preview → token → apply), `groups/save`, `groups/{index}/delete`.
- **Blocking validation** is a pre-check in the service (apply on Envir.Edit, `QuestDefinitions.Validate`, roll back on any error of the saved quest), so `PrepareForSave` stays report-only for every path. Validation now runs over `QuestWorld` (Envir view or detached world; `Validate(Envir)` kept).
- **Ids:** new rows carry temporary ids ≤ −1000 in the payload; the save maps them to counter ids. A positive id must already belong to the quest. A rejected save restores the counters; a failed disk write keeps them (retired).
- **Texts:** a save upserts the `Lang = ""` rows of the keys the graph references and drops keys only this quest used; deleting a quest removes its owned conditions and its unshared texts.
- **Notifier contract:** `QuestEditNotifier.QuestsChanged(questIndexes, sharedTablesChanged)` after every successful write (EditLock released, HTTP thread). Indexes: the saved/created quest, a deleted quest (gone from Edit), and quests referencing a changed shared row (a group, a text another quest uses); ascending, distinct. `sharedTablesChanged` = a group or a shared text changed. Body today: `WebAdminPending` + log.
- **Delete refusal:** `GameDataEngine.Delete` (grid and LiveDb) joins the table's guard with `QuestWhereUsed.DeleteGuard` for monsters, items, NPCs, maps, quests, questgroups, conditions.
- **Known gap until D2:** the legacy re-import (`QuestLegacyImporter`) rebuilds every quest, editor-created ones too, from its legacy fields and body and drops every quest-owned condition, so a re-import overwrites every editor edit (the page flags quests with a legacy body). Until D2, author in the editor only what a re-import may discard, or make the importer skip quests without a legacy body. Editor-created quests have no `FileName`, so each start logs a missing quest file for them. Not changed here (importer scope).
