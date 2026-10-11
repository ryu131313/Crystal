# Crystal Mir2 Game Client — AI Agent Reference

> **Scope:** `Client\` (`Client.exe`) — `Program.cs`, `Forms\` (`CMain` game window, `AMain` launcher/patcher, `Config`), `MirGraphics\`, `MirControls\`, `MirScenes\` + `MirScenes\Dialogs\`, `MirNetwork\`, `MirObjects\`, `MirSounds\`, `Settings.cs`, `KeyBindSettings.cs`, `Localization\`, `Resolution\`, `Utils\` — and the client-side use of `Shared\`. `LibraryEditor\` / `LibraryViewer\` appear only as the `.Lib` asset tools.
> **Not in scope:** `Server\`, `Server.MirForms\` (see `Knowledgebase/Server/server.md`).
> **Cross-boundary:** `Shared\` (packets, packet-id enums, `Monster`/`Stat`/`ItemType` enums, `Shared/Data` models, `Language.cs`) is ONE assembly compiled into both Client and Server. Any `Shared\` change is a client **and** server change and both must be rebuilt and deployed together.

**How to read the rules in §3.** This is an old, unoptimised codebase. Every NEVER/ALWAYS rule is the standard for **new and changed code**, not a description of the current code. Each rule block is followed by **Known debt** — existing breaches. Do not copy those patterns, and do not fix them in drive-by edits; fixing debt is a separate, explicitly requested task.

---

## 1. ROLE & IDENTITY

You are a **Principal .NET Game Client Engineer** working on the **Crystal** Legend of Mir 2 client (`Client.exe`, branch `FuckingAround`; `master` mirrors upstream Suprcode/Crystal). Your mandate:

1. **Frame budget first** — evaluate every change for per-spin/per-frame cost, allocations, texture uploads, sprite-batch flushes and whole-UI redraws.
2. **Main-thread discipline** — game state, UI, D3D9 device and packet *handling* belong to the UI thread. Two background threads already exist (library loader, socket IO); never add hidden cross-thread game-state access.
3. **Match lineage patterns** — `CMain`, `MirControl`/`MirScene`, `Network.Process`, `MLibrary`/`Libraries`, `MapControl` (inside `GameScene.cs`). Unlike WoOOLcraft, there is no render seam and no client database.
4. **Surgical diffs** — smallest correct change; no drive-by refactors or framework migrations.
5. **Prove impact** — when optimising, name the hot path (`CMain.Application_Idle` → `UpdateEnviroment` / `RenderEnvironment` → `MirScene.Draw` → `MapControl.CreateTexture` → `MLibrary.Draw*`) and the expected win.

**Default mindset:** WinForms `SlimDX.Windows.RenderForm` host + **SlimDX Direct3D 9 called directly** (no abstraction) + a **retained, render-target-cached `MirControl` tree** + APM TCP (`Network`). Not a modern engine: no render thread, no ECS, no DI.

---

## 2. SYSTEM ARCHITECTURE OVERVIEW

### 2.1 Process Model

```
Program.Main() [STAThread]                                   Client/Program.cs
  ├── "-tc" arg → Settings.UseTestConfig = true; #if DEBUG forces it (→ .\Mir2Test.ini)
  ├── UpdatePatcher(): .\AutoPatcher.gz exists → rename to .exe, start it "Auto", exit
  ├── Packet.IsServer = false;  Settings.Load()               (must precede any receive)
  ├── CheckResolutionSetting()  → mode enumeration fails → MessageBox + Environment.Exit;
  │                               configured Resolution unsupported → MessageBox, reset to 1024, Settings.Save(), continue
  ├── Settings.P_Patcher ([Launcher] Enabled, default true)
  │     ? Application.Run(PForm = new AMain())   launcher/patcher (namespace Launcher, WebView2, HttpClient); sets Launch
  │     : Launch = true
  ├── if (Launch) Application.Run(Form = new CMain())         game window + loop (AMain closed without launch → exit)
  │     CMain ctor: Application.Idle += Application_Idle, input events
  │     CMain_Load: ClientSize 1024x768, cursors, SlimDX.Configuration.EnableObjectTracking = true,
  │                 DXManager.Create(), SoundManager.Create()
  │     first touch of MirScene → static ActiveScene = new LoginScene()
  │        → Libraries static ctor: sync LoadLibraries() + background LoadGameLibraries thread
  ├── Settings.Save();  Application.Restart() if Program.Restart
  └── whole Main in try/catch → CMain.SaveError
exit: CMain_FormClosing → (cancelled while CMain.Time < GameScene.LogTime, unless test config/observing)
      Settings.Save(); DXManager.Dispose(); SoundManager.Dispose();
```

- No `AppDomain.UnhandledException` / `Application.ThreadException` handlers exist in Client.
- A failure inside `DXManager.Create()` is only logged; the loop keeps spinning with a null device (NRE every frame until the 100-entry log cap).
- `SlimDX.Configuration.EnableObjectTracking = true` is on in Release too (COM-object tracking overhead).

#### Threads

| Thread | Where | Does | Touches |
|---|---|---|---|
| **UI / main (STA)** | `CMain.Application_Idle`, all WinForms events | update, render, input, `Network.Process()` packet **handling**, sounds | everything |
| **Library loader** (`IsBackground`) | `Libraries` static ctor, `new Thread(LoadGameLibraries)` (`MirGraphics/MLibrary.cs`) | `MLibrary.Initialize()` on map/monster/armour/... libraries, bumps the unused `Libraries.Progress`, sets `Libraries.Loaded = true` | `.Lib` headers only. **No locks.** |
| **Socket IO** (APM threadpool) | `Network.Connection` / `ReceiveData` / `SendData` (`MirNetwork/Network.cs`) | **decodes** packets (`Packet.ReceivePacket` → `ReadPacket`) and enqueues into `ConcurrentQueue<Packet> _receiveList` | `_rawData`, `CMain.BytesReceived`, `Network.Disconnect()` on error; on a refused connect, `Sleep(100)` + `Network.Connect()` retry — including creating the max-attempts `MirMessageBox` off the UI thread |
| **NAudio playback** | `WaveOutEvent` (one shared `_OneShots` mixer + one per `LoopProvider`) | audio | NAudio internals |
| **Patcher** | `AMain._workThread`, `Task.Run` HTTP (`Forms/AMain.cs`) | downloads before the game form exists | patcher only |

**Hand-off queues:** socket → main = `Network._receiveList` (`ConcurrentQueue<Packet>`); main → socket = `Network._sendList`, flushed by `Network.Process()`. Loader → main = the `Libraries.Loaded` flag (`SelectScene` waits on `Libraries.Loaded` before starting the game). **There is no render thread.**

### 2.2 Frame Loop (`Client/Forms/CMain.cs`)

```csharp
private static void Application_Idle(object sender, EventArgs e)
{
    try
    {
        while (AppStillIdle)               // PeekMessage: no window message pending
        {
            UpdateTime();                   // CMain.Time = Stopwatch ms
            UpdateEnviroment();             // EVERY spin — uncapped
            if (IsDrawTime())               // 1000 / Math.Max(1, Settings.MaxFPS) ms
            {
                RenderEnvironment();
                UpdateFrameTime();          // FPS / DPS counters
            }
        }
    }
    catch (Exception ex) { SaveError(ex.ToString()); }
}
```

- **Busy loop, no sleep.** Update runs thousands of times per second (one core pegged). Only the device-lost branch sleeps (`Thread.Sleep(1)`). "Per frame" in Crystal means **per spin** for anything under `UpdateEnviroment`.
- **An exception anywhere** escapes the `while`, is logged, and the loop only resumes on the next `Application.Idle` (next window message) — a stall plus a log line.
- **Draw cap = `Settings.MaxFPS`** (static field, default 100 → 10 ms). It is **not** read from or written to the ini (see Known defects §3.5). Integer ms: 60 → 16 ms (62.5 fps), 144 → 6 ms (166 fps).
- **Vsync:** `Settings.FPSCap` (`[Graphics] FPSCap`, default true) sets `PresentationInterval.One` at device create, but `ResetDevice` uses `PresentInterval.Default` (inconsistent).

**`UpdateEnviroment()` order (sic — keep the spelling):**
1. once/sec `DXManager.Clean()` (texture expiry)
2. `Network.Process()` — drain `_receiveList` → `MirScene.ActiveScene.ProcessPacket(p)`; KeepAlive; flush `_sendList`; reconnect every 5 s
3. `MirScene.ActiveScene.Process()` (`GameScene.Process` ticks MapControl, objects, selected dialogs, `SoundManager.ProcessDelayedSounds()`)
4. `MirAnimatedControl.Animations[i].UpdateOffSet()`, `MirAnimatedButton.Animations[i].UpdateOffSet()`
5. `CreateHintLabel()`; `if (Settings.DebugMode) CreateDebugLabel()`

**`RenderEnvironment()` order:**
1. `DXManager.DeviceLost` → `AttemptReset(); Thread.Sleep(1); return;`
2. `Device.Clear` → `BeginScene` → `Sprite.Begin(SpriteFlags.AlphaBlend)` → `SetSurface(MainSurface)`
3. `MirScene.ActiveScene.Draw()`
4. `Sprite.End` → `EndScene` → `Present`
5. `catch (Direct3D9Exception)` → `DeviceLost = true` + `SaveError`; `catch (Exception)` → `SaveError` + `DXManager.AttemptRecovery()`

**Input:** WinForms events on `CMain` set `CMain.Shift/Alt/Ctrl/Tilde/SpellTargetLock` and `CMain.MPoint`, then forward to `MirScene.ActiveScene.OnXxx(e)` inside try/catch. Alt+Enter / SC_MAXIMIZE → `ToggleFullScreen()`. `CMain_Deactivate` clears modifiers. Screenshot keybind → `CreateScreenShot()` → `Screenshots\Image N.png`.

### 2.3 Scene Flow

| Scene | File | Stage |
|---|---|---|
| **Login** | `Client/MirScenes/LoginScene.cs` | connect, `S.Connected` → `Network.Connected = true` + `SendVersion()`, account auth |
| **Select** | `Client/MirScenes/SelectScene.cs` | character select/create; waits for `Libraries.Loaded`; picks resolution |
| **Game** | `Client/MirScenes/GameScene.cs` (~12.5k lines) | gameplay; owns `MapControl` (declared in the same file, ~L10275) |

- `MirScene.ActiveScene` is a static initialised `= new LoginScene()` (`MirControls/MirScene.cs`).
- Switch: `ActiveScene = new XScene(...)` **then** `Dispose()` the old scene (`SelectScene` case 4, `GameScene.LogOutSuccess` → `new SelectScene`, `GameScene.ReturnToLogin` → `new LoginScene()`). No `ReturnToLogin()` helper exists.
- **Resolution:** Login/Select are always 1024x768. Entering the game, `SelectScene` uses `min(server p.Resolution, Settings.Resolution)` and calls `CMain.SetResolution(1024,768 | 1280,800 | 1366,768 | 1920,1080)` — note the enum says `w1280h720` but the game uses **1280x800**. Logout/return-to-login reset to 1024x768.

### 2.4 Rendering Stack

| Layer | Technology | Key file |
|---|---|---|
| Window | `CMain : SlimDX.Windows.RenderForm`, `Application.Idle` + PeekMessage | `Forms/CMain.cs` |
| Graphics API | **Render seam** `IRenderBackend` (roadmap C3) behind the static façade `DXManager`. Backends: **Direct3D 11 via Vortice 3.8.3** (default since roadmap C4) and **SlimDX Direct3D 9** (`[Graphics] Renderer=D3D9`; removed in C5), chosen in `DXManager.Create`; D3D11 falls back to D3D9 if it cannot start. Game code uses `RenderTexture` and DXManager helpers only (SlimDX math types Vector2/3, Color4 and Matrix remain until C5). | `MirGraphics/DXManager.cs`, `MirGraphics/Rendering/` |
| 2D batching | D3D9: one D3DX `Sprite` (Begin/End per frame) + `Line`, inside `D3D9Backend`. D3D11: `D3D11Backend` emulates ID3DXSprite/ID3DXLine exactly (state save/restore at Begin/End, alpha test, anisotropic sampling, projection fixed at Begin, strict FIFO). Proven by goldens: `Knowledgebase/Client/D3D11-Parity.md`. | `Rendering/D3D9Backend.cs`, `Rendering/D3D11Backend.cs` |
| Shaders | D3D9: ps-assembly files `.\Data\Shaders\normal.ps`, `grayscale.ps`, `magic.ps`, assembled at runtime (`ShaderBytecode.AssembleFromFile`); missing file → shader null. D3D11: HLSL embedded in `D3D11Backend` (modulate + grayscale), compiled at start-up; grayscale enabled only if `grayscale.ps` exists (parity). | `Rendering/` |
| UI | retained `MirControl` tree; each `DrawControlTexture` control caches itself in a `Pool.Default` render target | `MirControls/MirControl.cs` |
| Text | GDI `TextRenderer.DrawText` into a locked `Pool.Managed` texture | `MirControls/MirLabel.cs` |
| Sprites/tiles | `MLibrary` / `MImage` (`.Lib` v2/v3), static registry `Libraries` | `MirGraphics/MLibrary.cs` |
| Map | `MapControl` (own RT + `DXManager.FloorTexture` + `DXManager.LightTexture`) | `MirScenes/GameScene.cs` |
| Lights | 10 GDI+ radial-gradient textures `DXManager.Lights` | `DXManager.cs` |
| Audio | NAudio 2.2.1 `WaveOutEvent` + `MixingSampleProvider` | `MirSounds/SoundManager.cs` |

**Device (D3D9 backend):** `DXManager.Create()` → `D3D9Backend.Create` — `X8R8G8B8` back buffer sized `Settings.ScreenWidth x ScreenHeight`, `LockableBackBuffer`, `SwapEffect.Discard`, `Windowed = !Settings.FullScreen`, `HardwareVertexProcessing` (+`PureDevice` if capable). **No `CreateFlags.Multithreaded`.** If VS/PS major < 2 the device type falls back to **Reference** (software, unusably slow). **D3D11 backend:** hardware device (feature level 11_0/10_1/10_0, BGRA support), swap chain with `Scaling.Stretch`: flip-discard (3 buffers) with vsync, blit (`SwapEffect.Discard`, 1 buffer) when uncapped — faster than flip at uncapped rates (D3D11-Parity.md); max frame latency 1 with vsync, 3 uncapped. Fullscreen = **borderless window over the screen** with a game-sized back buffer (`DXManager.UsesBorderlessFullscreen`); `CMain` maps mouse input through `DXManager.ClientToScene`.

**State setters (each flushes the sprite batch):** `DXManager.SetSurface(RenderTexture)` (no-op if same), `SetOpacity(float)`, `SetBlend(bool, rate, BlendMode)` (does `Sprite.End` + `Sprite.Begin`), `SetGrayscale(...)`, `SetNormal(...)`, `SetBlendMagic(...)`. Draw via `DXManager.Draw(...)` / `DrawOpaque(...)` (increments `CMain.DPSCounter`).

**Device lost / reset:** `DXManager.IsDeviceLostException(ex)` (D3D9: `Direct3D9Exception`; D3D11: device removed/reset) → `DeviceLost = true` → `AttemptReset()` (`TestCooperativeLevel`; `DeviceNotReset` → `ResetDevice()`). `ResetDevice()` → `CleanUp()` disposes Sprite, Line, surfaces, Floor/Light RTs, Lights, **every MImage texture in `DXManager.TextureList` and every control RT in `DXManager.ControlList`** → `Device.Reset(Parameters)` → `LoadTextures()`. Also called by `ToggleFullScreen()` and `CMain.SetResolution()`. After a reset every sprite re-decodes from disk lazily and every control re-renders (a hitch). D3D9 rule behind this: every `Pool.Default` resource must be released before `Reset`, or `Reset` fails with `InvalidCall`.

**Whole-UI scene texture:** `MirScene` has `DrawControlTexture = true` and renders **all** its children into one RT. `MirControl.Redraw()` bubbles to the parent; `MirScene.Redraw()` sets the scene `TextureValid = false`. So **any** Location/Size/Visible/Text/Index change anywhere re-renders every visible UI control next frame (plus GDI rasterisation for changed labels).

**Live overlays (2026-10-11, `[Graphics] RebuildOptimisations`, default on):** `MirScene.AddLiveOverlay(control)` (GameScene: both skill bars and the minimap) draws a top-level child into its own screen-sized RT (`MirControls/LiveOverlay.cs`), composited right after the scene texture, so its `Redraw()` re-renders only that RT. Active only while no other scene child overlaps it and its children stay inside its rectangle (checked at each scene rebuild and each overlay render); otherwise it falls back to the scene texture. Pixel parity holds because the scene RT is transparent (`BackColour = Color.Transparent`) wherever nothing else is drawn. Custom `BeforeDraw`/`AfterDraw` drawing outside the control's rectangle is not detected and would be clipped — keep it inside. Object moves/deaths call `GameScene.ObjectPositionsChanged()` (minimap/big-map dots) instead of `Scene.Redraw()`.

**Map texture:** `GameScene.DrawControl()` draws `MapControl.DrawControl()` first, then `base.DrawControl()` (UI texture), the held-item icon, `OutputLines`. `GameScene.Process` sets `MapControl.TextureValid = false` every 100 ms (`MoveTime`) and object frame code invalidates it too, so the map RT rebuilds ≥ 10x/s. `MapControl.CreateTexture()` order: `DrawFloor()` into `FloorTexture` only when `!FloorValid` → background → floor blit → `DrawObjects()` (behind-effects, per-row tiles + `M2CellInfo` objects, ghost/highlight, front effects, names/chat/poison/damages, health) → weather `ParticleEngine.Draw()` → `DrawLights()` (unless day) → item/object names → restore surface.

**Map invalidation with `RebuildOptimisations` (2026-10-11):** the 100 ms tick invalidates only when `MapControl.NeedsTimedRebuild()` finds time-driven content (an animated tile in range whose frame changes at this `AnimationCount`, weather, damage numbers, mouse moved over the map, chat/health/drop-view expiry, blindness ramp, draw-settings/lighting signature change). Everything else invalidates where it changes: object frames via `MapObject.InvalidateMap()` / `Effect.InvalidateMap()` (skipped when the object is outside the DrawObjects range plus `ViewMargin`, or `LightViewMargin` with a light), `MouseObjectID`/`TargetObjectID` setters, `PercentHealth`, `Chat()`, `AddObject`/`RemoveObject`, doors, map change, the name/colour/guild/hidden/poison/damage/health handlers, `SpellObject` frames, NPC quest blink. Rebuild counts and causes: `FrameProfiler` → `RebuildProfile.txt` with `DebugMode` on (including a "map tick reasons" line). At 1920x1080 the screen (±20 x ±17 cells) already covers the server's `DataRange` (16), so off-screen frame gating only helps at smaller resolutions. `RebuildOptimisations=False` restores the old tick and redraws for A/B checks.

### 2.5 Assets: `.Lib` libraries

- **Registry:** static fields on `Libraries` (`Prguse`, `Prguse2`, `Prguse3`, `UI_32bit`, `Title`, `Items`, `MagIcon`, ...) and arrays (`Monsters`, `NPCs`, `CArmours`, `MapLibs`, ...). Arrays are sized from the highest-numbered file in each directory (`InitLibrary`, 2-/3-digit names); `InitLibrary` **creates** missing directories.
- **Init split:** `LoadLibraries()` (sync, before login: ChrSel, Prguse, Prguse2, Prguse3, UI_32bit, Title) vs `LoadGameLibraries()` (background thread). `Libraries.Count`/`Progress` are unused (nothing reads them); `Count`'s `+ 19` single-library term is already off by one (20 singles).
- **`MapLibs = new MLibrary[400]`** in fixed bands: 0-99 Wemade Mir2, 100-199 Shanda Mir2, 200-299 Wemade Mir3, 300-399 Shanda Mir3. Map cells store the index; unassigned slots are null until `LoadGameLibraries` fills them with an empty `new MLibrary("")` (draws no-op).
- **Format** (`MLibrary.Initialize`, `LibVersion = 3`, extension `.Lib`): `int32 version (≥2)`, `int32 count`, `int32 frameSeek` (v3), `int32 offsets[count]`; at `frameSeek` (v3) a frame table → `FrameSet` (`MLibrary.Frames`); per image `int16 Width, Height, X, Y, ShadowX, ShadowY; byte Shadow; int32 Length` (+ mask header if `Shadow >> 7 == 1`), GZip-compressed A8R8G8B8 at `offset + 17`. Version < 2 → MessageBox + `Application.Exit()` **from the loader thread**.
- **Lazy upload:** first draw of an index → private `CheckImage` → `MImage.CreateTexture` on the main thread (`Pool.Managed`, gunzip into the locked rect) → added to `DXManager.TextureList`. Each `Draw*` refreshes `CleanTime = CMain.Time + Settings.CleanDelay`.
- **Expiry:** `DXManager.Clean()` once/sec disposes `MImage` and control textures unused for `Settings.CleanDelay` (const 600000 ms = 10 min). No memory budget.
- **Culling** in most `MLibrary.Draw*` overloads happens **after** `CheckImage` (`Draw(index, x, y)` pre-rejects only right/bottom), so many off-screen sprites are still decoded.
- **Tools:** `LibraryEditor\` (writes `.Lib`, embeds v3 frames; output `Build\Server Tools\LibraryEditor\`) and `LibraryViewer\` (`CMirLibraryViewer.exe`, read-only).

### 2.6 Networking (`Client/MirNetwork/Network.cs`, static)

| Setting | Value | Source |
|---|---|---|
| Transport | `TcpClient { NoDelay = true }`, APM `BeginConnect/BeginReceive/BeginSend` | `Network.cs` |
| Server | `Settings.IPAddress` `127.0.0.1`, `Settings.Port` `7000`; read from `[Network]` only when `UseConfig=true` | `Settings.cs` |
| KeepAlive | `C.KeepAlive` when idle past `Settings.TimeOut` (const 5000 ms); plus `GameScene.Process` sends one every 60 s for ping | `Network.Process` |
| Reconnect | every 5 s from `Network.Process`, plus an immediate (100 ms) retry chain on the socket thread after a refused connect; `MaxAttempts = 20` (shared by both) then error + close | `Network.cs` |
| Receive buffer | 8 KB `_rawBytes`, `_rawData` re-allocated per read | `Network.ReceiveData` |

**Frame** (`Shared/Packet.cs`): `[ushort totalLength incl. 4-byte header][short id][payload]`, little-endian `BinaryWriter`. **Max 65535 bytes per packet**; `GetPacketBytes` casts `(ushort)ms.Length` with no guard. `Compressed => true` (GZip) is used only by `S.NPCGoods`.

**Lifecycle:** `LoginScene` connect → `S.Connected` → `Network.Connected = true` + `C.ClientVersion { VersionHash = MD5(Application.ExecutablePath) }` → `S.ClientVersion` result → login packets → `SelectScene` → `S.StartGame` → `GameScene`.

**Dispatch:** `Packet.IsServer = false` → `ReceivePacket` uses `GetServerPacket(id)`. Main thread `Network.Process()` drains the **entire** queue per spin into `MirScene.ActiveScene.ProcessPacket(p)` — a manual `switch (p.Index)` in each scene, `default: base.ProcessPacket(p)`. `MirScene.ProcessPacket` (base) handles packets valid in any scene: `Disconnect`, `NewItemInfo`, `NewMonsterInfo`, `NewNPCInfo`, `NewChatItem`, `NewQuestInfo`, `NewRecipeInfo`, `NewHeroInfo`. Handlers are not individually try/caught — an exception propagates to the `Application_Idle` catch; remaining packets are drained on the next spin.

**Failure behaviour:** unknown id → factory returns null → **the whole receive buffer is discarded** silently. `ReadPacket` exception → `InvalidDataException` thrown on the socket thread with no try around the parse loop (unhandled on a threadpool thread terminates a .NET process). Socket errors → `catch { Disconnect(); }` with no logging. Lost connection → `MirMessageBox.Show(LostConnectionWithServer, true)` (closes the client on OK).

**No client database.** Game data streams on demand: `GameScene.RequestItemInfo/RequestMonsterInfo/RequestNPCInfo(index)` (deduped by `Requested*` HashSets) → `S.NewItemInfo/...` → static caches `GameScene.ItemInfoList`, `MonsterInfoList`, `NPCInfoList`, `QuestInfoList`, `RecipeInfoList`, `MapInfoList`, `HeroInfoList`, `GameShopInfoList` (they survive logout/scene change).

### 2.7 State & Persistence

| File (CWD-relative) | Owner | Contents |
|---|---|---|
| `.\Mir2Config.ini` (Release) / `.\Mir2Test.ini` (Debug or `-tc`) | `Settings.Load/Save` via `InIReader` (`Shared/Functions/IniReader.cs`) | `[Graphics]` FullScreen, Borderless, MouseClip, AlwaysOnTop, FPSCap, Resolution, DebugMode, UseMouseCursors; `[Network]` UseConfig, IPAddress, Port; `[Logs]`; `[Sound]` Volume, SoundOverLap, Music, CleanMinutes; `[Game]` AccountID, **Password (plaintext)**, view toggles, FontName, Language, `Skillbar{i}X/Y`, ...; `[Chat]`, `[Filter]`; `[Launcher]` Enabled, Host, PatchFile, Login, **Password (plaintext)**, ConcurrentDownloads (1..100), ... |
| `.\KeyBinds.ini` | `KeyBindSettings` (`CMain.InputKeys`) | one section per `KeybindOptions` member **name** |
| `.\Data\UserData\QuestTracking.ini` | `Settings.LoadTrackedQuests/SaveTrackedQuests(charName)` | tracked quests per character |
| `.\Localization\<Language>.json` | `GameLanguage.LoadClientLanguage` | UI strings (`Settings.DbLanguageJsonPath` is a declared but unused const) |
| `.\Error.txt` | `CMain.SaveError` / `AMain.SaveError` | appended errors, ≤ 100 entries per process |
| `<exe dir>\Screenshots\Image N.png` | `CMain.CreateScreenShot` | screenshots (`Application.StartupPath` — the one exe-relative path) |
| `.\Data\`, `.\Map\`, `.\Sound\` (`SoundList.lst`), `.\Data\Shaders\`, `.\Data\Cursors\` | `Settings.*Path` consts | assets |
| `.\DirectX\` (`Settings.ResourcePath`) | `AMain` | WebView2 user-data folder for the launcher |

- **Everything except screenshots is CWD-relative** (`Settings` path consts, `Error.txt`, ini). Launching from another working directory breaks asset loading and creates empty `Data`/`Map`/`Sound` dirs there.
- Not ini-backed (static defaults only): `MaxFPS` 100, `ScreenWidth/ScreenHeight` 1024x768, `FontSize` 8F, `CleanDelay` 600000, `TimeOut` 5000, `RemainingErrorLogs` 100.
- **No generic dialog-position persistence.** Only skill-bar positions (`Settings.SkillbarLocation` → `[Game] Skillbar{i}X/Y`) and a few bool toggles persist.

### 2.8 Build & Dependencies

| Item | Detail |
|---|---|
| Project | `Client/Client.csproj` (SDK-style), assembly `Client.exe` / `Client.dll` |
| TFM | `net8.0-windows7.0`, `WinExe`, `UseWindowsForms`, **`PlatformTarget x64`**, `AllowUnsafeBlocks`, `Nullable disable`, **`ImplicitUsings enable`**, default LangVersion |
| Output | `BaseOutputPath ..\Build\Client\` + `AppendTargetFrameworkToOutputPath=false` → `Build\Client\Debug\` / `Build\Client\Release\` |
| Refs | ProjectReference `..\Shared\Shared.csproj` (net8.0); `Reference SlimDX` HintPath `..\Components\SlimDX.dll`; NuGet `NAudio 2.2.1`, `Microsoft.Web.WebView2 1.0.2903.40`, `Microsoft.AspNet.WebApi.Client 6.0.0` |
| Copied | `Localization\English.json`, `Chinese.json`, `MIR2.ICO` (`CopyToOutputDirectory=Always` — the repo JSON overwrites the output copy on every build) |
| Native prereqs | SlimDX D3DX (`Sprite`, `Line`, `ShaderBytecode`) needs the DirectX 9 `d3dx9_43.dll` runtime; SlimDX's mixed-mode assembly needs a VC++ runtime (exact versions unverified) |
| Solution | `Legend of Mir.sln` defines only `Debug|Any CPU` / `Release|Any CPU`; the client csproj's conditional groups only set `GenerateAssemblyInfo` / `NoWarn` |

**Compile check for agents:** `dotnet build C:\Mir2\Client\Client.csproj -o <scratchpad>\client-build` (same command as the root index) — never let output land in `Build\`. **Never** pass a global `-p:BaseIntermediateOutputPath` (it flows to the `Shared` ProjectReference, so both projects restore into one `project.assets.json` and the build breaks). After any `Shared\` edit, also run `dotnet build C:\Mir2\Server\Server.Library.csproj` (outputs to `Server\bin`, safe). Never build `Server.MirForms\Server.csproj` without `-o <scratch>` (default output `Build\Server\Debug`/`Release`; Release is the live server folder), and never build `Legend of Mir.sln` with the dotnet CLI at all — with or without `-o` (see root `agents.md` "Build & Output Rules").

---

## 3. CRITICAL GAME CLIENT RULES

### 3.1 Memory & GC Management

Hot path = anything reached from `UpdateEnviroment` (every spin) or `RenderEnvironment` (every drawn frame): `Network.Process` → handlers, `ActiveScene.Process` → `MapControl.Process` → `MapObject.Process`, `MirScene.Draw` → `MapControl.CreateTexture` → `MLibrary.Draw*`, `CreateHintLabel`, `CreateDebugLabel`.

#### NEVER

- **NEVER** use **LINQ** (`Where`, `Select`, `OfType`, `Count(pred)`, `ToList`, `OrderBy`) in the hot path.
- **NEVER** build strings per spin/frame (`string.Format`, `$""`, `+`) for labels/debug text — update on change or throttle to ≥ 1 s.
- **NEVER** `new List<T>` / `Dictionary` / `byte[]` per spin in update or map draw — hoist to fields, `Clear()` and reuse.
- **NEVER** `new Texture(...)` per draw, or `new MLibrary(...)` ad hoc — go through `Libraries.X.Draw*` / `GetSize` / `GetTrueSize` (they call the private `CheckImage`).
- **NEVER** add synchronous file I/O to `Process`/`Draw`/packet handlers. (Texture decode in `CheckImage` is the existing, unavoidable exception.)
- **NEVER** box value types or capture closures in per-spin code.
- **NEVER** call `GC.Collect()`; the cache policy is `DXManager.Clean()` + `Settings.CleanDelay`.
- **NEVER** hold packet byte arrays beyond the handler; copy out the values.
- **NEVER** set a label's `Text`, or any control's `Location`/`Size`/`Index`/`Visible`, every frame unless the value actually changed — each change invalidates the **whole UI scene texture**.

#### ALWAYS

- **ALWAYS** remember update is uncapped: gate periodic work with `if (CMain.Time >= _nextXTime) { _nextXTime = CMain.Time + N; ... }` (as `GameScene.Process` does with `MoveTime`).
- **ALWAYS** use `for (int i = ...)` over `foreach` on lists in hot paths (pattern: `MapControl.Process`, the animation loops in `UpdateEnviroment`).
- **ALWAYS** resolve objects by ID with `MapControl.Objects.TryGetValue(id, out MapObject ob)`; never scan `MapControl.ObjectsList` for an ID.
- **ALWAYS** invalidate (`TextureValid = false`) instead of re-rendering eagerly; one rebuild per frame is coalesced.
- **ALWAYS** group draws by render state; every `SetSurface/SetOpacity/SetBlend/SetGrayscale` flushes, and `SetBlend` restarts the sprite.
- **ALWAYS** use `ArrayPool<byte>.Shared` / `Span<T>` when modifying network buffer code (net8 on both Client and Shared).
- **ALWAYS** `using`-dispose GDI objects (`Font`, `Bitmap`, `Graphics`, brushes) created in texture/draw/screenshot paths.

#### Known debt — existing breaches (do not copy these patterns; do not fix them in drive-by edits — fixing debt is a separate, explicitly requested task)

| Location | Breach |
|---|---|
| `CMain.CreateDebugLabel` (`Forms/CMain.cs`) | with `Settings.DebugMode`: ~10 `string.Format` + 2 LINQ `Count(...)` over `TextureList`/`ControlList` **every spin**; windowed mode sets `Program.Form.Text` every spin |
| `MapControl.CreateTexture` (`GameScene.cs` ~L10549-10634) | `Objects.Values.OfType<ItemObject>()` x2 + `OfType<MonsterObject>()` per map rebuild (≥ 10/s); `DrawBackground` does `FileName.Replace` + `StartsWith` per rebuild |
| `Network.ReceiveData` | `new byte[]` per read; builds a `List<byte>` by **re-serialising every received packet** (`GetPacketBytes()`) just to count `CMain.BytesReceived` |
| `Network.Process` send / `Packet.GetPacketBytes` | `List<byte>` + `AddRange` + `ToArray`; two `MemoryStream`s per packet |
| `MLibrary.CheckImage` → `MImage.CreateTexture` | `ReadBytes` + GZip/MemoryStream on the main thread mid-frame → hitches when new monsters/effects appear |
| `CachedSound` (`MirSounds/Libraries/`) | whole-file decode to `float[]` on the main thread on first play, LINQ `Take` per chunk |
| `SoundManager.ProcessDelayedSounds` | `Where(...).ToList()` every spin while any delayed sound is pending |
| `CMain.CreateScreenShot` | 5 `Font`s, the `Bitmap`, the `StringFormat` and the back-buffer `Surface` from `GetBackBuffer(0, 0)` never disposed (the leaked back-buffer ref may make a later `Device.Reset` fail; impact unverified) |
| `CMain_KeyDown/KeyUp` | `Enum.Parse(typeof(Keys), InputKeys.GetKey(...))` per key event |
| `GameScene.Process` | repositions tooltip labels (`ItemLabel`, `MailLabel`, `MemoLabel`, `GuildBuffLabel`) every spin (cheap only because the `Location` setter early-outs on equal values) |
| `MirLabel` defaults | `OutLine = true` → 5 GDI draws per label rasterise |

### 3.2 Threading & Concurrency

```
┌──────────────────────────────────────────────────────────────────┐
│ MAIN (UI) THREAD ONLY:                                           │
│ CMain loop, Network.Process + all ProcessPacket handlers,        │
│ MirScene/MirControl tree, GameScene, MapControl, MapObject,      │
│ DXManager.Device/Sprite/textures, MLibrary.Draw/CheckImage/      │
│ GetSize/GetTrueSize/VisiblePixel, SoundManager calls             │
├──────────────────────────────────────────────────────────────────┤
│ BACKGROUND (existing, allowed scope only):                       │
│ Library loader → MLibrary.Initialize() + Libraries.Progress/     │
│                  Loaded flags                                    │
│ Socket IO      → Packet.ReceivePacket/ReadPacket + enqueue       │
│ Patcher (AMain)→ HTTP/Task before the game form exists           │
└──────────────────────────────────────────────────────────────────┘
```

#### NEVER

- **NEVER** use `async`/`await`, `Task.Wait()`, `.Result` or `Thread.Sleep` in the loop, `Process`, `Draw` or packet handlers (the device-lost `Sleep(1)` is the only sanctioned sleep).
- **NEVER** mutate `GameScene`, `MapControl`, `MapObject`, any `MirControl`, or `GameScene.*InfoList` from a non-UI thread.
- **NEVER** touch `DXManager.Device`, `Sprite`, any `Texture`/`Surface`, or call `MLibrary.Draw*`/`CheckImage`/`GetSize`/`GetTrueSize`/`VisiblePixel` off the UI thread — the device has no `Multithreaded` flag and `MLibrary` streams are unlocked. The loader thread may only call `Initialize()`.
- **NEVER** let packet `ReadPacket` code (in `Shared\`) read or write client static state — it runs on the socket thread.
- **NEVER** reorder `MLibrary.Initialize()` so `_initialized = true` moves later without adding a lock (the early flag is what stops the main thread opening a second stream during background init); conversely never assume a game library is usable before `Libraries.Loaded`.
- **NEVER** use `Control.Invoke`/`BeginInvoke` for game state.
- **NEVER** add a new background thread for game logic; extend the existing hand-off queues instead.

#### ALWAYS

- **ALWAYS** keep socket callbacks to "decode + enqueue"; handling happens in `Network.Process()` on the main thread.
- **ALWAYS** post cross-thread results to the main thread via a `ConcurrentQueue` drained in a `Process()` call, or a single-writer flag (the `Libraries.Loaded` pattern).
- **ALWAYS** dispose SlimDX resources on the main thread.
- **ALWAYS** keep shutdown order in `CMain_FormClosing`: `Settings.Save()` → `DXManager.Dispose()` → `SoundManager.Dispose()`.

#### Known debt — existing breaches (do not copy; do not drive-by fix)

- **Library init race** (`MirGraphics/MLibrary.cs`): `_initialized = true` is set as the first statement of `Initialize()`, before `_images`/`_indexList` exist → main-thread `CheckImage` sees an initialised library with null `_images` and silently draws nothing; if the main thread reaches an uninitialised library first, both threads can run `Initialize()` on the same instance. Mitigated in practice because `SelectScene` waits for `Libraries.Loaded`.
- **`Network.Disconnect()` from the socket thread** nulls `_client`, `_sendList`, `_receiveList` statics while the main thread may be iterating them (`Process` re-checks `_receiveList != null` each loop).
- **`CMain.BytesReceived`/`BytesSent`** are `double`s updated non-atomically from the socket thread (debug stat only).
- `.Lib` version < 2 → `MessageBox` + `Application.Exit()` called from the loader thread; a corrupt/truncated `.Lib` makes `Initialize` rethrow on the loader thread → unhandled → process exit.
- `Network.Connection` (BeginConnect callback, threadpool) creates `_receiveList`/`_sendList` and, on `SocketException`, does `Thread.Sleep(100); Connect();` on the socket thread — which can build the max-attempts `MirMessageBox` (parents to `ActiveScene`) off the UI thread and burn the 20 attempts far faster than one per 5 s.
- `AMain` blocks on `.Result` for WebView2 environment creation.

### 3.3 Rendering & Device Rules

#### NEVER

- **NEVER** create a `Pool.Default` texture/surface (render target) that is not either (a) a `MirControl.ControlTexture` registered with `DXManager.ControlList.Add(this)` at creation, or (b) a `DXManager` static released in `DXManager.CleanUp()`. Anything else makes `Device.Reset` fail (alt-enter, resolution change, lock screen).
- **NEVER** set `Settings.ScreenWidth/ScreenHeight` directly — call `CMain.SetResolution(w, h)` (it resizes, resets the device; Floor/Light/scene RTs are sized from those settings).
- **NEVER** dereference `MImage.Data` except via `MImage.VisiblePixel` after a `TextureValid` check — it is a raw pointer kept after `UnlockRectangle`, freed by `DXManager.Clean()` (10 min idle) and by `ResetDevice()`. Never extend this pattern.
- **NEVER** change the `.Lib` reader (header layout, `+17` pixel offset, GZip A8R8G8B8, v3 frame table, mask quirk) without updating `LibraryEditor` which writes it. The mask quirk (`CreateTexture` builds the mask with `Width/Height` and reads `Length`, not `MaskWidth/MaskHeight/MaskLength`) is load-bearing; don't "fix" it alone.
- **NEVER** put a map library outside its `MapLibs` band (0-99 / 100-199 / 200-299 / 300-399).
- **NEVER** let draw code throw — null-check, bounds-check indices and objects in every `Draw`/`CreateTexture`/`DrawControl` path. Any non-D3D exception between `Sprite.Begin` and `Present` triggers `AttemptRecovery()` (ends the sprite/scene mid-frame) and costs one of the 100 log entries.
- **NEVER** call raw `Device.SetRenderState`/`SetPixelShader`/`Sprite.End/Begin` from gameplay code — use the `DXManager` helpers; if none exists for the state you need, add one to `DXManager` that restores state.
- **NEVER** set `MapControl.TextureValid = false` (or `FloorValid = false`) per spin or per packet — tie map invalidation to animation/frame ticks (the 100 ms `MoveTime` gate, object frame steps); each invalidation rebuilds the whole map RT on the next draw.
- **ALWAYS** invalidate the map where map-drawn state changes (`ob.InvalidateMap()` for object state, `MapControl.TextureValid = false` otherwise), and add anything drawn from time alone to `MapControl.NeedsTimedRebuild()` — with `RebuildOptimisations` there is no blind 100 ms rebuild to hide a missing invalidation. Note `FloorValid = false` and `MapControl.Redraw()` do **not** rebuild the map texture (`Redraw()` rebuilds the UI scene).
- **NEVER** call `GameScene.Scene.Redraw()` for a change only one dialog shows — redraw that dialog (it bubbles to the scene, or re-renders only its live overlay).

#### ALWAYS

- **ALWAYS** `Dispose()` the `Surface` returned by `GetSurfaceLevel(0)` after restoring the previous surface.
- **ALWAYS** restore state you change: `SetSurface(old)`, `SetOpacity(old)`, `SetBlend(false)`, `SetGrayscale(false)` / `SetNormal`.
- **ALWAYS** let `RenderEnvironment` own device-lost recovery: set `DXManager.DeviceLost = true` on `Direct3D9Exception`; never call `Device.Reset` yourself.
- **ALWAYS** register new libraries in `Libraries` (field + `Initialize()` in `LoadLibraries` for pre-login UI or `LoadGameLibraries` for game art). `Libraries.Count`/`Progress` (the `+ 19` term, `Progress++`) are read by nothing — keep them consistent if you touch them, but they have no effect.
- **ALWAYS** bounds-check data-driven library indices: `if ((ushort)img < Libraries.Monsters.Length)` (pattern: `NPCDialogs.cs` ~L630, `SpellObject.cs`).

#### Known debt — existing breaches (do not copy; do not drive-by fix)

- `MonsterObject.Load` indexes `Libraries.Monsters[(ushort)BaseImage]` unguarded (~L208) — a `Monster` value ≥ array length throws **after** the `MapObject` ctor registered the object, leaving a half-built object in `MapControl.Objects`.
- `MapControl.DrawFloor` indexes `Libraries.MapLibs[cell.BackIndex/MiddleIndex/FrontIndex]` with no range check — an index outside 0-399 throws `IndexOutOfRangeException` every floor rebuild.
- ~~`MapControl.DrawLights` sets raw blend render states and calls `Sprite.End/Begin` directly~~ — **moved behind helpers 2026-10-10 (roadmap C3)**: `SetLightAdditiveBlend`, `SetLightMultiplyBlend`, `RestartSpriteAlphaBlend` (same states, same order).
- `DXManager.SetGrayscale(float, Color)` etc.: `tintcolor.R / 255` is integer division → tint components are 0 or 1.
- `DXManager.AttemptReset` / `AttemptRecovery` swallow exceptions with empty catches.
- `CleanUp()` disposes `CurrentSurface` but not `MainSurface` separately when they differ (possible back-buffer ref leak; impact unverified).
- Each `MLibrary` keeps its `FileStream` open for the process lifetime (hundreds of handles; `.Lib` files stay locked while the client runs).

### 3.4 Error Handling

**Principle: keep the client running; degrade gracefully.**

| Failure | Actual behaviour |
|---|---|
| `Direct3D9Exception` in render | `DeviceLost = true` + `SaveError` → `AttemptReset()` each frame until `TestCooperativeLevel` succeeds |
| Other render exception | `SaveError` + `DXManager.AttemptRecovery()` |
| Exception in update / handler | escapes to `Application_Idle` catch → logged; loop resumes on next window message |
| Input-handler exception | the scene dispatch in each `CMain` input handler is try/caught → `SaveError`, but the `TargetSpellLockOn` `Enum.Parse` and (KeyUp) `CreateScreenShot` run before the try and escape to WinForms |
| Unknown packet id | whole receive buffer silently discarded |
| Malformed packet | `InvalidDataException` on the socket thread (unhandled) |
| Socket error / lost connection | silent `Disconnect()`; `Network.Process` shows `LostConnectionWithServer` box (closes client) |
| Missing `.Lib` | `Initialize` leaves `_images == null` → draws are no-ops |
| Corrupt/truncated `.Lib` | `Initialize` rethrows: on the loader thread → unhandled → process exit; on the main thread → `Application_Idle` catch, retried every draw (new `FileStream` each time) |
| `DXManager.Create` failure | logged; null device; NRE per frame until log cap |

#### NEVER

- **NEVER** swallow an exception silently — minimum `CMain.SaveError(ex.ToString())`.
- **NEVER** call `SaveError` per spin/frame: the budget is `Settings.RemainingErrorLogs` = **100 `SaveError` calls (entries, each possibly a multi-line stack trace) per process**, after which all logging stops silently.
- **NEVER** assume a server object exists — check `MapControl.Objects.TryGetValue`, `GameScene.User`, `GameScene.Scene`, `MapControl != null`.
- **NEVER** show a `MirMessageBox` from code that can run while `MirScene.ActiveScene` is not the scene you think (it parents to `ActiveScene`; e.g. during `GameScene()` construction ActiveScene is still the old `SelectScene`). Guard with `MirScene.ActiveScene == GameScene.Scene`.
- **NEVER** log passwords (`Settings.Password`, `Settings.P_Password`).

#### ALWAYS

- **ALWAYS** validate packet fields at the handler boundary (object IDs, indexes, array bounds, null strings).
- **ALWAYS** keep the two catch blocks in `RenderEnvironment`.
- **ALWAYS** wrap optional I/O (screenshots, ini writes, file logs) in try/catch.
- **ALWAYS** guard handlers that can arrive before `S.UserInformation` / `S.MapInformation` with `if (User == null || MapControl == null) return;`.

#### Known debt — existing breaches (do not copy; do not drive-by fix)

- `CMain.SaveError` doesn't check `Settings.LogErrors` itself (~12 callers gate on it, the rest don't), writes one CWD-relative `.\Error.txt` forever (no date, no dedupe).
- `Network.cs` catch blocks (`BeginReceive`, `EndReceive`, `BeginSend`, `SendData`) disconnect or swallow without logging.
- ~~`Network.ReceiveData` continued after `Disconnect()`; malformed packets threw on the socket thread~~ — **fixed 2026-10-10 (C1)**: returns after disconnect, decodes inside a try (logs to `Error.txt` and disconnects), and `Program.Main` logs unhandled exceptions to `Error.txt`.
- `CMain_KeyUp` calls `Program.Form.CreateScreenShot()` **outside** its try block → an I/O failure escapes to WinForms.
- ~~`CMain_KeyDown/KeyUp` `Enum.Parse` on a modifier binding~~ — **fixed 2026-10-10 (C1)** (`IsBoundKey`, never throws). Original note: `Enum.Parse(typeof(Keys), GetKey(TargetSpellLockOn))` was outside the try; a modifier binding makes `GetKey` return `"Ctrl + X"`, so every key press throws an unhandled exception.
- `GameScene.ProcessPacket` handlers mostly deref `User`/`MapControl` without guards (rely on server packet order).
- `GameLanguage.LoadClientLanguage` swallows all exceptions (`catch { }`).

### 3.5 Settings, Scenes, Packets & UI Invariants

#### NEVER

- **NEVER** assume paths resolve against the exe directory — use `Settings.*Path` consts and accept CWD-relative behaviour.
- **NEVER** expect `Settings.MaxFPS` to be configurable from the ini until someone adds both the `Load` read and the `Save` write; keep the `Math.Max(1, ...)` guard in `IsDrawTime`.
- **NEVER** `Dispose()` the old scene before assigning `MirScene.ActiveScene = new X(...)` — `MirScene.Dispose` sets `ActiveScene = null` if it is still `this`, and `Network.Process` then NREs.
- **NEVER** assign a shared/static `Font` to `MirLabel.Font` or `MirTextBox.Font` — the setter calls `MirControl.ScaleFont(value)`, which **disposes the Font you pass**. Never pre-wrap with `ScaleFont(...)` either (double DPI scaling).
- **NEVER** touch a `MirMessageBox`'s own fields inside its button handler — every button's first Click handler is `Dispose()`; capture what you need in locals.
- **NEVER** use raw WinForms controls for in-game text input — use `MirTextBox` (native box kept off-screen at -32000,-32000 and texture-rendered; `ApplyNativeTextBoxState()` is the only place that sets native Location/Visible).
- **NEVER** rename a `KeybindOptions` member (it is the `KeyBinds.ini` section name) — append new ones.
- **NEVER** hard-code user-visible English — use `ClientTextKeys`.
- **NEVER** call `Network.Enqueue` expecting delivery while disconnected (it silently no-ops when `_sendList == null`).
- **NEVER** exceed 65535 bytes per packet — split, or `Compressed => true` (don't copy the compressed-read slice idiom `rawBytes[4..(length - 4)]`).
- **NEVER** insert packet ids mid-enum; ids are implicit ordinals of `ServerPacketIds`/`ClientPacketIds` (`Shared/Enums.cs` ~L1434/~L1717).
- **NEVER** renumber, reorder or insert into persisted/wire enums: `MirAction` (stored by byte value in every v3 `.Lib` frame table written by `LibraryEditor`; implicitly numbered — append only), `Monster : ushort` (= `Data\Monster\NNN.Lib`), `Stat : byte`, `ItemType`, `Spell`, `BuffType`, `ItemSet`, `ObjectType`, `MirClass`, `MirGridType`, ... Add members at the end for sequential enums (with an explicit value where the enum already uses them), or take an unused value in the right band for banded enums (`Spell` by class, `Stat` < 255) — server.md §3.4. Check `[Flags]` (`PoisonType`, `BindMode`, `SpecialItemMode`, `RequiredClass`, `RequiredGender`) before `Enum.IsDefined`.

#### ALWAYS

- **ALWAYS** use `CMain.Time` (ms, monotonic `Stopwatch`) for timers/cooldowns; `CMain.Now` only for wall-clock display; never `DateTime.Now` in game logic.
- **ALWAYS** test with the right ini: Debug builds force `Mir2Test.ini`. "Setting doesn't take effect in Debug" usually means the wrong file was edited.
- **ALWAYS** add new `Settings` fields to both `Load()` and `Save()` with the same section, key and variable.
- **ALWAYS** add a packet to all three Shared places (id enum, class, factory case) — a missing factory case makes the receiver discard its whole buffer.
- **ALWAYS** ship Client and Server built from the same commit after any `Shared\` change.
- **ALWAYS** set `Library` + `Index` (or `Size`) before using `Center`/`Right`/`Bottom`... for `Location` (helpers read the current `Size`).
- **ALWAYS** drive per-frame dialog logic from `GameScene.Process()` explicitly — `MirControl` has **no** `Process()`.
- **ALWAYS** register new dialogs in the `GameScene()` ctor (`Parent = this, Visible = false`), null them in `GameScene.Dispose`, add them to the `KeybindOptions.Closeall` hide list.
- **ALWAYS** create map objects via `new XObject(objectID)` + `Load(p)` (the `MapObject(uint)` ctor self-registers in `MapControl.Objects`/`ObjectsList`); remove via `ob.Remove()`.
- **ALWAYS** clear `MirItemCell.Locked` in the server-reply handler of any new item operation, and add every new `MirGridType` to `MirItemCell.ItemArray` (its `default:` throws `NotImplementedException`).
- **ALWAYS** dispose `MirAnimatedControl`/`MirAnimatedButton` instances — they register in a static `Animations` list removed only in `Dispose`.

#### Known debt — existing breaches (do not copy; do not drive-by fix)

- `LoginScene.Login(S.LoginSuccess)` AfterAnimation handler calls `Dispose()` **before** `ActiveScene = new SelectScene(...)` (ActiveScene briefly null; harmless only because nothing runs in between).
- Shared fonts assigned to labels (the setter disposes them): `MainDialogs.cs` `Font = ChatFont` (~L603/965/1039), `QuestDialogs.cs` `Font = QuestFont` (~L838/858).
- `ScaleFont` pre-wrapped (double DPI scaling): `NPCDialogs.cs` ~L3537/3548.
- Hard-coded English: `MentorDialog.cs` `"Lv "` labels, `GameshopDialog.cs` `"Testing - "`.
- `[Network]` and `[Logs]` ini sections are load-only (`Save()` writes neither).

#### Known defects (found 2026-10-08, not fixed)

- ~~**`Settings.MaxFPS` is not in the ini.**~~ **Fixed 2026-10-10 (roadmap C1):** `[Graphics] MaxFPS` is read (min 10) and saved. Commit f7ed8f3c ("change max fps to use config setting") made `IsDrawTime` read `Settings.MaxFPS`, but `Settings.Load/Save` never read/write it — the cap is always 100.
- ~~**`LevelEffect` loads with the wrong default**~~ (fixed 2026-10-10, C1): `Reader.ReadBoolean("Game", "LevelEffect", Effect)` (`Settings.cs` ~L250).
- ~~**`ExpandedHeroBuffWindow` saves the wrong variable**~~ (fixed 2026-10-10, C1): `Reader.Write("Game", "ExpandedHeroBuffWindow", ExpandedBuffWindow)` (`Settings.cs` ~L371).
- **`[Logs] LogErrors`/`LogChat`** are now also written by `Save()` (fixed 2026-10-10, C1). `LogChat` is unused. `LogErrors` is checked by ~12 callers (`if (Settings.LogErrors) CMain.SaveError(...)`) but not inside `SaveError`.
- **`S.RefineCancel`** has a class and a `GameScene` handler but no `GetServerPacket` case (only the `ClientPacketIds` one) — dead.
- **`C.PurchaseGuildTerritory` has no `GetClientPacket` case** in `Shared/Packet.cs`; `GuildTerritoryDialog .cs` (filename has a space) sends it, so the server discards that connection's receive buffer and its handler is unreachable.
- **Client version check is weak:** `LoginScene.SendVersion` hashes `Application.ExecutablePath` — on .NET 8 that is the `Client.exe` apphost stub, not `Client.dll`, so a code-only rebuild likely produces the same hash (inference, unverified). The server's default `VersionPath` is `.\Mir2.Exe`, which doesn't match `Client.exe`. Never treat a passing version check as proof of matching `Shared` builds.
- ~~Looping-sound duplicate key / leaks~~ — **fixed 2026-10-10 (C1)**: replay restarts the loop, `StopSound` removes, timeout cleanup disposes. Original note: **`SoundManager.PlaySound(index, loop: true)`** did `_loopingSounds.Add` (throws on a duplicate key); `StopSound` disposes but doesn't remove; `CheckSoundTimeOut` removes loop entries without disposing. **Live:** `ElementsEffect` (`Effect.cs` ~L275/296) passes `loop: true` for the user's own type-3/4 elemental orbs (`GameScene.cs` ~L5582); a repeat for the same orb before the 30 s timeout check throws `ArgumentException` from `_loopingSounds.Add` into the `Application_Idle` catch.
- `SelectScene` overwrites the user's `Settings.Resolution` with the server cap when lower, and that is saved on exit.

---

## 4. CODE STYLE & PATTERNS

### 4.1 .NET 8 Constraints

- `net8.0-windows7.0`, default LangVersion (C# 12). **`ImplicitUsings` enabled** — existing files rely on implicit `System`, `System.Drawing`, `System.Windows.Forms` usings.
- `Nullable` disabled — don't enable per file.
- `AllowUnsafeBlocks` — confine to texture-locking helpers (`DXManager`, `MLibrary`, `MirLabel`, `MirTextBox`); comment why.
- UI is `MirControl`. Real WinForms only for: the `CMain` host, `MirTextBox`'s hidden native `TextBox`, `AMain` launcher, `Config` form.
- Block-scoped `namespace X { }` in existing files; match the file you edit. Preserve UTF-8 BOM and line endings.

### 4.2 Project Conventions

| Area | Convention |
|---|---|
| Namespaces | `Client`, `Client.MirControls`, `Client.MirScenes`, `Client.MirScenes.Dialogs`, `Client.MirObjects`, `Client.MirGraphics`, `Client.MirGraphics.Particles`, `Client.MirNetwork`, `Client.MirSounds`, `Client.MirSounds.Libraries`, `Client.Resolution`, `Client.Utils`, `Client.Resources`; `Launcher` (AMain). Global namespace: `KeyBindSettings.cs`, `MirObjects/Frames.cs`, `MirObjects/MapCode.cs`, `MirControls/MirAnimatedButton.cs`, `MirAnimatedControl.cs`, and most Shared types |
| Packet aliases | `using S = ServerPackets; using C = ClientPackets;` (no `G`) |
| Config | static `Settings` + `InIReader`, explicit `Reader.ReadX/Write` lines in `Load/Save` |
| Time | `CMain.Time` (long ms) |
| Paths | `Settings.DataPath`, `MapPath`, `SoundPath`, `MonsterPath`, ... (CWD-relative consts) |
| Globals | `GameScene.Scene`, `GameScene.User`, `GameScene.*InfoList`, `MapControl.Objects`, `MapObject.User/TargetObject/MouseObject`, `CMain.*`, `MirScene.ActiveScene` |
| Strings | `GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.X[, args])` |
| Sounds | `SoundManager.PlaySound(SoundList.X)`; controls: `Sound = SoundList.ButtonA` |
| Images | `Libraries.Prguse2.Draw(index, point, colour)`; arrays `Libraries.Monsters[n]` |
| Chat / centre text | `GameScene.Scene.ChatDialog.ReceiveChat(text, ChatType.System)`; `GameScene.Scene.OutputMessage(msg)` |

### 4.3 Dependency Injection

No DI container; static singletons (`CMain`, `Settings`, `DXManager`, `Libraries`, `Network`, `SoundManager`, `MirScene.ActiveScene`, `GameScene.Scene`). Hang new runtime state off `GameScene` fields/statics or `MapControl`. Do not introduce `Microsoft.Extensions.DependencyInjection` without an explicit migration task.

### 4.4 UI Control Pattern (`MirControl`)

**Lifecycle & behaviour (`MirControls/MirControl.cs`):**
- **Tree:** setting `Parent` adds to `Parent.Controls`; **list order = draw order (last on top), reverse = hit-test order**. `InsertControl(0, c)` puts a control at the bottom (MapControl). `BringToFront()`; `Sort = true` raises the control (and ancestors) on mouse-down.
- **`Visible`** getter is effective visibility (`Parent.Visible && _visible`). `Show()`/`Hide()` are virtual and early-return on current state; setting `Visible = true` directly **bypasses** overrides like `FriendDialog.Show` (which sends `C.RefreshFriends`).
- **Events:** `Click`, `DoubleClick`, `BeforeDraw`, `AfterDraw`, `MouseEnter/Leave/Move/Down/Up/Wheel`, `KeyDown/KeyUp/KeyPress`, `Shown`, `BeforeShown`, `Disposing`. `OnMouseClick` plays `Sound` then raises `Click`. Key events recurse children last→first and stop at `e.Handled`; they reach **hidden** controls too (only `Enabled` is checked), so guard key handlers with `if (!Visible) return;` and set `e.Handled` only when the key was consumed.
- **Flags:** `Movable` (drag, clamped; position not persisted), `Modal` (swallows all mouse input — `MirMessageBox`), `NotControl` (never hit-tested), `PixelDetect` on `MirImageControl`.
- **Draw:** `Draw()` → `BeforeDrawControl → DrawControl → DrawChildControls → DrawBorder → AfterDrawControl`. `DrawControl()` re-creates the RT only when `!TextureValid`. `Dispose()` is recursive and idempotent; clears events, texture, parent link, `ActiveControl`/`MouseControl`.
- **No automatic `Process()`** for controls or dialogs.

**Controls:** `MirImageControl` (`Library`, `Index`, `UseOffSet`, `AutoSize`; draws directly each frame), `MirButton` (`Index/HoverIndex/PressedIndex`, `Sound`), `MirLabel` (GDI text texture; `AutoSize`, `OutLine`, `DrawFormat`), `MirTextBox`, `MirMessageBox`, `MirInputBox`, `MirAmountBox`, `MirDropDownBox`, `MirCheckBox`, `MirScrollingLabel`, `MirAnimatedControl`, `MirAnimatedButton`, `MirItemCell`, `MirGoodsCell`, `GameShopCell` / `GameShopViewer` (file `MirControls/MirGameShopCell.cs`).

**Cached-RT control template** (copy when a control renders its own texture; same shape as `MirScene.CreateTexture`, `MapControl.CreateTexture`). In the ctor set `DrawControlTexture = true;` (without it `DrawControl` never calls `CreateTexture`). If the texture caches child controls, override `Redraw()` to set `TextureValid = false;` (as `MirScene` does — base `Redraw()` only bubbles to the parent), or invalidate yourself whenever content changes.

```csharp
if (Size != TextureSize) DisposeTexture();
if (ControlTexture == null || ControlTexture.Disposed)
{
    DXManager.ControlList.Add(this);   // REQUIRED: CleanUp() releases it before Device.Reset
    ControlTexture = new Texture(DXManager.Device, Size.Width, Size.Height, 1,
                                 Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
    TextureSize = Size;
}
Surface oldSurface = DXManager.CurrentSurface;
Surface surface = ControlTexture.GetSurfaceLevel(0);
DXManager.SetSurface(surface);
DXManager.Device.Clear(ClearFlags.Target, BackColour, 0, 0);
/* draw children / content */
DXManager.Sprite.Flush();
DXManager.SetSurface(oldSurface);
TextureValid = true;
surface.Dispose();                       // release the GetSurfaceLevel ref
```

**Message box template** (`Dialogs/InventoryDialog.cs`):

```csharp
MirMessageBox box = new MirMessageBox(
    GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ExtraSlots8), MirMessageBoxButtons.OKCancel);
box.OKButton.Click += (o, a) => Network.Enqueue(new C.Chat { Message = "@ADDINVENTORY" });
box.Show();   // parents to MirScene.ActiveScene; the box is already disposed when your handler runs
```

#### Recipe — add a dialog

1. `Client/MirScenes/Dialogs/MyDialog.cs` (or an existing grouped file such as `NPCDialogs.cs`):
   ```csharp
   namespace Client.MirScenes.Dialogs
   {
       public sealed class MyDialog : MirImageControl
       {
           public MirButton CloseButton;
           public MyDialog()
           {
               Index = 20; Library = Libraries.Prguse3;   // background art defines Size — set FIRST
               Movable = true; Sort = true;
               Location = Center;                          // after Index/Library
               CloseButton = new MirButton
               {
                   HoverIndex = 361, Index = 360, PressedIndex = 362,
                   Library = Libraries.Prguse2, Location = new Point(Size.Width - 23, 3),
                   Parent = this, Sound = SoundList.ButtonA,
               };
               CloseButton.Click += (o, e) => Hide();
           }
           public void Process() { /* only if needed; called from GameScene.Process */ }
       }
   }
   ```
2. `GameScene.cs`: field `public MyDialog MyDialog;` with the others; in the `GameScene()` ctor `MyDialog = new MyDialog { Parent = this, Visible = false };` — **construction order = z-order**, and after any dialog it references via `GameScene.Scene.X`. Null it in `GameScene.Dispose` (~L10199).
3. If it ticks: add `MyDialog.Process();` to `GameScene.Process()` (~L1183). It runs whether visible or not — guard on `Visible` yourself.
4. Open-side effects: override `Show()`/`Hide()` (`if (Visible) return; Visible = true; Network.Enqueue(...)`).
5. Open via keybind (recipe below) or a `MenuDialog` button (`MainDialogs.cs`); add `MyDialog.Hide()` to the `KeybindOptions.Closeall` case (`GameScene.cs` ~L668).
6. Strings via `ClientTextKeys`; text input via `MirTextBox`; fonts via `new Font(Settings.FontName, 9F)`.
7. Position persistence only if required: new `Settings` fields + Read/Write in `Load/Save` (the `SkillbarLocation` pattern).

#### Recipe — add a keybind

1. `KeyBindSettings.cs`: append a member to `enum KeybindOptions` (end of enum).
2. In `KeyBindSettings.New(list)` add `list.Add(new KeyBind { Group = "Dialogs", Description = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.MyOpenClose), function = KeybindOptions.MyThing, RequireAlt = 2, RequireShift = 2, RequireTilde = 2, RequireCtrl = 2, Key = Keys.None });` (0 = must be up, 1 = must be down, 2 = don't care). `Keylist` and `DefaultKeylist` are both built by `New` and indexed in parallel by `KeyboardLayoutDialog`.
3. Add `ClientTextKeys.MyOpenClose` (UI-string recipe).
4. `GameScene_KeyDown` switch (`GameScene.cs` ~L504+): `case KeybindOptions.MyThing: if (!MyDialog.Visible) MyDialog.Show(); else MyDialog.Hide(); break;`
5. Existing users' `KeyBinds.ini` lacks the section → default is used. Never rename members.

#### Recipe — add a UI string

1. `Shared/Language.cs`: add `MyKey,` to `enum ClientTextKeys`; add `{ nameof(ClientTextKeys.MyKey), "English text {0}" },` to `GameLanguage.ClientTextMap`.
2. Add `"MyKey"` to `Client/Localization/English.json` and a translation to `Chinese.json` (`"Text"` object). A JSON whose key count differs from the map is **rewritten** from the in-memory map at load (unknown keys dropped, missing ones added in English).
3. Use `GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.MyKey, arg)`; a missing key returns the key name.
4. Enum display names: register the enum type in `localizationEnumTypes` (`Language.cs`), add `"EnumType_Member"` to the JSON `"Enum"` object, display with `.ToLocalizedString()`.
5. Server strings use `ServerTextKeys`/`ServerTextMap` (server doc). Keys are looked up by **name**, so their order is free.

### 4.5 Server Packet Handler Pattern

```csharp
// GameScene.ProcessPacket switch (~L1340)
case (short)ServerPacketIds.ObjectTurn:
    ObjectTurn((S.ObjectTurn)p);
    break;

private void ObjectTurn(S.ObjectTurn p)
{
    if (p.ObjectID == User.ObjectID && !Observing) return;     // self-echo ignored unless observing
    if (MapControl.Objects.TryGetValue(p.ObjectID, out MapObject ob))
        ob.ActionFeed.Add(new QueuedAction { Action = MirAction.Standing, Direction = p.Direction, Location = p.Location });
}
```

- One private handler per packet in the scene that receives it (mostly `GameScene`); packets valid in all scenes go in `MirScene.ProcessPacket`.
- Handlers run on the main thread and may touch UI. Mutate cheaply and invalidate (`TextureValid = false`) rather than re-render.
- Client → server: `Network.Enqueue(new C.X { ... })`.
- Spawn/update pattern (`GameScene.ObjectMonster`): if `MapControl.Objects` holds a `MonsterObject` → `mob.Load(p, true)`, else `new MonsterObject(p.ObjectID).Load(p, false)`.
- `GameScene.cs` is intentionally huge — add `#region`s, don't split it without a dedicated refactor task.

#### Recipe — add a server→client packet (client half; server half in `Knowledgebase/Server/server.md`)

1. `Shared/Enums.cs`: append the member at the **end** of `ServerPacketIds` (current last: `GameDataChanged`; re-count before relying on an ordinal).
2. `Shared/ServerPackets.cs`:
   ```csharp
   public sealed class MyThing : Packet
   {
       public override short Index { get { return (short)ServerPacketIds.MyThing; } }
       public int Value; public string Text;
       protected override void ReadPacket(BinaryReader reader)  { Value = reader.ReadInt32(); Text = reader.ReadString(); }
       protected override void WritePacket(BinaryWriter writer) { writer.Write(Value); writer.Write(Text); }
   }
   ```
   Read/Write in identical order and types. Keep < 64 KB. Override `Observable => false` if spectators must not receive it.
3. `Shared/Packet.cs` `GetServerPacket` switch: `case (short)ServerPacketIds.MyThing: return new S.MyThing();` — missing ⇒ the client discards its whole receive buffer.
4. `GameScene.ProcessPacket`: `case` + `private void MyThing(S.MyThing p)` (template above), or `LoginScene`/`SelectScene`/`MirScene` base if needed outside the game.
5. Client → server is the mirror: `ClientPacketIds` (append; current last `RemoveStoragePassword`), `Shared/ClientPackets.cs`, `GetClientPacket` case, server `MirConnection.ProcessPacket` handler; client sends `Network.Enqueue(new C.X { ... })`.
6. Compile both `Client\Client.csproj` and `Server\Server.Library.csproj`; deploy client + server together.

### 4.6 MapObject / Rendering Pattern

- `MapObject` (`MirObjects/MapObject.cs`) is a mirror of server state — no authoritative simulation. Prediction is limited to local movement (`MapControl.CheckInput`), corrected by `S.UserLocation`.
- **Statics:** `MapObject.User`, `Hero`, `HeroObject`, `MouseObject`, `TargetObject`, `MagicObject` (ID setters resolve through `MapControl.Objects`).
- **Ctor `MapObject(uint)` self-registers** (removes an existing object with the same ID, adds to `MapControl.Objects` + `ObjectsList`). `Remove()` unregisters and removes from the cell.
- **State:** `ActionFeed` (packets append `QueuedAction`, `Process()` consumes), `CurrentAction`, `Frame`, `FrameIndex`, `DrawFrame`, `Effects`, `Buffs`, `BodyLibrary`, `NameLabel`/`ChatLabel`, `Damages`.
- **Subclasses:** `UserObject : PlayerObject` (local player), `PlayerObject`, `MonsterObject`, `NPCObject`, `ItemObject`, `HeroObject`, `UserHeroObject`, `SpellObject`, `DecoObject`. Effects: `Effect`, `Missile`, `SpecialEffect`, `BuffEffect`, `LightEffect`, ... (`MirObjects/Effect.cs`); `new Effect(MLibrary lib, int baseIndex, int count, int duration, MapObject owner | Point source, long starttime = 0, bool drawBehind = false)` added to `ob.Effects` or `MapControl.Effects`. Weather: `MirGraphics/ParticleEngine.cs`.
- **Frames** (`MirObjects/Frames.cs`): `FrameSet : Dictionary<MirAction, Frame>`; `Frame(start, count, skip, interval, effectstart, effectcount, effectskip, effectinterval)`; draw index = `Frame.Start + Frame.OffSet * (byte)Direction + FrameIndex` with `OffSet = Count + Skip` (`skip = -count` ⇒ non-directional).
- The map is a cached texture rebuilt ≥ 10x/s — profile (debug label FPS/DPS) before adding per-object work in `CreateTexture`.

#### Recipe — add a monster appearance / frames

1. `Shared/Enums.cs` `enum Monster : ushort`: add a member with an **explicit value = library file number** (`Data\Monster\NNN.Lib`). Shared ⇒ server rebuild (`MonsterInfo.Image` stores it).
2. Build `NNN.Lib` with `LibraryEditor` (v3) and embed frames; without them `MonsterObject` uses `BodyLibrary.Frames ?? FrameSet.DefaultMonster`.
3. Ship the `.Lib` to every client (patcher). `Libraries.Monsters.Length` = highest file number present on that client + 1. A missing `NNN.Lib` throws in `MonsterObject.Load` only if no higher-numbered file exists; otherwise the monster is silently invisible (nothing logged).
4. Only if special: add `case Monster.X:` to the `BodyLibrary`/`Frames` switches in `MonsterObject.Load` (special ranges: `Siege` −940, `Gates` −950, `Pets` −10000), and attack/effect cases in `SetAction()` / `ProcessFrames()`. Sounds: `BaseSound = (ushort)BaseImage * 10` (see `SoundRules.txt` at repo root).
5. Non-standard layouts may use a static `FrameSet` in `Frames.cs` (like `GreatFoxSpirit`); prefer embedding frames in the `.Lib`.

### 4.7 MLibrary / Texture Pattern

```csharp
Libraries.Prguse2.Draw(index, new Point(x, y), Color.White, offSet: true);  // UI lib
Libraries.Monsters[(ushort)image].Draw(index, location, colour, true);       // array lib — bounds-check first
Size s = Libraries.Items.GetTrueSize(index);
```

- Overloads: `Draw`, `DrawBlend`, `DrawTinted`, `DrawUp`, `DrawUpBlend` (check the exact signature in `MLibrary.cs` before use). Callers never touch `MImage` directly.
- First draw of an index decodes on the main thread; no preload facility exists. Don't fight `DXManager.Clean()` without profiling.

### 4.8 Client Data & User Prefs

- System data (`GameScene.ItemInfoList`, `MonsterInfoList`, ...) is filled only by `New*Info` handlers and the LiveDb push (`S.GameDataChanged`) — read-only elsewhere. Use `GameScene.RequestItemInfo/...` for missing entries. Updates go through the upsert helpers (`GameScene.UpsertItemInfo` / `UpsertQuestInfo` / `UpsertMonsterInfo` / `UpsertNPCInfo`): an `ItemInfo` is updated **in place** (`ItemInfo.CopyFrom`) because every `UserItem.Info` points at it — never replace or append a second copy (fixed F7, 2026-10-10: `NewItemInfo`/`NewQuestInfo` used to append, so a relog bound stale copies).
- **LiveDb push** (`GameScene.GameDataChanged`, server: web admin live edits): sections Items (in place), Monsters, NPCs, Quests (upsert), Magics / HeroMagics (server-owned fields copied onto the user's / hero's `ClientMagic` by `Spell`; level, key and cooldown stay), GameShop (remove by `GIndex`, then add; plus a removed list), MapInfoInvalidated (drops the cached `BigMapRecord` and disposes its buttons; re-requested by the big map / NPC dialogs). Chunks accumulate; the one marked `Last` runs `User.RefreshStats()`, the hero's, skill bars, the open game shop and disposes the item tooltip if its item changed. Errors are caught and logged once per packet (`if (Settings.LogErrors) CMain.SaveError(...)`).
- User prefs: new `Settings` field + `Load`/`Save` lines; keybinds `CMain.InputKeys.Save(CMain.InputKeys.Keylist)` → `KeyBinds.ini`; quest tracking `Settings.SaveTrackedQuests(charName)`.
- Shared DTOs live in `Shared/Data/`. `Client*` types in `ClientData.cs` are wire-only (ctor `(BinaryReader)`): changing their `Save`/ctor is a wire change (rebuild + ship client and server together). Models whose ctor takes `(BinaryReader, int version = int.MaxValue, int customVersion = int.MaxValue)` (e.g. `ItemInfo` in `ItemData.cs`, `UserItem`) are also server-DB formats: the client reads the packet layout via the `int.MaxValue` defaults (always newest), while the server needs an `Envir.Version` bump + gate (server.md §4.3). `GameShopItem` is the exception: the client reads it with the separate packet ctor `(reader, true)` / `Save(writer, packet)`, so a change must update both its packet and DB paths.

### 4.9 Naming & Files

- PascalCase types/methods, camelCase locals, `_camelCase` private fields — but Crystal mixes styles; match the surrounding file.
- New dialog: `Client/MirScenes/Dialogs/MyFeatureDialog.cs`. New world object: `Client/MirObjects/MyObject.cs` extending `MapObject`/`MonsterObject`.
- Comment **why** for D3D9 state changes, surface/blend restores, packet-order assumptions and invalidation choices. No XML docs on every member.

---

## 5. DEPLOYMENT & TELEMETRY

### 5.1 Current Observability

| Mechanism | Location | Output |
|---|---|---|
| `CMain.SaveError(string)` | `Forms/CMain.cs` | `.\Error.txt` (append, ≤ 100 entries per process shared by `CMain.SaveError` and `AMain.SaveError`; each entry may be a multi-line stack trace; ~31 `CMain` call sites) |
| Debug overlay | `CMain.CreateDebugLabel`, `[Graphics] DebugMode` | FPS, DPS (sprite draws/s), time, map coords, control, object count, `CMain.DebugText`, ping, bytes sent/recv, TLC (live MImage textures), CLC (live control textures). Fullscreen → label; windowed → window title |
| SlimDX object tracking | `EnableObjectTracking = true` in `CMain_Load` | COM leak visibility (usage of `ObjectTable` not present in repo) |

No profiler, perf log, packet monitor, ILogger or telemetry exists.

### 5.2 Logging Rules

- **ALWAYS** log through `if (Settings.LogErrors) CMain.SaveError(ex.ToString());` — never `Console.WriteLine`.
- **ALWAYS** prefix new subsystem messages (`"[MapControl] ..."`, `"[Network] ..."`).
- **ALWAYS** rate-limit: the global 100-entry budget is shared by everything.
- **ALWAYS** guard and throttle debug-label work behind `Settings.DebugMode`.
- **NEVER** log per spin/frame to disk; **NEVER** add Serilog/NLog without a migration task; **NEVER** log credentials.
- New metrics: extend the debug label (throttled) or add counters like `CMain.DPSCounter`. If OpenTelemetry/ILogger is ever introduced: aggregate per second, no hot-path export, thin wrapper over `SaveError`.

#### Known debt — existing breaches (do not copy; do not drive-by fix)

- `RenderEnvironment`'s `catch (Exception)` calls `SaveError` every drawn frame while an exception recurs (budget gone in ~100 frames).
- `AMain.cs` ~L306 writes `Error.txt` directly with `File.AppendAllText`, bypassing the budget.
- `Network.Connection` (threadpool) calls `SaveError` off the main thread.
- Messages carry no subsystem prefix; `Settings.cs` logs `ex.Message` instead of `ex.ToString()`.

### 5.2.1 Golden render tests (roadmap C1/C3/C4)
- `tools\rendertest.ps1 -Mode Compare -Backend D3D9` (exact, must be IDENTICAL x15) and `-Mode Parity -Backend D3D11` after **any** change under `Client/MirGraphics`, `MirControls` texture code, `MapControl` drawing or the backends. `-Mode Smoke [-FullScreen]` runs the real loop for N seconds. Goldens live in `%USERPROFILE%\Mir2RenderGoldens\D3D9` (outside git). Details and the list of proven D3DX behaviours: `Knowledgebase/Client/D3D11-Parity.md`.
- **NEVER** re-record goldens to make a failing compare pass; record only after an intended D3D9 change has been reviewed.
- **NEVER** add `using SlimDX.Direct3D9` or any `Vortice` namespace outside `Client/MirGraphics/Rendering/`; new device features go into `IRenderBackend` (both backends) plus a DXManager helper, and get a golden scenario.

### 5.3 Deployment Checklist

**Agent-verifiable:**

- [ ] Compiled to a scratch output — nothing written under `Build\`; if `Shared\` changed, `Server\Server.Library.csproj` compiles too.
- [ ] New packet: enum appended, class added, factory case added, handler added (§4.5).
- [ ] New render target: registered in `DXManager.ControlList` or released in `DXManager.CleanUp()` (code review).
- [ ] Settings changes: `Load()`/`Save()` lines symmetric — same section, key, variable (code review).

**Hand to the user (agents never touch `Build\`):**

- [ ] Smoke test (login → select → play → logout, Alt+Enter, resolution change): `Error.txt` unchanged; TLC/CLC in the debug overlay don't climb without bound over ~10 min. Debug builds use `Mir2Test.ini`, Release `Mir2Config.ini`.
- [ ] If `Shared\` changed: client and server deployed from the same commit; server `[General] VersionPath` updated if the client exe changed (the hash is weak).
- [ ] New assets (`.Lib`, sounds, `Localization\*.json`) included in the AutoPatcherAdmin `PList.gz` patch set and present beside `Client.exe`.

### 5.4 Runtime Layout (working directory = exe directory)

```
Build\Client\<Configuration>\        (user's run folder — agents never write here)
├── Client.exe / Client.dll / Shared.dll / SlimDX.dll
├── NAudio*.dll, Microsoft.Web.WebView2*.dll, ...
├── Localization\English.json, Chinese.json   (overwritten each build)
├── Mir2Config.ini | Mir2Test.ini, KeyBinds.ini, Error.txt   (generated)
├── Data\     *.Lib, per-class subdirs, Monster\NNN.Lib, Shaders\*.ps, Cursors\*.CUR, UserData\QuestTracking.ini
├── Map\      *.map
├── Sound\    SoundList.lst, *.wav / *.mp3
├── Screenshots\
├── DirectX\  (WebView2 data for the launcher)
└── AutoPatcher.gz / .exe (optional, patcher self-update)
```

Data/Map/Sound are not part of the build; they must be placed beside the exe.

---

## APPENDIX A — Quick Decision Tree

```
Does this code run under CMain.Application_Idle (update, render, handler, input)?
├── YES → Main thread. No async/blocking, no LINQ, no per-spin allocations or strings.
│         Update runs every spin — throttle with CMain.Time gates.
│         Draw via MLibrary.Draw* / DXManager helpers; restore any state you change.
│         Changing a control property redraws the whole UI texture — only on change.
│         New render target? → DXManager.ControlList.Add(this) or CleanUp().
└── NO  → Which thread?
          ├── Socket IO (Network.ReceiveData / Packet.ReadPacket)
          │     → decode + enqueue only; never touch client statics or UI.
          ├── Library loader (LoadGameLibraries)
          │     → MLibrary.Initialize() only; publish via Libraries.Loaded.
          └── Patcher (AMain) → async/HTTP fine; never touches game state.
Does it change Shared\ ?
└── YES → cross-boundary: append enum ids, factory case, rebuild + ship client AND server.
```

## APPENDIX B — Glossary

| Term | Meaning |
|---|---|
| **CMain** | Game window (`SlimDX.Windows.RenderForm`) + static loop/time/input state (`Forms/CMain.cs`) |
| **AMain** | Launcher/patcher form (namespace `Launcher`) shown before `CMain` when `[Launcher] Enabled` |
| **Application_Idle** | The game loop: `UpdateEnviroment` every spin, `RenderEnvironment` when `IsDrawTime()` |
| **CMain.Time** | Monotonic ms since start (`Stopwatch`); the game clock |
| **ActiveScene** | `MirScene.ActiveScene` — Login / Select / Game |
| **MirControl** | Retained UI control base; may cache itself in a render target |
| **TextureValid** | "cached RT is current" flag; set false to trigger one re-render |
| **ControlList / TextureList** | `DXManager` registries of control RTs / `MImage` textures released on reset and expiry |
| **DXManager** | Static SlimDX Direct3D9 manager (device, sprite, surfaces, shaders) — not an abstraction |
| **DeviceLost** | Real D3D9 device-lost flag → `AttemptReset()` → `ResetDevice()` |
| **MLibrary / MImage** | `.Lib` sprite archive / one image (lazy texture) |
| **Libraries** | Static registry of all `MLibrary` instances; `Loaded` set by the background loader |
| **MapControl** | Map renderer + object registry (`Objects`, `ObjectsList`), declared in `GameScene.cs` |
| **Shared** | One assembly with packets, enums, models, language — used by Client and Server |
| **APM** | `BeginReceive`/`BeginSend` async socket pattern |
| **TLC / CLC** | Debug overlay counts of live MImage / control textures |

## APPENDIX C — Related Docs

| Path | Content |
|---|---|
| `Knowledgebase/Server/server.md` | Server: Envir thread + mob threads, `MirConnection` handlers, DB versioning, web admin |
| `Client/Settings.cs` | All `Mir2Config.ini` keys |
| `Client/KeyBindSettings.cs` | Keybind enum, defaults, `KeyBinds.ini` |
| `Shared/Packet.cs`, `Shared/Enums.cs`, `Shared/ServerPackets.cs`, `Shared/ClientPackets.cs` | Wire format, packet ids, factories |
| `Shared/Language.cs` | `ClientTextKeys`, `GameLanguage` |
| `SoundRules.txt` (repo root) | Monster sound index mapping |
| `LibraryEditor/`, `LibraryViewer/` | `.Lib` authoring / viewing tools |
| `agents.md` (repo root) | Knowledgebase index: cross-boundary rules, build & output rules, folder map |

---

*Last updated: 2026-10-08 (initial version, written from a code survey of branch FuckingAround). Keep this file current when you change the areas it covers.*
