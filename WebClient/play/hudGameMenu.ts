// Game menu for the scene lab's UI tester (2026-10-15), a modern take on the desktop's MenuDialog (Exit, Log out, Help,
// Keyboard layout, Ranking, Crafting, Creatures, Mount, Fishing, Friends, Mentor, Relationship, Groups, Guild) as a
// pause-style overlay: Esc with no window open (or the menu button at the end of the HUD's menu strip) opens it over a
// dimmed, blurred world. Left: the character card (name, level, class, map, session time, combat state) and the system
// actions (Resume, Options, Key bindings, Help, Log out, Exit game). Right: a launcher of every window with its icon,
// hotkey and badge (unread mail), searchable by typing. Log out and Exit keep the desktop's rule (GameScene.LogOut /
// QuitGame): not within 10 s of combat ("You cannot leave the game for N seconds after fighting"), then a Yes / No box;
// here Log out goes to character select and Exit to the login screen. Keys: ↑ ↓ and Enter in the action list, a
// window's hotkey opens it (the menu closes and the key carries on), Esc resumes; the search box filters the tiles and
// Enter in it opens the first match. Icons: the illustrated set in hudMenuIcons.ts (a window without one keeps its HUD
// glyph). Mock only.

import { h, injectCss } from "./hudWindow.ts";
import { ICON_CSS, ICON_DEFS, menuIcon } from "./hudMenuIcons.ts";

export interface MenuWindow { name: string; key: string; icon: string; open(): void; badge?(): string; code?: string }
export interface GameMenuHost {
  profile(): { name: string; level: number; cls: string; map: Promise<string>; guild: string };
  /** Seconds left before the character may leave (0 = not in combat). */
  combatLock(): number;
  windows(): MenuWindow[];
  options(): void; keybinds(): void; help(): void;
  confirm(title: string, text: string, danger: boolean): Promise<boolean>;
  logOut(): void; exit(): void;
  say(text: string): void;
}

const ICON: Record<string, string> = {
  resume: "M8 5v14l11-7z",
  gear: "M12 8.5a3.5 3.5 0 110 7 3.5 3.5 0 010-7zM12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1",
  keys: "M3 7h18v11H3zM6 10h1M9 10h1M12 10h1M15 10h1M18 10h0M7 14h10",
  help: "M12 3a9 9 0 110 18 9 9 0 010-18zM9.5 9.5a2.5 2.5 0 015 0c0 1.8-2.5 2-2.5 4M12 17v.01",
  logout: "M10 4H5v16h5M14 8l4 4-4 4M18 12H9",
  exit: "M12 3v8M6.3 6.3a8 8 0 1011.4 0",
};
const fmtSession = (ms: number) => { const s = Math.floor(ms / 1000); return `${Math.floor(s / 3600)}h ${String(Math.floor(s / 60) % 60).padStart(2, "0")}m`; };

const CSS = `
.hudm .gmenu { position: absolute; inset: 0; z-index: 990000; display: flex; align-items: center; justify-content: center; gap: 18px; pointer-events: auto;
  background: radial-gradient(ellipse at 40% 50%, rgba(6,12,24,.45), rgba(2,4,10,.82)); backdrop-filter: blur(3px) saturate(.75); animation: hudm-gmin .2s ease-out; }
.hudm .gmenu[hidden] { display: none; }
@keyframes hudm-gmin { from { opacity: 0; } }
.hudm .gmenu .col { display: flex; flex-direction: column; gap: 12px; animation: hudm-gmslide .28s cubic-bezier(.2,.9,.3,1); }
.hudm .gmenu .col.r { animation-delay: .04s; animation-fill-mode: backwards; }
@keyframes hudm-gmslide { from { opacity: 0; transform: translateY(14px); } }
.hudm .gmenu .card, .hudm .gmenu .acts, .hudm .gmenu .launch { color: var(--tx); border-radius: calc(var(--rad) + 2px);
  background: linear-gradient(180deg, color-mix(in srgb, var(--pc) 88%, transparent), color-mix(in srgb, var(--pb) 92%, #000)); border: 1px solid var(--fr);
  box-shadow: 0 0 0 1px #000, 0 18px 44px rgba(0,0,0,.7), inset 0 1px 0 color-mix(in srgb, var(--frl) 25%, transparent); }
.hudm .gmenu .logo { text-align: center; font-size: 22px; font-weight: 800; letter-spacing: .5em; text-indent: .5em; color: #fff; text-shadow: 0 0 18px var(--glow), 0 0 2px var(--ac); }
.hudm .gmenu .logo small { display: block; margin-top: 2px; font-size: 9px; letter-spacing: .4em; color: var(--ac); font-weight: 700; }
.hudm .gmenu .card { width: 280px; padding: 14px 16px; display: flex; gap: 12px; align-items: center; position: relative; overflow: hidden; }
.hudm .gmenu .card::before { content: ""; position: absolute; inset: 0; background: radial-gradient(ellipse 50% 90% at 0% 50%, color-mix(in srgb, var(--ac) 18%, transparent), transparent); pointer-events: none; }
.hudm .gmenu .av { width: 48px; height: 48px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800; font-size: 13px; color: #fff;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--ac); box-shadow: 0 0 14px var(--glow); }
.hudm .gmenu .who b { display: block; font-size: 15px; color: #fff; } .hudm .gmenu .who span { display: block; font-size: 11px; color: var(--dim); line-height: 1.5; }
.hudm .gmenu .who em { font-style: normal; color: var(--ac); }
.hudm .gmenu .cbt { margin-top: 4px; display: inline-flex; align-items: center; gap: 5px; font-size: 10px; font-weight: 700; letter-spacing: .06em; padding: 1px 8px; border-radius: 9px;
  color: #6fe08a; border: 1px solid color-mix(in srgb, #6fe08a 45%, transparent); }
.hudm .gmenu .cbt.hot { color: #ffb3a6; border-color: #c44; animation: hudm-gmhot 1s ease-in-out infinite; } @keyframes hudm-gmhot { 50% { box-shadow: 0 0 10px rgba(255,90,74,.5); } }
.hudm .gmenu .acts { width: 280px; padding: 8px; display: flex; flex-direction: column; gap: 3px; }
.hudm .gmenu .act { position: relative; display: flex; align-items: center; gap: 12px; height: 40px; padding: 0 12px; border-radius: var(--rad); cursor: pointer; user-select: none;
  font-size: 13px; font-weight: 600; letter-spacing: .03em; border: 1px solid transparent; transition: background .12s, border-color .12s, padding .12s; }
.hudm .gmenu .act svg { width: 18px; height: 18px; flex: none; fill: none; stroke: var(--acd, var(--ac)); stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .gmenu .act.resume svg { fill: var(--ac); stroke: none; }
.hudm .gmenu .act kbd { margin-left: auto; font: inherit; font-size: 10px; color: var(--dim); padding: 1px 6px; border-radius: 4px; border: 1px solid var(--fr); }
.hudm .gmenu .act small { margin-left: auto; font-size: 10px; font-weight: 700; color: #ffb3a6; }
.hudm .gmenu .act.on { padding-left: 16px; color: #fff; border-color: color-mix(in srgb, var(--ac) 55%, var(--fr));
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 30%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gmenu .act.on svg { stroke: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .gmenu .act.locked { opacity: .55; } .hudm .gmenu .act.locked.on { box-shadow: inset 3px 0 0 #c44; }
.hudm .gmenu .act.exit:not(.locked).on { box-shadow: inset 3px 0 0 #ff5a4a; border-color: #c44; background: linear-gradient(90deg, rgba(200,50,40,.3), transparent); }
.hudm .gmenu .sep { height: 1px; margin: 4px 6px; background: linear-gradient(90deg, transparent, var(--fr), transparent); }
.hudm .gmenu .act.shake { animation: hudm-pshake2 .3s linear; } @keyframes hudm-pshake2 { 25% { transform: translateX(-5px); } 75% { transform: translateX(5px); } }
.hudm .gmenu .launch { width: 470px; padding: 14px; }
.hudm .gmenu .lh { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.hudm .gmenu .lh b { font-size: 11px; letter-spacing: .24em; color: var(--ac); }
.hudm .gmenu .lh input { margin-left: auto; width: 170px; height: 26px; padding: 0 10px; border-radius: 13px; font: inherit; font-size: 11px; color: #fff; outline: none; background: #05070b; border: 1px solid var(--fr); }
.hudm .gmenu .lh input:focus { border-color: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .gmenu .tiles { display: grid; grid-template-columns: repeat(5, 1fr); gap: 8px; }
.hudm .gmenu .tile { position: relative; height: 74px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; cursor: pointer; user-select: none;
  border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); background: radial-gradient(ellipse 80% 70% at 50% 30%, color-mix(in srgb, var(--pc) 70%, transparent), transparent);
  transition: transform .12s, border-color .12s, box-shadow .12s; }
.hudm .gmenu .tile svg { width: 24px; height: 24px; fill: none; stroke: var(--acd, var(--ac)); stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; transition: stroke .12s, filter .12s; }
.hudm .gmenu .tile span { font-size: 11px; font-weight: 600; }
.hudm .gmenu .tile kbd { position: absolute; right: 5px; top: 4px; font: inherit; font-size: 9px; color: var(--dim); }
.hudm .gmenu .tile i { position: absolute; left: 5px; top: 4px; min-width: 14px; height: 14px; padding: 0 3px; box-sizing: border-box; border-radius: 7px; font-style: normal; font-size: 9px; font-weight: 800;
  line-height: 14px; text-align: center; color: #fff; background: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .gmenu .tile:hover, .hudm .gmenu .tile.hit { transform: translateY(-2px); border-color: var(--ac); box-shadow: 0 6px 16px rgba(0,0,0,.5), 0 0 14px color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .gmenu .tile:hover svg, .hudm .gmenu .tile.hit svg { stroke: #fff; filter: drop-shadow(0 0 5px var(--glow)); }
.hudm .gmenu .tile.dim { opacity: .25; }
.hudm .gmenu .foot { display: flex; gap: 14px; justify-content: space-between; padding: 8px 4px 0; font-size: 10.5px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .gmenu .foot b { color: var(--tx); font-weight: 600; } .hudm .gmenu .foot .ok { color: #6fe08a; }
.hudm .hm-mbtn.gm { margin-left: 6px; }
/* Illustrated icons (hudMenuIcons.ts): no glyph stroke; they brighten and lift on hover / selection instead. */
.hudm .gmenu .tile svg.gmi, .hudm .gmenu .act svg.gmi { fill: none; stroke: none; transition: transform .12s, filter .12s; }
.hudm .gmenu .tile svg.gmi { width: 34px; height: 34px; }
.hudm .gmenu .act svg.gmi { width: 22px; height: 22px; }
.hudm .gmenu .tile:hover svg.gmi, .hudm .gmenu .tile.hit svg.gmi { stroke: none; transform: translateY(-1px) scale(1.08);
  filter: drop-shadow(0 1px 1px rgba(0,0,0,.7)) drop-shadow(0 0 6px var(--glow)) brightness(1.12); }
.hudm .gmenu .act.on svg.gmi, .hudm .gmenu .act.resume svg.gmi { stroke: none; fill: none; }
.hudm .gmenu .act.on svg.gmi { filter: drop-shadow(0 1px 1px rgba(0,0,0,.7)) drop-shadow(0 0 5px var(--glow)) brightness(1.12); }
.hudm .gmenu .act.locked svg.gmi { filter: grayscale(.7); }
`;

const svgOf = (d: string) => `<svg viewBox="0 0 24 24"><path d="${d}"/></svg>`;

export class GameMenu {
  readonly el = h("div", "gmenu");
  private sel = 0;
  private acts: { el: HTMLElement; run: () => void; gate?: boolean }[] = [];
  private tiles: { el: HTMLElement; w: MenuWindow }[] = [];
  private readonly started = performance.now();
  private timer = 0;
  private cbt!: HTMLElement;
  private sessionEl!: HTMLElement;
  private clockEl!: HTMLElement;
  private search!: HTMLInputElement;

  constructor(private readonly host: GameMenuHost) {
    injectCss("hudm-gmenu-css", CSS);
    injectCss("hudm-gmicons-css", ICON_CSS);
    this.el.hidden = true;
    this.el.addEventListener("mousedown", (e) => { if (e.target === this.el) this.close(); });
    window.addEventListener("keydown", (e) => this.key(e), true);
  }

  get open(): boolean { return !this.el.hidden; }
  toggle(): void { if (this.open) this.close(); else this.show(); }

  show(): void {
    this.build();
    this.el.hidden = false;
    this.sel = 0; this.mark();
    window.clearInterval(this.timer);
    this.timer = window.setInterval(() => this.live(), 250);
    this.live();
  }
  close(): void { this.el.hidden = true; window.clearInterval(this.timer); }

  private build(): void {
    const p = this.host.profile();
    this.cbt = h("div", "cbt");
    this.sessionEl = h("span");
    const mapEl = h("span", "", "…");
    void p.map.then((m) => { mapEl.textContent = m; });
    const card = h("div", "card", h("div", "av", p.name.slice(0, 2).toUpperCase()), h("div", "who", h("b", "", p.name),
      h("span", "", `Level ${p.level} ${p.cls} · `, h("em", "", p.guild)), h("span", "", mapEl, " · ", this.sessionEl), this.cbt));
    const acts = h("div", "acts");
    this.acts = [];
    const act = (cls: string, icon: string, label: string, keyHint: string, run: () => void, gate = false) => {
      const el = h("div", "act " + cls);
      el.innerHTML = menuIcon(icon) ?? svgOf(ICON[icon] ?? "");
      el.append(h("span", "", label), keyHint ? h("kbd", "", keyHint) : h("small"));
      const i = this.acts.length;
      el.addEventListener("mouseenter", () => { this.sel = i; this.mark(); });
      el.addEventListener("click", () => this.run(i));
      this.acts.push({ el, run, gate });
      acts.append(el);
    };
    act("resume", "resume", "Resume", "Esc", () => this.close());
    act("", "gear", "Options", "O", () => { this.close(); this.host.options(); });
    act("", "keys", "Key bindings", "U", () => { this.close(); this.host.keybinds(); });
    act("", "help", "Help", "?", () => { this.close(); this.host.help(); });
    acts.append(h("div", "sep"));
    act("", "logout", "Log out", "", () => void this.leave(false), true);
    act("exit", "exit", "Exit game", "", () => void this.leave(true), true);

    this.search = h("input");
    this.search.placeholder = "Type to find a window…";
    this.search.addEventListener("input", () => this.filter());
    this.search.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { const t = this.tiles.find((x) => x.el.classList.contains("hit")) ?? this.tiles.find((x) => !x.el.classList.contains("dim")); if (t) this.launch(t.w); e.preventDefault(); }
      e.stopPropagation();
    });
    const tiles = h("div", "tiles");
    this.tiles = this.host.windows().map((w) => {
      const el = h("div", "tile");
      el.innerHTML = menuIcon(w.name) ?? svgOf(w.icon);
      el.append(h("span", "", w.name));
      if (w.key) el.append(h("kbd", "", w.key));
      const b = w.badge?.();
      if (b) el.append(h("i", "", b));
      el.title = `${w.name}${w.key ? ` (${w.key})` : ""}`;
      el.addEventListener("click", () => this.launch(w));
      tiles.append(el);
      return { el, w };
    });
    this.clockEl = h("b");
    const foot = h("div", "foot", h("span", "", "Server ", h("b", "", "Crystal"), " · ", h("span", "ok", "● 24 ms")), h("span", "", h("b", "", "1,284"), " online"), h("span", "", "Local time ", this.clockEl));
    const launch = h("div", "launch", h("div", "lh", h("b", "", "WINDOWS"), this.search), tiles, foot);
    const defs = h("div");
    defs.innerHTML = ICON_DEFS; // the icons' gradients
    this.el.replaceChildren(h("div", "col", h("div", "logo", "CRYSTAL", h("small", "", "LEGEND OF MIR")), card, acts), h("div", "col r", launch), defs.firstElementChild!);
  }

  private filter(): void {
    const q = this.search.value.trim().toLowerCase();
    let first = true;
    for (const t of this.tiles) {
      const m = !q || t.w.name.toLowerCase().includes(q);
      t.el.classList.toggle("dim", !m);
      t.el.classList.toggle("hit", !!q && m && first);
      if (q && m) first = false;
    }
  }

  private launch(w: MenuWindow): void { this.close(); w.open(); }

  private mark(): void { this.acts.forEach((a, i) => a.el.classList.toggle("on", i === this.sel)); }

  private run(i: number): void {
    const a = this.acts[i];
    if (!a) return;
    if (a.gate && this.host.combatLock() > 0) {
      a.el.classList.remove("shake"); void a.el.offsetWidth; a.el.classList.add("shake");
      this.host.say(`You cannot leave the game for ${this.host.combatLock()} seconds after fighting.`);
      return;
    }
    a.run();
  }

  /** GameScene.LogOut / QuitGame: a Yes / No box, then character select / the login screen. */
  private async leave(exit: boolean): Promise<void> {
    const ok = await this.host.confirm(exit ? "Exit game" : "Log out", exit ? "Close the game and return to the desktop?" : "Log out and return to character select?", exit);
    if (!ok) return;
    if (this.host.combatLock() > 0) { this.host.say(`You cannot leave the game for ${this.host.combatLock()} seconds after fighting.`); return; }
    this.close();
    if (exit) this.host.exit(); else this.host.logOut();
  }

  /** Session time, the combat lock on Log out / Exit, the clock. */
  private live(): void {
    const lock = this.host.combatLock();
    this.cbt.className = "cbt" + (lock ? " hot" : "");
    this.cbt.textContent = lock ? `⚔ IN COMBAT · ${lock}s` : "● SAFE TO LEAVE";
    for (const a of this.acts) if (a.gate) {
      a.el.classList.toggle("locked", lock > 0);
      const s = a.el.querySelector("small");
      if (s) s.textContent = lock ? `${lock}s` : "";
    }
    this.sessionEl.textContent = `session ${fmtSession(performance.now() - this.started)}`;
    this.clockEl.textContent = new Date().toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
  }

  /** Capture: Esc resumes, ↑ ↓ Enter work the action list, a window's hotkey closes the menu and carries on to the HUD. */
  private key(e: KeyboardEvent): void {
    // this.el.closest("[hidden]"): closed, or open in a hidden HUD (Show HUD mock off), where keys are the lab's.
    if (this.el.closest("[hidden]") || document.querySelector(".hudm .hpop-veil:not([hidden])")) return;
    if ((e.target as HTMLElement).closest?.("input, textarea")) return;
    if (e.key === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); this.close(); return; }
    if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); e.stopImmediatePropagation(); this.sel = (this.sel + (e.key === "ArrowDown" ? 1 : this.acts.length - 1)) % this.acts.length; this.mark(); return; }
    if (e.key === "Enter") { e.preventDefault(); e.stopImmediatePropagation(); this.run(this.sel); return; }
    const w = this.host.windows().find((x) => x.code === e.code);
    if (w) this.close(); // the HUD's own handler opens it
  }
}
