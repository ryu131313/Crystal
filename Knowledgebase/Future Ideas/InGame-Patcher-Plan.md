# In-game patcher with on-demand assets (desktop client) — plan

*Written 2026-10-20 from a read-only survey of `Client/`, `WebRelay/`, the live client folder `Build\Client\Release\` (sizes and layout only) and the decompiled Mir Ametrine client (`C:\Mir Ametrine\Decompile\`). Expands item **P2** of `Ametrine-Client-Comparison.md`.*

*Revisions, 2026-10-20 (user decisions):*
- *The host is **WebRelay** serving the **raw client files**, with HTTP compression where it pays. No `.gz` patch set.*
- *The plan is **lean**: one short smoke check per phase, work done in the main session. Optional features moved to §8 "Later".*
- *A review agent checked the plan against the code. All 10 findings were adopted, each tagged "review #n" where it changed the design: apply safety and rollback for program updates, one patcher per folder, frames refresh for special monsters, looping sounds, restart timing, AMain streaming, library bounds, relay slot order, path case and Trash names, and `ReloadCells` details.*
- *Three items brought back from "Later": pre-requesting files from object packets, deleting files removed from the patch root, and installing program updates mid-session. That makes 4 phases.*

**Status:** phase 1 (relay patch host) built 2026-10-20 (`WebRelay/PatchIndex.cs`, 733f1f61). Phase 2 (client patcher) built 2026-10-20: `Client/Patching/`; smoke (a) core + background, (c) resume, (d) removed file + `Mods\` kept, (e) second instance read-only, plus the launcher program swap + restart, checked on scratch folders; (b) in-game pickup (monster appears and animates, map reload) and (f) `Mode=Full` not run (no GUI driving on this machine) — covered by a code review. Phase 2 committed 5af3a740. Phase 3 (UI) built 2026-10-20: `PatchStatus` → `IGameUi.PatchStatusChanged` → classic `PatchStatusStrip` / modern `Hud\DownloadStrip` (incl. "Downloading map…"), Options "Downloads" rows in both UIs, 15 `ClientTextKeys`; checked with `tools\uitest.ps1` steps `patch_strip_*` in both UIs and `modern-ui-gate` clean; docs (client.md §2.1/§5.4, webclient.md §3.2, Runbook §9, AGENTS.md). Phase 4 not started. All decisions are made (§7, 2026-10-20).

**Licensing:** Ametrine is decompiled third-party code. Borrow ideas only, never its code.

---

## 1. Goal

- A new player downloads a small **core set** (program files + UI), logs in and plays. Everything else downloads in the background.
- A file the game needs that isn't on disk yet (monster library, map, sound) **jumps the queue** and appears as soon as it lands, without a restart. Libraries for monsters, players and NPCs are requested as soon as the object arrives, before it's drawn.
- Files removed from the patch root are removed from clients.
- A new client build is downloaded **during play** and applied by a quick restart, with no download wait at the next start.
- Options gets **Download everything** and **Pause background downloads**.
- **One copy of the files:** WebRelay serves the same client folder to the web client and the desktop patcher. Publishing = updating that folder.
- `[Launcher] Mode=Full` (today's launcher, `PList.gz`, AutoPatcherAdmin) keeps working unchanged.

**Not in scope:** per-sprite streaming (Option A), any game server or protocol change, fixing Known debt the work touches (main-thread texture decode, `CachedSound` decode, the library init race).

---

## 2. Facts the design rests on

**Sizes** (live client, 5,046 files, 8.5 GB):

| Part | Size |
|---|---|
| `Data\Map\` (map tile/object libraries) | 3.6 GB |
| `Data\Monster\` | 2.1 GB |
| `Map\` (499 `.map` files) | 895 MB |
| `Data\CArmour\` | 562 MB |
| `Sound\` | 440 MB |

The largest file is `Data\Map\WemadeMir2\Tiles.Lib` at **450 MB**, so the first map a new player sees is the weak spot. Decided: the starter tier downloads before launch (§7.2).

**Compression:** gzip saves 92% on a `.map`, about 20% on a `.wav`, and 2-3% on a `.Lib` (images inside are already gzip). So raw files + HTTP `Content-Encoding` is enough.

**Per-user files** in a client folder that must never be served: `*.ini` (`Mir2Config.ini` holds plaintext passwords), `Error.txt`, `RebuildProfile.txt`, `Data\UserData\`, `DirectX\`, `Screenshots\`.

**Client code**

| Area | Code | What matters |
|---|---|---|
| Launcher | `Client/Program.cs` ~L42-65, `Client/Forms/AMain.cs` | `AMain` patches before `CMain` starts. `.dll/.exe` are swapped via `Old__*` + restart (~L306) |
| Library arrays | `Libraries.InitLibrary` (`MLibrary.cs` ~L199) | Sized from the **highest-numbered file on disk**. On a fresh install `Monsters.Length == 1` → every monster indexes out of range |
| Library open | `MLibrary.Initialize` (~L525), `CheckImage` (~L580) | All libraries are initialised at startup on the loader thread. A missing file → draws are silent no-ops. Opened with `FileShare.Read`, so an open `.Lib` **can't be replaced** |
| Maps | `MapControl.LoadMap` (`GameScene.cs` ~L6727), `MapReader` (`MapCode.cs` ~L132) | Missing file → blank 1000×1000 map. `LoadMap` → `ResetMap` removes every object except the user |
| Sounds | `SoundManager.PlaySound` (~L75), `CachedSound` | A missing sound stays cached as silent until `SoundCleanMinutes` |

**WebRelay** (`WebRelay/`, webclient.md §2.1)

| Piece | What matters |
|---|---|
| `StaticFiles` | Read-only, streams 64 KB chunks, single Range, `FileShare.ReadWrite \| Delete`. Reusable as is |
| `AssetHost` `?v=` whole file | `File.ReadAllBytes` + 256 MB compression LRU: fine for maps and sounds, **not** for a 450 MB `.Lib` |
| `/api/manifest` | Version = size + mtime only; no SHA-256, no program files, no tiers |
| `_httpSlots` | 64 slots for all HTTP, 503 when full: long patch downloads would starve the web client |

---

## 3. Rules this touches

| Rule | Where | Plan |
|---|---|---|
| **NEVER** add a background thread for game logic. The patcher may only run before the game form exists | client.md §3.2 | **Override approved by the user, 2026-10-20** (§7.1). The patch thread runs during the game but touches no game state: it writes files and hands paths to the UI thread through a `ConcurrentQueue` (the sanctioned pattern). Phase 2 writes the narrow exception into client.md §3.2 |
| **NEVER** sync file I/O in `Process`/`Draw`/handlers | client.md §3.1 | Reopening a library (a few KB) or a map (about 1.8 MB, same as a map change today) on the UI thread, drained at most every 250 ms |
| **NEVER** move `_initialized = true` in `Initialize` without a lock | client.md §3.2 | Reopen runs only on the UI thread after `Libraries.Loaded` and never resets `_initialized` |
| **NEVER** let the relay write to the client folder; never serve outside its roots | webclient.md §3.2 | The SHA-256 cache lives outside the folder. Allow-list rules + a hard deny list |
| **NEVER** touch `Build\` | AGENTS.md | Tests use scratch folders. The relay only ever reads |
| No hard-coded English; `Settings` in both `Load`/`Save`; `[Patch]` log prefix within the 100-entry budget | client.md §3.5, §5.2 | New `ClientTextKeys` (a `Shared/` change, phase 3) |

---

## 4. Design

### 4.1 WebRelay: patch host

**Options:**
- `--patch-root <folder>`: turns the patch routes on. Point it at a **clean distributable copy** of the client, not a folder someone plays from.
- `--patch-tiers <file>`.
- `--patch-cache <file>`: default `<WebRelay dir>\patch-hashes.bin`, refused if inside the root.
- `--patch-slots <n>`: default 32, a pool **separate** from the 64 general HTTP slots.

**`PatchTiers.txt`:** relay config, first match wins, globs relative to the root. `0` = core (before launch), `1` = starter, `2` = background, `3` = demand-only, `x` = never serve:

```
Client.exe = 0
*.dll = 0
Client.deps.json = 0
Client.runtimeconfig.json = 0
runtimes/** = 0
Localization/** = 0
Resources/** = 0
Data/Prguse*.Lib = 0
Data/ChrSel.Lib = 0
Data/Title.Lib = 0
Data/UI_32bit.Lib = 0
Data/ModernIcons.Lib = 0
Data/Shaders/** = 0
Data/Cursors/** = 0
Sound/SoundList.lst = 0
Map/0.map = 1
Data/Map/WemadeMir2/** = 1
*.pdb = x
*.xml = x
** = 2
```

**Hard deny list** (code, beats every rule): `*.ini`, `Error.txt`, `RebuildProfile.txt`, `Data/UserData/**`, `Screenshots/**`, `DirectX/**`, `Patch/**`, `Old__*`.

**Index:** at start, scan the root. Hash changed files (size or mtime differs from the cache) on a background task. `/api/patch` answers `503` + `Retry-After` until the first pass is done (about a minute or two for 8.5 GB, once).

**Publish marker** (review #1): the relay publishes a **new revision only when `PatchRevision.txt` in the root changes**. It never publishes from a root that is mid-copy.
- The operator copies the new files in, then writes `PatchRevision.txt` last (any text, e.g. a date). The Runbook gets one line for this.
- Seeing a new marker, the relay rescans and hashes. If any file changed again during the hash pass, it waits 30 s and rescans. Only then does it swap the index in one step.
- Until a new revision is published, the old index stays valid. A changed file's old `v` gets a 412, and the client simply retries at the next refresh.

**Routing** (review #8): `/api/patch` and `/patch/` are routed right after the mount is stripped (`RelayHost.cs` ~L100), **before** the general `_httpSlots` check (~L109). So a patch download holds only a patch slot. Patch slots full → `503` + `Retry-After: 2`; the client backs off and retries, it doesn't count a failure.

**`GET {mount}/api/patch`** (ETag, compressed): `{ "revision", "files": [ { "p", "s" (size), "h" (sha256), "t" (tier), "v" (size+mtime version) } ] }`. About 5,000 entries.

**`GET {mount}/patch/<path>?v=`:**
- Only indexed paths are served; `v` stale → `412`.
- `.map`/`.wav`/`.lst`/`.json`/`.txt` up to 32 MB go through `Compression`. Everything else, and every Range request, streams raw (the `StaticFiles` loop).
- Cache headers: `immutable`. Each download holds one patch slot.

The web client's existing routes don't change.

### 4.2 Client: launcher + `PatchService`

- **`[Launcher] Mode=OnDemand`:** `Host` = the relay's public URL including the mount.
  - **At start:** `AMain` fetches `/api/patch` and gets changed **program files + tier 0 + tier 1** (core and starter, §7.2). It hands the manifest to `PatchService` and launches.
    - **AMain is UI only here** (review #6): the downloads run on `PatchService`'s streaming, hashing, resuming engine, not AMain's whole-file-in-memory path (`AMain.cs` ~L283/297/345, about 450 MB for `Tiles.Lib`). AMain shows `PatchService.Progress` on its existing bars.
    - Program files changed at start are swapped with the same staged apply as mid-session (below), then the launcher restarts once.
  - **Default:** `Mode=OnDemand` is the default (§7.4). The built-in default `Host` (`mirfiles.com`) has no patch routes, so a server must ship `Mir2Config.ini` with its relay URL. Without one, the client launches with what's on disk.
  - **Relay unreachable:** launch with what's on disk, using the last manifest (`Patch\Manifest.bin`) for library sizing (§4.3).
- **One patcher per folder** (review #2, multiboxing): a lock file `Patch\patch.lock` opened with `FileShare.None` for the life of the process. Only the holder downloads, installs, deletes or applies updates. Other instances in the same folder run read-only: no `PatchService` workers, and their `Demand` calls are dropped. They still pick up installs: they rescan `State.bin` on its change time, at most every 5 s, and reopen what changed.
- **`PatchService`** (`Client/Patching/`): one background thread + 4 workers.
  - **Queues:** demand first, then out-of-date files on disk, then tier 2 smallest first (tiers 0-1 are done before launch; the service only re-checks them on refresh). Tier 3 only on demand or Download everything.
  - **Per file:** stream to `Patch\Temp\<sha>.part` while hashing. Resume an existing part with `Range`. On `412`, refresh the manifest. On a bad hash, delete and retry once.
  - **HTTP:** `AutomaticDecompression = All`, so the hash is over the raw file.
- **`Patch\Manifest.bin`:** the last `/api/patch` response, saved atomically. Used offline and for library sizing.
- **Paths** (review #9): manifest, `State.bin` and deny-list paths compare **case-insensitively** (`StringComparer.OrdinalIgnoreCase`, `/` and `\` normalised). A case-only rename in the patch root is then an update, not "removed + added".
- **`Patch\State.bin`:** path → size + sha256. A file is current when its recorded hash matches the manifest and its size matches. On first use, an existing install is **trusted by size**: a size match is recorded as current with the manifest's hash, with no full hash pass (§7.3).
- **Install:**
  1. Move the old file to `Patch\Trash\<guid>-<file name>`, unique per move (review #9: `CArmour\00.Lib` and `CHair\00.Lib` share a name). This works while the game has it open, after the share-mode change below.
  2. Move the new file in.
  3. Record it in `State.bin`.
  4. Queue the path for the UI thread.

  A **sharing violation** (e.g. NAudio's `AudioFileReader` holding a playing `.wav` without `FileShare.Delete`, review #4) means "retry later", not a failure: the file goes back on the queue with a 30 s delay. `Trash\` is emptied at the next start, skipping locked files.
- **Removed files:** a file that `State.bin` lists (so the patcher installed or verified it) but the new manifest doesn't is deleted.
  - Never touched: files the patcher never recorded (user mods, screenshots), and anything matching the relay's deny list (the client carries the same list).
  - An open `.Lib` is moved to `Trash\` instead and queued for the UI thread, whose `Reopen()` then finds it missing and draws nothing.
  - Removed program files are deleted as part of the next program update (below), never while running.
  - Safety valve: if a manifest would remove more than 25% of recorded files, skip deletion for that cycle and log once. A half-copied or wrongly pointed patch root shouldn't wipe clients.
- **Program updates mid-session** (`Patch\Staged\`):
  1. A manifest refresh finds changed program files (tier 0 files in the exe folder or `runtimes\`: `.exe`, `.dll`, `.json`).
  2. They download at the head of the background queue (after demand) into `Staged\`, hash-checked. `Staged.bin` records path + sha256 for the whole set.
  3. Only when the **whole set** is staged: show "Update ready — restart" (§4.4).
  4. **Apply** (on Restart from the Options button or the prompt, or on normal exit), in the running process (review #1):
     1. Write `Patch\Applying.bin` (the set's paths + hashes) first.
     2. For each file: rename the target to `Old__<name>` (Windows allows renaming a loaded DLL and the apphost exe), then **copy** the staged file in. Copy, not move, so `Staged\` stays complete until the new build is proven.
     3. On any failure: move every `Old__` file back, delete `Applying.bin`, keep `Staged\`, log once, and say "Update failed — restart the launcher".
  5. **Restart** (review #5): start `Client.exe --wait-pid <pid>` and close. The new process waits (up to 30 s) for the old one to exit before touching any file.
  6. **Proving the new build:** the first time the new build reaches `CMain` it writes `Patch\Healthy.bin` (the applied hashes). Then it deletes `Old__*`, `Applying.bin` and `Staged\`.
  7. **Recovery at start.** This is the very first thing in `Program.Main`, before any `Shared` type or library is touched, so it still runs with a mismatched `Shared.dll`. If `Applying.bin` exists without a matching `Healthy.bin`:
     - **First start after the apply:** let it run (it's proving itself, step 6).
     - **The proving start never reached `CMain`** (a counter in `Applying.bin`): restore every `Old__` file over the new one, delete `Applying.bin`, log, and start the old build.
  8. **`AMain.RepairOldFiles`** (~L737) today deletes `Old__` files without try/catch and would discard the rollback copy. In OnDemand mode it runs only when no `Applying.bin` exists, and every delete is wrapped (a locked file is left for the next start).
  9. A crash before applying leaves the set in `Staged\`. `AMain` core mode applies a complete, still-current set before downloading anything. A newer build published while one is staged replaces the staged set.
- **Manifest refresh:** every 15 min (ETag → usually a 304).
- **API** (thread-safe): `Demand(path)`, `DownloadEverything()`, `SetPaused(bool)`, `Progress` (immutable snapshot), `HighestIndex(folder, suffix)`, `DrainInstalled(Action<string>)` (UI thread only).

### 4.3 Client: picking files up in the game

- **`MLibrary`:**
  - Open with `FileShare.Read | FileShare.Delete`.
  - `Initialize` on a missing file only sets `_missing`. **It must not demand:** every library is initialised at startup, so a demand there would queue the whole client.
  - The first `CheckImage` on a `_missing` library calls `PatchService.Demand` once (a bool check on the UI thread). Anything the game tries to draw is demanded; nothing else is.
- **`Libraries.InitLibrary`:** size each array from the larger of disk and `PatchService.HighestIndex(...)`. The manifest comes from `/api/patch` or, offline, `Patch\Manifest.bin` (review #7).
- **Bounds and growth** (review #7): arrays are sized once in the `Libraries` static constructor, so a monster or NPC number beyond the array can still arrive (first offline start, or a mid-session manifest adding a number).
  - `MonsterObject.Load` (~L208, `Libraries.Monsters[(ushort)BaseImage]`) and `NPCObject.Load` (~L67, null `BodyLibrary`) get a bounds check that falls back to an empty library and the default frames.
  - After a manifest refresh, the UI thread grows any array whose highest index went up (`Array.Resize` + new `MLibrary`s), so a later `Load` finds it.
- **`ModernIcons`** (review #10) picks its path once at static init (`MLibrary.cs` ~L23: `Data\ModernIcons.Lib` if it exists, else `Resources\ModernIcons`). Put `Data/ModernIcons.Lib` in tier 0 so it's there before the client starts.
- **Pre-request from object packets:** `MLibrary.Prefetch()` does the same one-time demand as the first draw, without drawing. It's called right where objects pick their libraries, so every packet and equipment change that sets a look is covered:
  - end of `PlayerObject.SetLibraries` (`MirObjects/PlayerObject.cs` ~L252): body, hair, weapons, weapon effects, wings, mount;
  - after `BodyLibrary` is chosen in `MonsterObject.Load` (~L150-280);
  - after `BodyLibrary` is chosen in `NPCObject.Load` (~L60).

  Each is one call per library, guarded by `if (PatchService.Active)`, no allocation.
- **Frames after a late library:** monsters and NPCs copy `BodyLibrary.Frames` at `Load` (`MonsterObject.cs` ~L276, `NPCObject.cs` ~L67). A library that arrives after the object loaded leaves it on the default frames. So after `Reopen(lib)`, walk `MapControl.ObjectsList` once. For each monster/NPC whose `BodyLibrary == lib` **and whose `Frames` is still `FrameSet.DefaultMonster` / `FrameSet.DefaultNPC`**, take `lib.Frames` and call `SetAction()` so the cached `Frame` updates (review #3).
  - The default check leaves the special cases alone: HellBomb, CaveStatue, DragonStatue and GreatFoxSpirit use fixed FrameSets (`MonsterObject.cs` ~L257-278), and Shinsu swaps library and frames after `Load` (~L571-574, ~L1365-1368).
  - Players use `FrameSet.Player`, so they need nothing.
- **`CMain.UpdateEnviroment`:** at most every 250 ms, drain up to 8 installed paths:

  | Installed file | Action |
  |---|---|
  | Known `.Lib` (dictionary built after `Loaded`) | `Reopen()`: new stream and arrays swapped in; old textures freed through the normal `DXManager` clean |
  | Current `.map` | `MapControl.ReloadCells()`, **no `ResetMap`**. It: swaps `M2CellInfo` + `Width`/`Height` + `PathFinder`; clears `Doors` (as `ResetMap` does, `GameScene.cs` ~L6718); re-adds `ObjectsList` objects to their cells; sets `TextureValid`/`FloorValid`/`LightsValid` false. The lighting, atmosphere and animation caches key on `ReferenceEquals(M2CellInfo)`, so they rebuild themselves (review #10) |
  | `Sound\*` | `SoundManager.InvalidateFile`: drop the silent cache entry |
  | Anything else | Picked up at the next start |

- **Missing map:** keep the blank-map fallback, `Demand` the `.map`, and set `MapControl.WaitingForFile` (overlay says "Downloading map…", movement input ignored until `ReloadCells`). Its tile libraries then get demanded by their first draw.
- **Missing sound:** `CachedSound` demands the file once per index, with the extension taken from the manifest.
- **Looping sounds and music** (review #4) bypass `CachedSound`: `LoopProvider.TryCreate` (`SoundManager.cs` ~L110, ~L138) gets the same one-time demand. Music that arrives mid-visit starts at the next `PlayMusic` (map change). A playing loop keeps its file open, which is why installs retry on a sharing violation (§4.2).

### 4.4 Client: UI

- **Overlay:** a small strip with text and a fill bar, showing files/bytes left, the current map download, faults, paused, and "update available".
  - **Classic:** a `MirControl` registered in `ClassicBuild`/`ClassicDispose` and listed in `tools\modern-ui-gate.txt`.
  - **Modern:** a `ModernSurface` widget.
  - Both are driven through `GameScene.Ui`.
  - Updated ≤ 4×/s and only when a shown value changes. Hidden when idle.
- **Options** (`OptionDialog`, `OptionsWindow`): Download everything (with size left), Pause background downloads, Restart to update (only when a staged update is ready, phase 4).
- **Update prompt** (phase 4): when a program update is fully staged, a Yes/Later box ("Update ready — restart now?"). Later leaves the Options button and an overlay line, and the update applies on exit anyway. Shown only when `MirScene.ActiveScene == GameScene.Scene` (client.md §3.4: a `MirMessageBox` parents to the active scene); otherwise it waits.
- **Settings** (`Load` + `Save`): `[Launcher] Mode` (default `OnDemand`), `BackgroundDownloads` (default `true`); `ConcurrentDownloads` defaults to 4 when `Mode=OnDemand`.
- **Strings:** `ClientTextKeys` + both client JSON files.

---

## 5. Phases

Four phases, each a commit. Work happens in the main session. **No workflows, no per-phase review rounds.** One review agent at the end of phase 2 only (threading and file swaps are the risky part), Opus 5.5 medium effort; findings checked against the code before fixing. Phase 4 swaps the running program's files, so its smoke check is done by hand with care, not with an extra reviewer.

| # | Phase | Main files | Done when (smoke check, a few minutes) |
|---|---|---|---|
| **1** | **Relay patch host** (§4.1, incl. the `PatchRevision.txt` publish marker and patch routes before the general slots) | `WebRelay/Program.cs`, `RelayHost.cs`, new `PatchIndex.cs` | Scratch relay over a scratch root with a planted `Mir2Config.ini`: (a) `/api/patch` lists files but not the `.ini`; (b) `curl` a 450 MB `.Lib` → hash matches, relay memory flat; (c) `curl -r` resumes; (d) web client still loads; (e) change a file without touching `PatchRevision.txt` → the revision doesn't change |
| **2** | **Client patcher** (§4.2 + §4.3, except program staging): `Mode=OnDemand` in `AMain`, `PatchService`, `InitLibrary` sizing, `FileShare.Delete`, demand on draw, **pre-request from object packets**, reopen + frames refresh, map reload, sound invalidate, **removed-file deletion**, `patch.lock` one-patcher-per-folder, library bounds/growth; client.md §3.2 exception for the patch thread (§7.1) | `Forms/AMain.cs`, `Program.cs`, `Settings.cs`, new `Patching/PatchService.cs`, `MirGraphics/MLibrary.cs`, `Forms/CMain.cs`, `MirScenes/GameScene.cs` (`MapControl`), `MirObjects/{PlayerObject,MonsterObject,NPCObject}.cs`, `MirSounds/…` | Empty scratch client against the scratch relay + scratch server: (a) core download → login → in game; (b) walk to a monster and teleport to a map that aren't on disk → both appear without a restart, the monster **animating** (frames refreshed); (c) kill the client mid-download → next start resumes; (d) delete a `.Lib` from the scratch root → it's gone from the client after the next refresh, a planted `Mods\x.Lib` stays; (e) a second client in the same folder runs without downloading and still shows a file the first one installs; (f) `Mode=Full` still patches as before |
| **3** | **UI** (§4.4 overlay + Options, no update prompt yet): overlay, Download everything, Pause, strings; docs for phases 1-3 (client.md §2.1/§5.4 — the §3.2 exception lands with phase 2 —, webclient.md §2.1/§3.2, Runbook: relay as patch host, `PatchTiers.txt`; AGENTS.md) | `MirScenes/Classic/…`, `MirScenes/Modern/…`, `Dialogs/MainDialogs.cs`, `Modern/Windows/OptionsWindow.cs`, `Shared/Language.cs`, `Client/Localization/*.json`, `Knowledgebase/…` | Overlay shows during downloads in both UIs and hides when done; Pause and Download everything work; `tools\modern-ui-gate.ps1` prints `clean` |
| **4** | **Program updates mid-session** (§4.2 "Program updates"): `Staged\` + `Staged.bin`, apply on restart/exit (copy, `Applying.bin`/`Healthy.bin`, `Old__` kept until proven, rollback at start, `--wait-pid`, safe `RepairOldFiles`), `AMain` applies a leftover staged set, update prompt + Options Restart; Runbook: publishing a client build | `Patching/PatchService.cs`, `Forms/CMain.cs` (exit), `Forms/AMain.cs`, `Program.cs`, the two Options windows, `Shared/Language.cs`, `Client/Localization/*.json`, `Knowledgebase/Runbook.md` | Scratch client in game; copy a rebuilt `Client.dll` into the scratch root: (a) prompt appears once staging completes; (b) Restart → the new build runs (check the version label); (c) lock one target file with another process → rollback, the old build still starts; (d) exit instead of restart → the update applies at exit; (e) stage a `Client.dll` that throws before `CMain` → after two starts the old build is back |

**Mandatory checks only** (AGENTS.md):
- Every phase: `tools\verify.ps1 -Only <changed projects> -NoTests` to compile.
- Phases 3 and 4: change `Shared/` (strings), so compile every consumer and run `Shared.Tests`.

No new test projects, no load or fault-injection runs.

**Scratch setup** (all three phases, never `Build\`):
- **Relay:** `WebRelay --client-data <root> --patch-root <root> --patch-tiers <file> --listen 127.0.0.1:5691`, over `%TEMP%\mir2-patch\root`.
- **Client:** built with `-o %TEMP%\mir2-patch\client`.
- **Server:** `tools\start-scratch-server.ps1`.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| First install is large (core + starter, 1+ GB, `Tiles.Lib` alone 450 MB) | Accepted (§7.2): no blank first map; Option A later can shrink it |
| Many new players saturating the upload | Patch slot pool; add a rate cap later if it bites |
| Cloudflare tunnel: free plan edge-caches only files ≤ 512 MB, doesn't edge-cache Range responses, and its terms have restricted serving mostly large files on the free plan | Check the terms before relying on it at scale; fallback is a direct relay URL in `[Launcher] Host` |
| Serving a played-from folder leaks `Mir2Config.ini` | Deny list + a clean distributable root |
| A build published while clients patch | Every file is hash-checked against the manifest that listed it; stale versions get 412; program files apply only as a complete staged set |
| A half-copied or wrong patch root deletes client files | Only patcher-recorded files are deleted; the 25% safety valve skips a cycle that would remove too much |
| A program swap fails half way, or the new build can't start | `Old__` kept until the new build reaches `CMain`; rollback in the same process or at the next start (§4.2 step 7) |
| Multiboxing (several clients, one folder) | `patch.lock`: one patcher, the others read-only |

---

## 7. Decisions (user, 2026-10-20)

| # | Question | Decision | Effect on the plan |
|---|---|---|---|
| 1 | client.md §3.2: a patch thread during the game | **Approved; update the rule for this specific case** | Phase 2 adds a narrow exception to client.md §3.2 in the same commit as the thread: the patch service may run during the game, may only do HTTP and file installs, never touches game state, `MLibrary`, scenes or the device, and hands results to the UI thread through its queue. The general "no background threads for game logic" rule stays |
| 2 | Starter tier timing | **Before launch** | `AMain` core mode downloads program + tier 0 + **tier 1** before launching (§4.2). The first map is never blank; the first install waits longer (1+ GB) |
| 3 | Existing installs on first OnDemand start | **Trust size** | A file whose size matches the manifest is recorded as current without hashing (§4.2) |
| 4 | Default `[Launcher] Mode` | **OnDemand**; retire the old patcher once this runs well | `Settings` default `Mode=OnDemand`. `Mode=Full` stays only until retirement (§8) |

---

## 8. Later (not in the 3 phases)

Add only if a real need shows up:
- Per-IP download cap and a total bandwidth cap on the relay.
- Demand a map's tile libraries straight after it loads (instead of on first draw).
- **Retire the old patcher** (user, 2026-10-20: once OnDemand runs well): remove `Mode=Full` and its `AMain` path, the `PList.gz` reader (`FileInformation`, `PatchListExtension`), `AutoPatcherAdmin` + `AutoPatcherAdminV2` (sln, Utils folder, docs), and the `[Launcher] PatchFile/NeedLogin/Login/Password` settings. A separate task once phases 1-4 have run on the live server for a while.
- **Option A**, per-sprite streaming: reuses WebRelay's `/libindex` and `/data?v=&o=&n=` routes and the web client's approach. A partially present library is a third state next to missing and present.
