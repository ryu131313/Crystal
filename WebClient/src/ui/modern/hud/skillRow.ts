// Ported from play/hudMock.ts (slotShell / spellSlot / the cooldown sweep, ~912-955 and 1156-1174; modeSlot ~1005-1039):
// the HUD's skill row, four slots under each belt row, plus the attack mode (left) and pet mode (right) slots at its
// outer ends.
//
// Skill row: bar 1 (magic keys 1-8, KeybindOptions.Bar1Skill1-8, F1-F8 by default); while Ctrl is held, bar 2 (keys
// 9-16, Bar2Skill1-8, Ctrl+F1-F8); while Shift is held, the hero's skills (keys 17-24, HeroSkill1-8, Shift+F1-F8).
// Spells come from Magic (magic.magics / heroMagics by UserMagic.Key), icons MagIcon2[Icon * 2], the cooldown sweep from
// castTime + delay each frame (the pie path only while a cooldown runs; the digits through setText), the no-MP tint
// from the cost (BaseCost + LevelCost * Level) against the user's / hero's MP, and the 'armed' glow only for Crystal's
// real toggle spells (Magic's toggle flags: Slaying, Thrusting, HalfMoon, CrossHalfMoon, DoubleSlash, TwinDrakeBlade,
// FlamingSword) while they are on. Casting stays on the key bindings: a slot click replays the slot's binding through
// the input router (as the classic SkillBarDialog does), so game.ts casts at the mouse with every check it has.
// The mock's local cast (MP spent, a cooldown started here) and right-click "arm any slot" are gone.
//
// Mode slots: the server's AttackMode / PetMode (PlayerState.aMode / pMode, set by S.ChangeAMode / S.ChangePMode); a
// pick sends C.ChangeAMode / C.ChangePMode (300 ms apart, like game.ts's Ctrl+H / Ctrl+A) and the slot shows the
// server's echo, never a local change.

import { AttackMode, PetMode, Spell } from "../../../protocol/enums.generated.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import type { UserMagic, Toggles } from "../../../game/magic.ts";
import { KeybindOptions, getKeyBind, keyLabel, onKeyBindsChange, type KeybindOption } from "../../../game/keybinds.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { asButton, h, injectCss, restartAnim, setText } from "../core/dom.ts";
import { svgNode } from "../core/icons.ts";
import type { UiRuntime } from "../core/runtime.ts";
import { shortKey } from "./belt.ts";
import type { Popover } from "./statPlates.ts";

const CSS = `
.hudm .hm-slot { position: relative; flex: none; width: 40px; height: 40px; pointer-events: auto; cursor: pointer; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hm-slot::before { content: ""; position: absolute; inset: 0; pointer-events: none; z-index: 3; border-radius: inherit; padding: 1px;
  background: linear-gradient(160deg, color-mix(in srgb, var(--frl) 70%, transparent), transparent 40%, transparent 60%, color-mix(in srgb, var(--frl) 50%, transparent));
  mask: linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0); }
.hudm .hm-slot::after { content: ""; position: absolute; inset: 1px; pointer-events: none; z-index: 2; border-radius: calc(var(--rad) - 1px);
  box-shadow: inset 0 0 0 1px rgba(0,0,0,.9), inset 0 0 6px 1px rgba(0,0,0,.75); }
.hudm .hm-slot:hover { border-color: var(--ac); }
/* Icons draw 1:1 (MagIcon2 36 x 34): a 40 px slot leaves a 36 x 36 box, never scaled. */
.hudm .hm-slot .ico { position: absolute; inset: 1px; background: center / auto no-repeat; image-rendering: pixelated; }
.hudm .hm-slot .tint { position: absolute; inset: 2px; border-radius: calc(var(--rad) - 2px); z-index: 1; pointer-events: none; opacity: 0; transition: opacity .2s;
  background: color-mix(in srgb, var(--pb) 70%, transparent); }
.hudm .hm-slot .key { position: absolute; left: 2px; top: 0; z-index: 4; font-size: 9px; font-weight: 700; color: var(--tx); text-shadow: 0 0 2px #000, 0 0 2px #000; pointer-events: none; }
.hudm .hm-slot .cd { position: absolute; inset: 0; z-index: 2; pointer-events: none; width: 100%; height: 100%; }
.hudm .hm-slot .cdt { position: absolute; inset: 0; z-index: 4; display: flex; align-items: center; justify-content: center; font-size: 14px; font-weight: 700; color: #fff;
  text-shadow: 0 0 3px #000, 0 0 3px #000; pointer-events: none; }
.hudm .hm-slot .glint { position: absolute; inset: 0; z-index: 3; overflow: hidden; pointer-events: none; }
.hudm .hm-slot .glint::before { content: ""; position: absolute; top: -20%; bottom: -20%; width: 40%; left: -60%; transform: skewX(-20deg);
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 70%, #fff), transparent); }
.hudm .hm-slot.ready .glint::before { animation: hudm-glint .5s ease-out; }
@keyframes hudm-glint { to { left: 130%; } }
/* No MP: the tint fades in (opacity only). */
.hudm .hm-slot.nomp .tint { opacity: 1; }
/* Armed (a toggle spell that is on): the glow is a .halo child pulsing by opacity (Performance.md T1-1). */
.hudm .hm-slot.armed { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 calc(var(--glr) * .8) var(--glow); overflow: visible; --halo: 0 0 calc(var(--glr) * 1.5) var(--ac); }
.hudm .hm-slot > .halo { display: none; } .hudm .hm-slot.armed > .halo { display: block; }
.hudm .hm-slot.armed .ico { clip-path: inset(0 round max(0px, calc(var(--rad) - 2px))); }
.hudm .hm-slot.armed .cd { clip-path: inset(0 round max(0px, calc(var(--rad) - 1px))); }
.hudm .hm-slot.empty { cursor: default; }
.hudm .hm-slot.empty .ico { opacity: 0; }
.hudm .skills.hero .hm-slot:not(.mode) { border-color: color-mix(in srgb, var(--ac) 45%, var(--fr)); }
.hudm .skills.nohero .hm-slot:not(.mode) { opacity: .45; }
.hudm .hm-slot.flash { animation: hudm-sflash .35s ease-out; }
@keyframes hudm-sflash { from { box-shadow: 0 0 0 1px #000, 0 0 16px var(--ac), inset 0 0 18px var(--glow); } }
.hudm.nokeys .hm-slot .key { display: none; }

/* Attack / pet mode slots */
.hudm .hm-slot.mode { width: 64px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 1px; text-align: center; }
.hudm .hm-slot.mode small { font-size: 9px; line-height: 11px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); }
.hudm .hm-slot.mode span { font-size: 10.5px; line-height: 13px; font-weight: 600; color: var(--ac); text-shadow: 0 0 calc(6px * var(--gl)) var(--glow); white-space: nowrap; }
.hudm .hm-modes { min-width: 150px; }
.hudm .hm-modes div { display: flex; align-items: center; gap: 7px; padding: 4px 8px; border-radius: calc(var(--rad) * .6); cursor: pointer; white-space: nowrap; }
.hudm .hm-modes div:hover, .hudm .hm-modes div:focus-visible { background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .hm-modes div.on { box-shadow: inset 2px 0 0 var(--ac); }
.hudm .hm-modes kbd { margin-left: auto; font: inherit; font-size: 9px; color: var(--dim); }
`;

/** Which spells the row shows: 0 = bar 1 (keys 1-8), 1 = bar 2 (9-16, Ctrl held), 2 = the hero's (17-24, Shift held). */
export type SkillView = 0 | 1 | 2;

/** Crystal's toggle spells -> Magic's flag (Magic.toggled). Only these glow 'armed'. */
const TOGGLE_FIELD: Readonly<Partial<Record<number, keyof Toggles>>> = {
  [Spell.Slaying]: "slaying", [Spell.Thrusting]: "thrusting", [Spell.HalfMoon]: "halfMoon", [Spell.CrossHalfMoon]: "crossHalfMoon",
  [Spell.DoubleSlash]: "doubleSlash", [Spell.TwinDrakeBlade]: "twinDrakeBlade", [Spell.FlamingSword]: "flamingSword",
};

/** The binding behind slot `i` (0-7) of a view (game.ts: Bar1Skill1 + i for keys 1-16, HeroSkill1 + i for 17-24). */
export function slotOption(view: SkillView, i: number): KeybindOption {
  return (view === 2 ? KeybindOptions.HeroSkill1 + i : KeybindOptions.Bar1Skill1 + view * 8 + i) as KeybindOption;
}

/** Replays a binding as a keydown through the input router (classic magicBar.ts useSpellKey): game.ts casts at the
 *  mouse with its own checks. False when the binding is cleared. */
export function replayBinding(opt: KeybindOption): boolean {
  const b = getKeyBind(opt);
  if (!b || b.key === "") return false;
  const k = b.key;
  const code = /^F\d+$/.test(k) ? k : /^[A-Z]$/.test(k) ? `Key${k}` : /^D\d$/.test(k) ? `Digit${k.slice(1)}` : /^NumPad\d$/.test(k) ? `Numpad${k.slice(6)}` : k;
  window.dispatchEvent(new KeyboardEvent("keydown", {
    key: k.length === 1 ? k.toLowerCase() : /^D\d$/.test(k) ? k.slice(1) : k, code,
    ctrlKey: b.requireCtrl === 1, altKey: b.requireAlt === 1, shiftKey: b.requireShift === 1, bubbles: true, cancelable: true,
  }));
  return true;
}

/** Cooldown sweep: a pie from 12 o'clock, clockwise, covering the remaining fraction of a 44 x 44 box. */
function piePath(frac: number): string {
  if (frac <= 0) return "";
  if (frac >= 1) return "M0 0H44V44H0Z";
  const a = (1 - frac) * Math.PI * 2, r = 40, cx = 22, cy = 22;
  const x = cx + r * Math.sin(a), y = cy - r * Math.cos(a);
  return `M${cx} ${cy}L${x.toFixed(2)} ${y.toFixed(2)}A${r} ${r} 0 ${frac > 0.5 ? 1 : 0} 1 ${cx} ${cy - r}Z`;
}

interface SkillSlot {
  el: HTMLElement; ico: HTMLElement; cd: SVGPathElement; cdt: HTMLElement; kbd: HTMLElement;
  magic: UserMagic | null; shown: string; img: number; cdOn: boolean; nomp: boolean; armed: boolean;
}

export class SkillRow {
  /** The left (slots 1-4) and right (5-8) skill rows; the HUD adds the mode slots at their outer ends. */
  readonly left = h("div", "hm-hrow skills");
  readonly right = h("div", "hm-hrow skills");
  private readonly slots: SkillSlot[] = [];
  private _view: SkillView = 0;
  private dirty = true;
  /** Set by the touch pad (mobile/mobileUi.ts): a slot tap casts magic key 1-24 through TouchControl, not the binding. */
  onCast: ((key: number) => void) | null = null;

  constructor(private readonly ctx: GameUiContext, rt: UiRuntime) {
    injectCss("hud-skills", CSS);
    for (let i = 0; i < 8; i++) {
      const el = asButton(h("div", "hm-slot empty"));
      const ico = h("div", "ico"), cdt = h("div", "cdt"), kbd = h("div", "key");
      const cdSvg = svgNode("svg", { class: "cd", viewBox: "0 0 44 44", preserveAspectRatio: "none", "aria-hidden": "true" });
      const cd = svgNode("path", { d: "", fill: "rgba(0,0,0,.68)" });
      cdSvg.append(cd);
      el.append(h("div", "halo"), ico, h("div", "tint"), cdSvg, h("div", "glint"), cdt, kbd);
      el.addEventListener("click", () => this.cast(i));
      el.addEventListener("contextmenu", (e) => e.preventDefault());
      this.slots.push({ el, ico, cd, cdt, kbd, magic: null, shown: "", img: -2, cdOn: false, nomp: false, armed: false });
      (i < 4 ? this.left : this.right).append(el);
    }
    const sig = rt.signal, mark = () => { this.dirty = true; };
    const offs = [ctx.models.magic.subscribe(mark), ctx.models.magic.onHeroChange(mark), ctx.models.hero.onChange(mark), onKeyBindsChange(mark)];
    sig.addEventListener("abort", () => { for (const off of offs) off(); }, { once: true });
  }

  get view(): SkillView { return this._view; }

  /** Slot i's element (0-7: left row, then right). */
  slotEl(i: number): HTMLElement | undefined { return this.slots[i]?.el; }

  /** The magic key slot i casts in the current view (1-16, hero 17-24). */
  keyFor(i: number): number { return this._view === 2 ? 17 + i : this._view * 8 + i + 1; }

  /** Whether a tap on slot i can cast now (as last drawn by frame()). */
  slotState(i: number): "empty" | "cooldown" | "nomp" | "ok" {
    const s = this.slots[i];
    return !s?.magic ? "empty" : s.cdOn ? "cooldown" : s.nomp ? "nomp" : "ok";
  }

  /** Shows bar 1, bar 2 or the hero's skills (the HUD calls it from the held Ctrl / Shift state). */
  setView(v: SkillView): void {
    if (v === this._view) return;
    this._view = v;
    this.dirty = true;
    this.left.classList.toggle("hero", v === 2);
    this.right.classList.toggle("hero", v === 2);
  }

  /** Per frame: re-binds the slots after a magic / key / view change, then the cooldown sweep and the no-MP tint. */
  frame(now: number): void {
    if (this.dirty) this.bind();
    const view = this._view, magic = this.ctx.models.magic, hero = this.ctx.models.hero;
    const mp = view === 2 ? hero.info?.mp ?? 0 : this.ctx.player.mp;
    const toggles: Toggles = view === 2 ? magic.heroToggles : magic;
    for (const s of this.slots) {
      const m = s.magic;
      if (!m) continue;
      // Cooldown (magicBar.ts coolDownFrame: none under 100 ms left)
      const left = m.delay > 0 && Number.isFinite(m.castTime) ? m.castTime + m.delay - now : 0;
      if (left >= 100) {
        s.cdOn = true;
        s.cd.setAttribute("d", piePath(left / m.delay)); // the smooth sweep: one attribute write per frame while it runs
        setText(s.cdt, left >= 1000 ? String(Math.ceil(left / 1000)) : (left / 1000).toFixed(1));
      } else if (s.cdOn) {
        s.cdOn = false;
        s.cd.setAttribute("d", "");
        setText(s.cdt, "");
        restartAnim(s.el, "ready");
      }
      const nomp = m.BaseCost + m.LevelCost * m.Level > mp;
      if (nomp !== s.nomp) { s.nomp = nomp; s.el.classList.toggle("nomp", nomp); }
      const field = TOGGLE_FIELD[m.Spell];
      const armed = !!field && toggles[field];
      if (armed !== s.armed) { s.armed = armed; s.el.classList.toggle("armed", armed); }
    }
  }

  /** Which slots show which spells (on magic / hero / key binding / view changes). */
  private bind(): void {
    this.dirty = false;
    const view = this._view, magic = this.ctx.models.magic, hero = this.ctx.models.hero;
    const list = view === 2 ? magic.heroMagics : magic.magics;
    const noHero = view === 2 && !hero.info;
    this.left.classList.toggle("nohero", noHero);
    this.right.classList.toggle("nohero", noHero);
    for (let i = 0; i < 8; i++) {
      const s = this.slots[i]!;
      const key = view === 2 ? 17 + i : view * 8 + i + 1;
      let m: UserMagic | null = null;
      for (const x of list) if (x.Key === key) { m = x; break; }
      s.magic = m;
      setText(s.kbd, shortKey(keyLabel(slotOption(view, i))));
      const cost = m ? m.BaseCost + m.LevelCost * m.Level : 0;
      const sig = m ? `${view}|${m.Spell}|${m.Icon}|${m.Level}|${cost}` : `${view}|-|${noHero}`;
      if (sig === s.shown) continue;
      s.shown = sig;
      s.el.classList.toggle("empty", !m);
      // A new spell: clear the old one's sweep / tint / glow (the next frame redraws what applies, without a glint).
      s.cdOn = false; s.armed = false; s.nomp = false;
      s.el.classList.remove("armed", "nomp");
      s.cd.setAttribute("d", "");
      setText(s.cdt, "");
      const who = view === 2 ? "Hero skill" : view === 1 ? "Skill bar 2" : "Skill bar 1";
      s.el.title = m ? `${m.Name} (level ${m.Level}${cost ? `, ${cost} MP` : ""})\nClick: cast at the mouse` : noHero ? "No hero summoned" : `${who}: empty (assign a spell in the Skills window)`;
      s.el.setAttribute("aria-label", m ? m.Name : `${who} slot ${i + 1}, empty`);
      const img = m ? m.Icon * 2 : -1;
      if (img !== s.img) {
        s.img = img;
        s.ico.style.backgroundImage = "";
        if (img >= 0) void libPicture(Lib.MagIcon2, img).then((pic) => { if (pic && s.img === img) s.ico.style.backgroundImage = `url(${pic.url})`; });
      }
    }
  }

  /** A slot click: the slot's binding through the router (game.ts casts at the mouse). */
  private cast(i: number): void {
    const s = this.slots[i]!;
    if (!s.magic) return;
    if (this.onCast) { this.onCast(this._view === 2 ? 17 + i : this._view * 8 + i + 1); restartAnim(s.el, "flash"); return; }
    if (replayBinding(slotOption(this._view, i))) restartAnim(s.el, "flash");
  }
}

// — Mode slots —

/** [value, short name for the slot, the desktop's name (Client Localization AttackMode_* / PetMode_*), its direct bind]. */
const ATTACK_MODES: readonly [number, string, string, KeybindOption][] = [
  [AttackMode.Peace, "Peaceful", "Peaceful", KeybindOptions.AttackmodePeace], [AttackMode.Group, "Group", "Group", KeybindOptions.AttackmodeGroup],
  [AttackMode.Guild, "Guild", "Guild", KeybindOptions.AttackmodeGuild], [AttackMode.EnemyGuild, "Enemy Guild", "Enemy Guild", KeybindOptions.AttackmodeEnemyguild],
  [AttackMode.RedBrown, "Red/Brown", "Red/Brown", KeybindOptions.AttackmodeRedbrown], [AttackMode.All, "Attack All", "Attack All", KeybindOptions.AttackmodeAll],
];
const PET_MODES: readonly [number, string, string, KeybindOption][] = [
  [PetMode.Both, "Attack", "Attack and Move", KeybindOptions.PetmodeBoth], [PetMode.MoveOnly, "No Attack", "Do Not Attack", KeybindOptions.PetmodeMoveonly],
  [PetMode.AttackOnly, "No Move", "Do Not Move", KeybindOptions.PetmodeAttackonly], [PetMode.None, "Idle", "Do Not Attack or Move", KeybindOptions.PetmodeNone],
  [PetMode.FocusMasterTarget, "Focus", "Focus Master Target", KeybindOptions.PetmodeFocusMasterTarget],
];
/** GameScene's ChangeAModeTime / ChangePModeTime spacing. */
const MODE_GAP_MS = 300;

/** The attack mode (left) or pet mode (right) slot with its list. */
export class ModeSlot {
  readonly el: HTMLElement;
  private readonly name = h("span");
  private shown = -1;
  private nextSend = 0;

  constructor(private readonly kind: "attack" | "pet", private readonly ctx: GameUiContext, private readonly pop: Popover, private readonly host: HTMLElement) {
    injectCss("hud-skills", CSS);
    this.el = asButton(h("div", "hm-slot mode", h("small", "", kind === "pet" ? "Pet" : "Mode"), this.name));
    this.el.addEventListener("click", () => this.toggle());
    this.update();
  }

  private get list() { return this.kind === "pet" ? PET_MODES : ATTACK_MODES; }
  private get current(): number { return this.kind === "pet" ? this.ctx.player.pMode : this.ctx.player.aMode; }

  /** Shows the server's mode (cheap when unchanged; the HUD calls it each frame). */
  update(): void {
    const cur = this.current;
    if (cur === this.shown) return;
    const first = this.shown < 0;
    this.shown = cur;
    const row = this.list.find(([v]) => v === cur);
    setText(this.name, row?.[1] ?? "?");
    const bind = keyLabel(this.kind === "pet" ? KeybindOptions.ChangePetmode : KeybindOptions.ChangeAttackmode);
    this.el.title = `${this.kind === "pet" ? "Pet mode" : "Attack mode"}: ${row?.[2] ?? "?"}\nClick for the list${bind ? ` (${bind} cycles)` : ""}.`;
    if (!first) restartAnim(this.el, "flash");
  }

  private toggle(): void {
    if (this.pop.isFor(this.el)) { this.pop.close(); return; }
    const box = h("div", "hm-modes");
    const cur = this.current;
    for (const [v, , full, opt] of this.list) {
      const k = keyLabel(opt);
      const row = asButton(h("div", v === cur ? "on" : "", h("span", "", full), k ? h("kbd", "", k) : null));
      row.addEventListener("click", () => { this.pop.close(); this.send(v); });
      box.append(row);
    }
    this.pop.show(this.el, this.host, box, this.kind === "pet");
  }

  /** C.ChangeAMode / C.ChangePMode; the slot changes when the server answers (S.ChangeAMode / S.ChangePMode). */
  private send(mode: number): void {
    const now = this.ctx.now();
    if (mode === this.current || now < this.nextSend) return;
    this.nextSend = now + MODE_GAP_MS;
    this.ctx.send(this.kind === "pet" ? ClientPackets.ChangePMode({ Mode: mode }) : ClientPackets.ChangeAMode({ Mode: mode }));
  }
}
