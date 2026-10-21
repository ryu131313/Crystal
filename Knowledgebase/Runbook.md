# Crystal Operator Runbook (deploy, rollback, restore)

> **Scope:** how to put a new build into the live run folders (`Build\Server\Release`, `Build\Client\Release` since 2026-10-11; previously `Debug`) safely, how to roll back, and how to rehearse a restore. Written for roadmap phase P0 (2026-10-10).
> **Who runs this:** the operator (you). Agents build to scratch and stop there. They never deploy, and never touch `Build\` (AGENTS.md).

---

## 1. Build (agent or operator)

```powershell
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\verify.ps1 -Scratch D:\mir2-out
```
- This builds every project into `D:\mir2-out\<Project>` and runs every test project.
- It refuses any scratch path inside `Build\`.
- To build only what you'll deploy: `-Only Server` or `-Only Client`, plus `-NoTests` when you've just run the tests.

## 2. Deploy (operator only)

1. **Save and stop.** Stop the world via the web admin or SMain. A stop runs the final saves. Then close `Server.exe`.
2. **Take an off-box snapshot** of the run folder's data. The deploy script below takes one automatically, but keep a second copy on another disk or machine before risky deploys (DB format or runtime changes).
3. **Preview:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\deploy-with-backup.ps1 -Kind Server `
     -Source D:\mir2-out\Server -Target C:\Mir2\Build\Server\Release -BackupRoot D:\Mir2Backups -DryRun
   ```
4. **Deploy:** run the same command without `-DryRun`. The script:
   - snapshots all data plus the outgoing binaries into `D:\Mir2Backups\Server-<timestamp>\`;
   - copies the new build over, never overwriting the protected data paths;
   - deletes only files that the previous deploy's manifest listed and the new build no longer contains;
   - writes `.deploy-manifest.txt`.

   The first managed deploy deletes nothing, because there is no previous manifest yet.
5. **Start the server and watch.** At least 3 autosaves (`SaveDelay`, default 5 min), 1 planned full-process restart, and a read of `Logs\Server\Server (dd-MM-yyyy).log`. The watch length per change type is in `roadmap.md` §6.

Protected paths (never overwritten or deleted by the script):
- **Server:** `Server.MirDB*`, `Server.MirADB*`, `SystemData.db*` (roadmap P7), `Guilds\`, `Conquests\`, `Archive\`, `Back Up\`, `WebAdmin\admins.dat`, `Exports\`, `Configs\`, `Envir\`, `Maps\`, `Logs\`
- **Client:** `Data\`, `Sound\`, `Map\`, `Screenshots\`, `Mir2Config.ini`, `Mir2Test.ini`, `KeyBinds.ini`, `Error.txt`

## 3. Rollback

| What was deployed | How to roll back |
|---|---|
| Binary-only change (no `Envir.Version` change) | Stop the server. Copy `<snapshot>\_binaries\*` back over the target, or redeploy the previous scratch build with the script. Start. |
| DB format change (`Envir.Version` bumped, e.g. 117→118, 118→119) | **Data must go back too.** The old binaries refuse the newer DB. Stop the server, restore the snapshot's data files and `_binaries`, then start. Everything written since the deploy is lost. |
| Runtime change (.NET 8 → 10) | Same as binary-only. Keep the old runtime installed on the host. The script removes net10-only files that the old build lacks, if the net10 build was deployed by the script. |

### 3.1 Version 119 (2026-10-16, SceneLab UI phase 2.3)

- **Back up before the first v119 run:** off-box copy of `Server.MirDB`, `Server.MirADB` and `Guilds\` with the world stopped (the deploy script's snapshot plus a second copy). The first v119 save rewrites them as v119; any pre-119 `Server.exe` then refuses `Server.MirDB`/`Server.MirADB` (`LoadDB`/`LoadAccounts` reject a newer version), so a rollback needs the backup.
- Shared changed (`GameShopItem` deal prices, `S.GameShopInfo` / `S.GameDataChanged` layout): deploy server, desktop client and web client from the **same commit**. An old client misreads the shop.
- 2026-10-16 (phase 2.7, friends): `ClientFriend` grew five fields, so `S.FriendUpdate` changed layout (no DB change, still v119). Deploy server, desktop client and web client (relay + `WebClient`) from the **same commit**; an older client misreads the friend list (garbled entries, or a decode error: the desktop client can crash on its socket thread). Friend requests live in server memory only: a restart drops pending ones.
- After rollback to a pre-119 build you lose: play time, profiles, persisted social toggles, mail sender data, spell AutoCast flags, guild logs, market sales history and deal prices recorded since the deploy.

### 3.2 Eight-cell player belt + user-DB wipe (2026-10-16, SceneLab UI phase 2.9)

The player belt grows 6 → 8 cells (`Inventory[0..7]`, new characters 48 cells, bag resized up to 88). Still `Envir.Version` 119 and no wire change, but old characters (46 cells) do not fit the new resize steps, so **this build ships only together with a wipe of the user data** (user decision 2026-10-16). `Server.MirDB` (system data: items, monsters, maps, NPCs) is kept.

1. **Stop** the world (web admin or SMain) and close `Server.exe`.
2. **Back up**: run the deploy below with `-DryRun` first, then for real (its snapshot holds the old data and `_binaries`), and keep a second off-box copy of the whole run folder.
3. **Wipe** in the run folder (`Build\Server\Release`). Move each item into a dated folder outside the run folder rather than deleting it outright:
   - `Server.MirADB`, plus `Server.MirADBn` / `Server.MirADBo` if present (accounts, characters, heroes, mail, market, `NextGuildID` / `NextUserItemID`);
   - **`Back Up\Accounts\`** (all of it). Otherwise the start fails closed: `LoadAccounts` refuses to create a new accounts DB while `Back Up\Accounts\*.bak` or `Server.MirADBn/o` exist (`Envir.HasPriorDataEvidence`);
   - `Guilds\*.mgd` (guilds). **This also clears the territory owners:** the territory list is rebuilt from the map data with owner "None" at every start and only guilds whose saved `GTIndex` matches claim one (`Envir.StartEnvir`, `LoadGTInfo`), so no separate reset step or setting exists or is needed;
   - `Conquests\*.mcd` (conquest owners are stored as guild numbers, which restart at 0 after the wipe, so new guilds would inherit old conquests);
   - `Envir\Goods\*.msd` (NPC buy-back stock; its item `UniqueID`s restart with `NextUserItemID` and would collide);
   - `Archive\*.MirCA` (archived/deleted characters, still 46 cells; restoring one would give a broken bag).
4. **Deploy** the server, the desktop client and the web client (`WebClient` rebuilt with `tsc`) from the **same commit**. An old client shows the wrong belt/bag split (desktop older than this commit can index past its bag grid on an 88-cell bag). The relay (`WebRelay`) needs no rebuild or restart: no packet IDs or enums changed.
5. **Start** and check the log: `Server.MirADB` is created empty, no "[Load] … Refusing" line. Create a test character: bag 48 cells, keys 1-8 = belt, 9/0 = hero belt (web client).

**Rollback:** stop, restore the snapshot's `_binaries` **and** the moved files from step 3 (the old binaries expect 46-cell characters; characters made on the new build would not match the old resize steps), start. Everything played on the new build is lost.

## 4. Restore drill (do once, then after any backup-format change)

1. Make a scratch folder, e.g. `D:\mir2-drill`.
2. Copy a scratch build of `Server.exe` into it (`verify.ps1 -Only Server -Scratch D:\mir2-drill-build`, then copy the output in).
3. Copy `Configs\`, `Envir\`, `Maps\`, `Guilds\` and `Conquests\` from a snapshot into it. Copy the newest `Back Up\Accounts\*.bak` in as `Server.MirADB`, and the matching `Back Up\Database\*.bak` as `Server.MirDB`.
4. In the drill copy's `Configs\Setup.ini`, change the game port and the web admin port, so the drill never collides with the live server.
5. Start `D:\mir2-drill\Server.exe` from that folder. The working directory must be the drill folder. Log in with a known account and check a character.

## 5. Operator mitigations from roadmap P0 (no code)

- [ ] Change `GMPassword` away from the default `C#Mir 4.0` in the web admin Config page, then do a full process restart. Online players keep the old value until they relog.
- [ ] Take an off-box copy with the world stopped: `Server.MirDB`, `Server.MirADB`, `Guilds`, `Conquests`, `Archive`, `WebAdmin\admins.dat`, `Configs`, `Envir`, and `Maps` if not stored elsewhere.
- [ ] Check free disk space and prune `Back Up\`, **keeping at least the last 48 h** of `Back Up\Accounts` and `Back Up\Database`.
- [ ] Use full process restarts, not the in-process reboot (stale guilds), until roadmap P5 ships.
- [ ] Use one editor surface at a time (web admin *or* WinForms).

## 6. Scratch server and bot runs (roadmap P6)

For load tests only; never on the live server or its folders. Details: `Knowledgebase/StressClient/stressclient.md`.

```powershell
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\start-scratch-server.ps1 -Port 17000 -Reset   # builds to %TEMP%\mir2-p6-server, synthetic world, load-test Setup.ini
dotnet build C:\Mir2\StressClient\StressClient.csproj
C:\Mir2\StressClient\bin\Debug\net10.0\StressClient.exe --port 17000 --bots 200 --ramp 20 --duration 60
powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\stop-scratch-server.ps1                      # stops only the PID it started
```
- Use ports 17000-17999. The scratch `Setup.ini` sets `[LoadTest] ExemptLoopback=True`, `CheckVersion=False`, `EnforceDBChecks=False`, `AllowStartGame=True` and `[WebAdmin] Enabled=False`: **never copy it to a real server.**
- The scratch profile sets `[Network] StatusPortEnabled=False`, so the status port (3100 by default, now configurable) stays free for the live server.
- After a run, check `%TEMP%\mir2-p6-server\run\Logs\Server\` for `[World] Isolated error` / `FATAL` and `run\Logs\Crash\`.
- Baseline / regression: `powershell -ExecutionPolicy Bypass -File C:\Mir2\tools\run-baseline.ps1` (idle + 50/200/500 bots, ~10 min, writes `Knowledgebase\Future Optimisation\Baseline-<date>.md`). A/B a switch with `-Bots 500 -NoIdle -ExtraIni '[Optimisation]','EncodeOnce=False' -Out <file>`. CPU numbers are only comparable on a quiet machine (close browsers, chat apps).

## 7. Turning on v2 password hashes (roadmap P8)

`[Security] PasswordHashV2` (default `False`) makes the server store new and changed login passwords as PBKDF2-SHA256 (100k iterations, `v2$...`), and upgrade each account on its next successful login. Every build since commit `94443e51` **verifies** both v1 and v2; only the flag decides what is **written**.

1. **Precondition:** the live build already contains v2 verification and has run long enough that you won't roll back past it. **A build older than `94443e51` cannot log in an account once it holds a v2 hash.** Rolling back after that point means restoring `Server.MirADB` from before the switch, or living with those accounts locked out.
2. Turn it on from the web admin settings page ("Stronger password hashes (v2)"), or in `Configs\Setup.ini` with the server stopped (`[Security]` `PasswordHashV2=True`; remember `Settings.Save()` rewrites the file at exit).
3. What to expect:
   - **Off the game thread:** login, new-account and password-change hashing all run on workers. A 200-bot scratch run with v2 on showed a worst loop iteration of 15.9 ms.
   - **Web admin:** setting a password costs one v2 hash on the game thread per admin action (tens of ms).
   - **Storage passwords** stay v1 by design.
4. Check: after a few logins, the count of `v2$` hashes grows. The web admin account page shows the accounts; a scratch copy of `Server.MirADB` can be checked with `Select-String -Pattern 'v2\$'`.
5. To stop writing v2: set the flag back to `False`. Existing v2 hashes keep working.

## 8. Web client alpha (public relay at worldofwooolcraft.uk/portal/mir2webclient)

Two relays, same program (`WebRelay`), mirroring WoOOLcraft's private/public split (`Knowledgebase/WebClient/webclient.md` §5):

| | Private (dev) relay | Public relay |
|---|---|---|
| Script | `tools\start-webrelay.ps1` | `tools\start-public-webrelay.ps1` |
| Listens | `127.0.0.1:5690`, page at `/play/` | `127.0.0.1:5695`, page at `/portal/mir2webclient/play/` |
| Serves | the working tree `WebClient\` (edit, tsc, refresh) | the snapshot `WebClient\publish\public` (`tools\publish-webclient.ps1`) |
| Server | a scratch/test server (default 17000) | the live server (port from its `Setup.ini`, 7100) |
| Limits | relay budget only | `CF-Connecting-IP` keyed budget + failed-login / account / character limits (`--public`) |

**Interface default (2026-10-16, scene-lab UI phase 5):** the new ("Modern") interface is the default for a player whose `UiStyle` setting is "Default"; a saved "Classic" still wins. Players switch to classic from the new login / select footer links or the modern game menu ("Switch to classic interface"), and back to the new interface from the classic Options "More" panel row "New interface" (in game; the classic account screens have no link back); the choice saves at once and applies at the next screen (in game a popup offers to log out now). It ships with the `WebClient` snapshot below — no server, relay or patch step.

**One-time setup (operator):**
1. **Server setting.** Every web player reaches the server from `127.0.0.1`, so the live server needs `[LoadTest] ExemptLoopback=True`. Without it, `MaxIP=5` caps ALL web players at 5, and one player's 5 failed logins (1 min) or 4th account in an hour (24 h) IP-blocks every web player. Set it on the web admin settings page ("Load test: exempt loopback") or in `Build\Server\Release\Configs\Setup.ini` with the server stopped (`Settings.Save()` rewrites the file at exit). The relay re-applies those limits per player address. The exemption covers everything that connects from this machine; desktop players from other machines keep the server's own limits. `start-public-webrelay.ps1` refuses to start without it.
2. **Cloudflare tunnel route** (Zero Trust → Networks → Tunnels → the "WoooL Home" tunnel → Published application routes → Add):
   - Hostname `worldofwooolcraft.uk`, Path `^/portal/mir2webclient(/|$)`, Service `HTTP` → `127.0.0.1:5695` (not `localhost`: the relay listens on IPv4 only).
   - Move it **above the catch-all** route (WebPortalHost on 5080). It does not overlap WoOOLcraft's `/portal/webclient` or `/js/`, `/Data/`, ... routes (everything Crystal uses is under the mount), but keeping it near the top avoids surprises.
   - Leave HTTP Host Header empty (the relay accepts the forwarded host) and WebSockets on (zone → Network → WebSockets; already on for WoOOLcraft).
   - If Rocket Loader is on for the zone: a Configuration Rule turning it off for URI path starts with `/portal/mir2webclient`.
   - **Cache Rules** (zone → Caching → Cache Rules). Cloudflare's Browser Cache TTL otherwise rewrites the relay's `no-cache` to `max-age=14400` (seen 2026-10-13), and it never edge-caches `.Lib` / `.map`:
     1. "Mir2 web: respect origin": When `starts_with(http.request.uri.path, "/portal/mir2webclient/")` → Browser TTL: **Respect origin TTL** (the page and `/api/manifest` must stay `no-cache`).
     2. "Mir2 web: versioned assets": When `starts_with(http.request.uri.path, "/portal/mir2webclient/") and (http.request.uri.query contains "v=" or http.request.uri.path contains "/js/")` → **Eligible for cache**, Edge TTL: **Use cache-control header if present**, Browser TTL: **Respect origin TTL**. Every such URL is versioned and checked by the relay (412 when stale, never cached), so no purge is needed after a publish or a client patch.
3. **Portal link** (WoOOLcraft site, `C:\WoOOLcraft\wwwroot\portal`, live on save): a plain `<a href="/portal/mir2webclient/">` where you want it. Add it after the live check below passes.

**Each release:**
1. `powershell -ExecutionPolicy Bypass -File tools\publish-webclient.ps1` (builds, snapshots to `WebClient\publish\public`, keeps `public.old`; no relay restart).
2. If the relay is not running (or `WebRelay\` changed: stop it first, its exe is locked): `powershell -ExecutionPolicy Bypass -File tools\start-public-webrelay.ps1`. Check the banner shows the mount, origin `https://worldofwooolcraft.uk`, `CF-Connecting-IP` and the per-IP limits line. The window must stay open (no service yet: closed window = 502 on the site).
3. Live check: `https://worldofwooolcraft.uk/portal/mir2webclient` redirects to `.../play/` and shows the login screen; log in; the relay console shows `[ws N] open from <your public IP>` (not `127.0.0.1`: if it does, the request did not come through cloudflared). A 404 from WoOOLcraft's portal = route order/path wrong; 502 = relay not running.
4. Rollback of the web client: stop nothing, rename `WebClient\publish\public` ↔ `public.old`.

**After a client patch** (`Build\Client\Release` updated): nothing to do. The relay reads `Data\`, `Map\` and `Sound\` per request and versions them by size + write time; open pages get a 412 for a changed file and show "The game files were updated. Reload the page". If the server turns on `CheckVersion`, restart the public relay so it re-hashes the server's `VersionPath` file.

**Known gaps (alpha):** no autostart; a refusal by the relay's limits shows as "connection lost" in the browser; a first visit still downloads each sprite from home until Cloudflare's edge has it (Cache Rule 2).

## 9. Desktop in-game patcher (WebRelay as patch host, 2026-10-20)

Desktop clients with `[Launcher] Mode=OnDemand` (the default) download the raw client files from a WebRelay started with `--patch-root`. A new player gets the core + starter files before launch (program files, UI libraries, the first map) and everything else in the background or when the game needs it. Design: `Knowledgebase/Future Ideas/InGame-Patcher-Plan.md`.

**One-time setup:**
1. Make a **clean distributable copy** of the client (never a folder someone plays from; the relay refuses `*.ini`, `Error.txt`, `Data\UserData\`, `Screenshots\`, `DirectX\`, `Patch\`, `Old__*` anyway): a client build output + `Data\`, `Map\`, `Sound\`. Run the client from it once before publishing, so `Localization\*.json` is in the form the client writes back (else every client re-downloads it at each start).
2. Optional `PatchTiers.txt` (relay config, outside the root): `glob = 0|1|2|3|x` per line, first match wins (0 core and 1 starter = before launch, 2 background, 3 only when needed or "Download everything", x never). Without it the built-in list in `WebRelay\PatchIndex.cs` (`PatchTiers.Default`) applies.
3. Start the relay with `--patch-root <copy> [--patch-tiers <file>] [--patch-cache <file outside the copy>] [--patch-slots 32]` besides its usual options (the public relay script does not pass them yet). The first start hashes every file (about a minute or two for 8.5 GB, then cached).
4. Ship clients a `Mir2Config.ini` with `[Launcher] Host=<the relay URL including the mount>/` (the built-in default host has no patch routes). The old `PList.gz` patcher and AutoPatcherAdmin(V2) were retired 2026-10-20: old `[Launcher] Mode` / `PatchFile` / `Login` / `Password` lines in players' ini files are ignored.

**Sharing the client with new players:** every Release build of `Client.csproj` (the solution's Release build in Visual Studio) also writes `Build\Client\CrystalClient.zip`: the program files only (exe, DLLs, `runtimes\win-x64`, `Localization`, `Resources`) + `Mir2Config.ini` and `README.txt` from `Client\Distribution\`. `Client\Distribution\Mir2Config.ini` points both at the public relay: `[Launcher] Host=https://worldofwooolcraft.uk/portal/mir2webclient/` (the patcher) and `[Network] Relay=wss://worldofwooolcraft.uk/portal/mir2webclient/ws` (the game connection goes through the relay's WebSocket like the web client's, so no game port has to be open; the server sees every desktop player as 127.0.0.1, so the §8 `ExemptLoopback` setting covers them too, and the relay's per-player limits apply). The public relay must run with `-PatchRoot <clean client copy>` (`tools\start-public-webrelay.ps1`) for the patcher half; the build warns if the ini still reads `CHANGE_ME`. Players unzip it and run `Client.exe`; the patcher downloads the rest (needs the .NET 8 Desktop Runtime x64, as the README says). `-p:StarterZip=false` skips it.

**Each client release:** copy the changed files into the root, then write `PatchRevision.txt` **last** (any text, e.g. the date). The relay publishes the new index only after the marker changes and the folder held still for a whole hash pass. Clients pick it up at their next start or within `[Launcher] RefreshMinutes` (default 15).

**Publishing a client build** (program files: `Client.exe`, `*.dll`, `*.json` in the exe folder or `runtimes\`): copy the whole build output into the root, then the marker, as above. What players see:
- In game, the new build downloads into `Patch\Staged\` (never over the running files). When the whole set is there: a "restart now?" prompt (Later = the strip line and Options "Restart to update"; the update is applied at exit anyway).
- At the launcher, a changed build is staged, applied and the launcher restarts once (`--wait-pid`).
- Apply keeps the replaced files as `Old__<name>` until the new build reaches the game window. A build that fails to get there is rolled back at the next start and **rejected** (`Patch\Rejected.bin`): clients stay on the old build until you publish a different one. A failed apply (a file locked by another program) restores everything and tells the player to restart the launcher.
- If a build is bad, publish the previous build output again (same files = clients that have not updated yet do nothing; updated ones go back).
- A build whose new `Client.dll` cannot even load (corrupt file, missing runtime) cannot roll itself back: the recovery code lives in `Client.dll`. Test a build on one client before publishing.

## 10. System data in SQLite (`SystemData.db`, roadmap P7, 2026-10-21)

Since P7 phase 7.3 the server keeps items, monsters, maps, NPCs, quests, magic, the game shop, conquests and the dragon in `SystemData.db` (SQLite) next to `Server.exe`. Accounts, guilds, goods and conquest state stay in `Server.MirADB` and the side files. `Server.MirDB` is read once, on the first start, and never written again: it stays as the rollback. `MirDbExport` (`dotnet build C:\Mir2\tools\MirDbExport\MirDbExport.csproj`, exe in `tools\MirDbExport\bin\Debug\net10.0\`) works on copies, never on a running server's files.

**First deploy of a P7 build (the flip):**
1. Optional dry run on a copy: copy the live `Server.MirDB` to a scratch folder, then `MirDbExport import <folder> <folder>\SystemData.db` and `MirDbExport export-binary <folder>\SystemData.db <folder>\out`, and compare `out\Server.MirDB` with the copy (`fc /b`): they must be identical.
2. Deploy as §2 (stop, snapshot, deploy). The deploy script protects `SystemData.db*`.
3. Start. The log shows `[Store] Importing .\Server.MirDB (version 119) into .\SystemData.db` and `[Store] Created .\SystemData.db (import)`. Check a few Game Data grids in the web admin, then make one small edit and save it.

**Backups:** every editor or web Game Data save first copies the current store to `Back Up\Database\Database yyyy-MM-dd HH-mm-ss.bak` (LiveDb uses its own names). These are SQLite files with the old names, so `[Backup] RetentionEnabled` prunes them as before. Older `.bak` files in that folder are binary Server.MirDB copies.

**Restore a SQLite backup:** stop the world, then web admin → Backups → Database → Restore (it closes the store, swaps the file, runs an integrity check and reloads the editor copy; a binary `.bak` is refused with a message). By hand: stop `Server.exe`, keep a copy of the current `SystemData.db`, copy the `.bak` over `SystemData.db`, give it the current time (`(Get-Item SystemData.db).LastWriteTime = Get-Date`: a copy keeps the backup's old time, and a store older than `Server.MirDB` is refused), start.

**After an upgrade that adds fields:** the first start adds the new columns and first copies the store to `Back Up\Database\Pre-migrate <time>.bak` (`[Store] Added column ...`). An older build refuses the migrated store (`unknown column(s)`): to go back, put that `Pre-migrate` copy in place, or use the rollback below.

**Restore an old binary backup** (a `.bak` from before the flip): in a scratch folder, put it as `Server.MirDB`, run `MirDbExport import <folder> <folder>\SystemData.db`, then stop the server and put that `SystemData.db` in place.

**Roll back to a binary build** (2da35a92 or older; their default is `[Database] Backend=Binary`):
1. Stop `Server.exe`. Keep a copy of `SystemData.db`.
2. `MirDbExport export-binary <live>\SystemData.db <scratch>\out`, then copy `out\Server.MirDB` over the live `Server.MirDB` (the old one is in the deploy snapshot). This carries every edit made since the flip.
3. Deploy the previous build (§3). If its `Configs\Setup.ini` still has `[Database] Backend=Sqlite` (left from 7.2 testing), set it to `Binary` or remove it. Start.

**Back to SQLite after a rollback:** the server refuses a `SystemData.db` older than `Server.MirDB` (`[Store] Server.MirDB is newer than ...`), because the edits made on the binary build would be lost. Move `SystemData.db` away (rename it), and the next start imports `Server.MirDB` again (allowed because the binary build wrote `Server.MirDB` after every SQLite backup; drilled 2026-10-21).

**Startup refusals and what they mean:**
- `SystemData.db is missing but Back Up\Database has a SQLite backup newer than Server.MirDB`: the store was lost. Restore the newest SQLite `.bak` (above). It never re-imports the older `Server.MirDB` on its own.
- `schema_version is ...` or `table ...: unknown column(s)`: the store was written by a newer build, or a field was renamed or removed without a migration. Run the build that wrote it.
- New fields need nothing: a newer build adds missing columns on open (`[Store] Added column items.X (default ...)`).
- A leftover `SystemData.db.tmp` from an interrupted first import is replaced automatically.

**Design files (phase 7.4):** the Configs ini files Settings loads, Setup.ini's `[Game]` and `[Items]`, and every `Envir\Drops`, `Quests`, `Recipe` and `Routes` file are imported into `SystemData.db` on first use (`[Store] Imported ...` lines; lines naming an item or monster that doesn't exist are kept and counted). After that the files on disk are **not read again**: edit them in the web admin (Scripts, Configuration, Balance, Systems) or the WinForms editors; "open in editor" opens a working copy whose saves go back to the store. Before the deploy you can check a copy: `MirDbExport check-files <copy-of-server-folder>` must print `CHECK OK`. Rollback for these files: stop the server, `MirDbExport export-files <live>\SystemData.db <scratch>\files`, copy the `Configs\` and `Envir\` files out of it over the live folder (Setup.ini there holds only the design sections merged into the live one: compare first). To re-import everything from the disk files, move `SystemData.db` away (it is rebuilt from `Server.MirDB` and the files).

**Drops as tables (Drops-Tables-Plan phase 1):** drops are rows in `drop_lists` (one per former file, with its owner: a monster without DropPath by its Index, the fishing / awakening / strongbox / blackstone / dragon lists, or a named list) and `drops` (one row per line; groups nest, `#INSERT` links the included list, comments are `note` rows, lines the game can't load are `disabled` rows with their text). The game loads the rows. Since phase 2 drops are edited in the web admin's Game Data › Drops (each save backs the list's previous text up in `Back Up\WebAdmin\Scripts\drops\` and reloads that list in the running world); the Scripts editor shows drops read only (restoring a backup there is the undo), the WinForms DropBuilder is gone, and the ConfigForm Assassin/Archer drop toggle still works. First start of this build on a 7.4 store: it copies the store to `Back Up\Database\Pre-drops <time>.bak`, then logs `[Store] Converted N drop file(s) into the drop tables (...)` and removes the old `drop_lines` table (once; later starts skip it), then raises the store's `schema_version` to 3, so an older build refuses the file (`schema_version is 3; this build reads 2`) instead of running without drops. Before the deploy: `MirDbExport check-drops <copy-of-server-folder>` must print `CHECK OK` (it converts a copy and loads every list both ways; the live copy of 2026-10-21 gave 0 differences over 1,640 lists). Rollback: stop the server and put the `Pre-drops` copy in place as `SystemData.db` (set its time to now, as above); the previous build reads `drop_lines` again. Edits made since then are in the newer store only: export them first with `MirDbExport export-files` if needed.
