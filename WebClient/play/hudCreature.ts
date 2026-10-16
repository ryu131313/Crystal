// Creature window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// IntelligentCreatureDialog / Options / Grade dialogs: your creatures down the side (animated, fullness, expiry), the
// picked one big on a glowing pedestal with its real Prguse2 animation (creatures.ts creatureFrames: the default loop,
// and the extra loop every 8 s like CreatureButton), rename (Globals name rule, creatures.ts CREATURE_NAME), Summon /
// Dismiss / Release, a fullness ring with Feed and the "minimum fullness" rule, blackstone production (one every
// BLACKSTONE_PRODUCE_TIME = 3 h) with a countdown and Collect, pickup mode (Automatic / SemiAutomatic) with ranges, the
// item filter (creatures.ts setItemFilter: the desktop's exact rules) and minimum grade, and a live pickup feed while a
// creature is out. Mock data only.

import { IntelligentCreatureType as T, IntelligentCreaturePickupMode as PM, ItemGrade } from "../src/protocol/enums.generated.ts";
import { BLACKSTONE_PRODUCE_TIME, CREATURE_NAME, creatureFrames, defaultItemFilter, itemFilterFlags, setItemFilter } from "../src/game/creatures.ts";
import type { IntelligentCreatureItemFilter } from "../src/protocol/server.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

interface Creature {
  type: number; name: string; fullness: number; expires: string; summoned: boolean;
  mode: number; autoRange: number; semiRange: number; minFull: number; filter: IntelligentCreatureItemFilter; grade: number;
  stoneStart: number; stones: number; picked: number;
}
const TYPE_NAME: Record<number, string> = Object.fromEntries(Object.entries(T).map(([k, v]) => [v, k.replace(/([a-z])([A-Z])/g, "$1 $2")]));
const FILTERS = ["All items", "Gold", "Weapons", "Armours", "Helmets", "Boots", "Belts", "Jewellery", "Others"];
const GRADES: [number, string][] = [[ItemGrade.None, "Any grade"], [ItemGrade.Common, "Common +"], [ItemGrade.Rare, "Rare +"], [ItemGrade.Legendary, "Legendary +"], [ItemGrade.Mythical, "Mythical +"]];
const LOOT = ["1,240 gold", "SunPotion", "(HP)DrugLarge", "320 gold", "BronzeSword", "TownTeleport", "WhiteGoldRing", "85 gold", "SoulGem", "Amulet x5"];
const mk = (type: number, name: string, fullness: number, expires: string, summoned: boolean, stoneAgo: number, stones: number): Creature => ({
  type, name, fullness, expires, summoned, mode: PM.SemiAutomatic, autoRange: 5, semiRange: 3, minFull: 2500, filter: defaultItemFilter(), grade: ItemGrade.None,
  stoneStart: performance.now() - stoneAgo * 1000, stones, picked: 0 });
const CREATURES: Creature[] = [
  mk(T.BlackKitten, "Shadow", 7400, "Permanent", true, 6100, 2), mk(T.BabyDragon, "Ember", 4200, "12 days", false, 900, 0),
  mk(T.BabyPig, "Porkchop", 1800, "3 days", false, 10200, 1), mk(T.Frog, "Hops", 9600, "Permanent", false, 2400, 0),
];
const MAX_FULL = 10000;

const CSS = `
.hudm .cwin2 { --winh: 580px; width: 940px; }
.hudm .cwin2 .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .cwin2 .cbody { flex: 1; min-height: 0; display: flex; }
.hudm .cwin2 .list { width: 210px; flex: none; padding: 10px; display: flex; flex-direction: column; gap: 6px; overflow-y: auto; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .cwin2 .ccard { position: relative; display: flex; align-items: center; gap: 10px; padding: 8px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .cwin2 .ccard:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .cwin2 .ccard.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .cwin2 .mini { width: 52px; height: 52px; flex: none; border-radius: 50%; background: center / auto no-repeat, radial-gradient(circle at 50% 60%, color-mix(in srgb, var(--ac) 20%, transparent), var(--pb) 70%);
  image-rendering: pixelated; border: 1px solid var(--fr); }
.hudm .cwin2 .ccard .t { flex: 1; min-width: 0; } .hudm .cwin2 .ccard b { display: block; font-size: 13px; color: #fff; } .hudm .cwin2 .ccard small { font-size: 10px; color: var(--dim); }
.hudm .cwin2 .fb { height: 4px; margin-top: 5px; border-radius: 2px; background: #04060a; overflow: hidden; } .hudm .cwin2 .fb i { display: block; height: 100%; }
.hudm .cwin2 .out { position: absolute; right: 6px; top: 6px; font-size: 8px; font-weight: 800; letter-spacing: .08em; padding: 1px 5px; border-radius: 6px; color: #fff; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .cwin2 .pearls { margin-top: auto; padding: 8px; border-radius: var(--rad); font-size: 11px; color: var(--dim); text-align: center; background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .cwin2 .pearls b { font-size: 16px; color: #e8f4ff; text-shadow: 0 0 8px rgba(180, 220, 255, .8); }
/* Centre */
.hudm .cwin2 .stage { width: 320px; flex: none; display: flex; flex-direction: column; align-items: center; padding: 14px 12px; gap: 10px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .cwin2 .ped { position: relative; width: 250px; height: 210px; display: flex; align-items: center; justify-content: center; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 62%, color-mix(in srgb, var(--ac) 28%, transparent), transparent 62%), color-mix(in srgb, var(--pb) 75%, #000); }
.hudm .cwin2 .ped::before { content: ""; position: absolute; bottom: 30px; left: 50%; width: 150px; height: 26px; transform: translateX(-50%); border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 50%, transparent), transparent 70%); }
.hudm .cwin2 .ped::after { content: ""; position: absolute; inset: 0; background: radial-gradient(circle, transparent 55%, rgba(0,0,0,.6)); pointer-events: none; }
.hudm .cwin2 .ped .sprite { position: relative; width: 200px; height: 170px; background: center 70% / auto no-repeat; image-rendering: pixelated; transform: scale(2); transform-origin: center 70%; }
.hudm .cwin2 .ped .spark { position: absolute; width: 4px; height: 4px; border-radius: 50%; background: #fff; box-shadow: 0 0 6px var(--ac); animation: hudm-sparkle 3s ease-in-out infinite; }
@keyframes hudm-sparkle { 0%, 100% { opacity: 0; transform: translateY(0); } 50% { opacity: 1; transform: translateY(-14px); } }
.hudm .cwin2 .nameRow { display: flex; align-items: center; gap: 8px; }
.hudm .cwin2 .nameRow h3 { margin: 0; font-size: 20px; color: #fff; }
.hudm .cwin2 .pen { cursor: pointer; color: var(--dim); font-size: 13px; } .hudm .cwin2 .pen:hover { color: #fff; }
.hudm .cwin2 .stage .type { font-size: 11px; color: var(--dim); margin-top: -6px; }
.hudm .cwin2 input.in { height: 28px; box-sizing: border-box; padding: 0 8px; font: 13px "Segoe UI", system-ui, sans-serif; color: var(--tx); width: 170px;
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--ac); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .cwin2 .err { font-size: 11px; color: #ff8a6a; min-height: 14px; }
.hudm .cwin2 .acts { display: flex; gap: 6px; width: 100%; }
.hudm .cwin2 .acts .btn { flex: 1; height: 30px; justify-content: center; }
.hudm .cwin2 .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .cwin2 .btn.warn { border-color: #b33; color: #fbb; }
.hudm .cwin2 .btn.dis { opacity: .4; pointer-events: none; }
.hudm .cwin2 .stage .lbl { width: 100%; margin-bottom: 0; }
.hudm .cwin2 .feed { width: 100%; flex: 1; min-height: 0; overflow: hidden; display: flex; flex-direction: column; gap: 3px; font-size: 11px; }
.hudm .cwin2 .feed div { color: var(--dim); animation: hudm-feedin .4s ease-out; } .hudm .cwin2 .feed div b { color: var(--tx); }
@keyframes hudm-feedin { from { opacity: 0; transform: translateX(-8px); } }
/* Right */
.hudm .cwin2 .cfg { flex: 1; min-width: 0; padding: 12px 14px; display: flex; flex-direction: column; gap: 12px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .cwin2 .lbl { display: flex; justify-content: space-between; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; }
.hudm .cwin2 .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .cwin2 .gauges { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.hudm .cwin2 .gauge { display: flex; align-items: center; gap: 12px; padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .cwin2 .ring { width: 70px; height: 70px; flex: none; }
.hudm .cwin2 .gauge .t { flex: 1; min-width: 0; font-size: 11px; color: var(--dim); } .hudm .cwin2 .gauge .t b { display: block; font-size: 15px; color: #fff; margin-bottom: 2px; }
.hudm .cwin2 .gauge .btn { display: flex; width: max-content; margin-top: 6px; height: 24px; }
.hudm .cwin2 .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .cwin2 .seg div { flex: 1; height: 28px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-size: 12px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .cwin2 .seg div + div { border-left: 1px solid var(--fr); }
.hudm .cwin2 .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 28%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .cwin2 .hint { font-size: 11px; color: var(--dim); margin-top: 5px; }
.hudm .cwin2 .sl { display: flex; align-items: center; gap: 10px; font-size: 12px; color: var(--tx); margin-top: 6px; }
.hudm .cwin2 .sl span { width: 120px; color: var(--dim); }
.hudm .cwin2 input[type=range] { -webkit-appearance: none; appearance: none; flex: 1; height: 4px; border-radius: 2px; outline: none; cursor: pointer;
  background: linear-gradient(90deg, var(--ac) 0 var(--p, 50%), color-mix(in srgb, var(--fr) 70%, #000) var(--p, 50%) 100%); }
.hudm .cwin2 input[type=range]::-webkit-slider-thumb { -webkit-appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #fff; border: 2px solid var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .cwin2 .sl b { width: 54px; text-align: right; font-variant-numeric: tabular-nums; }
.hudm .cwin2 .chips { display: flex; flex-wrap: wrap; gap: 5px; }
.hudm .cwin2 .chip { height: 24px; padding: 0 10px; display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 12px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .cwin2 .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .cwin2 .chip.on::before { content: "✓"; color: var(--ac); }
.hudm .cwin2 select { height: 28px; padding: 0 8px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx); background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr);
  border-radius: calc(var(--rad) * .8); outline: none; }
`;

export class CreatureMock extends HudWindow {
  private creatures = CREATURES.map((c) => ({ ...c, filter: { ...c.filter } }));
  private picked = 0;
  private renaming = false;
  private renameErr = "";
  private confirmRelease = false;
  private pearls = 12;
  private feed: string[] = [];
  private frame = 0;
  private extra = false;
  private nextExtra = 0;
  private frameUrls = new Map<number, string>();
  private sprite: HTMLElement | null = null;
  private minis: { el: HTMLElement; type: number }[] = [];
  private stoneEl: HTMLElement | null = null;

  constructor() {
    super("cwin2", 42, 30);
    injectCss("hudm-creature-css", CSS);
    window.setInterval(() => this.animate(), 100);
    window.setInterval(() => { if (this.open) this.tickLive(); }, 1000);
  }

  protected override shown(): void { this.render(); }
  private cur(): Creature | undefined { return this.creatures[this.picked]; }

  /** Prguse2 frame URL (preloaded; undefined until decoded). */
  private url(i: number): string | undefined {
    if (!this.frameUrls.has(i)) { this.frameUrls.set(i, ""); void libPicture(Lib.Prguse2, i).then((p) => { if (p) this.frameUrls.set(i, p.url); }); }
    return this.frameUrls.get(i) || undefined;
  }

  /** CreatureButton.SetCreatureFrames: the default loop, and the extra loop once every 8 s. */
  private animate(): void {
    const c = this.cur();
    if (!this.open || !c) return;
    const now = performance.now();
    const f = creatureFrames(c.type);
    if (this.sprite) {
      const delay = this.extra ? f.exDelay : f.defDelay;
      const count = this.extra ? f.exCount : f.defCount;
      const tick = Math.floor(now / delay);
      if (tick !== this.frame) {
        this.frame = tick;
        if (!this.extra && now >= this.nextExtra && f.exCount > 0) { this.extra = true; this.frameStart = tick; }
        const step = tick - this.frameStart;
        if (this.extra && step >= count) { this.extra = false; this.nextExtra = now + 8000; }
        const idx = this.extra ? f.exIdx + (step % f.exCount) : f.defIdx + (tick % f.defCount);
        const u = this.url(idx);
        if (u) this.sprite.style.backgroundImage = `url(${u})`;
      }
    }
    for (const m of this.minis) {
      const mf = creatureFrames(m.type);
      const u = this.url(mf.defIdx + (Math.floor(now / mf.defDelay) % mf.defCount));
      if (u) m.el.style.backgroundImage = `url(${u}), radial-gradient(circle at 50% 60%, color-mix(in srgb, var(--ac) 20%, transparent), var(--pb) 70%)`;
    }
  }
  private frameStart = 0;

  /** Fullness drains while summoned, blackstones accrue, and the summoned creature picks things up. */
  private tickLive(): void {
    for (const c of this.creatures) if (c.summoned) c.fullness = Math.max(0, c.fullness - 6);
    const out = this.creatures.find((c) => c.summoned);
    if (out && out.fullness > out.minFull && Math.random() < 0.35) {
      const what = LOOT[Math.floor(Math.random() * LOOT.length)]!;
      this.feed.unshift(`${out.name} picked up ${what}`);
      this.feed = this.feed.slice(0, 8);
      out.picked++;
      if (this.picked === this.creatures.indexOf(out)) this.renderFeed();
    }
    this.renderStone();
  }

  private render(): void {
    const c = this.cur();
    if (!c) {
      this.sprite = null; this.stoneEl = null; this.minis = [];
      this.el.replaceChildren(h("div", "panel", this.titleBar("CREATURES", "Close (E)"), h("div", "cbody", h("div", "list", h("div", "pearls", h("b", "", `◈ ${this.pearls}`), h("div", "", "No creatures. Spend pearls on a new one."))))));
      return;
    }
    // List
    this.minis = [];
    const list = h("div", "list");
    this.creatures.forEach((x, i) => {
      const mini = h("div", "mini");
      this.minis.push({ el: mini, type: x.type });
      const fb = h("div", "fb", h("i"));
      const fi = fb.firstChild as HTMLElement;
      fi.style.width = (x.fullness / MAX_FULL * 100) + "%";
      fi.style.background = x.fullness < 2500 ? "#ff5a4a" : "linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac))";
      const card = h("div", "ccard" + (i === this.picked ? " sel" : ""), mini, h("div", "t", h("b", "", x.name), h("small", "", `${TYPE_NAME[x.type]} · ${x.expires}`), fb));
      if (x.summoned) card.append(h("span", "out", "OUT"));
      card.addEventListener("click", () => { this.picked = i; this.renaming = false; this.confirmRelease = false; this.extra = false; this.nextExtra = performance.now() + 3000; this.render(); });
      list.append(card);
    });
    list.append(h("div", "pearls", h("b", "", `◈ ${this.pearls}`), h("div", "", "Pearls · spend them on new creatures")));
    // Stage
    this.sprite = h("div", "sprite");
    const ped = h("div", "ped", this.sprite);
    for (let i = 0; i < 6; i++) { const s = h("span", "spark"); Object.assign(s.style, { left: 30 + i * 36 + "px", top: 60 + ((i * 37) % 70) + "px", animationDelay: i * 0.5 + "s" }); ped.append(s); }
    const nameRow = h("div", "nameRow");
    const err = h("div", "err", this.renameErr);
    if (this.renaming) {
      const inp = h("input", "in");
      inp.value = c.name; inp.maxLength = 15;
      const ok = h("div", "btn main", "Save");
      const save = () => {
        if (!CREATURE_NAME.test(inp.value)) { this.renameErr = "3-15 letters or numbers."; this.render(); return; }
        c.name = inp.value; this.renaming = false; this.renameErr = ""; this.render();
      };
      inp.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Enter") save(); if (e.key === "Escape") { this.renaming = false; this.render(); } });
      ok.addEventListener("click", save);
      nameRow.append(inp, ok);
      setTimeout(() => inp.focus({ preventScroll: true }), 0);
    } else {
      const pen = h("span", "pen", "✎"); pen.title = "Rename";
      pen.addEventListener("click", () => { this.renaming = true; this.renameErr = ""; this.render(); });
      nameRow.append(h("h3", "", c.name), pen);
    }
    const summon = h("div", "btn main", c.summoned ? "Dismiss" : "Summon");
    summon.addEventListener("click", () => {
      const was = c.summoned;
      for (const x of this.creatures) x.summoned = false;
      c.summoned = !was;
      this.feed = c.summoned ? [`${c.name} is following you.`] : [`${c.name} went home.`];
      this.render();
    });
    const release = h("div", "btn " + (this.confirmRelease ? "warn" : ""), this.confirmRelease ? "Really release?" : "Release");
    release.addEventListener("click", () => {
      if (!this.confirmRelease) { this.confirmRelease = true; this.render(); return; }
      this.creatures.splice(this.picked, 1); this.picked = 0; this.confirmRelease = false; this.render();
    });
    const feed = h("div", "feed");
    const stage = h("div", "stage", ped, nameRow, h("div", "type", `${TYPE_NAME[c.type]} · ${c.expires}${c.summoned ? " · following you" : ""}`), err, h("div", "acts", summon, release), h("div", "lbl", "Recent pickups", h("b", "count", `${c.picked} this session`)), feed);
    this.el.replaceChildren(h("div", "panel", this.titleBar("CREATURES", "Close (E)"), h("div", "cbody", list, stage, this.config(c))));
    this.renderFeed();
    this.renderStone();
  }

  private renderFeed(): void {
    const feed = this.el.querySelector(".feed");
    if (!feed) return;
    const count = this.el.querySelector(".stage .count"), c = this.cur();
    if (count && c) count.textContent = `${c.picked} this session`;
    feed.replaceChildren(...(this.feed.length ? this.feed.map((t) => { const [who, rest] = [t.split(" ")[0]!, t.slice(t.indexOf(" "))]; return h("div", "", h("b", "", who), rest); }) : [h("div", "", "Summon a creature to see what it collects.")]));
  }

  private ring(frac: number, colour: string): SVGElement {
    const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    s.setAttribute("viewBox", "0 0 70 70"); s.setAttribute("class", "ring");
    const r = 29, len = 2 * Math.PI * r;
    s.innerHTML = `<circle cx="35" cy="35" r="${r}" fill="none" style="stroke: color-mix(in srgb, var(--fr) 60%, #000)" stroke-width="7"/>`
      + `<circle cx="35" cy="35" r="${r}" fill="none" style="stroke: ${colour}; filter: drop-shadow(0 0 4px ${colour})" stroke-width="7" stroke-linecap="round" stroke-dasharray="${len * frac} ${len}" transform="rotate(-90 35 35)"/>`
      + `<text x="35" y="40" text-anchor="middle" font-size="14" font-weight="700" fill="#fff">${Math.round(frac * 100)}%</text>`;
    return s;
  }

  private renderStone(): void {
    const c = this.cur();
    if (!this.stoneEl || !c) return;
    const elapsed = (performance.now() - c.stoneStart) / 1000;
    while (elapsed >= BLACKSTONE_PRODUCE_TIME * (c.stones + 1) && c.stones < 5) c.stones++;
    const into = elapsed % BLACKSTONE_PRODUCE_TIME, left = BLACKSTONE_PRODUCE_TIME - into;
    const p = (n: number) => String(Math.floor(n)).padStart(2, "0");
    const collect = h("div", "btn" + (c.stones ? " main" : " dis"), c.stones ? `Collect ${c.stones}` : "None yet");
    collect.addEventListener("click", () => { this.feed.unshift(`${c.name} handed over ${c.stones} blackstone${c.stones === 1 ? "" : "s"}`); c.stoneStart = performance.now() - into * 1000; c.stones = 0; this.render(); });
    this.stoneEl.replaceChildren(this.ring(into / BLACKSTONE_PRODUCE_TIME, "#b48cff"),
      h("div", "t", h("b", "", `${c.stones} ready`), `Next in ${p(left / 3600)}:${p((left % 3600) / 60)}:${p(left % 60)}`, collect));
  }

  private config(c: Creature): HTMLElement {
    // Fullness
    const fullCol = c.fullness < c.minFull ? "#ff5a4a" : "var(--ac)";
    const feed = h("div", "btn main", "Feed");
    feed.addEventListener("click", () => { c.fullness = Math.min(MAX_FULL, c.fullness + 2500); this.feed.unshift(`${c.name} ate happily (+25 %)`); this.render(); });
    const full = h("div", "gauge", this.ring(c.fullness / MAX_FULL, fullCol), h("div", "t", h("b", "", c.fullness < c.minFull ? "Hungry!" : "Fullness"), `${Math.round(c.fullness / 100)} / 100 · min ${Math.round(c.minFull / 100)}`, feed));
    this.stoneEl = h("div", "gauge");
    // Pickup mode + ranges
    const seg = h("div", "seg");
    for (const [v, label] of [[PM.Automatic, "Automatic"], [PM.SemiAutomatic, "Semi-automatic"]] as [number, string][]) {
      const d = h("div", c.mode === v ? "on" : "", label);
      d.addEventListener("click", () => { c.mode = v; this.render(); });
      seg.append(d);
    }
    const slider = (label: string, min: number, max: number, step: number, get: () => number, set: (v: number) => void, fmt: (v: number) => string) => {
      const r = h("input");
      Object.assign(r, { type: "range", min: String(min), max: String(max), step: String(step), value: String(get()) });
      const out = h("b", "", fmt(get()));
      const paint = () => r.style.setProperty("--p", ((Number(r.value) - min) / (max - min) * 100) + "%");
      paint();
      r.addEventListener("input", () => { set(Number(r.value)); out.textContent = fmt(Number(r.value)); paint(); });
      r.addEventListener("keydown", (e) => e.stopPropagation());
      return h("div", "sl", h("span", "", label), r, out);
    };
    const pickup = h("div", "", h("div", "lbl", "Pickup"), seg,
      h("div", "hint", c.mode === PM.Automatic ? "Collects everything that matches the filter around it, on its own." : "Collects what you point at (X) or around you on Alt+A, within its range."),
      c.mode === PM.Automatic ? slider("Auto range", 1, 10, 1, () => c.autoRange, (v) => { c.autoRange = v; }, (v) => `${v} cells`) : slider("Semi-auto range", 1, 10, 1, () => c.semiRange, (v) => { c.semiRange = v; }, (v) => `${v} cells`),
      slider("Minimum fullness", 0, 9000, 500, () => c.minFull, (v) => { c.minFull = v; }, (v) => `${Math.round(v / 100)} %`));
    // Filter (the desktop's exact rules) + grade
    const flags = itemFilterFlags(c.filter);
    const chips = h("div", "chips");
    FILTERS.forEach((label, i) => {
      const ch = h("div", "chip" + (flags[i] ? " on" : ""), label);
      ch.addEventListener("click", () => { setItemFilter(c.filter, i); this.render(); });
      chips.append(ch);
    });
    const grade = h("select");
    for (const [v, label] of GRADES) grade.append(Object.assign(h("option", "", label), { value: String(v) }));
    grade.value = String(c.grade);
    grade.addEventListener("change", () => { c.grade = Number(grade.value); });
    const filter = h("div", "", h("div", "lbl", "Collect", h("b", "", flags[0] ? "everything" : `${flags.filter(Boolean).length} kinds`)), chips,
      h("div", "sl", h("span", "", "Minimum grade"), grade), h("div", "hint", "Ticking every kind turns into “All items”; unticking the last one does too (the desktop's rules)."));
    return h("div", "cfg", h("div", "gauges", full, this.stoneEl), pickup, filter);
  }
}
