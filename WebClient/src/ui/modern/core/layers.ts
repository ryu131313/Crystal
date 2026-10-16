// Ported from play/hudMock.ts (the '.hudm' root, '.rig' / '.wins' and HudMock.place / layout / effectiveScale): the new
// UI's root and its fixed layers.
//
// The root '.hudm' is appended to #stage after the 2D overlay canvas at scale 1 (play/main.ts fit() already scales
// #stage to the window), sized to the game resolution. Children, bottom to top:
//   rig      the bottom HUD (orbs, belt, skill row, menu strip); centred at the bottom, scale k
//   wins     every window (z band 10-39, wm.ts) and the always-on parts (chat, minimap, tracker, party, extras); scale k
//   scenes   full-screen scenes (z 40/41 inside: loading, phase 4 screens); scale 1, the whole screen
//   tips     the item tooltip; scale k
//   guides   window snap guides while dragging; scale k (same coordinates as wins)
//   menu     the game menu; scale k
//   pops     modal popups and request toasts (popups.ts); scale k
//   drag     the item drag ghost / label / small popups / toasts; scale k
// HUD scale k = UiScale x (UiAutoScale ? screenHeight / 768 : 1). The scaled layers are sized screen / k so their own
// coordinates are HUD pixels. Containers are pointer-events: none: only windows and controls take clicks (no
// pixelDetect). Everything is position: absolute inside the root; position: fixed inside the transformed #stage is
// the known offset defect, so never use it here: convert pointer positions with toLocal().
//
// Below a 1040 px wide windows layer the root gets '.narrow' (windows switch to their narrow layout).

import { h } from "./dom.ts";
import { installBaseStyles } from "./styles.ts";

/** Below this windows-layer width (HUD pixels) the root is '.narrow'. */
export const NARROW_WIDTH = 1040;
/** The reference height for auto scale (the desktop's 1024 x 768). */
export const AUTO_SCALE_HEIGHT = 768;

/** HUD scale k = UiScale x (UiAutoScale ? screenHeight / 768 : 1), kept in 0.25-4. */
export function hudScale(uiScale: number, autoScale: boolean, screenHeight: number): number {
  const s = (Number.isFinite(uiScale) && uiScale > 0 ? uiScale : 1) * (autoScale ? screenHeight / AUTO_SCALE_HEIGHT : 1);
  return Math.min(4, Math.max(0.25, s));
}

/** The current layout: the screen in game pixels, k, and the scaled layers' size in HUD pixels. */
export interface LayoutInfo {
  readonly screenW: number; readonly screenH: number;
  readonly k: number;
  /** Width / height of the scaled layers (screen / k). */
  readonly width: number; readonly height: number;
  readonly narrow: boolean;
}

export class Layers {
  readonly root: HTMLElement;
  readonly rig: HTMLElement;
  readonly wins: HTMLElement;
  readonly scenes: HTMLElement;
  readonly tips: HTMLElement;
  readonly guides: HTMLElement;
  readonly menu: HTMLElement;
  readonly pops: HTMLElement;
  readonly drag: HTMLElement;
  private info: LayoutInfo = { screenW: 1024, screenH: 768, k: 1, width: 1024, height: 768, narrow: true };

  constructor() {
    installBaseStyles();
    const ly = (name: string) => h("div", "ly ly-" + name);
    this.rig = h("div", "rig");
    this.wins = ly("wins");
    this.wins.classList.add("wins");
    this.scenes = ly("scenes");
    this.tips = ly("tips");
    this.guides = ly("guides");
    this.menu = ly("menu");
    this.pops = ly("pops");
    this.drag = ly("drag");
    this.root = h("div", "hudm", this.rig, this.wins, this.scenes, this.tips, this.guides, this.menu, this.pops, this.drag);
  }

  /** The layers drawn at HUD scale k (all but rig, which scales around its own bottom centre, and scenes). */
  get scaled(): readonly HTMLElement[] { return [this.wins, this.tips, this.guides, this.menu, this.pops, this.drag]; }

  get layout(): LayoutInfo { return this.info; }

  /** Appends the root to `stage` (last child: after the 2D overlay canvas). */
  mount(stage: HTMLElement): void { stage.append(this.root); }

  /** Sizes the root to the screen and applies k; returns whether anything changed. */
  apply(screenW: number, screenH: number, k: number): boolean {
    const width = screenW / k, height = screenH / k, narrow = width < NARROW_WIDTH;
    const o = this.info;
    if (o.screenW === screenW && o.screenH === screenH && o.k === k && this.root.style.width) return false;
    this.info = { screenW, screenH, k, width, height, narrow };
    Object.assign(this.root.style, { width: screenW + "px", height: screenH + "px" });
    this.rig.style.transform = `translateX(-50%) scale(${k})`;
    for (const el of this.scaled) Object.assign(el.style, { transform: `scale(${k})`, width: width + "px", height: height + "px" });
    this.root.classList.toggle("narrow", narrow);
    this.root.style.setProperty("--k", String(k));
    return true;
  }

  remove(): void { this.root.remove(); }
}

/** A pointer position (clientX / clientY) in an element's own coordinates, whatever scales sit above it (#stage's fit
 *  scale x HUD scale). Measure once per gesture where possible (pass the rect / scale you measured on pointerdown). */
export function toLocal(el: HTMLElement, clientX: number, clientY: number): [number, number] {
  const r = el.getBoundingClientRect();
  const s = scaleOf(el, r);
  return [(clientX - r.left) / s, (clientY - r.top) / s];
}

/** The total CSS scale of an element (screen px per own px). */
export function scaleOf(el: HTMLElement, rect = el.getBoundingClientRect()): number {
  return el.offsetWidth ? rect.width / el.offsetWidth : 1;
}
