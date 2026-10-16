// Ported from play/hudMock.ts (the '.hudm' root theme block, ~150-162), play/hudWindow.ts (the window CSS and the
// '.halo' glow) and play/hudPopup.ts (button look): the base sheet of the new UI. Module CSS of the HUD and windows is
// appended after this one through dom.ts injectCss (one <style id="crystal-ui">).
//
// Performance.md rules baked in: glows pulse by opacity ('.halo'), never by animating box-shadow / filter; no
// backdrop-filter anywhere (the world canvas below changes every frame); every animation and transition is cut under
// prefers-reduced-motion.

import { injectCss } from "./dom.ts";

/** Root variables: theme bases (theme.ts writes them inline on the root), derived panel colours, semantic tokens. */
const ROOT_CSS = `
.hudm { --h: 218; --sat: 1; --lit: 1; --ac: #3fb4ff; --fr: #3a5a8c; --tx: #d6e2f5; --gl: 1; --rad: 5px;
  --pa: hsl(var(--h) calc(45% * var(--sat)) calc(15% * var(--lit)) / .94);
  --pb: hsl(var(--h) calc(55% * var(--sat)) calc(6.5% * var(--lit)) / .95);
  --pc: hsl(var(--h) calc(40% * var(--sat)) calc(18% * var(--lit)) / .95);
  --frl: color-mix(in srgb, var(--fr) 55%, #fff);
  --frd: color-mix(in srgb, var(--fr) 45%, #000);
  --acd: color-mix(in srgb, var(--ac) 70%, var(--tx));
  --dim: color-mix(in srgb, var(--tx) 55%, var(--fr));
  --glow: color-mix(in srgb, var(--ac) min(100%, calc(60% * var(--gl))), transparent);
  --glr: calc(10px * var(--gl));
  /* Semantic tokens (Review-Core "Theming"): use these, never literal greens / reds / golds. */
  --ok: #6fe08a; --bad: #ff5a4a; --bad-soft: #ff8a7a; --warn: #f3c04a; --gold: #f3e08a; --info: #7cc4ff;
  --grade-none: #f3e08a; --grade-common: #f3e08a; --grade-rare: #3fbfff; --grade-legendary: #ff9a3c; --grade-mythical: #e0a0ff; --grade-heroic: #ff5a5a;
  --cls-warrior: #e0714f; --cls-wizard: #6aa8ff; --cls-taoist: #6fd38a; --cls-assassin: #c58bff; --cls-archer: #e6c25a;
  --field: #05070b;
  position: absolute; left: 0; top: 0; pointer-events: none; overflow: hidden; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  -webkit-font-smoothing: antialiased; }
.hudm * { box-sizing: border-box; }
.hudm [hidden] { display: none !important; }
/* Layers (layers.ts): containers never take clicks; windows / controls set pointer-events: auto themselves. */
.hudm > .ly { position: absolute; left: 0; top: 0; transform-origin: 0 0; pointer-events: none; }
.hudm > .ly-wins { z-index: 2; }
.hudm > .ly-scenes { z-index: 40; width: 100%; height: 100%; }
.hudm > .ly-tips { z-index: 50; }
.hudm > .ly-guides { z-index: 60; }
.hudm > .ly-menu { z-index: 70; }
.hudm > .ly-pops { z-index: 80; }
.hudm > .ly-drag { z-index: 90; }
.hudm > .rig { position: absolute; z-index: 1; left: 50%; bottom: 3px; transform-origin: 50% 100%; display: flex; align-items: flex-end; gap: 6px; }
/* Themed scrollbars (thin, accent thumb). */
.hudm ::-webkit-scrollbar { width: 8px; height: 8px; }
.hudm ::-webkit-scrollbar-track { background: color-mix(in srgb, var(--pb) 70%, #000); border-radius: 4px; }
.hudm ::-webkit-scrollbar-thumb { background: color-mix(in srgb, var(--fr) 80%, var(--ac)); border-radius: 4px; border: 1px solid #000; }
.hudm ::-webkit-scrollbar-thumb:hover { background: var(--ac); }
.hudm :focus-visible { outline: 1px solid var(--ac); outline-offset: 1px; }
/* Glow via opacity (Performance.md "Glow via opacity"): a '.halo' child keeps a fixed outer box-shadow (--halo) and only
   its opacity pulses, on the compositor. The parent must not clip it. --halo-t sets the period. */
.hudm .halo { position: absolute; inset: -1px; border-radius: inherit; pointer-events: none; opacity: 0; box-shadow: var(--halo, 0 0 var(--glr) var(--glow));
  animation: hudm-halo var(--halo-t, 1.6s) ease-in-out infinite; }
@keyframes hudm-halo { 50% { opacity: 1; } }
@media (prefers-reduced-motion: reduce) {
  .hudm *, .hudm *::before, .hudm *::after { animation-duration: .001ms !important; animation-iteration-count: 1 !important; animation-delay: 0s !important; transition-duration: .001ms !important; }
  .hudm .halo { opacity: .5; }
}
`;

/** Windows (wm.ts UiWindow): panel, title bar with the accent line, close cross, front glow, drag state, guides. */
const WINDOW_CSS = `
.hudm .hwin { position: absolute; pointer-events: auto; color: var(--tx);
  --page: linear-gradient(180deg, color-mix(in srgb, var(--pb) 85%, var(--pa)) 0%, color-mix(in srgb, var(--pb) 68%, #000) 30%,
    color-mix(in srgb, var(--pb) 42%, #000) 70%, color-mix(in srgb, var(--pb) 25%, #000) 100%); }
.hudm .hwin .panel { position: relative; z-index: 1; border-radius: var(--rad); background: var(--page); border: 1px solid var(--fr);
  box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 0 10px 30px rgba(0,0,0,.7); }
.hudm .hwin .title { position: relative; height: 30px; display: flex; align-items: center; justify-content: center; cursor: move; user-select: none; touch-action: none;
  font-size: 13px; font-weight: 700; letter-spacing: .28em; text-indent: .28em; color: var(--ac); text-shadow: 0 0 calc(8px * var(--gl)) var(--glow);
  background: linear-gradient(180deg, var(--pc), var(--pa)); border-radius: calc(var(--rad) - 1px) calc(var(--rad) - 1px) 0 0; }
.hudm .hwin .title::after { content: ""; position: absolute; left: 0; right: 0; bottom: 0; height: 2px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 35%, transparent) 22%, var(--ac) 50%, color-mix(in srgb, var(--ac) 35%, transparent) 78%, transparent);
  box-shadow: 0 0 calc(4px * var(--gl)) color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .hwin .x { position: absolute; right: 8px; top: 6px; width: 18px; height: 18px; display: flex; align-items: center; justify-content: center; padding: 0;
  font: inherit; font-size: 16px; line-height: 1; color: var(--dim); cursor: pointer; border: 0; background: none; border-radius: calc(var(--rad) * .6);
  letter-spacing: 0; text-indent: 0; text-shadow: none; }
.hudm .hwin .x:hover { color: #fff; background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .hwin.front .panel { border-color: color-mix(in srgb, var(--ac) 55%, var(--fr)); box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 30%, transparent), 0 10px 30px rgba(0,0,0,.7), 0 0 calc(14px * var(--gl)) color-mix(in srgb, var(--ac) 22%, transparent); }
/* While dragged, a window moves by transform on its own layer (no relayout, no repaint of its big shadow per move). */
.hudm .hwin.wdrag { will-change: transform; }
.hudm .wguide { position: absolute; pointer-events: none; background: var(--ac); box-shadow: 0 0 6px var(--glow); opacity: .85; }
.hudm .wguide.v { top: 0; bottom: 0; width: 1px; } .hudm .wguide.h { left: 0; right: 0; height: 1px; }
`;

/** Shared controls: buttons (any element with .btn; <button> is reset), text fields, section labels, empty states. */
const CONTROL_CSS = `
.hudm .btn { height: 22px; padding: 0 12px; display: inline-flex; align-items: center; justify-content: center; gap: 5px; cursor: pointer; user-select: none; pointer-events: auto;
  font: inherit; font-size: 11px; font-weight: 600; letter-spacing: .06em; color: var(--tx); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8);
  background: linear-gradient(180deg, var(--pc), var(--pb)); box-shadow: 0 0 0 1px #000; white-space: nowrap; }
.hudm .btn:hover { border-color: var(--ac); color: #fff; box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .btn.main { color: #fff; border-color: var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 50%, var(--pc)), color-mix(in srgb, var(--ac) 25%, var(--pb))); }
.hudm .btn.warn { color: #fff; border-color: var(--bad); background: linear-gradient(180deg, color-mix(in srgb, var(--bad) 45%, var(--pc)), color-mix(in srgb, var(--bad) 22%, var(--pb))); }
.hudm .btn.big { height: 30px; padding: 0 16px; font-size: 12px; }
/* Disabled keeps pointer events so its title (the reason) still shows (Review-Feature-Windows "UX"). */
.hudm .btn.dis, .hudm .btn[aria-disabled="true"] { opacity: .45; cursor: not-allowed; box-shadow: 0 0 0 1px #000; border-color: var(--fr); color: var(--tx); }
.hudm .btn svg { width: 13px; height: 13px; }
.hudm input.fld, .hudm textarea.fld, .hudm select.fld { height: 24px; padding: 0 8px; font: inherit; font-size: 12px; color: #fff; outline: none; pointer-events: auto;
  border-radius: calc(var(--rad) * .8); background: var(--field); border: 1px solid var(--fr); box-shadow: inset 0 1px 3px #000; }
.hudm textarea.fld { height: auto; padding: 6px 8px; resize: none; }
.hudm input.fld:focus, .hudm textarea.fld:focus, .hudm select.fld:focus { border-color: var(--ac); box-shadow: inset 0 1px 3px #000, 0 0 8px var(--glow); }
.hudm .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .empty-note { padding: 18px 10px; text-align: center; color: var(--dim); font-size: 11px; }
.hudm .gold { color: var(--gold); } .hudm .ok { color: var(--ok); } .hudm .bad { color: var(--bad); } .hudm .warn { color: var(--warn); }
/* Stroke icons (icons.ts svg()). */
.hudm svg.ic { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex: none; }
`;

/** Installs the base sheet (idempotent). layers.ts calls it when it builds the root. */
export function installBaseStyles(): void {
  injectCss("core-root", ROOT_CSS);
  injectCss("core-window", WINDOW_CSS);
  injectCss("core-controls", CONTROL_CSS);
}

/** The CSS variable for an ItemGrade value (0 None ... 5 Heroic). */
export function gradeVar(grade: number): string {
  return `var(--grade-${["none", "common", "rare", "legendary", "mythical", "heroic"][grade] ?? "none"})`;
}

/** The CSS variable for a MirClass value. */
export function classVar(cls: number): string {
  return `var(--cls-${["warrior", "wizard", "taoist", "assassin", "archer"][cls] ?? "warrior"})`;
}
