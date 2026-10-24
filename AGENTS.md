# Crystal Mir2 — AI Agent Knowledgebase Index

> **Purpose:** Route coding agents to the correct domain reference. Read **this file first**, then open the **one** child doc for the area you are editing (both, if the change crosses `Shared/` — see the Cross-Boundary table).
> **Rule:** Do not apply Server threading rules to Client code, or Client frame-budget rules to the Server or the patching tools. Each subsystem has a different thread model (see the Concurrency Cheat Sheet).
> **Repo:** `C:\Mir2` — Crystal, the C# Legend of Mir 2 server + client. Branch `FuckingAround` is the working branch; `master` mirrors upstream Suprcode/Crystal. All paths below are repo-relative (there is no `src\`).

---

## Agent Directive (mandatory)

1. **Operating rules.** This file and the child docs are your operating framework for this repo; treat them as overrides of default behaviour. Read this index at the start of every task.
2. **Flag conflicts.** If a user request conflicts with a NEVER/ALWAYS rule, say so and name the rule **before** proceeding.
3. **Extract rules before editing.** Before writing or editing code, do a dedicated step that lists the NEVER/ALWAYS rules for the code you will touch from: this index's Packet, persisted-enum and Build & Output rules; `Knowledgebase/Server/server.md` §3 + §5.2 (logging) + §5.4 (build); `Knowledgebase/Client/client.md` §3 + §5.2 (logging) + §2.8 (build); and the obligation notes in any §4 recipe you follow (e.g. server §4.3 version bump).
4. **Report.** After an edit, include a **"Never/Always Rules Followed"** section in your response: each extracted rule as followed, not applicable, or overridden (quote the user's approval), plus any Known debt / Known defect the change touched and left as is.
5. **Rules are the bar for new and changed code — not a description of the current code.** Crystal is an old, unoptimised upstream codebase and breaches many rules today. Each child doc lists those breaches under **Known debt**: do **not** copy those patterns, and do **not** fix them in drive-by edits — fixing debt is a separate, explicitly requested task. **Known defects** (real bugs found 2026-10-08, not fixed) are traps to avoid, not a to-do list.
6. **Codebase size.** Do not blindly read or grep the whole tree. Search narrowly (`Grep` with a path/glob) and read targeted line ranges. Giant files:

| File | Lines |
|---|---|
| `Server/MirObjects/PlayerObject.cs` | ~14.7k |
| `Client/MirScenes/GameScene.cs` (also declares `MapControl`) | ~12.5k |
| `Server/MirObjects/HumanObject.cs` | ~8.7k |
| `Shared/ServerPackets.cs` | ~6.8k |
| `Server/MirEnvir/Envir.cs` | ~5.6k |
| `Shared/Language.cs` | ~4.3k |
| `Server/MirObjects/MonsterObject.cs` | ~3.9k (+ 212 AI files in `Monsters/`) |
| `Shared/ClientPackets.cs`, `Server/MirNetwork/MirConnection.cs`, `Shared/Enums.cs` | ~2.7k / ~2.4k / ~2.0k |

7. **Never touch `Build\`.** It is the user's live run folder (see Build & Output Rules).

---

## Repository at a Glance

All projects are in `Legend of Mir.sln` (configs `Debug|Any CPU` / `Release|Any CPU` only). All SDK projects except `Server.WebAdmin.Tests` set `AppendTargetFrameworkToOutputPath=false`, so output lands directly in `<Base>\Debug\` (the tests build to `Server.WebAdmin.Tests\bin\Debug\net10.0\`).

| sln name | Project file | Assembly | TFM | Platform | Output | Role |
|---|---|---|---|---|---|---|
| **Shared** | `Shared/Shared.csproj` | `Shared.dll` | net8.0 | AnyCPU | `Shared/bin` | Packets, packet-ID enums, all shared enums, `Shared/Data` models, `Language.cs` — **one assembly used by both sides** |
| **Server.Library** | `Server/Server.Library.csproj` (folder `Server\`) | `Server.Library.dll` | net10.0 (`$(ServerTfm)`) | AnyCPU | `Server/bin` (safe) | **All** game logic: `MirEnvir`, `MirObjects`, `MirDatabase`, `MirNetwork`, `Settings`, the MirCraft web admin backend `Server/WebAdmin` |
| **Server** | `Server.MirForms/Server.csproj` (folder `Server.MirForms\`) | `Server.exe` | net10.0-windows7.0 (`$(ServerWinTfm)`) WinForms | AnyCPU | **`Build\Server\Release\` = LIVE run folder** (the user builds Release in VS and runs from there, 2026-10-11); `Build\Server\Debug\` = older data copy, also never touch | WinForms host `SMain`, DB editor forms, `wwwroot/` web admin frontend, `log4net.config`, `Localization/*.json` |
| **Client** | `Client/Client.csproj` | `Client.exe` / `Client.dll` | net8.0-windows7.0 WinForms | **x64** | `Build\Client\Release\` (live); `Build\Client\Debug\` (read-only data reference for render tests) | Game client: render seam with Direct3D 11 via Vortice (default) or SlimDX Direct3D 9 (`[Graphics] Renderer=D3D9`, until C5), NAudio, in-game patcher `Client/Patching/` (no launcher window since 2026-10-20). Golden render tests: `tools\rendertest.ps1` |
| **Server.WebAdmin.Tests** | `Server.WebAdmin.Tests/…csproj` | — | net10.0 | AnyCPU | own `bin` (safe) | xUnit 2.9.2; web admin tests (fork-only) |
| **Shared.Tests** | `Shared.Tests/…csproj` | — | `$(SharedTfm)`;net10.0 | AnyCPU | own `bin` (safe) | Packet safety net: factory cases, round trips, append-only wire manifest `packet-manifest.txt` (roadmap P1) |
| **Server.Persistence.Tests** | `Server.Persistence.Tests/…csproj` | — | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | DB harness: byte round trip + semantic oracle of `Server.MirDB`/`Server.MirADB` on synthetic data, or on a copy of real data via `MIR2_PERSISTENCE_DATA` (roadmap P1) |
| **StressClient** | `StressClient/StressClient.csproj` | `StressClient.exe` | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | Bot load generator on the real Shared codec (roadmap P6): worker-pump orchestrator, per-bot world model, A* movement on real map collision, combat/survival, loot and gear, merchant town runs, quests, chat/party, Director. Scratch servers only for agents (the web admin Tools page can also target the panel's own server, operator only): `Knowledgebase/StressClient/stressclient.md` |
| **StressClient.Tests** | `StressClient.Tests/…csproj` | — | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | Map-collision parser fixtures (formats 0-7/100) and A* tests for the bot harness |
| **WebRelay** | `WebRelay/WebRelay.csproj` | `WebRelay.exe` | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | Web client sidecar (roadmap W): WebSocket <-> TCP relay of raw Crystal frames + read-only `Data\`/`Map\` serving for `WebClient/` (TypeScript/WebGL2, `tsc`, not in the sln): `Knowledgebase/WebClient/webclient.md`. Also the desktop patch host (`--patch-root`, in-game patcher 2026-10-20): `/api/patch` + `/patch/<path>?v=` |
| **ScratchWorld** | `tools/scratch-world/ScratchWorld.csproj` | `ScratchWorld.exe` | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | Generates a synthetic map + `Server.MirDB` for scratch servers, or copies real server maps (`--maps-from`) with a walkable start point (`tools/start-scratch-server.ps1` / `stop-scratch-server.ps1`) |
| **MirDbExport** (+ `.Tests`) | `tools/MirDbExport/MirDbExport.csproj` | `MirDbExport.exe` | `$(ServerTfm)` | AnyCPU | own `bin` (safe) | Read-only SQLite export + independent verifier of `Server.MirDB` (roadmap P7 step 1): `Knowledgebase/Future Ideas/P7-SQLite-Export.md` |
| **LibraryEditor** | `LibraryEditor/…csproj` | `LibraryEditor.exe` | net8.0-windows7.0 | **x64** | `Build\Server Tools\LibraryEditor\` | `.Lib` sprite library editor (writes v3 frames; Squish + zlib) |
| **LibraryViewer** | `LibraryViewer/…csproj` | `CMirLibraryViewer.exe` | net8.0-windows7.0 | AnyCPU | `Build\Server Tools\LibraryViewer\` | Read-only `.Lib` browser |
| **CustomFormControl** | `Controls/FixedListViewControl/CustomFormControl.csproj` | `CustomFormControl.dll` | net8.0-windows7.0 | AnyCPU | `Components\Debug\` (untracked) | WinForms list control; source is built into LibraryEditor/LibraryViewer via ProjectReference; **Server.exe references the prebuilt `Components\CustomFormControl.dll`, not this build** |
| **PatcherWebSite** | `PatcherWebSite\` (.NET Framework 4.8 Web Site) | — | — | — | — | Static page `mir2-patchsite/` (was the old launcher's page; the client no longer shows it); not built by the dotnet CLI (skipped with warning MSB4078) |

Solution folders: `Utils` (LibraryEditor, LibraryViewer, PatcherWebSite, `Controls`), `Controls` (CustomFormControl), `Notes` (root `.txt` files).

**Shared protocol/data (cross-cutting):**
- **One `Shared` assembly** is compiled into both Server and Client. Unlike WoOOLcraft's Library/LibraryCore mirror pair, there is nothing to keep in sync by hand — the wire format can only drift **across builds** (a stale client against a new server). So: **deploy client and server built from the same commit after any `Shared/` change.**
- `Packet.IsServer` selects the decode factory: `true` in `Server.MirForms/Program.cs`, `false` in `Client/Program.cs`.
- **Shared models serialise to the wire AND to the server DB** (`UserItem`, `ItemInfo`, `Stats`, …): their `ctor(reader, version = int.MaxValue, customVersion = int.MaxValue)` reads the packet layout by default and the DB layout when the server passes the file's version (the enclosing ctor's `version, customVersion`, else `Envir.LoadVersion, Envir.LoadCustomVersion`; server.md §3.4). Exception: `GameShopItem` has a separate wire ctor `(reader, bool packet)` / `Save(writer, packet)` — update both.
- Server data: system data in **`SystemData.db`** (SQLite, roadmap P7 since phase 7.3: one writer thread, Envir.Edit is the only design writer, `Server/MirEnvir/Storage/`; the first start imports `Server.MirDB`, which the server never writes again; since phase 7.4 also the design ini files and the drop/quest/recipe/route text files, through `DesignIni` / `DesignFiles`) and user data in **`UserData.db`** (SQLite, UserData-and-Quests plan Part A, deploy D1: accounts/characters, world counters, guilds, NPC goods, conquest state as rows of the same hand-written binary blobs; one writer thread, one transaction per save, `VACUUM INTO` backups in `Back Up\Accounts\`; the first start imports `Server.MirADB` + `Guilds\`, `Envir\Goods\`, `Conquests\` and renames them `*.imported`; Runbook §12), the blobs versioned by `Envir.Version` (120; 118 = typed `Buff.Data`, no BinaryFormatter; 119 = social toggles/play time/profile, mail sender, spell AutoCast, guild log, market sales, GameShop deal prices, 2026-10-16; 120 = `CharacterInfo.ChatBanned`/`ChatBanExpiryDate`, were memory-only, 2026-10-20) / `CustomVersion` (0) / `MinVersion` (60). No client database — the client receives game data on demand (`S.NewItemInfo`, …).
- **New saved fields:** on **system-data** models (items, monsters, maps, NPCs, quests, magic, game shop, conquests, dragon) a new public field is an additive `SystemData.db` column added automatically, **no `Envir.Version` bump** (roadmap P7 override, approved 2026-10-20; recipe server.md §4.3). On **user data** (accounts, characters, guilds, goods, conquest state) and any Shared wire model's `Save`, new fields **bump `Envir.Version`** (user's decision, 2026-10-08: this fork does not pull upstream updates). Leave `CustomVersion` at 0 and unused.
- Web admin (MirCraft, fork-only): `HttpListener` backend in `Server/WebAdmin/`, vanilla-JS frontend in `Server.MirForms/wwwroot/{admin,shared}`, default `127.0.0.1:5680`.

---

## Domain References (read the one that matches your task)

### [Server](Knowledgebase/Server/server.md) — `Knowledgebase/Server/server.md`

**When to read:** game simulation, monsters/AI, NPC scripts, spells/buffs, items/drops, `MirConnection` packet handlers, `Server.MirDB`/`Server.MirADB` persistence and versioning, `Settings`/`Setup.ini`, WinForms host + editors, the MirCraft web admin backend and frontend, `Server.WebAdmin.Tests`.

| | |
|--|--|
| **Paths** | `Server/`, `Server.MirForms/`, `Server.WebAdmin.Tests/`, server use of `Shared/` |
| **Hot path** | Game thread `Envir.WorkLoop` (idle park ≤ 1 ms when idle, roadmap P6) + monster worker threads `Envir.ThreadLoop` when `Settings.Multithreaded` (default `true`, `ThreadLimit=2`) — no async, no locks, no LINQ per tick |
| **Persistence** | `SystemData.db`, `UserData.db` (legacy `Server.MirDB` / `Server.MirADB`, `Guilds\`, `Envir\Goods\`, `Conquests\` are read only by the first-start imports), `Back Up\` (all cwd-relative) |
| **Networking** | TCP binary packets, `[Network] Port=7000`; status port `[Network] StatusPort` (default 3100; upstream master uses 3000); web admin `HttpListener` 5680; legacy `HttpServer` 5679 (opt-in) |

**Agent persona:** Principal .NET Game **Server** Architect — thread ownership (game thread + mob threads), GC, tick latency, DB-format safety.

---

### [Client](Knowledgebase/Client/client.md) — `Knowledgebase/Client/client.md`

**When to read:** rendering (`DXManager`, `MapControl`), UI (`MirControl`/`MirScene`, classic dialogs, the new UI `Client/MirScenes/Modern/` incl. the account screens `Modern/Screens/`, default since 2026-10-20: rules in client.md §3.6), input/keybinds, client packet handlers (`GameScene.ProcessPacket`), `.Lib` assets and `Libraries`, sound, `Mir2Config.ini`, the in-game patcher `Client/Patching/`, monster frames/appearance.

| | |
|--|--|
| **Paths** | `Client/`, client use of `Shared/`; `LibraryEditor/`, `LibraryViewer/` for the `.Lib` format |
| **Hot path** | Single UI thread: `CMain.Application_Idle` → `UpdateEnviroment` (every spin, uncapped) + `RenderEnvironment` (capped by `Settings.MaxFPS`) + `Network.Process` |
| **Persistence** | `Mir2Config.ini` (Release) / `Mir2Test.ini` (Debug or `-tc`), `KeyBinds.ini`, `Data\UserData\QuestTracking.ini`, `Error.txt` — no client DB |
| **Networking** | Static `Network` (APM `TcpClient`), `[Network] UseConfig/IPAddress/Port`, default `127.0.0.1:7000` |

**Agent persona:** Principal .NET Game **Client** Engineer — frame budget, whole-UI texture invalidation, device-reset safety. All device calls go through `IRenderBackend` (`Client/MirGraphics/Rendering/`, roadmap C3/C4); D3D9 and D3D11 must stay golden-identical (`Knowledgebase/Client/D3D11-Parity.md`).

---

### Web admin (MirCraft) — `Knowledgebase/Server/server.md` §2.7 + §3.7 + §4.10, and `Server/WebAdmin/README.md`

**When to read:** `Server/WebAdmin/**`, `Server.MirForms/wwwroot/**`, `Server.WebAdmin.Tests/**`. There is no separate Websites child doc for Crystal; the server doc covers backend threading/marshalling (`WebAdminGame.RunAsync` → `Envir.QueueAdminAction`) and frontend rules (vanilla JS IIFEs, no npm/build, CSRF via `portalApi`/`OpsUI.post`). `Server/WebAdmin/README.md` is the authoritative operator + developer guide.

---

### Patching tools and asset tools — no child doc yet

| Area | Code | Notes |
|---|---|---|
| Player patcher | `Client/Patching/` (in game; `Program.cs` starts it in the background when `[Launcher] Enabled`) | Against WebRelay `--patch-root` (client.md §2.1, §3.2 exception, §5.4; Runbook §9). The launcher window (`AMain`, its Config form, WebView2), the old `PList.gz` patcher and AutoPatcherAdmin(V2) were removed 2026-10-20 |
| Patcher web page | `PatcherWebSite/mir2-patchsite/` | Static HTML/CSS |
| `.Lib` tools | `LibraryEditor/` (`Graphics/MLibraryV2.cs`), `LibraryViewer/` | Reader side + format rules in `client.md` §2.5 / §3.3 |
| Weapon art | `tools/weaponpose/` (Python 3, stdlib only; README) | Poses one upright weapon picture into the 832 frames of a `CWeapon` library using a reference weapon's lines; read-only on the client folder. Browser port in the Scene Lab Weapon tester (`WebClient/play/weaponPose.ts`) |

Async/Task code is fine in these tools; none of them touch game state. Read the source narrowly and ask before large changes.

---

## Which Document Should I Open?

```
What are you editing?
│
├─ Server/, Server.MirForms/ (except wwwroot/)
│     → Knowledgebase/Server/server.md
│
├─ Server/WebAdmin/, Server.MirForms/wwwroot/, Server.WebAdmin.Tests/
│     → server.md §2.7, §3.7, §4.10 + Server/WebAdmin/README.md
│
├─ Client/
│     → Knowledgebase/Client/client.md
│
├─ Shared/   (packets, enums, Shared/Data models, Language.cs)
│     → BOTH child docs — always cross-boundary (table below)
│
├─ LibraryEditor/, LibraryViewer/
│     → client.md §2.5 / §3.3 (.Lib format) + §3.5 (`MirAction` stored in frame tables) + the tool's source
│
├─ WebClient/, WebRelay/   (browser client + its relay)
│     → Knowledgebase/WebClient/webclient.md (+ BOTH child docs when a ported packet changes)
│
├─ PatcherWebSite/, Client/Patching/
│     → no child doc for the page (no longer shown by the client); Patching: client.md §2.1, §5.4; the relay side: webclient.md §2.1
│
├─ Components/ (vendored DLLs), Legend of Mir.sln, *.csproj
│     → this index (Build & Output Rules) + the affected child doc
│
└─ Unsure / spans areas
      → this index + every doc the change touches (Cross-Boundary table)
```

---

## Cross-Boundary Work (common multi-doc tasks)

| Task | Primary doc | Also read |
|---|---|---|
| Add / change a packet | Server §3.5, §4.2, §3.8 | Client §3.5 (incl. Known defect: `S.RefineCancel`; `C.PurchaseGuildTerritory` fixed 2026-10-16, Observable storage-password replies fixed 2026-10-24), §4.5 |
| Add / change a shared enum (`Monster`, `Stat`, `ItemType`, `Spell`, `BuffType`, `MirAction`, …) | Server §3.4 | Client §3.5 (enum values map to `.Lib` files / client logic); Client §4.4 "Recipe — add a UI string" step 4 (members of `Stat`, `BuffType`, `MirClass`, `PoisonType`, … need an `EnumType_Member` entry in the client JSON `Enum` object) |
| Change a `Shared/Data` model's `Save`/ctor, or any user-data DB layout (`Envir.Version` bump + `LoadVersion` gate); a system-data field (additive SQLite column, no bump) | Server §2.6, §3.4, §4.3 | Client §4.8 (client reads the packet layout with `int.MaxValue`) |
| Add a user-facing string (`ServerTextKeys` / `ClientTextKeys`) | Server §4.1 / Client §4.4 "Recipe — add a UI string" | Server keys → `Server.MirForms/Localization/{English,Chinese}.json`; client keys → `Client/Localization/{English,Chinese}.json` (never both) |
| Add a monster (server AI + client frames/libraries) | Server §4.5 (AI class, `GetMonster` case, free AI numbers) | Client §4.6 "Recipe — add a monster appearance / frames" (`Monster` value = `Data\Monster\NNN.Lib`), `SoundRules.txt` |
| Add a spell / buff | Server §4.7 | Client §4.6 (effects: `Effect`/`SpellObject`), §4.4 "Recipe — add a UI string" (spell/buff text; `BuffType` names step 4), §4.5 if a packet is added |
| Web admin game-data edits | Server §2.7, §3.7 (`Envir.Edit` under `EditLock`, `GameDataSave`) | Server §2.6 (`SystemDataStale`), `Server/WebAdmin/README.md` |
| Patch a client after a change | Runbook §9 (copy into the WebRelay patch root, then `PatchRevision.txt`) | Client §5.3 (new `.Lib`/sounds/`Localization` in the patch root) |
| Client/server version check | Server §2.5 ("Version check") | Client §3.5 Known defects (weak MD5 check) |
| Server address for players | Client §2.6, §2.7 (`[Network] UseConfig/IPAddress/Port`) | Server §2.5 (`Settings.IPAddress`/`Port`) |

**Packet rule (Crystal):** a packet lives in **four** places, all in one commit:
1. ID appended at the **end** of `ServerPacketIds` / `ClientPacketIds` in `Shared/Enums.cs` (~L1434 / ~L1717). IDs are **implicit ordinals** (declaration position) — inserting mid-enum renumbers every later ID. Current last members: `DungeonMap` (302, 2026-10-23, procedural dungeons, server.md §4.12); 301 = `ObjectMoveBatch` (2026-10-21 P9-08, behind `[Optimisation] MoveBatch`); 300 = `AllowToggles` (2026-10-16 phase 2.8; 298-299 = `FriendRequest`, `FriendRequestResult`, 2026-10-16 phase 2.7; 296-297 = `MarketPriceHistory`, `SentMail`, 2026-10-16 phase 2.6; 288-295 = `GuildMemberDetails`, `GuildWarList`, `GuildLog`, `RankingsDetail`, `GuildRankings`, `MentorDetails`, `MentorCandidates`, `PlayerProfile`, 2026-10-16 phase 2.5; 282-287 = `PoisonStatus` … `SelectDetails`; 281 = `ServerRules`) / `SetAllowToggle` (172, 2026-10-16 phase 2.8; 171 = `FriendRequestReply`, phase 2.7; 166-170 = `GameshopGift`, `MarketSearchEx`, `MarketPriceHistory`, `RequestSentMail`, `HideSentMail`, 2026-10-16 phase 2.6; 160-165 = `RequestGuildMemberDetails`, `RequestGuildLog`, `GetGuildRanking`, `FindMentor`, `GuildTerritoryAction`, `SetProfile`, 2026-10-16 phase 2.5; 158-159 = `HeroSpellToggle`, `RequestMapDetails`; 153-157 = `SellItems` … `SortItems`); re-check before relying on an ordinal.
2. `sealed class` in `Shared/ServerPackets.cs` (`S`) / `Shared/ClientPackets.cs` (`C`) with `ReadPacket`/`WritePacket` in identical field order and types.
3. A `case` in `Shared/Packet.cs` `GetServerPacket` / `GetClientPacket` — **a missing case makes the receiver discard its whole receive buffer, silently.**
4. Handler: server `MirConnection.ProcessPacket` (`Server/MirNetwork/MirConnection.cs`) → usually `PlayerObject`/`HumanObject`; client `GameScene.ProcessPacket` (or `LoginScene`/`SelectScene`/base `MirScene.ProcessPacket`).
5. If the packet is ported to the web client (`WebClient/src/protocol/server.ts` / `client.ts`): update the TS port and its `WebRelay/Fixtures.cs` sample, then `npm run codec:regen` + `npm run codec:test` in `WebClient/` (webclient.md §3.1). Any `Shared/Enums.cs` change: re-run `WebRelay --emit-ts`.
Also: ≤ 65 535 bytes per packet; private server packets `Observable => false`; hero-forwarded packets go in the `HeroObject.Enqueue` whitelist. `ReadPacket` runs on the socket/IOCP thread on both sides: no static/game state in packet classes. A read/write mismatch IP-blocks the player for 24 h (server) or crashes the client process (socket thread). There is no ID catalog file and no attribute serialisation (unlike WoOOLcraft's `WoooLPacketIds.ini`).

**Shared / persisted-enum rule:** there is **one** copy of every shared enum, in `Shared/Enums.cs` (plus `Stat`/`StatFormula` in `Shared/Data/Stat.cs`, `ClientTextKeys`/`ServerTextKeys` in `Shared/Language.cs`). Enums stored by numeric value in the DB, on the wire or in `.Lib` files are **never renumbered, reordered or inserted into**: `MirAction` (implicitly numbered, stored by byte in every v3 `.Lib` frame table — append at the end only), `Stat : byte` (< 255, `Unknown = 255`), `Monster : ushort` (= `Data\Monster\NNN.Lib`), `ItemType`, `Spell`, `BuffType`, `ItemSet`, `ObjectType`, `MirClass`, `MirGridType`, `BindMode`, `AllowToggle : byte` (explicit values, bit index into `S.AllowToggles.Flags`, so ≤ 15), …. New members get an **explicit unused value**: at the end for sequential enums, or a free value in the right band for banded ones (`Spell` by class, `Stat` gaps < 255). Check `[Flags]` enums (e.g. `PoisonType`, `BindMode`, `SpecialItemMode`, `RequiredClass`, `RequiredGender`) before `Enum.IsDefined`; `BindMode` has no free bit. Details: server.md §3.4, client.md §3.5. Language keys (`ClientTextKeys`/`ServerTextKeys`) and `KeybindOptions` are looked up **by name** — order is free, renaming is not. Client-only enums live in `Client/`.

---

## Build & Output Rules

- **NEVER** build `Legend of Mir.sln` with the dotnet CLI, with or without `-o`. Without `-o` it writes `Build\Server\Debug\` or `Build\Server\Release\` (Release is the user's **live server folder**, Debug an older data copy: real DBs, `Configs\`, `Envir\`, `Maps\`, `Logs\`; a build overwrites `log4net.config`, `Localization\*.json` and `wwwroot\` there), `Build\Client\`, `Build\Server Tools\LibraryEditor|LibraryViewer\` and `Components\Debug\`. With `-o` the SDK warns NETSDK1194 and every project lands in one folder (Client and Server `Localization\*.json` overwrite each other, as do prebuilt and source-built `CustomFormControl.dll`). Build individual projects instead. (`PatcherWebSite` is skipped by the CLI with warning MSB4078.)
- **NEVER** build `Server.MirForms\Server.csproj`, `Client\Client.csproj`, `LibraryEditor` or `LibraryViewer` without `-o <scratch dir>`.
- **NEVER** pass a global `-p:BaseIntermediateOutputPath`/`-p:OutputPath` to redirect a build; use `-o <scratch>` only (a global property flows into the `Shared` ProjectReference and breaks restore).
- **NEVER** modify anything under `Build\` (untracked runtime data and outputs).
- **ALWAYS** compile **every** consumer after a `Shared/` change:
  ```
  dotnet build C:\Mir2\Server\Server.Library.csproj
  dotnet build C:\Mir2\Server.MirForms\Server.csproj -o <scratchpad>\server-build
  dotnet build C:\Mir2\Client\Client.csproj -o <scratchpad>\client-build
  dotnet build C:\Mir2\LibraryEditor\LibraryEditor.csproj -o <scratchpad>\libedit-build   (if you touched types it uses, e.g. MirAction, Monster)
  ```
- **ALWAYS** run `dotnet test C:\Mir2\Server.WebAdmin.Tests\Server.WebAdmin.Tests.csproj` after changing `Server/WebAdmin`, `Envir` web hooks or `wwwroot` (safe: writes nothing to `Build\`).
- **ALWAYS** run `dotnet test C:\Mir2\Shared.Tests\Shared.Tests.csproj` after any `Shared/` change. A deliberate wire change (new packet, fixed known gap) needs the manifest regenerated with `MIR2_REGEN_PACKET_MANIFEST=1` and the diff committed.
- **ALWAYS** run `dotnet test C:\Mir2\Server.Persistence.Tests\Server.Persistence.Tests.csproj` after touching any `Save`/ctor pair, `Envir` load/save code or `Envir.Version`.
- **NEVER** expect source edits in `Controls/FixedListViewControl` to reach `Server.exe` — it references the checked-in `Components\CustomFormControl.dll` (LibraryEditor/LibraryViewer do build the source, into `Components\Debug\`).
- **NEVER** switch Client or LibraryEditor off `x64` (native deps: SlimDX, `NativeSquish_x64.dll`).
- Prereqs (`dotnet-upgrade.txt`): Windows, .NET 10 SDK. **Server host: .NET 10 Desktop Runtime x64** (since roadmap P4, 2026-10-10; keep the .NET 8 runtime installed during the rollback window). **Client: .NET 8 Desktop Runtime** until roadmap C5. `Shared` stays `net8.0` while the client is on net8. There is no CI. TFMs and `LangVersion` (12) are centralised in the root `Directory.Build.props` (`ServerTfm`, `ServerWinTfm`, `SharedTfm`, `ClientTfm`, `ToolsTfm`) — change a TFM there, never in a `.csproj`. This machine builds with .NET SDK 10.0.400 (no `global.json`) — do not add a `global.json`; retargeting happens only in the roadmap phases that schedule it (P4 server, P5 tools, C5 client/Shared; approved 2026-10-09).
- **ALWAYS** prefer `powershell -ExecutionPolicy Bypass -File tools\verify.ps1 [-Only <keys>] [-NoTests]` for compile + test checks: it builds every project to `%TEMP%\mir2-verify\<key>` and refuses `Build\`. Deploys use `tools\deploy-with-backup.ps1` and are **user-run only** (`Knowledgebase/Runbook.md`).

---

## Workspace Layout (quick paths)

```
C:\Mir2\
├── Legend of Mir.sln
├── agents.md               ← you are here
├── Shared/                 → BOTH docs (cross-boundary): Enums.cs, Packet.cs, Client/ServerPackets.cs,
│                             Data/*.cs, Language.cs, Functions/, Globals.cs
├── Server/                 → server.md   (Server.Library: MirEnvir, MirObjects, MirDatabase,
│                                          MirNetwork, Settings.cs, WebAdmin/ + README.md)
├── Server.MirForms/        → server.md   (Server.exe: Program.cs, SMain.cs, editor forms,
│                                          wwwroot/{admin,shared}, Localization/, log4net.config)
├── Server.WebAdmin.Tests/  → server.md §5.5
├── Client/                 → client.md   (Forms/, MirGraphics/, MirControls/, MirScenes/,
│                                          MirNetwork/, MirObjects/, MirSounds/, Localization/)
├── WebClient/, WebRelay/  → Knowledgebase/WebClient/webclient.md (browser client + relay)
├── PatcherWebSite/        → this index (no child doc; the old launcher's web page, unused by the client)
├── LibraryEditor/, LibraryViewer/                            → client.md §2.5, §3.3 (.Lib format) + §3.5 (MirAction)
├── Controls/FixedListViewControl/   → CustomFormControl source (built into LibraryEditor/Viewer; Server uses the Components\ copy)
├── Components/             → vendored DLLs: SlimDX (Client), CustomFormControl + VB PowerPacks (Server), DungeonGen\
│                             (Crystal.DungeonGen + data, published from the CrystalStudio repo; server.md §4.12),
│                             Squish 2.0 + dotnet zlib (LibraryEditor/Viewer)
├── Build/                  → build outputs + LIVE run data (Client, Server, Server Tools) — never touch
├── Knowledgebase/          → child docs (see Knowledgebase Folder Map)
└── README.md, dotnet-upgrade.txt, MonsterAttackMethods.txt (stale signatures),
    MonsterReferenceVideos.txt, SoundRules.txt, .editorconfig, .gitattributes
```

---

## Concurrency Cheat Sheet (do not mix these up)

| Subsystem | Model | Async in hot path? |
|---|---|---|
| **Server game thread** | `Envir.WorkLoop` (one thread; idle park of at most 1 ms when idle, roadmap P6): all packet handling, players, pets, NPCs, maps, saves, admin-action drain | **No** |
| **Server mob threads** | `Envir.ThreadLoop` on `MobThreads[1..ThreadLimit-1]` when `Settings.Multithreaded` (default true); `MobThreads[0]` runs on the game thread; barrier via `Monitor.PulseAll(_locker)`; AI of `Master == null` monsters only | **No** — and no locks, no global/cross-map mutation, `Envir.Random` only |
| **Server network** | APM accept/receive (IOCP) → `ConcurrentQueue<Packet>` per `MirConnection`; drained by `MirConnection.Process()` on the game thread. Sends: the game thread encodes, then `SocketSender` threads issue the socket sends (P9-09, `[Optimisation] SendThreads`) | No (decode + enqueue only; senders: `SendAsync` + completions, never game state) |
| **Server WinForms** | `SMain` UI thread; editors work on `Envir.Edit`. Existing forms (e.g. `Account/Market.cs`, `InterfaceTimer`) touch `Envir.Main` directly — Known debt; new/changed form code reaches the live world only via `Envir.Main.QueueAdminAction` (server.md §4.11) | n/a (UI) |
| **Server web admin** | `HttpListener` + per-request `Task`s (cap 32); world access only via `WebAdminGame.RunAsync`/`RunOrInlineAsync`/`Post` → `Envir.QueueAdminAction` | **Yes** (HTTP threads only) |
| **Client UI thread** | `CMain.Application_Idle` busy loop: update, render, input, `Network.Process` packet **handling** | **No** |
| **Client socket thread** | APM `Network.ReceiveData`: decode + enqueue into `ConcurrentQueue<Packet>` | No |
| **Client loader thread** | `Libraries` → `LoadGameLibraries`: `MLibrary.Initialize()` only; publishes `Libraries.Loaded`/`Progress` | No |
| **Patcher** | `Client/Patching/PatchService` threads (start thread + coordinator + workers, during the game; client.md §3.2 exception): HTTP + file installs only | No async needed (blocking HTTP on its own threads); never game state |
| **Web client** | WebRelay: async pool threads, two pumps per session (no game state). Browser: one `requestAnimationFrame` loop + async loads | **Yes** (relay); n/a (browser) |

Server details: `server.md` §2.3, §3.2. Client details: `client.md` §2.1, §3.2.

---

## Git & Branch Notes

- Remotes: `origin` = https://github.com/ryu131313/Crystal.git (user's fork), `upstream` = https://github.com/Suprcode/Crystal.git.
- `master` mirrors upstream (tip `0e315fe3`); `FuckingAround` is the working branch (one fork commit `6891bc2e` moves the status port 3000→3100, then the MirCraft web admin commits sit on top of master). Base fork work on `FuckingAround` unless told to target upstream `master`. Workflow worktrees created with `isolation: 'worktree'` branch from `master` — pre-create them from the right commit.
- Commit only when asked. Message style: imperative sentence, no prefix (e.g. "Add web admin backups, history and audit trail"); upstream commits carry a PR suffix like `(#1188)`.
- `.claude/` is excluded via `.git/info/exclude`. `Knowledgebase/` and `AGENTS.md` are tracked (since `9150e119`; the repo is private).
- Encoding: most `.cs` files start with a UTF-8 BOM — keep it; `.gitattributes` is `* text=auto` — keep line endings as found. `*.csproj.user` files are tracked despite `.gitignore`.
- The fork does **not** pull upstream updates (user, 2026-10-08), so upstream merge-compatibility is not a constraint. Keep diffs small for reviewability, not for merges.

---

## Knowledgebase Folder Map (honest status)

| Path | Status |
|---|---|
| `Knowledgebase/Server/server.md` | **Written 2026-10-08** — Crystal server, web admin, persistence |
| `Knowledgebase/Client/client.md` | **Written 2026-10-08** — Crystal client, rendering, UI, assets |
| `Knowledgebase/Client/D3D11-Parity.md` | **2026-10-10** — render seam, D3D9/D3D11 backends, golden harness, the D3DX behaviours the D3D11 backend reproduces, waivers, rollout |
| `Knowledgebase/roadmap.md` | **Proposed 2026-10-08** — ordered modernisation plan (.NET 10, SQLite, server optimisation, DX11 client track), phase gates, rule overrides (⚠), open decisions, status table. Check it before starting any modernisation phase; per-phase mechanics and evidence are in `Future Ideas/Roadmap-Phase-Notes.md` |
| `Knowledgebase/PatchHost/` | Empty placeholder (the patch host is WebRelay `--patch-root`: webclient.md §2.1, Runbook §9; the client side is `Client/Patching/`, client.md §5.4) |
| `Knowledgebase/StressClient/` | `stressclient.md` (updated 2026-10-21): the bot harness (worker pump, world model, A*, Director), scratch servers, baseline/soak scripts |
| `Knowledgebase/WebClient/` | `webclient.md` (2026-10-16): browser client + WebRelay, packet-port recipe, the scene-lab UI as the default web UI, known gaps (roadmap W) |
| `Knowledgebase/Websites/` | Empty placeholder (web admin rules live in `server.md` §2.7 / §3.7 / §4.10 + `Server/WebAdmin/README.md`) |
| `Knowledgebase/Future Ideas/` | `Roadmap-Phase-Notes.md` (per-phase notes for `roadmap.md`), the overnight decisions log (`Overnight-Log-2026-10-10.md`, D1–D39+), `X1-Security-Review.md`, `Packet-Mutation-Audit.md`, `Review-P2-P6-Threading.md`, `P7-SQLite-Export.md`, `Ametrine-Client-Comparison.md` (2026-10-13: what the Mir Ametrine client does better than ours, adoption order — ideas only), `InGame-Patcher-Plan.md` (2026-10-20: proposed in-game patcher with on-demand whole-file downloads from WebRelay serving the raw client files, phases B0-B6, open decisions), `Offline-Account-Cache-Plan.md` (2026-10-22: proposed P9 cache of offline accounts' save bytes, offline-mutation audit, reviewed; Phase -1 = faster serialiser + fewer accounts first), `UserData-and-Quests-Plan.md` (2026-10-24: one combined plan — Part A `UserData.db` (account blobs as rows, changed-only saves in one transaction, guilds/goods/conquests as rows, `PlayerStat` dictionary incl. PvP, JSON export for worldofwooolcraft.uk) and Part B a from-scratch quest model (SQLite definitions with steps/objectives/conditions/rewards, per-objective character state, event index runtime, V2 packets, LiveDb quest replace); one Version 121 bump and one wipe; contract-first waves with parallel lanes; decisions made, reviewed 2026-10-24 with the findings folded in; waves 0-1 built 2026-10-24 (quest enums; `UserData.db`; quest definition tables + legacy importer, read-only, legacy quests stay the runtime), D1 pending. **It reserves `Envir.Version` 121 and the packet-ID tail (from 303 / 173) between its deploys D1 and D2**: no other user-data bump or new packet ID on `FuckingAround` in that window) |
| `Knowledgebase/Future Optimisation/` | Baselines (`Baseline-2026-10-10.md`) and one file per measured P9 change (`P9-NN-*.md`) |
| `Knowledgebase/Archived and Completed/` | Empty placeholder (finished plans go here) |
| `Knowledgebase/Runbook.md` | Deploy, rollback, restore, operator mitigations, scratch servers, turning on v2 hashes, `SystemData.db` (§10), `UserData.db` (§12), deploy D1 (§13) |

The WoOOLcraft knowledgebase (`C:\WoOOLcraft\Knowledgebase`) describes a different codebase: never follow its paths, classes, packet IDs or rules as Crystal guidance. Read it only as design inspiration, and only when the user asks.

---

## How Agents Should Use This Knowledgebase

1. **Identify the primary subsystem** from the request or the file path (decision tree above).
2. **Open the matching child doc** and follow its ROLE, RULES and PATTERNS sections; extract its NEVER/ALWAYS rules before editing (Agent Directive §3).
3. **If the change crosses `Shared/` or both sides**, read the Cross-Boundary table and the second doc before coding.
4. **Prefer surgical diffs** — NEVER/ALWAYS rules apply to the code you write or change; overriding one needs explicit user approval. Leave Known debt alone unless the task is to fix it.
5. **Do not duplicate** — this index routes; the child docs hold the detail. If they disagree with this index, the child doc wins for its area — and fix the drift.
6. **For architecture questions spanning the repo**, start from this index and the child docs before exploring source, and verify anything load-bearing in the code (line numbers are approximate).

---

## Child Document Structure (what each contains)

Every child doc follows the same five sections:

1. **ROLE & IDENTITY** — persona and priorities
2. **SYSTEM ARCHITECTURE OVERVIEW** — process model, threads, loop, persistence, key files
3. **CRITICAL RULES** — NEVER/ALWAYS per topic, each followed by **Known debt** (existing breaches) and, where found, **Known defects**
4. **CODE STYLE & PATTERNS** — conventions, copyable templates and "how to add X" recipes
5. **DEPLOYMENT & TELEMETRY** — logging, build/test commands, deployment checklist

Appendices: **A** Quick Decision Tree, **B** Glossary, **C** Related Docs.

---

*Last updated: 2026-10-08 (initial version, written from a code survey of branch FuckingAround). Keep this index current when projects, child docs or cross-boundary rules change.*
