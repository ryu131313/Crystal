# Crystal Mir2 Game Server — AI Agent Reference

> **Scope:** `Server/` (project `Server.Library` — `MirEnvir`, `MirObjects`, `MirDatabase`, `MirNetwork`, `Settings.cs`, `Logger.cs`, `MessageQueue.cs`, `Helpers`, `Utils`, the `WebAdmin` backend), `Server.MirForms/` (the WinForms `Server.exe` host, DB editor forms, the `wwwroot/` web-admin frontend), `Server.WebAdmin.Tests/`, and the server-side use of `Shared/`.
> **Not in scope:** `Client/` (see `Knowledgebase/Client/client.md`), AutoPatcherAdmin(V2), LibraryEditor/Viewer, PatcherWebSite.
> **Cross-boundary:** everything in `Shared/` (packets, packet-ID enums, all shared enums, `Shared/Data/*` models) is ONE assembly compiled into both Server and Client. A change there is a server change AND a client change AND often a DB-format change.

**How to read the rules in this file.** Crystal is an old, unoptimised upstream codebase (Suprcode/Crystal). The NEVER/ALWAYS rules below are the standard for **new and changed code**; they do **not** describe the current code. Where existing code breaches a rule, the breach is listed under **Known debt**: do not copy those patterns, and do not fix them in drive-by edits — fixing debt is a separate, explicitly requested task. **Known defects** are real bugs found during the 2026-10-08 survey and left unfixed; treat them as traps.

---

## 1. ROLE & IDENTITY

You are a **Principal .NET Game Server Architect** working on **Crystal** — the C# Legend of Mir 2 server (branch `FuckingAround`; `master` mirrors upstream). Your mandate:

1. **Performance first** — evaluate every hot-path change for allocations, GC pressure and tick latency.
2. **Respect the thread model** — one game thread (`Envir.WorkLoop`) owns the world, **plus** optional monster worker threads (`Settings.Multithreaded`, default `true`) that run monster AI lock-free. Neither is protected by locks; correctness depends on discipline.
3. **Match Crystal's lineage patterns** — `Envir.Main`, `MapObject.Process` + `OperateTime`, the `MirConnection.ProcessPacket` switch, hand-written `BinaryReader`/`BinaryWriter` versioned `Save(writer)` / `ctor(reader)`.
4. **Surgical diffs** — smallest correct change; no drive-by refactors, no framework migrations, no "while I'm here" debt fixes.
5. **Prove impact** — cite the hot path (`Envir.WorkLoop`, `Envir.ThreadLoop`, `MirConnection.Process/ProcessPacket`, `MapObject.Process` overrides, `Map.Process`) and the expected win.

**Default mindset:** a **WinForms exe** (`SMain`) hosting a dedicated game thread, APM TCP sockets, and an embedded `HttpListener` admin panel (MirCraft). Not ASP.NET Core, no DI container, no async in the simulation. **Crystal has no safety net:** one unhandled exception anywhere in the loop stops the whole world (§3.3).

---

## 2. SYSTEM ARCHITECTURE OVERVIEW

### 2.1 Projects

| Project | File | Type / TFM | Output |
|---|---|---|---|
| **Server.Library** | `Server/Server.Library.csproj` | Library, `net10.0` (`$(ServerTfm)`, roadmap P4), `Nullable=disable`, `ImplicitUsings=enable`; package `log4net 3.3.0`; `InternalsVisibleTo Server.WebAdmin.Tests` | `Server/bin` (safe to build) |
| **Server** (host) | `Server.MirForms/Server.csproj` | `WinExe`, `net10.0-windows7.0` (`$(ServerWinTfm)`), WinForms; refs Server.Library, Shared, `Components/CustomFormControl.dll`, `Components/Microsoft.VisualBasic.PowerPacks.Vs.dll` | **`BaseOutputPath ..\Build\Server\` → `Build\Server\Debug\` = the user's LIVE run folder** |
| Shared | `Shared/Shared.csproj` | Library, `net8.0` (`$(SharedTfm)`; stays net8 until the client moves, roadmap C5) | `Shared/bin` |
| Tests | `Server.WebAdmin.Tests/` | xUnit 2.9.2, `net10.0`; refs Server.Library + Shared only | own bin (safe) |

All game logic lives in `Server.Library`. `Server.MirForms` holds `Program.cs`, `SMain.cs`, the editor forms, `log4net.config`, `Localization/*.json` and `wwwroot/`. Namespaces: `Server`, `Server.MirEnvir`, `Server.MirObjects`, `Server.MirDatabase`, `Server.MirNetwork`, `Server.WebAdmin`. Packet aliases: `using C = ClientPackets; using S = ServerPackets;`.

### 2.2 Process Model

```
Server.MirForms/Program.cs  Main [STAThread]   (does NOT set the cwd)
  ├─ Packet.IsServer = true
  ├─ log4net XmlConfigurator.Configure(new FileInfo("log4net.config"))   ← cwd-relative
  ├─ Settings.Load()                    ← Configs\Setup.ini + per-system ini + Localization\<Language>.json
  ├─ if Settings.WebAdminEnabled: WebAdminHost.Start()      ← process lifetime, errors only logged
  ├─ new SMain(); WebAdminHost.ShutdownRequested += mainForm.BeginInvoke(mainForm.Close)
  ├─ Application.Run(SMain)
  │    └─ SMain_Load: Envir.Edit.LoadDB()  →  if ok: Envir.Main.Start()
  │                                              └─ WorkLoop thread (the game thread)
  │    └─ SMain_FormClosing: Envir.Stop()   ← blocks UI until WorkLoop exits (final saves)
  └─ WebAdminHost.Stop(); Settings.Save()   ← rewrites Configs\Setup.ini from the statics
```

- Whole `Main` is one try/catch → `Logger.GetLogger().Error(ex)`. **No** `AppDomain.UnhandledException` / `Application.ThreadException` handler, **no** `Errors.txt` (unlike WoOOLcraft).
- **Two `Envir` instances** (`Server/MirEnvir/Envir.cs` `Envir.Main`, `Envir.Edit`): **Main** = the live world (owns the WorkLoop). **Edit** = editor copy of the **system** data (`Server.MirDB`) used by the WinForms editors (`SMain.EditEnvir`) and the web admin Game Data; never started, only `LoadDB()`/`SaveDB()`. Edit must load **before** Main starts (`Envir.BindGameShop` binds Main's `GameShopItem.Info` to `Edit.ItemInfoList`). New system rows take indexes from Edit (`++EditEnvir.MapIndex` etc. in `MapInfo`/`MonsterInfo`/`NPCInfo`/`QuestInfo`).
- Static state shared by both instances: `AccountLock`, `LoadLock`, `LoadVersion`, `LoadCustomVersion`, `IPBlocks`, `_systemDataStale`.
- `Envir.Start()` (~L3371) is a no-op if `Running || _thread != null`. `Envir.Stop()` (~L3381) sets `Running=false`, wakes/interrupts mob threads, stops the legacy HTTP service, then **busy-waits for `_thread == null`** → calling it on the game thread deadlocks. `Envir.Reboot()` = `new Thread(() => { Stop(); Start(); })`, unguarded.
- Failed start: `CanStartEnvir()` (returns the string `"true"` or an error) → `StopEnvir`, no account save, `RaiseWorldStopped`. A load **exception** in `StartEnvir` falls to the outer catch (logged; accounts were never loaded, so nothing is saved).

### 2.3 Threads (complete list)

| Thread | Created at | Owns / does |
|---|---|---|
| **WinForms UI** | `Program.Main` | `SMain`, editor forms (on `Envir.Edit`), `InterfaceTimer_Tick` (~100 ms: drains `MessageQueue` into the log textboxes, reads `Envir.Main` counters/players **unsynchronised**) |
| **Game thread** (WorkLoop) | `Envir.Start()` `new Thread(WorkLoop){IsBackground=true}` | DB load, accounts/guilds load, tick loop, **all packet handling**, players, pets, NPCs, items, spells, maps, `Envir.Process`, admin-action drain, saves, final shutdown save. Owner of world state. |
| **Mob threads** 1..`ThreadLimit-1` | `WorkLoop` start, only if `Settings.Multithreaded` | `ThreadLoop(MobThreads[j])`: AI of monsters with `Master == null` in that thread's `ObjectsList`. `MobThreads[0]` runs **on the game thread**. |
| **IOCP / thread pool** | `TcpListener.BeginAcceptTcpClient`, `MirConnection.BeginReceive` | Accept → `new MirConnection` + `lock (Connections) Connections.Add`; receive → decode → `ConcurrentQueue<Packet> _receiveList` |
| **Status port** | `StartNetwork`, **hard-coded port 3100** | `MirStatusConnection` (max 5), ASCII status line every 10 s |
| **Legacy HTTP** | `Server/Utils/HttpServer.cs` if `Settings.StartHTTPService` | `/newaccount` → `Envir.Main.HTTPNewAccount` mutates `AccountList`/`NextAccountID` from the HTTP thread (takes `AccountLock`, but the game thread reads `AccountList` without it — unsafe); `/broadcast` → `Envir.Main.Broadcast` **directly from the HTTP thread** (unsafe) |
| **Web admin** | `WebAdminHost.Start` (`HttpListener`, `Task.Run` accept loop, per-request tasks, `SemaphoreSlim` 32) | Admin REST; marshals world work via `WebAdminGame.RunAsync` → `Envir.QueueAdminAction` |
| **Async file IO** | `FileStream.BeginWrite` in `BeginSaveAccounts`, `SaveGuilds`, `SaveGoods`, `SaveConquests` | Rename `*n` temp → final; `EndSaveAccounts` clears `Saving` |
| Reboot / web start-stop | `Envir.Reboot()`; `ServerControlRoutes` `Task.Run` | `Stop(); Start();` off the game thread |
| Web admin timers/tasks | `System.Threading.Timer`: `WebAdminStatusSampler` (5 s), `WebAdminMaintenance` countdown (1 s → `KickNonAdmins`), `ServerControlRoutes` restart countdown (1 s); `Task.Run`: `WebAdminLoginLog.FlushLoop`, `WebAdminAdmins` persist | Thread-pool threads; world work must go through `WebAdminGame.Post/RunAsync` |
| Editor import | `ItemInfoFormNew` CSV import `await Task.Run` | Off the UI thread, on `Envir.Edit` data |

There is **no** Crystal save thread, log thread, network-sender thread or editor thread (contrast WoOOLcraft; the web admin has its own login-log flush task). log4net writes synchronously on the caller's thread (`MinimalLock` appenders open/close the file per write).

### 2.4 Tick Loop (`Envir.WorkLoop`, `Envir.cs` ~L1996–2231)

Setup: `Time = Stopwatch.ElapsedMilliseconds`; `saveTime = Time + Settings.SaveDelay * Minute`; create `MobThread`s; `StartEnvir()`; `CanStartEnvir()`; start mob threads; `StartNetwork()` (LoadAccounts → LoadGuilds → LoadConquests → LoadGTInfo → listeners); optional `HttpServer`; `OpenAdminQueue()`; `RaiseWorldStarted()`.

Each `while (Running)` iteration — **no sleep** (`Thread.Sleep(1)` is commented out, one core pegged):

1. `Time = Stopwatch.ElapsedMilliseconds`; once/sec roll `LastCount`/`LastRealCount`.
2. At most once per ms (`conTime != Time`): `AdjustLights()`; **`lock (Connections)`** reverse loop `Connections[i].Process()` (every packet handler runs here, inside the lock); then `lock (StatusConnections)`.
3. If Multithreaded: wake parked mob threads (`EndTime = Time + 10`, `Monitor.PulseAll(_locker)`), then `ThreadLoop(MobThreads[0])` on this thread — returns only when every sub-thread has set `Stop` (**barrier**).
4. Object slice: walk `Envir.Objects` (`LinkedList<MapObject>`) from the persisted cursor `current` for **≤ 20 ms**; process an object if `!Multithreaded || Race != Monster || Master != null` and `Time > OperateTime` → `Process(); SetOperateTime();`.
5. `MapList[i].Process()` for **every** map (respawns, doors, lightning/lava, map `ActionList`).
6. `DragonSystem?.Process()`.
7. `Envir.Process()` (~L2322): time-gated blocks — new day, guild wars (1 min), `Guilds[i].Process` (1 min), conquests (10 s), rented items (5 min), auctions (10 min), `RespawnTick` (10 s), `Robot.Process(RobotNPC)` (1 min), script `Timers` (1 s).
8. `DrainAdminActions()` — ≤ 64 actions or ~10 ms; each action in its own try/catch.
9. Save if `Time >= saveTime || _saveNowRequested`: `BeginSaveAccounts(); if (!SystemDataStale) SaveDB(); SaveGuilds(); SaveGoods(); SaveConquests();`.
10. Every 5 min broadcast online count; every `LineMessageTimer` min a random line message.

Shutdown tail: `CloseAdminQueue(); StopNetwork(); StopEnvir() [SaveGoods(true), GC.Collect()]; SaveAccounts(); SaveGuilds(true); SaveConquests(true);` — **no `SaveDB()` at shutdown**. Only `SaveAccounts()` is synchronous; the final guild/goods/conquest writes are async `BeginWrite`s that nothing awaits, so a fast process exit can leave `*n` temp files and the previous file in place (UNVERIFIED in practice).

**Time:** `Envir.Time` (long ms, set only by the loop) for timers; `Envir.Now` (`_startTime.AddMilliseconds(Time)`, UTC-based) for calendar. Constants `Settings.Second/Minute/Hour/Day` (ms). Timer idiom: `if (Envir.Time >= xTime) { xTime = Envir.Time + Settings.Minute; ... }`. Mob threads see `Time` frozen at the tick start.

**Scheduling:** objects are processed when `Time > OperateTime`. `HumanObject.SetOperateTime` sets `OperateTime = Envir.Time` (players/heroes every pass). `MonsterObject.SetOperateTime` (~L1232) = `Time + 2000` min'd over every timer field, `ActionList` time, poison tick and buff time.

**Mob threading (`Settings.Multithreaded=true`, `ThreadLimit=2`, `[General]` in Setup.ini):** each `Map` gets `Thread = Envir.Random.Next(Settings.ThreadLimit)` (Map.cs ctor). `MapObject.Spawned()` adds a monster to `Envir.Objects` AND `MobThreads[CurrentMap.Thread].ObjectsList`; `SpawnThread` is fixed at spawn even if the monster later changes map. Pets (`Master != null`), players, NPCs, items and spells always run on the game thread. `MobThreads` is sized from `Settings.ThreadLimit` when `Envir` is first touched.

### 2.5 Player Connections & Packets

| Component | Path | Role |
|---|---|---|
| Listener | `Envir.StartNetwork` (~L3532), `Envir.Connection` accept callback | `TcpListener` on `Settings.IPAddress`:`Settings.Port` (default 127.0.0.1:7000) |
| Connection | `Server/MirNetwork/MirConnection.cs` | `Stage`, `Account`, `Player`, `Observers`, `Observing`; APM receive; `_receiveList`/`_sendList`/`_retryList` |
| Protocol | `Shared/Packet.cs` | Frame `ushort totalLength | short Index | payload`; factories `GetClientPacket`/`GetServerPacket` (switches) |
| Packet classes | `Shared/ClientPackets.cs` (`C`), `Shared/ServerPackets.cs` (`S`) | `ReadPacket`/`WritePacket` |
| IDs | `Shared/Enums.cs` `ServerPacketIds : short` (~L1434, last `StoragePasswordResult` = 278), `ClientPacketIds : short` (~L1717, last `RemoveStoragePassword` = 152) | **ID = declaration position** (no explicit values) |
| Dispatch | `MirConnection.ProcessPacket` big `switch (p.Index)` → private handler | No reflection, nothing auto-registers |

**Stages** (`enum GameStage { None, Login, Select, Game, Observer, Disconnected }`, server-only in `MirConnection.cs`): None →(`C.ClientVersion` OK)→ Login →(`Envir.Login`)→ Select →(`StartGame` → `PlayerObject.StartGame`)→ Game; `LogOut` → Select; `AddObserver` → Observer.

**Receive path:** `ReceiveData` (IOCP) → `Packet.ReceivePacket` → `_receiveList`. Unknown ID → factory null → **the connection's whole buffer is discarded**, no log. Any `ReadPacket` exception → `InvalidDataException` → `Envir.UpdateIPBlock(IP, 24 h)` + disconnect. Flood guard: more than `Settings.MaxPacket` (50) socket **reads** per 5 s → 24 h IP block. Every accept sets a `Settings.IPBlockSeconds` (5 s) IP block; more than `Settings.MaxIP` (5) connections per IP is refused.
**Process path (game thread):** drain `_receiveList` → `ProcessPacket` (resets `TimeOutTime = Time + Settings.TimeOut`, 10 s) → re-enqueue `_retryList` (movement/attack/magic retried while `Player.ActionTime > Envir.Time`) → timeout → flush `_sendList` into one `BeginSend`.
**Send:** `MirConnection.Enqueue(Packet)` is queue-safe from any thread and mirrors every `Observable` packet (default **true**) to `Observers`. `PlayerObject.Broadcast` also forwards only `BroadcastObservePackets` to observers. `HeroObject.Enqueue` is a **whitelist** (AddBuff, RemoveBuff, PauseBuff, MagicDelay, MagicLeveled, DeleteItem, UseItem); anything else a hero enqueues is dropped.
**Per-connection info caches:** `SentItemInfo`, `SentMonsterInfo`, `SentNPCInfo`, `SentQuestInfo`, ... — `CheckItemInfo`/`CheckItem` send `S.NewItemInfo` once per connection.
**Version check:** first packet `C.ClientVersion` (MD5 of the client exe) vs `Settings.VersionHashes` from `[General] VersionPath` (default `.\Mir2.Exe`), only if `CheckVersion`. The client hashes `Application.ExecutablePath` (`LoginScene.cs`), i.e. the `Client.exe` apphost, which does not contain the managed code — the check is not proof that client and server share a `Shared.dll`. `VersionPath` is comma-separated. **Trap:** if no listed file exists, `VersionHashes` is empty and with `CheckVersion=True` every client is refused (reason 10); the default `.\Mir2.Exe` does not match `Client.exe`.
**Disconnect reasons.** Wire codes in `S.Disconnect.Reason` (client `MirScene` switch): 0 server stop, 1 logged in elsewhere, 2 packet error (over-long chat/NPC key), 3 WorkLoop crash, 4 kicked by admin (web admin GM kick), 5 max connections (client-defined, not sent by the server). Internal `StopGame`/`SoftDisconnect` reasons (never sent): 10 wrong version, 20 socket gone, 21 timeout, 22 client sent Disconnect, 23 logout, 24 observed player left. Pick unused numbers in the right space for new reasons.

### 2.6 State & Persistence

| File (cwd-relative) | Writer / Reader | Notes |
|---|---|---|
| `.\Server.MirDB` (system data) | `Envir.SaveDB` (~L2452) / `LoadDB` (~L2839) | Header `Version, CustomVersion`, index counters, then MapInfo, ItemInfo, MonsterInfo, NPCInfo, QuestInfo, DragonInfo, MagicInfo, GameShopItem, ConquestInfo, RespawnTimer, GTMap. **GTMap block is written but never read back.** |
| `.\Server.MirADB` (user data) | `BeginSaveAccounts`/`SaveAccounts` / `LoadAccounts` (~L2962) | Counters, HeroList (before accounts), AccountList (embeds CharacterInfo incl. mail), auctions, mail id, GameshopLog, SavedSpawns |
| `.\Guilds\{i}.mgd` | `SaveGuilds`/`LoadGuilds` | **File name = position in `GuildList`**, not GuildIndex; sentinel `int.MaxValue` + version header |
| `.\Envir\Goods\{npcIndex}.msd` | `SaveGoods` | sentinel 9999 + version |
| `.\Conquests\{Index}.mcd` | `SaveConquests`/`LoadConquests` | **No version header** |
| `.\Archive\*.MirCA` | archived characters | Version + CharacterInfo |
| `.\Back Up\Database\*.bak`, `.\Back Up\Accounts\*.bak` | every periodic save | **Never pruned** |

**Save mechanics (all on the game thread):**
- `SaveDB()` — fully synchronous: copy `Server.MirDB` → `Back Up\Database\Database yyyy-MM-dd HH-mm-ss.bak`, then `File.Create` in place (not atomic). Ends `if (this == Edit) _systemDataStale = true`.
- `BeginSaveAccounts()` — serialise into a `MemoryStream` on the game thread, **move** the live `Server.MirADB` into `Back Up\Accounts\`, async-write `Server.MirADBn`; `EndSaveAccounts` (IO thread) renames via an `o` temp and clears `Saving`. Between the move and the rename no `Server.MirADB` exists (recover from `Server.MirADBn` or the newest `.bak`). `SaveAccounts()` (sync, shutdown/stopped-world only) spins `while (Saving) Thread.Sleep(1)`.
- Guilds/Goods/Conquests: serialise on the game thread, async `BeginWrite` to `*n`, rename in the completion.
- **`SystemDataStale`** (fork): once `Envir.Edit.SaveDB()` ran, Main skips its periodic `SaveDB()` until `StartEnvir` (Start/Reboot) clears it — otherwise Main's older in-memory system data would overwrite the editor's save. Main's in-memory system data only reflects Edit changes after a world reboot (exceptions: web `?live=1` monsters, magic sync).

**Versioning:** one number for every file — `Envir.Version = 118`, `MinVersion = 60`, `CustomVersion = 0` (`Envir.cs` ~L55). Writers always write the current version; readers gate on the static `Envir.LoadVersion`/`LoadCustomVersion` (set by `LoadDB`, overwritten by `LoadAccounts`) or on a `version` ctor parameter — follow whichever the class already uses. `LoadDB` refuses `< MinVersion` or `> Version`; since roadmap P2 `LoadAccounts` refuses the same range (throws). v118 (roadmap P3): `Buff.Data` is a typed key/tag/value encoding (`BuffInfo.cs` `DataTag`, append-only byte values); v117 and older BinaryFormatter blobs are skipped on load (Concentration interrupt state resets). **Nothing in Server.Library may call `Functions.SerializeToBytes`/`DeserializeFromBytes` (BinaryFormatter, removed on .NET 9+)**; the Shared helpers are deleted in roadmap C5. Committed format fixtures: `Server.Persistence.Tests/Fixtures/v117`, `v118`. Shared models (`UserItem`, `ItemInfo`, `Stats`, `ExpireInfo`, `SealedInfo`, `RentalInformation`, ...) default `version = int.MaxValue` → the same ctor reads the **packet** layout (`Awake` has no version parameter; `GameShopItem` uses a separate `(reader, bool packet)` ctor / `Save(writer, packet)` for the wire). **Nothing reads `CustomVersion`**, and by the user's decision nothing should: new fields bump `Version` (§4.3).

**Load order** (`StartEnvir` ~L3426 → `StartNetwork`): `LoadDB` (needs `Settings` loaded; `FillMagicInfoList`; GameShop binds to Edit) → `BuffInfo.Load()` → recipes → `MapInfo.CreateMap()` per map → StartItems → `ReloadDrops()` → line messages → Dragon → default/monster/robot NPC scripts → `LoadAccounts` (HeroList before AccountList) → `RaiseAccountsLoaded` → `LoadGuilds` → `LoadConquests` (needs maps and guilds) → `LoadGTInfo` → listeners.

**Silent data loss on load** (persisted by the next save = permanent): `UserItem`s whose `ItemIndex` no longer exists are dropped everywhere (inventory, equipment, storage, guild storage, auctions); `UserMagic`/`UserIntelligentCreature`/`QuestProgressInfo` with no info are skipped; characters past `ArchiveDeletedCharacterAfterMonths` (1) / `ArchiveInactiveCharacterAfterMonths` (12) move to `.\Archive`; **every account except the first in list order with zero characters is dropped** (`LoadAccounts`, `if (i > 0 && NextAccount.Characters.Count == 0) continue;`).

### 2.7 Embedded HTTP — MirCraft web admin (+ legacy HttpServer)

Fork-only (not in upstream). Authoritative developer doc: `Server/WebAdmin/README.md`.

| Piece | Where | Facts |
|---|---|---|
| Host | `Server/WebAdmin/WebAdminHost.cs` | `HttpListener`, started in `Program.Main` before `SMain`, independent of the world. Concurrency 32 (503 when full). Security headers on every response; `/api/*` `no-store`. `WebAdminException` → JSON error; other exceptions → 500 "Internal error, see server log". |
| Options | `WebAdminOptions.cs`, `[WebAdmin]` in Setup.ini | `Enabled=True`, `Bind=127.0.0.1`, **`Port=5680`**, `WebRoot=.\wwwroot` (vs `AppContext.BaseDirectory`), `SessionHours=8` (1–720), `MaxBodyKB=2048` (16–65536). Read once per process → **restart Server.exe** to change. |
| Router | `WebAdminRouter.cs` | `IWebAdminRoutes` classes found by reflection (`RegisterAll`); `/api/auth/*` Anonymous, else `RouteAuth.Admin`; duplicate method+shape throws and drops that module. Modules: `Server/WebAdmin/Routes/*Routes.cs`. |
| Auth | `WebAdminAuth.cs`, `WebAdminSessions.cs`, `WebAdminAdmins.cs` | Sign-in = game account with `AdminAccount = true`. Password via `Server/Utils/Crypto.cs` (PBKDF2-SHA1, 50 iterations, 24-byte salt/hash, hash stored as a UTF8 string). Offline directory `<exe>\WebAdmin\admins.dat` while the world is stopped. Cookie `MirCraftAdmin` (HttpOnly, SameSite=Strict, not Secure); CSRF cookie `MirCraftAdminCsrf` + header `X-MirCraft-CSRF` on every mutating request. Login must be `application/json` and same-origin; rate limits 10/IP/min, 120/min global, 10 failures per (account, IP) per 15 min. Sessions in memory. |
| Game bridge | `WebAdminGame.cs`; `Envir.QueueAdminAction` (~L3298), `DrainAdminActions` (~L3325), `RequestSaveNow` (~L3312) | `RunAsync<T>(fn, timeoutMs=5000)`: 503 `WorldNotRunningException` if not queued, 504 `GameThreadTimeoutException` on timeout (cancelled if not started; `started=true` means it **may** have run). `RunOrInlineAsync`: inline under `lock (Envir.AccountLock)` only when fully stopped. `Post(fn)`: fire-and-forget. `WorldState`: running/stopped/starting/stopping; `WorldCrashed`. |
| Hooks | `WebAdminHooks.cs` | `WorldStarted/Stopped`, `AccountsLoaded/Saved`, `LoginAttempt`, `LoginGate` (`Func<AccountInfo,string>`), `PlayerEntered/Left`, `Chat` — raised on the calling (usually game) thread, wrapped in try/catch |
| Game data | `Server/WebAdmin/GameData/*` | Grid engine over `Envir.Edit` under `WebAdminGame.EditLock`; `__rev` per-row conflicts; `GameDataSave.EnsureSavable()` + `SaveEdit()` → `RunFileIo` (on the game thread while Main still saves, else under `Envir.LoadLock`); `WebAdminPending.Add` for reboot-only data |
| Files | `ScriptFiles.cs`, `ScriptValidation.cs`, `Routes/BackupRoutes.cs` | Atomic writes (temp + `File.Replace`), path confinement, encoding-preserving; backups `Back Up\WebAdmin\Scripts\...`; DB backup restore only when the world is fully stopped |
| Audit/logs | `WebAdminAudit.cs`, `WebAdminLoginLog.cs`, `WebAdminLogTap`, `WebAdminLog` | `Logs\WebAdmin\audit-yyyy-MM-dd.jsonl`, `logins-*.jsonl` (never pruned); log tap ring buffers fed by `MessageQueue` |
| Settings | `WebAdminOptions.SettingsLock`, `SettingsCatalog.cs`, `Routes/ConfigRoutes.cs` | Every Settings RMW / `Settings.Save*()` from web threads under `SettingsLock` (`InIReader` is not thread-safe); restart-scope keys (`Multithreaded`, `ThreadLimit`, `[WebAdmin]`) are staged |
| Frontend | `Server.MirForms/wwwroot/admin/`, `wwwroot/shared/` | Vanilla JS IIFEs, no npm/build; CDN ag-Grid Community 32.3.3, Chart.js 4.4.7, Google Fonts only |

Lock order: **`WebAdminGame.EditLock` → `Envir.LoadLock` → `Envir.AccountLock`** (`BackupRoutes.cs` ~L408). Server control (`Routes/ServerControlRoutes.cs`) has an Interlocked `_transition` guard (409); WinForms Start/Stop/Reboot bypass it. Open follow-ups (README §6): WinForms editors and web share `Envir.Edit` + settings files, last writer wins (only web has `__rev`); 15 s shutdown drain with writes not refused while the form closes; audit/login logs unpruned; character search by item scans all characters on the game thread; no `X-Forwarded-For`; sessions and maintenance gate in memory only; ag-Grid deprecation warnings.

**Legacy `HttpServer`** (`Server/Utils/HttpServer.cs`, `HttpService.cs`): only when `[Network] StartHTTPService`; prefix `Settings.HTTPIPAddress` (default `http://127.0.0.1:5679/`), trusted-IP check `HTTPTrustedIPAddress`. Logs with `Console.WriteLine` (invisible). Don't extend it — new HTTP features go in the web admin.

### 2.8 Data folder & Settings

Every path is relative to the **process cwd** (`Path.Combine(".", ...)`); `Program.Main` does not set the cwd, so `Server.exe` must be launched from its own folder. The web admin uses `AppContext.BaseDirectory` for its own files — a wrong cwd splits game data from web-admin data.

```
<cwd>\  Server.exe, Server.MirDB, Server.MirADB, log4net.config, Localization\*.json, wwwroot\
        Configs\   Setup.ini, ExpList, BaseStats{Job}, MonsterRarity.ini, FishingSystem, MailSystem, ... (Settings.Load)
        Envir\     NPCs\ (00Default.txt, 00Robot.txt, monster script), Goods\, Recipe\, Quests\, Drops\, Routes\,
                   NameLists\, Values\, Notice.txt, LineMessage.txt, Previews\
        Maps\*.map   Guilds\   Conquests\   Archive\   Exports\   Back Up\{Database,Accounts,WebAdmin}\
        Logs\{Server,Chat,Debug,Player,Spawn,WebAdmin}\   WebAdmin\admins.dat
```

- `Settings` (`Server/Settings.cs`) is a static class of public fields read from every thread without synchronisation. `Settings.Load()` reads `Configs\Setup.ini` sections General, Network, WebAdmin, Permission, Optional, Database, Game, ... then the per-system ini files, then `.\Localization\<Language>.json`.
- **`Settings.Save()` rewrites `Setup.ini` from the statics** at every process exit and from `ConfigForm.FormClosed` (always, even on cancel) → manual edits made while the server runs are lost. Edit ini files with the server stopped.
- Defaults worth knowing: `Port 7000`, `TimeOut 10000`, `MaxUser 50`, `MaxIP 5`, `MaxPacket 50`, `IPBlockSeconds 5`, `RelogDelay 50`, `SaveDelay 5` (min), `Multithreaded true`, `ThreadLimit 2`, `MonsterProcessWhenAlone false`, `CheckVersion true`, `GMPassword "C#Mir 4.0"`.
- `ReloadDrops` → `DropInfo.Load(..., createIfNotExists: true)` **creates template drop files** for every monster without one: starting a server against a data folder mutates it.

### 2.9 Key Entry Files

| File | Purpose |
|---|---|
| `Server.MirForms/Program.cs` | Entry: log4net, Settings, web admin, `SMain`, exit save |
| `Server.MirForms/SMain.cs` | WinForms host, `InterfaceTimer_Tick`, menus (Start/Stop/Reboot/Reload), editor launchers |
| `Server/MirEnvir/Envir.cs` (~5.6k lines) | World singleton: WorkLoop, ThreadLoop, Process, Load/Save, network, login/account handlers, admin queue, lookups; also `MobThread`, `RandomProvider` |
| `Server/MirEnvir/Map.cs` | Runtime map: cells, respawns, `Process`, `CompleteMagic` (ground/AoE spells) |
| `Server/MirNetwork/MirConnection.cs` | Connection, stages, `ProcessPacket` switch + handlers, `CheckItem*` |
| `Server/MirObjects/{MapObject,HumanObject,PlayerObject,HeroObject,MonsterObject,NPCObject,ItemObject,SpellObject}.cs` | Object hierarchy; `Monsters/` (212 AI files), `Hero/`, `NPC/` (script engine) |
| `Server/MirDatabase/*Info.cs` | Persisted models (`(BinaryReader)` ctor + `Save(BinaryWriter)`) |
| `Server/Settings.cs`, `Server/MessageQueue.cs`, `Server/Logger.cs`, `Server/Utils/Crypto.cs` | Config, logging, password hashing |
| `Shared/Packet.cs`, `Shared/{Client,Server}Packets.cs`, `Shared/Enums.cs`, `Shared/Data/*` | Wire + shared models |
| `Server/WebAdmin/*` | MirCraft backend (`WebAdminHost`, `WebAdminRouter`, `WebAdminGame`, `WebAdminHooks`, `GameData/`, `Routes/`) |

---

## 3. CRITICAL GAME SERVER RULES

### 3.1 Memory & GC Management

#### NEVER
- **NEVER** use **LINQ** (`Where`, `Select`, `Any`, `FirstOrDefault`, `ToList`, `OrderBy`, `Count(pred)`) in `WorkLoop`, `ThreadLoop`, `MapObject.Process` overrides, `SetOperateTime`, `Broadcast`, cell scans, combat/movement paths or packet handlers.
- **NEVER** allocate strings for logging/diagnostics in hot paths (`string.Format`, `$""`, `+`) — `MessageQueue.Enqueue` formats and writes the log file **synchronously on the calling thread**.
- **NEVER** `new List<T>()` / `new Dictionary<K,V>()` per tick per object — hoist to fields, pre-size, reuse.
- **NEVER** box value types or capture closures in per-tick code (note `DelayedAction`'s `params object[]` boxes — don't add new per-tick `DelayedAction`s where a field timer works).
- **NEVER** add `GC.Collect()` — the only existing call is in `StopEnvir`.
- **NEVER** let a per-connection queue grow unbounded — enforce `Settings.MaxPacket` on receive and cap any new queue you add.
- **NEVER** parse JSON or build `Dictionary<string,object>` on the game thread; web admin delegates passed to `RunAsync` only read/mutate and snapshot — parsing, hashing, IO and serialisation happen on the HTTP thread.
- **NEVER** use `Envir.GetObject/GetObjects`, `NPCObject.Get`, `NPCScript.GetOrAdd`, `Envir.GetMapByNameAndInstance` (LINQ over global collections) in per-tick code.

#### ALWAYS
- **ALWAYS** treat `WorkLoop` + `ThreadLoop` + `MirConnection.Process/ProcessPacket` + `MapObject.Process` overrides + `Map.Process` as **hot paths**: for every change there, state the per-call allocations and complexity added or removed; where a run is possible (scratch copy, §5.4), compare `Envir.LastRunTime`/`MobThread.LastRunTime` before and after under the same load.
- **ALWAYS** use index `for` loops (reverse when removal is possible) over `foreach`/LINQ on `List<T>`.
- **ALWAYS** use `Buffer.BlockCopy`/`Array.Copy` for byte buffers; consider `ArrayPool<byte>.Shared` when touching receive code.
- **ALWAYS** prefer `struct` for small short-lived aggregates; keep `class` for `MapObject`, `Packet`, `*Info` models.
- **ALWAYS** pre-size collections when the maximum is known; avoid LOH (>85 KB) allocations in steady state.
- **ALWAYS** use `TryDequeue`/`IsEmpty` on `ConcurrentQueue`, not `.Count`, for control flow.
- **ALWAYS** keep outbound packets batched (one `BeginSend` per connection per `Process`).
- **ALWAYS** cache `Envir.Now` in a local inside loops (the property computes `AddMilliseconds` each call).

#### Known debt — existing breaches (do not copy; do not fix in drive-by edits)

| Location | Issue |
|---|---|
| `Envir.WorkLoop` | ~~Busy spin~~ (idle park since roadmap P6); `Map.Process` for every map every iteration |
| `MirConnection.Process` | `new List<byte>()` + `AddRange(p.GetPacketBytes())` + `ToArray()` per flush |
| `MirConnection` queues | `_sendList`/`_retryList` uncapped; `MaxPacket` limits socket reads per 5 s, not queued packets |
| `PlayerObject.Broadcast` | `Array.Exists` lambda capturing `p` (closure per broadcast) + `foreach` over `Observers` |
| `MirConnection.ReceiveData` | `_rawData` re-allocated + `BlockCopy` on every read |
| `Packet.GetPacketBytes` | Two `MemoryStream`s + arrays per packet **per recipient**; a broadcast to N players serialises N times |
| `MonsterObject.GetInfo` (~L2876), `HeroObject` | `Buffs.Where().Select().ToList()` every time a player sees the object |
| `MonsterObject.Process` | `RefreshNameColour()` every process |
| `MapObject.HasAnyBuffs` | `Select().Except(new List<>).Intersect().Any()` (buff-expiry paths) |
| `HumanObject` FireWall (~L4157), `FindAllNearby(...).Where().ToList()` (~L5947), portal `Envir.Spells.Count(lambda)` | LINQ per cast |
| `HumanObject.Process` | `Pets.Count()` LINQ per tick |
| `Envir.Process` timers | `Timers.Keys.ToArray()` once per second |
| `SaveGuilds` | `Guilds.Find(lambda)` per guild per save |
| `NPCObject` speech, `NPCScript.PriceRate`, `GuildObject` ctor `Any`, `MonsterInfo.GameName` (`Regex.Replace`), `UserMagic` info lookup | LINQ/regex over collections |
| `Cell.LowWall/HighWall` static getters | allocate a new `Cell` per call |
| `MessageQueue.Enqueue` | `String.Format` + `DateTime.Now` + `Logger.GetLogger(type)` lookup + MinimalLock file write per message on the caller |

#### 3.1.1 In-memory indexes over `Envir` lists

Crystal keeps plain `List<T>` on `Envir` (`AccountList`, `CharacterList`, `ItemInfoList`, `MonsterInfoList`, ...) with linear lookups (`GetItemInfo`, `GetCharacterInfo`, `GetGuild`). When a hot path needs keyed lookup, add a **runtime-only** index:
- **NEVER** write the index in any `Save(BinaryWriter)` — it is rebuildable, not data.
- **NEVER** scatter index updates: one method creates the row and inserts into the index; every remove path updates it too.
- **ALWAYS** build lazily, keep it referential (same object instances), game-thread-only, and document the contract on the field.

### 3.2 Threading & Concurrency

#### Thread ownership model

```
┌──────────────────────────────────────────────────────────────────────┐
│ GAME THREAD (Envir.WorkLoop): world state, connections, players,     │
│ pets, NPCs, items, spells, maps, Envir.Process, saves, admin actions │
├──────────────────────────────────────────────────────────────────────┤
│ MOB THREADS (only during the ThreadLoop barrier, Multithreaded=true):│
│ AI of Master==null monsters in their ObjectsList — concurrently with │
│ MobThreads[0] on the game thread. No locks anywhere.                 │
├──────────────────────────────────────────────────────────────────────┤
│ IOCP: ConcurrentQueue enqueue + lock(Connections) Connections.Add    │
│ HTTP (web admin): Envir.QueueAdminAction / WebAdminGame.RunAsync     │
│ UI thread: Envir.Edit editors; must not touch Envir.Main live state  │
│ Envir.Edit: no thread — WebAdminGame.EditLock (web), UI (WinForms)   │
└──────────────────────────────────────────────────────────────────────┘
```

#### NEVER
- **NEVER** use `async`/`await`, `.Result`, `.Wait()` in `WorkLoop`, `ThreadLoop`, `Process`, `ProcessPacket` or anything they call.
- **NEVER** touch `Envir.Main` live state (`Players`, `MapList`, `Objects`, `Connections`, `AccountList`, `CharacterList`, `GuildList`, `Auctions`, any `PlayerObject`) from HTTP threads, WinForms event handlers, `Task.Run` or the legacy `HttpServer`.
- **NEVER** add `lock`s in hot paths or in `MapObject`/monster code — mob threads and the game thread are coordinated only by the `_locker`/`Monitor` barrier.
- **NEVER**, from monster code (it may run on a mob thread), mutate cross-map or global state — another map, `Envir.Players`, `Envir.Objects` outside `Spawned`/`Despawn`, global counters — without considering `Settings.Multithreaded`. Spawning onto a map with a different `Map.Thread` mutates another thread's `ObjectsList` concurrently. Defer such work by queuing a `DelayedAction` on `CurrentMap.ActionList` (e.g. `DelayedType.Spawn`; drained by `Map.Process` on the game thread after the mob barrier), or do it from player/pet/map code. Do **not** use the monster's own `ActionList` — `MapObject.Process` drains it on the mob thread. A monster whose `SpawnThread` differs from `CurrentMap.Thread` can still race other mob threads.
- **NEVER** use a shared `new Random()` or a static `Random` in server code — use `Envir.Random` / `RandomProvider.GetThreadRandom()` (ThreadLocal).
- **NEVER** change `Settings.Multithreaded` / `Settings.ThreadLimit` while an `Envir` exists — `MobThreads` is sized once; stage to ini and restart the process.
- **NEVER** mutate a shared collection from two threads; the only sanctioned cross-thread write is `lock (Connections) Connections.Add` in the accept callback.
- **NEVER** call `SaveDB`, `BeginSaveAccounts`, `SaveAccounts`, `SaveGuilds` from HTTP/UI threads while the world runs — use `Envir.Main.RequestSaveNow()`. `SaveAccounts()` off-thread only when the world is fully stopped.
- **NEVER** add blocking I/O, `Thread.Sleep` or waits on the game thread. **Sole exception (roadmap P6, approved 2026-10-09):** the WorkLoop idle park, `_loopSignal.WaitOne(1)` at the end of an iteration that took < 1 ms, guarded by `[Optimisation] IdlePark` and woken by `Envir.WakeLoop()` from receive, accept and `QueueAdminAction`. Anything new that must wake the loop calls `WakeLoop()`; nothing else may wait.
- **NEVER** read `DateTime.Now`/`DateTime.UtcNow` in game logic: use `Envir.Time` for timers and `Envir.Now` (UTC-based) for calendar values, cached in a local inside loops.
- **NEVER** call `Envir.Stop()` / `Reboot()` on the game thread (Stop busy-waits for that thread).
- **NEVER** take `lock (Connections)` while holding `AccountLock` (the loop holds `Connections` across packet handling and `Disconnect` takes `AccountLock` inside it). **NEVER** take `EditLock` on the game thread or while holding `AccountLock` (use `Monitor.TryEnter` and degrade).
- **NEVER** assume the packet-handler thread is the socket thread: `ProcessPacket` runs on the game thread.

#### ALWAYS
- **ALWAYS** marshal cross-thread world work through `Envir.Main.QueueAdminAction(action, dropped)` or its wrappers `WebAdminGame.RunAsync` / `RunOrInlineAsync` / `Post`, and return snapshots/DTOs, never live objects.
- **ALWAYS** check the result: `QueueAdminAction` returns false when the queue is closed or the world isn't running; `RunAsync` throws 503/504. `Envir.Main.Running` stays **true** after a crash — use `WebAdminGame.WorldState`/`WorldCrashed`.
- **ALWAYS** use `ConcurrentQueue<T>` for producer → game-thread hand-off.
- **ALWAYS** use `Interlocked` for any new global counter or ID reachable from monster code.
- **ALWAYS** iterate `Connections`, `Players` and `Map.Players` backwards when removal is possible.
- **ALWAYS** take locks in the order `EditLock → LoadLock → AccountLock`, and `Connections → AccountLock`.
- **ALWAYS** edit system data on `Envir.Edit` (under `EditLock` from web threads), never on `Envir.Main`.

#### Async zones

| Zone | Allowed | Pattern |
|---|---|---|
| Web admin handlers | `async Task` | `WebAdminHost` per-request tasks; await `WebAdminGame.RunAsync` |
| World start/stop from web | `Task.Run` | `ServerControlRoutes` (never the game thread) |
| Save file writes | APM `BeginWrite` | existing `BeginSaveAccounts`/`SaveGuilds`/`SaveGoods`/`SaveConquests` |
| Game simulation, packet handlers, monster AI | **Forbidden** | sync only |

#### Known debt — existing breaches
- WinForms UI thread mutates live state: `Server.MirForms/Account/PlayerInfoForm.cs` (`Character.Player.Teleport`), `Account/Market.cs` (`Envir.Main.Auctions.Remove`), `BalanceConfigForm` (`RequiresBaseStatUpdate` → `RefreshStats` on heroes), `Database/GameShop.cs` (`ClearGameshopLog`, `ResetGS`), `SMain` menus (`ReloadNPCs`, `ReloadDrops`, `ReloadLineMessages`, `IPBlocks.Clear`, `GlobalMessageButton_Click` → `ReceiveChat` for every player), `SMain.ProcessPlayersOnlineTab`/`ProcessGuildViewTab` and `InterfaceTimer_Tick` reading `Envir.Players`/`GuildList`.
- `Systems/MonsterTunerForm.cs` and `Systems/DragonInfoForm.cs` call `Envir.Main.SaveDB()` on the UI thread (races the game thread's SaveDB, and overwrites a newer Edit save with Main's data). WinForms editors save `Envir.Edit` with no `LoadLock`/`EditLock`.
- Legacy `HttpServer` `/broadcast` calls `Envir.Main.Broadcast` from its own thread; `/newaccount` → `HTTPNewAccount` adds to `AccountList`/`++NextAccountID` from it (under `AccountLock`, but game-thread readers don't take it).
- `WebAdminStatusSampler` reads `Envir.Main.Players.Count`/`Connections.Count` from a timer thread (racy; tolerated for counts only).
- Game-thread blocking IO: `SaveDB()` (sync backup copy + rewrite), synchronous log4net writes in `MessageQueue`, file reads/writes in `ReloadNPCs`/`ReloadDrops`/`ReloadLineMessages` (sanctioned only as operator-triggered actions, never per tick). `lock (Connections)` is held across all packet handling every ms, plus `lock (StatusConnections)`.
- Time: guild territory rent uses local `DateTime.Now` (`GuildObject`/`GuildInfo` `GTRent`, `PlayerObject` ~L10525, `NPCSegment` ~L3142) while `Envir.Now` is UTC. `Awake` upgrade/hit rolls (`Shared/Data/ItemData.cs` ~L995/1041) create `new Random(DateTime.Now.Millisecond)` per call.
- Monster code on mob threads: `MapObject.ObjectID = Envir.ObjectID` → `++_objectID` (non-atomic → duplicate ObjectIDs possible), `Envir.MonsterCount++/--`, `Map.MonsterCount`, `Envir.Objects.AddLast` from `Spawned()` of drops/summons/spells, damaging players on another thread.
- Accept callback counts per-IP connections over `Connections` **without** the lock, and its `finally` spins `while (Connections.Count >= Settings.MaxUser) Thread.Sleep(1)` on an IOCP thread.
- `MirConnection.Enqueue` iterates `Observers` (plain `List`) unsynchronised; `Saving` is a plain bool written on an IO thread.
- WinForms `ConfigForm`/Balance/System forms write `Settings` without `WebAdminOptions.SettingsLock`.
- `Shared/Extensions/ExtensionMethods.cs` `HelperExtensions.Shuffle` uses a `private static Random rng` — not thread-safe; don't call it from monster code.

### 3.3 Error Handling

#### Principle: isolate failures, never cascade

| Failure | Required behaviour for new code | What Crystal does today |
|---|---|---|
| Object `Process` / DelayedAction / map / `Envir.Process` block throws | Catch locally, log with identity, leave the object/feature in a safe state; world keeps running | **One try around the whole `while (Running)`** (~L2058–2205): `SendDisconnect(3)` to everyone, log `InnerWorkloopErrorLine`, shutdown tail with saves. **The world stops**; `Running` stays true (`Start()` becomes a no-op until `Stop()`). |
| Packet handler throws | Validate first; catch → disconnect that client only | Same as above — any handler exception stops the world |
| Monster AI throws on a mob thread | Must never happen | `ThreadLoop` catch logs `ThreadLoopError` and **exits that thread with `Stop == false`** → game thread waits in `ThreadLoop(MobThreads[0])` forever → **silent world hang**. On thread 0 the exception ends that pass and breaks the barrier: the game thread continues the tick while sub-threads are still processing monsters. |
| Malformed packet | Disconnect + IP block | `ReceiveData` catch → `UpdateIPBlock(IP, 24 h)` + disconnect (hard-coded) |
| Unknown packet ID in `ProcessPacket` | Log | logs `InvalidPacketReceived` (no disconnect) |
| Socket error | `Disconnecting = true`, cleaned up next `Process` | as required |
| Save failure | Log; clear `Saving` | `EndSave*Async` completions **swallow silently**; `SaveAccounts()` logs |
| Admin action throws | Contained | `DrainAdminActions` per-action try/catch → `MessageQueue.Enqueue(ex)` (the only isolated work on the loop) |
| Web admin hook subscriber throws | Contained | `WebAdminHooks.Raise*` try/catch |

#### NEVER
- **NEVER** let an exception escape code reachable from `ProcessPacket`, `MapObject.Process`/`Process(DelayedAction)`, `Map.Process`, `Envir.Process` or the save block. Use `TryParse`, null checks, bounds checks, `Race` filters; wrap risky new logic (file IO, parsing, reflection, script calls) in a local try/catch → `MessageQueue.Enqueue(ex)`.
- **NEVER** throw from monster AI (world hang on a mob thread).
- **NEVER** swallow exceptions silently — minimum `MessageQueue.Enqueue(ex)`. No new empty `catch { }`.
- **NEVER** leave a `PlayerObject`, `UserItem`, `CharacterInfo` or `AccountInfo` half-mutated — compute → validate → commit within one call.
- **NEVER** dereference `Player`/`Account` without the stage check that guarantees it (`if (Stage != GameStage.Game) return;`), and null-check anything else.
- **NEVER** call combat/health members on a cell-scan result without filtering `ob.Race` (`ItemObject`, `SpellObject`, `DecoObject` throw `NotSupportedException`; `MapObject.IsAttackTarget(MapObject)` throws for attacker races other than Player/Hero/Monster).
- **NEVER** expect `Envir.Start()` to recover a crashed loop — call `Stop()` first.

#### ALWAYS
- **ALWAYS** validate client input at the packet boundary or the first `PlayerObject` method: stage, ranges, grid indexes, counts, map presence. A bad index throws → world stop.
- **ALWAYS** disconnect via `Disconnecting = true` / `SendDisconnect(reason)` / `SoftDisconnect` / `Disconnect(reason)`; `Disconnect` runs `Player.StopGame` under `AccountLock`, removes from `Connections`, unlinks `Observing`.
- **ALWAYS** log errors with identity (`ObjectID`, `Name`, account, IP) via `MessageQueue.Enqueue`.
- **ALWAYS** prefer early `return` in handlers.

#### Known debt — existing breaches
- ~~No per-object or per-connection isolation~~ — **fixed 2026-10-10 (roadmap P5)**: `WorkLoop` catches per connection (disconnects only that client), per object (players disconnected, others quarantined for `WorldErrors.QuarantineMs`), per `Map.Process`, per `DragonSystem`/`Envir.Process` and per save step; `ThreadLoop` catches per monster. Every isolated error goes through `WorldErrors.Record` (counter, last 20, rate-limited log; exposed by the web admin `/status` JSON as `isolatedErrors`/`recentIsolatedErrors`). **New code still follows the NEVER rules above** — isolation is a safety net, not a licence to throw. Since roadmap P2, `Program.Main` installs `Server.CrashReporter` (observe-only `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` → `Logs\Crash\crash-*.txt` next to the exe + Server log); `Application.ThreadException` is intentionally not hooked.
- Silent swallows: `EndSaveGuildsAsync`, `EndSaveGoodsAsync`, `EndSaveConquestsAsync`, `SendData` (`EndSaveAccounts` now logs and counts failures in `Envir.AccountSaveFailures`, roadmap P2).
- Throwing members used by design: `GetBuffInfo` throws `NotImplementedException` for an unregistered `BuffType`; `MonsterObject.CompleteDeath` throws `NotImplementedException`; `Spawned()`/`Despawn()` throw `InvalidOperationException` on double calls; NPC script parser throws on unknown `#WORD`.

### 3.4 Persistence & Data Format

#### NEVER
- **NEVER** add a serialized field without a version gate (§4.3). A missing gate shifts every later read of old files → exception or silent corruption that the next save (and the `.bak` rotation) persists.
- **NEVER** change the value of an existing member of a persisted/wire enum: `Stat : byte` (stays < 255, `Unknown = 255`), `Monster : ushort`, `ItemType`, `Spell`, `BuffType`, `ObjectType`, `MirClass`, `BindMode`, `ItemSet`, `MirAction` (implicitly numbered, but every v3 `.Lib` frame table stores it by byte value — `Client/MirGraphics/MLibrary.cs`, `LibraryEditor/Graphics/MLibraryV2.cs` — so append only, never insert or reorder), ... Add members with an explicit unused value: at the end for sequential enums (`ItemType`, `BuffType`, `ItemSet`, `ObjectType`), or an unused value in the right band for banded enums (`Spell` by class, `Stat` < 255). Check `[Flags]` (`PoisonType`, `BindMode`, `SpecialItemMode`, `RequiredClass`, `RequiredGender`) before validating with `Enum.IsDefined`. `BindMode : short` has no free bit (`NoHero = -32768`) — a new bind flag needs a format change (§4.3).
- **NEVER** change a `Shared/Data` model's `Save`/ctor without treating it as **both** a DB-format and a wire-format change (`GameShopItem` has a second packet ctor/`Save(writer, packet)` — update both).
- **NEVER** add fields to `ConquestGuildInfo` (`.mcd`) without first adding a version header; **NEVER** append a section to `SaveDB` after `GTMapList` without first making `LoadDB` read or skip the GTMap block.
- **NEVER** delete an `ItemInfo`/`QuestInfo`/`MagicInfo` row casually — unbindable player items/quests/magic vanish at the next load and are gone after the next save.
- **NEVER** rename or delete a `MonsterInfo`/`ItemInfo` row whose name a Setting references (`SkeletonName`, `ShinsuName`, `BugBatName`, `Zuma1-7`, `Turtle1-5`, `BoneMonster1-4`, `HellKnight1-4`, `CloneName`, `FishingMonster`, `ScrollMob1-4`, ..., `RefineOreName`; full list in `Envir.CanStartEnvir`), and never remove the last StartPoint: with `[General] EnforceDBChecks=True` (default) the world refuses to start.
- **NEVER** call `Envir.Main.SaveDB()` after an editor save (`SystemDataStale`); **NEVER** call `Envir.Edit.SaveDB()` directly from web code (use `GameDataSave.EnsureSavable()` + `SaveEdit()`/`RunFileIo`); **NEVER** add a second unsynchronised writer of `Server.MirDB` (any open handle during Main's periodic `SaveDB` throws → world stop).
- **NEVER** hand-edit or replace `Server.MirDB`/`Server.MirADB` while the world runs; restores need the world fully stopped.
- **NEVER** copy object references from `Envir.Edit` into `Envir.Main` (e.g. an Edit `ItemInfo` into a live `UserItem.Info`); bind by index against Main.
- **NEVER** change `Server/Utils/Crypto.cs` (PBKDF2-SHA1, 50 iterations, 24/24 bytes, UTF8 string) — every stored password and web sign-in depends on it.

#### ALWAYS
- **ALWAYS** keep `Save` write order and ctor read order identical; append new fields at the end.
- **ALWAYS** pass the version of the file being read when constructing shared models from disk: the enclosing ctor's `version, customVersion` where it has them (`CharacterInfo`, `HeroInfo`, `GuildInfo`, `MailInfo`, `AuctionInfo`, NPC goods), otherwise `Envir.LoadVersion, Envir.LoadCustomVersion` (`AccountInfo`, `MapInfo`, `MonsterInfo`, ...). **NEVER** omit them (defaults `int.MaxValue` = wire layout); read `LoadVersion` only inside a load under `LoadLock`.
- **ALWAYS** load system data before user data; new bind steps go after the lists they read.
- **ALWAYS** mutate user data on the game thread; it persists at the next `BeginSaveAccounts` (≤ `SaveDelay` min). Set `GuildInfo.NeedSave`, `ConquestGuildInfo.NeedSave`, `NPCObject.NeedSave` after mutating those; set `GuildRefreshNeeded = true` after removing a guild.
- **ALWAYS** mutate guilds through the live `GuildObject` (its `.Info` is the persisted `GuildInfo`); never construct a second `GuildObject` for an existing index.
- **ALWAYS** call `WebAdminPending.Add(area, description, adminName)` after web writes that Main only reloads at world start.

#### Known debt — existing breaches
- ~~`SaveDB` truncates in place; `BeginSaveAccounts` leaves a window with no `Server.MirADB`~~ — **fixed 2026-10-10 (roadmap P2)**: both write a temp file (`Server.MirDBn` / `Server.MirADBn`) and swap it in with `File.Replace` (the previous file becomes the `Back Up\` copy); accounts are serialised to memory before any file is touched; `LoadDB`/`LoadAccounts` refuse to create an empty DB when backups or temp files exist, `LoadAccounts` refuses versions outside `MinVersion..Version`, and `StartEnvir` throws when Main's `LoadDB` fails. Still open: backups never pruned.
- `Conquests\*.mcd` has no header; GTMap block written but not read. Guild/goods/conquest async saves still use the n/o `File.Move` pattern and swallow errors (roadmap P5).
- `Envir.BindGameShop` binds Main's `GameShopItem.Info` to `Envir.Edit.ItemInfoList` objects (cross-instance references; requires Edit loaded first).
- `UserItem.Slots` writes `Slots[i] == null` and reads `if (reader.ReadBoolean()) continue;` (inverted vs every other nullable) — consistent, don't "fix" one side.

### 3.5 Packets & Connections

#### NEVER
- **NEVER** add a packet type without a `case` in `Shared/Packet.cs` `GetClientPacket`/`GetServerPacket` — a missing case silently drops that packet **and everything buffered after it**.
- **NEVER** insert packet IDs mid-enum — append at the end of `ClientPacketIds`/`ServerPacketIds`.
- **NEVER** let a packet exceed 65 535 bytes (the `ushort` length wraps silently). For large lists use `Compressed => true` (only `S.NPCGoods` does today) or split.
- **NEVER** let `ReadPacket` over-read: a field-order mismatch throws → the player's IP is blocked for 24 h. (Under-reading is silently ignored.)
- **NEVER** copy the compressed-read slice `rawBytes[4..(length - 4)]` from `Packet.ReceivePacket`.

#### ALWAYS
- **ALWAYS** keep `ReadPacket`/`WritePacket` field order and types identical.
- **ALWAYS** mark private server packets `public override bool Observable => false;` (passwords, storage, mail, inspect of self).
- **ALWAYS** call `CheckItem(item)` / `CheckItemInfo(info)` (and the monster/NPC/quest equivalents) before sending a packet that references a `UserItem`/info the client may not have.
- **ALWAYS** add packets a hero must forward to the `HeroObject.Enqueue` whitelist, and observer-visible broadcasts to `PlayerObject.BroadcastObservePackets`.
- **ALWAYS** rebuild and deploy client and server from the same commit after any `Shared/` change.
- **ALWAYS** remember the rate rules when testing with real clients: 5 s IP block per connect, `MaxIP` per IP, `MaxPacket` reads per 5 s → 24 h block.

### 3.6 Game Objects & NPC Scripts

#### NEVER
- **NEVER** rely on two due `DelayedAction`s firing in the same tick (`MapObject.Process` / `Map.Process` do `RemoveAt(i)` without `i--`, so the next due action waits a tick). Don't "fix" it casually.
- **NEVER** call `Spawned()` twice or `Despawn()` on a despawned object; **NEVER** despawn without `CurrentMap.RemoveObject(this)` first (Despawn doesn't touch cells); **NEVER** despawn a corpse inside `Die()` (`Drop()` needs `CurrentMap`).
- **NEVER** reuse or renumber monster AI numbers (`MonsterInfo.AI` is a byte; used 1–203, 210–223, 255, and 252 is special-cased in `MonsterObject.DeadDelay`; **free 204–209, 224–251, 253–254**). AI numbers are checked across server and client (`AI ==`, `AI !=`, `switch (Info.AI)` cases).
- **NEVER** put an unknown `#DIRECTIVE` in an NPC script (throws at parse), **NEVER** `#INCLUDE` a missing file (blanks the whole script), and **NEVER** assume a malformed comparison operator fails (it logs `IncorrectOperatorPage` and the segment reports success **without running either branch** — no `#ACT`/`#SAY`, no `#ELSEACT`/`#ELSESAY`; remaining checks skipped).

#### ALWAYS
- **ALWAYS** re-validate targets in delayed completions: `target != null && target.Node != null && target.CurrentMap == CurrentMap && target.IsAttackTarget(this)` (+ range for projectiles). `Node == null` means despawned.
- **ALWAYS** match `DelayedAction` param order to the consumer: object `ActionList` Magic params start with the `UserMagic`; **`Map.ActionList` Magic params start with the caster**.
- **ALWAYS** fold a new monster timer field into `SetOperateTime()` (or set `OperateTime = 0`) — otherwise it fires up to 2 s late.
- **ALWAYS** register a new `BuffType` in `BuffInfo.Load()`; add a new `Spell` to `Envir.FillMagicInfoList()` and a case in `HumanObject.Magic`.
- **ALWAYS** start subclass `GetInfo()` overrides from `(S.ObjectMonster)base.GetInfo()`.
- **ALWAYS** bounds-check cell scans (`GetCell` has no bounds check; use `ValidPoint` or the `if (y < 0) continue; if (y >= Height) break;` idiom).
- **ALWAYS** make NPC script pages reachable from `[@MAIN]` buttons/GOTOs (unreachable pages are never parsed), and add new jump keywords to the GOTO detection in `NPCScript.ParseSegment`.
- **ALWAYS** remember gold is per **account** (`player.Account.Gold`), and monster drops are owner-protected for `Settings.Minute`.

### 3.7 Web Admin (backend + frontend)

#### NEVER
- **NEVER** bind beyond loopback without a TLS reverse proxy (plain HTTP, cookies not Secure, no `X-Forwarded-For`).
- **NEVER** serialise raw Crystal objects with `WebAdminJson` (cycles: `CharacterInfo.AccountInfo`, `AccountInfo.Connection`) — project DTOs. **NEVER** return exception text to clients.
- **NEVER** put passwords or hashes in audit `details`.
- **NEVER** register a route shape another module owns (throws, drops the module); keep to your own prefix.
- **NEVER** mutate from the browser without `portalApi`/`OpsUI.post` (adds `X-MirCraft-CSRF`); **NEVER** call it with a relative `api/...` URL (the header is added only when the URL starts with `/api/` → 403).
- **NEVER** set unescaped `innerHTML` (use `OpsUI.esc`/`esc`), store session tokens in `localStorage`, add new CDNs/npm/bundlers/TypeScript/ES modules, hard-code host:port, use sync XHR, or poll faster than the existing ~5 s.
- **NEVER** PUT grid rows without the follow-up `POST /api/admin/database/save`; **NEVER** send partial rows or strip/fabricate `__rev` (`noRev` only from import); **NEVER** treat a 200 PUT as "all saved" — inspect applied/conflicts/validation/ignored fields.

#### ALWAYS
- **ALWAYS** `WebAdminAudit.Record(req, "area.verb", target, details, reason)` on every successful mutation.
- **ALWAYS** hold `WebAdminGame.EditLock` for `Envir.Edit` access and `WebAdminOptions.SettingsLock` for Settings RMW / `Settings.Save*()`.
- **ALWAYS** keep hook subscribers O(1) and non-blocking (they run on the game thread inside `Envir.Login` / `PlayerObject.Chat`); subscribe inside `Register` behind a static once-guard (`Interlocked.Exchange(ref _hooked, 1)`).
- **ALWAYS** keep routes working while the world is stopped: answer 503 (`data.world`) or use `RunOrInlineAsync`.
- **ALWAYS** treat `[WebAdmin]` keys as process-restart only.
- Frontend: IIFE modules, `app.js` loaded last; `AdminSections.register(id, {load, show, hide})` + `AdminSections.poll`; `makeDbGridEditor` + `DbSave.persist` for grids, `ag-theme-quartz-dark`, `getRowId` = `Index`; `SearchableLookupCellEditor` for FKs; `portalConfirm` for destructive actions; `portalToast` for errors; honour `Retry-After` on 429/503; `localStorage` keys prefixed `mircraft_`; ISO dates (never `/Date(ms)/`).

#### Known debt — existing breaches
- `GameDataRoutes.ApplyLiveMonsters` returns `"Live apply failed: " + ex.Message` (~L329).
- `WebAdminAuth.VerifyPassword` compares with `string.CompareOrdinal` (not constant-time; rate limits mitigate).

### 3.8 Known defects (found 2026-10-08, not fixed)
- **`C.PurchaseGuildTerritory`** has a class, a client sender and a server handler (`MirConnection.PurchaseGuildTerritory`) but **no `GetClientPacket` case** → never received, and the rest of that read buffer is dropped.
- **`S.RefineCancel`** has a class and a client handler but no `GetServerPacket` case and no server sender — dead.
- **`S.StorageUnlockResult` / `S.StoragePasswordResult`** are private but `Observable` (default true) → mirrored to spectators.
- **`Settings.Save()` never writes** `[General] GMPassword`, `[Network] MaxPacket`, `[Game] MonsterRecallEnabled/Range/Cooldown` (`Load` reads them) — a change made from WinForms or in code is lost at the next restart. The web admin Config page persists these keys itself (`ConfigRoutes.WriteIni`, `SavedBySettingsSave=false`); otherwise edit `Setup.ini` with the server stopped. `[Game] FishMonster` in Setup.ini is ineffective (Fishing settings own `FishingMonster`; see `Server/WebAdmin/SettingsCatalog.cs`).
- ~~Mob-thread exception → world hang; duplicate ObjectIDs~~ — **fixed 2026-10-10 (roadmap P5)**: per-object catch inside `ThreadLoop` (quarantine), a dead thread is skipped by the barrier (`MobThread.Dead`), `Envir.ObjectID` uses `Interlocked.Increment`.
- **Drop lines ignore the numerator**: `2/10` behaves as `1/10` (`parts[0].Substring(2)` in `DropInfo`). First failed `DropItem` aborts the remaining drops.
- ~~Zero-character accounts dropped at load; stale `Envir.Guilds` after an in-process restart~~ — **fixed 2026-10-10 (roadmap P5)**: accounts are kept (decision #14); `LoadGuilds` clears `Envir.Guilds` first.
- ~~Disconnect/receive race → 24 h IP block~~ — **fixed 2026-10-10 (roadmap P5)**: `ReceiveData` works on locals and returns quietly once `Disconnect` has nulled the queues.
- Status port **3100** is hard-coded (not in Setup.ini).

---

## 4. CODE STYLE & PATTERNS

### 4.1 .NET 10 constraints & conventions
- `net10.0` (Library/tests), `net8.0` (Shared, consumed by the net8 client too) and `net10.0-windows7.0` (host), set centrally in `Directory.Build.props`, `LangVersion` pinned to 12; AnyCPU, SDK-style, `Nullable=disable`, `ImplicitUsings=enable`. No DI container: singletons are `Envir.Main`, `Envir.Edit`, `Settings`, `MessageQueue.Instance`, `WebAdminHost`, `WebAdminGame`, `WebAdminSessions.Store`. Don't add `Microsoft.Extensions.*`, ILogger, Serilog/NLog.
- Most classes expose `protected static Envir Envir => Envir.Main` and `MessageQueue => MessageQueue.Instance`.
- Naming: PascalCase types/methods/public fields; match the surrounding file for private/protected fields (Crystal mixes `master`, `_target`, `brownTime`). Comment *why* (thread boundary, timing, wire/DB compatibility), not what. Keep UTF-8 BOM and line endings as found.
- New user-facing server text: `ServerTextKeys` member + English default in `GameLanguage.ServerTextMap` (`Shared/Language.cs`); add the key to `Server.MirForms/Localization/English.json` and a translation to `Chinese.json` (copied to output always; a run-folder JSON whose key count differs is rewritten at start, and missing keys fall back to English); log with `GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.X, args)`. Web admin uses literal `[WebAdmin]` text.

### 4.2 Packet handler pattern & add-a-packet recipe

```csharp
// Shared/ClientPackets.cs
public sealed class MyRequest : Packet
{
    public override short Index { get { return (short)ClientPacketIds.MyRequest; } }
    public int Slot;
    protected override void ReadPacket(BinaryReader reader) { Slot = reader.ReadInt32(); }
    protected override void WritePacket(BinaryWriter writer) { writer.Write(Slot); }
}

// Server/MirNetwork/MirConnection.cs  — ProcessPacket switch
case (short)ClientPacketIds.MyRequest: MyRequest((C.MyRequest)p); break;

private void MyRequest(C.MyRequest p)
{
    if (Stage != GameStage.Game) return;
    Player.MyRequest(p.Slot);          // validate p.Slot inside; reply with Enqueue(new S....)
}
```

1. Append the ID at the **end** of `ClientPacketIds`/`ServerPacketIds` (`Shared/Enums.cs`).
2. Packet class in `Shared/ClientPackets.cs` / `Shared/ServerPackets.cs` (lists: write `Count` then items; nullables: `writer.Write(x != null); x?.Save(writer)`). Private S packets: `Observable => false`.
3. **Factory case** in `Shared/Packet.cs` `GetClientPacket` / `GetServerPacket`.
4. Server: `ProcessPacket` case + private handler (C→S); logic on `PlayerObject`/`HumanObject`. Client: the scene's `ProcessPacket` (`GameScene` in-game) — see `Knowledgebase/Client/client.md`.
5. Hero forwarding → `HeroObject.Enqueue` whitelist; spectator broadcast → `BroadcastObservePackets`.
6. Build `Server/Server.Library.csproj` **and** `Client/Client.csproj` (both consume Shared); ship together.

### 4.3 Versioned DB field recipe (canonical: commit 0c669acc, `MonsterInfo.IsBoss`, 115→116)

**Decided (user, 2026-10-08): every new saved field bumps `Envir.Version`.** This fork does not pull upstream Crystal updates, so there is no version-number clash to avoid. Leave `CustomVersion` at 0 and never gate on `LoadCustomVersion`.

```csharp
// Server/MirEnvir/Envir.cs
public const int Version = 119;                       // bump ONCE per change set (was 118)
// Server/MirDatabase/XxxInfo.cs — ctor, appended at the end of the existing reads
if (Envir.LoadVersion >= 119) NewField = reader.ReadInt32();   // or: if (version >= 119) when the ctor takes a version
// XxxInfo.Save — appended at the end, unconditionally
writer.Write(NewField);
```
- New collection: list on `Envir`, write in `SaveDB` / `SaveAccounts(Stream)`, read in `LoadDB`/`LoadAccounts` under the same version gate; old files get an empty list. In `SaveDB`, anything appended after `GTMapList` needs `LoadDB` to read/skip the GTMap block first (§3.4).
- Shared model field → also a wire change (client reads with `int.MaxValue`).
- Bumping `Version` means older server binaries refuse the DB (`LoadDB` rejects `LoadVersion > Version`), so take a backup before the first run of a new version.
- A new public field on `MonsterInfo`/`ItemInfo`/... appears automatically in the web admin Game Data (reflection, `Server/WebAdmin/GameData/GameDataRegistry.cs`); exclude it there if it must not be edited.

### 4.4 MapObject / combat / DelayedAction pattern

```csharp
// Monster attack (MonsterObject.Attack style)
Direction = Functions.DirectionFromPoint(CurrentLocation, Target.CurrentLocation);
Broadcast(new S.ObjectAttack { ObjectID = ObjectID, Direction = Direction, Location = CurrentLocation });
ActionTime = Envir.Time + 300;
AttackTime = Envir.Time + AttackSpeed;
int damage = GetAttackPower(Stats[Stat.MinDC], Stats[Stat.MaxDC]);
if (damage == 0) return;
ActionList.Add(new DelayedAction(DelayedType.Damage, Envir.Time + 300, Target, damage, DefenceType.ACAgility));

// Completion — re-validate everything, the world changed in between
MapObject target = (MapObject)data[0];
int dmg = (int)data[1];
DefenceType defence = (DefenceType)data[2];
if (target == null || !target.IsAttackTarget(this) || target.CurrentMap != CurrentMap || target.Node == null) return;
target.Attacked(this, dmg, defence);
```
- Pace with `ActionTime`, `MoveTime`, `AttackTime`, `SpellTime`, `SearchTime`, `RoamTime`, `RegenTime`; never spin.
- Map moves: `GetCell(old).Remove(this); CurrentLocation = loc; GetCell(loc).Add(this);` + `RemoveObjects/AddObjects(dir, n)` (see `MonsterObject.Pushed`).
- AoE: scan `CurrentMap.GetCell(x, y).Objects` with bounds checks and a `switch (ob.Race)` filter; never `Envir.GetObjects`.
- Idle monsters: `Alone`/`CheckAlone()`; `Settings.MonsterProcessWhenAlone` (default false) skips AI when no player is within `DataRange*2`.

### 4.5 Add a monster AI
1. `Server/MirObjects/Monsters/NewMob.cs`: `public class NewMob : MonsterObject` (or a closer base), `protected internal NewMob(MonsterInfo info) : base(info) { }`; override `InAttackRange`, `Attack`, `ProcessTarget`, optionally `CompleteAttack`/`CompleteRangeAttack` (re-validate), `Die`, `GetInfo` (from `base.GetInfo()`); fold new timers into `SetOperateTime`. Reuse the helpers at the bottom of `MonsterObject.cs` (`LineAttack`, `HalfmoonAttack`, `FullmoonAttack`, `ProjectileAttack`, `PoisonTarget`, ...) — the root `MonsterAttackMethods.txt` has stale signatures.
2. `MonsterObject.GetMonster` switch: `case <free 204–209 / 224–251 / 253–254>: return new NewMob(info);` (252 is special-cased in `MonsterObject.DeadDelay`) — grep `AI ==`, `AI !=` and `switch (Info.AI)` / `case <n>:` in `Server/` and `Client/` first.
3. Data: set `MonsterInfo.AI` in the editor (WinForms `MonsterInfoForm` or web Game Data); drop file `Envir\Drops\<Name>.txt`.
4. Monster-thread rules apply (§3.2): no locks, no global/cross-map mutation, `Envir.Random`.
5. Optional hooks: `[@_SPAWN(idx)]`, `[@_DIE(idx)]` in the monster NPC script. Client animation/effects: `Knowledgebase/Client/client.md`.

### 4.6 Add an NPC check / action (and script traps)
1. Append to `CheckType` (`Server/MirObjects/NPC/NPCChecks.cs`) or `ActionType` (`NPCActions.cs`) — not persisted, append anyway.
2. Parse in `NPCSegment.ParseCheck` / `ParseAct`: `case "MYCHECK": if (parts.Length < 2) return; CheckList.Add(new NPCChecks(CheckType.MyCheck, parts[1])); break;`.
3. Evaluate in `NPCSegment.Check(PlayerObject)` / `Act(acts, PlayerObject)` (params are already variable-substituted); set `failed = true` on bad input; also add to `Check(MonsterObject)`/`Check()` and the monster/robot `Act` overloads if usable there.
4. Page-jumping actions → GOTO detection in `NPCScript.ParseSegment`.
- Engine facts: scripts `Envir\NPCs\<FileName>.txt`; specials `00Default` (`DefaultNPC`, hooks `[@_LOGIN]`, `[@_LEVELUP]`, `[@_USEITEM(n)]`, `[@_MAPCOORD(...)]`, `[@_CUSTOMCOMMAND(x)]`, ...), monster script (`MonsterNPC`), `00Robot` (`[@_TIME(...)]`). `#INSERT`/`#INCLUDE` preprocess; segments split at each `#IF` with `#SAY/#ACT/#ELSESAY/#ELSEACT`. `NPCScript.Call` rejects keys that aren't buttons of the player's current page (anti-spoof). A failed action usually `return`s from `Act`, skipping the rest of that list. Reload: `Envir.ReloadNPCs()` — on the game thread (web admin marshals it; the WinForms menu doesn't).

### 4.7 Add a spell / buff
1. `Shared/Enums.cs` `Spell : byte` — new value in a free slot.
2. `Envir.FillMagicInfoList()`: `if (!MagicExists(Spell.New)) MagicInfoList.Add(new MagicInfo { Name, Spell, Icon, Level1-3, Need1-3, BaseCost, LevelCost, DelayBase, PowerBase, ..., Range });`.
3. `HumanObject.Magic` switch (~L3499): `case Spell.New: ...` → helper validates and queues `DelayedAction(DelayedType.Magic, ...)` on `ActionList` (target spells) or `CurrentMap.ActionList` (ground; params start with `this`). Default is `cast = false`.
4. Completion: `HumanObject.CompleteMagic` (~L5903) or `Map.CompleteMagic` (Map.cs ~L857); persistent ground effects → `SpellObject` cases. Experience: `LevelMagic(magic)`.
5. Buff: `BuffType` + `BuffInfo.Load()` entry. Heroes: `Server/MirObjects/Hero/<Class>Hero.cs` `ProcessFriend/ProcessAttack`.
6. Client: targeting/effects/tooltips/localisation — `Knowledgebase/Client/client.md`.

### 4.8 Items, stats, drops
- Zero-code item: `ItemInfo.Type = Script`, `Shape = N` → `[@_USEITEM(N)]` page in `Envir\NPCs\00Default.txt`.
- Code item: `ItemType : byte` (append) → `PlayerObject.UseItem` (`switch (item.Info.Type)` then `Shape`) and `HeroObject.UseItem` for heroes (`HumanObject.UseItem` is an empty virtual).
- New stat: `Stat` enum (`Shared/Data/Stat.cs`, byte, append in a gap < 255) + consumer in the `HumanObject.RefreshStats` chain; client tooltip.
- Drop file syntax: `1/<N> <ItemName>[ Q]`, `1/<N> Gold <amount>`, `1/<N> GROUP[*|^]` + `{ ... }`, `#INSERT`. Chance = `Chance / Settings.DropRate`, reduced by `Stat.ItemDropRatePercent` and rarity. `Envir.ReloadDrops()` on the game thread.

### 4.9 Periodic system / map feature
- Periodic: add an `xTime` field + a time-gated block in `Envir.Process()`, **wrapped in its own try/catch** (new obligation — no isolation exists). Per-map timed work: `Map.ActionList` or `Map.Process`. Persist via versioned `SaveDB`/`LoadDB` or an own file using the guild-style pattern (serialise on the game thread, async `BeginWrite` to a `*n` temp, rename in the completion), with a sentinel + `Version` header (unlike `.mcd`) and failures **logged** in the completion via `MessageQueue.Enqueue(ex)` — the existing `End*Async` swallows are Known debt.
- Map flag: `MapInfo` field + versioned read/write + `MapInfoForm` + consumer (`CurrentMap.Info.NoPets/NoHero/NoDropMonster/...`).

### 4.10 Marshalling and web admin routes

```csharp
public sealed class WidgetRoutes : IWebAdminRoutes          // Server/WebAdmin/Routes/WidgetRoutes.cs
{
    public void Register(WebAdminRouter router)
    {
        router.Get("/api/admin/widgets", List);                   // RouteAuth.Admin by default
        router.Post("/api/admin/widgets/{index:int}/frob", Frob); // CSRF enforced (mutating)
    }

    private static async Task Frob(WebAdminRequest req)
    {
        var body = await req.ReadJsonAsync().ConfigureAwait(false);       // parse on the HTTP thread
        if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");
        var name = body.GetString("name");
        var dto = await WebAdminGame.RunAsync(() =>
        {
            var p = Envir.Main.GetPlayer(name);                           // game thread: live state
            return p == null ? null : new { p.Name, p.Level };            // snapshot, never the live object
        }).ConfigureAwait(false);
        if (dto == null) throw new WebAdminException(404, "Player not online");
        WebAdminAudit.Record(req, "widget.frob", name, dto);
        await req.Ok(dto, "Done.").ConfigureAwait(false);
    }
    // List(...) omitted
}
```
- Responses: `req.Ok(data, message)` → `{ok:true,message,data}`; `req.Error(status, msg, data)`; throw `WebAdminException(status, msg, data)`. Codes: 400, 401, 403 (not admin/CSRF), 404, 405, 409 (conflict / wrong world state / transition), 413, 415, 422, 429, 503 (world not running), 504 (game-thread timeout), 500.
- Non-web code that must hand work to the game thread calls `Envir.Main.QueueAdminAction(() => ..., ex => /* dropped */)` and checks the bool.
- Add a section: `index.html` nav button + `<section id="section-x" class="admin-section hidden">` + css + script before `app.js`; `SECTIONS` entry in `admin-shell.js`; `AdminSections.register`; `portalRegisterDeepLink` (README §5.3).

### 4.11 WinForms editor pattern
Forms expose `public Envir Envir => SMain.EditEnvir;`, mutate the Edit lists on control events (`if (ActiveControl != sender) return;`), and call `Envir.SaveDB()` in `FormClosed` (sets `SystemDataStale`). That save is **Known debt** (unsynchronised `Server.MirDB` writer, no `LoadLock`/`EditLock`; `GameDataSave` is `internal` to Server.Library): do not copy it. New editors go in the web admin (§4.10). When changing an existing form, add no new `SaveDB()` call sites or save triggers; the only call a form may make into `Envir.Main` is `Envir.Main.QueueAdminAction(...)` — never touch Main's lists or objects directly. Editors are modal (`ShowDialog`); `InterfaceTimer` is a WinForms timer and keeps ticking under the modal loop. A web-admin shutdown is a `BeginInvoke(mainForm.Close)` on the UI thread, so its behaviour while a dialog is open is untested.

---

## 5. DEPLOYMENT & TELEMETRY

### 5.1 Current Observability

| Mechanism | Location | Data |
|---|---|---|
| `MessageQueue.Instance` | `Server/MessageQueue.cs` → log4net + UI queues (≤100 pending, extras dropped from UI only) + `WebAdminLogTap` | Operator events |
| log4net files | `Server.MirForms/log4net.config` (cwd-relative) | `Logs\Server\Server (dd-MM-yyyy).log`, `Logs\Chat\`, `Logs\Debug\`, `Logs\Player\`, `Logs\Spawn\` |
| Runtime counters | `Envir` statics; `Envir.Main` | statics: `LastCount` (objects iterated per second by the main-thread pass; `LastRealCount` is never incremented → always 0), `LastRunTime` (full object pass ms), `MobThread.LastRunTime`; on `Envir.Main`: `MonsterCount`, `Players.Count`, `Connections.Count` |
| WinForms | `SMain` title/labels, log tabs | the counters above |
| Web admin | `Routes/StatusRoutes.cs`, `DashboardRoutes.cs`, `WebAdminStatusSampler.cs` (5 s), `WebAdminHostMetrics.cs` | Dashboard tiles, 1 h/6 h/24 h history, cycle delay, log viewer, audit |
| Ban diagnostics | `MirConnection._lastPackets` (`FixedSizedQueue`, 10) | Last packet types logged on a flood ban |

No `Errors.txt`, no loop profiler, no per-packet diagnostics (contrast WoOOLcraft).

### 5.2 Logging Rules

#### ALWAYS
- **ALWAYS** log via `MessageQueue.Instance`: `Enqueue(string)` → Server log, `Enqueue(Exception)` → Server log at Error, `EnqueueDebugging` → Debug log, `EnqueueChat` → Chat log. Player/Spawn audit trails use a **cached** `static readonly ILog` from `Logger.GetLogger(LogType.Player/Spawn)` (Known debt: `MirEnvir/Map.cs` ~L779 uncached Spawn logger; `Server.MirForms/Systems/GuildItemForm.cs` ~L172 logs through log4net directly). Web admin code uses `WebAdminLog.Info/Error` (`[WebAdmin]` prefix).
- **ALWAYS** prefix subsystem lines (`[WebAdmin]`, `[Guild]`, ...) or use a localized `ServerTextKeys` message.
- **ALWAYS** include identity (ObjectID, Name, account, IP) in error logs.
- **ALWAYS** rate-limit chatty logs — aggregate per second; the write is synchronous on the game thread.

#### NEVER
- **NEVER** log inside innermost combat/AI loops.
- **NEVER** use `Console.WriteLine` (invisible in the WinForms exe). Known debt: `MirDatabase/CharacterInfo.cs`, `PlayerObject.cs` (~L14565), `Utils/HttpServer.cs`, `Utils/HttpService.cs`.
- **NEVER** introduce Serilog/NLog/ILogger/OpenTelemetry without a dedicated migration; stay on log4net 3.3.0. If telemetry is ever added: aggregate on the game thread, flush off it, no PII in tags, config in `Configs\Setup.ini`, thin wrapper over `MessageQueue`.

### 5.3 Metrics to Instrument (new subsystems)
Expose tick cost (ms per `Process`, rolling max), queue depth, entity count vs cap, failure count — extend the `Envir` static counters and `StatusRoutes`/`WebAdminStatusSampler` before adding dependencies.

### 5.4 Build & Output
- **NEVER** run `dotnet build Server.MirForms\Server.csproj` without `-o <scratch dir>`: output goes to `Build\Server\Debug\`, the user's **live run folder** (real `Server.MirDB`/`Server.MirADB`, `Configs\`, `Envir\`, `Maps\`, `Logs\`). A build there also overwrites `log4net.config` and `Localization\*.json` (`CopyToOutputDirectory=Always`) and `wwwroot\` (PreserveNewest). **Never touch `Build\`.**
- **NEVER** build `Legend of Mir.sln` with the dotnet CLI, with or without `-o`: without it the sln writes `Build\Server\Debug\`, `Build\Client\`, `Build\Server Tools\` and `Components\Debug\`; with it the SDK warns NETSDK1194 and every project lands in one folder, overwriting each other's files. Build individual projects (root `agents.md` "Build & Output Rules").
- Safe compile checks:
  ```
  dotnet build C:\Mir2\Server\Server.Library.csproj
  dotnet build C:\Mir2\Server.MirForms\Server.csproj -o <scratchpad>\server-build
  dotnet build C:\Mir2\Client\Client.csproj -o <scratchpad>\client-build     (after Shared changes)
  ```
- Runtime: .NET 10 Desktop Runtime x64 (since roadmap P4; the prebuilt net472 `CustomFormControl.dll` and CLR2 VB PowerPacks load and render on it, smoke-tested 2026-10-10); files `Server.exe`, `Server.dll`, `Server.Library.dll`, `Shared.dll`, `log4net.dll`, `CustomFormControl.dll`, PowerPacks. `Components\CustomFormControl.dll` is the prebuilt copy Server references; rebuilding `Controls/FixedListViewControl` does not update it.
- Test servers run from a scratch folder that holds BOTH the built output (`-o <scratch>`) and a **copy** of the data (startup creates missing drop files; saves write `Back Up\`), with cwd = that same folder. The game resolves data relative to the cwd; the web admin resolves scripts, `admins.dat`, `Logs\WebAdmin`, the log viewer and `wwwroot` relative to the exe folder (`AppContext.BaseDirectory`). Never run an exe from `Build\` against scratch data, or the reverse.

### 5.5 Tests
```
dotnet test C:\Mir2\Server.WebAdmin.Tests\Server.WebAdmin.Tests.csproj
```
Safe (references Server.Library + Shared only, writes nothing to `Build\`). Tests that change statics (hooks, `LoginGate`, `WebAdminGame.World`, admins, pending, audit, Settings) go in `[Collection(WebAdminGlobalsCollection.Name)]` (`GameBridgeTests.cs`) and restore them; read `Settings` only via reflection metadata (its static ctor opens `Configs\Setup.ini`). `GameDataFrontendTests` needs the repo layout. There are no game-logic tests.

### 5.6 Deployment Checklist (agent-verifiable)
- [ ] Built with `-o <scratch>`; nothing under `Build\` modified.
- [ ] `Configs\Setup.ini` defaults unchanged unless required (remember `Settings.Save()` rewrites it at exit).
- [ ] DB format: any `Save`/ctor change has an `Envir.Version` bump + `LoadVersion` gate (§4.3); old `Server.MirDB`/`Server.MirADB` still load; backup taken before first run.
- [ ] Scratch test server: `[General] VersionPath` points at the deployed `Client.exe` (or `CheckVersion=False` for local testing only).
- [ ] Packets: ID appended, factory case present, read/write symmetric, `Observable` set; client and server from the same commit.
- [ ] No new blocking calls, locks, LINQ or allocations on the game thread or in monster AI; no exception paths escaping `Process`/handlers.
- [ ] Off-thread world access goes through `QueueAdminAction`/`WebAdminGame`; web mutations audited, CSRF-protected.
- [ ] Smoke run from a scratch copy: start, log in, fight, NPC click, disconnect, wait for a save; `Logs\Server\Server (dd-MM-yyyy).log` and `Logs\Debug\` clean; `LastRunTime` / dashboard cycle delay stable.
- [ ] `dotnet test Server.WebAdmin.Tests` green if `Server/WebAdmin`, `Envir` hooks or `wwwroot` changed.

---

## APPENDIX A — Quick Decision Tree

```
Where does this code run?
├── Game thread (WorkLoop, ProcessPacket, Process, Envir.Process, admin action)
│     → No async, no locks, no LINQ/allocations per tick, no blocking I/O, no throwing.
│       Mutate world state freely; log via MessageQueue.
├── Monster AI (MonsterObject + Monsters/*, Master == null)
│     → Everything above PLUS: may run on a mob thread concurrently. No locks, no global or
│       cross-map mutation, Envir.Random only, Interlocked for counters, never throw.
├── HTTP (web admin)
│     → Parse/validate here → WebAdminGame.RunAsync/RunOrInlineAsync (snapshot DTOs) → audit → JSON.
│       System data: Envir.Edit under EditLock → GameDataSave.SaveEdit → WebAdminPending.Add.
│       Settings: under WebAdminOptions.SettingsLock.
├── WinForms UI
│     → Envir.Edit only; live-world actions via Envir.Main.QueueAdminAction.
└── Shared/ change
      → Wire + maybe DB format: append IDs/enums, factory case, version gate, rebuild + ship both sides.
```

## APPENDIX B — Glossary

| Term | Meaning |
|---|---|
| **Game thread / WorkLoop** | `Envir._thread` running `Envir.WorkLoop` — owner of the world |
| **Mob thread** | `MobThreads[1..ThreadLimit-1]` running `ThreadLoop` (monster AI) when `Settings.Multithreaded`; `MobThreads[0]` runs on the game thread |
| **Envir.Main / Envir.Edit** | Live world / editor copy of system data (same class) |
| **Server.MirDB / Server.MirADB** | System data / user data (accounts, characters, heroes, auctions, mail) — hand-written binary, versioned by `Envir.Version` |
| **SystemDataStale** | Static flag set by `Edit.SaveDB()`; Main skips periodic `SaveDB` until restart |
| **OperateTime** | Earliest time an object needs processing; `SetOperateTime()` computes it |
| **Node** | `LinkedListNode` in `Envir.Objects`; `null` = despawned |
| **Stage** | `MirConnection.Stage` (`GameStage`) |
| **Observable / Observers** | Packet flag + spectator connections that receive mirrored packets |
| **DelayedAction** | Timed action on an object's or map's `ActionList` (`DelayedType`, untyped `object[]` params) |
| **APM** | `BeginReceive`/`BeginSend`/`BeginWrite` async callbacks |
| **MirCraft** | The fork's embedded web admin (`Server/WebAdmin`, `wwwroot/admin`, port 5680) |

## APPENDIX C — Related Docs

| Path | Content |
|---|---|
| `Knowledgebase/Client/client.md` | Client: scenes, `GameScene.ProcessPacket`, rendering, client side of packets/spells/monsters |
| `Server/WebAdmin/README.md` | MirCraft web admin — operator guide, feature tour, developer rules (authoritative) |
| `MonsterAttackMethods.txt`, `MonsterReferenceVideos.txt`, `SoundRules.txt` (repo root) | Upstream notes: attack helpers (stale signatures), reference videos, monster sound indices |
| `dotnet-upgrade.txt` | VS 2022 17.8 / .NET 8 SDK requirements |
| `agents.md` (repo root) | Knowledgebase index: cross-boundary rules, build & output rules, folder map |

---

*Last updated: 2026-10-08 (initial version, written from a code survey of branch FuckingAround). Keep this file current when you change the areas it covers.*
