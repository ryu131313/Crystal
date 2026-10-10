# StressClient and the scratch server (roadmap P6)

> **What:** headless load-test bots (`StressClient\`) plus a synthetic world generator (`tools\scratch-world\`) and start/stop scripts (`tools\start-scratch-server.ps1`, `tools\stop-scratch-server.ps1`) for a **scratch** `Server.exe`.
> **Status:** MVP written 2026-10-10. Not in `Legend of Mir.sln` yet and not in `tools\verify.ps1` (add both when it is committed).
> **Safety:** scratch only. Never point the bots at a live server, and never copy the load-test `Setup.ini` to a real server.

---

## 1. Pieces

| Piece | Path | Builds to | What it does |
|---|---|---|---|
| StressClient | `StressClient\StressClient.csproj` (net10.0 console, refs `Shared`) | `StressClient\bin\Debug\net10.0\` | N bots: login state machine, roam and fight, 1 Hz metrics, CSV |
| ScratchWorld | `tools\scratch-world\ScratchWorld.csproj` (net10.0 console, refs `Server.Library` + `Shared`) | `tools\scratch-world\bin\Debug\net10.0\` | Writes `Maps\scratch0.map` + `Server.MirDB` into a folder |
| start script | `tools\start-scratch-server.ps1` | `%TEMP%\mir2-p6-server\{build,run}` | Build Server.exe to scratch, assemble the run folder, write the load-test `Setup.ini`, start minimized, wait for `Network Started.` |
| stop script | `tools\stop-scratch-server.ps1` | — | Stops only the PID in `server.pid`, after checking the exe lives in the scratch folder |

None of them reads or writes `Build\`. The generator and the start script refuse a target inside `Build\`.

## 2. Quick start

```powershell
# 1. scratch server on 127.0.0.1:17000 (fresh world; builds Server.exe into %TEMP%\mir2-p6-server\build)
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\start-scratch-server.ps1 -Port 17000 -Reset

# 2. bots
dotnet build C:\Mir2\StressClient\StressClient.csproj
C:\Mir2\StressClient\bin\Debug\net10.0\StressClient.exe --port 17000 --bots 200 --ramp 20 --duration 60 --csv %TEMP%\run200.csv

# 3. stop (graceful CloseMainWindow, then kill after 20 s)
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\stop-scratch-server.ps1
```

Start-script options: `-Scratch <dir>` (default `%TEMP%\mir2-p6-server`), `-Port` (use 17000-17999), `-MaxUser` (600), `-LoopbackMaxPacket` (400), `-MapSize` (200), `-Reset` (regenerate the world, delete accounts/logs/backups in the run folder), `-NoBuild`, `-Multithreaded $false`.
Without `-Reset` the run folder keeps its accounts, so a second bot run exercises the login path (bots log in instead of creating accounts).

Check a run: `run\Logs\Server\Server (dd-MM-yyyy).log` must have no `[World] Isolated error`, no `FATAL`, and `run\Logs\Crash\` must not exist.

## 3. The synthetic world

- **Map:** the oldest Crystal format (`Map.FindType` → 0, `LoadMapCellsv0`): a 52-byte header (`Int16` width, `Int16` height, rest zero), then 12 zero bytes per cell, column-major. All-zero cells are walkable floor, no doors, no lights. The width's low byte must not be 0x00/0x0D/0x0F/0x10/0x15 (other formats' signatures); the generator checks this.
- **Server.MirDB:** written by the real `Envir.Main.SaveDB()` with cwd = the output folder (`Envir` paths are cwd-relative). One map `scratch0` with a `StartPoint` safe zone (size 5) in the centre; `ScratchHen` (AI 1, Deer: wanders, 40 HP) and `ScratchOma` (AI 0, 120 HP) respawning across the map; start items `WoodenSword` (DC 2-5), `BaseDress(M)`, `(HP)DrugSmall`, plus `Gold` and `BlackIronOre`.
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

**Protocol.** Real `Shared` codec only: `Packet.IsServer = false`, frames split on the 2-byte length prefix and decoded with `Packet.ReceivePacket`, sends serialised with `Packet.GetPacketBytes`. Rebuild it after every `Shared/` change (a stale build reads packets wrong; `decode` and `unk` count that).

**Login state machine** (mirrors `Client/MirScenes/LoginScene.cs` / `SelectScene.cs`):
`S.Connected` → `C.ClientVersion` → `S.ClientVersion(1)` → `C.Login` → (`S.Login(3)` → `C.NewAccount` → `S.NewAccount(8|7)` → `C.Login`) → `S.LoginSuccess` → (`C.NewCharacter` → `S.NewCharacterSuccess`) → `C.StartGame` → `S.StartGame(4)` + `S.UserInformation` = in game.
Identities are deterministic: account `<prefix><index:D5>` (e.g. `sb00042`), character `<prefix>c<index:D5>`, one password for all. `--first-index` runs several fleets with one prefix.

**Brain.** In game a bot equips its start weapon/armour (a level-1 character has 0 DC, so unarmed attacks do no damage), then: chases the nearest monster within `--seek-range`, attacks it facing it when adjacent (`C.Attack`, every `--attack-delay` ms), otherwise roams in random headings and turns when blocked. One `C.Walk` in flight at a time and never faster than `--move-delay` (650 ms vs the server's 600 ms `MoveDelay`), so `MirConnection._retryList` stays empty. Death → `C.TownRevive`. KeepAlive every 5 s (the server drops a quiet connection after 10 s); the echo gives the RTT.

**Throttle.** A per-bot token bucket on socket writes keeps each bot at ≤ 60 % of `--max-packet` per 5 s (default 50, the server default). Pass the server's `LoopbackMaxPacket` only if bots need more; a bot normally sends ~2 writes/s.

**Fleet.** `--ramp` bots/s; `--connect-budget` (25) bots at most between TCP connect and in-game (login storm limiter). Disconnected bots reconnect with backoff unless `--no-reconnect`. `--login-timeout` (30 s) closes a bot stuck before the game.

**Metrics line (1 Hz):** `conn` (got `S.Connected`), `connecting`, `in-game`, `reached` (bots that entered the game at least once), packets/s and KB/s out/in, walks/attacks/blocked per second, kills (monsters this bot hit last), deaths, KeepAlive RTT, and cumulative `err` (protocol refusals), `decode` (codec failures), `unk` (ids without a decode case), `refused` (TCP refused, or closed before `S.Connected`: MaxUser, IP block), `disc` (server closed an established session), `timeout`. `--csv` writes the same per second.

**Exit code:** 0 when every bot reached the game and there were no decode errors, 1 otherwise, 2 for bad arguments. Usable from a regression script.

## 6. Baseline runs (2026-10-10, scratch server, this machine)

| Run | Reached game | Server pkt/s → bots (steady) | Bot pkt/s → server | RTT | Errors / refused / disconnects | Server log |
|---|---|---|---|---|---|---|
| 50 bots, ramp 10/s, 60 s (new accounts) | 50/50 in 6 s | ~1.1-3.2 k | ~79 | 0.9 ms | 0 / 0 / 0 | clean |
| 200 bots, ramp 20/s, 60 s (50 logins + 150 new) | 200/200 in 11 s | ~10-30 k | ~313 | 1.6 ms | 0 / 0 / 0 | one `UnobservedTaskException` (status: server Known defect, see below) |

Not the P6 baseline matrix (that needs the idle park first; roadmap P6).

**Server finding (not fixed, reported):** at mass disconnect `Server.exe` logged `FATAL TaskScheduler.UnobservedTaskException` (`SocketException 995`) and wrote `Logs\Crash\crash-*.txt`. Cause: `MirConnection.ReceiveData` returns on `!Connected` **before** calling `EndReceive`, so the aborted receive's exception is never observed. Harmless to the world, but it fills `Logs\Crash`. Fix: call `EndReceive` (in the existing try/catch) before the `Connected` check.

## 7. Known limits

- Bots are all Warriors on one map; no spells, no NPCs, no trading, no chat.
- RTT is KeepAlive only (game-loop latency plus loopback), not per-action latency.
- `kills` counts monsters whose death the bot saw after it attacked them last; shared kills are under-counted.
- The bots' address is 127.0.0.1, so a real-network test needs the server bound elsewhere and **no** `ExemptLoopback` benefit: expect per-IP limits.
