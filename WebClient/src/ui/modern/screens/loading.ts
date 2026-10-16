// Ported from play/hudLoading.ts: the new UI's loading overlay, shown from S.StartGame (Result 4) until the world is
// ready (new UI only; the classic UI has no loading screen). The desktop's SelectScene.StartGame waited on
// Libraries.Loaded with the Prguse 940-948 animation at 100 ms; that spinner is used here.
//
// Lifetime: play/main.ts creates it on S.StartGame, before startGame, in #stage (not in #screen, which Screens.hide()
// clears, and not inside the game's UI root, which game.ts dispose removes). It covers the stage at whatever size the
// game sets, with its own '.hudm' theme root (the account stage has no game UiRuntime). It is fed by the game's real
// progress (no fake stages): mapInfo (S.MapInformation: title, BigMap picture, lights) -> mapLoaded (the .map file) ->
// userReady (S.UserInformation) -> firstFrame -> art drain (libraries.ts loadingCount(): the peak since the overlay
// opened, shown as (peak - pending) / peak; done at 0 or 6 s after the first frame). Then "PRESS ANY KEY TO CONTINUE":
// a key or a click fades it out, disposes it and calls onDone.
//
// Facts: NPC / exit counts from S.NewMapInfo (GameInfo.requestMap); safe zones, spawns and monster kinds with their
// level range from S.MapDetails (GameInfo.requestMapDetails, only while ServerRules.MapDetailsEnabled; hidden
// otherwise); your level (SelectInfo.Level, then S.UserInformation). Tips are a static list.
//
// Keys (webclient.md §3.6): no window / document listener. Before the game exists the overlay takes no keys at all;
// once attached, it registers one handler on the game's input router 'scene' layer (game keys stay off the world
// behind the overlay; text fields and modifier-only keys pass), removed on dispose. The click listener is on the
// overlay element itself. Timers (spinner, art poll, tip rotation) run only while it is shown and stop on dispose.
// prefers-reduced-motion: the base sheet cuts the CSS animations (pan, fade, pulse); the spinner holds one frame
// and the tips swap without the fade.

import { h, injectCss, later, rafCoalesce, reducedMotion, setStyle, setText, windowInterval, type Switch } from "../core/dom.ts";
import { installBaseStyles } from "../core/styles.ts";
import { applyTheme } from "../core/theme.ts";
import { accountThemeSettings } from "./accountRoot.ts";
import { fmt, plural, pretty } from "../core/format.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { loadingCount } from "../../../game/libraries.ts";
import { gameSettings } from "../../../game/settings.ts";
import type { GameInfo } from "../../../game/gameInfo.ts";
import { LightSetting } from "../../../protocol/enums.generated.ts";

/** What the overlay needs from S.MapInformation. */
export interface LoadingMapInfo {
  MapIndex: number;
  FileName: string;
  Title: string;
  /** MMap.Lib index of the big map picture (0 = none). */
  BigMap: number;
  /** MMap.Lib index of the minimap picture, the fallback background (0 = none). */
  MiniMap?: number;
  /** LightSetting. */
  Lights: number;
}

/** The game's parts the overlay uses once startGame has returned (the integrator builds it from the game). */
export interface LoadingGameLink {
  /** The game's info cache: S.NewMapInfo (NPCs, exits) and S.MapDetails (spawns, safe zones) with monster infos. */
  readonly info: GameInfo;
  /** Registers a key handler on the game's input router 'scene' layer; returns its removal. */
  sceneKeys(handler: (e: KeyboardEvent) => boolean | void): () => void;
  /** ServerRules.MapDetailsEnabled (the spawn / safe zone facts are hidden while false). */
  mapDetailsEnabled(): boolean;
}

/** One progress event source (an Emitter from src/game/emitter.ts fits). */
export interface LoadingEvent<T> { on(fn: (v: T) => void): () => void }

/** startGame's progress events, in the order they usually fire (mapLoaded and userReady may swap). */
export interface LoadingProgressEvents {
  mapInfo: LoadingEvent<LoadingMapInfo>;
  mapLoaded: LoadingEvent<unknown>;
  /** S.UserInformation: the level, or the packet itself. */
  userReady: LoadingEvent<number | { Level: number }>;
  firstFrame: LoadingEvent<unknown>;
}

export interface LoadingOptions {
  /** The character's level from the select screen (SelectInfo.Level), until S.UserInformation. */
  level?: number;
  /** A first guess at the map title (shown until S.MapInformation). */
  title?: string;
}

/** How long the art drain may hold the overlay after the first frame. */
const ART_CAP_MS = 6000;
const TICK_MS = 100;
const TIP_TICKS = 45; // 4.5 s
const FADE_MS = 700;
/** Friendly guards by AI (as src/ui/modern/windows/bigMap.ts): left out of the monster list and level range. */
const GUARD_AI = new Set([6, 57, 58, 80, 113]);
/** Harvestables / statics stored as monsters. */
const STATIC_NAME = /Tree|Mushroom|Ore|Gate|Wall|Door|Statue|Flag|Chest|Stone|Bush|Crystal/i;

const TIPS = [
  "Hold Shift while attacking to stay on the spot.", "Start a chat line with !! to talk to your group, !~ for your guild.",
  "Put potions on the belt so its keys drink them without opening your bag.", "Ctrl + right-click a player to inspect their gear.",
  "A failed gem can destroy an item; orbs never do.", "Refining needs Black Iron Ore with a purity close to your jewellery's stat.",
  "Merchants only buy the kind of goods they sell.", "Mentors earn 1% of their mentee's EXP when the mentorship ends.",
];

const LIGHTS: Readonly<Record<number, string>> = {
  [LightSetting.Normal]: "day and night", [LightSetting.Dawn]: "always dawn", [LightSetting.Day]: "always daylight",
  [LightSetting.Evening]: "always evening", [LightSetting.Night]: "always dark",
};

/** Steps with their share of the bar (the art drain fills the rest). */
type Step = "map" | "file" | "user" | "frame" | "art";
const STEPS: readonly [Step, string, number][] = [
  ["map", "Map details", 15], ["file", "Map file", 25], ["user", "Your character", 15], ["frame", "First frame", 10], ["art", "Art", 35],
];

// Performance.md: no backdrop-filter, the pan is a transform, the title / facts enter by opacity + transform, the bar
// fills by transform: scaleX, the press prompt pulses by opacity. A container query gives the narrow (<= 1100 px) layout.
const CSS = `
.hudm.ldroot { right: 0; bottom: 0; width: 100%; height: 100%; z-index: 100; pointer-events: auto; container-type: size; }
.ldroot .ldscr { position: absolute; inset: 0; overflow: hidden; background: #000; color: var(--tx); cursor: default; transition: opacity ${FADE_MS}ms; }
.ldroot .ldscr.out { opacity: 0; }
.ldroot .art { position: absolute; inset: -6%; background: center / cover no-repeat; opacity: .5; animation: ldr-pan 22s ease-in-out infinite alternate; }
@keyframes ldr-pan { from { transform: scale(1.05) translate(-2%, -1%); } to { transform: scale(1.18) translate(2%, 2%); } }
.ldroot .art.none { opacity: 1; background: radial-gradient(ellipse at 40% 40%, color-mix(in srgb, var(--ac) 25%, #0a0e18), #000 70%); animation: none; }
.ldroot .shade { position: absolute; inset: 0; background: linear-gradient(0deg, rgba(0,0,0,.92) 0%, rgba(0,0,0,.35) 35%, transparent 60%, rgba(0,0,0,.55) 100%), linear-gradient(90deg, rgba(0,0,0,.6), transparent 45%); }
.ldroot .title { position: absolute; left: 60px; top: 60px; right: 60px; }
.ldroot .title small { display: block; font-size: 11px; letter-spacing: .4em; color: var(--ac); text-shadow: 0 0 10px var(--glow); }
.ldroot .title b { display: block; margin-top: 6px; font: 900 52px/1.05 Georgia, serif; letter-spacing: .04em; text-transform: uppercase; color: #fff;
  overflow-wrap: anywhere; text-shadow: 0 4px 18px rgba(0,0,0,.8), 0 0 30px color-mix(in srgb, var(--ac) 30%, transparent); animation: ldr-in .9s ease-out both; }
.ldroot .title span { display: block; margin-top: 8px; font-size: 13px; color: var(--dim); }
@keyframes ldr-in { from { opacity: 0; transform: translateY(8px); } }
.ldroot .facts { position: absolute; left: 60px; bottom: 130px; width: 360px; display: flex; flex-direction: column; gap: 10px; animation: ldr-in .8s .2s ease-out both; }
.ldroot .row4 { display: flex; gap: 10px; }
.ldroot .fact { flex: 1; min-width: 0; padding: 8px 10px; border-radius: var(--rad); background: rgba(5,8,14,.78); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.ldroot .fact b { display: block; font-size: 20px; color: #fff; font-variant-numeric: tabular-nums; }
.ldroot .fact small { font-size: 10px; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); }
.ldroot .mons { display: flex; flex-wrap: wrap; gap: 5px; }
.ldroot .mons span { font-size: 11px; padding: 2px 9px; border-radius: 10px; background: rgba(5,8,14,.8); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.ldroot .mons span i { font-style: normal; margin-left: 5px; color: var(--dim); }
.ldroot .mons span.hi { border-color: var(--bad); } .ldroot .mons span.hi i { color: var(--bad-soft); }
.ldroot .rec { font-size: 12px; color: var(--dim); } .ldroot .rec b { color: #fff; }
.ldroot .rec b.warn { color: var(--bad-soft); } .ldroot .rec b.ok { color: var(--ok); }
.ldroot .lbl { font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); }
.ldroot .tip { position: absolute; right: 60px; bottom: 130px; width: 330px; padding: 12px 14px; border-radius: var(--rad); background: rgba(5,8,14,.78); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.ldroot .tip p { margin: 6px 0 0; font-size: 13px; line-height: 1.5; color: #fff; transition: opacity .4s; } .ldroot .tip p.out { opacity: 0; }
.ldroot .ldbar { position: absolute; left: 60px; right: 60px; bottom: 46px; display: flex; flex-direction: column; gap: 7px; }
.ldroot .ldhead { display: flex; align-items: center; gap: 12px; font-size: 13px; color: #fff; text-shadow: 0 1px 3px #000; }
.ldroot .spin { width: 92px; height: 20px; flex: none; background: left center / contain no-repeat; image-rendering: pixelated; opacity: .9; }
.ldroot .ldhead .lab { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ldroot .ldhead b { color: var(--ac); font-variant-numeric: tabular-nums; font-size: 14px; }
.ldroot .rail { position: relative; height: 6px; border-radius: 3px; overflow: hidden; background: rgba(255,255,255,.1); box-shadow: inset 0 1px 2px rgba(0,0,0,.6); }
.ldroot .fill { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 35%, #000), var(--ac)); transition: transform .35s ease-out; }
.ldroot .steps { display: flex; gap: 14px; font-size: 10px; color: var(--dim); flex-wrap: wrap; }
.ldroot .steps span::before { content: "\\25CB  "; } .ldroot .steps span.done { color: var(--tx); }
.ldroot .steps span.done::before { content: "\\2713  "; color: var(--ok); }
.ldroot .steps span.now { color: #fff; } .ldroot .steps span.now::before { content: "\\25CF  "; color: var(--ac); }
.ldroot .press { position: absolute; left: 50%; bottom: 18px; transform: translateX(-50%); white-space: nowrap; font-size: 13px; letter-spacing: .3em; color: #fff; text-shadow: 0 0 10px var(--glow); animation: ldr-press 1.4s ease-in-out infinite; }
@keyframes ldr-press { 50% { opacity: .35; } }
@container (max-width: 1100px) {
  .ldroot .title { left: 32px; top: 36px; right: 32px; } .ldroot .title b { font-size: 40px; }
  .ldroot .facts { left: 32px; width: 330px; } .ldroot .tip { right: 32px; width: 300px; }
  .ldroot .ldbar { left: 32px; right: 32px; }
}
@container (max-height: 640px) { .ldroot .tip { display: none; } }
`;


const isModifier = (k: string) => k === "Shift" || k === "Control" || k === "Alt" || k === "Meta" || k === "AltGraph" || k === "CapsLock";
const isTextEl = (t: EventTarget | null) =>
  t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement || (t instanceof HTMLElement && t.isContentEditable);

/** The loading overlay: `new LoadingOverlay(stage, { level })` on S.StartGame Result 4, then `follow(game.progress)` and
 *  `attach(link)` once startGame has returned; `dispose()` if the game ends first. Disposes itself after the player continues. */
export class LoadingOverlay {
  /** Called once after the player continued and the overlay faded out and was removed. */
  onDone: () => void = () => {};

  private readonly root: HTMLElement;
  private readonly scr: HTMLElement;
  private readonly ac = new AbortController();
  private readonly offs: (() => void)[] = [];
  private readonly done = new Set<Step>();
  private readonly startedAt = performance.now();
  private level: number;
  private map: LoadingMapInfo | null = null;
  private link: LoadingGameLink | null = null;
  private frameAt = 0;
  private peak = 0;
  private _ready = false;
  private leaving = false;
  private disposed = false;
  private spinUrls: string[] = [];
  private spinIndex = 0;
  private tickCount = 0;
  private tipIndex = Math.floor(Math.random() * TIPS.length);

  // DOM
  private readonly art: HTMLElement;
  private readonly titleName: HTMLElement;
  private readonly titleSub: HTMLElement;
  private readonly facts: HTMLElement;
  private readonly tipP: HTMLElement;
  private readonly spin: HTMLElement;
  private readonly label: HTMLElement;
  private readonly pct: HTMLElement;
  private readonly fill: HTMLElement;
  private readonly stepEls = new Map<Step, HTMLElement>();
  private readonly ticker: Switch;
  private readonly renderFacts: () => void;

  constructor(host: HTMLElement, opts: LoadingOptions = {}) {
    installBaseStyles();
    injectCss("modern-loading", CSS);
    this.level = opts.level ?? 0;
    const signal = this.ac.signal;

    this.art = h("div", "art none");
    this.titleName = h("b", "", opts.title ?? "");
    this.titleSub = h("span");
    this.facts = h("div", "facts");
    this.facts.hidden = true;
    this.tipP = h("p", "", TIPS[this.tipIndex]!);
    this.spin = h("div", "spin");
    this.label = h("span", "lab", "Entering the world…");
    this.pct = h("b", "", "0%");
    this.fill = h("div", "fill");
    const steps = h("div", "steps");
    for (const [id, text] of STEPS) { const el = h("span", "", text); this.stepEls.set(id, el); steps.append(el); }
    this.scr = h("div", "ldscr",
      this.art, h("div", "shade"),
      h("div", "title", h("small", "", "NOW ENTERING"), this.titleName, this.titleSub),
      this.facts,
      h("div", "tip", h("div", "lbl", "Tip"), this.tipP),
      h("div", "ldbar", h("div", "ldhead", this.spin, this.label, this.pct), h("div", "rail", this.fill), steps));
    this.root = h("div", "hudm ldroot", this.scr);
    this.root.setAttribute("role", "status");
    this.root.setAttribute("aria-live", "polite");
    applyTheme(this.root, accountThemeSettings(gameSettings));
    host.append(this.root);

    this.scr.addEventListener("click", () => this.continue(), { signal });
    this.renderFacts = rafCoalesce(() => this.drawFacts(), signal);
    void Promise.all(Array.from({ length: 9 }, (_, i) => libPicture(Lib.Prguse, 940 + i))).then((ps) => {
      if (this.disposed) return;
      this.spinUrls = ps.filter((p) => !!p).map((p) => p!.url);
      if (this.spinUrls.length) setStyle(this.spin, "background-image", `url("${this.spinUrls[0]}")`);
    });
    this.ticker = windowInterval(() => this.tick(), TICK_MS, signal);
    this.ticker.on();
    this.peak = loadingCount();
    this.update();
  }

  /** True once every step is done ("PRESS ANY KEY TO CONTINUE" is up). */
  get ready(): boolean { return this._ready; }

  /** Subscribes to startGame's progress events (unsubscribed on dispose). */
  follow(p: LoadingProgressEvents): void {
    if (this.disposed) return;
    this.offs.push(
      p.mapInfo.on((m) => this.mapInfo(m)),
      p.mapLoaded.on(() => this.mapLoaded()),
      p.userReady.on((u) => this.userReady(typeof u === "number" ? u : u.Level)),
      p.firstFrame.on(() => this.firstFrame()),
    );
  }

  /** Links the running game: facts from its info cache, keys through its router's 'scene' layer (removed on dispose). */
  attach(link: LoadingGameLink): void {
    if (this.disposed || this.link) return;
    this.link = link;
    this.offs.push(link.sceneKeys((e) => this.key(e)));
    this.offs.push(link.info.onChange((kind, index) => {
      if (!this.map) return;
      if (kind === "monster" || ((kind === "map" || kind === "mapDetails") && index === this.map.MapIndex)) this.renderFacts();
    }));
    if (this.map) this.requestFacts();
  }

  /** S.MapInformation. */
  mapInfo(m: LoadingMapInfo): void {
    if (this.disposed) return;
    this.map = m;
    this.done.add("map");
    setText(this.titleName, m.Title || m.FileName);
    setText(this.titleSub, `Map ${m.FileName} · ${LIGHTS[m.Lights] ?? "day and night"}`);
    const pic = m.BigMap > 0 ? m.BigMap : (m.MiniMap ?? 0);
    if (pic > 0) void libPicture(Lib.MiniMap, pic).then((p) => {
      if (this.disposed || this.map !== m) return;
      if (!p) return;
      this.art.style.backgroundImage = `url("${p.url}")`;
      this.art.classList.remove("none");
    });
    if (this.link) this.requestFacts();
    this.renderFacts();
    this.update();
  }

  /** The .map file is loaded. */
  mapLoaded(): void { if (!this.disposed) { this.done.add("file"); this.update(); } }

  /** S.UserInformation. */
  userReady(level: number): void {
    if (this.disposed) return;
    this.done.add("user");
    if (level > 0 && level !== this.level) { this.level = level; this.renderFacts(); }
    this.update();
  }

  /** The first world frame was drawn: the art drain starts (done at 0 pending or after the 6 s cap). */
  firstFrame(): void {
    if (this.disposed || this.done.has("frame")) return;
    this.done.add("frame");
    this.frameAt = performance.now();
    this.update();
  }

  /** Removes the overlay, its timers, key handler and subscriptions (does not call onDone). Safe to call twice. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.ac.abort();
    for (const off of this.offs.splice(0)) { try { off(); } catch { /* already gone */ } }
    this.root.remove();
  }

  // — internals —

  private requestFacts(): void {
    const m = this.map, l = this.link;
    if (!m || !l) return;
    l.info.requestMap(m.MapIndex);
    if (l.mapDetailsEnabled()) l.info.requestMapDetails(m.MapIndex);
  }

  /** Router 'scene' layer: before ready the world behind the overlay gets no keys; once ready a key continues. */
  private key(e: KeyboardEvent): boolean {
    if (this.disposed || isTextEl(e.target) || isTextEl(document.activeElement)) return false;
    if (isModifier(e.key)) return false;
    if (!this._ready) return true;
    e.preventDefault();
    this.continue();
    return true;
  }

  private tick(): void {
    this.tickCount++;
    if (this.spinUrls.length > 1 && !reducedMotion()) {
      this.spinIndex = (this.spinIndex + 1) % this.spinUrls.length;
      setStyle(this.spin, "background-image", `url("${this.spinUrls[this.spinIndex]}")`);
    }
    if (this.tickCount % TIP_TICKS === 0) this.nextTip();
    if (!this._ready) this.update();
  }

  private nextTip(): void {
    this.tipIndex = (this.tipIndex + 1) % TIPS.length;
    const text = TIPS[this.tipIndex]!;
    if (reducedMotion()) { setText(this.tipP, text); return; }
    this.tipP.classList.add("out");
    later(() => { setText(this.tipP, text); this.tipP.classList.remove("out"); }, 400, this.ac.signal);
  }

  /** Recomputes the bar from the steps and the art drain; flips to ready when all are done. */
  private update(): void {
    if (this.disposed || this._ready) return;
    const pending = loadingCount();
    if (pending > this.peak) this.peak = pending;
    let artShare = 0;
    if (this.done.has("frame")) {
      const since = performance.now() - this.frameAt; // one tick first: the first frame queues its misses after it is drawn
      if ((pending === 0 && since >= TICK_MS) || since >= ART_CAP_MS) this.done.add("art");
      artShare = this.peak > 0 ? (this.peak - pending) / this.peak : 1;
    } else if (this.peak > 0) {
      artShare = Math.min(0.5, (this.peak - pending) / this.peak * 0.5); // art loading before the first frame: up to half
    }
    let p = 0;
    for (const [id, , w] of STEPS) p += this.done.has(id) ? w : id === "art" ? w * artShare : 0;
    const percent = Math.min(100, Math.round(p));
    setText(this.pct, percent + "%");
    setStyle(this.fill, "transform", `scaleX(${percent / 100})`);

    const now = STEPS.find(([id]) => !this.done.has(id))?.[0];
    for (const [id] of STEPS) {
      const el = this.stepEls.get(id)!;
      const cls = this.done.has(id) ? "done" : id === now ? "now" : "";
      if (el.className !== cls) el.className = cls;
    }
    setText(this.label, this.labelFor(now, pending));
    if (!now) this.becomeReady();
  }

  private labelFor(step: Step | undefined, pending: number): string {
    switch (step) {
      case "map": return "Entering the world…";
      case "file": return `Loading map ${this.map?.FileName ?? ""}…`;
      case "user": return "Loading your character…";
      case "frame": return "Drawing the world…";
      case "art": return pending > 0 ? `Warming up the art (${fmt(pending)} left)…` : "Warming up the art…";
      default: return "Ready";
    }
  }

  private becomeReady(): void {
    this._ready = true;
    setText(this.pct, "100%");
    setStyle(this.fill, "transform", "scaleX(1)");
    this.scr.append(h("div", "press", "PRESS ANY KEY TO CONTINUE"));
    console.debug(`[loading] ready after ${Math.round(performance.now() - this.startedAt)} ms (art peak ${this.peak})`);
  }

  private continue(): void {
    if (!this._ready || this.leaving || this.disposed) return;
    this.leaving = true;
    this.ticker.off();
    const finish = () => { this.dispose(); this.onDone(); };
    if (reducedMotion()) { finish(); return; }
    this.scr.classList.add("out");
    later(finish, FADE_MS, this.ac.signal);
  }

  /** NPC / exit counts, safe zones and monsters (S.NewMapInfo, S.MapDetails), your level against the monsters'. */
  private drawFacts(): void {
    const m = this.map, l = this.link;
    if (this.disposed || !m) return;
    const map = l?.info.map(m.MapIndex);
    const details = l && l.mapDetailsEnabled() ? l.info.details(m.MapIndex) : undefined;
    const fact = (v: string, label: string) => h("div", "fact", h("b", "", v), h("small", "", label));
    const kids: HTMLElement[] = [];
    const row = h("div", "row4");
    if (map) row.append(fact(fmt(map.NPCs.length), "NPCs"), fact(fmt(map.Movements.length), "Exits"));

    if (details) {
      // Monsters by kind (numbered variants merged), most spawned first; guards, statics and level 0 / 255 left out.
      const kinds = new Map<string, [number, number]>(); // name -> [level, count]
      let spawns = 0;
      for (const r of details.respawns) {
        const info = l!.info.monster(r.MonsterIndex);
        if (!info) continue;
        const k = info.Name.replace(/\d+$/, "");
        if (GUARD_AI.has(info.AI) || info.Level >= 255 || info.Level <= 0 || STATIC_NAME.test(k)) continue;
        spawns += r.Count;
        const cur = kinds.get(k);
        kinds.set(k, [Math.max(cur?.[0] ?? 0, info.Level), (cur?.[1] ?? 0) + r.Count]);
      }
      row.append(fact(fmt(details.safeZones.length), plural(details.safeZones.length, "Safe zone")), fact(fmt(spawns), "Monsters"));
      kids.push(row);
      if (kinds.size) {
        const lvs = [...kinds.values()].map((v) => v[0]);
        const lo = Math.min(...lvs), hi = Math.max(...lvs);
        const mons = h("div", "mons");
        for (const [name, [lv, n]] of [...kinds.entries()].sort((a, b) => b[1][1] - a[1][1]).slice(0, 10)) {
          const e = h("span", this.level > 0 && lv > this.level + 5 ? "hi" : "", pretty(name), h("i", "", `Lv ${lv}`));
          e.title = `${fmt(n)} on this map`;
          mons.append(e);
        }
        kids.push(h("div", "lbl", "Who lives here"), mons);
        const over = this.level > 0 && hi > this.level + 5;
        kids.push(this.level > 0
          ? h("div", "rec", "Monsters ", h("b", "", lo === hi ? `Lv ${lo}` : `Lv ${lo}–${hi}`), " · you are ", h("b", over ? "warn" : "ok", `Lv ${this.level}`),
            over ? " · careful, some outlevel you" : " · you're ready")
          : h("div", "rec", "Monsters ", h("b", "", lo === hi ? `Lv ${lo}` : `Lv ${lo}–${hi}`)));
      } else if (details.complete) {
        kids.push(h("div", "rec", "No monsters here: a safe place."));
      }
    } else if (map) {
      kids.push(row);
    }
    if (!details && this.level > 0) kids.push(h("div", "rec", "You are ", h("b", "", `Lv ${this.level}`)));
    this.facts.replaceChildren(...kids);
    this.facts.hidden = kids.length === 0;
  }
}
