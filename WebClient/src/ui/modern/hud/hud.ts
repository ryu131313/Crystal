// Ported from play/hudMock.ts (the bottom HUD: build / buildCore / buildMenu / buildBuffs / render, ~148-336 CSS and
// ~757-1247) and play/hudExtras.ts (the durability strip and the debuff row, ~58-77 and 213-260): the new UI's bottom
// HUD as a PURE view of the real game state. No simulation, demo events or lab knobs.
//
// Layout (the user's sketch, 2026-10-15), inside UiRuntime.layers.rig (centred at the bottom, HUD scale k): two wings
// around a centre diamond. Each wing: three stat plates (statPlates.ts), the HP (left) / MP (right) bar, a belt row
// (belt.ts: player cells 1-8, hero cells 9 / 0) and a skill row (skillRow.ts) whose outer slot is the attack mode (left)
// or the pet mode (right). The diamond shows the level and the EXP % with the EXP ring round its edge, a Log out corner
// (left) and a game menu corner (right); the menu strip sits above it, the buff row above the left wing, the poison row
// and the durability strip above the right wing.
//
// State: PlayerState (hp / mp / level / experience / stats / modes / buffs; game.ts locals behind getters, no events),
// so the frame tick compares and writes only what changed (setText / setStyle; bars move by transform, Performance.md
// T3-7); the plates, buffs and poisons update ~4x a second; the belt cells redraw from the models (itemCells); the
// skill row from Magic events plus the per-frame cooldown sweep; the menu strip's Mail badge from MailBox.changed; the
// durability strip from Inventory changes. Containers are pointer-events: none; only the controls take clicks.
//
// Not ported (hidden): the mock's big-hit screen vignette (no full-screen HUD layer below the windows), the HPOnly MP
// orb rule (Warrior < 26 keeps the MP bar), Exit game (a page cannot close itself; the right corner opens the menu).

import { BuffTypeName, PoisonType, PoisonTypeName } from "../../../protocol/enums.generated.ts";
import { Stat } from "../../../protocol/enums.generated.ts";
import { ItemType } from "../../../protocol/enums.generated.ts";
import { buffIcon, buffText, poisonColour, type UserBuff } from "../../../game/buffs.ts";
import { KeybindOptions, keyLabel } from "../../../game/keybinds.ts";
import type { InputRouter } from "../../../game/input.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { asButton, h, injectCss, later, restartAnim, setStyle, setText } from "../core/dom.ts";
import { fmtShort, pretty } from "../core/format.ts";
import { svg, svgNode } from "../core/icons.ts";
import type { UiRuntime } from "../core/runtime.ts";
import { Belt } from "./belt.ts";
import { GameMenu, MENU_WINDOWS, WINDOW_KEYS, badgeText, unreadMail } from "./gameMenu.ts";
import { ModeSlot, SkillRow, type SkillView } from "./skillRow.ts";
import { Popover, StatPlates } from "./statPlates.ts";

/** What the HUD needs from the integrator (modernUi.ts). */
export interface HudHost {
  readonly rt: UiRuntime;
  readonly ctx: GameUiContext;
  /** The stat plate picks (the UiPlates setting through parsePlates) and saving them (formatPlates -> setSetting). */
  plates(): readonly string[];
  savePlates(keys: readonly string[]): void;
  /** "Switch to classic interface" (game menu): save UiStyle Classic and offer "Log out now to switch". */
  switchToClassic(): void;
  /** Opens / closes / toggles (show undefined) a window by registry key (WINDOW_KEYS). Default: rt.toggleWindow. False
   *  when nothing is registered for the key yet (later phases). */
  command?(key: string, show?: boolean): boolean;
}

const CSS = `
.hudm > .rig { font-variant-numeric: tabular-nums; }
.hudm .hm-plate { position: absolute; left: -46px; right: -46px; top: -16px; bottom: -3px; pointer-events: none;
  background: radial-gradient(ellipse 60% 120% at 50% 100%, var(--pb), color-mix(in srgb, var(--pb) 60%, transparent) 55%, transparent 80%); }
.hudm .hm-plate::before { content: ""; position: absolute; left: 12%; right: 12%; top: 6px; height: 1px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 45%, transparent), transparent); }
.hudm .hm-wing { position: relative; width: 240px; display: flex; flex-direction: column; gap: 3px; }
.hudm .hm-hrow { position: relative; display: flex; gap: 4px; }
.hudm .hm-ic { width: 13px; height: 13px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }

/* HP / MP bars: angled outer cap, the fill slides by transform (no width layout), a dark trail follows a drop. */
.hudm .hm-bar { position: relative; height: 28px; background: linear-gradient(180deg, var(--frl), var(--frd)); pointer-events: auto; }
.hudm .hm-bar.hm-hp { clip-path: polygon(12px 0, 100% 0, 100% 100%, 12px 100%, 0 50%); }
.hudm .hm-bar.hm-mp { clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%); }
.hudm .hm-bar .in { position: absolute; inset: 1px; background: #04060a; box-shadow: inset 0 2px 4px #000; overflow: hidden; clip-path: inherit; }
.hudm .hm-bar .fill, .hudm .hm-bar .lag { position: absolute; inset: 0; will-change: transform; }
.hudm .hm-bar .fill { transition: transform .25s ease-out; }
.hudm .hm-bar .lag { transition: transform .8s ease-in .45s; }
.hudm .hm-hp .fill { background: linear-gradient(90deg, #4a080e, #a01a24 55%, #e0414a); }
.hudm .hm-hp .lag { background: linear-gradient(90deg, #2a0306, #5c0a10); }
.hudm .hm-mp .fill { background: linear-gradient(90deg, #3a8ef0, #1a50b8 45%, #0c2262); }
.hudm .hm-mp .lag { background: linear-gradient(90deg, #0a1a40, #06102a); }
.hudm .hm-hp.psn .fill { background: linear-gradient(90deg, #0d3a18, #1f8a3c 55%, #4fe07a); }
/* Low HP: a white overlay pulses by opacity (Performance.md T3-8). */
.hudm .hm-bar .in::after { content: ""; position: absolute; inset: 0; opacity: 0; pointer-events: none; background: rgba(255, 120, 120, .35); }
.hudm .hm-hp.low .in::after { animation: hudm-halo 1s ease-in-out infinite; }
.hudm .hm-bar.struck { animation: hudm-shake .3s linear; }
@keyframes hudm-shake { 20% { transform: translateX(-3px); } 40% { transform: translateX(3px); } 60% { transform: translateX(-2px); } 80% { transform: translateX(1px); } }
.hudm .hm-bar .txt { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 600;
  text-shadow: 0 0 2px #000, 0 1px 1px #000; letter-spacing: .03em; }
.hudm .hm-bar .tag { position: absolute; top: 0; bottom: 0; display: flex; align-items: center; padding: 0 14px; font-size: 10px; font-weight: 700; color: rgba(255,255,255,.75); text-shadow: 0 1px 1px #000; }
.hudm .hm-hp .tag { left: 0; } .hudm .hm-mp .tag { right: 0; }

/* Centre diamond (SVG colours are styles so the theme variables reach them) */
.hudm .hm-core { position: relative; width: 120px; height: 130px; flex: none; }
.hudm .hm-core svg.dia { position: absolute; inset: 0; overflow: visible; pointer-events: none; }
.hudm .hm-core .hit { position: absolute; left: 20px; right: 20px; top: 20px; bottom: 20px; pointer-events: auto; transform: rotate(45deg) scale(.85); }
.hudm .hm-core .lv { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; pointer-events: none; }
.hudm .hm-core .lv small { font-size: 9px; letter-spacing: .2em; color: var(--dim); }
.hudm .hm-core .lv b { font: 700 30px/1 Georgia, "Times New Roman", serif; color: #f2f6ff; text-shadow: 0 0 var(--glr) var(--glow), 0 2px 2px #000; }
.hudm .hm-core .lv span { margin-top: 3px; font-size: 11px; font-weight: 600; color: var(--acd); }
.hudm .hm-core.lvup .lv b { animation: hudm-pop .9s ease-out; }
@keyframes hudm-pop { 0% { transform: scale(1); } 25% { transform: scale(1.5); color: #fff; } 100% { transform: scale(1); } }
.hudm .hm-core .burst { transform-box: fill-box; transform-origin: center; opacity: 0; }
.hudm .hm-core.lvup .burst { animation: hudm-burst 1s ease-out; }
.hudm .hm-core.lvup .burst.b2 { animation-delay: .15s; }
@keyframes hudm-burst { 0% { opacity: 1; transform: scale(1); } 100% { opacity: 0; transform: scale(1.9); } }
.hudm .hm-float { position: absolute; left: 50%; top: -6px; transform: translateX(-50%); white-space: nowrap; font-weight: 700; pointer-events: none;
  text-shadow: 0 0 4px #000, 0 1px 2px #000; animation: hudm-rise 1.4s ease-out forwards; }
.hudm .hm-float.big { font: 700 18px Georgia, serif; color: #fff; text-shadow: 0 0 10px var(--ac), 0 2px 2px #000; }
.hudm .hm-float.exp { font-size: 12px; color: var(--acd); }
@keyframes hudm-rise { 0% { opacity: 0; translate: 0 8px; } 15% { opacity: 1; } 100% { opacity: 0; translate: 0 -34px; } }
.hudm .hm-core .tri { cursor: pointer; }
.hudm .hm-core .tri polygon { pointer-events: auto; stroke-width: 1.5; stroke-linejoin: round; }
.hudm .hm-core .tri path { pointer-events: none; fill: none; stroke: var(--acd); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hm-core .tri:hover polygon, .hudm .hm-core .tri:focus-visible polygon { stroke: var(--ac); }
.hudm .hm-core .tri:hover path, .hudm .hm-core .tri:focus-visible path { stroke: #fff; }
.hudm .hm-core .tri.flash polygon { animation: hudm-triflash .35s ease-out; }
@keyframes hudm-triflash { from { fill: var(--ac); } }

/* Menu strip (above the diamond) */
.hudm .hm-menu { position: absolute; left: 50%; bottom: calc(100% + 14px); transform: translateX(-50%); display: flex; gap: 4px; }
.hudm .hm-mbtn { position: relative; width: 26px; height: 26px; display: flex; align-items: center; justify-content: center; pointer-events: auto; cursor: pointer;
  color: var(--acd); background: linear-gradient(180deg, var(--pa), var(--pb));
  border: 1px solid var(--fr); border-radius: calc(var(--rad) * .7); box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 25%, transparent); transform: rotate(45deg) scale(.82); }
.hudm .hm-mbtn .hm-ic { transform: rotate(-45deg); width: 15px; height: 15px; }
.hudm .hm-mbtn .badge { position: absolute; right: -7px; top: -7px; min-width: 14px; height: 14px; padding: 0 3px; transform: rotate(-45deg);
  border-radius: 7px; font: 700 9px/14px "Segoe UI", system-ui, sans-serif; text-align: center; color: #fff; background: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 6px var(--glow); }
.hudm .hm-mbtn:hover, .hudm .hm-mbtn:focus-visible { color: #fff; border-color: var(--ac); }
.hudm .hm-mbtn.gm { margin-left: 6px; }
.hudm .hm-mbtn.flash { animation: hudm-mflash .35s ease-out; }
@keyframes hudm-mflash { from { background: var(--ac); } }

/* Buff row (above the left wing) and poison row (above the right wing) */
.hudm .hm-buffs { position: absolute; left: 0; bottom: calc(100% + 8px); display: flex; flex-wrap: wrap-reverse; gap: 4px; max-width: 240px; }
.hudm .hm-debuffs { position: absolute; right: 0; bottom: calc(100% + 8px); display: flex; flex-direction: row-reverse; gap: 4px; }
.hudm .hm-buff { position: relative; width: 26px; pointer-events: auto; text-align: center; }
.hudm .hm-buff i { display: block; width: 26px; height: 26px; background: center / contain no-repeat; image-rendering: pixelated; border-radius: calc(var(--rad) * .7);
  border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hm-buff span { display: block; margin-top: 1px; font-size: 9px; font-weight: 600; color: var(--tx); text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .hm-buff.ending i { animation: hudm-blink .6s steps(2) infinite; }
@keyframes hudm-blink { 50% { opacity: .35; } }
.hudm .hm-buff.psn i { display: flex; align-items: center; justify-content: center; border-color: var(--pz);
  background: radial-gradient(circle at 50% 35%, color-mix(in srgb, var(--pc) 70%, var(--pb)), #05070b); }
.hudm .hm-buff.psn svg { width: 17px; height: 17px; color: color-mix(in srgb, var(--pz) 70%, #fff); }

/* Durability strip (above the right wing): worn gear at or under half durability */
.hudm .hdura { position: absolute; right: 0; bottom: calc(100% + 50px); display: flex; gap: 4px; justify-content: flex-end; pointer-events: auto; cursor: pointer; }
.hudm .hdura .dc { position: relative; width: 28px; height: 28px; border-radius: 7px; background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%);
  border: 1px solid var(--dc); box-shadow: 0 0 0 1px #000; }
.hudm .hdura .dc u { position: absolute; inset: 0; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .hdura .dc i { position: absolute; left: 3px; right: 3px; bottom: 2px; height: 3px; border-radius: 2px; background: rgba(0,0,0,.7); overflow: hidden; }
.hudm .hdura .dc i b { display: block; height: 100%; background: var(--dc); transform-origin: 0 0; }
.hudm .hdura .dc.broken u { filter: grayscale(1) brightness(.6); }
.hudm .hdura .dc.broken::after { content: ""; position: absolute; left: 3px; right: 3px; top: 50%; height: 2px; background: var(--bad); transform: rotate(-35deg); box-shadow: 0 0 3px #000; }
.hudm .hdura .pop { position: absolute; right: 0; bottom: calc(100% + 6px); width: 230px; padding: 8px 10px; border-radius: var(--rad); font-size: 11px; color: var(--tx); display: none;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 8px 18px #000; cursor: default; }
.hudm .hdura:hover .pop { display: block; }
.hudm .hdura .pop .hd { font-weight: 700; color: var(--ac); letter-spacing: .1em; font-size: 10px; margin-bottom: 6px; }
.hudm .hdura .pop .r { display: grid; grid-template-columns: 1fr 70px 44px; align-items: center; gap: 6px; height: 18px; }
.hudm .hdura .pop .r span:first-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .hdura .pop .r span:last-child { text-align: right; color: var(--dc); }
.hudm .hdura .pop .r em { height: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .hdura .pop .r em b { display: block; height: 100%; background: var(--dc); transform-origin: 0 0; }
.hudm .hdura .pop .ft { margin-top: 6px; color: var(--dim); font-size: 10px; }
`;

/** Diamond geometry (viewBox 0 0 120 130, the wings' height): the EXP ring runs on D from the top vertex, clockwise. */
const D = "M60 4L116 65L60 126L4 65Z";
const D_OUT = "M60 -2L122 65L60 132L-2 65Z";
const D_IN = "M60 13L108 65L60 117L12 65Z";
/** D's corners from the top, clockwise (the ring's head is placed on them by arithmetic: no getPointAtLength). */
const CORNERS: readonly (readonly [number, number])[] = [[60, 4], [116, 65], [60, 126], [4, 65], [60, 4]];
/** Quarter marks: [percent, x, y] at the right, bottom and left corners. */
const VERTS: readonly (readonly [number, number, number])[] = [[25, 116, 65], [50, 60, 126], [75, 4, 65]];
/** The desktop's durability warnings (DuraStatusDialog): Warning = MaxDura / 2, Danger = MaxDura / 5. */
const DURA_WARN = 0.5, DURA_DANGER = 0.2;

interface BuffEl { el: HTMLElement; t: HTMLElement; ending: boolean }

export class Hud {
  readonly rt: UiRuntime;
  readonly ctx: GameUiContext;
  readonly gameMenu: GameMenu;
  readonly plates: StatPlates;
  readonly belt: Belt;
  readonly skills: SkillRow;
  private readonly pop: Popover;
  private readonly modes: ModeSlot[] = [];
  private readonly core = h("div", "hm-core");
  private readonly menu = h("div", "hm-menu");
  /** The buff / poison rows (the touch layout moves them under its HP / MP box: mobile/mobileUi.ts). */
  readonly buffRow = h("div", "hm-buffs");
  readonly poisonRow = h("div", "hm-debuffs");
  private readonly dura = h("div", "hdura");
  private hpBar!: HTMLElement; private hpFill!: HTMLElement; private hpLag!: HTMLElement; private hpTxt!: HTMLElement;
  private mpBar!: HTMLElement; private mpFill!: HTMLElement; private mpLag!: HTMLElement; private mpTxt!: HTMLElement;
  private expPath!: SVGPathElement; private expHead!: SVGCircleElement; private ticks: SVGPathElement[] = [];
  private lvNum = h("b"); private lvPct = h("span");
  private menuBtns: { key: string; el: HTMLElement; badge?: HTMLElement }[] = [];
  private menuKeys = -1;
  // What is drawn (the frame compares against these and writes only on a change).
  private shown = { hp: -1, hpMax: -1, mp: -1, mpMax: -1, psn: false, low: false, level: -1, lit: -1 };
  private shownExp: bigint = -1n;
  private shownMaxExp: bigint = -1n;
  private buffKey = "";
  private buffEls: BuffEl[] = [];
  private buffList: readonly UserBuff[] = [];
  private poisonKey = "";
  private poisonEls: { t: HTMLElement; type: number }[] = [];
  private duraDirty = true;
  private duraKey = "";
  private ctrlHeld = false;
  private shiftHeld = false;
  private disposed = false;

  constructor(private readonly host: HudHost) {
    this.rt = host.rt;
    this.ctx = host.ctx;
    injectCss("hud-rig", CSS);
    const rt = this.rt, ctx = this.ctx, sig = rt.signal;
    this.pop = new Popover(rt.layers.rig, sig);
    this.plates = new StatPlates(ctx, host, this.pop);
    this.belt = new Belt(rt);
    this.skills = new SkillRow(ctx, rt);
    this.gameMenu = new GameMenu({ rt, ctx, started: ctx.now(), openWindow: (k) => this.command(k, true), switchToClassic: () => host.switchToClassic() });

    const wing = (side: 0 | 1) => {
      const w = h("div", "hm-wing");
      const fill = h("div", "fill"), lag = h("div", "lag"), txt = h("div", "txt"), tag = h("div", "tag", side ? "MP" : "HP");
      const bar = h("div", "hm-bar " + (side ? "hm-mp" : "hm-hp"), h("div", "in", lag, fill), txt, tag);
      if (side) { this.mpFill = fill; this.mpLag = lag; this.mpTxt = txt; this.mpBar = bar; } else { this.hpFill = fill; this.hpLag = lag; this.hpTxt = txt; this.hpBar = bar; }
      const skills = side ? this.skills.right : this.skills.left;
      const mode = new ModeSlot(side ? "pet" : "attack", ctx, this.pop, skills);
      this.modes.push(mode);
      if (side) skills.append(mode.el); else skills.prepend(mode.el);
      w.append(side ? this.plates.right : this.plates.left, bar, side ? this.belt.right : this.belt.left, skills);
      if (side) w.append(this.poisonRow, this.dura); else w.append(this.buffRow);
      return w;
    };
    this.buildCore();
    this.buildMenu();
    this.dura.hidden = true;
    asButton(this.dura, "Gear durability: open the Character window");
    this.dura.addEventListener("click", (e) => { if (!(e.target as Element).closest(".pop")) this.command(WINDOW_KEYS.character, true); });
    // Buff / poison titles are built on hover (fresh time left), not rewritten per tick.
    this.buffRow.addEventListener("pointerover", (e) => this.buffTitle(e.target));
    this.poisonRow.addEventListener("pointerover", (e) => this.poisonTitle(e.target));
    rt.layers.rig.replaceChildren(h("div", "hm-plate"), wing(0), this.core, wing(1));

    // Model events
    const offs: (() => void)[] = [
      ctx.models.inventory.subscribe(() => { this.duraDirty = true; }),
      ctx.models.mail.changed.on(() => this.mailBadge()),
      ctx.models.poisons.changed.on(() => { this.poisonKey = "\u0000"; }),
      rt.ticker.subscribe((now) => this.frame(now)),
    ];
    sig.addEventListener("abort", () => { for (const off of offs) off(); this.dispose(); }, { once: true });
    const slow = rt.ticker.every(250, (now) => this.slow(now));
    slow.on();
    const menuCheck = rt.ticker.every(1000, () => this.menuRegistry());
    menuCheck.on();
    this.mailBadge();
  }

  // — Input (the integrator calls bindInput once from GameUi.bindInput) —

  /** Registers the HUD on the router: held Ctrl / Shift (skill row view) as watchers; the game menu on the "menu" layer;
   *  Esc for an open HUD list on the "menu" layer; Esc with nothing open (game menu) on the "hotkeys" layer. */
  bindInput(input: InputRouter): void {
    const sig = this.rt.signal, offs: (() => void)[] = [];
    offs.push(input.watch((e) => this.held(e, true)));
    offs.push(input.onKeyUp((e) => this.held(e, false)));
    offs.push(input.onBlur(() => { this.ctrlHeld = this.shiftHeld = false; this.updateView(); }));
    offs.push(input.register("menu", (e) => this.handleKey(e)));
    offs.push(input.register("menu", (e) => this.gameMenu.handleKey(e)));
    offs.push(input.register("hotkeys", (e) => this.gameMenu.escape(e)));
    sig.addEventListener("abort", () => { for (const off of offs) off(); }, { once: true });
  }

  /** Esc closes an open stat / mode list. */
  handleKey(e: KeyboardEvent): boolean {
    if (e.key === "Escape" && this.pop.open) { e.preventDefault(); this.pop.close(); return true; }
    return false;
  }

  /** Something the durability strip draws may have changed outside Inventory (S.NewItemInfo: UiEvent "itemInfo"). */
  refresh(): void { this.duraDirty = true; }

  /** The UiPlates setting changed elsewhere (Options). */
  reloadPlates(): void { this.plates.reload(); }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.pop.close();
    this.gameMenu.dispose();
    this.belt.dispose();
    this.rt.layers.rig.replaceChildren();
  }

  private command(key: string, show?: boolean): boolean {
    return this.host.command ? this.host.command(key, show) : this.rt.toggleWindow(key, show);
  }

  /** Held Ctrl -> bar 2, held Shift -> the hero's skills (synthetic replays from slot clicks are ignored). */
  private held(e: KeyboardEvent, down: boolean): void {
    if (!e.isTrusted) return;
    if (e.key === "Control") this.ctrlHeld = down;
    else if (e.key === "Shift") this.shiftHeld = down;
    else { this.ctrlHeld = e.ctrlKey; this.shiftHeld = e.shiftKey; } // catch a release missed while typing
    this.updateView();
  }

  private updateView(): void {
    const v: SkillView = this.shiftHeld ? 2 : this.ctrlHeld ? 1 : 0;
    this.skills.setView(v);
  }

  // — Build —

  private buildCore(): void {
    const core = this.core;
    const s = svgNode("svg", { class: "dia", viewBox: "0 0 120 130", "aria-hidden": "true" });
    const defs = svgNode("defs");
    const fillGrad = svgNode("linearGradient", { id: "hudr-core", x1: 0, y1: 0, x2: 0, y2: 1 });
    fillGrad.append(svgNode("stop", { offset: 0, style: "stop-color: var(--pc)" }), svgNode("stop", { offset: 1, style: "stop-color: var(--pb)" }));
    const metal = svgNode("linearGradient", { id: "hudr-metal", x1: 0, y1: 0, x2: 0, y2: 1 });
    metal.append(svgNode("stop", { offset: 0, style: "stop-color: var(--frl)" }), svgNode("stop", { offset: 0.45, style: "stop-color: var(--fr)" }),
      svgNode("stop", { offset: 0.55, style: "stop-color: var(--frd)" }), svgNode("stop", { offset: 1, style: "stop-color: var(--frl)" }));
    const glow = svgNode("filter", { id: "hudr-glow", x: "-50%", y: "-50%", width: "200%", height: "200%" });
    glow.append(svgNode("feGaussianBlur", { stdDeviation: 2.5, result: "b" }));
    const merge = svgNode("feMerge");
    merge.append(svgNode("feMergeNode", { in: "b" }), svgNode("feMergeNode", { in: "SourceGraphic" }));
    glow.append(merge);
    defs.append(fillGrad, metal, glow);

    const fin = (pts: string) => svgNode("polygon", { points: pts, fill: "url(#hudr-metal)", stroke: "#000", "stroke-width": 1 });
    // Corner buttons under the diamond: Log out (left) and the game menu (right).
    const tri = (points: string, iconAt: string, d: string, title: string, run: () => void) => {
      const g = svgNode("g", { class: "tri", role: "button", tabindex: 0, "aria-label": title });
      const t = svgNode("title");
      t.textContent = title;
      g.append(t, svgNode("polygon", { points, fill: "url(#hudr-core)", stroke: "url(#hudr-metal)" }), svgNode("path", { d, transform: `${iconAt} scale(.82)` }));
      const go = () => { restartAnim(g, "flash"); run(); };
      g.addEventListener("click", go);
      g.addEventListener("keydown", (e) => { const k = (e as KeyboardEvent).key; if (k === "Enter" || k === " ") { e.preventDefault(); e.stopPropagation(); go(); } });
      return g;
    };
    const logoutKey = keyLabel(KeybindOptions.Logout);
    const parts: SVGElement[] = [
      tri("0,74 0,130 52,130", "translate(3 106.3)", "M9 14L4 9l5-5M4 9h10a6 6 0 010 12h-3", `Log out${logoutKey ? ` (${logoutKey})` : ""}`, () => this.ctx.actions.logOut(false)),
      tri("120,74 120,130 68,130", "translate(97.3 106.3)", "M4 6h16M4 12h16M4 18h16", "Game menu (Esc)", () => this.gameMenu.toggle()),
      fin("12,57 -6,65 12,73"), fin("108,57 126,65 108,73"),
      svgNode("path", { d: D_OUT, fill: "none", stroke: "url(#hudr-metal)", "stroke-width": 2.5 }),
      svgNode("path", { d: D, fill: "url(#hudr-core)", stroke: "#000", "stroke-width": 10, "stroke-linejoin": "miter" }),
      svgNode("path", { d: D, fill: "none", style: "stroke: color-mix(in srgb, var(--fr) 70%, #000)", "stroke-width": 6 }),
      svgNode("path", { d: D, fill: "none", stroke: "rgba(0,0,0,.6)", "stroke-width": 2 }),
    ];
    this.expPath = svgNode("path", { d: D, fill: "none", style: "stroke: var(--ac)", "stroke-width": 6, pathLength: 100, "stroke-dasharray": "0 100", filter: "url(#hudr-glow)" });
    this.expHead = svgNode("circle", { r: 3.2, style: "fill: color-mix(in srgb, var(--ac) 30%, #fff)", filter: "url(#hudr-glow)" });
    parts.push(this.expPath, this.expHead);
    this.ticks = VERTS.map(([, x, y]) => svgNode("path", { d: `M${x} ${y - 4}L${x + 4} ${y}L${x} ${y + 4}L${x - 4} ${y}Z`, style: "fill: var(--fr)", stroke: "#000", "stroke-width": 1 }));
    parts.push(...this.ticks);
    parts.push(svgNode("path", { d: D_IN, fill: "none", style: "stroke: color-mix(in srgb, var(--frl) 40%, transparent)", "stroke-width": 1 }));
    parts.push(svgNode("path", { d: "M60 -6L65 2L60 9L55 2Z", fill: "url(#hudr-metal)", stroke: "#000", "stroke-width": 1 }));
    parts.push(svgNode("path", { class: "burst", d: D, fill: "none", style: "stroke: color-mix(in srgb, var(--ac) 50%, #fff)", "stroke-width": 3 }));
    parts.push(svgNode("path", { class: "burst b2", d: D, fill: "none", style: "stroke: var(--ac)", "stroke-width": 2 }));
    s.append(defs, ...parts);
    const hit = h("div", "hit");
    hit.title = "Experience: the ring fills clockwise from the top";
    core.append(s, hit, h("div", "lv", h("small", "", "LEVEL"), this.lvNum, this.lvPct), this.menu);
  }

  private buildMenu(): void {
    this.menu.replaceChildren();
    this.menuBtns = [];
    for (const w of MENU_WINDOWS) {
      if (!w.strip) continue;
      const b = asButton(h("div", "hm-mbtn", svg(w.icon, "hm-ic")), w.name);
      const k = w.bind !== undefined ? keyLabel(w.bind) : "";
      b.title = `${w.name}${k ? ` (${k})` : ""}`;
      b.addEventListener("click", () => { restartAnim(b, "flash"); this.command(w.key); });
      const entry: { key: string; el: HTMLElement; badge?: HTMLElement } = { key: w.key, el: b };
      if (w.key === WINDOW_KEYS.mail) { entry.badge = h("span", "badge"); entry.badge.hidden = true; b.append(entry.badge); }
      b.hidden = !this.rt.hasWindow(w.key);
      this.menuBtns.push(entry);
      this.menu.append(b);
    }
    const gm = asButton(h("div", "hm-mbtn gm", svg("menu", "hm-ic")), "Game menu");
    gm.title = "Game menu (Esc)";
    gm.addEventListener("click", () => { restartAnim(gm, "flash"); this.gameMenu.toggle(); });
    this.menu.append(gm);
    this.menuKeys = this.rt.windowKeys.length;
  }

  /** Later phases register windows: show their strip buttons (checked once a second; one length compare). */
  private menuRegistry(): void {
    const n = this.rt.windowKeys.length;
    if (n === this.menuKeys) return;
    this.menuKeys = n;
    for (const b of this.menuBtns) b.el.hidden = !this.rt.hasWindow(b.key);
  }

  private mailBadge(): void {
    const b = this.menuBtns.find((x) => x.badge)?.badge;
    if (!b) return;
    const t = badgeText(unreadMail(this.ctx));
    setText(b, t);
    b.hidden = !t;
  }

  // — Per frame: bars, EXP, level, modes, skill row —

  private frame(now: number): void {
    if (this.disposed || this.rt.root.hidden || this.rt.layers.rig.hidden) return; // rig hidden: the touch layout
    const p = this.ctx.player, st = p.stats?.stats, sh = this.shown;
    const hpMax = st?.[Stat.HP] ?? 0, mpMax = st?.[Stat.MP] ?? 0;
    if (p.hp !== sh.hp || hpMax !== sh.hpMax) {
      if (sh.hp >= 0 && p.hp < sh.hp && hpMax === sh.hpMax) restartAnim(this.hpBar, "struck");
      sh.hp = p.hp; sh.hpMax = hpMax;
      const v = hpMax > 0 ? Math.min(100, Math.max(0, (p.hp * 100) / hpMax)) : 0;
      // The HP fill sits at the bar's right (the diamond side): slide it right as HP drops.
      const tr = `translateX(${(100 - v).toFixed(2)}%)`;
      setStyle(this.hpFill, "transform", tr);
      setStyle(this.hpLag, "transform", tr);
      setText(this.hpTxt, `${p.hp} / ${hpMax}`);
      const low = hpMax > 0 && v < 30;
      if (low !== sh.low) { sh.low = low; this.hpBar.classList.toggle("low", low); }
    }
    if (p.mp !== sh.mp || mpMax !== sh.mpMax) {
      sh.mp = p.mp; sh.mpMax = mpMax;
      const v = mpMax > 0 ? Math.min(100, Math.max(0, (p.mp * 100) / mpMax)) : 0;
      const tr = `translateX(-${(100 - v).toFixed(2)}%)`;
      setStyle(this.mpFill, "transform", tr);
      setStyle(this.mpLag, "transform", tr);
      setText(this.mpTxt, `${p.mp} / ${mpMax}`);
    }
    const psn = ((p.user?.poison ?? 0) & PoisonType.Green) !== 0;
    if (psn !== sh.psn) { sh.psn = psn; this.hpBar.classList.toggle("psn", psn); }
    if (p.level !== sh.level) {
      const up = sh.level > 0 && p.level > sh.level;
      sh.level = p.level;
      setText(this.lvNum, String(p.level));
      if (up) { restartAnim(this.core, "lvup"); this.float("LEVEL UP!", "big"); }
    }
    if (p.experience !== this.shownExp || p.maxExperience !== this.shownMaxExp) this.drawExp(p.experience, p.maxExperience);
    for (const m of this.modes) m.update();
    this.skills.frame(now);
  }

  private drawExp(exp: bigint, max: bigint): void {
    const prev = this.shownExp, prevMax = this.shownMaxExp;
    this.shownExp = exp; this.shownMaxExp = max;
    const pct = max > 0n ? Number((exp * 10000n) / max) / 100 : 0;
    // "+x% EXP" on a gain within the same level (not on the first draw or a level change).
    if (prev >= 0n && max === prevMax && exp > prev && max > 0n) {
      const gain = Number(((exp - prev) * 10000n) / max) / 100;
      if (gain >= 0.01) this.float(`+${gain.toFixed(2)}% EXP`, "exp");
    }
    const f = Math.min(100, Math.max(0, pct));
    this.expPath.setAttribute("stroke-dasharray", `${f} ${100 - f}`);
    const at = (f / 100) * 4, seg = Math.min(3, Math.floor(at)), t = at - seg;
    const [ax, ay] = CORNERS[seg]!, [bx, by] = CORNERS[seg + 1]!;
    this.expHead.setAttribute("cx", (ax + (bx - ax) * t).toFixed(2));
    this.expHead.setAttribute("cy", (ay + (by - ay) * t).toFixed(2));
    this.expHead.style.display = f > 0.2 ? "" : "none";
    setText(this.lvPct, `${f.toFixed(2)}%`);
    let lit = 0;
    for (const [v] of VERTS) if (f >= v) lit++;
    if (lit !== this.shown.lit) {
      this.shown.lit = lit;
      VERTS.forEach(([v], i) => { this.ticks[i]!.style.fill = f >= v ? "var(--ac)" : "var(--fr)"; });
    }
  }

  private float(text: string, cls: string): void {
    const f = h("div", "hm-float " + cls, text);
    this.core.append(f);
    later(() => f.remove(), 1500, this.rt.signal);
  }

  // — ~4x a second: plates, buffs, poisons, durability —

  private slow(now: number): void {
    if (this.disposed || this.rt.root.hidden) return;
    if (this.rt.layers.rig.hidden) { this.drawBuffs(now); this.drawPoisons(now); return; } // the touch layout shows only these
    this.plates.update();
    this.drawBuffs(now);
    this.drawPoisons(now);
    if (this.duraDirty) { this.duraDirty = false; this.drawDura(); }
  }

  private drawBuffs(now: number): void {
    const list = this.ctx.player.buffs;
    let key = "";
    for (const b of list) key += `${b.Type}:${b.Infinite ? 1 : 0}:${b.Paused ? 1 : 0}:${b.expire}|`;
    if (key !== this.buffKey) {
      this.buffKey = key;
      this.buffList = list.slice();
      this.buffEls = [];
      this.buffRow.replaceChildren();
      for (const b of this.buffList) {
        const i = h("i"), t = h("span");
        const el = h("div", "hm-buff", i, t);
        el.setAttribute("aria-label", pretty(BuffTypeName[b.Type] ?? "Buff"));
        const [lib, index] = buffIcon(b.Type);
        void libPicture(lib, index).then((pic) => { if (pic) i.style.backgroundImage = `url(${pic.url})`; });
        this.buffEls.push({ el, t, ending: false });
        this.buffRow.append(el);
      }
    }
    for (let k = 0; k < this.buffList.length; k++) {
      const b = this.buffList[k]!, e = this.buffEls[k]!;
      let text: string, ending = false;
      if (b.Infinite) text = "∞";
      else if (b.Paused) text = "II";
      else { const left = (b.expire - now) / 1000; text = fmtShort(left); ending = left < 10; }
      setText(e.t, text);
      if (ending !== e.ending) { e.ending = ending; e.el.classList.toggle("ending", ending); }
    }
  }

  private buffTitle(target: EventTarget | null): void {
    const el = target instanceof Element ? target.closest(".hm-buff") : null;
    const k = el ? this.buffEls.findIndex((x) => x.el === el) : -1;
    const b = this.buffList[k];
    if (el instanceof HTMLElement && b) el.title = buffText(b, BuffTypeName[b.Type] ?? `Buff ${b.Type}`, this.ctx.now());
  }

  /** The user's poisons: S.PoisonStatus timers (poisons model) plus any S.Poisoned flag without a timer. */
  private drawPoisons(now: number): void {
    const timers = this.ctx.models.poisons, flags = this.ctx.player.user?.poison ?? 0;
    let seen = 0, key = "";
    for (const t of timers.list) { seen |= t.type; key += `${t.type}:${t.endsAt}|`; }
    for (let bit = 1; bit <= flags; bit <<= 1) if (flags & bit && !(seen & bit)) key += `${bit}|`;
    if (key !== this.poisonKey) {
      this.poisonKey = key;
      this.poisonEls = [];
      this.poisonRow.replaceChildren();
      const add = (type: number) => {
        const c = poisonColour(type);
        const t = h("span");
        const el = h("div", "hm-buff psn", h("i", "", svg("drop", "ic")), t);
        el.style.setProperty("--pz", c ? `rgb(${Math.round(c.r * 255)} ${Math.round(c.g * 255)} ${Math.round(c.b * 255)})` : "var(--bad)");
        el.dataset.type = String(type);
        el.setAttribute("aria-label", pretty(PoisonTypeName[type] ?? "Poison"));
        this.poisonEls.push({ t, type });
        this.poisonRow.append(el);
      };
      for (const t of timers.list) add(t.type);
      for (let bit = 1; bit <= flags; bit <<= 1) if (flags & bit && !(seen & bit)) add(bit);
    }
    for (const e of this.poisonEls) {
      const t = timers.get(e.type);
      setText(e.t, t ? fmtShort(timers.timeLeft(t, now) / 1000) : "");
    }
  }

  private poisonTitle(target: EventTarget | null): void {
    const el = target instanceof HTMLElement ? target.closest<HTMLElement>(".hm-buff.psn") : null;
    if (!el) return;
    const type = Number(el.dataset.type), timers = this.ctx.models.poisons, t = timers.get(type);
    el.title = pretty(PoisonTypeName[type] ?? "Poison") + (t ? `\n${fmtShort(timers.timeLeft(t) / 1000)} left` : "");
  }

  /** Worn gear at or under half durability, worst first (CharacterDuraPanel: amulets by Count / StackSize, everything
   *  else by CurrentDura / MaxDura; dura-less items skipped). */
  private drawDura(): void {
    const inv = this.ctx.models.inventory, infos = this.ctx.models.itemInfos;
    const low: { name: string; img: number; frac: number }[] = [];
    for (const it of inv.equipment) {
      if (!it) continue;
      const info = infos.get(it.ItemIndex);
      if (!info) continue;
      const amulet = info.Type === ItemType.Amulet;
      const cur = amulet ? it.Count : it.CurrentDura, max = amulet ? info.StackSize : it.MaxDura;
      if (max <= 0) continue;
      const frac = Math.min(1, Math.max(0, cur) / max);
      if (frac <= DURA_WARN) low.push({ name: info.Name, img: info.Image, frac });
    }
    low.sort((a, b) => a.frac - b.frac);
    const key = low.map((x) => `${x.img}:${Math.round(x.frac * 100)}`).join("|");
    if (key === this.duraKey) return;
    this.duraKey = key;
    this.dura.hidden = !low.length;
    this.dura.replaceChildren();
    if (!low.length) return;
    const col = (f: number) => (f === 0 ? "var(--dim)" : f <= DURA_DANGER ? "var(--bad)" : "var(--warn)");
    const pop = h("div", "pop", h("div", "hd", "GEAR NEEDS REPAIR"));
    for (const x of low.slice(0, 8)) {
      const u = h("u"), bar = h("b"), barP = h("b");
      const c = h("div", "dc" + (x.frac === 0 ? " broken" : ""), u, h("i", "", bar));
      c.style.setProperty("--dc", col(x.frac));
      bar.style.transform = `scaleX(${x.frac})`;
      void libPicture(Lib.Items, x.img).then((pic) => { if (pic) u.style.backgroundImage = `url(${pic.url})`; });
      this.dura.append(c);
      const r = h("div", "r", h("span", "", x.name), h("em", "", barP), h("span", "", x.frac === 0 ? "broken" : `${Math.round(x.frac * 100)}%`));
      r.style.setProperty("--dc", col(x.frac));
      barP.style.transform = `scaleX(${x.frac})`;
      pop.append(r);
    }
    pop.append(h("div", "ft", "Broken gear gives no stats. Repair it at a blacksmith. Click: Character window."));
    this.dura.append(pop);
  }
}
