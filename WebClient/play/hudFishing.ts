// Fishing window mock for the scene lab's UI tester (window style from hudWindow.ts), a playable take on the desktop's
// FishingDialog + FishingStatusDialog. Crystal's flow: cast, wait while the chance / progress bars run (S.FishingUpdate),
// press the fish button while a fish bites (FoundFish), auto-cast needs a Reel in the rod. Here: the rod and its five
// parts (Hook / Float / Bait / Finder / Reel, Items.Lib icons, bait counted down per cast), an animated water scene (the
// float arcs out, bobs while the progress fills, dips on a bite; a REEL IN button with a shrinking timing ring; fish
// shadows under the water when a Finder is fitted; a catch card with size, rarity and "new record"), auto-cast, a fish
// journal (the server's four fish, silhouettes until caught, best size) and session stats. Item names, images and
// prices are from a copy of the live Server.MirDB (2026-10-15); chances, timings and sizes are mock.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Part = "Hook" | "Float" | "Bait" | "Finder" | "Reel";
const PARTS: Part[] = ["Hook", "Float", "Bait", "Finder", "Reel"];
/** name, image, effect line, chance bonus */
const GEAR: Record<Part, [string, number, string, number][]> = {
  Hook: [["FishingHook", 2330, "Required to catch anything", 0]],
  Float: [["FishingFloat", 2331, "+10 % catch chance", 10], ["PremiumFloat", 3783, "+20 % catch chance", 20]],
  Bait: [["FishBait", 2332, "+5 % chance, one per cast", 5], ["PremiumBait", 3784, "+15 % chance, one per cast", 15]],
  Finder: [["FishDetector", 2333, "Shows fish under the water", 5], ["PremiumFinder", 3785, "Shows fish, +10 % chance", 10]],
  Reel: [["FishingReel", 2334, "Auto-cast, +0.3 s to react", 0], ["PremiumReel", 3786, "Auto-cast, +0.6 s to react", 0]],
};
/** The server's fish: name, image, rarity, base size (cm), weight of the roll */
const FISH: [string, number, string, number, number][] = [["Walleye", 2394, "Common", 38, 45], ["Trout", 2393, "Common", 32, 30], ["RockBass", 2396, "Uncommon", 44, 17], ["SwordFish", 2395, "Rare", 120, 8]];
const RARITY_COL: Record<string, string> = { Common: "#c9d3e0", Uncommon: "#4fe07a", Rare: "#6fb6ff" };
type Phase = "idle" | "cast" | "wait" | "bite" | "caught" | "lost";
const pretty = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2");

const CSS = `
.hudm .fswin { --winh: 560px; width: 960px; }
.hudm .fswin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .fswin .fbody { flex: 1; min-height: 0; display: flex; }
.hudm .fswin .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; }
.hudm .fswin .lbl b { letter-spacing: 0; color: var(--tx); text-transform: none; font-weight: 600; }
/* Rod */
.hudm .fswin .rod { width: 230px; flex: none; padding: 12px; display: flex; flex-direction: column; gap: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); overflow-y: auto; }
.hudm .fswin .rodcard { display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: var(--rad); background: color-mix(in srgb, var(--ac) 10%, transparent); border: 1px solid color-mix(in srgb, var(--ac) 45%, transparent); }
.hudm .fswin .rodcard b { display: block; color: #fff; font-size: 13px; } .hudm .fswin .rodcard small { color: var(--dim); font-size: 10px; }
.hudm .fswin .part { display: flex; align-items: center; gap: 8px; padding: 5px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid transparent; }
.hudm .fswin .part:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .fswin .part .eq { width: 40px; height: 40px; }
.hudm .fswin .part .t { flex: 1; min-width: 0; font-size: 12px; } .hudm .fswin .part .t b { display: block; color: var(--tx); } .hudm .fswin .part .t small { color: var(--dim); font-size: 10px; }
.hudm .fswin .part.empty .t b { color: var(--dim); font-style: italic; }
.hudm .fswin .part .sw { font-size: 10px; color: var(--acd); }
.hudm .fswin .chance { margin-top: auto; }
.hudm .fswin .cbar { height: 8px; border-radius: 4px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .fswin .cbar i { display: block; height: 100%; background: linear-gradient(90deg, #2f9e5a, #4fe07a); box-shadow: 0 0 6px rgba(80, 230, 120, .6); }
/* Water scene */
.hudm .fswin .scene { position: relative; flex: 1; min-width: 0; overflow: hidden; display: flex; flex-direction: column; }
.hudm .fswin .sky { height: 90px; flex: none; background: linear-gradient(180deg, #0b1a33, #1b3a5e 70%, #2a5a7e); position: relative; }
.hudm .fswin .sky::after { content: ""; position: absolute; right: 60px; top: 18px; width: 34px; height: 34px; border-radius: 50%; background: radial-gradient(circle at 40% 40%, #fffbe0, #f2d58a); box-shadow: 0 0 30px rgba(255, 230, 150, .6); }
.hudm .fswin .water { position: relative; flex: 1; overflow: hidden; background: linear-gradient(180deg, #1d4a6e 0%, #0e2a48 40%, #06152a 100%); cursor: pointer; }
.hudm .fswin .water::before { content: ""; position: absolute; left: -50%; right: -50%; top: 0; height: 40px; opacity: .5;
  background: repeating-linear-gradient(90deg, transparent 0 30px, rgba(150, 210, 255, .25) 30px 34px, transparent 34px 70px); animation: hudm-waves 6s linear infinite; }
@keyframes hudm-waves { to { transform: translateX(70px); } }
.hudm .fswin .water::after { content: ""; position: absolute; inset: 0; background: radial-gradient(ellipse 60% 30% at 50% 0%, rgba(255, 230, 150, .12), transparent 70%); pointer-events: none; }
.hudm .fswin .glint { position: absolute; width: 18px; height: 2px; border-radius: 1px; background: rgba(255, 255, 255, .5); animation: hudm-glint2 3s ease-in-out infinite; }
@keyframes hudm-glint2 { 0%, 100% { opacity: 0; } 50% { opacity: .9; } }
.hudm .fswin .shadow { position: absolute; width: 46px; height: 14px; border-radius: 50%; background: rgba(0, 10, 20, .55); filter: blur(2px); animation: hudm-swim 7s ease-in-out infinite alternate; }
@keyframes hudm-swim { from { transform: translateX(-60px) scaleX(-1); } to { transform: translateX(60px); } }
.hudm .fswin .bobber { position: absolute; left: 50%; top: 42%; width: 22px; height: 34px; margin-left: -11px; transition: left .6s cubic-bezier(.3,.6,.4,1), top .6s cubic-bezier(.3,-.4,.6,1); }
.hudm .fswin .bobber svg { width: 100%; height: 100%; filter: drop-shadow(0 2px 3px rgba(0,0,0,.6)); }
.hudm .fswin .scene.wait .bobber svg { animation: hudm-bobf 1.6s ease-in-out infinite; }
@keyframes hudm-bobf { 50% { transform: translateY(3px) rotate(4deg); } }
.hudm .fswin .scene.bite .bobber svg { animation: hudm-dip .35s ease-in-out infinite; }
@keyframes hudm-dip { 50% { transform: translateY(12px) rotate(-10deg); } }
.hudm .fswin .ripple { position: absolute; left: 50%; top: 42%; width: 40px; height: 12px; margin: 26px 0 0 -20px; border-radius: 50%; border: 2px solid rgba(180, 225, 255, .6); animation: hudm-ripple 1.6s ease-out infinite; }
.hudm .fswin .scene.bite .ripple { animation-duration: .5s; border-color: #fff; }
@keyframes hudm-ripple { from { transform: scale(.5); opacity: .9; } to { transform: scale(2.6); opacity: 0; } }
.hudm .fswin .line { position: absolute; left: 0; top: 0; width: 100%; height: 100%; pointer-events: none; }
.hudm .fswin .hudbar { position: absolute; left: 16px; right: 16px; bottom: 14px; display: flex; flex-direction: column; gap: 6px; }
.hudm .fswin .pbar { height: 10px; border-radius: 5px; background: rgba(0,0,0,.55); border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); overflow: hidden; }
.hudm .fswin .pbar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); box-shadow: 0 0 8px var(--glow); }
.hudm .fswin .status { text-align: center; font-size: 13px; font-weight: 700; color: #fff; text-shadow: 0 1px 3px #000; }
.hudm .fswin .ctrl { display: flex; gap: 8px; justify-content: center; align-items: center; }
.hudm .fswin .ctrl .btn { height: 34px; padding: 0 22px; font-size: 13px; }
.hudm .fswin .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .fswin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .fswin .opt { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #dbe6f5; text-shadow: 0 1px 2px #000; }
.hudm .fswin .tog { width: 32px; height: 18px; border-radius: 9px; position: relative; cursor: pointer; background: rgba(0,0,0,.5); border: 1px solid var(--fr); }
.hudm .fswin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 12px; height: 12px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .fswin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); } .hudm .fswin .tog.on::after { left: 16px; background: #fff; }
.hudm .fswin .tog.off { opacity: .4; cursor: not-allowed; }
/* REEL IN */
.hudm .fswin .reel { position: absolute; left: 50%; top: 44%; transform: translate(-50%, -50%); width: 120px; height: 120px; cursor: pointer; z-index: 3; animation: hudm-reelin .2s ease-out; }
@keyframes hudm-reelin { from { transform: translate(-50%, -50%) scale(.6); opacity: 0; } }
.hudm .fswin .reel svg { position: absolute; inset: 0; }
.hudm .fswin .reel span { position: absolute; inset: 22px; border-radius: 50%; display: flex; align-items: center; justify-content: center; text-align: center; font-size: 15px; font-weight: 900; color: #fff; letter-spacing: .04em;
  background: radial-gradient(circle at 40% 35%, color-mix(in srgb, var(--ac) 80%, #fff), color-mix(in srgb, var(--ac) 50%, #000)); box-shadow: 0 0 24px var(--ac); animation: hudm-pulse2 .35s ease-in-out infinite; }
@keyframes hudm-pulse2 { 50% { transform: scale(1.08); } }
/* Catch card */
.hudm .fswin .catch { position: absolute; left: 50%; top: 46%; transform: translate(-50%, -50%); z-index: 4; width: 240px; padding: 14px; text-align: center; border-radius: var(--rad);
  background: var(--page); border: 1px solid var(--rc, var(--ac)); box-shadow: 0 0 30px color-mix(in srgb, var(--rc, var(--ac)) 60%, transparent); animation: hudm-pop2 .3s ease-out; }
.hudm .fswin .catch .big { width: 72px; height: 72px; margin: 0 auto 6px; background: center / contain no-repeat; image-rendering: pixelated; filter: drop-shadow(0 0 10px var(--rc)); }
.hudm .fswin .catch h3 { margin: 0; font-size: 18px; color: #fff; } .hudm .fswin .catch .r { font-size: 10px; font-weight: 800; letter-spacing: .14em; text-transform: uppercase; color: var(--rc); }
.hudm .fswin .catch .sz { margin-top: 4px; font-size: 13px; color: var(--tx); } .hudm .fswin .catch .rec { margin-top: 4px; font-size: 11px; font-weight: 800; color: #f2c94c; text-shadow: 0 0 8px rgba(242, 201, 76, .7); }
.hudm .fswin .catch.lost { --rc: #ff6a5a; } .hudm .fswin .catch.lost h3 { color: #ffb0a6; }
/* Journal */
.hudm .fswin .jr { width: 250px; flex: none; padding: 12px; display: flex; flex-direction: column; gap: 10px; border-left: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); overflow-y: auto; }
.hudm .fswin .fishes { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .fswin .fc { padding: 8px 6px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .fswin .fc i { display: block; width: 44px; height: 40px; margin: 0 auto 4px; background: center / contain no-repeat; image-rendering: pixelated; }
.hudm .fswin .fc.none i { filter: brightness(0) opacity(.5); }
.hudm .fswin .fc b { display: block; font-size: 11px; color: #fff; } .hudm .fswin .fc.none b { color: var(--dim); }
.hudm .fswin .fc small { font-size: 10px; color: var(--dim); display: block; } .hudm .fswin .fc .rr { font-size: 9px; font-weight: 800; letter-spacing: .1em; text-transform: uppercase; }
.hudm .fswin .stats { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .fswin .stats div { padding: 8px; text-align: center; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .fswin .stats b { display: block; font-size: 18px; color: #fff; font-variant-numeric: tabular-nums; } .hudm .fswin .stats span { font-size: 10px; color: var(--dim); }
.hudm .fswin .log { font-size: 11px; color: var(--dim); display: flex; flex-direction: column; gap: 3px; }
.hudm .fswin .log b { color: var(--tx); }
`;

export class FishingMock extends HudWindow {
  private fitted: Partial<Record<Part, number>> = { Hook: 0, Float: 0, Bait: 0, Finder: 0, Reel: 0 };
  private bait = 40;
  private phase: Phase = "idle";
  private progress = 0;
  private auto = false;
  private biteEnds = 0;
  private catchInfo: { fish: (typeof FISH)[number]; size: number; record: boolean } | null = null;
  private journal = new Map<string, { n: number; best: number }>();
  private stats = { casts: 0, caught: 0, lost: 0, streak: 0 };
  private log: string[] = [];
  private timers: number[] = [];
  private scene: HTMLElement | null = null;

  constructor() {
    super("fswin", 32, 40);
    injectCss("hudm-fishing-css", CSS);
    window.setInterval(() => this.tick(), 80);
  }
  protected override shown(): void {
    // tick() pauses while the window is closed: a bite that was showing gets its full reaction time again.
    if (this.phase === "bite") this.biteEnds = performance.now() + this.reactTime();
    this.render();
  }

  private gear(p: Part) { const i = this.fitted[p]; return i === undefined ? null : GEAR[p][i]!; }
  private chance(): number { return Math.min(95, 25 + PARTS.reduce((s, p) => s + (p === "Bait" && this.bait <= 0 ? 0 : this.gear(p)?.[3] ?? 0), 0)); }
  private reactTime(): number { const r = this.gear("Reel"); return 1400 + (r ? (r[0].startsWith("Premium") ? 600 : 300) : 0); }

  private later(ms: number, fn: () => void): void { this.timers.push(window.setTimeout(fn, ms)); }
  private clearTimers(): void { for (const t of this.timers) window.clearTimeout(t); this.timers = []; }

  private cast(): void {
    if (!this.gear("Hook")) { this.log.unshift("You need a hook to fish."); this.render(); return; }
    this.clearTimers();
    this.catchInfo = null;
    this.phase = "cast";
    this.progress = 0;
    this.stats.casts++;
    if (this.gear("Bait") && this.bait > 0) this.bait--;
    this.render();
    this.later(650, () => { this.phase = "wait"; this.render(); });
  }

  private tick(): void {
    if (!this.open || !this.scene) return;
    if (this.phase === "wait") {
      // Progress fills; the bite comes somewhere in the second half, sooner with a better chance.
      this.progress = Math.min(100, this.progress + 0.8 + this.chance() / 60 + Math.random() * 0.6);
      if (this.progress >= 55 && Math.random() < this.chance() / 900 || this.progress >= 100) {
        this.phase = "bite"; this.biteEnds = performance.now() + this.reactTime(); this.render(); return;
      }
      this.paintBars();
    } else if (this.phase === "bite") {
      const left = this.biteEnds - performance.now();
      if (left <= 0) { this.finish(false, true); return; }
      const ring = this.scene.querySelector<SVGCircleElement>(".reel .tm");
      if (ring) ring.setAttribute("stroke-dashoffset", String(339 * (1 - left / this.reactTime())));
    }
  }

  private reelIn(): void {
    if (this.phase !== "bite") return;
    // Most hooked fish land; the chance decides how often one slips the hook anyway.
    this.finish(Math.random() * 100 < this.chance() + 20, false);
  }

  private slow = false;
  private finish(caught: boolean, slow: boolean): void {
    this.slow = slow;
    if (caught) {
      const total = FISH.reduce((s, f) => s + f[4], 0);
      let roll = Math.random() * total;
      const fish = FISH.find((f) => (roll -= f[4]) < 0) ?? FISH[0]!;
      const size = Math.round(fish[3] * (0.7 + Math.random() * 0.7));
      const j = this.journal.get(fish[0]) ?? { n: 0, best: 0 };
      const record = size > j.best;
      this.journal.set(fish[0], { n: j.n + 1, best: Math.max(j.best, size) });
      this.catchInfo = { fish, size, record };
      this.stats.caught++; this.stats.streak++;
      this.log.unshift(`Caught a ${size} cm ${pretty(fish[0])}${record ? " (new record!)" : ""}`);
      this.phase = "caught";
    } else {
      this.stats.lost++; this.stats.streak = 0;
      this.log.unshift(slow ? "Too slow: the fish got away." : "It slipped the hook.");
      this.phase = "lost";
    }
    this.log = this.log.slice(0, 6);
    this.render();
    if (this.auto && this.gear("Reel")) this.later(1800, () => { if (this.open) this.cast(); });
  }

  private paintBars(): void {
    const p = this.scene?.querySelector<HTMLElement>(".pbar i");
    if (p) p.style.width = this.progress.toFixed(1) + "%";
  }

  private render(): void {
    this.el.replaceChildren(h("div", "panel", this.titleBar("FISHING", "Close (N)"), h("div", "fbody", this.rodView(), this.sceneView(), this.journalView())));
  }

  private rodView(): HTMLElement {
    const rod = h("div", "rodcard", itemCell(this.el, "BlueFishingRod", 2313), h("div", "", h("b", "", "Blue Fishing Rod"), h("small", "", "Equipped · 5 part slots")));
    const parts = h("div", "");
    for (const p of PARTS) {
      const g = this.gear(p);
      const row = h("div", "part" + (g ? "" : " empty"), g ? itemCell(this.el, pretty(g[0]), g[1], p === "Bait" ? this.bait : 0) : itemCell(this.el, "", -1),
        h("div", "t", h("b", "", g ? pretty(g[0]) : `No ${p.toLowerCase()}`), h("small", "", g ? (p === "Bait" ? `${this.bait} left · ${g[2]}` : g[2]) : `${p} slot · click to fit`)), h("span", "sw", GEAR[p].length > 1 ? "⇄" : g ? "×" : "+"));
      row.title = GEAR[p].length > 1 ? "Click to swap for the other one" : g ? "Click to take it off" : "Click to fit it";
      row.addEventListener("click", () => {
        const i = this.fitted[p];
        if (GEAR[p].length > 1) this.fitted[p] = i === undefined ? 0 : i + 1 < GEAR[p].length ? i + 1 : undefined;
        else this.fitted[p] = i === undefined ? 0 : undefined;
        if (p === "Bait" && this.fitted.Bait !== undefined && this.bait <= 0) this.bait = 40;
        if (!this.gear("Reel")) this.auto = false;
        this.render();
      });
      parts.append(row);
    }
    const ch = this.chance();
    const bar = h("div", "cbar", h("i"));
    (bar.firstChild as HTMLElement).style.width = ch + "%";
    return h("div", "rod", rod, h("div", "lbl", "Parts"), parts, h("div", "chance", h("div", "lbl", "Catch chance", h("b", "", `${ch}%`)), bar));
  }

  private sceneView(): HTMLElement {
    const scene = h("div", "scene " + this.phase);
    this.scene = scene;
    const sky = h("div", "sky");
    const water = h("div", "water");
    for (let i = 0; i < 7; i++) { const g = h("span", "glint"); Object.assign(g.style, { left: 8 + i * 13 + "%", top: 8 + ((i * 29) % 60) + "%", animationDelay: i * 0.4 + "s" }); water.append(g); }
    if (this.gear("Finder")) for (let i = 0; i < 3; i++) { const s = h("span", "shadow"); Object.assign(s.style, { left: 25 + i * 22 + "%", top: 55 + i * 9 + "%", animationDuration: 5 + i * 2 + "s" }); water.append(s); }
    // The line from the top-left (the rod tip) to the float, and the float itself.
    const out = this.phase !== "idle";
    const line = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    line.setAttribute("class", "line"); line.setAttribute("viewBox", "0 0 100 100"); line.setAttribute("preserveAspectRatio", "none");
    if (out) line.innerHTML = `<path d="M2 -40 Q 30 ${this.phase === "bite" ? 55 : 20} 50 ${this.phase === "bite" ? 50 : 44}" fill="none" stroke="rgba(230,240,255,.55)" stroke-width=".35"/>`;
    const fl = h("div", "bobber");
    fl.innerHTML = `<svg viewBox="0 0 22 34"><path d="M11 0v9" stroke="#ddd" stroke-width="1.5"/><ellipse cx="11" cy="15" rx="7" ry="7" fill="#e83a3a"/><path d="M4 15a7 7 0 0014 0z" fill="#f4f4f4"/><path d="M11 22v10" stroke="#888" stroke-width="1.5"/></svg>`;
    if (!out) Object.assign(fl.style, { left: "8%", top: "-30%" });
    water.append(line);
    if (out && this.phase !== "cast") water.append(h("span", "ripple"));
    water.append(fl);
    water.addEventListener("click", () => { if (this.phase === "idle" || this.phase === "caught" || this.phase === "lost") this.cast(); else if (this.phase === "bite") this.reelIn(); });
    // REEL IN with a timing ring
    if (this.phase === "bite") {
      const reel = h("div", "reel");
      reel.innerHTML = `<svg viewBox="0 0 120 120"><circle cx="60" cy="60" r="54" fill="none" stroke="rgba(0,0,0,.5)" stroke-width="7"/><circle class="tm" cx="60" cy="60" r="54" fill="none" style="stroke: var(--ac)" stroke-width="7" stroke-linecap="round" stroke-dasharray="339" stroke-dashoffset="0" transform="rotate(-90 60 60)"/></svg><span>REEL<br>IN!</span>`;
      reel.addEventListener("click", (e) => { e.stopPropagation(); this.reelIn(); });
      water.append(reel);
    }
    if (this.phase === "caught" && this.catchInfo) {
      const { fish, size, record } = this.catchInfo;
      const big = h("div", "big");
      void libPicture(Lib.Items, fish[1]).then((p) => { if (p) big.style.backgroundImage = `url(${p.url})`; });
      const card = h("div", "catch", big, h("div", "r", fish[2]), h("h3", "", pretty(fish[0])), h("div", "sz", `${size} cm`), record ? h("div", "rec", "★ New record!") : "");
      card.style.setProperty("--rc", RARITY_COL[fish[2]]!);
      water.append(card);
    }
    if (this.phase === "lost") water.append(h("div", "catch lost", h("h3", "", this.slow ? "It got away!" : "It slipped the hook!"), h("div", "sz", this.slow ? "Too slow. Reel in while the ring is still running." : "Better floats and bait make a fish less likely to slip.")));
    const status = { idle: "Click the water or Cast to throw your line", cast: "Casting…", wait: `Waiting for a bite… ${this.chance()}% chance`, bite: "A fish is biting!", caught: "Nice catch!", lost: "The line went slack." }[this.phase];
    const bar = h("div", "pbar", h("i"));
    (bar.firstChild as HTMLElement).style.width = this.progress.toFixed(1) + "%";
    const castBtn = h("div", "btn main" + (this.phase === "cast" || this.phase === "wait" || this.phase === "bite" ? " dis" : ""), this.phase === "caught" || this.phase === "lost" ? "Cast again" : "Cast");
    castBtn.addEventListener("click", () => this.cast());
    const stop = h("div", "btn" + (this.phase === "idle" ? " dis" : ""), "Reel up");
    stop.addEventListener("click", () => { this.clearTimers(); this.phase = "idle"; this.progress = 0; this.catchInfo = null; this.render(); });
    const hasReel = !!this.gear("Reel");
    const tog = h("div", "tog" + (this.auto ? " on" : "") + (hasReel ? "" : " off"));
    tog.title = hasReel ? "Cast again on its own after each catch" : "Auto-cast needs a reel";
    tog.addEventListener("click", () => { if (hasReel) { this.auto = !this.auto; this.render(); } });
    water.append(h("div", "hudbar", h("div", "status", status), bar, h("div", "ctrl", castBtn, stop, h("div", "opt", tog, "Auto-cast"))));
    scene.append(sky, water);
    return scene;
  }

  private journalView(): HTMLElement {
    const grid = h("div", "fishes");
    for (const [name, img, rarity] of FISH) {
      const j = this.journal.get(name);
      const i = h("i");
      void libPicture(Lib.Items, img).then((p) => { if (p) i.style.backgroundImage = `url(${p.url})`; });
      const rr = h("span", "rr", rarity); rr.style.color = RARITY_COL[rarity]!;
      grid.append(h("div", "fc" + (j ? "" : " none"), i, h("b", "", j ? pretty(name) : "???"), rr, h("small", "", j ? `${j.n} caught · best ${j.best} cm` : "not caught yet")));
    }
    const rate = this.stats.casts ? Math.round(this.stats.caught / this.stats.casts * 100) : 0;
    const stats = h("div", "stats", h("div", "", h("b", "", String(this.stats.casts)), h("span", "", "casts")), h("div", "", h("b", "", String(this.stats.caught)), h("span", "", "caught")),
      h("div", "", h("b", "", `${rate}%`), h("span", "", "success")), h("div", "", h("b", "", String(this.stats.streak)), h("span", "", "streak")));
    const log = h("div", "log");
    for (const l of this.log) log.append(h("div", "", l));
    if (!this.log.length) log.append(h("div", "", "Your catches show up here."));
    return h("div", "jr", h("div", "lbl", "Fish journal", h("b", "", `${this.journal.size} / ${FISH.length}`)), grid, h("div", "lbl", "This session"), stats, log);
  }
}
