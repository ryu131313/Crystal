// Ported from play/hudGameMenu.ts (the pause-style game menu) with the menu emblems now in core/icons.ts: a modern take
// on the desktop's MenuDialog, opened by Esc with no window open (GameMenu.escape, the router's hotkeys layer) or the
// menu button at the end of the HUD's menu strip. Left: the character card (name, level, class, guild, map, session
// time) and the actions (Resume, Options, Key bindings, Help, Log out, Switch to classic interface). Right: a searchable
// launcher of every registered window with its hotkey (from the key bindings) and badge (unread mail).
//
// Log out goes through ctx.actions.logOut(false): GameScene.LogOut, which refuses inside the 10 s combat lock
// (game.ts keeps LogTime and posts "Cannot leave game for N seconds") and otherwise asks Yes / No before C.LogOut.
// Not ported (Crystal / a browser has no such thing): Exit to desktop (a page cannot close itself), the online count
// (no packet), ping, the crafting tile (crafting is an NPC page); the combat-state chip (LogTime is not on the
// GameUiContext; the refusal line still shows). Windows that are not registered yet (later phases) have no tile.
// No backdrop-filter over the live world (Performance.md T3-2): the veil is a plain darker fill. The clock and the
// session time tick once a second only while the menu is open.

import { KeybindOptions, keyLabel, matches, type KeybindOption } from "../../../game/keybinds.ts";
import type { GameUiContext } from "../../gameUi.ts";
import { asButton, h, injectCss, setText } from "../core/dom.ts";
import { className, classTag, fmtDuration, fmtTimeOfDay } from "../core/format.ts";
import { menuIcon, svg, type IconName } from "../core/icons.ts";
import type { UiRuntime } from "../core/runtime.ts";
import { classVar } from "../core/styles.ts";
import type { TickSub } from "../core/ticker.ts";
import { searchBox } from "../core/widgets.ts";

/** A window the menu strip / launcher can open: the runtime registry key (later phases register these keys), its
 *  name, its emblem (core/icons.ts menuIcon) and strip icon (ICONS), and the binding that toggles it (hotkey hint). */
export interface MenuWindow {
  readonly key: string; readonly name: string; readonly emblem: string; readonly icon: IconName;
  readonly bind?: KeybindOption;
  /** On the HUD's menu strip (the rest are launcher-only). */
  readonly strip?: boolean;
}

/** The registry keys of the new UI's windows (phases 3b-3f register them with UiRuntime.registerWindow). */
export const WINDOW_KEYS = {
  inventory: "inventory", character: "character", skills: "skills", quests: "quests", guild: "guild", group: "group",
  friends: "friends", mail: "mail", gameShop: "gameShop", bigMap: "bigMap", options: "options", ranking: "ranking",
  creatures: "creatures", mount: "mount", fishing: "fishing", mentor: "mentor", relationship: "relationship", hero: "hero",
  territories: "territories", keybinds: "keybinds", help: "help",
} as const;

const W = WINDOW_KEYS;
/** Every window of the strip and the launcher, strip order first. */
export const MENU_WINDOWS: readonly MenuWindow[] = [
  { key: W.inventory, name: "Bag", emblem: "Bag", icon: "bag", bind: KeybindOptions.Inventory, strip: true },
  { key: W.character, name: "Character", emblem: "Character", icon: "person", bind: KeybindOptions.Equipment, strip: true },
  { key: W.skills, name: "Skills", emblem: "Skills", icon: "star", bind: KeybindOptions.Skills, strip: true },
  { key: W.quests, name: "Quests", emblem: "Quests", icon: "scroll", bind: KeybindOptions.Quests, strip: true },
  { key: W.guild, name: "Guild", emblem: "Guild", icon: "guild", bind: KeybindOptions.Guilds, strip: true },
  { key: W.group, name: "Group", emblem: "Group", icon: "group", bind: KeybindOptions.Group, strip: true },
  { key: W.friends, name: "Friends", emblem: "Friends", icon: "friends", bind: KeybindOptions.Friends, strip: true },
  { key: W.mail, name: "Mail", emblem: "Mail", icon: "mail", strip: true },
  { key: W.gameShop, name: "Game shop", emblem: "Game shop", icon: "shop", bind: KeybindOptions.GameShop, strip: true },
  { key: W.bigMap, name: "World map", emblem: "World map", icon: "map", bind: KeybindOptions.Bigmap, strip: true },
  { key: W.options, name: "Options", emblem: "Options", icon: "gear", bind: KeybindOptions.Options, strip: true },
  { key: W.hero, name: "Hero", emblem: "Hero", icon: "hero", bind: KeybindOptions.HeroInventory },
  { key: W.ranking, name: "Ranking", emblem: "Ranking", icon: "ranking", bind: KeybindOptions.Ranking },
  { key: W.territories, name: "Territories", emblem: "Territories", icon: "flag" },
  { key: W.creatures, name: "Creatures", emblem: "Creatures", icon: "creature", bind: KeybindOptions.Creature },
  { key: W.mount, name: "Mount", emblem: "Mount", icon: "mount", bind: KeybindOptions.MountWindow },
  { key: W.fishing, name: "Fishing", emblem: "Fishing", icon: "fishing", bind: KeybindOptions.Fishing },
  { key: W.mentor, name: "Mentor", emblem: "Mentor", icon: "mentor", bind: KeybindOptions.Mentor },
  { key: W.relationship, name: "Relationship", emblem: "Relationship", icon: "heart", bind: KeybindOptions.Relationship },
  { key: W.keybinds, name: "Key bindings", emblem: "Key bindings", icon: "keyboard", bind: KeybindOptions.Keybind },
  { key: W.help, name: "Help", emblem: "help", icon: "help", bind: KeybindOptions.Help },
];

/** Unread mail (UserObject.Mail not Opened): the Mail badge on the strip and the launcher. */
export function unreadMail(ctx: GameUiContext): number {
  let n = 0;
  for (const m of ctx.models.mail.list) if (!m.Opened) n++;
  return n;
}
/** A badge text ("" = none). */
export const badgeText = (n: number): string => (n <= 0 ? "" : n > 9 ? "9+" : String(n));

export interface GameMenuHost {
  readonly rt: UiRuntime;
  readonly ctx: GameUiContext;
  /** Opens a window by registry key (false when nothing is registered for it). */
  openWindow(key: string): boolean;
  /** "Switch to classic interface": the integrator saves UiStyle Classic and offers "Log out now to switch". */
  switchToClassic(): void;
  /** performance.now() when the game scene started (the session time). */
  readonly started: number;
}

const CSS = `
.hudm .gmenu { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; gap: 18px; pointer-events: auto;
  background: radial-gradient(ellipse at 40% 50%, rgba(6,12,24,.62), rgba(2,4,10,.88)); animation: hudm-gmin .2s ease-out; }
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
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb)); border: 2px solid var(--cls, var(--ac)); box-shadow: 0 0 14px var(--glow); }
.hudm .gmenu .who { min-width: 0; }
.hudm .gmenu .who b { display: block; font-size: 15px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .gmenu .who span { display: block; font-size: 11px; color: var(--dim); line-height: 1.5; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .gmenu .who em { font-style: normal; color: var(--ac); }
.hudm .gmenu .acts { width: 280px; padding: 8px; display: flex; flex-direction: column; gap: 3px; }
.hudm .gmenu .act { position: relative; display: flex; align-items: center; gap: 12px; height: 40px; padding: 0 12px; border-radius: var(--rad); cursor: pointer; user-select: none;
  font-size: 13px; font-weight: 600; letter-spacing: .03em; border: 1px solid transparent; transition: background .12s, border-color .12s, padding .12s; }
.hudm .gmenu .act svg.ic { width: 18px; height: 18px; stroke: var(--acd); stroke-width: 1.8; }
.hudm .gmenu .act svg.gmi { width: 22px; height: 22px; flex: none; transition: transform .12s; }
.hudm .gmenu .act kbd { margin-left: auto; font: inherit; font-size: 10px; color: var(--dim); padding: 1px 6px; border-radius: 4px; border: 1px solid var(--fr); }
.hudm .gmenu .act.on { padding-left: 16px; color: #fff; border-color: color-mix(in srgb, var(--ac) 55%, var(--fr));
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 30%, transparent), transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gmenu .act.on svg.gmi { transform: scale(1.08); }
.hudm .gmenu .act.on svg.ic { stroke: var(--ac); }
.hudm .gmenu .act.leave.on { box-shadow: inset 3px 0 0 var(--bad); border-color: color-mix(in srgb, var(--bad) 55%, var(--fr));
  background: linear-gradient(90deg, color-mix(in srgb, var(--bad) 28%, transparent), transparent); }
.hudm .gmenu .sep { height: 1px; margin: 4px 6px; background: linear-gradient(90deg, transparent, var(--fr), transparent); }
.hudm .gmenu .launch { width: 470px; padding: 14px; }
.hudm .gmenu .lh { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
.hudm .gmenu .lh b { font-size: 11px; letter-spacing: .24em; color: var(--ac); }
.hudm .gmenu .lh .ui-search { margin-left: auto; width: 190px; }
.hudm .gmenu .tiles { display: grid; grid-template-columns: repeat(5, 1fr); gap: 8px; }
.hudm .gmenu .tile { position: relative; height: 74px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px; cursor: pointer; user-select: none;
  border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); background: radial-gradient(ellipse 80% 70% at 50% 30%, color-mix(in srgb, var(--pc) 70%, transparent), transparent);
  transition: transform .12s, border-color .12s; }
.hudm .gmenu .tile svg.gmi { width: 34px; height: 34px; transition: transform .12s; }
.hudm .gmenu .tile svg.ic { width: 24px; height: 24px; stroke: var(--acd); stroke-width: 1.7; }
.hudm .gmenu .tile span { font-size: 11px; font-weight: 600; text-align: center; }
.hudm .gmenu .tile kbd { position: absolute; right: 5px; top: 4px; font: inherit; font-size: 9px; color: var(--dim); }
.hudm .gmenu .tile i { position: absolute; left: 5px; top: 4px; min-width: 14px; height: 14px; padding: 0 3px; border-radius: 7px; font-style: normal; font-size: 9px; font-weight: 800;
  line-height: 14px; text-align: center; color: #fff; background: var(--ac); }
.hudm .gmenu .tile:hover, .hudm .gmenu .tile.hit, .hudm .gmenu .tile:focus-visible { transform: translateY(-2px); border-color: var(--ac); }
.hudm .gmenu .tile:hover svg.gmi, .hudm .gmenu .tile.hit svg.gmi { transform: translateY(-1px) scale(1.08); }
.hudm .gmenu .tile.dim { opacity: .25; }
.hudm .gmenu .none { grid-column: 1 / -1; }
.hudm .gmenu .foot { display: flex; gap: 14px; justify-content: space-between; padding: 8px 4px 0; font-size: 10.5px; color: var(--dim); font-variant-numeric: tabular-nums; }
.hudm .gmenu .foot b { color: var(--tx); font-weight: 600; }
/* Narrow layer (< 1040 px): one column, a smaller launcher. */
.hudm.narrow .gmenu { flex-direction: column; gap: 10px; overflow-y: auto; justify-content: flex-start; padding: 16px 0; }
.hudm.narrow .gmenu .logo { display: none; }
.hudm.narrow .gmenu .launch { width: min(470px, 96%); }
.hudm.narrow .gmenu .tiles { grid-template-columns: repeat(4, 1fr); }
`;

interface Act { el: HTMLElement; run: () => void }
interface Tile { el: HTMLElement; w: MenuWindow }

export class GameMenu {
  private el: HTMLElement | null = null;
  private acts: Act[] = [];
  private tiles: Tile[] = [];
  private sel = 0;
  private sessionEl = h("span");
  private clockEl = h("b");
  private search: ReturnType<typeof searchBox> | null = null;
  private readonly live: TickSub;

  constructor(private readonly host: GameMenuHost) {
    this.live = host.rt.ticker.every(1000, () => this.tick());
    host.rt.signal.addEventListener("abort", () => this.dispose(), { once: true });
  }

  get open(): boolean { return !!this.el && !this.el.hidden; }
  toggle(): void { if (this.open) this.close(); else this.show(); }

  show(): void {
    if (this.host.rt.signal.aborted) return;
    if (!this.el) {
      injectCss("hud-game-menu", CSS);
      this.el = h("div", "gmenu");
      this.el.setAttribute("role", "dialog");
      this.el.setAttribute("aria-label", "Game menu");
      // A press on the veil itself resumes.
      this.el.addEventListener("pointerdown", (e) => { if (e.target === this.el) this.close(); });
      this.host.rt.layers.menu.append(this.el);
    }
    this.build();
    this.el.hidden = false;
    this.sel = 0;
    this.mark();
    this.live.on();
    this.tick();
  }

  close(): void {
    if (!this.el || this.el.hidden) return;
    this.el.hidden = true;
    this.live.off();
  }

  /** The router's "menu" layer: while open, Esc resumes, Up / Down / Enter work the actions; a window's hotkey closes
   *  the menu and carries on (returns false) so the window opens. Other keys pass. */
  handleKey(e: KeyboardEvent): boolean {
    if (!this.open || this.host.rt.root.hidden) return false;
    const typing = e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;
    if (e.key === "Escape") { e.preventDefault(); this.close(); return true; }
    if (typing) return false;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const n = this.acts.length;
      if (n) { this.sel = (this.sel + (e.key === "ArrowDown" ? 1 : n - 1)) % n; this.mark(); }
      return true;
    }
    if (e.key === "Enter" && !(e.target instanceof HTMLElement && e.target.closest(".tile, .act"))) { e.preventDefault(); this.run(this.sel); return true; }
    for (const w of MENU_WINDOWS) if (w.bind !== undefined && matches(e, w.bind)) { this.close(); return false; }
    return false;
  }

  /** Esc with nothing else to close (register after the window layer): opens the menu. */
  escape(e: KeyboardEvent): boolean {
    if (e.key !== "Escape" || e.shiftKey || e.ctrlKey || e.altKey || this.open || this.host.rt.root.hidden) return false;
    if (this.host.rt.wm.anyOpen() || this.host.rt.popups.open) return false;
    e.preventDefault();
    this.show();
    return true;
  }

  dispose(): void {
    this.live.off();
    this.el?.remove();
    this.el = null;
  }

  private build(): void {
    const { ctx, rt } = this.host;
    const u = ctx.player.user;
    const cls = u?.look.class ?? 0;
    const av = h("div", "av", classTag(cls));
    av.style.setProperty("--cls", classVar(cls));
    const map = ctx.world.mapTitle;
    const card = h("div", "card", av, h("div", "who",
      h("b", "", u?.name ?? ""),
      h("span", "", `Level ${ctx.player.level} ${className(cls)}`, u?.guildName ? " · " : "", u?.guildName ? h("em", "", u.guildName) : null),
      h("span", "", map || "…", " · ", this.sessionEl)));

    const acts = h("div", "acts");
    this.acts = [];
    const act = (cls: string, icon: Node, label: string, hint: string, run: () => void) => {
      const el = asButton(h("div", "act " + cls, icon, h("span", "", label), hint ? h("kbd", "", hint) : null));
      const i = this.acts.length;
      el.addEventListener("pointerenter", () => { this.sel = i; this.mark(); });
      el.addEventListener("click", () => this.run(i));
      this.acts.push({ el, run });
      acts.append(el);
    };
    const emblem = (key: string, fallback: IconName) => menuIcon(key) ?? svg(fallback);
    act("resume", emblem("resume", "play"), "Resume", "Esc", () => this.close());
    const win = (key: string, emblemKey: string, icon: IconName, label: string, bind: KeybindOption) => {
      if (rt.hasWindow(key)) act("", emblem(emblemKey, icon), label, keyLabel(bind), () => { this.close(); this.host.openWindow(key); });
    };
    win(W.options, "Options", "gear", "Options", KeybindOptions.Options);
    win(W.keybinds, "Key bindings", "keyboard", "Key bindings", KeybindOptions.Keybind);
    win(W.help, "help", "help", "Help", KeybindOptions.Help);
    acts.append(h("div", "sep"));
    act("leave", emblem("logout", "logout"), "Log out", keyLabel(KeybindOptions.Logout), () => { this.close(); ctx.actions.logOut(false); });
    act("", svg("swap"), "Switch to classic interface", "", () => { this.close(); this.host.switchToClassic(); });

    if (!this.search) { // built once (its debounce lives in the UI scope); cleared on every open
      this.search = searchBox("Type to find a window…", (q) => this.filter(q), { signal: rt.signal });
      this.search.input.addEventListener("keydown", (e) => {
        if (e.key !== "Enter") return;
        const t = this.tiles.find((x) => x.el.classList.contains("hit")) ?? this.tiles.find((x) => !x.el.classList.contains("dim"));
        if (t) { e.preventDefault(); this.launch(t.w); }
      });
    }
    this.search.set("");
    const tiles = h("div", "tiles");
    this.tiles = [];
    const unread = badgeText(unreadMail(ctx));
    for (const w of MENU_WINDOWS) {
      if (!rt.hasWindow(w.key)) continue; // later phases register it
      const k = w.bind !== undefined ? keyLabel(w.bind) : "";
      const badge = w.key === W.mail ? unread : "";
      const el = asButton(h("div", "tile", emblem(w.emblem, w.icon), h("span", "", w.name), k ? h("kbd", "", k) : null, badge ? h("i", "", badge) : null));
      el.title = `${w.name}${k ? ` (${k})` : ""}`;
      el.addEventListener("click", () => this.launch(w));
      tiles.append(el);
      this.tiles.push({ el, w });
    }
    if (!this.tiles.length) tiles.append(h("div", "empty-note none", "No windows yet."));
    const foot = h("div", "foot", h("span", "", "Local time ", this.clockEl));
    const launch = h("div", "launch", h("div", "lh", h("b", "", "WINDOWS"), this.search.el), tiles, foot);
    this.el!.replaceChildren(h("div", "col", h("div", "logo", "CRYSTAL", h("small", "", "LEGEND OF MIR")), card, acts), h("div", "col r", launch));
  }

  private filter(q: string): void {
    const s = q.toLowerCase();
    let first = true;
    for (const t of this.tiles) {
      const m = !s || t.w.name.toLowerCase().includes(s);
      t.el.classList.toggle("dim", !m);
      t.el.classList.toggle("hit", !!s && m && first);
      if (s && m) first = false;
    }
  }

  private launch(w: MenuWindow): void { this.close(); this.host.openWindow(w.key); }

  private mark(): void { this.acts.forEach((a, i) => a.el.classList.toggle("on", i === this.sel)); }

  private run(i: number): void { this.acts[i]?.run(); }

  /** Once a second while open: the session time and the clock. */
  private tick(): void {
    setText(this.sessionEl, `session ${fmtDuration((this.host.rt.ticker.now || performance.now()) / 1000 - this.host.started / 1000)}`);
    setText(this.clockEl, fmtTimeOfDay());
  }
}
