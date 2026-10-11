# Direct3D 11 backend and golden parity (roadmap C1 / C3 / C4) — 2026-10-10

> Status: **C1, C3 and C4 are done.** D3D11 is the **default renderer** (since the automated QA pass, 2026-10-10). `[Graphics] Renderer=D3D9` selects D3D9, and D3D11 falls back to D3D9 by itself if it cannot start. What's left is the roadmap's ~2-week default cycle in real play before C5 removes D3D9/SlimDX.

## What exists

| Piece | Where | Notes |
|---|---|---|
| Render seam | `Client/MirGraphics/Rendering/IRenderBackend.cs` | Shaped like the D3D9 + ID3DXSprite subset the client uses. `RenderTexture` replaces SlimDX `Texture`/`Surface` in game code. |
| D3D9 backend | `Client/MirGraphics/Rendering/D3D9Backend.cs` | The original DXManager device code, moved verbatim. Byte-identical to the pre-seam client (gate below). Removed with SlimDX in C5. |
| D3D11 backend | `Client/MirGraphics/Rendering/D3D11Backend.cs` | Vortice 3.8.3 (Direct3D11, DXGI, D3DCompiler). Emulates the D3D9 device state and D3DX sprite semantics; HLSL compiled at start-up (`d3dcompiler_47.dll`, in Windows 10+). Falls back to D3D9 (logged in `Error.txt`) if it cannot start. |
| Façade | `Client/MirGraphics/DXManager.cs` | Same state logic as before; every device call goes through `DXManager.Backend`. New helpers: `CreateTexture`, `CreateRenderTarget`, `LockTexture`/`UnlockTexture`, `BeginFrame`/`EndFrame`/`Present`, `Clear`, `Flush`, `SetSpriteTransform`, `DrawLines`, `SetLightAdditiveBlend`/`SetLightMultiplyBlend`/`RestartSpriteAlphaBlend`, `CaptureBackBuffer`, `IsDeviceLostException`, `UsesBorderlessFullscreen`, `ClientToScene`, `ApplyWindowBounds`. |
| Golden harness | `Client/RenderTest/RenderTestRunner.cs`, `tools/rendertest.ps1` | `Client.exe --rendertest --out <dir> [--bench N]`, `--rendercompare <golden> <candidate> [--parity] [--waive Name:N]`, `--smoke N [--fullscreen]`. |
| Frame profiler | `Client/MirGraphics/FrameProfiler.cs` | Update / draw / present ms, worst frame, once per second in the debug label (`[Graphics] DebugMode`). |
| Rebuild profiler | `FrameProfiler.AddRebuild` / `NoteInvalidation`; hooks in `MapControl.DrawControl` / `CreateTexture` (floor), the `MapControl.TextureValid` shim, `MirScene.CreateTexture` / `Redraw` | Map, floor, UI-scene and live-overlay (`ovl`) render-target rebuilds per second and CPU ms per rebuild (debug label, "Rebuilds:"). With `DebugMode` on it also records the first game-code caller of each valid→invalid flip (one stack walk per rebuild) and appends a 10 s report to `RebuildProfile.txt` in the client folder from the thread pool. Map time includes the floor rebuild. Measurement shim for the rebuild work (2026-10-11). |

**Goldens** live outside the repo, in `%USERPROFILE%\Mir2RenderGoldens\D3D9`: 15 scenarios × `.bgra` (exact bytes) + `.png` (viewing). S01–S12 were recorded from the **untouched** pre-seam client (commit 7c4a6548). S13–S15 were added later and recorded from the D3D9 backend, which the C3 gate had already proven byte-identical.

## How to run

```powershell
powershell -ExecutionPolicy Bypass -File tools\rendertest.ps1 -Mode Compare -Backend D3D9      # exact: must be IDENTICAL x15
powershell -ExecutionPolicy Bypass -File tools\rendertest.ps1 -Mode Parity  -Backend D3D11     # parity (+ documented waivers)
powershell -ExecutionPolicy Bypass -File tools\rendertest.ps1 -Mode Smoke   -Backend D3D11 [-FullScreen]
powershell -ExecutionPolicy Bypass -File tools\rendertest.ps1 -Mode Record  -Backend D3D9      # only after an intended D3D9 change
```

- The run folder `%TEMP%\mir2-rendertest\run` holds the scratch build plus **junctions** to `Build\Client\Debug\Data`, `Map` and `Sound` (read only). Never `Remove-Item -Recurse` it.
- Test and smoke modes never save settings and play no sound. They point the network at `127.0.0.1:1`, so they cannot reach a real server; WoOOLcraft listens on 7000 on this machine.
- Compare an intended rendering change against the goldens before re-recording them.

## Results (this machine, 2026-10-10)

| Gate | Result |
|---|---|
| **C3** — D3D9 through the seam vs the pre-seam goldens | **12/12 byte-identical** (later 15/15) |
| **C4** — D3D11 vs the D3D9 goldens, parity mode | **14/15 byte-identical**, S14 waived (2 pixels) |
| Real loop (smoke, login scene, 100 fps cap) | D3D9: draw 0.12 ms, present 0.38 ms. D3D11: draw 0.15 ms, present 0.24 ms. Both screenshots look identical. |
| D3D11 borderless fullscreen (smoke) | Window covers the 3440×1440 screen; 1024×768 back buffer stretched by DXGI |
| Harness benchmark (`-Bench`, back buffer + Present per frame) | D3D11 median 0.5–1.9 ms vs D3D9 0.4–1.8 ms; tails dominated by the busy desktop. Follow-up for C6 frame pacing. |

**Waiver — S14_render_targets (≤ 4 pixels).** The scenario deliberately triggers the D3DX "projection fixed at Begin" quirk (below), which magnifies render-target content about 3.4×. Two pixels on one magnified edge land exactly on a pixel centre, and float rounding tips the fill-rule tie the other way. The game never draws into a non-screen-sized target, so this edge cannot occur in play.

## D3D9 / D3DX behaviour the D3D11 backend reproduces (each proven by a golden)

| # | Behaviour | Evidence |
|---|---|---|
| 1 | No half-pixel offset in D3D11: D3DX applied D3D9's −0.5 internally. Plain ortho projection, raw UVs. | S01–S12 identical |
| 2 | ID3DXSprite samples **anisotropic** for every draw. That is exact for 1:1 sprites, and the only match for scaled ones (point fails S09/S14; linear fails the anisotropically shrunk sprites in S09). | S09, S14 |
| 3 | Vertex colours are 8-bit and **truncated** like SlimDX `Color4.ToArgb` (0.5 → 127). Probed directly against SlimDX.dll. | S05, S10 |
| 4 | No separate alpha blending: the alpha channel uses the alpha form of the colour factors (SourceColor → SourceAlpha, …). | S04, S08 |
| 5 | `Sprite.Begin(AlphaBlend)` enables the alpha test (Greater 0); `Begin(DoNotSaveState)` does **not**, so blending sprites draw zero-alpha texels under the opacity factor. | S13 part 3 |
| 6 | `Sprite.Begin` **resets the pixel shader** to none. Grayscale set inside a DoNotSaveState sprite ends at the next `SetBlend` rate change. | S13 part 5; with the opposite assumption S13 fails (14.8 % within 1) |
| 7 | `Sprite.End` after `Begin(AlphaBlend)` restores the device state saved at Begin. This includes the quirk where `SetBlend(false)` drops an active `SetOpacity` while `DXManager.Opacity` still says so. | S05, S13 |
| 8 | ID3DXSprite builds its projection from the viewport **at Begin**, while D3D9 `SetRenderTarget` resets the viewport. After a mid-sprite switch to a differently sized target, draws scale until the next Begin, and the −0.5 shift scales with them (V/S). | S14 |
| 9 | ID3DXLine draws a strip, includes each segment's end pixel, ignores the device blend state (plain alpha blending) and begins its own pass, so it projects with the current target. | S10, S12 |
| 10 | The grayscale effect exists only if `Data\Shaders\grayscale.ps` exists, the same as D3D9. normal.ps and magic.ps don't exist; their callers are dead. | S06 |
| 11 | Draws are strictly in submission order; a texture's Lock/Unlock between Draw and Flush shows the new pixels, as with D3DX deferral. | S11, S10 |
| 12 | Device reset / swap-chain resize re-creates everything DXManager owns. | S15 |

Kill switches for diagnosis only: environment variables `MIR2_D3D11_SAMPLER=point|linear|auto|anisotropic` and `MIR2_D3D11_SPRITE_PS=keep`. Defaults are the proven behaviour.

## Fullscreen and input

- **D3D9** keeps exclusive fullscreen.
- **D3D11 fullscreen** (`UsesBorderlessFullscreen`) is a borderless window over the screen. The back buffer stays at the game resolution and DXGI stretches it (flip model, `Scaling.Stretch`; no `SetFullscreenState`, so tearing presents stay legal).
- **Input:** `CMain` maps mouse positions and `MouseEventArgs` through `DXManager.ClientToScene`.
- **Aspect ratio:** non-4:3 monitors stretch the picture, as a 1024×768 exclusive mode on the same monitor would by default. A letterbox option is a C6 polish item.

## Independent review (2026-10-10) and fixes

A read-only review of the seam and both backends found, and this commit fixed:
- **Critical (D3D9, default renderer):** the screenshot path disposed the live back buffer. SlimDX returns the same managed `Surface`, so after one PrintScreen every render-target switch threw. Now it never disposes it. Smoke mode takes a mid-run screenshot and keeps rendering to prove it (0 `Error.txt` entries on both backends).
- **High (D3D11):** a driver reset (TDR) mid-flush left sprites queued, so cleanup re-flushed a dead device forever. The queue is now always emptied, and textures are not flushed on a removed device.
- **High (D3D11):** a failed re-create or `ResizeBuffers` left the backend half-built while reporting OK. It now reports NotReset, so the reset retries.
- **Medium (D3D11):**
  - Screenshots read an undefined flip-discard buffer after Present; now they read a copy taken just before Present.
  - Shaders compile before the swap chain is bound to the window, so a compiler failure leaves a clean window for the D3D9 fallback.
  - Borderless fullscreen is no longer `TopMost`, so Alt+Tab works.

Low items, fixed in the follow-up commit:
- tearing only in uncapped borderless fullscreen, matching D3D9 exclusive + Immediate; windowed D3D11 no longer tears, like D3D9 windowed. **Revised 2026-10-11:** without the tearing flag, windowed flip-model presents were paced by DWM to the refresh rate (user saw "vsync" with `FPSCap=False`; D3D9 windowed Immediate ran uncapped). Uncapped presents now carry `AllowTearing` windowed too; a composed window still does not tear, only independent flip can;
- `SetMaximumFrameLatency(1)`. **Revised 2026-10-11:** 1 only with vsync; uncapped uses 3 (the D3D9 driver default). With 1 the CPU waited for the GPU on every Present: uncapped login scene 1,704 fps vs D3D9 ~10,900 (2 = 2,846, 3 = 5,287, 4+ ~6,000). A queued frame is a fraction of a millisecond uncapped, so the input-lag reason only applies to vsync. Re-applied on `Reset` (vsync can change). Also 2026-10-11: the back-buffer copy for `CaptureBackBuffer` (flip-model back buffers are undefined after Present) is no longer taken every frame — `IRenderBackend.Present(keepFrame)`, requested by `DXManager.KeepFrames` (golden/QA modes) or `DXManager.CaptureNextFrame()` (screenshot key: saved right after the next Present; plain smoke runs capture their two frames the same way). **Swap model (2026-10-11):** uncapped (`FPSCap=false`) now uses the blit model (`SwapEffect.Discard`, 1 buffer, no tearing flag — Present(0) is not paced by DWM); vsync keeps flip-discard (3 buffers, independent flip in borderless fullscreen, variable refresh). A windowed flip Present cost ~0.13 ms vs ~0.08 ms blit. Release bench, uncapped login scene: windowed D3D11 flip ~6,700-7,100 fps → blit ~11,100-11,700 (D3D9 ~10,000-10,600); borderless fullscreen flip ~7,600 → blit ~8,500. Debug builds run ~10% slower (D3D11 debug layer when the SDK layers are installed) — benchmark Release with `-tc`. `Reset` recreates the device when vsync changes (ResizeBuffers cannot switch swap models). Scripted QA passes on the blit model; the vsync/flip path is untested by the harness (test mode forces `FPSCap=false`);
- `SetRenderTarget` to the current target no longer rebinds.

Originally left as follow-ups:
- **Low:** tearing when `FPSCap=false` in borderless fullscreen (D3D9 windowed Immediate did not tear); up to ~2 frames more latency with 3 buffers and no frame-latency wait; a render-target rebind on every `SetBlend` (performance only).
- **Known debt, not new:** `MImage.CreateTexture` leaks on a corrupt `.Lib` retry; the screenshot `Bitmap` is not disposed.

## Automated QA (replaces the manual checklist where a script can drive it)

`tools\rendertest.ps1 -Mode Smoke -Backend D3D11 -Qa -SmokeSeconds 32` drives the real game window through these steps, taking a screenshot after each and checking the back-buffer size, that the frame isn't blank, and that the device isn't lost:
- resolutions 1024 → 1280×800 → 1366×768 → 1920×1080 → 1024;
- Alt+Enter into borderless fullscreen;
- a resolution change while fullscreen;
- Alt+Enter back to windowed;
- minimise and restore.

`-Mode Parity -Bench 400` adds three churn rounds: release every texture and render target, reset, and redraw all scenes. It logs process memory around them.

| Check (2026-10-10) | D3D11 | D3D9 |
|---|---|---|
| Scripted QA | **PASS**, 9 screenshot checks + 10 actions, 0 `Error.txt` entries | **PASS** (windowed steps; exclusive fullscreen not switched on this desktop) |
| Mid-run screenshot, then keep rendering | OK | OK (the review's critical bug is fixed) |
| Churn + 6,000 bench frames | private 357 → 375 (transient) → **357 MB** | 330 → 333 → **329 MB** |

Not automatable here:
- **Lock screen / TDR (real device loss):** covered by code review and the reset path that S15 exercises.
- **Live game scene with a server:** its drawing paths are covered by the goldens: floor, objects, lights, grayscale, effects and UI.
- **Subjective feel**, such as frame pacing on other GPUs.

That is what the default cycle in real play is for.

## Rollout (roadmap C4)

1. ~~Opt-in~~ → **done: D3D11 is the default** after the automated QA. `[Graphics] Renderer=D3D9` opts out, and a device failure falls back to D3D9 with an `Error.txt` entry.
2. **QA:** the automated part is done (above). Still worth watching in play, though it no longer blocks:
   - login, select and game scenes;
   - night lighting, effects, death grayscale;
   - Alt+Enter;
   - resolution changes (1024 / 1280×800 / 1366 / 1920);
   - minimise and restore;
   - lock screen;
   - screenshots;
   - a long session (memory: the D3D11 CPU texture copies mirror D3D9's managed pool).
3. **Now:** one ~2-week default cycle with D3D9 selectable; then C5 removes D3D9/SlimDX.
4. **Ship:** the client patch must include `Vortice.*.dll` and `SharpGen.Runtime*.dll`; they are in the build output.
