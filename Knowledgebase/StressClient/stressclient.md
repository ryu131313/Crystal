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

**Brain.** Priority, early-returning: dead → `C.TownRevive` → not in game → **potion** (`C.UseItem` below 55 % HP) → **town run** (quests / buy / sell / repair) → equip a better weapon/armour → loot → hunt → social → Director directive → roam. Hunting prefers the monster that just struck us (`S.Struck`/`S.ObjectStruck`), skips monsters more than 5 levels above the bot (`ClientMonsterInfo.Level`), casts a learned offensive spell when out of melee reach (`C.Magic`, MP + `S.MagicDelay`/`ClientMagic.Delay` cooldown), and attacks facing the target.

**Town runs (economy).** Triggered by junk in the bag, damaged equipment, fewer than 3 potions, or an accepted quest old enough to hand in. The bot paths to the merchant (or to its spawn/home point and re-acquires it), sends `C.CallNPC {Key="[@MAIN]"}` (the server ignores any other opening key), accepts an offered quest via `C.AcceptQuest`, opens `[@BUYSELL]`, sells junk with `C.SellItem` (one at a time, waiting for `S.SellItem`; the server removes the item itself, it does **not** send `S.DeleteItem`), buys HP potions with `C.BuyItem` (goods `UniqueID` from `S.NPCGoods`), repairs with `C.RepairItem` after `[@REPAIR]`, then hands in the quest with `C.FinishQuest` and walks away. Inventory is mirrored from `S.GainedItem`, `S.MoveItem`, `S.EquipItem`, `S.DeleteItem`, `S.SellItem`, `S.ItemRepaired`; item stats are resolved from `S.NewItemInfo`.

**Quests.** `S.NewQuestInfo` marks the NPC's offers, `S.ChangeQuest` tracks task state, `S.CompleteQuest` marks handed-in quests. Kill tasks must resolve to a monster at load time: `SqliteWorldCodec` used to call `QuestInfo.LoadInfo()` before the item/monster tables were swapped into `Envir`, so a store-loaded quest lost its kill tasks (the client saw `completed=True` with an empty task list). It now defers that call until after the lists exist.

**Social.** Local chat via `C.Chat` on a per-bot timer, party invites to a nearby player via `C.AddMember`, and automatic acceptance of `S.GroupInvite` (`C.GroupInvite { AcceptInvite = true }`). Group membership is tracked from `S.AddMember`/`S.DeleteMember`/`S.DeleteGroup`.

**Director.** Idle bots enqueue an `IdleRequest` (map, file name, position); the Director answers with an immutable `Directive`. Route data comes from packets the server already sends clients: `S.MapDetails` (`C.RequestMapDetails`: respawn monster/location/spread/count + safe zones) and `S.NewMapInfo` (portal exits, `ShowOnBigMap` only). Directives prefer a respawn region near the bot and clamp the offset (≤ 20 cells) so a town run never becomes a cross-map walk; exits are only chosen within 30 cells. `--no-director` disables it.

**Throttle.** A per-bot token bucket in the send task keeps each bot at ≤ 60 % of `--max-packet` per 5 s (default 50). A bot normally sends ~1-2 writes/s.

**Fleet.** `--ramp` bots/s; `--connect-budget` (25) bots at most between TCP connect and in-game; `--workers` (4) sweeps the population. Disconnected bots reconnect with backoff unless `--no-reconnect`. `--login-timeout` (30 s) closes a bot stuck before the game.

**Metrics line (1 Hz):** `conn`, `connecting`, `in-game`, `reached`, packets/s and KB/s out/in, walk/run/attack/blocked per second, kills, deaths, path found/fail/runs/maps missing, KeepAlive RTT, and cumulative `err`, `decode`, `unk`, `refused`, `disc`, `timeout`. When any in-game bot has not received a packet (or completed a write) for 5 s, an extra `liveness:` line names the count — a stall warning, not a normal idle bot. The summary adds `economy` (potions, casts, pickups, sold, bought, repairs, quests accepted/handed in/done, chats, invites, groups) and `npc` (calls, pages, goods, sell pages, sales ok/failed). `--csv` writes the same per second, then the movement columns (`maps_loaded,maps_missing,path_found,path_fail,repaths,runs,doors_opened,move_timeouts,directives`); the original columns are unchanged.

**Exit code:** 0 when every bot reached the game and there were no decode errors, 1 otherwise, 2 for bad arguments. `--check-maps` exits 0 when every file parses.

## 6. Results (2026-10-21, scratch server, this machine)

| Run | Reached game | Maps / paths | Movement | Errors / refused / disconnects | Server log |
|---|---|---|---|---|---|
| 30 bots, ramp 10/s, 40 s, synthetic 200×200 | 30/30 in 4 s | 1 map, 0 paths (run predates the `S.MapInformation` fix) | 864 walks, 450 blocked (collisions at spawn) | 0 / 0 / 0 | clean |
| 40 bots, ramp 20/s, 45 s, 4 copied real maps (700×700 type 100, 100×100 type 100, 250×250 type 1, 400×400 type 1) | 40/40 | 4 maps, 306 paths found / 42 failed | 1442 walks, 524 runs, 68 blocked, 48 kills, RTT 1.0 ms | 0 / 0 / 0 | clean (0 isolated errors, no FATAL, no crash files) |
| 3 bots, ramp 3/s, 150 s, synthetic world with merchant + quest | 3/3 | 1 map, 35 paths | 3 sold, 3 potions bought, 3 quests accepted; kill task reached `Completed`, hand-in packets sent | 0 / 0 / 0 | clean |

`--check-maps C:\Mir2\Build\Server\Release\Maps`: **1705 files parsed, 0 failed**, formats type 0 ×237, 1 ×1442, 3 ×1, 5 ×6, 100 ×19. The 42 path failures are goals inside unreachable pockets on real maps (random roam points); the bot repicks.

Older numbers (2026-10-10, first harness) are not comparable: that bot had no world model and no pathfinding.

## 7. Known limits

- Console only (no GUI). Behaviour now covers potions, threat response, level-band targeting, learned spells, loot pickup, gear upgrades, merchant runs (sell/buy/repair), quest accept/hand-in, chat and party invites. Still missing: trade, mail, market/game shop, storage transfers, crafting/refine, guilds, and any spells the scratch bots never learn.
- Bots are all Warriors and fight with `C.Attack`/`Spell.None`; `C.Magic` is wired but never exercised because the scratch world grants no spells.
- Quest hand-in only succeeds once the server has marked the tasks complete; the scratch quest is a single Scratch Oma kill, and the bot retries on a later town run if the hand-in is refused.
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
