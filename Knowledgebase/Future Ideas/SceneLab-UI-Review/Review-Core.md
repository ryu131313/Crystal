# Scene Lab UI mock review: core files

Reviewed 2026-10-15 (uncommitted work on `FuckingAround`). Scope: `WebClient/play/` hudMock, hudWindow, hudDrag, hudTooltip, hudPopup, hudItems, hudInventory, hudCharacter, hudSkills, hudPeople, hudLoading, hudLogin, hudSelect, hudGameMenu, hudOptions, hudKeybinds, hudChat, hudMinimap, hudBigMap, hudHelp, and the `lab.ts` / `lab.html` diff. The other hud files are covered by a separate review. No source files were edited.

## Summary

- The mock is solid for a dev tool. It typechecks clean, timers are mostly guarded, and the host interfaces (`SkillsHost`, `GameMenuHost`, `WorldHost`, `DragHost`) are a good seam for porting.
- **Worst bug: data loss.** Stackable items placed in an equipment slot lose their count. For example, pressing `0` on the 10-slot belt destroys 249 of the 250 Amulets.
- **Z-order drifts.** Window z-indexes grow without limit. After about 40 focus changes, the item tooltip draws behind windows, and windows draw over the login screen.
- **Keyboard routing is spread out.** About ten independent `keydown` listeners decide who gets each key, and the result depends on the order the classes are constructed in. Several keep taking keys when the HUD is hidden (Show HUD mock off) or when their window is not in front, which blocks the lab's own keys (pan, G, H, arrows).
- **Re-entry guards are missing.** Login, Start game and the Loading screen can each run more than once from repeated Enter presses.
- **Self-XSS exists today and is a porting risk.** A whisper target is a name the user types, and it reaches `innerHTML` in the chat name menu. The popup `text` API takes HTML by contract.
- **Per-frame DOM work runs while hidden.** The bag/stat text is rewritten every frame, the inventory grid is rebuilt on every `bagChanged()` even when its window is shut, and the chat log is fully rebuilt for every message. The chat simulation keeps running when the HUD is hidden.

## Bugs (ranked by severity)

Status 2026-10-15: all 18 fixed (approved by the user). `npx tsc --noEmit -p tsconfig.json` passes; not exercised in a browser. Each entry ends with its **Status** line.

### High

1. **Stack count lost in equipment slots (item destruction).** `hudCharacter.ts:122` `get` always returns `count: 1`, and `wear()` (`:177-180`) stores only the name and image. `hudDrag.ts:244` (swap) and `:247` (move) write the full stack into the slot. Ways to hit it:
   - Press `0` with the 10-slot belt. Belt cell 9 is Amulet x250. Amulet is not in `USE_TYPES` (`hudMock.ts:92`), so `wearFromBelt` swaps it into the Amulet slot (`hudMock.ts:1011-1013`). The Amulet becomes x1, and swapping back returns 1.
   - Drag Amulet x500 from the bag onto the Amulet or right-bracelet slot.
   - Right-click a bag Amulet when the belt has no room (`hudInventory.ts:60`).

   Fix: store a count in `Equip` (Crystal keeps amulet stacks in the slot), or have `equip.accepts`/`place` move only `min(n, stackMax)` and return the rest. Also add `"Amulet"` to `USE_TYPES`, or decide the rule explicitly.
   **Status: Fixed.** `hudCharacter.ts` keeps a per-slot count (`WORN_COUNTS`); `get` returns it, `wear` stores it, and the cell shows it. Amulets stay gear (a belt key equips them), and the count now survives the swap.
2. **Z-index grows without limit.** `front()` does `++zTop` on every focus change (`hudWindow.ts:84,133`). The tooltip has a fixed `z-index: 50` (`hudTooltip.ts:100`) and the login screen has 40 (`hudLogin.ts:27`); login does not use `scene-on`. After about 40 alternating focus or open actions, hovering an item in the front window shows no tooltip because it is behind the window, and open windows paint over Simulate > Login screen. Fix: renumber the open windows into a fixed band (for example 100-999) whenever `zTop` passes the band, and put the tooltip, scenes and popups in fixed layers above it.
   **Status: Fixed.** `hudWindow.ts` keeps windows in the band 10-39; `front()` calls `renumber()` to repack the open windows when the band fills. The tooltip (50) and scenes (40/41) stay above. If more than 29 windows are open at once, the extra ones share z 38, which is still below those layers.

### Medium

3. **Keys taken while the HUD is hidden or a window is not in front.** Each case below calls `stopImmediatePropagation` (or `preventDefault`) and so blocks the lab's G, H and pan keys:
   - `hudMock.ts:489`: the select screen check runs before the `root.hidden` check. Open Character select, then untick Show HUD mock: every key is swallowed.
   - `hudLoading.ts:82`: the capture listener eats every key while the screen is not `hidden`, even with the root hidden. When the screen is ready it also `preventDefault`s typing in the lab panel's inputs.
   - `hudGameMenu.ts:112,233`: Esc, arrows and Enter are captured while the menu is open, also with the root hidden.
   - `hudHelp.ts:237-240`: ArrowLeft/Right are captured whenever Help is open on a topic, even behind other windows. This steals the lab's arrow-key pan.

   Fix: one input router with a focus stack (modal popup > scene > game menu > front window > HUD hotkeys > lab) that returns early when `root.hidden`.
   **Status: Fixed with per-listener guards, not a router** (smaller change; the router stays a porting improvement):
   - `hudMock.ts:489` checks `root.hidden` first.
   - `hudLoading.ts` and `hudGameMenu.ts` use `el.closest("[hidden]")`; Loading also skips lab panel inputs.
   - `hudHelp.ts` takes arrows only when `isFront` (new `HudWindow.isFront`).
   - The HudWindow Esc handler ignores windows in a hidden HUD.
   - Options and Key bindings take rebind keys only when `isFront`.
4. **Windows not kept on screen after a resolution or scale change.** The re-clamp runs only on `window` `resize` (`hudWindow.ts:163-164`). `setResolution`/`fit` (`lab.ts:425-447`) and `HudMock.layout()` (`hudMock.ts:716-720`) change the `.wins` size without it. The root has `overflow: hidden` (`hudMock.ts:158`). Example: drag a window to x≈1300 at 1920x1080, then press "1024 x 768": the window is clipped out of view until it is reopened or Reset is used. The comment at `hudWindow.ts:163` says resolution previews are covered, but they are not. Fix: export `HudWindow.reclampAll()` and call it from `layout()`.
   **Status: Fixed.** Added `HudWindow.reclampAll()`, called from `HudMock.layout()` (resolution change, HUD scale, auto scale) and on window resize.
5. **Login can be submitted twice.** `hudLogin.ts:181`: Enter calls `.card .go`'s `click()`, which ignores `.go.busy { pointer-events: none }`. `login()` (`:209`) has no `busy` guard. Pressing Enter twice within 1.4 s counts two wrong-password tries, or for a correct password runs `enter()` twice, so `onDone` and `select.show()` run twice. Also, the `enter()` and stage timers (`:215, :238-241`) are never cancelled by Quit, `hide()` or `show()`: clicking Quit during the door animation still opens character select 3.5 s later. Fix: guard on `busy`, and keep the timer ids and clear them in `hide()`/`show()`.
   **Status: Fixed.** `login()` returns while `busy` or `entering`. All login and door timeouts go through `after()` and are cleared in `show()` and `hide()`.
6. **Start game can run more than once.** `hudSelect.ts:238-245` has no guard. Holding Enter auto-repeats (`hudMock.ts:489` forwards every keydown, including repeats) and queues many `onStart` calls. Each one calls `loading.show()` (`hudLoading.ts:86`). Earlier async renders still push timers that set `ready` early and append extra "PRESS ANY KEY" lines (`:137`). Fix: an `entering` flag in `start()`, `if (e.repeat) return` in `key()`, and a show token in `LoadingMock.render`.
   **Status: Fixed.** `hudSelect.ts` has an `entering` flag, reset in `show()`; `key()` ignores repeats and lab inputs. `hudLoading.ts` has a `token`, so an older `show()` returns after its await.
7. **Stale stack snapshot in drag and drop (item duplication).** `hudDrag.ts:140` captures `stack` at drag start, and `drop`/`place` (`:204, :235-249`) write `s.count - k` from it. With Demo events on, drag belt potion x10 out of belt cell 0. The demo drinks one (`hudMock.ts:1074`, which sets `belt[0]` to x9). Dropping into the bag gives x10 in the bag, so one potion is duplicated. The same happens with the Split popup (`askAmount`), which is not modal. Fix: re-read `from.get(i)` at drop and cancel if the name or count changed (or clamp to it).
   **Status: Fixed.** `place()` (`hudDrag.ts`) re-reads the source cell and moves at most what is there; if the item is gone it shows a toast instead. This covers drag, split, quick moves and `transfer`. The drop-on-ground confirmation also re-checks the cell.
8. **Self-XSS in the chat name menu (will become real XSS once names come from the server).** `hudChat.ts:537` builds `` `<span …>Block ${n}</span>` `` with `innerHTML` (`openPop` at `:511`). `n` can be a typed whisper target (`/name ` at `:413-414`, and `send` at `:464`) with no spaces, for example `/<img/src=x/onerror=alert(1)> `. Send a line, then click the name in the log (`:316`): the script runs. `itemMenu` (`:546`) and `suggest` (`:425-429`) use the same item-as-HTML pattern. Fix: build menu items from nodes (label + small), never HTML strings.
   **Status: Fixed.** `hudChat.ts` popup rows are now `PopLabel` (text or nodes, no `innerHTML`). The command, whisper, channel, name (Block) and item-link menus build nodes.
9. **The popup `text` parameter is HTML by contract.** `hudPopup.ts:210` sets `tx.innerHTML = text`. `hudMock.ts:663` passes territory text containing `e.owner` (a guild name from DB data) through it. This is safe for mock data only. Fix before porting: take `Node | string` parts, or a small `{b, gold}` markup builder. The `\n` to `<br>` replacement at `:663` is also unneeded because `.tx` already has `white-space: pre-line`.
   **Status: Fixed.** `hudPopup.ts` sets the text with `textContent` by default. `html: true` on `ConfirmOpts`/`InputOpts`/`AmountOpts` is the opt-in for trusted markup from code. The seven `popupDemo` boxes with markup opt in; the territory and game-menu texts stay plain (the `<br>` replace is gone).

### Low

10. **Rebind wait survives closing the window.** `hudOptions.ts:217-227` and `hudKeybinds.ts:185-193`: close the window with × while it is waiting for a key, reopen it, and the next key press becomes a bind. Fix: clear `waiting` when the window is hidden (override `toggle`, or add an `hidden()` hook in `HudWindow`).
   **Status: Fixed.** New `HudWindow` close hook: protected `closed()` plus a public `onClose` callback. It runs on every close path (cross, Esc, closeAll, `toggle(false)`) and only when the window was open. Options and Key bindings clear `waiting` in it.
11. **Inventory gold never changes.** `hudInventory.ts:11,44` uses a fixed `GOLD = 1_240_000`. The HUD's gold (`hudMock.ts:441`) changes with loot, teleport, sell and deposit, so the two disagree. Read it from a host instead.
   **Status: Fixed.** `InventoryMock.setGold()` is called from `HudMock.render()` and only writes the text when the amount changes.
12. **Blob URL leak in the doll.** `baseBody()` (`hudCharacter.ts:210-231`) keys pixels and calls `URL.createObjectURL` on every doll redraw (each armour, weapon or helmet change while the window is open; the hero window too) and never revokes it. Memoise the promise per gender.
   **Status: Fixed.** `baseBody()` is memoised per gender; a failed load is retried next time.
13. **Minimap keeps the old accent colour.** `hudMinimap.ts:154`: the redraw key does not include the theme, so after a theme preset the player arrow, exits and party marks keep the old accent until the camera or an actor moves.
   **Status: Fixed.** The redraw key includes `--ac`.
14. **HUD frozen without a map.** `lab.ts:896`: `hud.tick(t)` is inside `if (map)`, so the HUD (cooldowns, EXP easing, demo) stops if a map fails to load.
   **Status: Fixed.** Moved after the `if (map)` block.
15. **Import HUD JSON is not range-checked.** `lab.ts` "Import HUD JSON" only checks types. Importing `"hudBelt": 7` builds `perSide = 3.5`, which gives fractional belt indices (`hudMock.ts:731-754`). Clamp to `{6, 10}` and to each knob's min and max.
   **Status: Fixed.** The import now clamps the belt to 6 or 10, the class to Warrior/Wizard/Taoist, and every numeric knob to its slider range.
16. **Big map listener runs while closed.** `hudBigMap.ts:215-217`: after the first open, every page-wide `mousemove` runs `cellAt` (two layout reads) before the `this.open` check. Move the check first.
   **Status: Fixed.** The handler returns at once when the map is closed.
17. **Drag popup listeners linger.** `hudDrag.ts:259-260`: `popAt`'s document `mousedown` listener is removed only by a click outside. OK and Cancel leave it attached until the next mousedown, and replacing a popup leaves the old one's listener. Remove it in a shared `close()`.
   **Status: Fixed.** `popAt` returns `{ p, close }`; `close()` removes the listener, and opening a new box closes the old one.
18. **Misplaced doc comments.** `lab.ts:357-358`: `applyGameSettings` lost its doc comment, so `applyHud` now has two. Same at `hudMock.ts:537-538` (an orphaned "The HUD's scale" comment).
   **Status: Fixed.** Both comments moved to their functions.

Checked and not bugs:
- Enum use (`AttackMode`, `PetMode`, `MirClass`, `BuffType`, `EquipmentSlot`) matches `src/protocol/enums.generated.ts`.
- The chat log is capped at 250 lines and the history at 30.
- `clearTimeout` on interval ids in `LoadingMock.show` is valid (the timers share one id pool).
- Popups trap keys correctly in the capture phase.

## Improvements

**Porting readiness (extract before moving into `WebClient/src/ui`)**
- **Input router.** Replace the roughly ten `window` keydown listeners (hudWindow, hudMock, hudPopup, hudLoading, hudGameMenu, hudHelp, hudOptions, hudKeybinds, hudTooltip Shift, hudDrag Esc) with one dispatcher and a focus stack. Today, correctness depends on field construction order plus capture vs bubble. Route hotkeys through the real keybind table (`KeybindOptions`; hudKeybinds and hudOptions already load `keyBinds()` from src) instead of hard-coded `e.code` checks in `hudMock.ts:494-521`. Ignore `e.repeat` for toggles; holding C currently flickers the window.
- **Shared primitives.** `h`/`injectCss` are shared, but `svg(d)` is redefined in five or more files (chat, help, mentor, relationship, minimap). Class-name arrays are repeated four times in hudMock, plus hudCharacter, hudTooltip and hudSelect; use `MirClass` from `enums.generated`. `pretty()` is in hudLoading and hudMinimap, and number formatting is repeated. Popovers and menus come in three implementations (chat `pop`, drag `dpop`, HUD `hm-modes`); make one component. Chat has its own drag code (`hudChat.ts:237-256`) instead of the `HudWindow.drag` snapping.
- **Model and host layer.** Mock state lives inside UI classes: gold in hudMock and in Inventory, equipment in `WORN`, the bag and belt in hudItems, the roster in hudPeople. Put all of it behind host interfaces (bag, equipment, gold, player), the way `WorldHost` and `GameMenuHost` already work, so the port swaps in the session. Use `Spell` and `ItemType` from `enums.generated` instead of string names and types (`hudMock.ts:79-92`, `hudInventory.ts:13-14`).
- **Text and markup.** Ban `innerHTML` for anything with data. Today it is used for static SVG icons, which is fine, but popups and chat menus take HTML strings. Give `confirm`/`input` a text-parts API.
- **Theming.** The variables are good. Move the remaining hard-coded semantic colours (`#6fe08a`, `#ff5a4a`, `#c44`, `#ffd75a`, `#f3e08a`, grade colours) to tokens (`--ok`, `--bad`, `--warn`, `--gold`, `--grade-*`). Move the CSS strings into real stylesheet files when porting.
- **Overlap with existing src/ui.** `src/ui` already has `messageBox.ts`, `itemLabel.ts`, `miniMap.ts`, `bigMap.ts`, `hudCommon.ts`, `hudExtras.ts`, and others. Decide per window whether the mock replaces them or shares logic (for example, item rules in `hudItems` versus the item label), and avoid two `hudExtras.ts`.
- **Circular import.** `hudItems` → `hudTooltip` → `hudCharacter` → `hudItems`/`hudDrag`. It works only because no top-level code uses the cycle. Move `EQUIPPED_NAMES` and the item data loader into a data module.

**Performance (DOM churn)**
- `HudMock.render()` (`hudMock.ts:1152-1158`) runs `bag.filter`, `bagWeight()` and three `textContent` writes every frame. Cache the strings, or update from `onBag` and gold changes.
- Buff timers (`:1103-1109`) rewrite `textContent` every frame. Update only when the shown second changes.
- `onBag` makes Inventory rebuild all 40 cells (40 `libPicture` lookups) on every belt potion use, even when shut (`hudInventory.ts:63,67-76`). Mark it dirty and redraw in `shown()` or when it is open.
- `ChatMock.add` rebuilds the whole log (up to 250 lines, a `RegExp` per line) and the tabs for every message (`hudChat.ts:283,300-333`). Append the new node and trim the oldest. The chat simulation (`:173`) and `ageLines` (`:172`) run even with the HUD hidden. The game menu (250 ms) and big map (400 ms) intervals also run while hidden if they were open. Gate them on `root.hidden`.

**UX and accessibility**
- Most controls are `div`s with `title` attributes only. Use `<button>` or `role="button"`, `tabindex` and Enter/Space handling for close ×, tiles, chips and slots.
- Windows have no focus management (nothing moves focus in on open or back on close).
- The hold-to-confirm danger button has no keyboard path.
- Drag targets use colour only (green/red outline); the label helps but needs `aria-live` for screen readers.
- Respect `prefers-reduced-motion` for the tooltip shine, loading pan, login embers and HUD flashes.
- The Character Stats slide-out (270 px) is not counted by `place()`, so it can open off the right edge.
- Window drags use mouse events without pointer capture. Releasing outside the browser leaves the drag running; use pointer events with `setPointerCapture`.

## Typecheck result

- `WebClient/tsconfig.json` includes `src`, `tools` and `play`, and `npm run typecheck` runs `tsc --noEmit -p tsconfig.json`.
- Run 2026-10-15: `npx tsc --noEmit -p tsconfig.json` in `C:\Mir2\WebClient` gave exit 0 with no errors, covering all the `play/hud*.ts` files and `lab.ts`.
- `tsconfig.build.json` also includes `play` (it emits to `js/`). It was not run, to avoid writing build output.
