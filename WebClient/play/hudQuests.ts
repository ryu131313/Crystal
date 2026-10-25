// Quests window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// QuestDiaryDialog / QuestDetail / QuestTrackingDialog: Active / Available / Completed tabs, search, quests grouped by
// the quest categories (Story, Side, Daily, Repeatable, ...) in folding groups, a pin per quest to track it (at most 5, like
// the desktop), and a detail pane with the giver, zone, objectives (kill / item / talk, with progress) and rewards
// (EXP, gold, credit, fixed items and a choose-one pick, Items.Lib icons) plus Accept / Turn in / Track / Find on map /
// Abandon. Tracked quests show in an on-screen tracker (top right); kills in the HUD simulation advance kill objectives.
// Quests are mock data; reward item Image numbers are from a copy of the live Server.MirDB (2026-10-15).

import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Kind = "kill" | "item" | "talk";
type QType = "Story" | "General" | "Daily" | "Repeatable";
type State = "active" | "available" | "completed";
interface Obj { kind: Kind; text: string; have: number; need: number }
interface Quest {
  id: number; name: string; type: QType; level: number; zone: string; giver: string; text: string;
  objs: Obj[]; exp: number; gold: number; credit: number;
  items: [string, number, number][]; choose: [string, number, number][];
  state: State; tracked: boolean; when?: string;
}
const TYPES: QType[] = ["Story", "General", "Daily", "Repeatable"];
const MAX_TRACKED = 5;

const k = (text: string, have: number, need: number): Obj => ({ kind: "kill", text, have, need });
const it = (text: string, have: number, need: number): Obj => ({ kind: "item", text, have, need });
const tk = (text: string, done = false): Obj => ({ kind: "talk", text, have: done ? 1 : 0, need: 1 });

const QUESTS: Quest[] = [
  { id: 1, name: "Shadows over Bichon", type: "Story", level: 30, zone: "Bichon Province", giver: "Captain of the Wall",
    text: "Oma raiders have been seen beyond the northern wall, bolder than ever. The captain wants their warband broken and proof that someone is leading them.",
    objs: [k("Oma Warrior", 7, 10), it("Oma Fang", 5, 5), tk("Report to the Elder Shaman")], exp: 45000, gold: 12000, credit: 0,
    items: [["SunPotion", 16, 5]], choose: [["HeavenSword", 74, 1], ["DragonStaff", 58, 1], ["HolyBloodSpear", 77, 1]], state: "active", tracked: true },
  { id: 2, name: "Whispers in Woomyon", type: "General", level: 33, zone: "Woomyon Woods", giver: "Herbalist Lin",
    text: "The herbalist's gatherers have stopped coming back from the woods. Find out what is hunting them and bring back what herbs you can.",
    objs: [k("Woomyon Spider", 3, 12), it("Mountain Ginseng", 2, 6)], exp: 28000, gold: 8000, credit: 0,
    items: [["(HP)DrugLarge", 28, 20], ["(MP)DrugLarge", 29, 20]], choose: [], state: "active", tracked: true },
  { id: 3, name: "Daily: Cull the Pack", type: "Daily", level: 28, zone: "Bichon Province", giver: "Hunter Gorm",
    text: "Wolves have been taking the farmers' livestock again. Thin the pack before nightfall.",
    objs: [k("Grey Wolf", 30, 30)], exp: 15000, gold: 5000, credit: 5,
    items: [["BenedictionOil", 26, 1]], choose: [], state: "active", tracked: true },
  { id: 4, name: "Ore for the Smith", type: "Repeatable", level: 25, zone: "Bichon Mine", giver: "Blacksmith Haro",
    text: "Haro always needs more iron. Bring him black iron ore from the mine and he will pay in coin.",
    objs: [it("Black Iron Ore", 4, 10)], exp: 6000, gold: 9000, credit: 0, items: [], choose: [], state: "active", tracked: false },
  { id: 5, name: "A Letter for the Peddler", type: "General", level: 22, zone: "Bichon Province", giver: "Postmaster Wen",
    text: "A sealed letter needs to reach the travelling peddler. He was last seen near the old mill.",
    objs: [tk("Deliver the letter to the Peddler")], exp: 4000, gold: 2000, credit: 0, items: [["TownTeleport", 402, 3]], choose: [], state: "active", tracked: false },
  { id: 6, name: "The Bone Elite", type: "Story", level: 38, zone: "Bone Cave", giver: "Elder Shaman",
    text: "The shaman has traced the raiders' courage to a single source: a Bone Elite stirring in the deepest cave. End it.",
    objs: [k("Bone Elite", 0, 1), it("Elite's Skull", 0, 1)], exp: 90000, gold: 30000, credit: 20,
    items: [["HealthStone(M)", 586, 1]], choose: [["WhiteGoldRing", 773, 1], ["RedOrchidBracelet", 434, 1], ["RecallNecklace", 247, 1]], state: "active", tracked: false },
  { id: 7, name: "Spider Silk", type: "General", level: 34, zone: "Woomyon Woods", giver: "Tailor Mei",
    text: "Mei pays well for clean spider silk, if you can stomach collecting it.", objs: [it("Spider Silk", 0, 8)], exp: 18000, gold: 6000, credit: 0,
    items: [], choose: [["WhiteGoldBelt", 761, 1], ["WhiteGoldShoes", 752, 1]], state: "available", tracked: false },
  { id: 8, name: "Daily: Mine Patrol", type: "Daily", level: 26, zone: "Bichon Mine", giver: "Foreman Dao",
    text: "Clear the upper tunnels so the miners can work.", objs: [k("Cave Bat", 0, 20), k("Scorpion", 0, 10)], exp: 14000, gold: 4000, credit: 5,
    items: [], choose: [], state: "available", tracked: false },
  { id: 9, name: "The Lost Patrol", type: "Story", level: 41, zone: "Serpent Valley", giver: "Captain of the Wall",
    text: "A patrol sent to the valley has not returned. Their last message spoke of a serpent the size of a river.",
    objs: [tk("Find the patrol's camp"), k("Serpent Valley Naga", 0, 15)], exp: 120000, gold: 40000, credit: 0,
    items: [["DragonTorch", 132, 1]], choose: [], state: "available", tracked: false },
  { id: 10, name: "Herbs for the Healer", type: "Repeatable", level: 20, zone: "Bichon Province", giver: "Healer Sun",
    text: "The healer turns herbs into potions for the guards. Any you bring are welcome.", objs: [it("Red Herb", 0, 15)], exp: 3000, gold: 3000, credit: 0,
    items: [["SunPotion", 16, 2]], choose: [], state: "available", tracked: false },
  { id: 11, name: "First Steps", type: "Story", level: 7, zone: "Bichon Province", giver: "Captain of the Wall",
    text: "Prove yourself to the guard.", objs: [k("Hen", 10, 10)], exp: 800, gold: 500, credit: 0, items: [], choose: [], state: "completed", tracked: false, when: "3 days ago" },
  { id: 12, name: "Deer Hunt", type: "General", level: 12, zone: "Bichon Province", giver: "Hunter Gorm",
    text: "Venison for the inn.", objs: [k("Deer", 8, 8)], exp: 2500, gold: 1200, credit: 0, items: [], choose: [], state: "completed", tracked: false, when: "2 days ago" },
  { id: 13, name: "Daily: Cull the Pack", type: "Daily", level: 28, zone: "Bichon Province", giver: "Hunter Gorm",
    text: "Wolves have been taking the farmers' livestock again.", objs: [k("Grey Wolf", 30, 30)], exp: 15000, gold: 5000, credit: 5, items: [], choose: [], state: "completed", tracked: false, when: "yesterday" },
];

const ICON: Record<QType | Kind | "pin" | "map" | "check", string> = {
  Story: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z",
  General: "M6 3h10l2 2v16H8l-2-2zM9 8h6M9 12h6M9 16h4",
  Daily: "M12 7a5 5 0 110 10 5 5 0 010-10zM12 1v3M12 20v3M1 12h3M20 12h3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1",
  Repeatable: "M4 12a8 8 0 0114-5.3L20 9M20 4v5h-5M20 12a8 8 0 01-14 5.3L4 15M4 20v-5h5",
  kill: "M3 3l12 12M21 3L9 15M13 17l4 4M17 17l-4 4M7 17l4 4M11 17l-4 4",
  item: "M6 10a5 5 0 0111 0v9a1.5 1.5 0 01-1.5 1.5h-8A1.5 1.5 0 016 19z",
  talk: "M4 5h16v10H9l-5 4z",
  pin: "M9 3h6l-1 6 4 4H6l4-4zM12 13v8",
  map: "M2 5l6-2 8 3 6-2v15l-6 2-8-3-6 2zM8 3v15M16 6v15",
  check: "M5 12l4 4 10-10",
};
const svgIcon = (d: string, cls = "qu-ic") => {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", cls);
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
};
const ready = (q: Quest) => q.objs.every((o) => o.have >= o.need);
const doneCount = (q: Quest) => q.objs.filter((o) => o.have >= o.need).length;
const fmt = (n: number) => n.toLocaleString("en-GB");

const CSS = `
.hudm .qwin { --winh: 476px; width: 660px; }
.hudm .qwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .qwin .cols { flex: 1; min-height: 0; display: flex; }
.hudm .qwin .left { width: 300px; display: flex; flex-direction: column; padding: 10px 8px 8px 12px; min-height: 0; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .qu-ic { width: 13px; height: 13px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .qwin .seg { display: flex; border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); overflow: hidden; }
.hudm .qwin .seg div { flex: 1; height: 26px; display: flex; align-items: center; justify-content: center; gap: 5px; cursor: pointer; user-select: none;
  font-size: 11px; font-weight: 600; color: var(--dim); background: color-mix(in srgb, var(--pb) 60%, #000); }
.hudm .qwin .seg div + div { border-left: 1px solid var(--fr); }
.hudm .qwin .seg div i { font-style: normal; font-size: 10px; padding: 0 5px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .qwin .seg div:hover { color: var(--tx); }
.hudm .qwin .seg div.on { color: #fff; background: color-mix(in srgb, var(--ac) 24%, var(--pb)); box-shadow: inset 0 -2px 0 var(--ac); }
.hudm .qwin .search { position: relative; margin: 8px 0 6px; }
.hudm .qwin .search input { width: 100%; height: 26px; box-sizing: border-box; padding: 0 8px 0 26px; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx);
  background: color-mix(in srgb, var(--pb) 60%, #000); border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); outline: none; }
.hudm .qwin .search input:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .qwin .search input::placeholder { color: var(--dim); }
.hudm .qwin .search svg { position: absolute; left: 8px; top: 7px; width: 12px; height: 12px; fill: none; stroke: var(--dim); stroke-width: 2.4; stroke-linecap: round; }
.hudm .qwin .list { flex: 1; min-height: 0; overflow-y: auto; padding-right: 4px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .qwin .grp { display: flex; align-items: center; gap: 6px; height: 24px; margin-top: 4px; padding: 0 4px; cursor: pointer; user-select: none;
  font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .qwin .grp .chev { transition: transform .15s; font-size: 10px; width: 10px; }
.hudm .qwin .grp.shut .chev { transform: rotate(-90deg); }
.hudm .qwin .grp span { margin-left: auto; letter-spacing: 0; color: var(--dim); }
.hudm .qwin .grp::after { content: ""; flex: 0 0 0; }
.hudm .qwin .row { position: relative; display: flex; align-items: center; gap: 8px; padding: 6px 6px 6px 10px; margin-bottom: 3px; cursor: pointer;
  border: 1px solid transparent; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .qwin .row::before { content: ""; position: absolute; left: 3px; top: 7px; bottom: 7px; width: 2px; border-radius: 1px; background: color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .qwin .row:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .qwin .row.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .qwin .row.sel::before, .hudm .qwin .row.ready::before { background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .qwin .row .mid { flex: 1; min-width: 0; }
.hudm .qwin .row .nm { font-size: 12px; font-weight: 600; color: var(--tx); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.hudm .qwin .row.done .nm { color: var(--dim); }
.hudm .qwin .row .sub { display: flex; align-items: center; gap: 6px; margin-top: 3px; font-size: 10px; color: var(--dim); }
.hudm .qwin .tbar { flex: 1; height: 4px; border-radius: 2px; background: #04060a; overflow: hidden; }
.hudm .qwin .tbar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); }
.hudm .qwin .pill { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 7px; white-space: nowrap;
  color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 40%, transparent); }
.hudm .qwin .pill.hot { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, transparent); box-shadow: 0 0 6px var(--glow); }
.hudm .qwin .pin { width: 22px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: calc(var(--rad) * .7);
  color: color-mix(in srgb, var(--dim) 70%, transparent); border: 1px solid transparent; }
.hudm .qwin .pin:hover { color: var(--tx); border-color: var(--fr); }
.hudm .qwin .pin.on { color: var(--ac); filter: drop-shadow(0 0 4px var(--ac)); }
.hudm .qwin .empty { padding: 30px 10px; text-align: center; color: var(--dim); font-size: 12px; }
.hudm .qwin .foot { padding-top: 6px; font-size: 11px; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .qwin .foot b { color: var(--tx); font-weight: 600; }
.hudm .qwin .detail { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .qwin .dscroll { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px 10px; display: flex; flex-direction: column; gap: 12px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .qwin .dhead h3 { margin: 0 0 6px; font-size: 17px; color: #fff; display: flex; align-items: center; gap: 8px; }
.hudm .qwin .dhead h3 .qu-ic { width: 16px; height: 16px; color: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .qwin .dhead .tags { display: flex; flex-wrap: wrap; gap: 5px; }
.hudm .qwin .giver { display: flex; gap: 14px; margin-top: 8px; font-size: 11px; color: var(--dim); }
.hudm .qwin .giver b { color: var(--tx); font-weight: 600; }
.hudm .qwin .desc { font-size: 12px; line-height: 1.55; color: color-mix(in srgb, var(--tx) 85%, var(--dim)); font-style: italic; }
.hudm .qwin .lbl { font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 6px; display: flex; justify-content: space-between; }
.hudm .qwin .obj { display: flex; align-items: center; gap: 8px; padding: 6px 8px; margin-bottom: 4px; border-radius: calc(var(--rad) * .7); background: color-mix(in srgb, var(--frl) 6%, transparent); font-size: 12px; }
.hudm .qwin .obj .box { width: 16px; height: 16px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: 4px; border: 1px solid var(--fr); color: transparent; }
.hudm .qwin .obj.ok .box { border-color: var(--ac); color: #fff; background: color-mix(in srgb, var(--ac) 45%, transparent); }
.hudm .qwin .obj.ok .tx { color: var(--dim); text-decoration: line-through; text-decoration-color: color-mix(in srgb, var(--dim) 60%, transparent); }
.hudm .qwin .obj .qu-ic:not(.ck) { color: var(--acd); }
.hudm .qwin .obj .ck { width: 11px; height: 11px; stroke-width: 3; }
.hudm .qwin .obj .tx { flex: 1; }
.hudm .qwin .obj .ct { font-weight: 600; font-variant-numeric: tabular-nums; color: var(--tx); }
.hudm .qwin .obj .tbar { flex: 0 0 60px; }
.hudm .qwin .rew { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.hudm .qwin .chip { height: 24px; padding: 0 10px; display: inline-flex; align-items: center; gap: 6px; font-size: 12px; border-radius: 12px;
  background: color-mix(in srgb, var(--frl) 8%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .qwin .chip span { color: var(--dim); font-size: 10px; font-weight: 700; letter-spacing: .08em; }
.hudm .qwin .chip b { color: var(--tx); font-variant-numeric: tabular-nums; }
.hudm .qwin .choose .eq.sel { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow), inset 0 0 10px var(--glow); }
.hudm .qwin .actions { display: flex; gap: 5px; padding: 10px 16px 12px; border-top: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .qwin .actions .btn { height: 26px; padding: 0 9px; white-space: nowrap; }
.hudm .qwin .actions .btn.main { margin-left: auto; color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .qwin .actions .btn.dis { opacity: .4; pointer-events: none; }
.hudm .qwin .actions .btn.warn { border-color: #b33; color: #fbb; }
.hudm .qwin .actions .btn .qu-ic { margin-right: 5px; }
.hudm .qwin .note { font-size: 11px; color: var(--dim); align-self: center; }

/* On-screen tracker, BDO style (the user's picks, 2026-10-15): no panel, right-hand side with its middle halfway down
   the screen; shiny gold quest titles, objectives in light text that turn red and ticked when done, a green
   "Ready: return to ..." line when the whole quest is done. Lines too long for the width scroll back and forth. */
.hudm .qtrack { position: absolute; right: 10px; top: 50%; transform: translateY(-50%); width: 220px; pointer-events: auto; color: #e8edf5; font-size: 11.5px;
  text-shadow: 0 0 2px #000, 0 1px 2px #000, 0 0 6px rgba(0,0,0,.8); }
.hudm .qtrack .th { display: flex; align-items: center; gap: 6px; height: 20px; cursor: pointer; user-select: none; font-size: 10px; font-weight: 700; letter-spacing: .18em;
  color: rgba(232, 237, 245, .75); }
.hudm .qtrack .th::after { content: ""; flex: 1; height: 1px; background: linear-gradient(90deg, rgba(240, 200, 110, .55), transparent); }
.hudm .qtrack .th span { order: 2; letter-spacing: 0; color: rgba(232, 237, 245, .6); }
.hudm .qtrack.shut .tb { display: none; }
.hudm .qtrack .tq { padding: 5px 0 3px; cursor: pointer; }
.hudm .qtrack .clip { overflow: hidden; white-space: nowrap; }
.hudm .qtrack .clip > span { display: inline-block; }
.hudm .qtrack .clip.marq > span { animation: hudm-marq var(--dur, 6s) ease-in-out infinite alternate; }
@keyframes hudm-marq { 0%, 18% { transform: translateX(0); } 82%, 100% { transform: translateX(var(--dx)); } }
/* Shiny gold: a gradient clipped to the text with a light band running across it. The shadow is a copy of the text
   underneath (.sh: transparent text, text-shadow only) instead of drop-shadow filters on the line, which had to be
   re-rasterised every frame of the sweep. */
.hudm .qtrack .tn { font-size: 12.5px; font-weight: 700; letter-spacing: .02em; text-shadow: none; }
.hudm .qtrack .tn > span { position: relative; }
.hudm .qtrack .tn .sh { position: absolute; left: 0; top: 0; color: transparent; pointer-events: none; user-select: none;
  text-shadow: 0 1px 1px #000, 0 0 3px rgba(0,0,0,.9), 0 1px 4px rgba(0,0,0,.6); }
.hudm .qtrack .tn .g { position: relative; background: linear-gradient(100deg, #a8741c 0%, #e8b84a 30%, #fff4c2 45%, #f0c860 55%, #b8862b 75%, #e8b84a 100%);
  background-size: 250% 100%; -webkit-background-clip: text; background-clip: text; color: transparent; animation: hudm-gold 5s linear infinite; }
@keyframes hudm-gold { from { background-position: 100% 0; } to { background-position: -150% 0; } }
.hudm .qtrack .tq:hover .tn .sh { text-shadow: 0 1px 1px #000, 0 0 5px rgba(255, 200, 90, .7); }
.hudm .qtrack .to { display: flex; align-items: baseline; gap: 6px; padding-left: 12px; line-height: 1.55; position: relative; }
.hudm .qtrack .to::before { content: ""; position: absolute; left: 3px; top: .72em; width: 4px; height: 4px; border-radius: 1px; background: rgba(232, 237, 245, .7); transform: rotate(45deg); }
.hudm .qtrack .to .clip { flex: 1; min-width: 0; }
.hudm .qtrack .to b { flex: none; font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .qtrack .to.ok { color: #ff5a4a; }
.hudm .qtrack .to.ok::before { content: "✓"; width: auto; height: auto; background: none; transform: none; top: 0; left: 0; font-size: 10px; font-weight: 700; color: #ff5a4a; }
.hudm .qtrack .to.bump b { animation: hudm-qbump .6s ease-out; }
@keyframes hudm-qbump { 0% { color: #fff; text-shadow: 0 0 8px #fff, 0 0 2px #000; } }
.hudm .qtrack .tr { display: flex; align-items: baseline; gap: 6px; padding-left: 12px; position: relative; line-height: 1.55; font-weight: 700; color: #5fe07a;
  text-shadow: 0 0 2px #000, 0 1px 2px #000, 0 0 8px rgba(80, 230, 110, .55); }
.hudm .qtrack .tr::before { content: "✓"; position: absolute; left: 0; top: 0; font-size: 10px; }
.hudm .qtrack .tr .clip { flex: 1; min-width: 0; }
`;

export class QuestsMock extends HudWindow {
  readonly tracker: HTMLElement;
  private quests: Quest[] = QUESTS.map((q) => ({ ...q, objs: q.objs.map((o) => ({ ...o })) }));
  private tab: State = "active";
  private picked = 1;
  private shut = new Set<string>();
  private chosen = new Map<number, number>();
  private confirmAbandon = 0;
  private bumped = "";
  private list!: HTMLElement;
  private detail!: HTMLElement;
  private seg!: HTMLElement;
  private foot!: HTMLElement;
  private search!: HTMLInputElement;

  constructor() {
    super("qwin", 100, 50); // clear of the top-right tracker on a 1024 screen
    injectCss("hudm-quests-css", CSS);
    this.tracker = h("div", "qtrack");
    new ResizeObserver(() => this.marquee()).observe(this.tracker); // measure the scrolling lines once it is laid out / rescaled
    this.build();
    this.renderTracker();
  }

  protected override shown(): void { this.render(); }

  private build(): void {
    this.seg = h("div", "seg");
    this.search = h("input");
    this.search.placeholder = "Search quests, places, people…";
    this.search.addEventListener("input", () => this.renderList());
    this.search.addEventListener("keydown", (e) => e.stopPropagation());
    const sWrap = h("div", "search", this.search);
    sWrap.insertAdjacentHTML("afterbegin", '<svg viewBox="0 0 24 24"><path d="M10.5 3a7.5 7.5 0 105.2 12.9L21 21M15.7 15.9A7.5 7.5 0 0010.5 3"/></svg>');
    this.list = h("div", "list");
    this.foot = h("div", "foot");
    this.detail = h("div", "detail");
    this.el.append(h("div", "panel", this.titleBar("QUESTS", "Close (Q)"), h("div", "cols", h("div", "left", this.seg, sWrap, this.list, this.foot), this.detail)));
  }

  private render(): void {
    this.renderSeg();
    this.renderList();
    this.renderDetail();
    this.renderTracker();
  }

  private renderSeg(): void {
    this.seg.replaceChildren();
    for (const [st, label] of [["active", "Active"], ["available", "Available"], ["completed", "Completed"]] as [State, string][]) {
      const n = this.quests.filter((q) => q.state === st).length;
      const b = h("div", st === this.tab ? "on" : "", label, h("i", "", String(n)));
      b.addEventListener("click", () => {
        this.tab = st;
        const first = this.quests.find((q) => q.state === st);
        if (first) this.picked = first.id;
        this.confirmAbandon = 0;
        this.render();
      });
      this.seg.append(b);
    }
  }

  private renderList(): void {
    const q = this.search.value.trim().toLowerCase();
    const items = this.quests.filter((x) => x.state === this.tab && (!q || [x.name, x.zone, x.giver, x.text, x.type, ...x.objs.map((o) => o.text)].some((s) => s.toLowerCase().includes(q))));
    this.list.replaceChildren();
    if (!items.length) this.list.append(h("div", "empty", q ? "No quests match." : "Nothing here yet."));
    for (const t of TYPES) {
      const group = items.filter((x) => x.type === t);
      if (!group.length) continue;
      const key = this.tab + t;
      const head = h("div", "grp" + (this.shut.has(key) ? " shut" : ""), h("span", "chev", "▾"), svgIcon(ICON[t]), t, h("span", "", String(group.length)));
      head.addEventListener("click", () => { if (this.shut.has(key)) this.shut.delete(key); else this.shut.add(key); this.renderList(); });
      this.list.append(head);
      if (this.shut.has(key)) continue;
      for (const x of group) this.list.append(this.row(x));
    }
    const tracked = this.quests.filter((x) => x.tracked).length;
    const active = this.quests.filter((x) => x.state === "active");
    this.foot.replaceChildren(h("span", "", h("b", "", String(active.filter(ready).length)), " ready to turn in"), h("span", "", "Tracked ", h("b", "", `${tracked} / ${MAX_TRACKED}`)));
  }

  private row(x: Quest): HTMLElement {
    const isReady = x.state === "active" && ready(x);
    const bar = h("div", "tbar", h("i"));
    (bar.firstChild as HTMLElement).style.width = (doneCount(x) / x.objs.length * 100).toFixed(0) + "%";
    const sub = x.state === "completed"
      ? h("div", "sub", `Lv ${x.level} · ${x.zone}`, h("span", "", ""), h("span", "", x.when ?? ""))
      : h("div", "sub", `Lv ${x.level} · ${x.zone}`, x.state === "active" ? bar : h("span", "", ""), x.state === "active" ? `${doneCount(x)}/${x.objs.length}` : "");
    const parts: (HTMLElement | SVGElement)[] = [h("div", "mid", h("div", "nm", x.name), sub)];
    if (isReady) parts.push(h("span", "pill hot", "Ready"));
    if (x.state === "active") {
      const pin = h("div", "pin" + (x.tracked ? " on" : ""), svgIcon(ICON.pin));
      pin.title = x.tracked ? "Untrack" : "Track on screen";
      pin.addEventListener("click", (e) => { e.stopPropagation(); this.toggleTrack(x); });
      parts.push(pin);
    }
    const r = h("div", "row" + (x.id === this.picked ? " sel" : "") + (isReady ? " ready" : "") + (x.state === "completed" ? " done" : ""), ...parts);
    r.addEventListener("click", () => { this.picked = x.id; this.confirmAbandon = 0; this.renderList(); this.renderDetail(); });
    return r;
  }

  private toggleTrack(x: Quest): void {
    if (!x.tracked && this.quests.filter((q) => q.tracked).length >= MAX_TRACKED) return;
    x.tracked = !x.tracked;
    this.render();
  }

  private renderDetail(): void {
    const x = this.quests.find((q) => q.id === this.picked && q.state === this.tab) ?? this.quests.find((q) => q.state === this.tab);
    this.detail.replaceChildren();
    if (!x) { this.detail.append(h("div", "empty", "Pick a quest.")); return; }
    this.picked = x.id;
    const isReady = x.state === "active" && ready(x);
    const tags = h("div", "tags", h("span", "pill", x.type), h("span", "pill", `Level ${x.level}`));
    if (isReady) tags.append(h("span", "pill hot", "Ready to turn in"));
    if (x.state === "completed") tags.append(h("span", "pill", `Completed ${x.when ?? ""}`));
    if (x.tracked) tags.append(h("span", "pill", "Tracked"));
    const head = h("div", "dhead", h("h3", "", svgIcon(ICON[x.type]), x.name), tags,
      h("div", "giver", h("span", "", "From ", h("b", "", x.giver)), h("span", "", "In ", h("b", "", x.zone))));
    const objs = h("div", "");
    objs.append(h("div", "lbl", h("span", "", "Objectives"), h("span", "", `${doneCount(x)} / ${x.objs.length}`)));
    for (const o of x.objs) {
      const ok = o.have >= o.need;
      const box = h("div", "box", svgIcon(ICON.check, "qu-ic ck"));
      const line = h("div", "obj" + (ok ? " ok" : ""), box, svgIcon(ICON[o.kind]),
        h("span", "tx", o.kind === "kill" ? `Defeat ${o.text}` : o.kind === "item" ? `Collect ${o.text}` : o.text));
      if (o.need > 1) {
        const bar = h("div", "tbar", h("i"));
        (bar.firstChild as HTMLElement).style.width = (Math.min(1, o.have / o.need) * 100).toFixed(0) + "%";
        line.append(bar, h("span", "ct", `${Math.min(o.have, o.need)}/${o.need}`));
      }
      objs.append(line);
    }
    const rewards = h("div", "");
    rewards.append(h("div", "lbl", h("span", "", "Rewards"), h("span", "", "")));
    const chips = h("div", "rew");
    const chip = (k: string, v: string) => h("div", "chip", h("span", "", k), h("b", "", v));
    if (x.exp) chips.append(chip("EXP", fmt(x.exp)));
    if (x.gold) chips.append(chip("GOLD", fmt(x.gold)));
    if (x.credit) chips.append(chip("CREDIT", fmt(x.credit)));
    for (const [n, img, c] of x.items) chips.append(itemCell(this.el, c > 1 ? `${n} (${c})` : n, img, c));
    rewards.append(chips);
    if (x.choose.length) {
      rewards.append(h("div", "lbl", h("span", "", "Choose one"), h("span", "", "")));
      const pick = h("div", "rew choose");
      x.choose.forEach(([n, img, c], i) => {
        const cell = itemCell(this.el, n, img, c);
        if (this.chosen.get(x.id) === i) cell.classList.add("sel");
        cell.addEventListener("click", () => { this.chosen.set(x.id, i); this.renderDetail(); });
        pick.append(cell);
      });
      rewards.append(pick);
    }
    const scroll = h("div", "dscroll", head, h("div", "desc", x.text), objs, rewards);
    this.detail.append(scroll, this.actions(x));
  }

  private actions(x: Quest): HTMLElement {
    const bar = h("div", "actions");
    const btn = (label: string, cls: string, fn: () => void, icon?: string) => {
      const b = h("div", "btn " + cls);
      if (icon) b.append(svgIcon(icon));
      b.append(label);
      b.addEventListener("click", fn);
      bar.append(b);
      return b;
    };
    if (x.state === "available") {
      bar.append(h("span", "note", `Speak to ${x.giver}`));
      btn("Accept", "main", () => {
        x.state = "active";
        x.tracked = this.quests.filter((q) => q.tracked).length < MAX_TRACKED;
        this.tab = "active";
        this.render();
      });
      return bar;
    }
    if (x.state === "completed") { bar.append(h("span", "note", `Rewards claimed ${x.when ?? ""}`)); return bar; }
    btn(x.tracked ? "Untrack" : "Track", "", () => this.toggleTrack(x), ICON.pin);
    const find = btn("Map", "", () => { find.lastChild!.textContent = "Marked"; }, ICON.map);
    find.title = "Find on map";
    const ab = btn(this.confirmAbandon === x.id ? "Click again to abandon" : "Abandon", this.confirmAbandon === x.id ? "warn" : "", () => {
      if (this.confirmAbandon !== x.id) { this.confirmAbandon = x.id; this.renderDetail(); return; }
      x.state = "available"; x.tracked = false; this.confirmAbandon = 0;
      for (const o of x.objs) o.have = 0;
      this.render();
    });
    void ab;
    const needPick = x.choose.length > 0 && !this.chosen.has(x.id);
    const turnIn = btn(needPick && ready(x) ? "Pick a reward" : "Turn in", "main" + (ready(x) && !needPick ? "" : " dis"), () => {
      x.state = "completed"; x.tracked = false; x.when = "just now";
      this.tab = "completed";
      this.render();
    });
    turnIn.title = ready(x) ? (needPick ? "Choose one of the rewards first" : `Return to ${x.giver}`) : "Finish the objectives first";
    return bar;
  }

  /** A kill in the HUD simulation: advance one unfinished kill objective of a tracked quest (else any active one). */
  onKill(): void {
    const pool = this.quests.filter((q) => q.state === "active" && q.objs.some((o) => o.kind === "kill" && o.have < o.need));
    const q = pool.find((p) => p.tracked) ?? pool[0];
    const o = q?.objs.find((x) => x.kind === "kill" && x.have < x.need);
    if (!q || !o) return;
    o.have++;
    this.bumped = `${q.id}/${q.objs.indexOf(o)}`;
    this.renderTracker();
    if (this.open) { this.renderList(); this.renderDetail(); }
  }

  private trackShut = false;
  private renderTracker(): void {
    const tracked = this.quests.filter((q) => q.tracked && q.state === "active");
    this.tracker.hidden = !tracked.length;
    this.tracker.classList.toggle("shut", this.trackShut);
    const head = h("div", "th", "QUESTS", h("span", "", this.trackShut ? `${tracked.length} ▸` : `${tracked.length} ▾`));
    head.addEventListener("click", () => { this.trackShut = !this.trackShut; this.renderTracker(); });
    const body = h("div", "tb");
    const clip = (cls: string, text: string) => h("div", "clip " + cls, h("span", "", text));
    for (const q of tracked) {
      const sh = h("span", "sh", q.name);
      sh.setAttribute("aria-hidden", "true");
      const block = h("div", "tq", h("div", "clip tn", h("span", "", sh, h("span", "g", q.name))));
      if (ready(q)) block.append(h("div", "tr", clip("", `Ready: return to ${q.giver}`)));
      else q.objs.forEach((o, i) => {
        const ok = o.have >= o.need;
        const text = o.kind === "kill" ? `Defeat ${o.text}` : o.kind === "item" ? `Collect ${o.text}` : o.text;
        const line = h("div", "to" + (ok ? " ok" : "") + (this.bumped === `${q.id}/${i}` ? " bump" : ""), clip("", text));
        if (o.need > 1) line.append(h("b", "", `${Math.min(o.have, o.need)}/${o.need}`));
        block.append(line);
      });
      block.addEventListener("click", () => { this.tab = "active"; this.picked = q.id; this.toggle(true); });
      body.append(block);
    }
    this.bumped = "";
    this.tracker.replaceChildren(head, body);
    this.marquee();
  }

  /** Lines wider than their box scroll to their end and back (hold at each end), at about 25 px a second. */
  private marquee(): void {
    const fit = () => {
      for (const c of this.tracker.querySelectorAll<HTMLElement>(".clip")) {
        const inner = c.firstElementChild as HTMLElement;
        const over = inner.scrollWidth - c.clientWidth;
        c.classList.toggle("marq", over > 1);
        if (over > 1) { c.style.setProperty("--dx", -over - 2 + "px"); c.style.setProperty("--dur", (2.5 + over / 25).toFixed(1) + "s"); }
      }
    };
    fit();
    requestAnimationFrame(fit); // again once the layout is final (fonts, the HUD scale)
  }
}
