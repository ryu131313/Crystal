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
- **Server:** `Server.MirDB*`, `Server.MirADB*`, `Guilds\`, `Conquests\`, `Archive\`, `Back Up\`, `WebAdmin\admins.dat`, `Exports\`, `Configs\`, `Envir\`, `Maps\`, `Logs\`
- **Client:** `Data\`, `Sound\`, `Map\`, `Screenshots\`, `Mir2Config.ini`, `Mir2Test.ini`, `KeyBinds.ini`, `Error.txt`

## 3. Rollback

| What was deployed | How to roll back |
|---|---|
| Binary-only change (no `Envir.Version` change) | Stop the server. Copy `<snapshot>\_binaries\*` back over the target, or redeploy the previous scratch build with the script. Start. |
| DB format change (`Envir.Version` bumped, e.g. 117→118) | **Data must go back too.** The old binaries refuse the newer DB. Stop the server, restore the snapshot's data files and `_binaries`, then start. Everything written since the deploy is lost. |
| Runtime change (.NET 8 → 10) | Same as binary-only. Keep the old runtime installed on the host. The script removes net10-only files that the old build lacks, if the net10 build was deployed by the script. |

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
