// Character select mock for the scene lab's UI tester: a modern, full-screen take on the desktop's SelectScene and
// NewCharacterDialog over the live game world (blurred and dimmed; the desktop's Prguse 65 background is its whole old
// select UI, so it is not used) with the real ChrSel class previews (Warrior 20 / 300,
// Wizard 40 / 320, Taoist 60 / 340, Assassin 80 / 360, Archer 100 / 140 for male / female; 16 frames at 250 ms, plus
// the +560 glow layer the desktop blends on top). A roster of up to Globals.MaxCharacterCount (4) characters with empty
// slots, the selected character big on a lit pedestal with its name plate, a details panel (level, class, guild, map,
// last played, play time), Start game with a loading bar and a fade into the HUD, Delete with a type-the-name confirm
// (the server only marks it Deleted), and character creation: class cards (Assassin / Archer follow
// Settings.AllowCreateAssassin / AllowCreateArcher, on by default), gender, a live preview, a name field with the
// server's CharacterReg (3-15 letters, digits, _ or Chinese), taken names, a random name, and the client's messages for
// the server's results (0 creation disabled, 1 name not acceptable, 2 gender, 3 class, 4 too many characters, 5 name
// exists). Characters and their details are mock. Simulate > Character select.

import { ME } from "./hudPeople.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { h, injectCss } from "./hudWindow.ts";

const MAX_CHARS = 4, NAME_RX = /^[一-龥_A-Za-z0-9]{3,15}$/;
const CLASSES = ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"] as const;
type Cls = typeof CLASSES[number];
const DISPLAY: Record<Cls, [number, number]> = { Warrior: [20, 300], Wizard: [40, 320], Taoist: [60, 340], Assassin: [80, 360], Archer: [100, 140] };
const ABOUT: Record<Cls, [string, string, string]> = {
  Warrior: ["Melee", "Heavy armour and big swords. The toughest class, first into every fight.", "M5 19L19 5M14 5h5v5M5 14l5 5M3 21l3-3"],
  Wizard: ["Magic", "Fire, ice and lightning from range. Fragile up close, devastating at a distance.", "M12 2l2.5 6.5L21 9l-5 4.5L17.5 21 12 17l-5.5 4L8 13.5 3 9l6.5-.5z"],
  Taoist: ["Support", "Heals, poisons, summons and buffs. Holds a group together.", "M12 3a9 9 0 110 18 9 9 0 010-18zM12 3a4.5 4.5 0 000 9 4.5 4.5 0 010 9"],
  Assassin: ["Stealth", "Fast twin blades, hiding and burst damage.", "M4 20L16 8M8 4l12 12M14 4h6v6"],
  Archer: ["Ranged", "Bows, traps and elemental arrows from afar.", "M5 19C9 9 15 5 19 5M19 5l-4 0M19 5l0 4M5 19l14-14"],
};
const ALLOW = { Assassin: true, Archer: true }; // Settings.AllowCreateAssassin / AllowCreateArcher defaults
const RANDOM = ["Aelric", "Brynna", "Caspian", "Dorian", "Elara", "Fenwick", "Grimm", "Halvar", "Isolde", "Jorund", "Kestrel", "Lyra", "Morrow", "Nerys", "Orrin", "Perrin", "Quill", "Rhosyn", "Sable", "Tamsin"];
interface Char { name: string; cls: Cls; female: boolean; level: number; guild: string; map: string; last: string; played: string; gold: number }

const CSS = `
.hudm .slscr { position: absolute; left: 0; top: 0; width: 100%; height: 100%; z-index: 40; pointer-events: auto; overflow: hidden; color: var(--tx); background: rgba(0,0,0,.35); backdrop-filter: blur(4px) brightness(.55) saturate(.8); font-size: 13px; }
/* While a full-screen scene shows, the HUD and its windows are hidden (they'd show through the blurred world). */
.hudm.scene-on .rig, .hudm.scene-on .vign, .hudm.scene-on .wins > :not(.slscr):not(.lgscr):not(.ldscr):not(.itip) { visibility: hidden; }
.hudm .slscr .shade { position: absolute; inset: 0; background: radial-gradient(ellipse 40% 60% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%), linear-gradient(90deg, rgba(0,0,0,.7), transparent 30%, transparent 70%, rgba(0,0,0,.7)); }
.hudm .slscr .rays { position: absolute; left: 50%; top: -10%; width: 520px; height: 90%; translate: -50% 0; background: conic-gradient(from 180deg at 50% 0%, transparent 160deg, color-mix(in srgb, var(--ac) 18%, transparent) 172deg, transparent 180deg, color-mix(in srgb, var(--ac) 12%, transparent) 188deg, transparent 200deg);
  filter: blur(6px); opacity: .7; animation: hudm-slrays 8s ease-in-out infinite alternate; } @keyframes hudm-slrays { to { opacity: .35; transform: scaleX(1.15); } }
.hudm .slscr .glass { position: absolute; border-radius: calc(var(--rad) * 1.4); background: color-mix(in srgb, var(--pb) 62%, transparent); backdrop-filter: blur(10px); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6), 0 20px 50px rgba(0,0,0,.6); }
.hudm .slscr .top { position: absolute; left: 0; right: 0; top: 0; height: 56px; display: flex; align-items: center; gap: 14px; padding: 0 28px; background: linear-gradient(180deg, rgba(0,0,0,.7), transparent); }
.hudm .slscr .top b { font: 900 22px Georgia, serif; letter-spacing: .1em; color: transparent; background: linear-gradient(100deg, #b8902f, #ffe9a8 40%, #b8902f); -webkit-background-clip: text; background-clip: text; }
.hudm .slscr .top span { font-size: 11px; letter-spacing: .3em; color: var(--dim); } .hudm .slscr .top .sp { flex: 1; } .hudm .slscr .top a { cursor: pointer; color: var(--tx); font-size: 12px; } .hudm .slscr .top a:hover { color: var(--ac); }
.hudm .slscr .roster { left: 28px; top: 80px; width: 270px; padding: 14px; display: flex; flex-direction: column; gap: 8px; }
.hudm .slscr .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); } .hudm .slscr .lbl b { letter-spacing: 0; color: var(--tx); }
.hudm .slscr .ch { position: relative; display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: rgba(3,5,10,.4); transition: transform .15s, border-color .15s; }
.hudm .slscr .ch:hover { transform: translateX(3px); border-color: color-mix(in srgb, var(--ac) 60%, transparent); }
.hudm .slscr .ch.on { border-color: var(--ac); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 22%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac), 0 0 14px color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .slscr .ch .pt { width: 46px; height: 46px; flex: none; border-radius: 50%; border: 2px solid var(--fr); background: #05070c center / cover no-repeat; image-rendering: pixelated; }
.hudm .slscr .ch.on .pt { border-color: var(--ac); box-shadow: 0 0 10px var(--glow); }
.hudm .slscr .ch .t { flex: 1; min-width: 0; } .hudm .slscr .ch .t b { display: block; font-size: 15px; color: #fff; } .hudm .slscr .ch .t small { display: block; font-size: 11px; color: var(--dim); }
.hudm .slscr .ch .lv { font-size: 20px; font-weight: 800; color: var(--ac); text-shadow: 0 0 8px var(--glow); } .hudm .slscr .ch .lv small { display: block; font-size: 8px; letter-spacing: .14em; color: var(--dim); text-align: center; }
.hudm .slscr .ch.empty { justify-content: center; border-style: dashed; color: var(--dim); min-height: 46px; } .hudm .slscr .ch.empty:hover { color: #fff; }
.hudm .slscr .stage { position: absolute; left: 50%; top: 70px; width: 420px; height: 520px; translate: -50% 0; }
.hudm .slscr .stage::after { content: ""; position: absolute; left: 50%; bottom: 40px; width: 260px; height: 44px; translate: -50% 0; border-radius: 50%; background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%);
  box-shadow: 0 0 40px color-mix(in srgb, var(--ac) 25%, transparent); }
.hudm .slscr .spr { position: absolute; left: 50%; bottom: 52px; transform-origin: 50% 100%; z-index: 1; }
.hudm .slscr .spr img { position: absolute; image-rendering: pixelated; } .hudm .slscr .spr img.glow { mix-blend-mode: screen; }
.hudm .slscr .spr.in { animation: hudm-slin .5s ease-out; } @keyframes hudm-slin { from { opacity: 0; filter: brightness(3); } }
.hudm .slscr .slplate { position: absolute; left: 50%; bottom: 0; translate: -50% 0; z-index: 2; text-align: center; white-space: nowrap; }
.hudm .slscr .slplate b { display: block; font-size: 30px; font-weight: 800; color: #fff; text-shadow: 0 0 16px var(--glow), 0 2px 4px #000; letter-spacing: .04em; }
.hudm .slscr .slplate span { font-size: 12px; letter-spacing: .18em; text-transform: uppercase; color: var(--ac); }
.hudm .slscr .info { right: 28px; top: 80px; width: 270px; padding: 16px; display: flex; flex-direction: column; gap: 9px; }
.hudm .slscr .kv { display: flex; justify-content: space-between; font-size: 12px; padding: 3px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .slscr .kv span { color: var(--dim); } .hudm .slscr .kv b { color: #fff; }
.hudm .slscr .start { position: absolute; left: 50%; bottom: 46px; translate: -50% 0; width: 300px; height: 52px; display: flex; align-items: center; justify-content: center; cursor: pointer; overflow: hidden; border-radius: 26px; z-index: 3;
  font-size: 17px; font-weight: 900; letter-spacing: .2em; color: #fff; border: 1px solid var(--ac); background: linear-gradient(180deg, color-mix(in srgb, var(--ac) 60%, var(--pb)), color-mix(in srgb, var(--ac) 25%, var(--pb)));
  box-shadow: 0 0 0 1px #000, 0 0 30px var(--glow); animation: hudm-slbreath 2.4s ease-in-out infinite; }
@keyframes hudm-slbreath { 50% { box-shadow: 0 0 0 1px #000, 0 0 12px var(--glow); } } .hudm .slscr .start:hover { filter: brightness(1.15); } .hudm .slscr .start.dis { opacity: .4; pointer-events: none; animation: none; }
.hudm .slscr .start i { position: absolute; left: 0; top: 0; bottom: 0; width: 0; background: rgba(255,255,255,.25); } .hudm .slscr .start.load i { animation: hudm-slload 1.6s ease-in forwards; } @keyframes hudm-slload { to { width: 100%; } }
.hudm .slscr .btns { display: flex; gap: 6px; } .hudm .slscr .btn2 { flex: 1; height: 32px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: calc(var(--rad) * .9); font-size: 12px; font-weight: 600;
  border: 1px solid var(--fr); background: rgba(3,5,10,.5); } .hudm .slscr .btn2:hover { border-color: var(--ac); color: #fff; } .hudm .slscr .btn2.red:hover { border-color: #e05555; color: #ff9a8a; } .hudm .slscr .btn2.dis { opacity: .4; pointer-events: none; }
.hudm .slscr .btn2.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 40%, var(--pb)); }
.hudm .slscr .confirm { padding: 10px; border-radius: var(--rad); border: 1px solid #c44; background: rgba(160,30,30,.25); font-size: 12px; line-height: 1.4; display: flex; flex-direction: column; gap: 6px; }
.hudm .slscr input[type=text] { width: 100%; height: 36px; box-sizing: border-box; padding: 0 12px; border-radius: calc(var(--rad) * .9); font-size: 14px; color: #fff; outline: none; border: 1px solid var(--fr); background: rgba(3,5,10,.7); }
.hudm .slscr input[type=text]:focus { border-color: var(--ac); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ac) 20%, transparent); }
.hudm .slscr .nm.bad input { border-color: #e05555; } .hudm .slscr .nm.good input { border-color: #4fbf6a; } .hudm .slscr .nm small { display: block; font-size: 10px; margin-top: 3px; color: var(--dim); }
.hudm .slscr .nm.bad small { color: #ff8a7a; } .hudm .slscr .nm.good small { color: #6fe08a; }
.hudm .slscr .classes { left: 28px; top: 80px; width: 270px; padding: 14px; display: flex; flex-direction: column; gap: 7px; }
.hudm .slscr .cc { display: flex; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); background: rgba(3,5,10,.4); }
.hudm .slscr .cc:hover { border-color: color-mix(in srgb, var(--ac) 60%, transparent); } .hudm .slscr .cc.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 18%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .slscr .cc.off { opacity: .35; cursor: not-allowed; }
.hudm .slscr .cc svg { width: 26px; height: 26px; flex: none; fill: none; stroke: var(--ac); stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .slscr .cc b { display: block; color: #fff; } .hudm .slscr .cc small { font-size: 10px; color: var(--dim); letter-spacing: .1em; text-transform: uppercase; }
.hudm .slscr .about { font-size: 12px; line-height: 1.5; color: var(--tx); }
.hudm .slscr .gender { display: flex; gap: 6px; } .hudm .slscr .gender div { flex: 1; height: 32px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: calc(var(--rad) * .9); border: 1px solid var(--fr); }
.hudm .slscr .gender div.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .slscr .msg { font-size: 12px; padding: 8px 10px; border-radius: var(--rad); line-height: 1.4; } .hudm .slscr .msg.err { color: #ffd0c8; border: 1px solid #c44; background: rgba(160,30,30,.3); } .hudm .slscr .msg.ok { color: #c8ffd4; border: 1px solid #4fbf6a; background: rgba(30,120,50,.3); }
.hudm .slscr .fade { position: absolute; inset: 0; z-index: 10; background: #000; opacity: 0; pointer-events: none; transition: opacity .8s; } .hudm .slscr .fade.on { opacity: 1; }
.hudm .slscr .tip { position: absolute; left: 50%; bottom: 14px; translate: -50% 0; z-index: 3; font-size: 11px; color: var(--dim); }
`;

export class SelectMock {
  readonly el: HTMLElement;
  /** After Start game: the chosen character (hudMock.ts can set the HUD's class / level from it). */
  onStart: (c: { name: string; cls: number; level: number; map: string }) => void = () => {};
  /** Back to the login screen. */
  onBack: () => void = () => {};
  private chars: Char[] = [
    { name: "GMRyu", cls: "Warrior", female: false, level: 42, guild: "GameMaster", map: "Bichon Province", last: "2026/10/15 11:02", played: "41d 6h", gold: 1240000 },
    { name: "GMRyuWiz", cls: "Wizard", female: true, level: 36, guild: "GameMaster", map: "Woomyon Woods", last: "2026/10/12 21:40", played: "12d 2h", gold: 380500 },
    { name: "GMRyuTao", cls: "Taoist", female: false, level: 18, guild: "", map: "Serpent Valley", last: "2026/09/30 08:15", played: "2d 11h", gold: 21400 },
  ];
  private sel = 0;
  private mode: "select" | "create" = "select";
  private delAsk = false;
  private delTyped = "";
  private cCls: Cls = "Wizard";
  private cFemale = false;
  private cName = "";
  private msg: [string, string] = ["", ""];
  private frame = 0;
  private timer = 0;
  /** Start game was pressed: further presses wait for the screen to close (held Enter repeats). */
  private entering = false;
  private frames = new Map<number, { pics: { url: string; x: number; y: number; w: number; h: number }[]; glow: { url: string; x: number; y: number; w: number; h: number }[] }>();

  constructor() {
    injectCss("hudm-select-css", CSS);
    this.el = h("div", "slscr");
    this.el.hidden = true;
  }

  show(): void {
    this.mode = this.chars.length ? "select" : "create"; this.delAsk = false; this.msg = ["", ""]; this.entering = false;
    this.el.hidden = false; this.el.closest(".hudm")?.classList.add("scene-on"); this.render();
    window.clearInterval(this.timer); this.timer = window.setInterval(() => this.animate(), 250);
  }
  hide(): void { this.el.hidden = true; window.clearInterval(this.timer); this.el.closest(".hudm")?.classList.remove("scene-on"); }

  // — Preview (ChrSel class display: 16 frames at 250 ms + the +560 glow) —
  private dispIndex(cls: Cls, female: boolean): number { return DISPLAY[cls][female ? 1 : 0]; }
  private async load(idx: number): Promise<void> {
    if (this.frames.has(idx)) return;
    this.frames.set(idx, { pics: [], glow: [] });
    const conv = (p: { url: string; x: number; y: number; width: number; height: number } | null) => p ? { url: p.url, x: p.x, y: p.y, w: p.width, h: p.height } : null;
    const pics = (await Promise.all(Array.from({ length: 16 }, (_, i) => libPicture(Lib.ChrSel, idx + i)))).map(conv);
    const glow = (await Promise.all(Array.from({ length: 16 }, (_, i) => libPicture(Lib.ChrSel, idx + 560 + i)))).map(conv);
    this.frames.set(idx, { pics: pics.filter((p) => !!p) as never, glow: glow.filter((p) => !!p) as never });
    this.animate(true);
  }
  private current(): [Cls, boolean] { return this.mode === "create" ? [this.cCls, this.cFemale] : this.chars[this.sel] ? [this.chars[this.sel]!.cls, this.chars[this.sel]!.female] : ["Warrior", false]; }
  private animate(force = false): void {
    if (this.el.hidden) return;
    const spr = this.el.querySelector<HTMLElement>(".spr");
    if (!spr) return;
    const [cls, fem] = this.current(), idx = this.dispIndex(cls, fem);
    const f = this.frames.get(idx);
    if (!f) { void this.load(idx); return; }
    if (!f.pics.length) return;
    if (!force) this.frame = (this.frame + 1) % f.pics.length;
    const p = f.pics[this.frame % f.pics.length]!, g = f.glow[this.frame % Math.max(1, f.glow.length)];
    const all = [...f.pics];
    const x0 = Math.min(...all.map((q) => q.x)), y0 = Math.min(...all.map((q) => q.y)), x1 = Math.max(...all.map((q) => q.x + q.w)), y1 = Math.max(...all.map((q) => q.y + q.h));
    const w = x1 - x0, hh = y1 - y0, k = Math.min(1.7, 420 / Math.max(1, hh), 380 / Math.max(1, w));
    Object.assign(spr.style, { width: w + "px", height: hh + "px", marginLeft: -w / 2 + "px", transform: `scale(${k})` });
    let [a, b] = [spr.querySelector<HTMLImageElement>("img.base"), spr.querySelector<HTMLImageElement>("img.glow")];
    if (!a) { a = h("img"); a.className = "base"; spr.append(a); }
    if (!b) { b = h("img"); b.className = "glow"; spr.append(b); }
    a.src = p.url; Object.assign(a.style, { left: p.x - x0 + "px", top: p.y - y0 + "px", width: p.w + "px", height: p.h + "px" });
    if (g) { b.hidden = false; b.src = g.url; Object.assign(b.style, { left: g.x - x0 + "px", top: g.y - y0 + "px", width: g.w + "px", height: g.h + "px" }); } else b.hidden = true;
  }

  private render(): void {
    const me = this.chars.find((c) => c.name === ME.name);
    if (me) { me.cls = ME.cls as Cls; me.level = ME.level; }
    const back = h("a", "", "‹ Back to log in"); back.addEventListener("click", () => { this.hide(); this.onBack(); });
    const exit = h("a", "", "Exit"); exit.addEventListener("click", () => this.hide());
    const top = h("div", "top", h("b", "", "LEGEND OF MIR"), h("span", "", this.mode === "create" ? "CREATE A CHARACTER" : "CHOOSE YOUR CHARACTER"), h("span", "sp"), back, exit);
    const [cls, fem] = this.current();
    const spr = h("div", "spr in");
    const c = this.chars[this.sel];
    const plate = this.mode === "create" ? h("div", "slplate", h("b", "", this.cName || "New hero"), h("span", "", `${fem ? "Female" : "Male"} ${cls}`))
      : c ? h("div", "slplate", h("b", "", c.name), h("span", "", `Level ${c.level} ${c.cls}${c.guild ? " · " + c.guild : ""}`)) : h("div", "slplate");
    const stage = h("div", "stage", spr, plate);
    const parts: HTMLElement[] = [h("div", "shade"), h("div", "rays"), top, stage];
    if (this.mode === "select") parts.push(this.roster(), this.info(), this.startBtn(), h("div", "tip", "↑ ↓ pick a character · Enter to start"));
    else parts.push(this.classPanel(), this.createPanel());
    parts.push(h("div", "fade"));
    this.el.replaceChildren(...parts);
    this.frame = 0; this.animate(true);
    if (this.mode === "create") window.setTimeout(() => this.el.querySelector<HTMLInputElement>(".nm input")?.focus());
  }

  private portrait(el: HTMLElement, cls: Cls, female: boolean): void {
    void libPicture(Lib.Prguse, 1400 + CLASSES.indexOf(cls) + 10 * (female ? 1 : 0) + 370).then((p) => { if (p) el.style.backgroundImage = `url(${p.url})`; });
  }
  private roster(): HTMLElement {
    const r = h("div", "glass roster", h("div", "lbl", "Characters", h("b", "", `${this.chars.length} / ${MAX_CHARS}`)));
    this.chars.forEach((c, i) => {
      const pt = h("div", "pt"); this.portrait(pt, c.cls, c.female);
      const e = h("div", "ch" + (i === this.sel ? " on" : ""), pt, h("div", "t", h("b", "", c.name), h("small", "", `${c.cls} · ${c.map}`)), h("div", "lv", String(c.level), h("small", "", "LEVEL")));
      e.addEventListener("click", () => { this.sel = i; this.delAsk = false; this.msg = ["", ""]; this.render(); });
      e.addEventListener("dblclick", () => this.start());
      r.append(e);
    });
    for (let i = this.chars.length; i < MAX_CHARS; i++) {
      const e = h("div", "ch empty", "+ Create a character");
      e.addEventListener("click", () => this.newChar());
      r.append(e);
    }
    return r;
  }
  private newChar(): void {
    if (this.chars.length >= MAX_CHARS) { this.msg = [`You cannot make more than ${MAX_CHARS} characters.`, "err"]; this.render(); return; }
    this.mode = "create"; this.cName = ""; this.cCls = "Wizard"; this.cFemale = false; this.msg = ["", ""]; this.render();
  }
  private info(): HTMLElement {
    const c = this.chars[this.sel];
    const p = h("div", "glass info", h("div", "lbl", "Details"));
    if (!c) { p.append(h("div", "about", "No characters yet. Create one to begin.")); return p; }
    for (const [k, v] of [["Class", `${c.female ? "Female" : "Male"} ${c.cls}`], ["Level", String(c.level)], ["Guild", c.guild || "—"], ["Location", c.map], ["Last played", c.last], ["Played", c.played], ["Gold", c.gold.toLocaleString("en-GB")]] as const)
      p.append(h("div", "kv", h("span", "", k), h("b", "", v)));
    const create = h("div", "btn2" + (this.chars.length >= MAX_CHARS ? " dis" : ""), "New character"); create.addEventListener("click", () => this.newChar());
    const del = h("div", "btn2 red", "Delete"); del.addEventListener("click", () => { this.delAsk = true; this.delTyped = ""; this.render(); });
    p.append(h("div", "btns", create, del));
    if (this.delAsk) {
      const inp = h("input"); inp.type = "text"; inp.placeholder = `Type ${c.name} to confirm`; inp.value = this.delTyped;
      const yes = h("div", "btn2 red dis", "Delete forever");
      inp.addEventListener("keydown", (e) => e.stopPropagation());
      inp.addEventListener("input", () => { this.delTyped = inp.value; yes.classList.toggle("dis", inp.value !== c.name); });
      yes.addEventListener("click", () => {
        if (this.delTyped !== c.name) return;
        this.chars.splice(this.sel, 1); this.sel = 0; this.delAsk = false;
        this.msg = [`${c.name} was deleted.`, "ok"]; this.mode = this.chars.length ? "select" : "create"; this.render();
      });
      const no = h("div", "btn2", "Keep"); no.addEventListener("click", () => { this.delAsk = false; this.render(); });
      p.append(h("div", "confirm", h("div", "", `Delete `, h("b", "", c.name), `, level ${c.level} ${c.cls}? This can't be undone in the game.`), inp, h("div", "btns", yes, no)));
      window.setTimeout(() => inp.focus());
    }
    if (this.msg[0]) p.append(h("div", "msg " + this.msg[1], this.msg[0]));
    return p;
  }
  private startBtn(): HTMLElement {
    const s = h("div", "start" + (this.chars[this.sel] ? "" : " dis"), h("i"), h("span", "", "START GAME"));
    s.addEventListener("click", () => this.start());
    return s;
  }
  private start(): void {
    const c = this.chars[this.sel];
    if (!c || this.entering) return;
    this.entering = true;
    const s = this.el.querySelector(".start");
    s?.classList.add("load"); const t = s?.querySelector("span"); if (t) t.textContent = "ENTERING…";
    window.setTimeout(() => this.el.querySelector(".fade")?.classList.add("on"), 1400);
    window.setTimeout(() => { this.hide(); this.onStart({ name: c.name, cls: CLASSES.indexOf(c.cls), level: c.level, map: c.map }); }, 2300);
  }

  /** Keyboard: ↑ ↓ to pick, Enter to start, Esc to cancel creation (hudMock.ts calls this while the screen shows). */
  key(e: KeyboardEvent): void {
    if (this.el.hidden || this.entering || e.repeat || (e.target as HTMLElement).closest?.("input, textarea, select")) return;
    if (this.mode === "select") {
      if (e.code === "ArrowDown" || e.code === "ArrowUp") { e.preventDefault(); this.sel = (this.sel + (e.code === "ArrowDown" ? 1 : this.chars.length - 1)) % Math.max(1, this.chars.length); this.delAsk = false; this.render(); }
      if (e.code === "Enter") { e.preventDefault(); this.start(); }
    } else if (e.code === "Escape" && this.chars.length) { this.mode = "select"; this.render(); }
  }

  // — Create (NewCharacterDialog) —
  private classPanel(): HTMLElement {
    const p = h("div", "glass classes", h("div", "lbl", "Class"));
    for (const cl of CLASSES) {
      const off = (cl === "Assassin" || cl === "Archer") && !ALLOW[cl];
      const e = h("div", "cc" + (this.cCls === cl ? " on" : "") + (off ? " off" : ""));
      e.innerHTML = `<svg viewBox="0 0 24 24"><path d="${ABOUT[cl][2]}"/></svg>`;
      e.append(h("div", "", h("b", "", cl), h("small", "", ABOUT[cl][0])));
      e.title = off ? "Disabled on this server" : "";
      e.addEventListener("click", () => { if (!off) { this.cCls = cl; this.render(); } });
      p.append(e);
    }
    return p;
  }
  private createPanel(): HTMLElement {
    const p = h("div", "glass info", h("div", "lbl", this.cCls), h("div", "about", ABOUT[this.cCls][1]), h("div", "lbl", "Gender"));
    const g = h("div", "gender");
    for (const [label, f] of [["Male", false], ["Female", true]] as const) { const d = h("div", this.cFemale === f ? "on" : "", label); d.addEventListener("click", () => { this.cFemale = f; this.render(); }); g.append(d); }
    const inp = h("input"); inp.type = "text"; inp.maxLength = 15; inp.placeholder = "Character name"; inp.value = this.cName; inp.spellcheck = false;
    const hint = h("small", "");
    const nm = h("div", "nm", inp, hint);
    const check = () => {
      const v = inp.value, taken = this.chars.some((c) => c.name.toLowerCase() === v.toLowerCase()) || ["admin", "gamemaster", "gm"].includes(v.toLowerCase());
      const bad = !v ? "" : !NAME_RX.test(v) ? "3-15 letters, digits, _ or Chinese characters" : taken ? "That name is already taken" : "";
      nm.classList.toggle("bad", !!bad); nm.classList.toggle("good", !!v && !bad); hint.textContent = bad || (v ? "✓ Available" : "");
      const plate = this.el.querySelector(".slplate b"); if (plate) plate.textContent = v || "New hero";
      this.el.querySelector(".cr")?.classList.toggle("dis", !v || !!bad);
    };
    inp.addEventListener("input", () => { this.cName = inp.value; check(); });
    inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") this.create(); if (e.key === "Escape" && this.chars.length) { this.mode = "select"; this.render(); } });
    const rnd = h("div", "btn2", "🎲 Random name"); rnd.addEventListener("click", () => { inp.value = this.cName = RANDOM[Math.floor(Math.random() * RANDOM.length)]!; check(); });
    const cr = h("div", "btn2 main cr dis", "Create"); cr.addEventListener("click", () => this.create());
    const cancel = h("div", "btn2" + (this.chars.length ? "" : " dis"), "Cancel"); cancel.addEventListener("click", () => { this.mode = "select"; this.render(); });
    p.append(g, h("div", "lbl", "Name"), nm, rnd, h("div", "btns", cancel, cr));
    if (this.msg[0]) p.append(h("div", "msg " + this.msg[1], this.msg[0]));
    window.setTimeout(check);
    return p;
  }
  /** Envir.NewCharacter's checks and the client's messages for them. */
  private create(): void {
    const v = this.cName.trim();
    let err = "";
    if (!NAME_RX.test(v)) err = "Your character name is not acceptable.";
    else if (this.chars.length >= MAX_CHARS) err = `You cannot make more than ${MAX_CHARS} characters.`;
    else if (this.chars.some((c) => c.name.toLowerCase() === v.toLowerCase()) || ["admin", "gamemaster", "gm"].includes(v.toLowerCase())) err = "This character name already exists.";
    if (err) { this.msg = [err, "err"]; this.render(); return; }
    this.chars.push({ name: v, cls: this.cCls, female: this.cFemale, level: 1, guild: "", map: "Bichon Province", last: "Never", played: "0h", gold: 0 });
    this.sel = this.chars.length - 1; this.mode = "select"; this.msg = [`${v} was created. Welcome to Mir!`, "ok"]; this.render();
  }
}
