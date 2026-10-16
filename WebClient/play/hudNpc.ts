// NPC dialogue mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// NPCDialog + QuestListDialog. Pages are written in Crystal's NPC script markup and drawn the way NPCDialog parses it:
// <Text/@label> inline links, <<Text/@label>> big buttons (here numbered option cards, keys 1-9), {Text/COLOUR} colours,
// [ITEM:n|Name] item links (the item tooltip shows on hover), <$WEAPON> / <$USERNAME> / <$GOLD> variables. Lines are
// paraphrased from this server's own scripts (Envir/NPCs/BichonProvince: Blacksmith Bill, Teleport Jason, High Priest
// Jude), and the teleport destinations are those scripts' MOVE lines (2,000 gold), which move the lab camera.
// The NPC is drawn from its own library (Data/NPC/NNN.Lib, the image from maps.json): the standing loop (frames
// 0-3, 450 ms, FrameSet.DefaultNPC) on a pedestal, and the Harvest frames (12-21) as a gesture on each new page. Text
// types out (click or Space finishes it); Back and a conversation log; services (buy, sell, repair, storage, teleport)
// open the other mocks; a Quests tab with the NPC's quests (! available, … in progress, ? ready) to accept, abandon or
// hand in. Quests, rewards and the services' prices are mock.

import { libPicture, type LibPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";
import type { WorldHost } from "./hudMinimap.ts";

export interface NpcHost {
  gold(): number;
  level(): number;
  spend(n: number): boolean;
  weapon(): string;
  world(): WorldHost | undefined;
  open(service: "buy" | "sell" | "repair" | "storage" | "srepair" | "disassemble" | "downgrade" | "reset"): void;
  toast(text: string): void;
}
type Act = (m: NpcMock) => string | void; // returns a page to go to
interface Npc { id: string; name: string; title: string; image: number; dir: number; services: [string, string][]; pages: Record<string, string>; acts: Record<string, Act>; quests: Quest[] }
interface Quest { name: string; level: number; kind: string; state: "available" | "active" | "ready" | "done"; text: string; goal: string; reward: string; progress?: [number, number] }

const COLOURS: Record<string, string> = { CORAL: "#ff8a6a", GOLD: "#f2c94c", YELLOW: "#ffe066", RED: "#ff6a6a", GREEN: "#6fe08a", LIME: "#a6f06a", CYAN: "#6ae0f0", ORANGE: "#ff9a3c", WHITE: "#fff", SKYBLUE: "#7fc8ff" };
const teleport = (file: string, x: number, y: number, name: string): Act => (m) => m.teleport(file, x, y, name);
const NPCS: Npc[] = [
  {
    id: "bill", name: "Bill", title: "Master Artisan · Blacksmith", image: 104, dir: 1,
    services: [["Buy", "@buy"], ["Sell", "@sell"], ["Repair", "@repair"], ["Special repair", "@srepair"], ["Disassemble", "@disassemble"], ["Reset stats", "@reset"], ["Downgrade", "@downgrade"]],
    pages: {
      "@main": "Welcome traveller, my name is {Bill/GOLD}, I'm a Master Artisan.\nI have specialised in weaponry for many years now.\n\nI see you're wearing: {<$WEAPON>/CORAL}\n\n<<Buy weapons/@buy>>\n<<Sell weapons/@sell>>\n<<Repair my gear/@repair>>\n<<Special repair/@srepair>>\n<<Break an item down/@disassemble>>\n<<Reset an item's stats/@reset>>\n<<Take an awakening off/@downgrade>>\n<<Any advice?/@advice>>\n\n<Close/@exit>",
      "@srepair": "Traveller, you are lucky... we have the materials to do {special repairs/GOLD}.\nIt costs three times the normal price, but your weapon keeps its full maximum durability.\n\n<<Show me what needs repairing/@srepairbox>>\n\n<Back/@main>",
      "@advice": "A weapon is only as good as the hand that holds it... and the oil on its blade.\nA drop of [ITEM:26|BenedictionOil] can bless a weapon with luck, but too much will curse it.\n\nIf you find {Black Iron Ore/CYAN} in the mines, bring it to me. I pay well.\n\n<Back/@main>",
    },
    acts: {
      "@buy": (m) => { m.host.open("buy"); return; }, "@sell": (m) => { m.host.open("sell"); return; }, "@repair": (m) => { m.host.open("repair"); return; },
      "@disassemble": (m) => { m.host.open("disassemble"); return; }, "@reset": (m) => { m.host.open("reset"); return; }, "@downgrade": (m) => { m.host.open("downgrade"); return; },
      "@srepairbox": (m) => { m.host.open("srepair"); return; },
    },
    quests: [
      { name: "Tools of the Trade", level: 30, kind: "General", state: "available", text: "Bill needs fresh ore to keep his forge running.", goal: "Bring 5 Black Iron Ore", reward: "45,000 EXP · 8,000 Gold" },
      { name: "A Blade Reforged", level: 38, kind: "Story", state: "active", text: "Recover the broken blade pieces from the Oma Cave.", goal: "Blade pieces", reward: "120,000 EXP · [ITEM:0|DragonSword]", progress: [2, 3] },
    ],
  },
  {
    id: "jason", name: "Jason", title: "The Wandering Warrior · Teleporter", image: 119, dir: 1,
    services: [["Teleport", "@tele"]],
    pages: {
      "@main": "Hello, I'm {Jason/GOLD}, the wandering warrior.\nI transport men and goods to other places, fast and safe.\nJust pay the fee and I'll escort you anywhere.\nSo what do you say?\n\nI'll use this <Service/@tele>\nMaybe next <time/@exit>",
      "@tele": "Which place would you like to go?\n\nMy service fee is {2,000 gold/YELLOW}.\n\n<<BorderVillage/@Border>>\n<<SerpentValley/@Serpent>>\n<<MudWall/@MudWall>>\n<<TaoistSchool/@TaoVillage>>\n<<CastleGi-Ryoong/@CastleGi>>\n<<WoomyonCamp/@Camp>>\n\n<Back/@main>",
      "@gold": "{You don't have enough gold./RED}\nCome back when you can pay the fee.\n\n<Back/@main>",
      "@gone": "Hold on tight!\n\n<Close/@exit>",
    },
    acts: {
      "@Border": teleport("0", 289, 617, "BorderVillage"), "@Serpent": teleport("2", 500, 485, "SerpentValley"), "@MudWall": teleport("3", 361, 342, "MudWall"),
      "@TaoVillage": teleport("11", 163, 337, "TaoistSchool"), "@CastleGi": teleport("4", 264, 257, "CastleGi-Ryoong"), "@Camp": teleport("1", 315, 82, "WoomyonCamp"),
    },
    quests: [{ name: "Safe Passage", level: 25, kind: "Repeatable", state: "ready", text: "Escort a merchant caravan to Border Village.", goal: "Caravan escorted", reward: "30,000 EXP · 2 [ITEM:0|TownTeleport]", progress: [1, 1] }],
  },
  {
    id: "jude", name: "Jude", title: "High Priest · Bichon Province", image: 102, dir: 1,
    services: [["Storage", "@storage"]],
    pages: {
      "@main": "Don't get too close... I've lost everything...\nWho sent you?\n\n<<Nobody, I came on my own/@alone>>\n<<<$USERNAME> is my name. I came to help/@help>>\n<<Can you keep my things safe?/@storage>>\n\n<Close/@exit>",
      "@alone": "Then you are either brave or foolish.\nThe dead walk in the {Oma Cave/CYAN} again. My brothers went in... none came back.\n\n<Back/@main>",
      "@help": "Help? ...Perhaps you can.\nIf you are strong enough, look at my <quests/@quests>.\nTake this, it may keep you alive: [ITEM:0|AmuletOfRevival]\n\n<Back/@main>",
    },
    acts: { "@storage": (m) => { m.host.open("storage"); return; }, "@quests": (m) => { m.tab = "Quests"; m.render(); return; } },
    quests: [
      { name: "Shadows over Bichon", level: 35, kind: "Story", state: "available", text: "Find out what happened to the priests who entered the Oma Cave.", goal: "Search the Oma Cave", reward: "90,000 EXP · [ITEM:0|AmuletOfRevival]" },
      { name: "Rest for the Fallen", level: 40, kind: "General", state: "available", text: "Put the restless dead of the cave to rest.", goal: "Defeat 20 Oma Warriors", reward: "150,000 EXP · 20,000 Gold" },
    ],
  },
];

const CSS = `
.hudm .npwin { width: 780px; }
.hudm .npwin .panel { height: 470px; display: flex; flex-direction: column; overflow: hidden; }
.hudm .npwin .nbody { flex: 1; min-height: 0; display: flex; }
.hudm .npwin .side { width: 230px; flex: none; display: flex; flex-direction: column; align-items: center; padding: 10px; gap: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent);
  background: radial-gradient(ellipse 80% 55% at 50% 40%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%); }
.hudm .npwin .stage { position: relative; width: 210px; height: 220px; overflow: hidden; }
.hudm .npwin .stage::before { content: ""; position: absolute; left: 50%; bottom: 18px; width: 150px; height: 30px; translate: -50% 0; border-radius: 50%;
  background: radial-gradient(ellipse, color-mix(in srgb, var(--ac) 45%, transparent), transparent 70%); box-shadow: 0 0 24px color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .npwin .spr { position: absolute; left: 50%; bottom: 26px; transform-origin: 50% 100%; }
.hudm .npwin .spr img { position: absolute; image-rendering: pixelated; }
.hudm .npwin .mark { position: absolute; left: 50%; top: 4px; translate: -50% 0; font-size: 30px; font-weight: 900; color: #f2c94c; text-shadow: 0 0 10px rgba(242,201,76,.8), 0 2px 3px #000; animation: hudm-mark 1.4s ease-in-out infinite; }
.hudm .npwin .mark.rdy { color: #6fe08a; text-shadow: 0 0 10px rgba(111,224,138,.8), 0 2px 3px #000; }
@keyframes hudm-mark { 50% { translate: -50% -5px; } }
.hudm .npwin .who { text-align: center; } .hudm .npwin .who b { display: block; font-size: 20px; color: #fff; letter-spacing: .03em; text-shadow: 0 0 10px var(--glow); }
.hudm .npwin .who small { font-size: 11px; color: var(--ac); }
.hudm .npwin .svc { display: flex; flex-wrap: wrap; justify-content: center; gap: 4px; }
.hudm .npwin .svc div { padding: 3px 9px; border-radius: 10px; cursor: pointer; font-size: 11px; border: 1px solid var(--fr); background: var(--pb); }
.hudm .npwin .svc div:hover { color: #fff; border-color: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .npwin .main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .npwin .tabs { display: flex; gap: 2px; padding: 6px 12px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .npwin .tabs div { padding: 6px 12px; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .12em; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .npwin .tabs div.on { color: var(--ac); border-bottom-color: var(--ac); } .hudm .npwin .tabs b { margin-left: 5px; padding: 0 5px; border-radius: 7px; font-size: 9px; color: #000; background: #f2c94c; }
.hudm .npwin .tabs .sp { flex: 1; cursor: default; } .hudm .npwin .tabs .lg { letter-spacing: 0; font-weight: 600; }
.hudm .npwin .talk { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 18px 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .npwin .say { font-size: 14px; line-height: 1.6; color: #e9eef8; white-space: pre-wrap; cursor: pointer; min-height: 60px; }
.hudm .npwin .say .caret { display: inline-block; width: 7px; height: 14px; margin-left: 2px; vertical-align: -2px; background: var(--ac); animation: hudm-caret .8s steps(2) infinite; }
@keyframes hudm-caret { 50% { opacity: 0; } }
.hudm .npwin .say a { color: var(--ac); cursor: pointer; text-decoration: underline; text-underline-offset: 3px; font-weight: 600; } .hudm .npwin .say a:hover { color: #fff; text-shadow: 0 0 6px var(--glow); }
.hudm .npwin .say .it { cursor: help; font-weight: 700; color: #f2c94c; } .hudm .npwin .say .it::before { content: "["; opacity: .6; } .hudm .npwin .say .it::after { content: "]"; opacity: .6; }
.hudm .npwin .opts { display: flex; flex-direction: column; gap: 5px; margin-top: 12px; opacity: 0; transform: translateY(6px); transition: opacity .25s, transform .25s; }
.hudm .npwin .opts.on { opacity: 1; transform: none; }
.hudm .npwin .op { display: flex; align-items: center; gap: 10px; padding: 7px 10px; border-radius: var(--rad); cursor: pointer; font-size: 13px;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 10%, transparent), transparent); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); transition: transform .12s, border-color .12s; }
.hudm .npwin .op:hover { border-color: var(--ac); transform: translateX(4px); box-shadow: 0 0 10px color-mix(in srgb, var(--ac) 25%, transparent); color: #fff; }
.hudm .npwin .op kbd { width: 20px; height: 20px; flex: none; display: flex; align-items: center; justify-content: center; font: 700 11px ui-monospace, Consolas, monospace; border-radius: 4px; color: var(--ac); border: 1px solid var(--ac); }
.hudm .npwin .op span { flex: 1; } .hudm .npwin .op small { font-size: 10px; color: var(--dim); }
.hudm .npwin .op.dis { opacity: .45; cursor: not-allowed; } .hudm .npwin .op.dis:hover { transform: none; border-color: var(--fr); box-shadow: none; }
.hudm .npwin .op.exit { background: none; border-style: dashed; color: var(--dim); }
.hudm .npwin .foot { display: flex; align-items: center; gap: 8px; padding: 6px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); font-size: 11px; color: var(--dim); }
.hudm .npwin .foot .sp { flex: 1; } .hudm .npwin .foot b { color: #f2c94c; }
.hudm .npwin .log { padding: 6px 18px; max-height: 120px; overflow-y: auto; border-bottom: 1px solid color-mix(in srgb, var(--fr) 40%, transparent); font-size: 11px; color: var(--dim); background: #0004; }
.hudm .npwin .log div { padding: 2px 0; } .hudm .npwin .log b { color: var(--tx); }
.hudm .npwin .ql { flex: 1; min-height: 0; overflow-y: auto; padding: 10px 14px; display: flex; flex-direction: column; gap: 8px; }
.hudm .npwin .qc { display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .npwin .qc .qm { width: 30px; height: 30px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-size: 17px; font-weight: 900; border: 1px solid; }
.hudm .npwin .qm.available { color: #f2c94c; border-color: #f2c94c; } .hudm .npwin .qm.active { color: var(--dim); border-color: var(--fr); } .hudm .npwin .qm.ready { color: #6fe08a; border-color: #6fe08a; box-shadow: 0 0 8px rgba(111,224,138,.5); }
.hudm .npwin .qm.done { color: var(--dim); border-color: var(--fr); opacity: .5; }
.hudm .npwin .qc .qt { flex: 1; min-width: 0; font-size: 12px; } .hudm .npwin .qc .qt b { font-size: 13px; color: #fff; } .hudm .npwin .qc .qt small { margin-left: 6px; font-size: 10px; color: var(--dim); }
.hudm .npwin .qc .qt p { margin: 3px 0; color: var(--tx); line-height: 1.4; } .hudm .npwin .qc .qt .rw { color: #f2c94c; } .hudm .npwin .qc .qt .gl { color: var(--dim); }
.hudm .npwin .qc .bar { height: 5px; margin-top: 4px; border-radius: 3px; background: #05070c; overflow: hidden; } .hudm .npwin .qc .bar i { display: block; height: 100%; background: var(--ac); }
.hudm .npwin .qc .btns { display: flex; flex-direction: column; gap: 4px; justify-content: center; }
.hudm .npwin .btn.nmain2 { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .npwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .npwin .warp { position: absolute; inset: 0; z-index: 9; display: flex; align-items: center; justify-content: center; font-size: 18px; font-weight: 700; color: #fff; pointer-events: none;
  background: radial-gradient(circle, color-mix(in srgb, var(--ac) 50%, transparent), #000d 70%); animation: hudm-warp 1.4s ease-out forwards; text-shadow: 0 0 12px var(--glow); }
@keyframes hudm-warp { 0% { opacity: 0; } 25% { opacity: 1; } 100% { opacity: 0; } }
`;

export class NpcMock extends HudWindow {
  npc: Npc = NPCS[0]!;
  tab: "Talk" | "Quests" = "Talk";
  private page = "@main";
  private back: string[] = [];
  private history: [string, string][] = [];
  private showLog = false;
  private typing = 0;
  private finishType: (() => void) | null = null;
  private frames = new Map<number, { pics: LibPicture[]; harvest: LibPicture[]; box: [number, number, number, number] }>();
  private spr: HTMLElement | null = null;
  private frame = 0;
  private gesture = -1;
  private opts: [string, string][] = [];

  constructor(readonly host: NpcHost) {
    super("npwin", 40, 50);
    injectCss("hudm-npc-css", CSS);
    window.setInterval(() => this.animate(), 150);
    // Keys only while this is the front window; the number keys only while the pointer is over it too, so the belt keys
    // (1-6) keep working with a dialogue open, as on the desktop.
    this.el.addEventListener("mouseenter", () => { this.hover = true; });
    this.el.addEventListener("mouseleave", () => { this.hover = false; });
    window.addEventListener("keydown", (e) => {
      if (!this.open || !this.el.classList.contains("front") || (e.target as HTMLElement).closest?.("input, textarea")) return;
      if (e.code === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); this.toggle(false); return; }
      if (e.code === "Space" && this.finishType) { e.preventDefault(); e.stopImmediatePropagation(); this.finishType(); return; }
      const n = /^Digit([1-9])$/.exec(e.code);
      if (n && this.hover && this.tab === "Talk" && !this.finishType) {
        const o = this.opts[+n[1]! - 1];
        if (o) { e.preventDefault(); e.stopImmediatePropagation(); this.choose(o[1], o[0]); }
      }
    }, { capture: true });
  }
  static readonly ids = NPCS.map((n) => [n.id, n.name] as const);
  private hover = false;
  /** Bumped on each new page; a redraw of the same page (Log, tabs) neither replays the typing nor logs it again. */
  private seq = 0;
  private shownSeq = -1;

  /** Opens the dialogue with an NPC (lab Simulate buttons). */
  talk(id: string): void {
    this.npc = NPCS.find((n) => n.id === id) ?? NPCS[0]!;
    this.page = "@main"; this.back = []; this.history = []; this.tab = "Talk"; this.gesture = 0; this.seq++;
    this.toggle(true);
    this.render();
  }
  protected override shown(): void { this.render(); }

  // — Script —
  choose(label: string, text: string): void {
    if (label === "@exit") { this.toggle(false); return; }
    this.history.push(["You", text]);
    const act = this.npc.acts[label];
    let next: string | void = label;
    if (act) next = act(this);
    if (!next) return;
    if (this.npc.pages[next]) { if (label !== "@main" && next !== this.page) this.back.push(this.page); if (next === "@main") this.back = []; this.page = next; this.gesture = 0; this.seq++; this.render(); }
  }
  teleport(file: string, x: number, y: number, name: string): string {
    if (!this.host.spend(2000)) return "@gold";
    const w = this.host.world();
    // After the "@gone" page has rendered (a render replaces the panel).
    window.setTimeout(() => this.el.querySelector(".panel")?.append(h("div", "warp", `Teleporting to ${name}…`)));
    window.setTimeout(() => {
      if (w) {
        if (w.map() === file) w.walk(x, y);
        else { w.travel(file); let n = 0; const t = window.setInterval(() => { if (w.map() === file || ++n > 50) { window.clearInterval(t); w.walk(x, y); } }, 200); }
      }
      this.host.toast(`Teleported to ${name} (${x}, ${y}) for 2,000 gold.`);
      this.toggle(false);
    }, 900);
    return "@gone";
  }

  /** NPCDialog's markup → DOM. Big buttons are collected into `opts`. */
  private markup(src: string): { nodes: Node[]; opts: [string, string][] } {
    const opts: [string, string][] = [];
    let text = src.replace(/<\$WEAPON>/g, `[ITEM:0|${this.host.weapon()}]`).replace(/<\$USERNAME>/g, "GMRyu").replace(/<\$GOLD>/g, this.host.gold().toLocaleString("en-GB"));
    text = text.replace(/<<(.*?)\/(@.*?)>>\n?/g, (_, t: string, l: string) => { opts.push([t, l]); return ""; });
    // A line of only inline links (e.g. Close / Back) becomes options too, like the desktop's bottom buttons.
    const lines = text.split("\n");
    const keep: string[] = [];
    for (const ln of lines) {
      const only = ln.replace(/<([^<>]*?)\/(@[^<>]*?)>/g, "").trim();
      if (!only && /<[^<>]*?\/@[^<>]*?>/.test(ln)) { for (const m of ln.matchAll(/<([^<>]*?)\/(@[^<>]*?)>/g)) opts.push([m[1]!, m[2]!]); }
      else keep.push(ln);
    }
    text = keep.join("\n").replace(/\n{3,}/g, "\n\n").trim();
    const nodes: Node[] = [];
    const re = /<([^<>]*?)\/(@[^<>]*?)>|\{([^{}]*?)\/([A-Za-z]+)\}|\[ITEM:\d+\|([^\]]+)\]/g;
    let at = 0;
    for (const m of text.matchAll(re)) {
      if (m.index! > at) nodes.push(document.createTextNode(text.slice(at, m.index)));
      if (m[2]) { const a = h("a", "", m[1]!); const lbl = m[2], t = m[1]!; a.addEventListener("click", (e) => { e.stopPropagation(); this.choose(lbl, t); }); nodes.push(a); }
      else if (m[4]) {
        // {Text/COLOUR} may hold an item link (e.g. {<$WEAPON>/CORAL}).
        const s = h("span", "");
        const im = /^\[ITEM:\d+\|([^\]]+)\]$/.exec(m[3]!);
        if (im) { const it = h("span", "it", im[1]!); it.dataset.item = im[1]!; s.append(it); } else s.textContent = m[3]!;
        s.style.color = COLOURS[m[4].toUpperCase()] ?? m[4].toLowerCase(); nodes.push(s);
      }
      else { const s = h("span", "it", m[5]!); s.dataset.item = m[5]!; nodes.push(s); }
      at = m.index! + m[0].length;
    }
    if (at < text.length) nodes.push(document.createTextNode(text.slice(at)));
    return { nodes, opts };
  }

  render(): void {
    const n = this.npc;
    const title = this.titleBar(n.name.toUpperCase(), "Close (Esc)");
    const stage = h("div", "stage");
    const mark = n.quests.some((q) => q.state === "ready") ? h("div", "mark rdy", "?") : n.quests.some((q) => q.state === "available") ? h("div", "mark", "!") : null;
    this.spr = h("div", "spr"); stage.append(this.spr); if (mark) stage.append(mark);
    void this.loadFrames(n).then(() => this.animate(true));
    const svc = h("div", "svc");
    for (const [label, l] of n.services) { const d = h("div", "", label); d.addEventListener("click", () => this.choose(l, label)); svc.append(d); }
    const side = h("div", "side", stage, h("div", "who", h("b", "", n.name), h("small", "", n.title)), svc);
    const qn = n.quests.filter((q) => q.state === "available" || q.state === "ready").length;
    const tabs = h("div", "tabs");
    for (const t of ["Talk", "Quests"] as const) { const d = h("div", this.tab === t ? "on" : "", t.toUpperCase(), ...(t === "Quests" && qn ? [h("b", "", String(qn))] : [])); d.addEventListener("click", () => { this.tab = t; this.render(); }); tabs.append(d); }
    const lg = h("div", "lg", this.showLog ? "Hide log" : "Log"); lg.addEventListener("click", () => { this.showLog = !this.showLog; this.render(); });
    tabs.append(h("div", "sp"), lg);
    const main = h("div", "main", tabs);
    if (this.showLog) { const log = h("div", "log"); for (const [who, t] of this.history.slice(-30)) log.append(h("div", "", h("b", "", who + ": "), t)); if (!this.history.length) log.append("Nothing said yet."); main.append(log); }
    main.append(this.tab === "Talk" ? this.talkPane() : this.questPane());
    const backB = h("div", "btn" + (this.back.length ? "" : " dis"), "‹ Back");
    backB.addEventListener("click", () => { const p = this.back.pop(); if (p) { this.page = p; this.seq++; this.render(); } });
    main.append(h("div", "foot", backB, h("span", "sp"), "Gold ", h("b", "", this.host.gold().toLocaleString("en-GB")), h("span", "", " · 1-9 choose (pointer on the window) · Space skip")));
    this.el.replaceChildren(h("div", "panel", title, h("div", "nbody", side, main)));
  }

  private talkPane(): HTMLElement {
    const src = this.npc.pages[this.page] ?? this.npc.pages["@main"]!;
    const { nodes, opts } = this.markup(src);
    this.opts = opts;
    const say = h("div", "say");
    say.append(...nodes);
    const optEl = h("div", "opts");
    opts.forEach(([t, l], i) => {
      const exit = l === "@exit" || /^back$/i.test(t);
      const cost = this.npc.id === "jason" && this.page === "@tele" && !exit ? "2,000 gold" : "";
      const poor = !!cost && this.host.gold() < 2000;
      const o = h("div", "op" + (exit ? " exit" : "") + (poor ? " dis" : ""), h("kbd", "", String(i + 1)), h("span", "", t), ...(cost ? [h("small", "", cost)] : []));
      o.addEventListener("click", () => { if (!poor) this.choose(l, t); });
      optEl.append(o);
    });
    const pane = h("div", "talk", say, optEl);
    // Typewriter over the text nodes (links and coloured words appear as the text reaches them).
    window.clearInterval(this.typing);
    const texts: [Text, string][] = [];
    const walk = (n: Node) => { if (n.nodeType === 3) { texts.push([n as Text, n.textContent ?? ""]); (n as Text).textContent = ""; } else n.childNodes.forEach(walk); };
    say.childNodes.forEach(walk);
    const hidden = [...say.children] as HTMLElement[];
    for (const e of hidden) e.style.visibility = "hidden";
    const caret = h("span", "caret"); say.append(caret);
    let ti = 0, ci = 0;
    const finish = () => {
      window.clearInterval(this.typing); this.finishType = null;
      for (const [t, full] of texts) t.textContent = full;
      for (const e of hidden) e.style.visibility = "";
      caret.remove(); optEl.classList.add("on");
    };
    this.finishType = finish;
    say.addEventListener("click", () => { if (this.finishType) finish(); });
    if (this.shownSeq === this.seq) { finish(); return pane; }
    this.shownSeq = this.seq;
    this.typing = window.setInterval(() => {
      for (let k = 0; k < 3; k++) {
        const cur = texts[ti];
        if (!cur) { finish(); return; }
        const el = cur[0].parentElement;
        if (el && el !== say) el.style.visibility = "";
        cur[0].textContent = cur[1].slice(0, ++ci);
        if (ci >= cur[1].length) { ti++; ci = 0; }
      }
    }, 16);
    this.history.push([this.npc.name, src.replace(/<[^>]*>|\{|\/[A-Z]+\}|\[ITEM:\d+\||\]/g, "").split("\n").filter(Boolean).slice(0, 2).join(" ")]);
    return pane;
  }

  private questPane(): HTMLElement {
    const list = h("div", "ql");
    for (const q of this.npc.quests) {
      const mk = { available: "!", active: "…", ready: "?", done: "✓" }[q.state];
      const reward = h("div", "rw"); reward.append("Reward: ", ...this.markup(q.reward).nodes);
      const t = h("div", "qt", h("b", "", q.name), h("small", "", `Lv ${q.level} · ${q.kind}`), h("p", "", q.text), h("div", "gl", `Goal: ${q.goal}${q.progress ? ` (${q.progress[0]}/${q.progress[1]})` : ""}`), reward);
      if (q.progress) { const b = h("div", "bar", h("i")); (b.firstChild as HTMLElement).style.width = q.progress[0] / q.progress[1] * 100 + "%"; t.append(b); }
      const btns = h("div", "btns");
      const btn = (label: string, cls: string, fn: () => void, dis = false) => { const b = h("div", "btn " + cls + (dis ? " dis" : ""), label); b.addEventListener("click", fn); btns.append(b); };
      if (q.state === "available") btn(this.host.level() < q.level ? `Needs Lv ${q.level}` : "Accept", "nmain2", () => { q.state = "active"; q.progress = [0, 5]; this.host.toast(`Quest accepted: ${q.name}`); this.render(); }, this.host.level() < q.level);
      if (q.state === "active") btn("Abandon", "", () => { q.state = "available"; delete q.progress; this.render(); });
      if (q.state === "ready") btn("Hand in", "nmain2", () => { q.state = "done"; this.host.toast(`Quest complete: ${q.name}`); this.render(); });
      if (q.state === "done") btns.append(h("small", "", "Completed"));
      list.append(h("div", "qc", h("div", "qm " + q.state, mk), t, btns));
    }
    return list;
  }

  // — Sprite —
  private async loadFrames(n: Npc): Promise<void> {
    if (this.frames.has(n.image)) return;
    const path = `NPC/${String(n.image).padStart(2, "0")}`;
    const pics = (await Promise.all([0, 1, 2, 3].map((i) => libPicture(path, n.dir * 4 + i)))).filter((p): p is LibPicture => !!p);
    const harvest = (await Promise.all(Array.from({ length: 10 }, (_, i) => libPicture(path, 12 + i)))).filter((p): p is LibPicture => !!p);
    const all = pics.length ? pics : harvest; // the standing frames set the box; gestures may reach outside it
    if (!all.length) return;
    const box: [number, number, number, number] = [Math.min(...all.map((p) => p.x)), Math.min(...all.map((p) => p.y)), Math.max(...all.map((p) => p.x + p.width)), Math.max(...all.map((p) => p.y + p.height))];
    this.frames.set(n.image, { pics, harvest, box });
  }
  private animate(force = false): void {
    if (!this.open || !this.spr) return;
    const f = this.frames.get(this.npc.image);
    if (!f) return;
    const now = Date.now();
    let pic: LibPicture | undefined;
    if (this.gesture >= 0 && f.harvest.length) { pic = f.harvest[Math.min(this.gesture, f.harvest.length - 1)]; this.gesture++; if (this.gesture >= f.harvest.length) this.gesture = -1; }
    else { const i = Math.floor(now / 450) % Math.max(1, f.pics.length); if (!force && i === this.frame && this.spr.firstChild) return; this.frame = i; pic = f.pics[i]; }
    if (!pic) return;
    const [x0, y0, x1, y1] = f.box, w = x1 - x0, ht = y1 - y0;
    const k = Math.min(3, 170 / Math.max(1, ht), 190 / Math.max(1, w));
    Object.assign(this.spr.style, { width: w + "px", height: ht + "px", marginLeft: -w / 2 + "px", transform: `scale(${k})` });
    let img = this.spr.firstChild as HTMLImageElement | null;
    if (!img) { img = h("img"); this.spr.append(img); }
    img.src = pic.url;
    Object.assign(img.style, { left: pic.x - x0 + "px", top: pic.y - y0 + "px", width: pic.width + "px", height: pic.height + "px" });
  }
}
