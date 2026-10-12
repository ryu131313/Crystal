// Shared helpers for the desktop HUD dialogs (MainDialog, ChatDialog, MiniMapDialog, BuffDialog, ...):
//   pixelDetect  MirControl.PixelDetect: a click / move on a transparent pixel of the dialog's own picture goes to the
//                map below (the event is re-dispatched to the game canvas), like the desktop's IsMouseOver test.
//   Section      Library.Draw(index, Rectangle section, Point, ...): a cropped part of a picture (orbs, bars).
//   scaleOf      the stage's CSS scale (main.ts scales the 1024 x 768 stage to the window).

import { libAsync } from "../game/libraries.ts";
import { libPicture, libPictureNow, type MirControl, type MirImage } from "./mir/controls.ts";

interface AlphaMask { w: number; h: number; alpha: Uint8Array }
const masks = new Map<string, AlphaMask | null>();
const loading = new Set<string>();

function maskNow(library: string, index: number): AlphaMask | null | undefined {
  const key = library + "#" + index;
  if (masks.has(key)) return masks.get(key);
  if (!loading.has(key)) {
    loading.add(key);
    void (async () => {
      const lib = await libAsync(library);
      const img = lib && (await lib.image(index));
      if (!img || !img.width || !img.height) { masks.set(key, null); return; }
      const alpha = new Uint8Array(img.width * img.height);
      for (let i = 0; i < alpha.length; i++) alpha[i] = img.rgba[i * 4 + 3]!;
      masks.set(key, { w: img.width, h: img.height, alpha });
    })().catch(() => masks.set(key, null));
  }
  return undefined;
}

/** The scale of the stage element (CSS transform from main.ts). */
export function scaleOf(el: HTMLElement): number {
  const r = el.getBoundingClientRect();
  return r.width / (el.offsetWidth || 1) || 1;
}

/** MirControl.PixelDetect for a HUD dialog: mouse events on a transparent pixel of `ctrl`'s own picture (not its
 *  children) are passed to the game canvas instead (walk / attack / pick up through the dialog's empty parts). */
export function pixelDetect(ctrl: MirImage): void {
  const forward = (e: Event) => {
    if (e.target !== ctrl.el) return;
    const m = e as MouseEvent;
    const lib = ctrl.library;
    if (!lib || ctrl.index < 0) return;
    const mask = maskNow(lib, ctrl.index);
    if (mask === undefined) return; // still loading: opaque
    const s = scaleOf(ctrl.el);
    const r = ctrl.el.getBoundingClientRect();
    const x = Math.floor((m.clientX - r.left) / s), y = Math.floor((m.clientY - r.top) / s);
    if (mask && x >= 0 && y >= 0 && x < mask.w && y < mask.h && mask.alpha[y * mask.w + x]! > 0) return;
    const canvas = document.getElementById("game");
    if (!canvas) return;
    e.stopImmediatePropagation();
    if (e.type === "contextmenu") e.preventDefault();
    if (e.type === "pointerdown") return; // the mousedown that follows is forwarded
    canvas.dispatchEvent(new MouseEvent(e.type, m));
  };
  for (const t of ["pointerdown", "mousedown", "mousemove", "contextmenu"]) ctrl.el.addEventListener(t, forward, { capture: true });
}

/** Library.Draw with a source rectangle: shows (sx, sy, w, h) of a picture at the element's Location. */
export class Section {
  readonly el = document.createElement("div");
  private key = "";

  constructor(parent: MirControl | HTMLElement, x = 0, y = 0) {
    this.el.className = "mir-pic";
    this.el.style.left = x + "px";
    this.el.style.top = y + "px";
    (parent instanceof HTMLElement ? parent : parent.el).append(this.el);
  }

  move(x: number, y: number): void {
    const l = x + "px", t = y + "px";
    if (this.el.style.left !== l) this.el.style.left = l;
    if (this.el.style.top !== t) this.el.style.top = t;
  }

  /** Draws `library[index]` cropped to (sx, sy, w, h); w or h <= 0 draws nothing. */
  set(library: string, index: number, sx: number, sy: number, w: number, h: number): void {
    w = Math.max(0, Math.floor(w));
    h = Math.max(0, Math.floor(h));
    const key = `${library}#${index}#${sx}#${sy}#${w}#${h}`;
    if (key === this.key) return;
    this.key = key;
    this.el.style.width = w + "px";
    this.el.style.height = h + "px";
    this.el.style.backgroundPosition = `${-sx}px ${-sy}px`;
    const apply = (url: string | null) => { if (this.key === key) this.el.style.backgroundImage = url ? `url("${url}")` : ""; };
    const now = libPictureNow(library, index);
    if (now !== undefined) apply(now?.url ?? null);
    else void libPicture(library, index).then((p) => apply(p?.url ?? null));
  }
}

/** Sets a label-like element's text only when it changed. */
export function setText(el: HTMLElement, text: string): void {
  if (el.textContent !== text) el.textContent = text;
}

/** Functions.PrintTimeSpanFromSeconds (accurate). */
export function printTimeSpan(secs: number): string {
  secs = Math.max(0, Math.round(secs));
  const d = Math.floor(secs / 86400), h = Math.floor((secs % 86400) / 3600), m = Math.floor((secs % 3600) / 60), s = secs % 60;
  const p2 = (n: number) => String(n).padStart(2, "0");
  if (secs < 60) return `${s}s`;
  if (secs < 3600) return `${m}m ${p2(s)}s`;
  if (secs < 86400) return `${h}h ${p2(m)}m ${p2(s)}s`;
  return `${d}d ${p2(h)}h ${p2(m)}m ${p2(s)}s`;
}
