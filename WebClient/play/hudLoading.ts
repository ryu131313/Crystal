// Loading screen mock for the scene lab's UI tester: what shows between Start game and the world (the desktop's
// SelectScene.StartGame waits on Libraries.Loaded with the Prguse 940-948 animation at 100 ms; that spinner is used here).
// Full-screen over the destination map's real art (its MMap.Lib BigMap picture from play/lab-data/maps.json, panning
// slowly), the region's name, facts from the map data (NPCs, exits, safe zones, monster kinds with their level range
// from the respawns, a suggested level against yours), a rotating tip, staged progress (server, character, map file,
// art warm-up, NPCs, monsters) with a percentage, then "press any key". Timings are mock; the map data is real.
// SelectMock's Start game opens it (hudMock.ts); Simulate > Loading screen.

import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { GUARD_AI, loadMapData, loadMonsterAi, type MapRow } from "./hudBigMap.ts";
import { h, injectCss } from "./hudWindow.ts";

const TIPS = [
  "Hold Shift while attacking to stay on the spot.", "Start a chat line with !! to talk to your group, !~ for your guild.",
  "Put potions on the belt so keys 1-6 drink them without opening your bag.", "Ctrl + right-click a player to inspect their gear.",
  "A failed gem can destroy an item; orbs never do.", "Refining needs Black Iron Ore with a purity close to your jewellery's stat.",
  "Merchants only buy the kind of goods they sell.", "Mentors earn 1% of their mentee's EXP when the mentorship ends.",
];
const pretty = (t: string) => t.replace(/_/g, " ").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/([A-Za-z])\(/g, "$1 (").replace(/\s+/g, " ").trim();

const CSS = `
.hudm .ldscr { position: absolute; left: 0; top: 0; width: 100%; height: 100%; z-index: 41; pointer-events: auto; overflow: hidden; background: #000; color: var(--tx); cursor: default; }
.hudm .ldscr .art { position: absolute; inset: -6%; background: center / cover no-repeat; filter: brightness(.5) saturate(.9); animation: hudm-ldpan 22s ease-in-out infinite alternate; }
@keyframes hudm-ldpan { from { transform: scale(1.05) translate(-2%, -1%); } to { transform: scale(1.18) translate(2%, 2%); } }
.hudm .ldscr .art.none { background: radial-gradient(ellipse at 40% 40%, color-mix(in srgb, var(--ac) 25%, #0a0e18), #000 70%); }
.hudm .ldscr .shade { position: absolute; inset: 0; background: linear-gradient(0deg, rgba(0,0,0,.92) 0%, rgba(0,0,0,.35) 35%, transparent 60%, rgba(0,0,0,.55) 100%), linear-gradient(90deg, rgba(0,0,0,.6), transparent 45%); }
.hudm .ldscr .title { position: absolute; left: 60px; top: 60px; }
.hudm .ldscr .title small { display: block; font-size: 11px; letter-spacing: .4em; color: var(--ac); text-shadow: 0 0 10px var(--glow); }
.hudm .ldscr .title b { display: block; margin-top: 6px; font: 900 52px/1 Georgia, serif; letter-spacing: .04em; color: #fff; text-shadow: 0 4px 18px rgba(0,0,0,.8), 0 0 30px color-mix(in srgb, var(--ac) 30%, transparent); animation: hudm-ldin .9s ease-out; }
@keyframes hudm-ldin { from { opacity: 0; letter-spacing: .25em; } }
.hudm .ldscr .title span { display: block; margin-top: 8px; font-size: 13px; color: var(--dim); }
.hudm .ldscr .facts { position: absolute; left: 60px; bottom: 130px; width: 360px; display: flex; flex-direction: column; gap: 10px; animation: hudm-ldup .8s .2s ease-out both; }
@keyframes hudm-ldup { from { opacity: 0; transform: translateY(12px); } }
.hudm .ldscr .row4 { display: flex; gap: 10px; }
.hudm .ldscr .fact { flex: 1; padding: 8px 10px; border-radius: var(--rad); background: rgba(5,8,14,.55); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); backdrop-filter: blur(6px); }
.hudm .ldscr .fact b { display: block; font-size: 20px; color: #fff; } .hudm .ldscr .fact small { font-size: 10px; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); }
.hudm .ldscr .mons { display: flex; flex-wrap: wrap; gap: 5px; }
.hudm .ldscr .mons span { font-size: 11px; padding: 2px 9px; border-radius: 10px; background: rgba(5,8,14,.6); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .ldscr .mons span i { font-style: normal; margin-left: 5px; color: var(--dim); } .hudm .ldscr .mons span.hi { border-color: #e05555; } .hudm .ldscr .mons span.hi i { color: #ff8a7a; }
.hudm .ldscr .rec { font-size: 12px; color: var(--dim); } .hudm .ldscr .rec b { color: #fff; } .hudm .ldscr .rec b.warn { color: #ff8a7a; } .hudm .ldscr .rec b.ok { color: #6fe08a; }
.hudm .ldscr .lbl { font-size: 10px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; color: var(--dim); }
.hudm .ldscr .tip { position: absolute; right: 60px; bottom: 130px; width: 330px; padding: 12px 14px; border-radius: var(--rad); background: rgba(5,8,14,.55); border: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); backdrop-filter: blur(6px); }
.hudm .ldscr .tip p { margin: 6px 0 0; font-size: 13px; line-height: 1.5; color: #fff; transition: opacity .4s; } .hudm .ldscr .tip p.out { opacity: 0; }
/* Progress: one row (desktop LOADING strip, stage, percent), a slim rail, the steps. Class .ldbar: the HUD has a global .hudm .bar (28 px HP / MP bar). */
.hudm .ldscr .ldbar { position: absolute; left: 60px; right: 60px; bottom: 46px; display: flex; flex-direction: column; gap: 7px; }
.hudm .ldscr .ldhead { display: flex; align-items: center; gap: 12px; font-size: 13px; color: #fff; text-shadow: 0 1px 3px #000; }
.hudm .ldscr .spin { width: 92px; height: 20px; flex: none; background: left center / contain no-repeat; image-rendering: pixelated; opacity: .9; }
.hudm .ldscr .ldhead .lab { flex: 1; } .hudm .ldscr .ldhead b { color: var(--ac); font-variant-numeric: tabular-nums; font-size: 14px; }
.hudm .ldscr .rail { position: relative; height: 6px; border-radius: 3px; overflow: hidden; background: rgba(255,255,255,.1); box-shadow: inset 0 1px 2px rgba(0,0,0,.6); }
.hudm .ldscr .fill { position: absolute; left: 0; top: 0; bottom: 0; width: 0; border-radius: 3px; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 35%, #000), var(--ac)); box-shadow: 0 0 10px var(--glow); transition: width .35s ease-out; }
.hudm .ldscr .fill::after { content: ""; position: absolute; right: 0; top: 0; bottom: 0; width: 24px; border-radius: 3px; background: linear-gradient(90deg, transparent, rgba(255,255,255,.7)); }
.hudm .ldscr .steps { display: flex; gap: 14px; font-size: 10px; color: var(--dim); flex-wrap: wrap; }
.hudm .ldscr .steps span::before { content: "○ "; } .hudm .ldscr .steps span.done { color: var(--tx); } .hudm .ldscr .steps span.done::before { content: "✓ "; color: #6fe08a; }
.hudm .ldscr .steps span.now { color: #fff; } .hudm .ldscr .steps span.now::before { content: "● "; color: var(--ac); }
.hudm .ldscr .press { position: absolute; left: 50%; bottom: 18px; translate: -50% 0; font-size: 13px; letter-spacing: .3em; color: #fff; text-shadow: 0 0 10px var(--glow); animation: hudm-ldpress 1.4s ease-in-out infinite; }
@keyframes hudm-ldpress { 50% { opacity: .35; } }
.hudm .ldscr .mock { position: absolute; right: 14px; top: 10px; font-size: 10px; color: var(--dim); }
.hudm .ldscr.out { transition: opacity .7s; opacity: 0; }
`;

export class LoadingMock {
  readonly el: HTMLElement;
  /** Called after the player continues (and the screen has faded). */
  onDone: () => void = () => {};
  private spin: string[] = [];
  private timers: number[] = [];
  private ready = false;
  private level = 42;
  private ai: Record<string, number> = {};
  /** Bumped by each show(), so an older show still loading its data does not render over a newer one. */
  private token = 0;

  constructor() {
    injectCss("hudm-loading-css", CSS);
    this.el = h("div", "ldscr");
    this.el.hidden = true;
    void Promise.all(Array.from({ length: 9 }, (_, i) => libPicture(Lib.Prguse, 940 + i))).then((ps) => { this.spin = ps.filter((p) => !!p).map((p) => p!.url); });
    this.el.addEventListener("click", () => this.go());
    window.addEventListener("keydown", (e) => {
      // Not showing, or the HUD is hidden (Show HUD mock off), or typing in the lab panel: not ours.
      if (this.el.closest("[hidden]") || (e.target as HTMLElement).closest?.("input, textarea, select")) return;
      if (this.ready) { e.preventDefault(); e.stopImmediatePropagation(); this.go(); } else e.stopImmediatePropagation();
    }, { capture: true });
  }

  /** Loads into a map by its title or file (maps.json), for a character of this level. */
  async show(mapName: string, level: number): Promise<void> {
    const token = ++this.token;
    this.level = level; this.ready = false;
    for (const t of this.timers) window.clearTimeout(t); this.timers = [];
    this.el.hidden = false; this.el.classList.remove("out");
    this.el.closest(".hudm")?.classList.add("scene-on");
    const [maps, ai] = await Promise.all([loadMapData(), loadMonsterAi()]);
    if (token !== this.token || this.el.hidden) return; // a newer show() took over
    this.ai = ai;
    const key = mapName.replace(/[\s()]/g, "").toLowerCase();
    const m = maps.find((x) => x.f === mapName) ?? maps.find((x) => x.t.replace(/[\s()]/g, "").toLowerCase() === key) ?? maps.find((x) => x.t.replace(/[\s()]/g, "").toLowerCase().startsWith(key)) ?? maps[0];
    this.render(m);
  }

  private render(m: MapRow | undefined): void {
    const art = h("div", "art" + (m ? "" : " none"));
    if (m) void libPicture(Lib.MiniMap, m.b).then((p) => { if (p) art.style.backgroundImage = `url(${p.url})`; else art.classList.add("none"); });
    const name = m ? pretty(m.t) : "Unknown land";
    // Monsters by kind (numbered variants merged: OmaFighter0 = OmaFighter), most spawned first. Left out of the list and the
    // level range: friendly guards (by AI, see GUARD_AI; they also sit at level 255) and harvestables / statics (trees,
    // mushrooms, ores, gates: stored as monsters at level 60 or 0).
    const kinds = new Map<string, [number, number]>(); // name -> [level, count]
    for (const [mon, lv, , , , n] of m?.sp ?? []) {
      const k = mon.replace(/\d+$/, "");
      if (GUARD_AI.has(this.ai[mon] ?? -1) || lv >= 255 || /Tree|Mushroom|Ore|Gate|Wall|Door|Statue|Flag|Chest|Stone|Bush|Crystal/i.test(k) || lv <= 0) continue;
      const cur = kinds.get(k); kinds.set(k, [Math.max(cur?.[0] ?? 0, lv), (cur?.[1] ?? 0) + n]);
    }
    const lvs = [...kinds.values()].map((v) => v[0]), lo = lvs.length ? Math.min(...lvs) : 0, hi = lvs.length ? Math.max(...lvs) : 0;
    const groups = (m?.sp ?? []).reduce((n, s) => n + s[5], 0);
    const title = h("div", "title", h("small", "", "NOW ENTERING"), h("b", "", name.toUpperCase()), h("span", "", m ? `Map ${m.f} · ${m.l ? "lit at night" : "dark"} · ${(m.sz ?? []).length} safe zone${(m.sz ?? []).length === 1 ? "" : "s"}` : ""));
    const fact = (v: string | number, l: string) => h("div", "fact", h("b", "", String(v)), h("small", "", l));
    const mons = h("div", "mons");
    for (const [mon, [lv, n]] of [...kinds.entries()].sort((a, b) => b[1][1] - a[1][1]).slice(0, 10)) { const e = h("span", lv > this.level + 5 ? "hi" : "", pretty(mon), h("i", "", `Lv ${lv}`)); e.title = `${n} on this map`; mons.append(e); }
    const rec = lvs.length ? h("div", "rec", "Monsters ", h("b", "", `Lv ${lo}–${hi}`), " · you are ", h("b", hi > this.level + 5 ? "warn" : "ok", `Lv ${this.level}`), hi > this.level + 5 ? " · careful, some outlevel you" : " · you're ready") : h("div", "rec", "No monsters here: a safe place.");
    const facts = h("div", "facts", h("div", "row4", fact(m?.npc.length ?? 0, "NPCs"), fact(m?.mv.length ?? 0, "Exits"), fact(kinds.size, "Monster kinds"), fact(groups, "Monsters")), h("div", "lbl", "Who lives here"), mons, rec);
    const tipP = h("p", "", TIPS[Math.floor(Math.random() * TIPS.length)]!);
    const tip = h("div", "tip", h("div", "lbl", "Tip"), tipP);
    this.timers.push(window.setInterval(() => { tipP.classList.add("out"); window.setTimeout(() => { tipP.textContent = TIPS[Math.floor(Math.random() * TIPS.length)]!; tipP.classList.remove("out"); }, 400); }, 4500) as unknown as number);
    const spin = h("div", "spin");
    let si = 0;
    this.timers.push(window.setInterval(() => { if (this.spin.length) { spin.style.backgroundImage = `url(${this.spin[si++ % this.spin.length]})`; } }, 100) as unknown as number);
    const stages: [string, number][] = [["Connecting to the server", 10], ["Loading your character", 22], [`Loading map ${m?.f ?? "?"}.map`, 48], ["Warming up the art", 72],
      [`Placing ${m?.npc.length ?? 0} NPCs`, 84], [`Spawning ${groups} monsters`, 96], ["Entering the world", 100]];
    const stepsEl = h("div", "steps"); for (const [s] of stages) stepsEl.append(h("span", "", s.replace(/^(Loading|Placing|Spawning|Warming up|Connecting to|Entering) /, (x) => x)));
    const label = h("span", "lab", stages[0]![0]), pct = h("b", "", "0%"), fill = h("div", "fill");
    const bar = h("div", "ldbar", h("div", "ldhead", spin, label, pct), h("div", "rail", fill), stepsEl);
    this.el.replaceChildren(art, h("div", "shade"), title, facts, tip, bar, h("div", "mock", "loading timings are mock"));
    let t = 0;
    stages.forEach(([s, p], i) => {
      t += 350 + Math.random() * 450;
      this.timers.push(window.setTimeout(() => {
        label.textContent = s + (p < 100 ? "…" : ""); pct.textContent = `${p}%`; fill.style.width = p + "%";
        stepsEl.querySelectorAll("span").forEach((e, j) => { e.className = j < i ? "done" : j === i ? "now" : ""; });
        if (p === 100) { stepsEl.querySelectorAll("span").forEach((e) => e.className = "done"); this.ready = true; this.el.append(h("div", "press", "PRESS ANY KEY TO CONTINUE")); }
      }, t));
    });
  }

  private go(): void {
    if (!this.ready || this.el.hidden) return;
    this.ready = false;
    this.el.classList.add("out");
    window.setTimeout(() => {
      for (const t of this.timers) { window.clearTimeout(t); window.clearInterval(t); }
      this.el.hidden = true; this.el.classList.remove("out"); this.el.closest(".hudm")?.classList.remove("scene-on");
      this.onDone();
    }, 700);
  }
}
