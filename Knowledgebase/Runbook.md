# Crystal Operator Runbook (deploy, rollback, restore)

> **Scope:** how to put a new build into the live run folders (`Build\Server\Debug`, `Build\Client\Debug`) safely, how to roll back, and how to rehearse a restore. Written for roadmap phase P0 (2026-10-10).
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
     -Source D:\mir2-out\Server -Target C:\Mir2\Build\Server\Debug -BackupRoot D:\Mir2Backups -DryRun
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
- Server.exe always binds the hard-coded status port 3100. Stop the scratch server before (re)starting the live server on the same machine.
- After a run, check `%TEMP%\mir2-p6-server\run\Logs\Server\` for `[World] Isolated error` / `FATAL` and `run\Logs\Crash\`.
