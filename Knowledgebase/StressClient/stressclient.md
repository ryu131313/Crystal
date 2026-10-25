# StressClient and the scratch server (roadmap P6)

> **What:** headless load-test bots (`StressClient\`) plus a synthetic world generator (`tools\scratch-world\`) and start/stop scripts (`tools\start-scratch-server.ps1`, `tools\stop-scratch-server.ps1`) for a **scratch** `Server.exe`.
> **Status:** committed 2026-10-10; **reworked 2026-10-21** into a WoOOLcraft-style harness (worker-pump orchestrator, per-bot `WorldModel`, priority brain, A* pathfinding on real map collision, Director routing). In `Legend of Mir.sln` and `tools\verify.ps1` (keys `StressClient`, `ScratchWorld`, test project `StressClient.Tests`).
> **Safety:** scratch only. Never point the bots at a live server, and never copy the load-test `Setup.ini` to a real server.

---

## 1. Pieces

| Piece | Path | Builds to | What it does |
|---|---|---|---|
| StressClient | `StressClient\StressClient.csproj` (net10.0 console, refs `Shared`) | `StressClient\bin\Debug\net10.0\` | N bots: login FSM, world model, A* movement, hunt/roam, 1 Hz metrics, CSV, Director |
| StressClient.Tests | `StressClient.Tests\StressClient.Tests.csproj` (xUnit, refs StressClient + Shared) | own `bin\` | Map-parser fixtures (type 0/100, walls, doors, truncation) and A* tests |
| ScratchWorld | `tools\scratch-world\ScratchWorld.csproj` (net10.0 console, refs `Server.Library` + `Shared`; links `StressClient\MapFile.cs`) | `tools\scratch-world\bin\Debug\net10.0\` | Writes `Maps\*.map` + `Server.MirDB` into a folder; synthetic maps **or copies of real server maps** |
| start script | `tools\start-scratch-server.ps1` | `%TEMP%\mir2-p6-server\{build,run}` | Build Server.exe to scratch, assemble the run folder, write the load-test `Setup.ini`, start minimized, wait for `Network Started.` |
| stop script | `tools\stop-scratch-server.ps1` | — | Stops only the PID in `server.pid`, after checking the exe lives in the scratch folder |

None of them reads or writes `Build\`. The generator and the start script refuse a target inside `Build\`; the bots only **read** map files (`--map-folder`).

## 2. Quick start

```powershell
# 1. scratch server on 127.0.0.1:17000 (fresh synthetic world; builds Server.exe into %TEMP%\mir2-p6-server\build)
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\start-scratch-server.ps1 -Port 17000 -Reset

# 2. bots (A* on the scratch map)
dotnet build C:\Mir2\StressClient\StressClient.csproj
C:\Mir2\StressClient\bin\Debug\net10.0\StressClient.exe --port 17000 --bots 200 --ramp 20 --duration 60 `
    --map-folder "%TEMP%\mir2-p6-server\run\Maps" --csv %TEMP%\run200.csv

# 3. stop (graceful CloseMainWindow, then kill after 20 s)
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\stop-scratch-server.ps1
```

Start-script options: `-Scratch <dir>` (default `%TEMP%\mir2-p6-server`), `-Port` (use 17000-17999), `-MaxUser` (600), `-LoopbackMaxPacket` (400), `-MapSize` (200), `-Maps` (1), `-Reset` (regenerate the world, delete accounts/logs/backups in the run folder), `-NoBuild`, `-Multithreaded $false`.
Without `-Reset` the run folder keeps its accounts, so a second bot run exercises the login path (bots log in instead of creating accounts).

Check a run: `run\Logs\Server\Server (dd-MM-yyyy).log` must have no `[World] Isolated error`, no `FATAL`, and `run\Logs\Crash\` must not exist. Bots exit politely (`C.Disconnect`, server logs "User closed game").

### Real terrain (A* proof)

Synthetic maps are flat, so they only prove the plumbing. To exercise collision and A*:

```powershell
# build once, start + stop to assemble the run folder
powershell -File tools\start-scratch-server.ps1 -Scratch "$env:TEMP\mir2-p7-real" -Port 17011 -Reset
powershell -File tools\stop-scratch-server.ps1 -Scratch "$env:TEMP\mir2-p7-real"

# replace the synthetic world with 4 copies of real server maps
$run = "$env:TEMP\mir2-p7-real\run"
Remove-Item "$run\Maps","$run\Server.MirDB","$run\SystemData.db" -Recurse -Force -ErrorAction SilentlyContinue
tools\scratch-world\bin\Debug\net10.0\ScratchWorld.exe --out $run --maps-from C:\Mir2\Build\Server\Release\Maps --maps 4 --force

powershell -File tools\start-scratch-server.ps1 -Scratch "$env:TEMP\mir2-p7-real" -Port 17011 -NoBuild
StressClient.exe --port 17011 --bots 40 --ramp 20 --duration 45 --map-folder "$run\Maps"
```

`ScratchWorld --maps-from` copies the files (source untouched), picks a walkable start cell with the same parser the bots use, and creates `MapInfo`/safe zone/respawns for each. `--map`/`--size` are ignored in that mode; `--maps N` selects N files spread across the folder's alphabetical list.

## 3. The synthetic world

- **Map:** the oldest Crystal format (`Map.FindType` → 0, `LoadMapCellsv0`): a 52-byte header (`Int16` width, `Int16` height, rest zero), then 12 zero bytes per cell, column-major. All-zero cells are walkable floor, no doors, no lights. The width's low byte must not be 0x00/0x0D/0x0F/0x10/0x15 (other formats' signatures); the generator checks this.
- **Server.MirDB:** written by the real `Envir.Main.SaveDB()` with cwd = the output folder (`Envir` paths are cwd-relative). One map `scratch0` (or `--maps N` / start-script `-Maps N`: `scratch0..N-1`, each with its own start point; new characters pick a random start point, so bots spread across maps) with a `StartPoint` safe zone (size 5) in the centre; `ScratchHen` (AI 1, Deer: wanders, 40 HP) and `ScratchOma` (AI 0, 120 HP) respawning across the map; start items `WoodenSword` (DC 2-5), `BaseDress(M)`, `(HP)DrugSmall`, plus `Gold` and `BlackIronOre`.
- **Real maps (`--maps-from <folder> [--maps N]`):** copies N `.map` files into `<out>\Maps`, picks a walkable start cell (expanding-ring search from the map centre using `StressClient\MapFile.cs`), and adds the same two monster types with respawns there. A map the parser cannot read falls back to the centre and prints a warning.
- **Bot content:** every world also gets a merchant NPC (`Envir\NPCs\scratchtrader.txt`: buy/sell, repair, storage, quest links), one kill quest (`Envir\Quests\scratchhunt.txt`: kill a Scratch Oma for 200 exp / 500 gold) and a `ScratchJunk` start item to sell. The Oma respawn concentrated within 20 cells of the start point so bots find fights and finish the quest without long walks.
- `EnforceDBChecks=False` is required: the world has none of the ~50 special monsters `Envir.CanStartEnvir` otherwise demands.

## 4. The load-test profile (`run\Configs\Setup.ini`)

```ini
[General]  CheckVersion=False  EnforceDBChecks=False  Multithreaded=True  ThreadLimit=2
[Network]  IPAddress=127.0.0.1  Port=17000  MaxUser=600  MaxIP=5  MaxPacket=50  IPBlockSeconds=5  StartHTTPService=False
[LoadTest] ExemptLoopback=True  LoopbackMaxPacket=400
[Permission] AllowNewAccount=True  AllowLogin=True  AllowNewCharacter=True  AllowStartGame=True
[WebAdmin] Enabled=False
```

- `[LoadTest] ExemptLoopback=True` exempts 127.x/::1 from the connect IP block, `MaxIP`, and the per-IP account (>2/h) and character (>4/h) 24 h blocks. **Never on a real server**: any local proxy or tunnel would make every player "loopback".
- `MaxPacket` stays enforced. It counts **socket receives per connection per 5 s window** (not packets); a breach IP-blocks for 24 h. `LoopbackMaxPacket` raises it for loopback only.
- `CheckVersion=False` because bots send a dummy hash. `AllowStartGame=True` because it defaults to false (admins only).
- The status listener (`[Network] StatusPortEnabled` / `StatusPort`, default on / 3100) is turned **off** in the scratch profile, so a scratch server never blocks a live server from binding it.

## 5. StressClient

### Architecture

```
Program.Main
  ├─ --check-maps <dir>   parse every map file, print coverage, exit 0/1
  └─ run                  Orchestrator.Run()
        ├─ Ramp thread          release bots at --ramp/s
        ├─ Worker threads ×N    blocking ticks every --tick (default 50 ms), bots partitioned i % N
        ├─ Printer             1 Hz metrics line + optional --csv
        └─ Director            every --director-interval (3 s): route bots that asked for a destination
```

Per bot: `VirtualPlayer` (login FSM, session, movement bookkeeping), `WorldModel` (self + objects + map + inventory/equipment/magics/quests), `BotBrain` (priority FSM), `Navigator` (path index). Socket I/O is **not** on a worker: the existing `BotSocket` receive loop decodes and queues, and a per-session send task drains the outbox through the per-bot token bucket. One bot is touched by exactly one worker; the only cross-thread handoffs are the received-packet queue, the send queue, the volatile `Directive` and `Interlocked` metrics. `MapManager`/`WorldDirectory` are shared and lock internally.

A worker wraps every `Tick` in a try/catch (one bot's exception counts `err` and leaves the other bots on that worker alone). Shutdown is non-blocking: workers stop, every bot queues `C.Disconnect`, its send loop flushes the queue and closes the socket, and the orchestrator waits up to 3 s before force-closing stragglers.

**Protocol.** Real `Shared` codec only: `Packet.IsServer = false`, frames split on the 2-byte length prefix and decoded with `Packet.ReceivePacket`, sends serialised with `Packet.GetPacketBytes`. Rebuild it after every `Shared/` change (a stale build reads packets wrong; `decode` and `unk` count that).

**Login state machine** (mirrors `Client/MirScenes/LoginScene.cs` / `SelectScene.cs`):
`S.Connected` → `C.ClientVersion` → `S.ClientVersion(1)` → `C.Login` → (`S.Login(3)` → `C.NewAccount` → `S.NewAccount(8|7)` → `C.Login`) → `S.LoginSuccess` → (`C.NewCharacter` → `S.NewCharacterSuccess`) → `C.StartGame` → `S.StartGame(4)` + `S.UserInformation` = in game. The current map arrives as `S.MapInformation` on first entry and `S.MapChanged` on every teleport.
Identities are deterministic: account `<prefix><index:D5>` (e.g. `sb00042`), character `<prefix>c<index:D5>`, one password for all. `--first-index` runs several fleets with one prefix.

**World model.** Mirrors `S.ObjectMonster/ObjectPlayer/ObjectNPC/ObjectItem/ObjectGold`, movement (`ObjectWalk/Run/Turn`), health (`S.HealthChanged`, `S.ObjectHealth`), XP/level/gold, doors (`S.Opendoor`), map facts (`S.MapInformation`, `S.MapChanged`, `S.NewMapInfo`, `S.MapDetails`) and monster/item info (`S.NewMonsterInfo`, `S.NewItemInfo`). It is deliberately tolerant: unknown/ignored packets are dropped silently (the switch has no default arm), as in the real client.

**Map data + A*.** `StressClient\MapFile.cs` is a parser port of the server's loader (`Server/MirEnvir/Map.cs`): same `FindType` detection and the same wall/door rules for formats 0-7 and 100, collision only (no tile art). `MapManager` indexes the `--map-folder` roots once (case-insensitive, recursive) and caches `MapGrid`s by the `S.MapChanged.FileName`; a missing map falls back to greedy movement and counts `maps missing`. `PathFinder` is a bounded 8-direction A* (octile 10/14, `--path-nodes` expansion cap, default 4000) that consults terrain, live players/monsters/NPCs (except the current target) and cells whose move the server recently refused (5 s avoid list).

**Movement.** One move in flight; the bot waits for `S.UserLocation` before the next step (never fills `MirConnection._retryList`). On the ack it advances the path; if the server echoed the pre-move cell the step is refused → count `blocked`, avoid that cell briefly, repath. Doors: a door cell is blocked until `S.Opendoor`; the bot sends `C.Opendoor` when within the server's 4-cell range. `C.Run` is used on straight segments (`--run-percent`, default 40 %; `--no-run` disables), capped at 8 consecutive runs because the server drains 1 HP every 8 runs after 10. `--map-folder` missing ⇒ the old greedy roam (random heading, turn when blocked) still drives load.

**Brain.** Priority, early-returning: dead → `C.TownRevive` → not in game → **potion** (`C.UseItem` below 55 % HP) → **quest housekeeping** (every 5 s) → **town run** (quests / buy / sell / repair) → equip a better weapon/armour → loot → hunt (quest targets first) → social → Director directive → roam. Hunting never targets pets (`MasterObjectId != 0`) or town guards (AI 6/58 `Guard`, 57 `TownArcher`), not even when they hit the bot (`MonsterState.Huntable`); it prefers the monster that just struck us (`S.Struck`/`S.ObjectStruck`), skips monsters more than 5 levels above the bot (`ClientMonsterInfo.Level`), casts a learned offensive spell when out of melee reach (`C.Magic`, MP + `S.MagicDelay`/`ClientMagic.Delay` cooldown), and attacks facing the target.

**Town runs (economy).** Triggered by junk in the bag, damaged equipment, fewer than 3 potions, a Ready quest, or an NPC in view offering a quest the bot would take. The bot paths to the NPC that finishes its Ready quest (else one offering a quest, else the nearest; if the finisher is out of view, to where it was last seen, else to its spawn/home point) and re-acquires it, sends `C.CallNPC {Key="[@MAIN]"}` (the server ignores any other opening key), does its quest requests there (below), opens `[@BUYSELL]`, sells junk with `C.SellItem` (one at a time, waiting for `S.SellItem`; the server removes the item itself, it does **not** send `S.DeleteItem`), buys HP potions with `C.BuyItem` (goods `UniqueID` from `S.NPCGoods`), repairs with `C.RepairItem` after `[@REPAIR]`, then walks away. `--no-town` keeps the trip for the quest requests only. Inventory is mirrored from `S.GainedItem`, `S.MoveItem`, `S.EquipItem`, `S.DeleteItem`, `S.SellItem`, `S.ItemRepaired`; item stats are resolved from `S.NewItemInfo`.

**Quests (V2 protocol, lane H 2026-10-25).** Per-bot `QuestBook` in the `WorldModel` (worker-owned): definitions from `S.QuestDefinition` (sent on demand for active quests and for quests offered by NPCs in view), runs from `S.QuestStates` (Full at login, then changes; status None = run ended) and `S.QuestProgress`, history ids from `S.QuestHistory`, one entry per NPC in view from `S.QuestAvailability` (an upsert; dropped when the NPC leaves view or the map changes; the server re-sends it), and `S.QuestOffer` prompts. Decisions are pure functions in `QuestLogic` (tested in `StressClient.Tests\QuestLogicTests.cs`):
- **Doable objectives:** `Kill` of a `Monster` or `MonsterGroup`, `Collect` of an item / item group (quest drops come from hunting; the server counts the quest bag), `ReachLevel`. Talk, area, use item, craft, gather, skill, counter, flag and reserved types are skipped. A quest is doable when every step can complete (All / Any / N-of, optional objectives ignored).
- **Offer choice:** at the NPC, the offer with a known definition, doable, not run or pending, `LevelHint ≤ level + 5`, lowest `LevelHint` first; at most 3 runs per bot (`QuestBook.MaxActive`). `C.QuestAccept { Quest, NpcObjectID }`; an `S.QuestOffer` (item / area / auto / share) is accepted with `NpcObjectID = 0`.
- **Hunting:** unmet `Kill` objectives of each Active run's current step become target names: a `Monster` id through `S.NewMonsterInfo` (`GameName`, which is what `S.ObjectMonster.Name` carries), a `MonsterGroup` through its `TargetMembers` (member monster ids, named through `S.NewMonsterInfo` when known) and, as a fallback for members whose info the bot lacks, its `TargetName` label (the server names the group after its first member and sends every member's info). A target counts 6 cells closer when picking what to hunt; the Director prefers a respawn of a quest monster within 60 cells (`IdleRequest.QuestMonsters`, an immutable array).
- **Turn-in:** at an NPC whose availability lists the Ready run under `Finishing` (a quest without a Finish endpoint is listed at its Start NPC): `C.QuestTurnIn` with one pick per choice group of the Quest-scope rewards (item over currency, then the larger amount, then the first), at most 6 requests per visit.
- **Abandon:** a Failed run at once; an Active run with no progress for `--quest-stuck` seconds (default 300) or whose current step the bot cannot do; never a Ready run, a run whose definition forbids abandoning, or one asked once already. A refused request (`S.QuestRefused`) clears its pending entry at once and counts `refused` in the summary's quest numbers; a refused accept or turn-in is not retried for 15 s. Unanswered accept / turn-in / abandon requests expire after 15 s.
- **Metrics:** `quests N accepted (M sent)/T handed in/D done/A abandoned`; accepted = confirmed by `S.QuestStates`, done = the run removed after a turn-in. `@status` adds `questsAbandoned`.

**Social.** Local chat via `C.Chat` on a per-bot timer, party invites to a nearby player via `C.AddMember`, and automatic acceptance of `S.GroupInvite` (`C.GroupInvite { AcceptInvite = true }`). Group membership is tracked from `S.AddMember`/`S.DeleteMember`/`S.DeleteGroup`.

**Director.** Idle bots enqueue an `IdleRequest` (map, file name, position); the Director answers with an immutable `Directive`. Route data comes from packets the server already sends clients: `S.MapDetails` (`C.RequestMapDetails`: respawn monster/location/spread/count + safe zones) and `S.NewMapInfo` (portal exits, `ShowOnBigMap` only). Directives prefer a respawn region near the bot and clamp the offset (≤ 20 cells) so a town run never becomes a cross-map walk; exits are only chosen within 30 cells. `--no-director` disables it.

**Throttle.** A per-bot token bucket in the send task keeps each bot at ≤ 60 % of `--max-packet` per 5 s (default 50). A bot normally sends ~1-2 writes/s.

**Fleet.** `--ramp` bots/s; `--connect-budget` (25) bots at most between TCP connect and in-game; `--workers` (4) sweeps the population. Disconnected bots reconnect with backoff unless `--no-reconnect`. `--login-timeout` (30 s) closes a bot stuck before the game. StressClient runs on server GC (P9-14): with workstation GC a 4k-bot crowd spent half its time in GC pauses and its sends collapsed. A bot holds its connect-budget slot from TCP connect until in game (about 6 server round trips), so when the server loop slows, joins/s ≈ budget ÷ login time: a drip of a few bots/s is the server's latency, not a client fault.

**Metrics line (1 Hz):** `conn`, `connecting`, `in-game`, `reached`, packets/s and KB/s out/in, walk/run/attack/blocked per second, kills, deaths, path found/fail/runs/maps missing, KeepAlive RTT, and cumulative `err`, `decode`, `unk`, `refused`, `disc`, `timeout`. When any in-game bot has not received a packet (or completed a write) for 5 s, an extra `liveness:` line names the count — a stall warning, not a normal idle bot. The summary adds `economy` (potions, casts, pickups, sold, bought, repairs, quests accepted/handed in/done, chats, invites, groups) and `npc` (calls, pages, goods, sell pages, sales ok/failed). `--csv` writes the same per second, then the movement columns (`maps_loaded,maps_missing,path_found,path_fail,repaths,runs,doors_opened,move_timeouts,directives`); the original columns are unchanged.

**Exit code:** 0 when every bot reached the game and there were no decode errors, 1 otherwise, 2 for bad arguments. `--check-maps` exits 0 when every file parses.

## 6. Results (2026-10-21, scratch server, this machine)

| Run | Reached game | Maps / paths | Movement | Errors / refused / disconnects | Server log |
|---|---|---|---|---|---|
| 30 bots, ramp 10/s, 40 s, synthetic 200×200 | 30/30 in 4 s | 1 map, 0 paths (run predates the `S.MapInformation` fix) | 864 walks, 450 blocked (collisions at spawn) | 0 / 0 / 0 | clean |
| 40 bots, ramp 20/s, 45 s, 4 copied real maps (700×700 type 100, 100×100 type 100, 250×250 type 1, 400×400 type 1) | 40/40 | 4 maps, 306 paths found / 42 failed | 1442 walks, 524 runs, 68 blocked, 48 kills, RTT 1.0 ms | 0 / 0 / 0 | clean (0 isolated errors, no FATAL, no crash files) |
| 3 bots, ramp 3/s, 150 s, synthetic world with merchant + quest | 3/3 | 1 map, 35 paths | 3 sold, 3 potions bought, 3 quests accepted; kill task reached `Completed`, hand-in packets sent | 0 / 0 / 0 | clean |
| 2026-10-25, V2 quests (lane H): 20 bots, ramp 10/s, 200 s, `--quest-stuck 120`, synthetic world (second start) | 20/20 | 1 map, 473 paths found / 2 failed | 286 kills; quests 24 accepted / 15 handed in / 15 done / 5 abandoned (no Oma kill within 120 s; 4 re-accepted) | 0 / 0 / 0 | clean (no isolated errors, FATAL or crash files); loop busy avg 0.9 %, max 7.3 %; 3 iterations of 20-50 ms, all in the login burst of a fresh world or at the midnight day change. A/B, 20 new bots × 75 s each: quests on max iteration 7.1 ms, quests off 12.1 ms |

`--check-maps C:\Mir2\Build\Server\Release\Maps`: **1705 files parsed, 0 failed**, formats type 0 ×237, 1 ×1442, 3 ×1, 5 ×6, 100 ×19. The 42 path failures are goals inside unreachable pockets on real maps (random roam points); the bot repicks.

Older numbers (2026-10-10, first harness) are not comparable: that bot had no world model and no pathfinding.

## 7. Known limits

- Console, or the web admin Tools page (§9). Behaviour now covers potions, threat response, level-band targeting, learned spells, loot pickup, gear upgrades, merchant runs (sell/buy/repair), quest accept/hand-in, chat and party invites. Still missing: trade, mail, market/game shop, storage transfers, crafting/refine, guilds, and any spells the scratch bots never learn.
- Bots are all Warriors and fight with `C.Attack`/`Spell.None`; `C.Magic` is wired but never exercised because the scratch world grants no spells.
- Quests: the scratch world has one quest (a single Scratch Oma kill; `ScratchWorld --maps-from` adds the same content, not the real server's quests). A fresh scratch server needs a second start before its quest definitions are live (start, stop, start). Bots match a `MonsterGroup` by its member ids (`S.QuestDefinition.Objective.TargetMembers`, wave 3 kick-off) and its label name; `Collect` objectives are pursued by plain hunting (the bot does not know which monster drops the item).
- The scratch merchant's script lives in `Envir\NPCs` (file-based); quest bodies are imported into `SystemData.db`.
- The portal graph is only what the server sends every client (`ShowOnBigMap` exits); maps without such exits cannot be routed between.
- Doors are opened with `C.Opendoor` (4-cell server range); no door-state memory across map changes.
- Running costs HP after 10 runs server-side; the bot caps itself at 8.
- A* is capped at 4000 expansions; long cross-map paths may fail and are retried.
- RTT is KeepAlive only (game-loop latency plus loopback), not per-action latency.
- At shutdown the scratch server sometimes logs "Connection timed out" instead of "User closed game" for bots whose `C.Disconnect` races the socket close. The in-run liveness check shows no stalls; it is a log-reason artefact, not a lost session.
- `kills` counts monsters whose death the bot saw after attacking them last; shared kills are under-counted.
- The bots' address is 127.0.0.1, so a real-network test needs the server bound elsewhere and **no** `ExemptLoopback` benefit: expect per-IP limits.

## 8. Baseline, A/B and soak scripts

- `tools\run-baseline.ps1`: idle (Multithreaded on/off) plus 50/200/500 bots, 60 s windows; markdown report with the `[Diag] Loop` averages. `-Bots`, `-NoIdle`, `-ExtraIni '[Section]','Key=Value'` for A/B of an `[Optimisation]` switch, `-Label`, `-Out`.
- `tools\run-soak.ps1`: fresh world with `-Maps` maps, `-Bots` bots for `-Minutes`. It samples the server every `-SampleSeconds` (CPU, working set, private bytes, handles, threads) into a CSV next to the report, and reports the memory slope after a 10-minute warm-up, the first vs last 10 minutes of the loop profile, isolated errors, FATAL lines, crash files, EncodeOnce warnings and the bot summary. Defaults: 300 bots, 4 maps, 60 min.
- Both scripts still work unchanged (they pass no new flags; the summary line and CSV prefix are compatible). Neither passes `--map-folder`, so they run the greedy fallback unless the script is extended.
- Results live in `Knowledgebase\Future Optimisation\` (`Baseline-*.md`, `Soak-*.md`, `P9-NN-*.md`). CPU figures are only comparable on a quiet machine; memory, handles, errors and bot results hold regardless.

## 9. Web admin Tools page (2026-10-21)

The MirCraft panel's **Monitoring → Tools** page runs the bots and a scratch server from the browser (`Server/WebAdmin/README.md` §3, server.md §2.7 "Tools").

- **Bots** run as a `StressClient.exe` child of `Server.exe`, built from the checkout (`StressClient\bin\{Debug,Release}\net10.0`, newest wins; *Build bots* runs `dotnet build -c Debug`). The page passes `--control-stdin --status-json` plus the form's options; the bot password goes in the `STRESSCLIENT_PASSWORD` environment variable (read before the arguments, `--password` wins), so it never shows in the process list.
- **Scratch server** = this doc's scripts with `-Scratch %TEMP%\mir2-webadmin-scratch`. *World: copies of this server's maps* passes `-MapsFrom <server Maps>` (ScratchWorld `--maps-from`, read-only copy) and, like `-Maps`, applies only when the world is generated (*New world (reset)* or the first start).
- **Target "This server"** points the bots at the server hosting the panel. It is refused unless that server has `[LoadTest] ExemptLoopback=True`, `CheckVersion=False`, logins and StartGame allowed, a loopback/any bind and a non-default bot password; the bot accounts then live in its account DB.
- **Passwords and bans.** The server bans an account for 2 minutes after 6 wrong passwords (`Envir` login, "too many wrong login attempts"). A run that reuses an earlier run's prefix with another password used to re-ban every existing bot account in a loop. Now a bot stops for the rest of the run on a wrong password (`S.Login` Result 4) or `S.LoginBanned`, and the Tools page checks the run's existing accounts first (one game-thread read, up to 3 hashes verified on the HTTP thread): a different password or a current ban is refused with 409 before any bot connects.
- **Rule (user, 2026-10-21):** the self target is for the operator only. **Agents test and demo bots against scratch servers** (a panel hosted by a scratch server may use its own self target); never against the live server, and never turn on its load-test settings, unless the user explicitly asks in that conversation.

New StressClient options for this (also usable from the console):

| Option | Effect |
|---|---|
| `--no-hunt` `--no-loot` `--no-town` `--no-quests` `--no-potions` `--no-equip` `--no-chat` `--no-party` | Turn one brain branch off. `--no-town` keeps quest-only merchant trips (accept / hand in, no shop or repair) unless `--no-quests`; `--no-party` also declines `S.GroupInvite`. With everything off the bots only roam (plus Director routing unless `--no-director`). |
| `--quest-stuck <s>` | Abandon an active quest after this many seconds without progress (default 300, 10-86400). |
| `--class warrior\|wizard\|taoist\|assassin\|archer\|mixed` | Class of NEW characters (mixed = bot index mod 5). Existing characters keep theirs; every class still fights with `C.Attack`. When the server disables Assassin/Archer (`S.ServerRules` flags, or a `S.NewCharacter` Result=3 refusal) the bot retries at once with an allowed class: mixed → Warrior/Wizard/Taoist by index, a fixed class → Warrior. The Tools page refuses an explicit Assassin/Archer run on "This server" when it is disabled there. |
| `--duration 0` | Run until stopped (needs `--control-stdin`). |
| `--control-stdin` | A `stop` line, or end of stdin (parent closed it or died), stops the run gracefully. |
| `--status-json` | One `@status {json}` line per second (gauges, rates, cumulative economy/problem counters, `silent`). |

Smoke test (2026-10-21, scratch only): a panel on a scratch server (web admin 5691) started a second scratch server (2 synthetic maps) from the page, then 30 mixed-class bots with town/chat/quests off: 30/30 in game, 31 kills in 25 s, 0 sold / 0 chats, 0 errors; Stop → polite exit 0, `RESULT: PASS`. Self target on the panel's own scratch server: 10/10 in game, visible in Players Online; default password and bad input refused with 400. No isolated errors, FATAL or crash files; the password is in neither the audit trail nor the server log.
