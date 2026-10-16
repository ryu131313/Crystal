# Scene Lab UI review (2026-10-15)

Review of the uncommitted UI mock in the scene lab: `WebClient/play/hud*.ts` (~16k lines, about 45 windows), the
`lab.ts` / `lab.html` changes and `lab-data/`. Ideas and findings only; no source was changed.

| File | Contents |
|---|---|
| [Review-Core.md](Review-Core.md) | HUD, window framework, drag, tooltip, popup, inventory, character, skills, chat, maps, options, keybinds, login/select/loading, lab integration. Typecheck clean (`npx tsc --noEmit`). |
| [Review-Feature-Windows.md](Review-Feature-Windows.md) | The other ~28 feature windows (trade, guild, mail, refine, awaken, socket, rent, mentor, ...), `lab-data`, the publish script. Includes where the mock's behaviour differs from Crystal's. |
| [Performance.md](Performance.md) | Performance review: free gains (paint-only infinite animations, backdrop blur over the live canvas, per-frame/per-mousemove DOM writes), tiered with fixes and how to measure. |
| [Server-Changes.md](Server-Changes.md) | Per-window classification of what is client-only, what needs packets, and what needs persisted data, plus proposed packets, an `Envir.Version` 119 bundle, risks and the order of work. |

## Fix before the next web client publish

- **The lab ships to players.** `tools/publish-webclient.ps1` excludes `lab.html` and `lab-data\` but still copies every
  `js/**/*.js`, and `WebRelay/PageBuild.cs` (`ServePageAsync`) adds a modulepreload for every module except `tools/`.
  So `js/play/lab.js` and the `hud*.js` modules (~1.26 MB) would be preloaded on the game page.
  **Fixed 2026-10-15:** the publish script now copies only `main.js` from `js\play\` (the game entry imports nothing else
  from `play/`). The dev relay that serves the working tree still preloads the lab modules on its game page (dev only).
- `lab-data/` is not git-ignored and has no generator checked in.

## Top bugs (details and file:line in the review files)

1. Items are lost or duplicated in drag/drop and the equipment slots: a whole stack goes into an equipment slot, the
   drag keeps a copy of the stack taken at drag start, and several places drop what `addToBag` could not fit.
2. Window z-index grows without limit, so the tooltip (z 50) and login screen (z 40) end up behind windows.
3. About ten separate global keydown listeners, some active while hidden. Fix: one input router with a focus stack.
4. Trade is only cancelled by the title-bar X. Esc and Close leave the offered items out of the bag.
5. Awaken / socket keep a stale selection after an item is destroyed, so `splice(-1)` removes the wrong item.
6. `innerHTML` with names (chat name menu, hero feed, ranking guild shield, popup text) is XSS once the data is real.

## Server-side headline

- Most windows are client-only work over existing packets. The web client already decodes all but 2 server packets.
- About 14 optional new packets (no DB change), plus a proposed `S.ServerRules` login packet so windows stop hard-coding
  refine / awaken / mail / marriage costs.
- **Risk:** bulk buttons (sell junk, repair all, collect all) can exceed `Settings.MaxPacket` (50 per 5 s). That gives a
  24 h IP block, and behind WebRelay all web players may share one IP. Add batch packets before those buttons.
- **Belt (user decisions 2026-10-15/16):** 10 slots. Keys 1-8 are the player's belt as proper inventory cells (6 → 8,
  `Inventory` 46 → 48, resize cap 86 → 88), keys 9-0 the hero's belt (existing, no server change). The user DB is wiped
  when this ships, so there is no migration; the saved format does not change, so there is no `Envir.Version` bump. Change list:
  Server-Changes.md "Belt: 8 player cells".
- Porting DOM windows conflicts with webclient.md §3.5 ("no plain DOM panels"); needs an explicit decision.
