// The HUD's small extras for the scene lab's UI tester (2026-10-15), modern takes on five desktop pieces:
//  - Durability (DuraStatusDialog / CharacterDuraPanel): worn gear at or under half durability shows as a strip of
//    icons under the minimap, rimmed amber (<= 50 %, the desktop's Warning = MaxDura / 2) or red (<= 20 %, Danger =
//    MaxDura / 5), broken gear struck through; hover lists them with bars, click opens the Character window. Durability
//    is the item tooltip's (hudTooltip.ts duraFrac), worn down by hits (armour pieces) and kills (the weapon).
//  - Poisons (MapObject.DrawPoison's coloured dots over a head, PoisonType): a debuff row mirroring the buff row, above
//    the right wing, one icon per poison in the dot's colour with a timer, plus what it does here: Green and Bleeding
//    tick HP down (the HP bar turns green while poisoned), Frozen / Paralysis / Stun stop casting and the belt (a status
//    line under the character), Blindness darkens the screen, Frozen frosts it, Paralysis greys it, Dazed blurs it, Slow
//    tints it, and a Delayed Explosion counts down to a big hit.
//  - Compass (CompassDialog, S.SetCompass from NPC scripts): an arrow circling the character pointing at the target,
//    with its name and distance in steps; it clears on arrival.
//  - Timers (TimerDialog, S.SetTimer key / seconds / type 0 plain, 1 hourglass, 2 alert; S.ExpireTimer): countdown pills
//    under the notice line, soonest first, a ring for the time left, red pulse in the last 10 s; the same key updates.
//  - Dice (RollDialog, S.Roll type 0 die / 1 yut, result, auto roll): a 3D die that tumbles onto the result, or four yut
//    sticks (Do / Gae / Geol / Yut / Mo); click to roll unless it rolls itself, then the result goes to chat.
// Mock data only; nothing is sent anywhere.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { EQUIPPED_NAMES } from "./hudCharacter.ts";
import { info } from "./hudItems.ts";
import { duraFrac, setDura } from "./hudTooltip.ts";
import { loadMapData, npcName } from "./hudBigMap.ts";
import type { WorldHost } from "./hudMinimap.ts";
import { h, injectCss, setText } from "./hudWindow.ts";

export interface ExtrasHost {
  world(): WorldHost | undefined;
  /** Changes HP by `pct` of the maximum (negative = damage). */
  hp(pct: number): void;
  bigHit(): void;
  say(text: string): void;
  openCharacter(): void;
}

const svgIcon = (d: string) => `<svg viewBox="0 0 24 24"><path d="${d}"/></svg>`;
/** PoisonType: [flag, name, colour (DrawPoison's, lifted for a dark HUD), icon path, what it does, seconds]. */
const POISONS: [number, string, string, string, string, number][] = [
  [1, "Green poison", "#3fd86b", "M12 3c3 4 6 7.5 6 11a6 6 0 01-12 0c0-3.5 3-7 6-11zM9.5 14.5h.01M14.5 14.5h.01M10 17.5h4", "Losing HP over time", 12],
  [2, "Red poison", "#ff5050", "M12 3l7 3v5c0 5-3 8.5-7 10-4-1.5-7-5-7-10V6zM12.5 7l-2 5h3l-2 5", "Armour lowered (AC / MAC)", 15],
  [128, "Bleeding", "#e0404a", "M8 4c2 3 3 5 3 7a3 3 0 01-6 0c0-2 1-4 3-7zM16 9c2 3 3 5 3 7a3 3 0 01-6 0c0-2 1-4 3-7z", "Losing HP over time", 10],
  [4, "Slow", "#b070ff", "M12 4a8 8 0 110 16 8 8 0 010-16zM12 8v4l3 2", "Moving and attacking slower", 8],
  [16, "Stunned", "#ffe14a", "M12 4l1.8 3.7 4 .6-2.9 2.8.7 4L12 13.2 8.4 15.1l.7-4-2.9-2.8 4-.6zM5 19c2-1.5 4-1.5 7 0s5 1.5 7 0", "Can't act", 3],
  [1024, "Dazed", "#ffe14a", "M4 12a8 8 0 0116 0M7 12a5 5 0 0110 0M10 12a2 2 0 014 0M12 16v4", "Can't attack", 5],
  [512, "Blinded", "#ff5cb0", "M3 12s3.5-6 9-6 9 6 9 6-3.5 6-9 6-9-6-9-6zM12 9a3 3 0 110 6 3 3 0 010-6zM4 4l16 16", "Can barely see", 7],
  [8, "Frozen", "#5aa0ff", "M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9M9.5 4.5L12 7l2.5-2.5M9.5 19.5L12 17l2.5 2.5", "Can't act", 4],
  [32, "Paralysed", "#c4c4c4", "M13 2L5 14h6l-1 8 8-12h-6z", "Can't act", 4],
  [64, "Delayed explosion", "#ffa53a", "M11 8a6 6 0 110 12 6 6 0 010-12zM14 9l3-3M17 3v2M20 6h-2", "Explodes when the timer runs out", 6],
];
const LOCKS = 8 | 16 | 32 | 256;
const WARN = 0.5, DANGER = 0.2;
const fmtT = (s: number) => s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
/** Steps (cells) to a compass direction name, Mir's eight directions. */
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
const YUT = ["Mo", "Do", "Gae", "Geol", "Yut"];

const CSS = `
/* Durability strip */
.hudm .hdura { position: absolute; right: 14px; top: 250px; display: flex; gap: 4px; justify-content: flex-end; pointer-events: auto; }
.hudm .hdura[hidden] { display: none; }
.hudm .hdura .dc { position: relative; width: 28px; height: 28px; border-radius: 7px; cursor: pointer; background: center / auto no-repeat, radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%);
  border: 1px solid var(--dc); box-shadow: 0 0 0 1px #000, 0 0 8px color-mix(in srgb, var(--dc) 55%, transparent); image-rendering: pixelated; animation: hudm-din .25s ease-out; }
.hudm .hdura .dc i { position: absolute; left: 3px; right: 3px; bottom: 2px; height: 3px; border-radius: 2px; background: rgba(0,0,0,.7); overflow: hidden; }
.hudm .hdura .dc i b { display: block; height: 100%; background: var(--dc); }
.hudm .hdura .dc.danger { animation: hudm-din .25s ease-out, hudm-dpulse 1.4s ease-in-out infinite; }
.hudm .hdura .dc.broken { filter: grayscale(1) brightness(.6); }
.hudm .hdura .dc.broken::after { content: ""; position: absolute; left: 3px; right: 3px; top: 50%; height: 2px; background: #ff5a4a; transform: rotate(-35deg); box-shadow: 0 0 3px #000; }
@keyframes hudm-din { from { opacity: 0; transform: translateY(-4px); } }
@keyframes hudm-dpulse { 50% { box-shadow: 0 0 0 1px #000, 0 0 14px var(--dc); } }
.hudm .hdura .pop { position: absolute; right: 0; top: calc(100% + 6px); width: 230px; padding: 8px 10px; border-radius: var(--rad); font-size: 11px; color: var(--tx); display: none;
  background: color-mix(in srgb, var(--pb) 95%, #000); border: 1px solid var(--fr); box-shadow: 0 8px 22px rgba(0,0,0,.7); z-index: 5; }
.hudm .hdura:hover .pop { display: block; }
.hudm .hdura .pop .hd { font-weight: 700; color: var(--ac); letter-spacing: .1em; font-size: 10px; margin-bottom: 6px; }
.hudm .hdura .pop .r { display: grid; grid-template-columns: 1fr 70px 36px; align-items: center; gap: 6px; height: 18px; }
.hudm .hdura .pop .r span:last-child { text-align: right; font-variant-numeric: tabular-nums; color: var(--dc); }
.hudm .hdura .pop .r em { height: 4px; border-radius: 2px; background: #04060a; overflow: hidden; } .hudm .hdura .pop .r em b { display: block; height: 100%; background: var(--dc); }
.hudm .hdura .pop .ft { margin-top: 6px; color: var(--dim); font-size: 10px; }
/* Debuff row (the buff row's mirror, above the right wing) */
.hudm .hm-debuffs { position: absolute; right: 0; bottom: calc(100% + 8px); display: flex; gap: 4px; flex-direction: row-reverse; }
.hudm .hm-debuff { position: relative; width: 26px; pointer-events: auto; text-align: center; animation: hudm-din .2s ease-out; }
.hudm .hm-debuff i { display: flex; align-items: center; justify-content: center; width: 26px; height: 26px; border-radius: calc(var(--rad) * .7);
  background: radial-gradient(circle at 50% 35%, color-mix(in srgb, var(--pc) 70%, var(--pb)), #05070b); border: 1px solid var(--pz); box-shadow: 0 0 0 1px #000, 0 0 8px color-mix(in srgb, var(--pz) 45%, transparent); }
.hudm .hm-debuff svg { width: 17px; height: 17px; fill: none; stroke: var(--pz); stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; filter: drop-shadow(0 0 3px var(--pz)); }
.hudm .hm-debuff span { display: block; margin-top: 1px; font-size: 9px; font-weight: 600; color: var(--tx); text-shadow: 0 0 2px #000, 0 0 2px #000; font-variant-numeric: tabular-nums; }
.hudm .hm-debuff.end i { animation: hudm-dblink .5s steps(2) infinite; } @keyframes hudm-dblink { 50% { opacity: .35; } }
.hudm .hm-bar.hm-hp.psn .fill { background: linear-gradient(90deg, #0d3a18, #1f8a3c 55%, #4fe07a); }
.hudm .hm-bar.hm-hp.psn { box-shadow: 0 0 10px rgba(63, 216, 107, .45); }
/* Screen effects (behind the HUD) */
.hudm .hfx { position: absolute; inset: 0; pointer-events: none; }
/* Off layers are visibility: hidden once faded, so their full-screen backdrop-filter is never computed over the live
   world; daze's sway pauses while off (paused, not removed, so it does not jump during the fade). */
.hudm .hfx div { position: absolute; inset: 0; opacity: 0; visibility: hidden; transition: opacity .5s, visibility 0s .5s; }
.hudm .hfx .on { opacity: 1; visibility: visible; transition: opacity .5s, visibility 0s; }
.hudm .hfx .blind { background: radial-gradient(circle at 50% 46%, transparent 0 70px, rgba(0,0,0,.92) 230px); }
.hudm .hfx .frost { background: radial-gradient(ellipse 70% 65% at 50% 50%, transparent 55%, rgba(120,180,255,.42)); box-shadow: inset 0 0 90px rgba(170,215,255,.55); backdrop-filter: saturate(.6) hue-rotate(15deg); }
.hudm .hfx .para { backdrop-filter: grayscale(.85) brightness(.85); }
.hudm .hfx .daze { backdrop-filter: blur(1.6px); animation: hudm-daze 2.4s ease-in-out infinite paused; } .hudm .hfx .daze.on { animation-play-state: running; } @keyframes hudm-daze { 50% { transform: translateX(3px) rotate(.25deg); } }
.hudm .hfx .slow { background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 60%, rgba(140,70,220,.3)); }
.hudm .hfx .green { background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 62%, rgba(40,200,90,.22)); }
.hudm .hstat { position: absolute; left: 50%; top: 52%; translate: -50% 0; padding: 3px 12px; border-radius: 12px; font-size: 12px; font-weight: 700; letter-spacing: .08em; color: #fff;
  background: rgba(8,10,16,.75); border: 1px solid var(--pz, var(--fr)); box-shadow: 0 0 12px color-mix(in srgb, var(--pz, var(--ac)) 40%, transparent); pointer-events: none; }
.hudm .hstat[hidden] { display: none; }
/* Compass */
.hudm .hcomp { position: absolute; left: 50%; top: 47%; width: 0; height: 0; pointer-events: none; }
.hudm .hcomp[hidden] { display: none; }
.hudm .hcomp .arm { position: absolute; left: 0; top: 0; width: 0; height: 0; transition: transform .25s ease-out; }
.hudm .hcomp .arr { position: absolute; left: -15px; top: -104px; width: 30px; height: 30px; filter: drop-shadow(0 0 6px var(--glow)) drop-shadow(0 2px 3px #000); animation: hudm-cbob 1.2s ease-in-out infinite; }
.hudm .hcomp .arr svg { width: 100%; height: 100%; fill: var(--ac); stroke: #fff; stroke-width: 1; }
@keyframes hudm-cbob { 50% { translate: 0 -5px; } }
.hudm .hcomp .lbl { position: absolute; left: 0; top: 66px; translate: -50% 0; white-space: nowrap; padding: 2px 10px; border-radius: 10px; font-size: 11px; color: var(--tx);
  background: rgba(8,10,16,.7); border: 1px solid color-mix(in srgb, var(--ac) 50%, var(--fr)); }
.hudm .hcomp .lbl b { color: #fff; } .hudm .hcomp .lbl span { color: var(--ac); margin-left: 6px; font-weight: 700; }
.hudm .hcomp.here .lbl { border-color: var(--ac); box-shadow: 0 0 14px var(--glow); }
/* Timers */
.hudm .htimers { position: absolute; left: 50%; top: 44px; translate: -50% 0; display: flex; gap: 8px; pointer-events: none; }
.hudm .htm { display: flex; align-items: center; gap: 8px; padding: 4px 12px 4px 5px; border-radius: 20px; color: var(--tx); font-size: 11px; pointer-events: auto;
  background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 80%, transparent), color-mix(in srgb, var(--pb) 88%, transparent)); border: 1px solid var(--fr);
  box-shadow: 0 0 0 1px #000, 0 6px 16px rgba(0,0,0,.55); animation: hudm-din .25s ease-out; }
.hudm .htm.first { border-color: color-mix(in srgb, var(--ac) 60%, var(--fr)); }
.hudm .htm svg.ring { width: 30px; height: 30px; flex: none; transform: rotate(-90deg); }
.hudm .htm svg.ring circle { fill: none; stroke-width: 3; } .hudm .htm svg.ring .bg { stroke: #04060a; } .hudm .htm svg.ring .fg { stroke: var(--ac); stroke-linecap: round; filter: drop-shadow(0 0 3px var(--glow)); }
.hudm .htm .ic { position: relative; width: 30px; height: 30px; flex: none; } .hudm .htm .ic > svg:not(.ring) { position: absolute; inset: 8px; width: 14px; height: 14px; fill: none; stroke: var(--tx); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .htm .t { font-size: 15px; font-weight: 800; color: #fff; font-variant-numeric: tabular-nums; letter-spacing: .02em; }
.hudm .htm .k { color: var(--dim); font-size: 10px; letter-spacing: .04em; }
.hudm .htm.hot { border-color: #ff5a4a; animation: hudm-thot 1s ease-in-out infinite; } .hudm .htm.hot .fg { stroke: #ff5a4a; } .hudm .htm.hot .t { color: #ffb3a6; }
@keyframes hudm-thot { 50% { box-shadow: 0 0 0 1px #000, 0 0 16px rgba(255, 90, 74, .55); } }
.hudm .htm.gone { animation: hudm-tgone .5s ease-in forwards; } @keyframes hudm-tgone { to { opacity: 0; transform: translateY(-8px); } }
/* Dice */
.hudm .hdice { position: absolute; left: 50%; top: 40%; translate: -50% -50%; width: 260px; padding: 16px 16px 14px; text-align: center; border-radius: 14px; pointer-events: auto; color: var(--tx);
  background: radial-gradient(ellipse 80% 60% at 50% 40%, color-mix(in srgb, var(--ac) 14%, var(--pb)), color-mix(in srgb, var(--pb) 92%, #000)); border: 1px solid color-mix(in srgb, var(--ac) 50%, var(--fr));
  box-shadow: 0 0 0 1px #000, 0 16px 40px rgba(0,0,0,.75), 0 0 24px var(--glow); animation: hudm-din .2s ease-out; }
.hudm .hdice[hidden] { display: none; }
.hudm .hdice h3 { margin: 0; font-size: 13px; letter-spacing: .24em; color: var(--ac); text-shadow: 0 0 8px var(--glow); }
.hudm .hdice .sub { font-size: 11px; color: var(--dim); margin: 3px 0 10px; }
.hudm .hdice .stage { height: 120px; display: flex; align-items: center; justify-content: center; perspective: 600px; cursor: pointer; }
.hudm .hdice .cube { position: relative; width: 64px; height: 64px; transform-style: preserve-3d; transform: rotateX(-25deg) rotateY(35deg); }
.hudm .hdice .face { position: absolute; inset: 0; border-radius: 12px; display: grid; grid-template: repeat(3, 1fr) / repeat(3, 1fr); padding: 9px;
  background: radial-gradient(circle at 35% 30%, #f6f8fc, #c9d2e2); border: 1px solid #8592aa; box-shadow: inset 0 0 10px rgba(0,0,0,.25); backface-visibility: hidden; }
.hudm .hdice .face i { width: 11px; height: 11px; margin: auto; border-radius: 50%; background: radial-gradient(circle at 40% 35%, #3a4660, #0b1020); visibility: hidden; }
.hudm .hdice .face.one i { background: radial-gradient(circle at 40% 35%, #ff7a6a, #8a1010); width: 15px; height: 15px; }
.hudm .hdice .face i.on { visibility: visible; }
.hudm .hdice .sticks { display: flex; gap: 10px; align-items: center; height: 110px; }
.hudm .hdice .stick { width: 16px; height: 92px; border-radius: 8px; transform-style: preserve-3d; position: relative; }
.hudm .hdice .stick b { position: absolute; inset: 0; border-radius: 8px; backface-visibility: hidden; }
.hudm .hdice .stick .flat { background: linear-gradient(90deg, #e8d3a8, #f6e7c6, #e8d3a8); border: 1px solid #8a6a3a; }
.hudm .hdice .stick .flat::after { content: "✕"; position: absolute; left: 0; right: 0; top: 40%; font-size: 10px; color: #7a3a1a; }
.hudm .hdice .stick .round { background: linear-gradient(90deg, #6a4420, #a8723c, #6a4420); border: 1px solid #3a2410; transform: rotateY(180deg); }
.hudm .hdice .res { height: 26px; font-size: 18px; font-weight: 800; color: #fff; text-shadow: 0 0 10px var(--glow); }
.hudm .hdice .hint { font-size: 11px; color: var(--dim); }
.hudm .hdice .row { display: flex; gap: 8px; justify-content: center; margin-top: 8px; }
.hudm .hdice .row div { padding: 4px 14px; border-radius: 6px; cursor: pointer; font-size: 11px; font-weight: 600; border: 1px solid var(--fr); background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .hdice .row div:hover { border-color: var(--ac); color: #fff; }
`;

/** Pips per face (cells 0-8 of a 3 x 3 grid). */
const PIPS: Record<number, number[]> = { 1: [4], 2: [2, 6], 3: [2, 4, 6], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
/** The cube rotation that shows face n at the front (faces: 1 front, 2 right, 3 top, 4 bottom, 5 left, 6 back). */
const SHOW: Record<number, [number, number]> = { 1: [0, 0], 2: [0, -90], 3: [-90, 0], 4: [90, 0], 5: [0, 90], 6: [0, 180] };
const FACE_AT: Record<number, string> = { 1: "translateZ(32px)", 2: "rotateY(90deg) translateZ(32px)", 3: "rotateX(90deg) translateZ(32px)", 4: "rotateX(-90deg) translateZ(32px)", 5: "rotateY(-90deg) translateZ(32px)", 6: "rotateY(180deg) translateZ(32px)" };

interface Debuff { flag: number; until: number; len: number; el: HTMLElement; t: HTMLElement }
interface Timer { key: string; label: string; type: number; ends: number; len: number; el: HTMLElement | null; gone: boolean;
  /** Cached parts of `el` (time, label, ring) and the last ring offset written. */
  ui?: { t: HTMLElement; k: HTMLElement; fg: SVGCircleElement; off: string } }

export class HudExtras {
  /** Screen effects layer: goes first in the HUD root, behind the rig and the windows. */
  readonly fx = h("div", "hfx");
  /** Goes in the right wing (hudMock.ts), mirroring the buff row. */
  readonly debuffRow = h("div", "hm-debuffs");
  readonly dura = h("div", "hdura");
  readonly status = h("div", "hstat");
  readonly compass = h("div", "hcomp");
  readonly timers = h("div", "htimers");
  readonly dice = h("div", "hdice");
  /** Called when the poison flags change (the HP bar's green tint). */
  onPoison: (flags: number) => void = () => {};
  private debuffs: Debuff[] = [];
  private fxEl: Record<string, HTMLElement> = {};
  private flags = 0;
  private nextTick = 0;
  private target: { x: number; y: number; name: string } | null = null;
  private arm = h("div", "arm");
  private compName = h("b");
  private compDist = h("span");
  private compLbl = h("div", "lbl", this.compName, this.compDist);
  private compAng = "";
  private lockFlag = 0;
  private arrived = 0;
  private list: Timer[] = [];
  private duraKey = "";
  private rolling = false;

  constructor(private readonly host: ExtrasHost) {
    injectCss("hudm-extras-css", CSS);
    for (const k of ["green", "slow", "blind", "frost", "para", "daze"]) { this.fxEl[k] = h("div", k); this.fx.append(this.fxEl[k]!); }
    this.status.hidden = true;
    this.compass.hidden = true;
    const arr = h("div", "arr");
    arr.innerHTML = '<svg viewBox="0 0 24 24"><path d="M12 2l7 14-7-4-7 4z"/></svg>';
    this.arm.append(arr);
    this.compass.append(this.arm, this.compLbl);
    this.dice.hidden = true;
    this.dura.addEventListener("click", () => this.host.openCharacter());
    this.drawDura();
  }

  /** The PoisonType flags on the character now. */
  get poisonFlags(): number { return this.flags; }
  /** Whether the character can cast / use the belt (not frozen, stunned or paralysed). */
  canAct(): boolean { return !(this.flags & LOCKS); }

  // — Durability —

  /** Wear: a hit wears a random armour piece, a kill the weapon (mock amounts). */
  wear(kind: "hit" | "kill", times = 1): void {
    const worn = EQUIPPED_NAMES.filter((n) => n && (info(n)?.dura ?? 0) > 0 && info(n)?.type !== "Amulet");
    for (let k = 0; k < times; k++) {
      const pool = kind === "kill" ? worn.filter((n) => info(n)?.type === "Weapon") : worn.filter((n) => info(n)?.type !== "Weapon");
      const n = pool[Math.floor(Math.random() * pool.length)];
      if (!n) continue;
      const before = duraFrac(n);
      setDura(n, before - (kind === "kill" ? 0.01 + Math.random() * 0.03 : 0.02 + Math.random() * 0.05));
      const after = duraFrac(n);
      if (before > 0 && after === 0) this.host.say(`Your ${n} broke! Repair it at a blacksmith.`);
      else if (before > DANGER && after <= DANGER) this.host.say(`Your ${n} is about to break.`);
    }
    this.drawDura();
  }
  /** Repairs everything worn (the lab's Repair gear; a blacksmith). */
  repairAll(): void {
    for (const n of EQUIPPED_NAMES) if (n) setDura(n, 1);
    this.host.say("All your gear has been repaired.");
    this.drawDura();
  }
  /** Redraws the strip (cheap: only when what it shows changed). */
  drawDura(): void {
    const low = EQUIPPED_NAMES.filter((n) => n && (info(n)?.dura ?? 0) > 0 && info(n)?.type !== "Amulet" && duraFrac(n) <= WARN)
      .map((n) => [n, duraFrac(n)] as [string, number]).sort((a, b) => a[1] - b[1]);
    const key = low.map(([n, f]) => n + Math.round(f * 100)).join("|");
    if (key === this.duraKey) return;
    this.duraKey = key;
    this.dura.hidden = !low.length;
    this.dura.replaceChildren();
    if (!low.length) return;
    const col = (f: number) => f === 0 ? "#8a8f99" : f <= DANGER ? "#ff5a4a" : "#f3c04a";
    const pop = h("div", "pop", h("div", "hd", "GEAR NEEDS REPAIR"));
    for (const [n, f] of low.slice(0, 8)) {
      const it = info(n)!;
      const c = h("div", "dc" + (f === 0 ? " broken" : f <= DANGER ? " danger" : ""), h("i", "", h("b")));
      c.style.setProperty("--dc", col(f));
      (c.querySelector("i b") as HTMLElement).style.width = f * 100 + "%";
      void libPicture(Lib.Items, it.img).then((p) => { if (p) c.style.backgroundImage = `url(${p.url}), radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%)`; });
      this.dura.append(c);
      const r = h("div", "r", h("span", "", n), h("em", "", h("b")), h("span", "", f === 0 ? "broken" : Math.round(f * 100) + "%"));
      r.style.setProperty("--dc", col(f));
      (r.querySelector("em b") as HTMLElement).style.width = f * 100 + "%";
      pop.append(r);
    }
    pop.append(h("div", "ft", "Broken gear gives no stats. Repair at a blacksmith · click for the Character window"));
    this.dura.append(pop);
  }

  // — Poisons —

  /** Applies a poison (PoisonType flag), or a random one. */
  poison(flag?: number): void {
    const def = flag ? POISONS.find((p) => p[0] === flag) : POISONS[Math.floor(Math.random() * POISONS.length)];
    if (!def) return;
    const [f, name, colour, path, what, secs] = def;
    const now = performance.now();
    const had = this.debuffs.find((d) => d.flag === f);
    if (had) { had.until = now + secs * 1000; had.len = secs; return; }
    const i = h("i"); i.innerHTML = svgIcon(path);
    const t = h("span");
    const el = h("div", "hm-debuff", i, t);
    el.style.setProperty("--pz", colour);
    el.title = `${name}\n${what}`;
    this.debuffs.push({ flag: f, until: now + secs * 1000, len: secs, el, t });
    this.debuffRow.prepend(el);
    this.host.say(`You are ${name.toLowerCase().replace(/ poison$/, "-poisoned").replace("delayed explosion", "marked for a delayed explosion")}.`);
    this.refreshFlags();
  }
  clearPoisons(): void { for (const d of this.debuffs) d.el.remove(); this.debuffs = []; this.refreshFlags(); }

  private refreshFlags(): void {
    const f = this.debuffs.reduce((a, d) => a | d.flag, 0);
    if (f === this.flags) return;
    this.flags = f;
    this.onPoison(f);
    const on = (k: string, v: number) => this.fxEl[k]!.classList.toggle("on", !!(f & v));
    on("green", 1); on("slow", 4); on("blind", 512); on("frost", 8); on("para", 32 | 256 | 16); on("daze", 1024);
  }

  // — Compass —

  /** S.SetCompass: point at (x, y) on this map. */
  setCompass(x: number, y: number, name: string): void { this.target = { x, y, name }; this.arrived = 0; this.compass.classList.remove("here"); }
  /** The lab's Simulate > Compass: a random NPC on this map (maps.json). */
  async compassToNpc(): Promise<void> {
    const w = this.host.world();
    if (!w) return;
    const row = (await loadMapData()).find((m) => m.f === w.map());
    const npcs = (row?.npc ?? []).filter((n) => !n[0].startsWith("_"));
    if (!npcs.length) { this.host.say("No NPCs on this map to point at."); return; }
    const c = w.cam();
    const far = npcs.filter((n) => Math.hypot(n[1] - c.x, n[2] - c.y) > 12);
    const [raw, x, y] = (far.length ? far : npcs)[Math.floor(Math.random() * (far.length || npcs.length))]!;
    const [name, role] = npcName(raw);
    this.setCompass(x, y, role ? `${name} (${role})` : name);
    this.host.say(`Compass set: ${role ? `${name}, ${role}` : name} at ${x}, ${y}.`);
  }

  // — Timers —

  /** S.SetTimer: key, seconds, type (0 plain, 1 hourglass, 2 alert); the same key updates; 0 seconds expires it. */
  setTimer(key: string, label: string, seconds: number, type = 1): void {
    const now = performance.now();
    const t = this.list.find((x) => x.key === key);
    // S.ExpireTimer / 0 seconds: the timer just goes (no "time's up").
    if (seconds <= 0) { if (t) { t.el?.remove(); this.list.splice(this.list.indexOf(t), 1); this.drawTimers(); } return; }
    if (t) { t.ends = now + seconds * 1000; t.len = seconds; t.type = type; t.label = label; t.gone = false; if (t.el) t.el.classList.remove("gone"); this.drawTimers(); return; }
    this.list.push({ key, label, type, ends: now + seconds * 1000, len: seconds, el: null, gone: false });
    this.drawTimers();
  }
  /** The lab's Simulate > Start timer: cycles a few server-style timers. */
  private demoTimer = 0;
  startDemoTimer(): void {
    const T: [string, string, number, number][] = [["dungeon", "Dungeon collapses", 25, 2], ["exp", "Double EXP", 90, 1], ["event", "Guild war starts", 45, 0], ["boss", "Boss respawns", 15, 1]];
    const [k, l, s, ty] = T[this.demoTimer++ % T.length]!;
    this.setTimer(k, l, s, ty);
  }
  private drawTimers(): void {
    const live = this.list.filter((t) => !t.gone).sort((a, b) => a.ends - b.ends);
    live.forEach((t, i) => {
      if (!t.el) {
        const ring = `<svg class="ring" viewBox="0 0 30 30"><circle class="bg" cx="15" cy="15" r="12"/><circle class="fg" cx="15" cy="15" r="12" stroke-dasharray="75.4"/></svg>`;
        const glyph = t.type === 2 ? '<svg viewBox="0 0 24 24"><path d="M12 3l10 18H2zM12 10v5M12 18v.01"/></svg>' : t.type === 1 ? '<svg viewBox="0 0 24 24"><path d="M6 2h12M6 22h12M7 2c0 6 10 6 10 10S7 16 7 22M17 2c0 6-10 6-10 10s10 4 10 10"/></svg>' : "";
        const ic = h("div", "ic"); ic.innerHTML = ring + glyph;
        const tx = h("div", "t"), k = h("div", "k");
        t.el = h("div", "htm", ic, h("div", "", tx, k));
        t.ui = { t: tx, k, fg: ic.querySelector(".fg") as SVGCircleElement, off: "" };
        t.el.title = `${t.label} (timer "${t.key}")`;
      }
      if (t.ui) setText(t.ui.k, t.label); // setTimer can relabel a timer
      t.el.classList.toggle("first", i === 0);
      this.timers.append(t.el); // keeps them in soonest-first order
    });
  }

  // — Dice —

  /** S.Roll: type 0 die / 1 yut, the result (die 1-6, yut 0-4 flat sides), auto = rolls by itself. */
  roll(type: 0 | 1, auto = false, result = type ? Math.floor(Math.random() * 5) : 1 + Math.floor(Math.random() * 6)): void {
    this.rolling = false;
    const stage = h("div", "stage"), res = h("div", "res"), hint = h("div", "hint", auto ? "Rolling…" : "Click to roll");
    const close = h("div", "", "Close");
    close.addEventListener("click", () => { this.dice.hidden = true; });
    let go: () => void;
    if (!type) {
      const cube = h("div", "cube");
      for (let n = 1; n <= 6; n++) {
        const f = h("div", "face" + (n === 1 ? " one" : ""));
        for (let c = 0; c < 9; c++) f.append(h("i", PIPS[n]!.includes(c) ? "on" : ""));
        f.style.transform = FACE_AT[n]!;
        cube.append(f);
      }
      stage.append(cube);
      go = () => {
        const [x, y] = SHOW[result]!;
        cube.animate([{ transform: "rotateX(-25deg) rotateY(35deg) translateY(0)" }, { transform: `rotateX(${400 + x / 2}deg) rotateY(${300 + y / 2}deg) translateY(-30px)`, offset: 0.45 },
          { transform: `rotateX(${720 + x}deg) rotateY(${720 + y}deg) translateY(0)` }], { duration: 1400, easing: "cubic-bezier(.2,.7,.25,1)", fill: "forwards" })
          .finished.then(() => this.rolled(res, hint, `You rolled ${result}`, `rolled a ${result}`)).catch(() => {});
      };
    } else {
      const wrap = h("div", "sticks"), sticks: HTMLElement[] = [];
      for (let k = 0; k < 4; k++) { const s = h("div", "stick", h("b", "flat"), h("b", "round")); sticks.push(s); wrap.append(s); }
      stage.append(wrap);
      // result = number of flat sides up (0 = Mo); which sticks show flat is random
      const flat = new Set<number>(); while (flat.size < result) flat.add(Math.floor(Math.random() * 4));
      go = () => {
        const done = sticks.map((s, k) => s.animate([{ transform: "translateY(0) rotateY(0) rotateZ(0)" }, { transform: `translateY(-60px) rotateY(${540 + k * 90}deg) rotateZ(${(k - 1.5) * 20}deg)`, offset: 0.5 },
          { transform: `translateY(0) rotateY(${flat.has(k) ? 720 : 900}deg) rotateZ(${(k - 1.5) * 6}deg)` }], { duration: 1100 + k * 120, easing: "cubic-bezier(.3,.6,.3,1)", fill: "forwards" }).finished);
        void Promise.all(done).then(() => this.rolled(res, hint, `${YUT[result]}! (${result || "no"} flat side${result === 1 ? "" : "s"})`, `threw ${YUT[result]}`)).catch(() => {});
      };
    }
    stage.addEventListener("click", () => { if (this.rolling) return; this.rolling = true; hint.textContent = "Rolling…"; go(); });
    this.dice.replaceChildren(h("h3", "", type ? "YUT NORI" : "ROLL THE DIE"), h("div", "sub", type ? "Throw the four sticks" : "The NPC waits for your roll"), stage, res, hint, h("div", "row", close));
    this.dice.hidden = false;
    if (auto) { this.rolling = true; window.setTimeout(go, 300); }
  }
  private rolled(res: HTMLElement, hint: HTMLElement, text: string, chat: string): void {
    res.textContent = text;
    hint.textContent = "The NPC goes on with your result";
    this.host.say(`You ${chat}.`);
  }

  // — Per frame —

  tick(now: number): void {
    // Poisons: timers, ticks, the explosion.
    if (this.debuffs.length) {
      const ticking = now >= this.nextTick;
      if (ticking) this.nextTick = now + 1000;
      for (let k = this.debuffs.length - 1; k >= 0; k--) { // backwards: expired ones are spliced out as we go
        const d = this.debuffs[k]!;
        const left = (d.until - now) / 1000;
        if (left <= 0) {
          d.el.remove(); this.debuffs.splice(k, 1);
          if (d.flag === 64) { this.host.bigHit(); this.host.say("The delayed explosion went off!"); }
          continue;
        }
        setText(d.t, left >= 1 ? String(Math.ceil(left)) : left.toFixed(1));
        d.el.classList.toggle("end", left < 3);
        if (ticking && d.flag === 1) this.host.hp(-1.2);
        if (ticking && d.flag === 128) this.host.hp(-0.8);
      }
      this.refreshFlags();
      let lock: Debuff | undefined;
      for (const d of this.debuffs) if (d.flag & LOCKS && (!lock || d.until > lock.until)) lock = d;
      this.status.hidden = !lock;
      if (lock) {
        const def = POISONS.find((p) => p[0] === lock.flag)!;
        setText(this.status, `${def[1].toUpperCase()} · ${Math.max(0, (lock.until - now) / 1000).toFixed(1)}s`);
        if (lock.flag !== this.lockFlag) { this.lockFlag = lock.flag; this.status.style.setProperty("--pz", def[2]); }
      }
    } else this.status.hidden = true;

    // Compass.
    const w = this.host.world();
    if (this.target && w) {
      const c = w.cam(), dx = this.target.x - c.x, dy = this.target.y - c.y, d = Math.max(Math.abs(dx), Math.abs(dy));
      this.compass.hidden = false;
      if (d <= 1) {
        if (!this.arrived) { this.arrived = now; this.compass.classList.add("here"); setText(this.compName, this.target.name); setText(this.compDist, "You have arrived"); }
        if (now - this.arrived > 2500) { this.target = null; this.compass.hidden = true; }
      } else {
        const ang = Math.atan2(dx * 48, -dy * 32) * 180 / Math.PI; // cells are 48 x 32 px on screen
        const rot = `rotate(${ang.toFixed(1)}deg)`;
        if (rot !== this.compAng) { this.compAng = rot; this.arm.style.transform = rot; }
        const dir = DIRS[Math.round(((ang + 360) % 360) / 45) % 8]!;
        setText(this.compName, this.target.name);
        setText(this.compDist, `${d} steps ${dir}`);
      }
    } else if (!this.target) this.compass.hidden = true;

    // Timers.
    if (this.list.length) {
      let changed = false;
      for (const t of this.list) {
        if (t.gone || !t.el || !t.ui) continue;
        const left = Math.max(0, Math.ceil((t.ends - now) / 1000));
        setText(t.ui.t, fmtT(left));
        // The ring is ~75 px round: 0.1 px steps look the same as every frame and skip most writes on long timers.
        const off = (75.4 * (1 - Math.max(0, t.ends - now) / (t.len * 1000))).toFixed(1);
        if (off !== t.ui.off) { t.ui.off = off; t.ui.fg.style.strokeDashoffset = off; }
        t.el.classList.toggle("hot", left <= 10 && left > 0);
        if (t.ends <= now) {
          t.gone = true; changed = true;
          const el = t.el; el.classList.add("gone"); window.setTimeout(() => { if (t.gone) { el.remove(); t.el = null; const at = this.list.indexOf(t); if (at >= 0) this.list.splice(at, 1); } }, 500);
          this.host.say(`${t.label}: time's up.`);
        }
      }
      if (changed) { this.list = this.list.filter((t) => !t.gone || t.el); this.drawTimers(); }
    }
  }

  /** The lab's reset: no poisons, timers, compass or dice. */
  reset(): void {
    this.clearPoisons();
    for (const t of this.list) t.el?.remove();
    this.list = [];
    this.target = null; this.compass.hidden = true;
    this.dice.hidden = true;
  }
}
