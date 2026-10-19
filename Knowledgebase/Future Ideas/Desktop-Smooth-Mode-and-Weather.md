# Desktop client: Smooth mode + enhanced weather (lean plan)

> **Status:** Proposed 2026-10-19. `Client/` only, plus `ClientTextKeys` in `Shared/Language.cs`. No packet, enum or DB change.
> **Port from:** `WebClient/src/game/objects.ts` `updateMovement` (smooth), `weatherFx.ts` + `atmosphere.ts` + `render/postFx.ts` (weather). Copy the behaviour and tuning constants; skip the web-only performance workarounds.

## Ground rules (every phase)

- **D3D11 only:** weather and effects run only when `Atmosphere` is on and Enhanced lighting is on (D3D11). D3D9 keeps today's sprites.
- **Smooth mode:** on by default (user, 2026-10-19). It works on both backends; it moves offsets, not shaders.
- **Classic render unchanged:** `rendertest.ps1 -Mode Compare` (D3D9) and `-Mode Parity` (D3D11) must stay identical.
  - The goldens are static draws with no `Process()` movement, so Smooth mode being on does not touch them.
  - If a scenario ever moves an object, it forces `SmoothMove=false`.
- **Frame loop:** no allocations per frame (pool footprints, splashes, lamp lists). No frame cap; measure with `-Bench`.
- **New render targets:** release them in `DXManager.CleanUp` and recreate them after `Reset`.
- **Builds:** `tools\verify.ps1` only, never into `Build\`. After adding the `Language.cs` keys, run `Shared.Tests`.
- **Decided (user, 2026-10-19):**
  - Lightning on every rainy map; thunder = sound 8301 at half volume.
  - Smooth mode and Weather each get a toggle in both UIs, plus `@smooth on|off` and `@weather on|off`.
  - Glow, heat haze and spell flashes are Phase 4.
  - No footfall splash sound (no desktop file).
- **UI toggles (Phase 1, both features):**
  - **Modern:** two `Bool(...)` rows in `Modern/Windows/OptionsWindow.cs` under Graphics. The Weather row's description says it needs Enhanced lighting (D3D11).
  - **Classic:** `OptionDialog` (`MainDialogs.cs`) gets two rows below the UI style row (y 320): a `MirLabel` plus the existing On/Off `MirButton` pair (Title lib, as for `NewMove`). Check they fit on the panel art with `tools\uitest.ps1`, and update the button highlight in the dialog's refresh like the other pairs.
  - **Strings:** new `ClientTextKeys` (`SmoothMove`, `SmoothMoveDesc`, `Weather`, `WeatherDesc`; Phase 4 adds `GlowEffects`, `GlowEffectsDesc`) in `Shared/Language.cs`, with text in `Client/Localization/{English,Chinese}.json`.

## Loop per phase

1. Build it.
2. Run `verify.ps1`.
3. Run goldens (Compare and Parity).
4. The user looks in the live client on a rainy map.
5. Tune constants.
6. Commit.

No phase needs its own new golden. Each adds at most one D3D11 self-check scenario in `RenderTestRunner` (like `X01`).

---

## Phase 1: Smooth mode + rain + lightning (first visible result)

**Smooth mode**
- Add one helper, `MapObject.ComputeMoveOffset`, used by both `PlayerObject.Process` and `MonsterObject.Process`. It takes over the existing direction switch.
- With the setting on:
  - `steps = FrameIndex + clamp01(1 - (GameScene.MoveTime - CMain.Time) / 100)`;
  - round to the nearest even pixel.
- With it off, the code path stays exactly as today.
- While anything near the view is moving, redraw the map every frame (and the floor too when the user moves).
- Add `SmoothMove` to `DrawSettingsSignature`.
- Setting: `[Game] SmoothMove=true`, the UI toggles (see Ground rules), and `@smooth on|off`.

**Renderer additions** (`IRenderBackend` + `D3D11Backend`; D3D9 reports `SupportsAtmosphere = false`):
- `DrawFullscreen(kind, inputs)`;
- `SetAtmosphereConstants(Span<Vector4>)`, a dynamic cbuffer;
- `CreateRenderTarget(w, h, Rgba8|Rgba16F)`;
- a linear-clamp sampler;
- separate alpha blend and a write mask (needed in Phase 2).
- HLSL lives in a new `AtmosphereShaders.cs`, compiled at startup. If it fails to compile, turn Atmosphere off and log it.

**Weather**
- `WeatherFx.cs`: port of `weatherFx.ts` (wind and drift, rain intensity, wetness, strike and thunder timers, snow build-up, footprints).
- `MapControl.Atmosphere.cs`: hooks into `CreateTexture` in this order:
  1. floor;
  2. ground pass (empty for now);
  3. objects;
  4. sprites (rain sprites skipped);
  5. sky pass;
  6. Enhanced lights;
  7. composite;
  8. names.
- While active on a weather map, redraw every frame instead of the 10 fps "weather" tick.
- Rain = `RAINFALL_FS` (3 parallax layers, near layer, gust sheets, wash, glints from last frame's light map) + grey tint.
- Lightning strobe through the light map plus the composite; thunder 8301.
- Setting: `[Graphics] Atmosphere=true`, the UI toggles, and `@weather on|off`.
**Check:**
- `X02_rain`: frozen time and seed; output identical across runs; strobe frame brighter.
- Smooth offset check: `steps = count` gives exactly the classic final offset, and every value is even.

---

## Phase 2: Wet ground + reflections

- **Cell mask:** R = floor tile, G = water, B = eaves-drip base. Rebuilt when the user changes cell and every 1.5 s.
- **`GROUND_FS`:**
  - puddles spreading with wetness, ripples, splashes, eaves drips;
  - it writes scene alpha as reflectivity.
- **CPU splashes** drawn as sprites: on heads and shoulders, and on footfalls in puddles (needs a `PuddleAt` CPU copy of the shader `puddle()`; change both together).
- **Reflections:**
  - an alpha snapshot before the lights;
  - `REFLECT_FS` for objects (half-res RGBA8) and lamps (half-res RGBA16F, max 48 lamps);
  - `DrawEnhancedLights` records each lamp's source point and ground contact;
  - added in the composite only on wet floor.
- **Check:** `X03_wet`: no wetness on void cells, and reflections only where the snapshot marks wet floor. Run `-Bench` at 1080p; if reflections cost too much, add a `[Graphics] Reflections` sub-toggle.

---

## Phase 3: Snow, fog, rain sound, docs

- **Snow:**
  - cover and footprints in `GROUND_FS`;
  - `SNOWFALL_FS`;
  - frost edge and a cool tint;
  - the snow sprites stay.
- **Fog:** `FOG_FS` ground and high layers before the lights, replacing the fog sprites.
- **Procedural rain:** an NAudio `ISampleProvider` (noise bands plus drop ticks) at rain intensity. It stops on map change, logout and when the option goes off.
- **Docs:**
  - `client.md`: settings, frame order, renderer additions;
  - `D3D11-Parity.md`: Atmosphere listed as a scoped exception, like Enhanced lighting;
  - a one-line note in `webclient.md`.

---

## Phase 4: Glow, heat haze, flashes (the rest of web `atmosphere.ts`)

All of this reuses the Phase 1 renderer additions and the composite. Setting: `[Graphics] GlowEffects=true` (not `Effects`: `Settings.Effect` already exists) (web "Atmosphere effects"), with a toggle in both UIs like Phase 1. Gated on Enhanced lighting (D3D11) and the user being alive.

- **Glow (bloom):**
  - Record every additive `DrawBlend` of the frame: spells, blended monster frames, map fire tiles. Not snow, rain or plain leaves.
  - Record normal draws over an earlier glow as black occluders.
  - Replay them into a half-res RGBA16F target, blur twice (`BLUR_FS`), and add the result in the composite (`GLOW_STRENGTH`).
- **Spell and crit flashes:** short lights keyed by the effect's `library:firstFrame` (web `EFFECTS` table), added to the Enhanced light map. Crit flashes need `DisplayDamage`. Lightning spells also strobe the screen.
- **Lit particles:** ember and fiery-leaf weather particles add small flickering lights at night.
- **Heat haze:** fire effects, FireWall and MeteorStrike / lava draw soft blobs into a mask that wobbles the scene UVs in the composite (`HAZE_PIXELS`).
- **Dungeon depth:** on maps whose light is permanent Night, darken the scene with distance from the player (`DUNGEON_DEPTH`).
- **Check:** `X04_effects` self-check (a glow target that isn't black where an additive draw happened, nothing elsewhere); `-Bench` with a crowd of spell effects.

---

**Known defect, left alone:** `UpdateWeather` never disposes particle engine 0 (`i > 0`). Fix it as a separate task.
