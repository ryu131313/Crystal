# Crystal Mir2 Game Server — AI Agent Reference

> **Scope:** `Server/` (project `Server.Library` — `MirEnvir`, `MirObjects`, `MirDatabase`, `MirNetwork`, `Settings.cs`, `Logger.cs`, `MessageQueue.cs`, `Helpers`, `Utils`, the `WebAdmin` backend), `Server.MirForms/` (the WinForms `Server.exe` host, DB editor forms, the `wwwroot/` web-admin frontend), `Server.WebAdmin.Tests/`, and the server-side use of `Shared/`.
> **Not in scope:** `Client/` (see `Knowledgebase/Client/client.md`), LibraryEditor/Viewer, PatcherWebSite (AutoPatcherAdmin(V2) were retired 2026-10-20).
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
| **Server** (host) | `Server.MirForms/Server.csproj` | `WinExe`, `net10.0-windows7.0` (`$(ServerWinTfm)`), WinForms; refs Server.Library, Shared, `Components/CustomFormControl.dll`, `Components/Microsoft.VisualBasic.PowerPacks.Vs.dll` | **`BaseOutputPath ..\Build\Server\` → `Build\Server\Release\` = the user's LIVE run folder** (user builds Release, 2026-10-11; `Build\Server\Debug\` = older data copy) |
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
| **IOCP / thread pool** | `TcpListener.BeginAcceptTcpClient`, `MirConnection.BeginReceive` | Accept → `new MirConnection` + `lock (Connections) Connections.Add`; receive → decode → `ConcurrentQueue<Packet> _receiveList`; with `SendThreads` > 0 also send completions, which continue that connection's `PumpSends` |
| **Sender threads** (P9-09) "Mir2 sender N" | `SocketSender` on the first queued send, `[Optimisation] SendThreads` (default 1, 0-4, live; 0 = `BeginSend` on the game thread) | Socket sends only: drain the ready queue (`MirConnection.PumpSends` → `Socket.SendAsync`, one send in flight per connection, queued arrays leave together, ≤ 64 per send) and dispose closed connections' sockets once their sends finish (≤ 2 s). Touch only the connection's send fields (`_outbox`, counters, `_socket`, the `SocketAsyncEventArgs`), never game state; a failed send or a client > 4 MB behind sets `_sendFaulted`, which the game thread turns into `Disconnecting` in `Process` |
| **Status port** | `StartNetwork`, `[Network] StatusPort` (default 3100), off with `StatusPortEnabled=False` (since 2026-10-10) | `MirStatusConnection` (max 5), ASCII status line every 10 s |
| **Legacy HTTP** | `Server/Utils/HttpServer.cs` if `Settings.StartHTTPService` | `/newaccount` → `Envir.Main.HTTPNewAccount` mutates `AccountList`/`NextAccountID` from the HTTP thread (takes `AccountLock`, but the game thread reads `AccountList` without it — unsafe); `/broadcast` → `Envir.Main.Broadcast` **directly from the HTTP thread** (unsafe) |
| **Web admin** | `WebAdminHost.Start` (`HttpListener`, `Task.Run` accept loop, per-request tasks, `SemaphoreSlim` 32) | Admin REST; marshals world work via `WebAdminGame.RunAsync` → `Envir.QueueAdminAction` |
| **Async file IO** | `FileStream.BeginWrite` in `SaveGuilds`, `SaveGoods`, `SaveConquests` (and the legacy account save); a pool-thread work item (`WriteAccountsFile`) for the account save since P9-10 | `File.Replace` of the `*n` temp → final (`CompleteSideFileSave`, `EndSaveAccounts`, `WriteAccountsFile`); the account completion clears `Saving` |
| **Password workers** (roadmap P8) | `Task.Run` in `Envir.Login`, and in `NewAccount` / `ChangePassword` when a v2 hash is involved | Only the hash or verify, on copies (password, salt, stored hash); the rest continues on the game thread through `QueueAdminAction` (`CompleteLogin`, `CompleteNewAccount`, `CompleteChangePassword`). `MirConnection.LoginPending` allows one at a time per connection |
| **Backup retention** (roadmap P5) | `BackupRetention.RunInBackground()` after a save, only with `[Backup] RetentionEnabled` (default off) | Prunes `Back Up\` files only |
| Reboot / web start-stop | `Envir.Reboot()`; `ServerControlRoutes` `Task.Run` | `Stop(); Start();` off the game thread |
| Web admin timers/tasks | `System.Threading.Timer`: `WebAdminStatusSampler` (1 s timer: the 5 s work every fifth tick, the 1 s live ring only while a Dashboard Live lease is held), `WebAdminMaintenance` countdown (1 s → `KickNonAdmins`), `ServerControlRoutes` restart countdown (1 s); `Task.Run`: `WebAdminLoginLog.FlushLoop`, `WebAdminAdmins` persist | Thread-pool threads; world work must go through `WebAdminGame.Post/RunAsync` |
| Editor import | `ItemInfoFormNew` CSV import `await Task.Run` | Off the UI thread, on `Envir.Edit` data |

There is **no** Crystal save thread, log thread or editor thread (the sender threads above only issue socket sends) (contrast WoOOLcraft; the web admin has its own login-log flush task). log4net writes synchronously on the caller's thread (`MinimalLock` appenders open/close the file per write).

### 2.4 Tick Loop (`Envir.WorkLoop`, `Envir.cs` ~L1996–2231)

Setup: `Time = Stopwatch.ElapsedMilliseconds`; `saveTime = Time + Settings.SaveDelay * Minute`; create `MobThread`s; `StartEnvir()`; `CanStartEnvir()`; start mob threads; `StartNetwork()` (LoadAccounts → LoadGuilds → LoadConquests → LoadGTInfo → listeners); optional `HttpServer`; `OpenAdminQueue()`; `RaiseWorldStarted()`.

Each `while (Running)` iteration (since roadmap P6: an iteration that took < 1 ms ends with the idle park `_loopSignal.WaitOne(1)`, `[Optimisation] IdlePark`; mob threads do one pass per tick with `[Optimisation] MobThreadOnePass`, and the game thread's own mob list has a full-pass-or-10 ms rule; every phase is timed by `LoopProfiler`):

1. `Time = Stopwatch.ElapsedMilliseconds`; once/sec roll `LastCount`/`LastRealCount`.
2. At most once per ms (`conTime != Time`): `AdjustLights()`; **`lock (Connections)`** reverse loop `Connections[i].Process()` (every packet handler runs here, inside the lock); then `lock (StatusConnections)`.
3. If Multithreaded: wake parked mob threads (`EndTime = Time + 10`, `Monitor.PulseAll(_locker)`), then `ThreadLoop(MobThreads[0])` on this thread — returns only when every sub-thread has set `Stop`, is `Dead` or has `Exited` (**barrier**, `WaitForSubThreads`; unbounded while running, at most 2 s once `Running` is false). With the due scheduler only threads whose scheduler has work are woken, and thread 0 waits (`SpinWait`) instead of re-walking its list. `Envir.InMobPass` is true from arming to the end of the barrier; `EndMobPass()` then applies the deferred list links and runs the work monster code deferred with `RunOutsideMobPass` (§3.2). After the loop exits, WorkLoop waits for the mob threads again before `StopNetwork`/`StopEnvir`/`SaveAccounts`.
4. Objects. **Due scheduler (`[Optimisation] DueScheduler`, default on, read at world start; roadmap P9-05):** `RunDue(_mainScheduler, 20 ms)` drains wakes and pops only due objects (`Time > OperateTime`) → `Process(); SetOperateTime();` → re-scheduled at `max(OperateTime + 1, Time + 1)`. Mob threads do the same on their own `ObjectScheduler`. **Off (old walk):** walk `Envir.Objects` from the persisted cursor `current` for **≤ 20 ms**; process an object if `!Multithreaded || Race != Monster || Master != null` and `Time > OperateTime`.
5. Maps. **Due scheduler:** `RunMaps()` runs only maps whose `NextProcessTime` is due (respawn check every `[Optimisation] MapRespawnCheckMs`, default 1000, per-map random phase; lightning/lava; an open door's 5 s close; the earliest queued map action) or that `MapActionList.Add` woke. **ALWAYS** queue map actions with `map.ActionList.Add(...)` on the `MapActionList`-typed field (never through a `List<DelayedAction>` reference, which would skip the wake). **Off:** `MapList[i].Process()` for every map whose `ProcessDue` is true.
6. `DragonSystem?.Process()`.
7. `Envir.Process()` (~L2322): time-gated blocks — new day, guild wars (1 min), `Guilds[i].Process` (1 min), conquests (10 s), rented items (5 min), auctions (10 min), `RespawnTick` (10 s), `Robot.Process(RobotNPC)` (1 min), script `Timers` (1 s).
8. `DrainAdminActions()` — ≤ 64 actions or ~10 ms; each action in its own try/catch.
9. Save if `Time >= saveTime || _saveNowRequested`: `BeginSaveAccounts(); if (!SystemDataStale) SaveDB(); SaveGuilds(); SaveGoods(); SaveConquests();`.
10. Every 5 min broadcast online count; every `LineMessageTimer` min a random line message.

Shutdown tail: `CloseAdminQueue(); StopNetwork(); StopEnvir() [SaveGoods(true), GC.Collect()]; SaveAccounts(); SaveGuilds(true); SaveConquests(true);` — **no `SaveDB()` at shutdown**. Only `SaveAccounts()` is synchronous; the final guild/goods/conquest writes are async `BeginWrite`s that nothing awaits, so a fast process exit can leave `*n` temp files and the previous file in place (UNVERIFIED in practice).

**Time:** `Envir.Time` (long ms, set only by the loop) for timers; `Envir.Now` (`_startTime.AddMilliseconds(Time)`, UTC-based) for calendar. Constants `Settings.Second/Minute/Hour/Day` (ms). Timer idiom: `if (Envir.Time >= xTime) { xTime = Envir.Time + Settings.Minute; ... }`. Mob threads see `Time` frozen at the tick start.

**Scheduling:** objects are processed when `Time > OperateTime`. `HumanObject.SetOperateTime` runs players/heroes at most every `[Optimisation] HumanProcessMs` (10), never later than their earliest `ActionList` entry. `MonsterObject.SetOperateTime` (~L1232) = `Time + 2000` min'd over every timer field, `ActionList` time, poison tick and buff time.

**Due scheduler contract (P9-05, `Server/MirEnvir/ObjectScheduler.cs`):**
- `MapObject.OperateTime` is a property. A write that makes an object due earlier than its scheduled entry (`OperateTime = 0` from any thread) wakes its owner (`Envir.WakeObject` → the owner's `ConcurrentQueue`); it runs on the owner's next pass (~1 ms). Raising `OperateTime` needs no wake (the old entry pops early and is re-scheduled).
- **ALWAYS** express "process this object soon" by lowering its `OperateTime` (or folding the timer into `SetOperateTime`). A timer only checked inside `Process` and not reflected in `OperateTime` fires late — as it did with the old walk, which was gated on the same condition.
- Owner = `MobThreads[SpawnThread]` for `Race == Monster && Master == null` when Multithreaded (game thread if that mob thread died), else the game thread. A `Master` change takes effect when the object next falls due (the old owner forwards it).
- Heaps are owner-thread only; entries are lazy (`ScheduledDue` + `SpawnedGeneration` + `Node != null` decide liveness). `Envir.LastRunTime` ("cycle delay") and `MobThread.LastRunTime` are the worst lateness of a processed object in the last second, not walk time.

**Mob threading (`Settings.Multithreaded=true`, `ThreadLimit=2`, `[General]` in Setup.ini):** each `Map` gets `Thread = Envir.Random.Next(Settings.ThreadLimit)` (Map.cs ctor). `MapObject.Spawned()` adds a monster to `Envir.Objects` AND `MobThreads[CurrentMap.Thread].ObjectsList` (through `Envir.LinkObject`/`UnlinkObject`, which defer the list change to the barrier during a mob pass; `Node` is set at once). `MonsterObject.Teleport` to another map from a mob pass is deferred to the barrier, and a cross-map teleport then moves the monster to the new map's thread (`SyncSpawnThread`). Pets (`Master != null`), players, NPCs, items and spells always run on the game thread. `MobThreads` is sized from `Settings.ThreadLimit` when `Envir` is first touched.

### 2.5 Player Connections & Packets

| Component | Path | Role |
|---|---|---|
| Listener | `Envir.StartNetwork` (~L3532), `Envir.Connection` accept callback | `TcpListener` on `Settings.IPAddress`:`Settings.Port` (default 127.0.0.1:7000) |
| Connection | `Server/MirNetwork/MirConnection.cs` | `Stage`, `Account`, `Player`, `Observers`, `Observing`; APM receive; `_receiveList`/`_sendList`/`_retryList` |
| Protocol | `Shared/Packet.cs` | Frame `ushort totalLength | short Index | payload`; factories `GetClientPacket`/`GetServerPacket` (switches) |
| Packet classes | `Shared/ClientPackets.cs` (`C`), `Shared/ServerPackets.cs` (`S`) | `ReadPacket`/`WritePacket` |
| IDs | `Shared/Enums.cs` `ServerPacketIds : short` (~L1434, last `PlayerProfile` = 295, 2026-10-16), `ClientPacketIds : short` (~L1717, last `SetProfile` = 165, 2026-10-16) | **ID = declaration position** (no explicit values) |
| Dispatch | `MirConnection.ProcessPacket` big `switch (p.Index)` → private handler | No reflection, nothing auto-registers |

**Stages** (`enum GameStage { None, Login, Select, Game, Observer, Disconnected }`, server-only in `MirConnection.cs`): None →(`C.ClientVersion` OK)→ Login →(`Envir.Login`)→ Select →(`StartGame` → `PlayerObject.StartGame`)→ Game; `LogOut` → Select; `AddObserver` → Observer.

**Rules snapshot (2026-10-16, Scene Lab UI phase 2.2):** `S.ServerRules` (Observable, ~200 bytes, built by `Envir.ServerRulesPacket()` from the live `Settings.*` / `Awake.*` statics, copies of the arrays) goes out right after `S.LoginSuccess` in `Envir.Login` and again after `S.StartGame { Result = 4 }` in `PlayerObject.StartGameSuccess`, so a settings reload between log-ins reaches the client. Contents: refine, awakening (`AwakeMaterials` capped to the first `AwakeType.HPMP` entries: `Settings.LoadAwakeAttribute`/`SaveAwakeAttribute` append without clearing, Known defect left as is), mentor, marriage, mail, territory costs, `GemStatIndependent`, `AllowCreateAssassin/Archer`, and the feature switches `MapDetailsEnabled` (`[Game] MapDetailsEnabled`, default true; for phase 2.4 `S.MapDetails`), `FriendRequestsEnabled` (`Settings.FriendRequestsEnabled`, a `const false` until phase 2.7) and `MaxFriends` (`[Game] MaxFriends`, default 100, load clamps to >= 0; enforced from phase 2.7). Both new `[Game]` keys are in `SettingsCatalog` (Gameplay group). The desktop client ignores the packet (`MirScene.ProcessPacket` has no case).

**Detail packets (2026-10-16, Scene Lab UI phase 2.4)** — all game-thread, none hero-forwarded, the desktop ignores them (`MirScene.ProcessPacket` has no case):
- `S.PoisonStatus` (Observable): player only. `HumanObject.PoisonStatusDirty` is set where poisons are added / replaced (`ApplyPoison`) and removed or change stage (`ProcessPoison`, incl. `DelayedExplosion`); `PlayerObject.Process` flushes it after `base.Process()` when dirty **or** `PoisonList.Count` differs from the last count sent (catches clears outside the hooks: death, purification). Never per tick otherwise; one status after login. ms left = `HumanObject.PoisonStatusEntry` (estimate for `DelayedExplosion`), clamped >= 0.
- `S.GroupMemberStatus` (Observable): `HumanObject.GroupStatusDirty` (HP/MP via `SendHealthChanged`, `Level` setter, `AddBuff`, buff expiry in `ProcessBuffs`); `PlayerObject.Process` builds **one** packet and sends it to the other members when `GroupMembers != null && dirty && Envir.Time >= next`, then waits 1000 ms (<= 1/s per member). Join: `ExchangeGroupMemberStatus` sends both directions at once. Buffs capped at 20.
- `C.HeroSpellToggle` -> `HeroObject.SetAutoCast` on a spawned hero's known spell; reply `S.HeroAutoCast` (full disabled list, Observable false), also sent via `Owner.Enqueue` after `S.HeroInformation`. `HeroObject.CanUseMagic` requires `magic.Key > 0` **and** `magic.AutoCast` (v119 field). Player spells unaffected.
- `C.RequestMapDetails` -> `S.MapDetails` (`Compressed`): needs `[Game] MapDetailsEnabled`, the player's current map or a `MapInfo` with `BigMap > 0`, a 1 s per-player cooldown (armed on rejected requests too). Built once per `MapInfo` from `MapInfo.Respawns` / `SafeZones` into `Envir.MapDetailsCache` (shared packet parts; `Envir.InvalidateMapDetails()` after every LiveDb apply in `LiveDbEngine` and on a DB load, so map edits show at once), `Connection.CheckMonsterInfo` per referenced monster on every request, split into parts of <= 3000 respawns / 500 safe zones (`Part` / `PartCount`, part 0 replaces). Info disclosure: turn the setting off to hide spawns.
- `S.LoginFailed` (Observable false): sent right **after** `S.Login` (Result 3 no account, 4 wrong password), never instead (the desktop needs `S.Login`). `AccountTriesLeft` = `max(0, Envir.AccountWrongPasswordLimit (5) - WrongPasswordCount)` (-1 no account); `ConnectionTriesLeft` from `MirConnection` failed logins vs `MaxFailedLogins`; `LockedUntil` = `DateTime.ToBinary` of the 1-minute IP block when this failure closes the connection, else 0. An account lock still sends `S.LoginBanned` only.
- `S.SelectDetails` (Observable false, `Envir.SelectDetailsPacket(AccountInfo)`): account gold (uint) + per listed character (same order / filter as `GetSelectInfo`) guild name, map title, `PlayTime` seconds; after `S.LoginSuccess`, both `S.LogOutSuccess` paths and `S.NewCharacterSuccess`.

**Guild / social detail packets (2026-10-16, Scene Lab UI phase 2.5)** — game-thread handlers in `MirConnection` (Stage `Game`; `GetGuildRanking` also Observer) -> `PlayerObject` region "Phase 2.5"; cooldowns are player fields vs `Envir.Time`; nothing hero-forwarded; the desktop ignores the S→C packets:
- `C.RequestGuildMemberDetails` -> `S.GuildMemberDetails` (Observable false): guild + rank, silent 5 s cooldown; one pass over `CharacterList` for offline class / level (`GuildMember`/`GuildRank` are the `.mgd` layout: never extend them for UI data); parts of <= 800 rows (`Part` / `PartCount`, worst part ~49.6 KB).
- `S.GuildWarList` (Observable false, `GuildObject.SendGuildWarList` / `SendGuildWarListToAll`): full list at login (`PlayerLogged`), on join, in `GoToWar` and `GuildAtWar.EndWar` (both guilds; `EndWar` skips the war being ended because `Envir` removes it only after the call).
- Guild log: one `GuildInfo.AddLog(GuildLogKind.X, text)` per event (join, leave, kick, rank change, notice, buff bought / activated, war start / end, territory bought / sold, gold deposit / withdraw); the text is a localised `ServerTextKeys.GuildLog*` line formatted when it happens. `C.RequestGuildLog` -> `S.GuildLog` (Observable false, silent 5 s cooldown, <= 100 entries oldest first, text cut to 128 chars on the wire). Web admin guild edits and disbanding add no entries.
- `S.RankingsDetail` (Observable): right after `S.Rankings` in `Envir.GetRanking` for the same rows (guild, online, `ExpPerTenThousand` via `Envir.ExpPerTenThousand`).
- Guild board: `C.GetGuildRanking { Page }` -> `S.GuildRankings` (Observable, page 20, clamped). `Envir.BuildGuildRanking` sorts `GuildList` by level then EXP (newbie guild excluded) into a cache **rebuilt on request at most every 5 minutes** — no per-tick work; the board can lag guild changes by up to 5 minutes.
- `S.MentorDetails` (Observable false) after `S.MentorUpdate` in `GetMentor` while `Info.Mentor != 0`, and after the partner's offline `S.MentorUpdate` in `LogoutMentor`. `C.FindMentor` -> `S.MentorCandidates` (<= 20, 3 s cooldown with `MentorSearchCooldown`; the asker must have no mentor and no cooldown, else `MentorSearchNotEligible`).
- **Territory actions share one code path:** the NPC commands `BuyGT` / `ExtendGT` / `TeleportGT` (`NPCSegment` ~L3102) and `GTSale` / `GTCancelSale` (~L4386) are now `if (!player.GuildTerritoryX()) return; break;` over `PlayerObject.GuildTerritoryRent(GTMap target = null)` / `GuildTerritoryExtend` / `GuildTerritoryTeleport` / `GuildTerritoryListForSale(int? price)` / `GuildTerritoryCancelSale` (each returns `bool`; NPC behaviour unchanged, incl. the `Leader`/`Leader2` quirk). **Change a territory check there, once.** `C.GuildTerritoryAction { Kind, TerritoryIndex, Price }` calls the same methods; the packet path also needs alive + guild + rank, and Rent / Extend / Teleport need the `[@GUILDTERRITORY]` NPC page in range (no teleport-from-anywhere); ListForSale / CancelSale need `CurrentMap.Info.GT` (`MapInfo.GTIndex` defaults to 0, so the script's index check alone passes anywhere for territory 0's owner); `Kind` > `Teleport` is dropped; replies are chat lines only (the web client re-requests its page). Guild gold log entries are written only for amount > 0.
- `C.SetProfile` (10 s cooldown, `ProfileCooldown` / `ProfileUpdated`): cut to `MaxChatLength`, control chars removed, trimmed, capped at `CharacterInfo.ProfileTextMaxLength` (120); ignored while chat-banned. The server has **no chat word filter** (filtering is client-side). `S.PlayerProfile` (Observable) is always sent right after `S.PlayerInspect` in `Envir.Inspect` (empty text clears a stale one).

**Parcels in safe zones (2026-10-18):** a parcel that is not auto-sent (`MailInfo.Collected = false`, `[AutoSend]` off) no longer needs a post office NPC: `PlayerObject.ReleaseParcelsInSafeZone` marks every waiting parcel `Collected` while the player stands in a safe zone. It runs at the top of `GetMail` (so new mail arriving in a safe zone is ready at once) and from the `OnSafeZoneChanged` override (sets `_parcelsReleased`; `Process` sends the refreshed list next tick). `[@COLLECTPARCEL]` still works. No DB or wire change; `MailCollectFromPostOffice` text now names both ways.

**Market / mail detail packets (2026-10-16, Scene Lab UI phase 2.6)** — game-thread handlers in `MirConnection` (Stage `Game`) -> `PlayerObject` region "Phase 2.6 detail packets"; cooldowns are player fields vs `Envir.Time`; nothing hero-forwarded:
- **Gameshop purchases share one code path:** `GameshopBuy` is now `GameshopPurchase(GIndex, Quantity, PType, null)`; `C.GameshopGift` -> `GameshopGift` checks recipient (exists, not deleted, not self, mutual unblocked friends via `IsFriendOf`, mailbox not full) **before** charging, then calls `GameshopPurchase` with the recipient. **Change a purchase check there, once.** The gift mail carries the buyer as `Sender` / `SenderIndex` (so it is in the buyer's sent folder); a plain purchase stays system mail (`SenderIndex` -1).
- `C.MarketSearchEx` sets the extended filter (`SetMarketFilterEx`: sort, price min/max, min grade) and then runs the normal `MarketSearch` path (same `SearchTime` throttle, `MarketKey` / NPC gate, paging, `S.NPCMarket` / `S.NPCMarketPage`). `GetMarket` applies `MatchEx` only while the filter is on and sorts once per request (cached static `Comparison`s, `AuctionID` tie-break). The filter **sticks** across refresh / paging / re-search until the next plain `C.MarketSearch` or a fresh NPC market open (`NPCScript` `MarketKey`), both of which call `ClearMarketFilterEx`.
- `C.MarketPriceHistory` -> `S.MarketPriceHistory` (Observable) from `Envir.MarketSales`: alive, 1 s cooldown, `MarketKey` page with its NPC in range; silent on failure.
- **Sent mail index (in memory only, no DB field):** `Envir.SentMailIndex` = sender `CharacterInfo.Index` -> `List<SentMailEntry { Recipient, Mail }>`, oldest first. Built by `BuildSentMailIndex` at the end of every `LoadAccounts` (inside `LoadLock`; so also after a web admin restore) from `MailInfo.SenderIndex >= 0`. **Kept current only through:** `MailInfo.Send()` -> `AddSentMail` (every send path), `PlayerObject.DeleteMail` and the `Envir.CleanUp` capacity trim -> `RemoveSentMail`. **Any new code that removes a `MailInfo` from `CharacterInfo.Mail` must call `Envir.RemoveSentMail(mail)`** (a stale entry is also pruned when the folder is built, as a safety net). **Any code that removes a character from `CharacterList` must call `Envir.RemoveSentMailFor(character)`** (GM `ARCHIVEPLAYER` does; web admin account delete in `AccountRoutes.cs` does not yet). Deleted recipients are hidden, not dropped (undelete brings them back). `C.RequestSentMail` (2 s cooldown) / `C.HideSentMail` (0.5 s; ownership = in the player's own list with `SenderIndex == Info.Index`, sets `SenderHidden`) -> `S.SentMail` (Observable false, <= 50 newest, `SenderHidden` skipped).

**Friends: mutual friendships via online requests (2026-10-16, Scene Lab UI phase 2.7)** — `PlayerObject` friends region; game thread only; **requests are in memory only** (`PendingFriendRequests` on the target: requester `CharacterInfo` + expiry `Envir.Time + 60 s`, cap 10, cleared in `StopGame`; nothing saved, no `Envir.Version` change). `Settings.FriendRequestsEnabled` (const, not in the ini) is now `true` (sent in `S.ServerRules`).
- `C.AddFriend { Blocked = false }` sends a **request**, it no longer adds. First failing check wins: empty name (ignored), `PlayerDoesNotExist`, self (`CannotAddYourself`), already listed (`PlayerAlreadyAdded`), **target already has a live request to you → treated as an accept**, target offline (`PlayerIsNotOnline` + Result `Offline`), your list full (`FriendListFull`, `Settings.MaxFriends`), 5 s per-requester cooldown, already pending (`FriendRequestAlreadySent`), `AllowFriendRequests` off (`NotAccepting`), their list full (`ListFull`), 10 pending (`FriendRequestTooMany`). A target who blocked the requester gets a **silent** pending entry (`PendingFriendRequest.Silent`: stored, counted toward the cap of 10, expires with the same `Expired` result + line, but never sent to the target, never acceptable by reply or add-back) so every answer matches a real request and blocks stay private; blocking someone turns their pending request to you silent instead of deleting it. Then: if the target has blocked you the request is **dropped silently** but you still get `FriendRequestSent` (blocks stay private); else stored + `S.FriendRequest { Name, Class, Level }` (Observable false) + a `FriendRequestReceived` line to the target.
- `C.FriendRequestReply { Name, Accept }` (Stage `Game`): missing → `FriendRequestNotFound`; expired → requester gets `Expired`. Decline → requester gets `Declined`. Accept: requester must be online; a block on either side → `NotFound` to the replier (requester not told); either list full → `ListFull`; else a non-blocked `FriendInfo` on **both** characters (existing ones kept), a crossed request removed, `GetFriends` to both, `FriendAdded` to both, `Accepted` to the requester.
- `S.FriendRequestResult { Name, Result }` (Observable false; `Accepted 0, Declined 1, Expired 2, Offline 3, ListFull 4, NotAccepting 5`, class constants) goes to the requester for those six outcomes, **always together with a chat line**; cooldown / already sent / too many / not found are chat lines only.
- Expiry: `PlayerObject.Process` checks `PendingFriendRequests.Count > 0 && Envir.Time >= _nextFriendRequestCheck` (walk at most once a second, no allocation); expired requests notify the requester if online.
- `C.AddFriend { Blocked = true }`: instant and one-sided as before; a new block counts toward `MaxFriends`; blocking an existing friend turns that entry into a block; cancels your pending request to them and turns theirs to you silent; removes the blocker from the blocked player's list (unless that entry is itself a block) and refreshes them if online.
- `C.RemoveFriend`: a non-blocked entry is removed on **both** sides (the other side's own block entry is kept), `GetFriends` + `FriendRemovedYou` to the other player if online, `FriendRemoved` to you; removing a block entry is one-sided.
- **`NotifyFriendsChanged()`** (public): for each online friend in this player's non-blocked list who also lists this player, calls their `GetFriends()`. Called at login (after the player's own `GetFriends`), in `StopGame` (after `LastLogoutDate` is set, so friends see the logout time), on `LevelUp` (`FriendLevelChanged`: at most one push per 10 s, a postponed push is sent from `Process`), on guild create / invite accept and in `GuildObject.MemberDeleted` (leave / kick). Events only, never per tick. Not covered: level **down** (GM).
- `FriendInfo.CreateClientFriend` fills the new wire-only `ClientFriend` fields: `Class`, `Level`, `Gender`, `GuildName` (online: `Player.MyGuild`, no lookup; offline: `Envir.GetGuild` + `FindRank(name)` membership check, because a member kicked while offline keeps a stale `GuildIndex` until next login; "" guildless), `LastSeen` (`LastLogoutDate.ToBinary()`, 0 while online). Whispers / chat from blocked players are dropped as before.
- Gaps: `CharacterInfo.AllowFriendRequests` is set only by `C.SetAllowToggle { FriendRequests }` (phase 2.8; the desktop has no UI for it, so it stays true there); the desktop cannot answer a request except by adding the requester back (client.md §4.5); `S.FriendUpdate` is `Compressed` and `GetFriends` calls `S.FriendUpdate.FitFrame()`: a frame over 65 535 bytes (only reachable with a large `MaxFriends`; 100 rows of 200-char CJK memos fit) has memos cleared from the end, then rows dropped from the end, and logs a line. The desktop inflates it through `Shared.dll` unchanged.

**Social allow toggles echo (2026-10-16, Scene Lab UI phase 2.8)** — `PlayerObject.SendAllowToggles()` sends `S.AllowToggles { ushort Flags }` (Observable false; bit `1 << (int)AllowToggle.X`, shared enum `AllowToggle : byte` Group 0, Trade 1, Observe 2, GuildInvite 3, GroupRecall 4, Marriage 5, LoverRecall 6, Mentor 7, FriendRequests 8 (`Info.AllowFriendRequests`)). Sent at login right after `S.SwitchGroup` and after every change: `SwitchGroup(bool)` (`C.SwitchGroup` and the auto `SwitchGroup(true)` on a group invite), `C.ChangeTrade`, `C.ChangeMarriage` (Marriage or LoverRecall), `C.AllowMentor`, `@ALLOWTRADE` / `@ALLOWGUILD` / `@ENABLEGROUPRECALL` / `@ALLOWOBSERVE`, `SetAllowObserve` (also sends `S.AllowObserve`), `AcceptGuildInvite` (clears `EnableGuildInvite`) and `C.SetAllowToggle`.
- `C.SetAllowToggle { Toggle, Value }` (Stage `Game`, `Enum.IsDefined` else ignored) -> `PlayerObject.SetAllowToggle`: an idempotent setter (the flip packets / `@` commands stay for the desktop). Group -> `SwitchGroup(value)`, Observe -> `SetAllowObserve(value)` (same side effects as before); the others set the flag and send the old `@` command's hint only when the value changes (`SetAllowTrade` / `SetEnableGuildInvite` / `SetEnableGroupRecall` hold that hint code, shared with the `@` commands); FriendRequests sends no hint. Always answered by `S.AllowToggles`, even when nothing changed. Marriage / LoverRecall are set regardless of marital state (`C.ChangeMarriage` still flips one or the other). `C.ChangeTrade` stays silent (no hint).
- **Any new code path that changes one of these toggles must call `SendAllowToggles()`** (a new toggle = a new `AllowToggle` member with the next explicit value ≤ 15, a `Set` in `SendAllowToggles`, a case in `SetAllowToggle`).

**Player belt = 8 cells (2026-10-16, Scene Lab UI phase 2.9; user DB wiped, no `Envir.Version` bump, no packet change)** — `HumanObject.BeltSize` 8 (player; `HeroObject` overrides keep the hero at 2), auto-fill bands unchanged (`PotionBeltMinimum/Maximum` 0/4, `AmuletBeltMinimum/Maximum` 4/6: cells 7-8 = indices 6-7 are filled by hand only). `CharacterInfo.Inventory` starts at 48 (8 belt + 40 bag); `ResizeInventory` opens one row of 10 per purchase (48 → 58 → 68 → 78 → 88; an off-row length rounds up to its row end), cap `>= 88`; `@ADDINVENTORY` costs `2,500,000 << ((Inventory.Length - 48) / 10)` (2.5m / 5m / 10m / 20m) and charges nothing at 88 (2026-10-17: the web bag is a 10-wide grid; the desktop keeps 8 x 5 pages and lifts a half-open 4-cell LockBar). Web admin `CharacterLogic.BeltSize` 8 / `SecondBagStart` 48 ("[0-7] Belt 1-8, [8-47] Bag I, [48+] Bag II"), `PlayerInfoForm` the same split. The inventory is saved with its own length, so an old 46-cell character still loads but never matches the resize steps: the constants ship only with the user-data wipe (`Knowledgebase/Runbook.md` §3.2). **Always** use `BeltSize` / `Inventory.Length` in new code, never a literal 6/8/46/48.

**Receive path:** `ReceiveData` (IOCP) → `Packet.ReceivePacket` → `_receiveList`. Unknown ID → factory null → **the connection's whole buffer is discarded**, no log. Any `ReadPacket` exception → `InvalidDataException` → `Envir.UpdateIPBlock(IP, 24 h)` + disconnect. Flood guard: more than `Settings.MaxPacket` (50) socket **reads** per 5 s → 24 h IP block. Every accept sets a `Settings.IPBlockSeconds` (5 s) IP block; more than `Settings.MaxIP` (5) connections per IP is refused.
**Process path (game thread):** drain `_receiveList` → `ProcessPacket` (resets `TimeOutTime = Time + Settings.TimeOut`, 10 s) → re-enqueue `_retryList` (movement/attack/magic retried while `Player.ActionTime > Envir.Time`; capped at 64) → timeout, and the pre-login limit (5 min in stage None/Login) → flush `_sendList` into one `byte[]` and one send: with `[Optimisation] SendThreads` > 0 (P9-09, default 1) `BeginSend` queues the array for a sender thread (`QueueSend`; ordered per connection, also for `SendDisconnect`/`ClientVersion` sends from any thread; arrays still queued keep a connection on that path after the switch goes to 0), else `Socket.BeginSend` on the calling thread. `Disconnect` hands a socket with queued sends to `SocketSender.CloseWhenDrained` instead of disposing it. **Encode-once** (roadmap P9, `[Optimisation] EncodeOnce`, default on): bytes are cached per packet instance for one connection pass (`MirConnection.EncodedThisPass`, cleared by `EndEncodePass` after every pass), so a packet broadcast to N players is encoded once. **Consequence: never change a packet after you have enqueued it.** A sampled guard logs `[Optimisation] EncodeOnce: <Packet> changed after its first send`.
**Login:** the password check runs off the game thread (see §2.3, Password workers). Failed logins are counted per connection (1-minute IP block after repeated failures). `[General] GMPassword` must not stay at the default (`@LOGIN` refuses it).
**Send:** `MirConnection.Enqueue(Packet)` is queue-safe from any thread and mirrors every `Observable` packet (default **true**) to `Observers`. `PlayerObject.Broadcast` also forwards only `BroadcastObservePackets` to observers. `HeroObject.Enqueue` is a **whitelist** (AddBuff, RemoveBuff, PauseBuff, MagicDelay, MagicLeveled, DeleteItem, UseItem); anything else a hero enqueues is dropped.
**Per-connection info caches:** `SentItemInfo`, `SentMonsterInfo`, `SentNPCInfo`, `SentQuestInfo`, ... — `CheckItemInfo`/`CheckItem` send `S.NewItemInfo` once per connection.
**Version check:** first packet `C.ClientVersion` (MD5 of the client exe) vs `Settings.VersionHashes` from `[General] VersionPath` (default `.\Mir2.Exe`), only if `CheckVersion`. The client hashes `Application.ExecutablePath` (`LoginScene.cs`), i.e. the `Client.exe` apphost, which does not contain the managed code — the check is not proof that client and server share a `Shared.dll`. `VersionPath` is comma-separated. **Trap:** if no listed file exists, `VersionHashes` is empty and with `CheckVersion=True` every client is refused (reason 10); the default `.\Mir2.Exe` does not match `Client.exe`.
**Disconnect reasons.** Wire codes in `S.Disconnect.Reason` (client `MirScene` switch): 0 server stop, 1 logged in elsewhere, 2 packet error (over-long chat/NPC key), 3 WorkLoop crash, 4 kicked by admin (web admin GM kick), 5 max connections (client-defined, not sent by the server). Internal `StopGame`/`SoftDisconnect` reasons (never sent): 10 wrong version, 20 socket gone, 21 timeout, 22 client sent Disconnect, 23 logout, 24 observed player left. Pick unused numbers in the right space for new reasons.

### 2.6 State & Persistence

| File (cwd-relative) | Writer / Reader | Notes |
|---|---|---|
| `.\Server.MirDB` (system data, **import only** since roadmap P7 phase 7.3) | Read by `BinaryWorldStore` (`Server/MirEnvir/Storage/`) on the first start, then never written by the server | Header `Version, CustomVersion`, index counters, then MapInfo, ItemInfo, MonsterInfo, NPCInfo, QuestInfo, DragonInfo, MagicInfo, GameShopItem, ConquestInfo, RespawnTimer, GTMap (written, never read). Kept as the rollback; `MirDbExport export-binary` writes it back from SystemData.db |
| `.\SystemData.db` (system data, roadmap P7) | `Envir.Store` = `SqliteWorldStore` (`Server/MirEnvir/Storage/`) | One STRICT table per Game Data registry endpoint keyed by `(parent_position, position)` (`SqliteWorldCodec`, schema_version 2) plus `meta`, `index_counters`, `respawn_tick(_options)` and `runtime_state` (the respawn tick counter). One writer thread owns the file; **Envir.Edit is the only design writer**, Main's save writes `runtime_state` only. The first start imports Server.MirDB (left untouched); a store older than Server.MirDB, or a lost store with SQLite backups, is refused. Missing tables/columns are added on open (`SqliteWorldFile.Migrate`, §4.3). Backups: `VACUUM INTO` copies in `Back Up\Database` with the old names. Offline: `MirDbExport import / export-binary / roundtrip` |
| `.\Configs\*.ini` design settings, `.\Envir\{Drops,Quests,Recipe,Routes}\**\*.txt` (roadmap P7 phase 7.4) | In SystemData.db once it exists: `DesignIni` (`ini_files`/`ini_lines`; Setup.ini's `[Game]`/`[Items]` via `SetupIniReader`, the rest of Setup.ini stays a file) and `DesignTexts` (`design_files` + `route_points`/`quest_lines`/`recipe_lines`, item/monster Index links). Drops are tables since Drops-Tables-Plan phase 1: `DropTables` (`drop_lists` with the owner, `drops` rows with `parent_id` groups, `include` / `note` / `disabled` rows; the game's `DropInfo.LoadForMonster` / `Load` / `DragonInfo.LoadDrops` build from the rows, a monster without DropPath finds its own list by Index; the 7.4 `drop_lines` table is converted on store open, with a `Pre-drops` backup). Drops are edited in the web admin's Game Data › Drops (`DropRoutes`, phase 2: whole-list saves straight to the store, then `Envir.ReloadDropList` on the game thread); the Scripts editor shows them read only, DropBuilder is gone, and renames don't touch drops. Loaders and editors go through `DesignFiles` (a path facade over `File.*`) with an in-memory cache | Imported lazily from the files on first use; the files are not read again (they are the rollback: `MirDbExport export-files`). NPC scripts, `Envir\Values`, `Envir\NameLists`, DisabledChars/LineMessage/Notice stay files |
| `.\Server.MirADB` (user data) | `BeginSaveAccounts`/`SaveAccounts` / `LoadAccounts` (~L2962) | Counters, HeroList (before accounts), AccountList (embeds CharacterInfo incl. mail), auctions, mail id, GameshopLog, SavedSpawns, MarketSales (v119: `Dictionary<int ItemIndex, MarketSaleHistory>`, last 14 sales each) |
| `.\Guilds\{i}.mgd` | `SaveGuilds`/`LoadGuilds` | **File name = position in `GuildList`**, not GuildIndex; sentinel `int.MaxValue` + version header; v119 appends the guild log (`GuildLogEntry` list, cap 100) |
| `.\Envir\Goods\{npcIndex}.msd` | `SaveGoods` | sentinel 9999 + version |
| `.\Conquests\{Index}.mcd` | `SaveConquests`/`LoadConquests` | **No version header** |
| `.\Archive\*.MirCA` | archived characters | Version + CharacterInfo |
| `.\Back Up\Database\*.bak`, `.\Back Up\Accounts\*.bak` | every periodic save | Never pruned unless `[Backup] RetentionEnabled=True` (default off; `KeepAllHours`, `KeepHourlyDays`, `KeepDailyDays`; `Server/MirEnvir/BackupRetention.cs`) |

**Save mechanics (all on the game thread):**
- `SaveDB()` → `Envir.Store.SaveDesign` (roadmap P7): for Envir.Edit, encode on the calling thread, then the writer thread takes a `VACUUM INTO` backup (`Back Up\Database\Database yyyy-MM-dd HH-mm-ss.bak`, or LiveDb's name) and rewrites the design tables in one transaction; the caller waits. For Main, only the respawn counter is posted (no wait). Ends `if (this == Edit) _systemDataStale = true`. (Until 7.3 this wrote Server.MirDB via temp file + `File.Replace`; `BinaryWorldStore` keeps that code for the tools and tests.)
- `BeginSaveAccounts()` (crash-safe since P2; P9-10): serialise on the game thread, then write `Server.MirADBn` off it and `File.Replace` it in (the previous file becomes the `Back Up\Accounts\` .bak), reset `Saving` in `finally`, and count failures (`Envir.AccountSaveFailures`, on `/status`). **`[Optimisation] AccountSaveOffThread` (default on, P9-10):** the serialised bytes are the snapshot. They go into one `MemoryStream` kept across saves (`_accountSaveBuffer`, no growth or `ToArray` copy), and a pool thread (`WriteAccountsFile`) does every file step from the backup check on, with full paths. The buffer belongs to the game thread only while `Saving` is false, and the pool thread only reads it. Off = the legacy path (`BeginSaveAccountsLegacy`: new stream + `ToArray`, file opened on the game thread, APM write, `EndSaveAccounts`). Both write the same bytes (`AccountSaveOffThreadTests`). `CharacterInfo.WriteFlags` writes the 1999 quest flags in one block (same bytes as one `Write(bool)` each). The live `Server.MirADB` always exists. `SaveAccounts()` (sync, shutdown/stopped-world only) waits for `Saving` at most 30 s. With `[Diag] LogLoopProfile` each save logs one `[Diag] Save accounts=… db=… guilds=… goods=… conquests=… total=… bytes=… accounts=…` line (game-thread ms per step).
- Guilds/Goods/Conquests: serialise on the game thread, async `BeginWrite` to `*n`, `File.Replace` in the completion (`CompleteSideFileSave`). The guild refresh no longer deletes files first; surplus guild files are pruned only after every write succeeded (`PruneSurplusGuildFiles`).
- **Fail closed (P2):** `LoadDB`/`LoadAccounts` refuse to create an empty DB when backups or temp files show prior data (`HasPriorDataEvidence`); `StartEnvir` throws when `LoadDB` fails. Restore steps: `Knowledgebase/Runbook.md`.
- **`SystemDataStale`** (fork): set once `Envir.Edit.SaveDB()` ran, cleared by `StartEnvir`. Since roadmap P7 it no longer gates any save (Main never writes design data); it only tells the web admin that Main's in-memory system data is older than the store until a world reboot, except what **LiveDb** applies live (every table's live columns except the conquest children; see §2.7) and the magic sync.
- **LiveDb persistence details:** `Envir.SaveDB(string backupFileName)` names the backup (Edit's clock never advances, so the web admin passes a wall-clock `Database yyyy-MM-dd HH-mm-ss.bak` at most every `[LiveDb] BackupEveryMinutes`, else the rolling `Database livedb-previous.bak`; `LiveDbBackupPolicy`, F3). Main publishes `RespawnTick.CurrentTickcounter` after `RespawnTick.Process()` (`Interlocked`), and Edit's `SaveDB` writes that value (`RespawnTimer.Save(writer, counter)`, same layout, F4). No format change, no `Envir.Version` bump.

**Versioning:** one number for every file — `Envir.Version = 120`, `MinVersion = 60`, `CustomVersion = 0` (`Envir.cs` ~L55). Writers always write the current version; readers gate on the static `Envir.LoadVersion`/`LoadCustomVersion` (set by `LoadDB`, overwritten by `LoadAccounts`) or on a `version` ctor parameter — follow whichever the class already uses. `LoadDB` refuses `< MinVersion` or `> Version`; since roadmap P2 `LoadAccounts` refuses the same range (throws). v118 (roadmap P3): `Buff.Data` is a typed key/tag/value encoding (`BuffInfo.cs` `DataTag`, append-only byte values); v117 and older BinaryFormatter blobs are skipped on load (Concentration interrupt state resets). v119 (2026-10-16, SceneLab UI phase 2.3): `CharacterInfo` `PlayTime` (s), `ProfileText` (cap `ProfileTextMaxLength` 120 on write paths), `EnableGroupRecall`/`EnableGuildInvite`/`AllowMarriage`/`AllowLoverRecall`/`AllowMentor` (were session-only `PlayerObject` fields, now properties over `Info`) and `AllowFriendRequests` (default true); `MailInfo.SenderIndex` (-1 = system/NPC/pre-119) + `SenderHidden`; `UserMagic.AutoCast` (default true, players and heroes); `GuildInfo.Log` (`AddLog(GuildLogKind.X, text)`, server-only append-only byte kinds); `Envir.MarketSales` (`RecordMarketSale`); `GameShopItem.DealGoldPrice`/`DealCreditPrice` (DB + wire). `HeroInfo` overrides Load/Save and does not store the CharacterInfo v119 fields. v120 (2026-10-20): `CharacterInfo.ChatBanned` + `ChatBanExpiryDate` (`ToBinary`), appended after the v119 block; before v120 a chat ban (in-game `PlayerObject` or web admin `GmActions`) was lost on restart. Heroes do not store them. **Nothing in Server.Library may call `Functions.SerializeToBytes`/`DeserializeFromBytes` (BinaryFormatter, removed on .NET 9+)**; the Shared helpers are deleted in roadmap C5. Committed format fixtures: `Server.Persistence.Tests/Fixtures/v117`, `v118`, `v119`, `v120` (v117/v118 must upgrade with every v119 field at its default, `Oracle.WithoutV119`; v119 must upgrade with the chat ban at its default — the oracle already printed those fields, so its dump is unchanged; v120 re-saves byte-identically). Shared models (`UserItem`, `ItemInfo`, `Stats`, `ExpireInfo`, `SealedInfo`, `RentalInformation`, ...) default `version = int.MaxValue` → the same ctor reads the **packet** layout (`Awake` has no version parameter; `GameShopItem` uses a separate `(reader, bool packet)` ctor / `Save(writer, packet)` for the wire). **Nothing reads `CustomVersion`**, and by the user's decision nothing should: new fields bump `Version` (§4.3).

**Load order** (`StartEnvir` ~L3426 → `StartNetwork`): `LoadDB` (needs `Settings` loaded; `FillMagicInfoList`; GameShop binds to Edit) → `BuffInfo.Load()` → recipes → `MapInfo.CreateMap()` per map → StartItems → `ReloadDrops()` → line messages → Dragon → default/monster/robot NPC scripts → `LoadAccounts` (HeroList before AccountList) → `RaiseAccountsLoaded` → `LoadGuilds` → `LoadConquests` (needs maps and guilds) → `LoadGTInfo` → listeners.

**Silent data loss on load** (persisted by the next save = permanent): `UserItem`s whose `ItemIndex` no longer exists are dropped everywhere (inventory, equipment, storage, guild storage, auctions); `UserMagic`/`UserIntelligentCreature`/`QuestProgressInfo` with no info are skipped; characters past `ArchiveDeletedCharacterAfterMonths` (1) / `ArchiveInactiveCharacterAfterMonths` (12) move to `.\Archive`; ~~every account except the first with zero characters is dropped~~ (fixed 2026-10-10, roadmap P5: zero-character accounts are kept).

### 2.7 Embedded HTTP — MirCraft web admin (+ legacy HttpServer)

Fork-only (not in upstream). Authoritative developer doc: `Server/WebAdmin/README.md`.

| Piece | Where | Facts |
|---|---|---|
| Host | `Server/WebAdmin/WebAdminHost.cs` | `HttpListener`, started in `Program.Main` before `SMain`, independent of the world. Concurrency 32 (503 when full). Security headers on every response; `/api/*` `no-store`. `WebAdminException` → JSON error; other exceptions → 500 "Internal error, see server log". |
| Options | `WebAdminOptions.cs`, `[WebAdmin]` in Setup.ini | `Enabled=True`, `Bind=127.0.0.1`, **`Port=5680`**, `WebRoot=.\wwwroot` (vs `AppContext.BaseDirectory`), `SessionHours=8` (1–720), `MaxBodyKB=2048` (16–65536). Read once per process → **restart Server.exe** to change. |
| Router | `WebAdminRouter.cs` | `IWebAdminRoutes` classes found by reflection (`RegisterAll`); `/api/auth/*` Anonymous, else `RouteAuth.Admin`; duplicate method+shape throws and drops that module. Modules: `Server/WebAdmin/Routes/*Routes.cs`. |
| Auth | `WebAdminAuth.cs`, `WebAdminSessions.cs`, `WebAdminAdmins.cs` | Sign-in = game account with `AdminAccount = true`. Password via `Server/Utils/Crypto.cs` (PBKDF2-SHA1, 50 iterations, 24-byte salt/hash, hash stored as a UTF8 string). Offline directory `<exe>\WebAdmin\admins.dat` while the world is stopped. Cookie `MirCraftAdmin` (HttpOnly, SameSite=Strict, not Secure); CSRF cookie `MirCraftAdminCsrf` + header `X-MirCraft-CSRF` on every mutating request. Login must be `application/json` and same-origin; rate limits 10/IP/min, 120/min global, 10 failures per (account, IP) per 15 min. Sessions in memory. |
| Game bridge | `WebAdminGame.cs`; `Envir.QueueAdminAction` (~L3298), `DrainAdminActions` (~L3325), `RequestSaveNow` (~L3312) | `RunAsync<T>(fn, timeoutMs=5000)`: 503 `WorldNotRunningException` if not queued, 504 `GameThreadTimeoutException` on timeout (cancelled if not started; `started=true` means it **may** have run). `RunOrInlineAsync`: inline under `lock (Envir.AccountLock)` only when fully stopped. `Post(fn)`: fire-and-forget. `WorldState`: running/stopped/starting/stopping; `WorldCrashed`. |
| Hooks | `WebAdminHooks.cs` | `WorldStarted/Stopped`, `AccountsLoaded/Saved`, `LoginAttempt`, `LoginGate` (`Func<AccountInfo,string>`), `PlayerEntered/Left`, `PlayerDied` (may run on a **mob thread**: subscribers lock-free, enqueue only), `Chat` — raised on the calling (usually game) thread, wrapped in try/catch |
| Game data | `Server/WebAdmin/GameData/*` | Grid engine over `Envir.Edit` under `WebAdminGame.EditLock`; `__rev` per-row conflicts; `GameDataSave.EnsureSavable()` + `SaveEdit([backupName])` → `RunFileIo` (on the game thread while Main still saves, else under `Envir.LoadLock`); `GameDataSave.TryRunOnGameThread` = the shared "queue and wait" helper; `WebAdminPending.Add` for reboot-only data |
| LiveDb | `Server/WebAdmin/LiveDb/*`, `Routes/LiveDbRoutes.cs`, `[LiveDb]` settings (`Enabled=True`, `BackupEveryMinutes`, `SweepPerTick`, `MaxBatchRows`, `MaxPushRows`) | `POST /api/admin/livedb/batch[?dryRun=1]` (and `PUT /api/admin/db/{endpoint}?live=1`): one batch, all-or-nothing — validate every row (dry run) → apply to Edit with before-images → save (failure restores Edit) → one game-thread step (`LiveDbMainApplier`, after the mob barrier; failure restores Main, keeps the save, pending entry) → journal `Logs\WebAdmin\livedb\batches-*.jsonl` + audit (`batch:<id>`). Refuses 503 while starting/stopping, 409 while a WinForms DB editor is open (`Envir.EditorFormsOpen`) or other unsaved Edit changes exist (unless `flushUnsaved`). Client batch id + LRU 500 → a retry returns the first outcome. Per-column class `ReadThrough`/`Hooked`/`RestartOnly` + timing (`LiveDbClassification`; unclassified = RestartOnly; a test fails on an unclassified column); Live tables: monsters (stats/speeds → budgeted `LiveDbSweeper` in `WorkLoop` after the drain: `RefreshAll` + HP clamp; DropPath → drop reload; inserts cloned + drops loaded), magic (`GameDataMagicSync.CopyInto`), since Batch B items (read-through columns; stats/weight/light/set/shape/effect/unique/fast-run → `RefreshStats` of online holders incl. spawned heroes; RandomStatsId → re-resolve; StartItem → `StartItems` rebuild; inserts cloned) and, since Batch C, the world tables (`ILiveDbHooks` / `LiveDbWorldHooks`): NPCs (visibility re-check, re-broadcast, script rebind, move via `RelocateNpc` remove → change → add (F10), insert spawns), quests (fields + shared `GameData/QuestReload` for FileName), maps (flags read through `Map.Info`, `Map.RebuildMine`; Light / MapDarkLight / WeatherParticles → `S.MapAtmosphere` to the players and observers on the map via `ILiveDbHooks.PushMapAtmosphere`, after the client push), safe zones (zone spells removed/recreated — only caster-less never-expiring zone decorations/healing cells — `StartPoints`, `InSafeZone`), respawns (`MapRespawn` cache rebuilt in place, `SavedSpawns`, insert/delete; spawned monsters stay), movements, mine zones, game shop (bound to Main's `ItemInfo`, pushed with per-player stock), conquests (refused while the war is on), dragon (Exps, `Dragon.RefreshDropArea`). Only the conquest child tables save only. A moved/new NPC's cell is checked on the game thread before the save (unwalkable → 422). Refusals are per row/field (`live.refusedLive`). **Client push** (Batch B, `LiveDbPush`): last step of the live action, `S.GameDataChanged` (private) per connection with only the rows in its `Sent*Info` lists (+ each player's/hero's own `ClientMagic`), fresh snapshot copies, chunks ≤ 60 KB, `Last` on the final chunk; a push failure is logged and never rolls back; more client-visible rows than `MaxPushRows` → no push and item updates saved only. `LiveDbState`: divergence of position-keyed children, cleared on `WorldStarted`; sweeper dropped on `WorldStopped`. Position-keyed child lists (safe zones, movements, mine zones) are checked by the live step against the editor copy's pre-batch length and each touched row's values, so a list changed behind LiveDb's back is never changed by position; a structural change that does not reach Main marks the parent diverged. Status/schema/history: `GET /api/admin/livedb/{status,schema/{endpoint},history,history/{id}}` (history paged + filtered by table/admin/date, with `inverseOf`/`undoneBy`; detail = per-row before/after + undo state); `livedb` block on `/api/admin/status` (Dashboard tile). **Undo** (Batch D, `LiveDbUndo`): `POST /api/admin/livedb/undo/{id}[?dryRun=1]` builds the inverse from the journal (updates → before-values with `__rev` = `revAfter`; inserts → deletes guarded by `revAfter` via the batch table's `deleteRevs`; deletes refused 409 → restore a backup) and runs it through the same pipeline (all-or-nothing, per-row conflicts, journal `inverseOf`, audit). Undo id `undo:<id>` (double click → stored outcome; undo of an undo = redo; then `undo2:<id>`). Removing an added item/monster/NPC is saved only (the running world keeps it until restart, said in `notes`). Frontend: section `admin-livedb.js` (LiveDb History) |
| Live Monitor | `WebAdminMonitor.cs`, `Routes/MonitorRoutes.cs`, `MapArt/*`, `wwwroot/admin/admin-monitor.js` | Polled every **1 s** (approved exception to the ~5 s floor, 2026-10-11). `GET /api/admin/monitor/{snapshot,heat,maps/{index}/image,maps/{index}/minimap,maps/{index}/tiles/{z}/{tx}/{ty}}`. Map art (`[WebAdmin] ClientDataPath`, read-only, process-restart): `MapArtService` renders 768 x 512 tiles on HTTP threads, one at a time (`RenderGate`), from the server's `Maps\*.map` with a copy of the client's `MapReader` and `MapLibs` table (keep in step with `Client/MirObjects/MapCode.cs` / `Client/MirGraphics/MLibrary.cs`); map info via one `RunAsync` per index per world run. Snapshot = one `RunAsync` per poll (O(maps + players), + one `Envir.Objects` walk when monsters are asked, mob threads parked); event ring drained and read on the game thread only; heat captured by a `Post` from `WebAdminStatusSampler` every 5 s and folded under `HeatLock` off the game thread; map PNG = one cell copy on the game thread, encoded on the HTTP thread, cached until `WorldStarted`. GM actions from the page reuse `POST /api/admin/gm/action` |
| Tools | `Tools/WebAdminTools.cs`, `Tools/StressBotConfig.cs`, `Tools/ToolProcess.cs`, `Routes/ToolsRoutes.cs`, `wwwroot/admin/admin-tools.js` | Section `tools` (Monitoring). Child processes only, never game state or game locks: one StressClient fleet (`--control-stdin --status-json`, `@status` lines parsed into a 300-sample ring), one `dotnet build` of StressClient, one scratch-server script run (`tools\start/stop-scratch-server.ps1 -Scratch %TEMP%\mir2-webadmin-scratch`, new `-MapsFrom`). Repo from `[WebAdmin] RepoPath` or the first folder above the exe with `Legend of Mir.sln`. Target `self` = the server hosting the panel, refused unless `[LoadTest] ExemptLoopback`, `CheckVersion=False`, AllowLogin+AllowStartGame, loopback/any bind and a non-default bot password; stopped on `WorldStopped`. **Agents test bots on scratch servers only** (user, 2026-10-21; stressclient.md §9). Polled every 5 s |
| Maps editor | `MapEditor/MapEditorService.cs`, `Routes/MapEditorRoutes.cs`, `wwwroot/admin/admin-mapeditor{,-view,-edit,-picker}.js` | Section `mapeditor` (Game Data group). Read-only routes `GET /api/admin/mapeditor/{maps,online,maps/{index}/{overlay,terrain,minimap,tiles/{z}/{tx}/{ty},players}}` read the editor copy under `EditLock` (overlay rows = the Game Data grid rows with `__rev`) and the server map file on the HTTP thread (walkability via `ClientMapData` + the server's v0 middle-tile wall, cached per file stamp; tiles via `MapArtService` sized from the file), so they work while the world is stopped; only `/players` and `/online` use one `RunAsync` each (both empty while stopped). `admin-mapeditor-picker.js` gives the rest of the panel `MapPicker.pick`, *Pick on map…* in the row drawer (`FormDrawer.EXTENSIONS`) and map hover previews in grids (`window.ImagePreview`). Writes go through the Game Data grids / LiveDb pipeline (`GameData.persist` with the page's own grid editors, dirty keys `mapeditor:<endpoint>`), and edit mode (drag / resize / add / delete on the canvas, LiveDb only) sends one `MirLiveDb.runBatch` over the map's tables (dirty key `mapeditor:edits`); never a route of its own |
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
- Settings added by the roadmap (all also on the web admin settings page): `[Network] StatusPort` (3100) / `StatusPortEnabled` (true); `[Optimisation] IdlePark` (true), `MobThreadOnePass` (true), `EncodeOnce` (true), `HumanProcessMs` (10; players/heroes process at most every N ms, delayed actions still on time; 0 = every iteration), `MapRespawnCheckMs` (1000, max 5000; maps check respawns at most every N ms with a random phase; doors, weather and map actions still on time; 0 = every iteration), `DueScheduler` (true; per-thread due-time heaps instead of walking every object; world restart), the kill switches for the P6/P9 loop changes; `[Diag] LogLoopProfile` (false, writes a `[Diag] Loop` line to the Debug log every few seconds); `[Security] PasswordHashV2` (false; see the Runbook before turning it on); `[Backup] RetentionEnabled` (false) + keep windows; `[LoadTest] ExemptLoopback` (false) / `LoopbackMaxPacket`, **scratch servers only**.
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
- **NEVER** change a map cell's objects except through `Cell.Add`/`Cell.Remove` (2026-10-22, map memory): `Cell` is a struct handle (map + index) from `Map.GetCell`, attributes live in the load-time-immutable `Map.CellData` byte grid, and object lists are created per cell on the first `Add` in lazily allocated 64-cell blocks; `Cell.Objects` of a cell that never held an object is one shared empty list, so never `Add`/`Remove` on the list itself (the only exception is `Map.RemoveSafeZoneSpells`). Per-cell side data goes in sparse structures (`Map.GetDoor`, `MineGrid`, the object blocks), not a `[Width, Height]` array of objects; whole-map object scans use `Map.ObjectCells()`.
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
- **ALWAYS** treat a rented flush array (`MirConnection.BeginSend(data, length, pooled)`, 2026-10-22) as owned by the send path: it goes back to `ArrayPool` exactly once, after the socket is done with it (`CompleteSend` after the send args let go of it, the APM `SendData` callback, or at once when it is never queued). When a failure leaves it unclear whether a send still reads it, leave it to the GC instead. Never return an array you did not rent (`Return` throws for odd sizes) and never send past `length` (rented arrays are larger).
- **ALWAYS** keep outbound packets batched (one `BeginSend` per connection per `Process`). Send bytes only through `MirConnection.BeginSend`, never `Socket.Send*` directly: it keeps a connection's bytes in order across the sender-thread path (P9-09).
- **ALWAYS** cache `Envir.Now` in a local inside loops (the property computes `AddMilliseconds` each call).

#### Known debt — existing breaches (do not copy; do not fix in drive-by edits)

| Location | Issue |
|---|---|
| `Envir.WorkLoop` | ~~Busy spin~~ (idle park since roadmap P6); ~~`Map.Process` for every map every iteration~~ (P9-04 respawn gate, P9-05 map scheduler; the per-map loop remains as the `DueScheduler=False` fallback); ~~the object walk visits every mob-thread monster node only to skip it~~ (P9-05 due scheduler; the walk remains as the `DueScheduler=False` fallback) |
| `MirConnection.Process` | ~~`new List<byte>()` + `AddRange` + `ToArray()` per flush~~ (P9-04: one exact-size `byte[]` per flush; since 2026-10-22 flushes up to 64 KB rent it from `ArrayPool<byte>.Shared` and the send completion returns it: `Future Optimisation/Alloc-Tail-2026-10-22.md`) |
| `MirConnection` queues | `_sendList`/`_retryList` uncapped; `MaxPacket` limits socket reads per 5 s, not queued packets |
| `PlayerObject.Broadcast` | ~~`Array.Exists` closure per broadcast + `foreach` over `Observers`~~ (fixed P9-03: skips when there are no observers, for-loops) |
| `MirConnection.ReceiveData` | ~~`_rawData` re-allocated + `BlockCopy` on every read, and a copy of the rest per packet~~ (2026-10-22: parsed in place with `Packet.ReceivePacket(buffer, offset, count, out consumed)`; only a partial frame is copied). Still per read: the APM `BeginReceive` Task wrapper; per packet: `MemoryStream` + `BinaryReader` |
| `Packet.GetPacketBytes` | Two `MemoryStream`s + arrays per packet **per recipient**; a broadcast to N players serialises N times |
| `MonsterObject.GetInfo` (~L2876), `HeroObject`, `PlayerObject.GetInfo` | ~~`Buffs.Where().Select().ToList()`~~ (P9-03: a loop; since 2026-10-22 `MapObject.AddVisibleBuffTypes` fills the packet's own list, no array when no visible buff); the info packet is still built per viewer |
| `MonsterObject.Process` | `RefreshNameColour()` every process (compute only; broadcasts on change) |
| `MapObject.HasAnyBuffs` | ~~`Select().Except(new List<>).Intersect().Any()`~~ (fixed P9-03: nested loop; the `params` array remains) |
| `HumanObject` FireWall (~L4157), `FindAllNearby(...).Where().ToList()` (~L5947), portal `Envir.Spells.Count(lambda)` | LINQ per cast |
| `HumanObject.Process` | ~~`Pets.Count()` LINQ per tick~~ (fixed P9-03) |
| `Envir.Process` timers | `Timers.Keys.ToArray()` once per second |
| `SaveGuilds` | `Guilds.Find(lambda)` per guild per save |
| `NPCObject` speech, `NPCScript.PriceRate`, `GuildObject` ctor `Any`, `MonsterInfo.GameName` (`Regex.Replace`), `UserMagic` info lookup | LINQ/regex over collections |
| `Cell.LowWall/HighWall` static getters | ~~allocate a new `Cell` per call~~ (fixed 2026-10-22: no per-cell objects at all, `Cell` is a struct over `Map.CellData`; `Future Optimisation/MapGrid-Memory-2026-10-22.md`) |
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
- **NEVER**, from monster code (it may run on a mob thread), mutate cross-map or global state — another map, `Envir.Players`, `Envir.Objects` outside `Spawned`/`Despawn`, global counters — without considering `Settings.Multithreaded`. Defer such work with `Envir.RunOutsideMobPass(action)` (runs at once outside a mob pass, otherwise on the game thread right after the barrier, same tick; errors isolated), or queue a `DelayedAction` on `CurrentMap.ActionList` (`MapActionList.Add` itself defers during a mob pass), or do it from player/pet/map code. Already deferred: monster `[@_DIE]`/`[@_SPAWN]` scripts, the exp/quest/drop part of `MonsterObject.Die` and its copies (`Sep*`, `HumanAssassin`, `HumanWizard`), `SnowWolfKing` slave taming, cross-map `MonsterObject.Teleport`, `Envir.Objects`/`ObjectsList` links. Capture fields the deferred code needs (e.g. `EXPOwner`, `Master == null`) before the call. Do **not** use the monster's own `ActionList` — `MapObject.Process` drains it on the mob thread.
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
| Save file writes | APM `BeginWrite`; one `ThreadPool.UnsafeQueueUserWorkItem` per account save (P9-10) | existing `SaveGuilds`/`SaveGoods`/`SaveConquests`; `BeginSaveAccountsOffThread` → `WriteAccountsFile` (files, the handed-off bytes and `Saving` only) |
| SQLite system-data writer (roadmap P7) | its own background thread per file (`SqliteWriter`: `SystemData.db`, `SystemData.shadow.db`) | Owns the only connection; takes no web admin or Envir lock and never posts to the game thread, so callers may `Run` (wait) under EditLock/LoadLock. The game thread only `Post`s (Main's runtime save); it waits only in `StartEnvir`'s `Main.LoadDB`, before the loop runs. Encode/decode happen on the caller's thread. Drained on ProcessExit |
| Game simulation, packet handlers, monster AI | **Forbidden** | sync only |

#### Known debt — existing breaches
- WinForms UI thread mutates live state: `Server.MirForms/Account/PlayerInfoForm.cs` (`Character.Player.Teleport`), `Account/Market.cs` (`Envir.Main.Auctions.Remove`), `BalanceConfigForm` (`RequiresBaseStatUpdate` → `RefreshStats` on heroes), `Database/GameShop.cs` (`ClearGameshopLog`, `ResetGS`), `SMain` menus (`ReloadNPCs`, `ReloadDrops`, `ReloadLineMessages`, `IPBlocks.Clear`, `GlobalMessageButton_Click` → `ReceiveChat` for every player), `SMain.ProcessPlayersOnlineTab`/`ProcessGuildViewTab` and `InterfaceTimer_Tick` reading `Envir.Players`/`GuildList`.
- `Systems/MonsterTunerForm.cs` and `Systems/DragonInfoForm.cs` call `Envir.Main.SaveDB()` on the UI thread (races the game thread's SaveDB). **Partly fixed 2026-10-10 (approved):** they skip the save with a message box while `[LiveDb] Enabled` or `SystemDataStale`, so they can no longer overwrite a newer Edit save; with LiveDb off and no Edit save yet the race remains. WinForms editors save `Envir.Edit` with no `LoadLock`/`EditLock` (LiveDb refuses batches while one is open: `SMain.ShowEditor` → `Envir.EditorFormsOpen`).
- Legacy `HttpServer` `/broadcast` calls `Envir.Main.Broadcast` from its own thread; `/newaccount` → `HTTPNewAccount` adds to `AccountList`/`++NextAccountID` from it (under `AccountLock`, but game-thread readers don't take it).
- `WebAdminStatusSampler` reads `Envir.Main.Players.Count`/`Connections.Count` from a timer thread (racy; tolerated for counts only).
- Game-thread blocking IO: `SaveDB()` (sync backup copy + rewrite), synchronous log4net writes in `MessageQueue`, file reads/writes in `ReloadNPCs`/`ReloadDrops`/`ReloadLineMessages` (sanctioned only as operator-triggered actions, never per tick). `lock (Connections)` is held across all packet handling every ms, plus `lock (StatusConnections)`.
- Time: guild territory rent uses local `DateTime.Now` (`GuildObject`/`GuildInfo` `GTRent`, `PlayerObject` ~L10525, `NPCSegment` ~L3142) while `Envir.Now` is UTC. `Awake` upgrade/hit rolls (`Shared/Data/ItemData.cs` ~L995/1041) create `new Random(DateTime.Now.Millisecond)` per call.
- ~~Monster code on mob threads: non-atomic `ObjectID`, `MonsterCount`, `Envir.Objects.AddLast` from `Spawned()`, die/spawn scripts, exp and drops~~ — **fixed**: `ObjectID` (roadmap P5); `Envir.MonsterCount`, `Map.MonsterCount` and `MapRespawn.Count` use `Interlocked`, and list links, scripts, death rewards and cross-map teleports are deferred to the barrier (monster threading review, 2026-10-11). Still open: other monster AI that reaches a player, another map or a global from inside `Process` without going through `RunOutsideMobPass` (audit case by case; same-map player damage is safe because players never run during the barrier).
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
| Monster AI throws on a mob thread | Must never happen | Since roadmap P5: per-monster catch inside `ThreadLoop` (the monster is quarantined for `WorldErrors.QuarantineMs`); an exception outside it marks the thread `Dead` and the barrier skips it (legacy walk: its monsters freeze until restart; due scheduler: the game thread adopts them). An exception in the game thread's own mob pass ends that pass early but the barrier still waits for the sub-threads; `RunDue` on the game thread is wrapped too. A shutdown interrupt ends a mob thread (`Exited`) instead of being swallowed by the per-monster catch. |
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
- **NEVER** change the value of an existing member of a persisted/wire enum: `Stat : byte` (stays < 255, `Unknown = 255`), `Monster : ushort`, `ItemType`, `Spell`, `BuffType`, `ObjectType`, `MirClass`, `BindMode`, `ItemSet`, `AllowToggle : byte` (explicit values; bit index into `S.AllowToggles.Flags`, so ≤ 15), `MirAction` (implicitly numbered, but every v3 `.Lib` frame table stores it by byte value — `Client/MirGraphics/MLibrary.cs`, `LibraryEditor/Graphics/MLibraryV2.cs` — so append only, never insert or reorder), ... Add members with an explicit unused value: at the end for sequential enums (`ItemType`, `BuffType`, `ItemSet`, `ObjectType`), or an unused value in the right band for banded enums (`Spell` by class, `Stat` < 255). Check `[Flags]` (`PoisonType`, `BindMode`, `SpecialItemMode`, `RequiredClass`, `RequiredGender`) before validating with `Enum.IsDefined`. `BindMode : short` has no free bit (`NoHero = -32768`) — a new bind flag needs a format change (§4.3).
- **NEVER** change a `Shared/Data` model's `Save`/ctor without treating it as **both** a DB-format and a wire-format change (`GameShopItem` has a second packet ctor/`Save(writer, packet)` — update both).
- **NEVER** add fields to `ConquestGuildInfo` (`.mcd`) without first adding a version header; **NEVER** append a section to `SaveDB` after `GTMapList` without first making `LoadDB` read or skip the GTMap block.
- **NEVER** delete an `ItemInfo`/`QuestInfo`/`MagicInfo` row casually — unbindable player items/quests/magic vanish at the next load and are gone after the next save.
- **NEVER** rename or delete a `MonsterInfo`/`ItemInfo` row whose name a Setting references (`SkeletonName`, `ShinsuName`, `BugBatName`, `Zuma1-7`, `Turtle1-5`, `BoneMonster1-4`, `HellKnight1-4`, `CloneName`, `FishingMonster`, `ScrollMob1-4`, ..., `RefineOreName`; full list in `Envir.CanStartEnvir`), and never remove the last StartPoint: with `[General] EnforceDBChecks=True` (default) the world refuses to start.
- **NEVER** call `Envir.Edit.SaveDB()` directly from web code (use `GameDataSave.EnsureSavable()` + `SaveEdit()`/`RunFileIo`); **NEVER** open `SystemData.db` from anything but its `SqliteWriter` (one connection, one thread) — offline tools work on a copy; **NEVER** expect a change made on `Envir.Main`'s design lists to be saved (Main's save writes only `runtime_state`): edit `Envir.Edit`.
- **NEVER** read or write the design files (`Configs\*.ini` that Settings loads, `Envir\Drops|Quests|Recipe|Routes`) with `File.*`/`new InIReader` in new code: use `DesignFiles` / `DesignIni` (roadmap P7 phase 7.4). They serve the store while it is active and the disk otherwise; a direct file access reads stale data or writes something nothing loads.
- **NEVER** hand-edit or replace `SystemData.db`/`Server.MirADB` while the world runs; restores need the world fully stopped (the web restore closes the store's connection first). **NEVER** rename or remove a public field of a system-data model, or exclude it from `GameDataRegistry`, without a written migration: the store's columns come from the registry, and an unknown column fails the load.
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
- `Envir.BindGameShop` binds Main's `GameShopItem.Info` to `Envir.Edit.ItemInfoList` objects (cross-instance references; requires Edit loaded first). LiveDb binds the rows it inserts or whose ItemIndex it changes to Main's `ItemInfo`, and its push copies Main's item; rows loaded at world start keep the Edit reference.
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
- **ALWAYS** fill a packet with copies of data that can change before the connection flushes (EncodeOnce encodes a packet once per pass and mirrors the first bytes): the LiveDb push (`S.GameDataChanged`) carries snapshot copies (`ItemInfo.CopyFrom`, `MonsterInfo.ClientInformation`, `UserMagic.CreateClientMagic`), never Main's records. Large lists are split by measuring each element (`LiveDbPush`).
- **ALWAYS** rebuild and deploy client and server from the same commit after any `Shared/` change.
- **ALWAYS** call `PlayerObject.SendAllowToggles()` after any new code path that changes a social allow toggle (`AllowGroup`, `AllowTrade`, `AllowObserve`, `EnableGuildInvite`, `EnableGroupRecall`, `AllowMarriage`, `AllowLoverRecall`, `AllowMentor`, `Info.AllowFriendRequests`; §2.5 "Social allow toggles echo").
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
- **ALWAYS** bounds-check cell scans (`GetCell` throws `IndexOutOfRangeException` outside the map; use `ValidPoint` or the `if (y < 0) continue; if (y >= Height) break;` idiom).
- **ALWAYS** make NPC script pages reachable from `[@MAIN]` buttons/GOTOs (unreachable pages are never parsed), and add new jump keywords to the GOTO detection in `NPCScript.ParseSegment`.
- **ALWAYS** remember gold is per **account** (`player.Account.Gold`), and monster drops are owner-protected for `Settings.Minute`.

### 3.7 Web Admin (backend + frontend)

#### NEVER
- **NEVER** bind beyond loopback without a TLS reverse proxy (plain HTTP, cookies not Secure, no `X-Forwarded-For`).
- **NEVER** serialise raw Crystal objects with `WebAdminJson` (cycles: `CharacterInfo.AccountInfo`, `AccountInfo.Connection`) — project DTOs. **NEVER** return exception text to clients.
- **NEVER** put passwords or hashes in audit `details`.
- **NEVER** register a route shape another module owns (throws, drops the module); keep to your own prefix.
- **NEVER** mutate from the browser without `portalApi`/`OpsUI.post` (adds `X-MirCraft-CSRF`); **NEVER** call it with a relative `api/...` URL (the header is added only when the URL starts with `/api/` → 403).
- **NEVER** set unescaped `innerHTML` (use `OpsUI.esc`/`esc`), store session tokens in `localStorage`, add new CDNs/npm/bundlers/TypeScript/ES modules, hard-code host:port, use sync XHR, or poll faster than the existing ~5 s (exceptions: the Live Monitor snapshot, 1 s, user-approved 2026-10-11; the Dashboard's opt-in Live range, 1 s, user-approved 2026-10-21).
- **NEVER** PUT grid rows without the follow-up `POST /api/admin/database/save`; **NEVER** send partial rows or strip/fabricate `__rev` (`noRev` only from import); **NEVER** treat a 200 PUT as "all saved" — inspect applied/conflicts/validation/ignored fields. **LiveDb exception:** a `POST /api/admin/livedb/batch` sends only `Index` + `__rev` + the changed cells of an edited row (new rows whole), saves by itself (no database/save) and is all-or-nothing; reuse the client batch `id` on a retry; read `rows[].changes[].live` / `restartFields` / `live.outcome` for what reached the running world. **NEVER** revert a LiveDb batch by hand-building rows: use `POST /api/admin/livedb/undo/{id}` (dry run first), which keeps the `revAfter` guards and records `inverseOf`.
- **NEVER** set a placed NPC's `Info.Location` / `Info.MapIndex` directly: `NPCObject.CurrentLocation` reads `Info.Location`, so the NPC must leave its cell first (`LiveDbWorldHooks.RelocateNpc` / `LiveDbNpcMover.Move`: remove → change → add; F10). **NEVER** remove spell objects from a safe zone by area alone: only caster-less, never-expiring zone decorations / healing cells are the zone's (`Map.RemoveSafeZoneSpells`); player spells and conquest war borders stay.
- **NEVER** apply a web edit to `Envir.Main` outside the LiveDb live step (`LiveDbMainApplier` inside one queued admin action, with before-images and rollback); **NEVER** copy Edit objects into Main (clone new rows, as `LiveDbMainApplier.PrepareInsert` does); **NEVER** mark a column live without a `LiveDbClassification` entry and, for `Hooked`, the refresh hook; **NEVER** let the client push throw into the live step or roll it back (it runs last, inside its own try/catch), and keep it inside `[LiveDb] MaxPushRows`.

#### ALWAYS
- **ALWAYS** `WebAdminAudit.Record(req, "area.verb", target, details, reason)` on every successful mutation.
- **ALWAYS** hold `WebAdminGame.EditLock` for `Envir.Edit` access and `WebAdminOptions.SettingsLock` for Settings RMW / `Settings.Save*()`.
- **ALWAYS** keep hook subscribers O(1) and non-blocking (they run on the game thread inside `Envir.Login` / `PlayerObject.Chat`); subscribe inside `Register` behind a static once-guard (`Interlocked.Exchange(ref _hooked, 1)`).
- **ALWAYS** keep routes working while the world is stopped: answer 503 (`data.world`) or use `RunOrInlineAsync`.
- **ALWAYS** treat `[WebAdmin]` keys as process-restart only.
- Frontend: IIFE modules, `app.js` loaded last; `AdminSections.register(id, {load, show, hide})` + `AdminSections.poll`; `makeDbGridEditor` + `DbSave.persist` for grids, `ag-theme-quartz-dark`, `getRowId` = `Index`; `SearchableLookupCellEditor` for FKs; `portalConfirm` for destructive actions; `portalToast` for errors; honour `Retry-After` on 429/503; `localStorage` keys prefixed `mircraft_`; ISO dates (never `/Date(ms)/`).

#### Known debt — existing breaches
- ~~`GameDataRoutes.ApplyLiveMonsters` returns `"Live apply failed: " + ex.Message`~~ — removed 2026-10-10 (LiveDb Batch A): `PUT ?live=1` delegates to the LiveDb engine.

### 3.8 Known defects (found 2026-10-08, not fixed)
- ~~**`C.PurchaseGuildTerritory`** has a class, a client sender and a server handler (`MirConnection.PurchaseGuildTerritory`) but **no `GetClientPacket` case** → never received, and the rest of that read buffer is dropped.~~ — **fixed 2026-10-16 (Scene Lab UI phase 2.1)**: factory case added; the handler (`PlayerObject.PurchaseGuildTerritory`) was reviewed and hardened in the same change: it now needs the player alive at the `[@GUILDTERRITORY]` NPC page within `DataRange`, null-checks `MyGuildRank`, looks the territory up with an index loop, pays the seller only when the owner is not `"None"` and that guild's `GTIndex` matches (no `EndGT` on a guild without a territory), clamps the seller's gold at `uint.MaxValue` and sets `NeedSave` on both guilds. Remaining limits: the wire carries the owner name, not the territory index, so `"None"` always buys the first unowned territory, and unlike the `GTSale` script action there is no on-territory-map requirement.
- **`S.RefineCancel`** has a class and a client handler but no `GetServerPacket` case and no server sender — dead.
- **`S.StorageUnlockResult` / `S.StoragePasswordResult`** are private but `Observable` (default true) → mirrored to spectators.
- **`Settings.Save()` never writes** `[General] GMPassword`, `[Network] MaxPacket`, `[Game] MonsterRecallEnabled/Range/Cooldown` (`Load` reads them) — a change made from WinForms or in code is lost at the next restart. The web admin Config page persists these keys itself (`ConfigRoutes.WriteIni`, `SavedBySettingsSave=false`); otherwise edit `Setup.ini` with the server stopped. `[Game] FishMonster` in Setup.ini is ineffective (Fishing settings own `FishingMonster`; see `Server/WebAdmin/SettingsCatalog.cs`).
- ~~Mob-thread exception → world hang; duplicate ObjectIDs~~ — **fixed 2026-10-10 (roadmap P5)**: per-object catch inside `ThreadLoop` (quarantine), a dead thread is skipped by the barrier (`MobThread.Dead`), `Envir.ObjectID` uses `Interlocked.Increment`.
- **Drop lines ignore the numerator**: `2/10` behaves as `1/10` (`parts[0].Substring(2)` in `DropInfo`). First failed `DropItem` aborts the remaining drops.
- ~~Zero-character accounts dropped at load; stale `Envir.Guilds` after an in-process restart~~ — **fixed 2026-10-10 (roadmap P5)**: accounts are kept (decision #14); `LoadGuilds` clears `Envir.Guilds` first.
- ~~Disconnect/receive race → 24 h IP block~~ — **fixed 2026-10-10 (roadmap P5)**: `ReceiveData` works on locals and returns quietly once `Disconnect` has nulled the queues.
- ~~Status port 3100 hard-coded~~ — **fixed 2026-10-10**: `[Network] StatusPort` / `StatusPortEnabled`.

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

**System data (roadmap P7, since phase 7.3): a new field is an additive SQLite column, no `Envir.Version` bump.** A new public field on a system-data model (`MapInfo`, `ItemInfo`, `MonsterInfo`, `NPCInfo`, `QuestInfo` header, `MagicInfo`, `GameShopItem`, `ConquestInfo` and its children, `RespawnInfo`, `MovementInfo`, `SafeZoneInfo`, `MineZone`, `DragonInfo`) of a registry-supported type (scalar, enum, string, `DateTime`, `Point`, `Stats`, `List<int>`, fixed primitive array) becomes a Game Data column and so a `SystemData.db` column; the writer adds it to existing stores on open with the value a freshly constructed model holds (`SqliteWorldFile.Migrate`, logged as `[Store] Added column`). So: give the field its default in the declaration or the parameterless ctor, mark it `ReadOnly` in `GameDataRegistry` if it must not be edited (an `Exclude` also drops it from storage), and add a value for it to `SyntheticWorld` so the byte round trip and `SqliteStoreTests` cover it. Do **not** change the binary `Save`/ctor of a Server.MirDB-only model (Server.MirDB is import-only; `export-binary` then simply can't carry the new field, which only matters for a rollback). Another kind of field (a list of objects, a new collection) needs its own table in `SqliteWorldCodec` + `SqliteWorldFile`. `meta.schema_version` changes only when a layout or encoding rule changes. **Shared models on the wire** (`ItemInfo`, `GameShopItem`'s packet layout): a field the client needs is a packet change (AGENTS.md packet rules); because `ItemInfo.Save` is also its Server.MirDB layout, such a change still bumps `Envir.Version` as below.

**User data (accounts, characters, guilds, goods, conquest state) — decided (user, 2026-10-08): every new saved field bumps `Envir.Version`.** This fork does not pull upstream Crystal updates, so there is no version-number clash to avoid. Leave `CustomVersion` at 0 and never gate on `LoadCustomVersion`.

```csharp
// Server/MirEnvir/Envir.cs
public const int Version = 121;                       // bump ONCE per change set (was 120; 120 = the 2026-10-20 chat ban)
// Server/MirDatabase/XxxInfo.cs — ctor, appended at the end of the existing reads
if (Envir.LoadVersion >= 121) NewField = reader.ReadInt32();   // or: if (version >= 121) when the ctor takes a version
// XxxInfo.Save — appended at the end, unconditionally
writer.Write(NewField);
```
- New user-data collection: list on `Envir`, write in `SaveAccounts(Stream)`, read in `LoadAccounts` under the same version gate; old files get an empty list. (A new system-data collection is a new table in the SQLite store, above.)
- Shared model field → also a wire change (client reads with `int.MaxValue`).
- Bumping `Version` means older server binaries refuse the DB (`LoadDB` rejects `LoadVersion > Version`), so take a backup before the first run of a new version (Runbook §3).
- Tests: give the field a non-default value in `Server.Persistence.Tests/SyntheticWorld.cs`, add it to `Oracle`'s new-version line filter (`WithoutV119` pattern) so older fixtures prove the default, write `Fixtures/v<new>` with `MIR2_WRITE_FIXTURE=1` (test `Write_fixture_for_current_version_when_requested`) and move the byte-identical re-save test to it. Note the oracle skips `Server.MirEnvir` types (`MailInfo`): assert those fields directly. A field that existed in memory before it was saved (v120 chat ban) is already in older oracles at its default, so it needs no filter: the previous fixture's oracle must equal the upgraded dump.
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
- `Stats` storage (2026-10-22): a packed sorted `long[]`, dense `int[264]` past 24 entries; `Values` is a struct view (`foreach`, `TryGetValue`, `Remove`; raw writes that keep a 0 entry use `Values.Set`). Never change a `Stats` while enumerating it. `Future Optimisation/Stats-Storage-2026-10-22.md`.
- Drop file syntax: `1/<N> <ItemName>[ Q]`, `1/<N> Gold <amount>`, `1/<N> GROUP[*|^]` + `{ ... }`, `#INSERT`. Stored as rows (`DropTables`, Drops-Tables-Plan); the line syntax lives once, in `DropInfo.TryParseLine` / `DragonInfo.DropInfo.TryParseLine`, shared by the text loader and the tables. `MirDbExport check-drops <folder>` proves rows and text load identically. Chance = `Chance / Settings.DropRate`, reduced by `Stat.ItemDropRatePercent` and rarity. `Envir.ReloadDrops()` on the game thread.

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
Forms expose `public Envir Envir => SMain.EditEnvir;`, mutate the Edit lists on control events (`if (ActiveControl != sender) return;`), and call `Envir.SaveDB()` in `FormClosed` (sets `SystemDataStale`). That save is **Known debt** (unsynchronised `Server.MirDB` writer, no `LoadLock`/`EditLock`; `GameDataSave` is `internal` to Server.Library): do not copy it. New editors go in the web admin (§4.10). When changing an existing form, add no new `SaveDB()` call sites or save triggers; the only call a form may make into `Envir.Main` is `Envir.Main.QueueAdminAction(...)` — never touch Main's lists or objects directly. Editors are modal (`ShowDialog`); open an editor that edits `Envir.Edit` through `SMain.ShowEditor(form)` so `Envir.EditorFormsOpen` counts it (LiveDb refuses web batches while one is open). `InterfaceTimer` is a WinForms timer and keeps ticking under the modal loop. A web-admin shutdown is a `BeginInvoke(mainForm.Close)` on the UI thread, so its behaviour while a dialog is open is untested.

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

| Loop profiler (roadmap P6) | `Server/MirEnvir/LoopProfiler.cs`; `loopProfile` on the web admin `/status` JSON; `[Diag] LogLoopProfile` | Per-second iterations, busy %, ms per phase (conn, mob, obj, maps, envir, admin, saves, park), packets and bytes in/out, allocation, max iteration; with DueScheduler + Multithreaded also `mobThreads` (per mob thread: busy ms, passes, 10 ms overruns, slowest pass, AI runs, distinct awake monsters, queued, lateness; counters on `MobThread`, written by the owning thread, published by `Envir.PublishMobLoad` while the threads are parked) and `monstersAwake` (`MapObject.ProfileWindow` stamp) |
| Thread CPU (2026-10-21) | `Server/WebAdmin/WebAdminThreadCpu.cs`; `threadCpu` on `/api/admin/host` | Game thread and each mob thread as % of one core over at least 5 s, matched by OS thread id (`Envir.GameThreadNativeId`, `MobThread.NativeThreadId`), plus the rest of the process; Windows only. Dashboard history (`StatusSample`) carries all load fields; `?live=1` serves the 15 min 1 s ring |
| Isolated errors (roadmap P5) | `Server/MirEnvir/WorldErrors.cs`; `isolatedErrors` / `recentIsolatedErrors` on `/status`; `[World] Isolated error` lines in the Server log | One failing connection or object is disconnected or quarantined instead of stopping the world |
| Crash files (roadmap P2) | `Server/CrashReporter.cs` → `Logs\Crash\crash-*.txt` next to the exe | Unhandled and unobserved-task exceptions |

No `Errors.txt` and no per-packet diagnostics (contrast WoOOLcraft). Baseline numbers and the regression script: `Knowledgebase/Future Optimisation/`, `tools/run-baseline.ps1`.

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
- **NEVER** run `dotnet build Server.MirForms\Server.csproj` without `-o <scratch dir>`: output goes to `Build\Server\Debug\` or `Build\Server\Release\` (Release is the user's **live run folder**) (real `Server.MirDB`/`Server.MirADB`, `Configs\`, `Envir\`, `Maps\`, `Logs\`). A build there also overwrites `log4net.config` and `Localization\*.json` (`CopyToOutputDirectory=Always`) and `wwwroot\` (PreserveNewest). **Never touch `Build\`.**
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
Also `Shared.Tests` (packet factory cases, round trips, append-only `packet-manifest.txt`), `Server.Persistence.Tests` (DB byte harness, oracle, v117/v118/v119/v120 fixtures, crash-safe saves, crypto) and `tools/MirDbExport.Tests`. `powershell -ExecutionPolicy Bypass -File tools\verify.ps1` builds every project to `%TEMP%` and runs all of them. Load tests: `tools/start-scratch-server.ps1` + StressClient, or `tools/run-baseline.ps1` (`Knowledgebase/StressClient/stressclient.md`).

Web admin tests are safe (they reference Server.Library + Shared only and write nothing to `Build\`). Tests that change statics (hooks, `LoginGate`, `WebAdminGame.World`, admins, pending, audit, Settings) go in `[Collection(WebAdminGlobalsCollection.Name)]` (`GameBridgeTests.cs`) and restore them; read `Settings` only via reflection metadata (its static ctor opens `Configs\Setup.ini`). `GameDataFrontendTests` needs the repo layout. There are no game-logic tests.

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
