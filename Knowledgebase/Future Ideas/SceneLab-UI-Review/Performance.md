# Scene Lab UI: performance review (2026-10-15)

Read-only review of the uncommitted HUD mock (`WebClient/play/hud*.ts`, `lab.ts`, `lab.html`). It looks for **free
gains**: changes that cost little work and make no visible difference, or almost none. No source was changed.
Line numbers are from the working tree on 2026-10-15. The Tier 1 findings were checked by hand against the source.

**Headline:** the HUD is cheap to lay out but expensive to *paint*. Almost all of the always-on cost comes from three
patterns, repeated across many files:

1. **CSS animations of paint-only properties** (`box-shadow`, `background-position`, `filter`, `border-color`). They run
   forever, so the browser repaints every frame on the main thread.
2. **`backdrop-filter` over the live WebGL canvas.** The canvas changes every frame, so the blur is recomputed every frame.
3. **DOM writes and rebuilds on every frame or every event**, even when nothing changed. Assigning `textContent` the same
   string still replaces the text node and invalidates layout. `replaceChildren` rebuilds are also used for things that
   only move.

Fixing these patterns once, with the shared helpers in the next section, removes most of the cost.
Only one item (T3-7, HP/MP bars) has any visible difference, and it is optional.

---

## Shared helpers (do these first; most fixes below use them)

**Done 2026-10-15:** the helpers are in `hudWindow.ts` as `setText`, `restartAnim` (two frame callbacks, no reflow;
`hudMock.ts restart` now uses it), `rafCoalesce`, `windowInterval` and the `.halo` CSS (`--halo`, `--halo-t`).
`rafCoalesce` and `windowInterval` have no callers yet; they are for Tier 2 and Tier 4.

| Helper | What | Used by |
|---|---|---|
| `setText(el, s)` | `if (el.textContent !== s) el.textContent = s;`. Or keep the last string in a `WeakMap`/field so the getter is never called. | T1-2, T1-3, T2-*, T3-* |
| **Glow via opacity** | A pseudo-element or child span with a *fixed* `box-shadow`/`filter`/gradient, and only its `opacity`/`transform` animated. Both run on the compositor, so there is no repaint. | T1-1, T1-4, T3-8, T3-12 |
| `rafCoalesce(fn)` | Store the latest mouse position and apply it once per frame. | T2-1..T2-4 |
| Measure once, then position with `transform` | Read rects or sizes on mousedown, show or resize; per move only write `transform: translate(x,y)`. | T2-1..T2-4 |
| Restart animations with `el.getAnimations().forEach(a => { a.cancel(); a.play(); })` (or `el.animate`) | Replaces `void el.offsetWidth` reflow hacks: `hudMock.ts:323`, `hudTooltip.ts:220`, `hudMentor.ts:195`, `hudPopup.ts:170/305`, `hudStorage.ts:166` | T3-11 |
| Interval lifecycle | Start intervals in `shown()` and clear them in `closed()`, as `hudTerritory.ts:141-145` and `hudGameMenu.ts:122-126` already do. | T4-2 |

---

## Tier 1: always-on cost (every frame while the HUD is up)

These cost frame time even when the player is doing nothing. Fix them first.

**Done 2026-10-15.** Typecheck clean; checked in the lab (HUD on, poison, timer, compass; no console errors). Changes in
behaviour and implementation notes:
- T1-1: the armed slot keeps its base glow and the halo adds the 1.5x accent glow on top, so the peak is slightly brighter
  than before. The slot gives up `overflow: hidden` only while armed; `.ico` / `.cd` clip themselves with `clip-path`.
  Before, the first `flash` class on a slot replaced its armed animation for good, so the pulse stopped after the first cast. It now keeps pulsing.
- T1-2: the drop-shadow filters became a transparent copy of the name with `text-shadow` underneath (`.sh`). A third, soft
  shadow stands in for the second filter shadowing the first. The gold sweep still runs every frame, but it now repaints
  text only, with no filter re-raster.
- T1-3: bag count and weight are recomputed on `onBag` plus a 4 Hz catch-up, because not every bag mutation calls `bagChanged()`.
- T1-5: the compass arm angle is rounded to 0.1 degrees and the timer ring offset to 0.1 px, so long timers skip most writes.
- T1-6: off layers go `visibility: hidden` after their 0.5 s fade; daze's sway is paused, not removed, so it does not jump while fading.

| # | Where | What | Fix | Visual |
|---|---|---|---|---|
| T1-1 | `hudMock.ts:233-234` | `.hm-slot.armed` animates **`box-shadow`** forever (`hudm-armed 1.6s infinite`). Every class has an armed slot by default (`ARMED`, L94), so it repaints every frame, including the masked `::before`. | Glow via opacity: give a `.halo` child the 1.5× shadow and animate its `opacity` 0→1. | none |
| T1-2 | `hudQuests.ts:193-197` | The quest tracker's gold shine animates **`background-position`** on `background-clip:text` under a **double `drop-shadow` filter**, forever, for every tracked quest. | Animate a gradient pseudo-element with `transform` (with `mix-blend-mode` over the text), or a static gradient with a one-shot sweep every ~8 s. | minimal |
| T1-3 | `hudMock.ts:1204-1209` (`render()`) | `bag.filter(Boolean)` (allocates) plus `bagWeight()` (walks the bag), and the bag, gold and weight `textContent` are rewritten **every frame**. | Recompute in the `bagChanged` path; `setText`. | none |
| T1-4 | `hudMock.ts:1147, 1156, 1159` (`tick()`) | Cooldown digits, the `"∞"` buff text and every buff's `fmtTime` are written every frame. They change 1-10×/s, never, and 1×/s respectively. The cooldown pie `setAttribute("d")` per frame is fine (smooth sweep). | `setText` | none |
| T1-5 | `hudExtras.ts:428, 438-443, 399-410` (`tick()`, called per frame) | Compass: `replaceChildren(h(b), h(span))` creates 2 elements **every frame**. Timers: 3 `querySelector` calls plus 2 text writes per timer per frame (one is the constant label). Debuffs: `[...set].filter().sort()` and `POISONS.find` allocate every frame. | Cache the child refs at build. Write the compass label only when `d`/`dir` changes; the arm `rotate` can stay per frame. Timer text only on a whole-second change; drop the label write. Recompute debuffs when the set changes. | none |
| T1-6 | `hudExtras.ts:93-95` | `.frost` / `.para` / `.daze` are full-screen **`backdrop-filter`** layers that always exist at `opacity:0`. `.daze` also runs an infinite animation when it is not `.on`. Whether Chrome skips opacity-0 backdrop surfaces has not been checked. | Scope `backdrop-filter` and `animation` to `.on` only. | none |
| T1-7 | `lab.ts:920-921` | `#status` string is rebuilt and assigned every frame. | `setText` (or build it only when cam, fps or cell changes). | none |
| T1-8 | `hudMock.ts:1185-1192` | While EXP eases (~1 s bursts), `VERTS.forEach` writes `style.fill` on every tick mark every frame. Path and head carry an SVG `filter: url(#hudm-glow)`, which is re-rasterised on each change. | Write the tick-mark fills only when the number of crossed marks changes. | none |

## Tier 2: interaction paths (per mousemove: hover, drag, pan)

Mice at 500-1000 Hz fire several moves per frame. Each of these forces a **synchronous layout per event**, because a
read follows the previous event's write.

**Done 2026-10-15.** Typecheck clean. Checked in the lab with synthetic mouse events (no console errors):
- big map pan keeps the same 118 marker nodes, and zoom re-places them; the markers line up with the picture
- window drag moves by transform and commits left / top on mouseup
- the tooltip follows the mouse, and the item-drag ghost follows each frame
- the options nav nodes survive a slider drag

Notes:
- T2-1: markers are rebuilt only on a map, layer, focus or waypoint change. The marker layer `.mkl` is translated on a
  pan, and `layout()` re-places markers on a zoom. "Me" is one persistent node, moved when the camera cell changes,
  so its ping no longer restarts.
- T2-2 / T2-6: the viewport (big map) and disc (minimap) rects are measured on mouseenter / mousedown. Big map moves
  are coalesced to one per frame and ignored unless over the viewport or dragging.
- T2-3: the tooltip measures its box once per draw; mouse follow is coalesced. It still moves with left / top: it is
  out of flow, so with the reads gone that is a small layout.
- T2-4: one `elementFromPoint` before any write. Ghost and label have `will-change: transform` and move by transform;
  the label is rewritten only on change.
- T2-5: `.hwin.wdrag` (`will-change: transform`) while dragging; clamping uses the sizes measured at mousedown.
- T2-7: the nav is built once, and later renders only toggle `on` / `dirty`. Slider input syncs footer + nav once a frame.
- **Known defect (pre-existing, not fixed):** the drag ghost is `position: fixed` inside the scaled `.hudm`, so with a
  HUD scale other than 1 it sits off the pointer (by scale x position). The old left / top code gave the same position.

| # | Where | What | Fix | Visual |
|---|---|---|---|---|
| T2-1 | `hudBigMap.ts:273-278`, `drawOverlay()` `:299-357`, interval `:154` | **Highest in this tier.** `applyView()` runs on every pan mousemove and wheel tick and calls `drawOverlay()`. That rebuilds every marker (zones, exits, NPCs, waypoints) with listeners, `innerHTML` SVG parses and `libPicture` calls. The 400 ms follow-camera interval also rebuilds everything just to move the "me" arrow, which also **restarts its ping animation every 400 ms**. | Build markers only when the map, layers, focus or waypoints change. Put them in a container moved with `transform` on pan; on zoom, update positions of the cached nodes. Keep one persistent `.me` node. Delegate clicks to `ov`. | none (fixes the ping reset) |
| T2-2 | `hudBigMap.ts:215-225, 286-289` | The window mousemove (even with no drag) calls `cellAt()`, which reads 2 rects, `offsetWidth` and `clientWidth/Height`, plus a second `screenScale()` while dragging. | Cache on mousedown, open and resize; `rafCoalesce`. | none |
| T2-3 | `hudTooltip.ts:183` → `place()` `:224-231` | Each move reads `getBoundingClientRect` plus 4× `offsetWidth/Height`, then writes `left/top`. | Measure the tooltip size and layer rect/scale in `render()` and on resize; per move write `transform: translate3d()`. | none |
| T2-4 | `hudDrag.ts:177-187, 62-63` | Item drag ghost: writes `left/top`, then `elementFromPoint` (×2 via `overWorld` L170) forces layout. The ghost has a **double `drop-shadow` filter**, so it is repainted every move. The label `className`/`textContent` are rewritten every move. | Call `elementFromPoint` *before* any write (ghost is `pointer-events:none`). Move with `transform: translate(x,y) scale(1.15) rotate(-4deg)` and add `will-change: transform` on the ghost only. Write the label only when it changes. | none |
| T2-5 | `hudWindow.ts:250-253` → `place()` `:193-201` | Window drag reads `layer.clientWidth/Height` and `el.offsetWidth` after each write. The panel's `0 10px 30px` shadow is repainted on every move. | Clamp with the `w/hh/W/H` already captured at mousedown (L238). Move via `transform`, or add `will-change: transform` to the window only during the drag. | none |
| T2-6 | `hudMinimap.ts:107` | `getBoundingClientRect` on every hover move. | Cache on `mouseenter`. | none |
| T2-7 | `hudOptions.ts:354` | Dragging a slider calls `renderFoot()` + `renderNav()` per `input` event, rebuilding every category item with SVG and listener (~60×/s). | Toggle `.dirty` and update the count text in place. | none |

## Tier 3: rebuild-on-update (per event or per timer tick while a window is open)

| # | Where | What | Fix | Visual |
|---|---|---|---|---|
| T3-1 | `hudSelect.ts:34, 39, 40` | Select screen: full-viewport `backdrop-filter: blur(4px)…`, **plus a nested `blur(10px)` on every `.glass` panel**, plus `.rays` `filter: blur(6px)` animated forever. The world canvas is redrawn every frame, so everything is re-blurred every frame. | Pause the lab scene render while `.hudm.scene-on` is set; a static backdrop lets the browser cache the blur. And/or drop the inner `.glass` blur for a slightly more opaque fill (the outer layer already blurs and darkens to .55). Add `will-change: transform` on `.rays`. | none / minimal |
| T3-2 | `hudGameMenu.ts:38`, `hudPopup.ts:72` | Full-screen veil `backdrop-filter: blur()` over the live canvas while the game menu or any popup is open. | Same option as T3-1 (pause or throttle the scene under a modal veil), or drop the blur and darken the veil a little. | minimal |
| T3-3 | `hudChat.ts:271-286, 303-336, 399-401` | **Each new message** rebuilds the tab bar and up to 250 log lines: a `new RegExp(ME)` per line, `rich()` regex, 2-3 listeners per line. Then 2 forced layouts (tab `offsetLeft` read, `scrollHeight`). The sim feed fires every 2.5-7 s; search re-renders per keystroke. | Append only the new line (or bump the ×n badge). Compile the mention regex once and cache the result per line. Delegate clicks on `this.log`. Update unread counts in place. Debounce search ~100 ms. | none |
| T3-4 | `hudInventory.ts:66, 76-84` | Every `bagChanged()` (about once a second in demo: potions, loot) rebuilds all 40 cells with listeners and `libPicture` calls, **even while the bag is closed**. | Mark dirty when closed and render in `shown()`. When open, rebuild only cells whose name or count changed. | none |
| T3-5 | `hudMinimap.ts:114, 150-167, 182, 211, 222` | Every 150 ms it calls `getComputedStyle` (style flush, ×2) to read `--ac`, builds a key over all objects, rebuilds the banner SVGs on any camera change, and redraws with `shadowBlur` (expensive in canvas 2D). **It keeps drawing when folded** (`.shut` is not checked). | Cache the accent on theme change. Return early on `.shut` / `document.hidden`. Update the banner text nodes only when the rounded coordinates change. Pre-render the glowing arrow and diamond to an offscreen canvas and blit them. | none |
| T3-6 | `hudHero.ts:213, 271-274, 295-309`; `hudGroup.ts:144, 155-166, 279-293` | Each 1 s / 1.2 s tick rebuilds the party frames and open window bodies, including avatars, crowns and 10 `itemCell`s. Group frames are rebuilt **even when hidden**. | Build once; update bar widths, names and feed lines in place. Return early when hidden. | none |
| T3-7 | `hudMock.ts:194-195` | HP/MP fill and lag animate **`width`** (layout transitions on 4 elements), constantly in demo mode. | **Optional, the only item with a visible difference:** keep the bars at full width and animate `transform: translateX(-(100-v)%)` inside the existing `overflow:hidden`. The gradient then slides instead of being re-fitted. Skip it if the look matters. | minimal |
| T3-8 | `hudMock.ts:185, 200, 202` | `hudm-pulse` animates `filter: brightness()` on low HP / overweight. | Glow via opacity (white or `--ac` overlay, `mix-blend-mode: screen`). | minimal |
| T3-9 | `hudSocket.ts:86`, `hudSelect.ts:69-70`, `hudRent.ts:59`, `hudOptions.ts:180`, `hudKeybinds.ts:104-105` (`border-color` pulse on ~100 keys), `hudLogin.ts:40-42` (logo `background-position` under 2 drop-shadows), `hudTooltip.ts:105-107` (`.shine` on Legendary+ tooltips) | Infinite paint-only animations while that window is open. | Glow via opacity, or a `transform`-moved pseudo-element for shines. | none |
| T3-10 | `hudCreature.ts:157-161, 263-274` | Each mini's `backgroundImage` (url + radial-gradient + `color-mix`) is rewritten every 100 ms even when the frame is the same. The stone ring and button are rebuilt every second. | Cache the last frame per mini (the main sprite already does, L147); update only the countdown text. | none |
| T3-11 | `hudQuests.ts:453-462` | `fit()` interleaves `scrollWidth/clientWidth` reads with class/property writes per `.clip`. That is N forced layouts, twice per tracker render and on every ResizeObserver callback. | Two passes: read all, then write all. | none |
| T3-12 | `hudSelect.ts:154-156`, `hudNpc.ts:366` | The character and NPC preview recompute the frame bounds (`Math.min(...map)`) and rewrite constant size, margin and transform on every frame step. | Cache the bounds and scale per index; write only `src` and position. | none |
| T3-13 | Search boxes: `hudTrust.ts:245`, `hudGameShop.ts:228`, `hudKeybinds.ts:339`, `hudHelp.ts:252`, `hudGuild.ts:349`, `hudFriends.ts:185`, `hudCraft.ts:158` / `181-182`, `hudOptions.ts:238` | Each keystroke runs a full window render (filter, sort, `findIn` regex per listing, `maxCraft` per recipe). | Debounce ~100 ms; re-render only the result list. Resolve `Item` once per listing (Trust). | none |
| T3-14 | `hudGameMenu.ts:218-229`, `hudChat.ts:175`, `hudGuild.ts:505-511`, `hudRelationship.ts:143` | Small ticks: the clock calls `toLocaleTimeString({…})` (a new formatter each call) 4×/s; `ageLines` walks all lines with chat hidden; guild buff cells are rebuilt each second; Relationship runs `querySelector` while closed. | One cached `Intl.DateTimeFormat`; `setText`; return early when hidden. | none |

## Tier 4: startup and one-off jank

| # | Where | What | Fix | Visual |
|---|---|---|---|---|
| T4-1 | `lab.ts:14, 198`; `hudMock.ts:12-59` | **The whole HUD is built at lab start although it is off by default.** That means ~41 window constructors, ~38 `injectCss` `<style>` inserts (each one a full style recalc), DOM builds, `items.json`/`recipes.json` fetches (`hudTrust.ts:131`, `hudRefine.ts:111`, `hudCraft.ts:106`) and ~50 extra unbundled module requests. | Build `HudMock` the first time "Show HUD mock" is ticked, via `await import("./hudMock.ts")`. Inside `HudMock`, construct each window on first open (a lazy getter per window). Merge the CSS into one `<style>` insert. | none |
| T4-2 | ~20 permanent `setInterval`s: `hudGroup.ts:144`, `hudChat.ts:176` (sim keeps adding lines), `hudHero.ts:213`, `hudMentor.ts:152`, `hudRelationship.ts:143`, `hudRefine.ts:112`, `hudMount.ts:117` (100 ms), `hudNpc.ts:162` (150 ms), `hudCreature`, `hudFishing` (80 ms), … | They keep running while the HUD or their window is hidden. Most return early; some do not (Group, Chat, Relationship). | Interval lifecycle helper (start in `shown()`, clear in `closed()`). Simulations owned by `HudMock` should pause with `root.hidden`. | none |
| T4-3 | `hudMount.ts:144-152` | The first view of each mount type pushes every opaque pixel's x/y from 16 frames into JS arrays and **sorts** them (hundreds of thousands of numbers on the main thread). `toBlob`/`decode` at L161-168 run one at a time. | Per-axis `Uint32Array` histograms with percentiles from the cumulative count: O(n), no sort, no big arrays. Run the decodes with `Promise.all`. | none |
| T4-4 | `hudCharacter.ts:271-289` | `topPointX()` decodes the helmet again and runs `getImageData` on every doll draw (equip, inspect, hero switch). | Memoise by picture URL. | none |
| T4-5 | `hudNpc.ts:312` | Typewriter on a 16 ms `setInterval`. | Use rAF, so it follows frames and stops in background tabs. | none |

## Lab scene (not the HUD, but in the same loop)

- `lab.ts:899`: `objects: [cam, ...actors, ...testers.map(...)]` allocates a new array every frame. Reuse one array and
  rebuild it only when actors or testers change.
- `lab.ts:906-909`: while split-compare is on, 2 `getBoundingClientRect` calls run per frame. Cache them in `fit()` /
  `setResolution` / on scroll.

## Checked and fine

- No per-open `document`/`window` listener leaks: global listeners are added once in constructors. Timers in Popup,
  Trade, Select, Territory, Login and GameMenu are cleared correctly.
- HP/MP/EXP/level writes are already guarded by `this.shown`; `setGold` is guarded internally. Repeated
  `classList.toggle(c, force)` calls with an unchanged state do nothing.
- `libPicture` is cached app-wide (`src/ui/mir/controls.ts:54`), so re-renders cost a promise and a microtask, not a
  decode. `libPictureNow()` could set backgrounds synchronously when rebuilding.
- `lab-data` JSON (maps 242 KB, items 57 KB, …) is fetched once, the promise is cached, and maps load lazily.
- Confetti, shards, embers, `hudm-blink` and the NpcBox/Socket/Relationship/Mount loops animate transform/opacity
  (composited) and stop when hidden.
- The canvas ignores `devicePixelRatio` and uses `pixelated`. That is intentional for pixel art, so there is no gain there.

## Suggested order

1. **Helpers + Tier 1** (T1-1..T1-8): about an hour. This removes almost all of the idle per-frame repaint and DOM work.
2. **T4-1 / T4-2**: lazy HUD and interval lifecycle. The lab starts faster and costs nothing while the HUD is off.
3. **T2-1 / T3-1 / T3-2**: BigMap overlay, and pausing the scene under full-screen blurs (the largest GPU cost while
   those screens are open).
4. Everything else as each window is touched. **When windows are ported into the game client, apply these patterns
   from the start** (`webclient.md` §3.5 decision still open, see `README.md`).

## How to measure

- Chrome DevTools > Performance, 10 s idle with the HUD on and demo off. Compare **Paint**/**Composite** time per frame
  and **Recalculate Style** counts before and after Tier 1. Rendering > *Paint flashing* shows T1-1/T1-2 at once: they
  flash green continuously.
- `crystalLab.hud` is exposed on `window` (`lab.ts:943`), so a quick `performance.now()` around `hud.tick` in the
  console gives the per-frame JS cost.
- For T2, record a 3 s window and item drag with a high-rate mouse. Look for purple **Layout** blocks marked *Forced
  reflow*.
