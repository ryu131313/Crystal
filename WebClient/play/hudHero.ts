// Hero window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's hero
// dialogs (HeroDialogs.cs: HeroInventoryDialog, HeroBeltDialog, HeroInfoPanel, HeroBehaviourPanel, HeroManageDialog):
// a roster of your heroes on the left (the active one, stored heroes you can make active, empty and locked slots; class
// portraits are Prguse HeroAvatar + 370 like HeroManageAvatar), the active hero in the middle (paperdoll drawn like the
// Character window, 14 equipment slots, HP / MP / EXP, Summon / Dismiss / Revive, an activity feed) and three tabs on
// the right: Tactics (the four HeroBehaviour modes, auto potion with HP / MP thresholds, the 2-slot hero belt), Bag
// (the 40-cell hero inventory) and Skills (the hero's spells with an auto-cast switch each, like SpellToggleState, and
// the Shift+F1-F8 hero skill bar). A frame on the left of the screen shows the summoned hero (HeroInfoPanel); its mode
// chip cycles the behaviour. Item names and images are from a copy of the live Server.MirDB (2026-10-15); spell icons
// and unlock levels are the server's defaults; heroes, stats and the fight simulation are mock.

import { MirClass } from "../src/protocol/enums.generated.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { dollInto } from "./hudCharacter.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Behaviour = "Attack" | "CounterAttack" | "Follow" | "Custom";
const BEHAVIOURS: [Behaviour, string, string, string][] = [
  ["Attack", "Attack", "Fights any monster near you.", "M5 19L19 5M14 5h5v5M5 14l5 5M3 21l3-3"],
  ["CounterAttack", "Counter", "Only fights what attacks you or your hero.", "M12 3l8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6zM9 12l2 2 4-4"],
  ["Follow", "Follow", "Stays at your side and never fights.", "M7 20l3-8-3-3 4-5M14 20l2-6 3-2M15 4a2 2 0 110 4 2 2 0 010-4"],
  ["Custom", "Custom", "Casts only the skills switched on in Skills.", "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0M14 4v4M8 10v4M16 16v4"],
];
const CLASS_NAME: Record<number, string> = { [MirClass.Warrior]: "Warrior", [MirClass.Wizard]: "Wizard", [MirClass.Taoist]: "Taoist", [MirClass.Assassin]: "Assassin", [MirClass.Archer]: "Archer" };
const CLASS_COLOUR: Record<number, string> = { [MirClass.Warrior]: "#e0714f", [MirClass.Wizard]: "#6aa8ff", [MirClass.Taoist]: "#6fd38a" };

/** [slot name, item name, Items.Lib image (StateItem paperdoll for armour / weapon / helmet)]; image -1 = empty. */
type Gear = [string, string, number];
/** [Spell key, Icon (MagIcon2 = Icon * 2), unlock level] (server defaults, Envir.cs). */
type HeroSpell = [string, number, number];
interface Hero {
  name: string; cls: number; female: boolean; level: number;
  hp: number; mp: number; maxHp: number; maxMp: number; exp: number;
  state: "Summoned" | "Resting" | "Fallen";
  doll: [number, number, number]; // armour, weapon, helmet (StateItem)
  gear: Gear[]; spells: HeroSpell[]; auto: Set<string>; bar: (string | null)[];
}
const ACCESSORIES = (neck: Gear, ring: Gear): Gear[] => [
  neck, ["Bracelet", "WhiteGoldBracelet(H)", 767], ["Bracelet", "WhiteGoldBracelet(H)", 767], ring, ["Ring", "WhiteGoldRing(H)", 769],
  ["Belt", "WhiteGoldBelt(H)", 758], ["Boots", "WhiteGoldShoes(H)", 755], ["Torch", "DragonTorch", 132], ["Amulet", "", -1], ["Stone", "", -1], ["Mount", "", -1],
];
const SPELLS: Record<number, HeroSpell[]> = {
  [MirClass.Wizard]: [["FireBall", 0, 7], ["GreatFireBall", 4, 15], ["ThunderBolt", 10, 17], ["FireBang", 22, 22], ["FireWall", 21, 24],
    ["Lightning", 9, 26], ["FrostCrunch", 38, 28], ["MagicShield", 30, 31], ["IceStorm", 32, 35], ["FlameDisruptor", 34, 38], ["Blizzard", 51, 44], ["MeteorStrike", 52, 49]],
  [MirClass.Warrior]: [["Fencing", 2, 7], ["Slaying", 6, 15], ["Thrusting", 11, 22], ["HalfMoon", 24, 26], ["TwinDrakeBlade", 37, 32], ["FlamingSword", 25, 35],
    ["CrossHalfMoon", 33, 38], ["ProtectionField", 50, 39], ["Rage", 49, 44], ["Fury", 76, 45], ["SlashingBurst", 55, 50]],
  [MirClass.Taoist]: [["Healing", 1, 7], ["SpiritSword", 3, 9], ["Poisoning", 5, 14], ["SoulFireBall", 12, 18], ["SoulShield", 13, 22], ["BlessedArmour", 14, 25],
    ["MassHealing", 28, 31], ["UltimateEnhancer", 35, 33], ["Curse", 45, 40], ["Plague", 74, 42]],
};
function makeHero(name: string, cls: number, female: boolean, level: number, state: Hero["state"], doll: [number, number, number], weapon: Gear, armour: Gear, helmet: Gear, neck: Gear, ring: Gear): Hero {
  const maxHp = 300 + level * (cls === MirClass.Warrior ? 28 : 14), maxMp = 120 + level * (cls === MirClass.Wizard ? 30 : 12);
  const spells = (SPELLS[cls] ?? []).filter((s) => s[2] <= level);
  const auto = new Set(spells.slice(-4).map((s) => s[0]));
  const bar: (string | null)[] = Array.from({ length: 8 }, (_, i) => spells[spells.length - 1 - i]?.[0] ?? null);
  return { name, cls, female, level, hp: state === "Fallen" ? 0 : maxHp, mp: maxMp, maxHp, maxMp, exp: 0.35, state, doll,
    gear: [weapon, armour, helmet, ...ACCESSORIES(neck, ring)], spells, auto, bar };
}
const ITEMS_BAG: [string, number, number][] = [
  ["(HP)DrugXL", 813, 18], ["(MP)DrugXL", 814, 22], ["(HP)DrugLarge", 28, 30], ["(MP)DrugLarge", 29, 26], ["SunPotion", 16, 6],
  ["SoulRing", 674, 1], ["GoldDragonRing", 1001, 1], ["TarragonNecklace", 2764, 1], ["BugNecklace", 782, 1], ["MirNecklace", 823, 1],
  ["BoneRobe(F)", 741, 1], ["WhiteGoldRing", 773, 1], ["SealedHero", 789, 1],
];
const POTS: Record<"HP" | "MP", [string, number][]> = { HP: [["(HP)DrugXL", 813], ["(HP)DrugLarge", 28]], MP: [["(MP)DrugXL", 814], ["(MP)DrugLarge", 29]] };
const SLOTS = 8;
const pretty = (n: string) => n.replace(/([a-z])([A-Z])/g, "$1 $2");
const expNeed = (lv: number) => 40000 + lv * lv * 900;

const CSS = `
.hudm .hrwin { width: 960px; }
.hudm .hrwin .panel { height: 572px; display: flex; flex-direction: column; }
.hudm .hrwin .hbody { flex: 1; min-height: 0; display: flex; }
.hudm .hrwin .lbl { display: flex; justify-content: space-between; align-items: baseline; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin: 2px 0 4px; }
.hudm .hrwin .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
/* Roster */
.hudm .hrwin .roster { width: 210px; flex: none; padding: 10px; display: flex; flex-direction: column; gap: 6px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hrwin .hc { position: relative; display: flex; align-items: center; gap: 8px; padding: 6px; border-radius: var(--rad); cursor: pointer;
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .hrwin .hc:hover { border-color: color-mix(in srgb, var(--ac) 50%, transparent); }
.hudm .hrwin .hc.act { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 3px 0 0 var(--ac); cursor: default; }
.hudm .hrwin .hc.ask { border-color: #f2c94c; }
.hudm .hrwin .av { position: relative; width: 40px; height: 40px; flex: none; border-radius: var(--rad); overflow: hidden; border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%) ; }
.hudm .hrwin .av i { position: absolute; inset: 0; background: center / cover no-repeat; image-rendering: pixelated; }
.hudm .hrwin .av u { position: absolute; left: 0; right: 0; bottom: 0; height: 3px; text-decoration: none; }
.hudm .hrwin .hc .t { flex: 1; min-width: 0; } .hudm .hrwin .hc .t b { display: block; font-size: 12px; color: #fff; } .hudm .hrwin .hc .t small { font-size: 10px; color: var(--dim); }
.hudm .hrwin .chip { font-size: 8px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 6px; color: var(--tx); border: 1px solid var(--fr); background: var(--pb); }
.hudm .hrwin .chip.Summoned { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 45%, var(--pb)); box-shadow: 0 0 6px var(--glow); }
.hudm .hrwin .chip.Fallen { color: #fff; border-color: #c44; background: #6a1a1a; }
.hudm .hrwin .hc.emp { justify-content: center; color: var(--dim); font-size: 11px; border-style: dashed; min-height: 40px; }
.hudm .hrwin .hc.emp .eq { width: 30px; height: 30px; }
.hudm .hrwin .hc.lock { justify-content: center; color: color-mix(in srgb, var(--dim) 60%, transparent); font-size: 11px; cursor: not-allowed; min-height: 30px; opacity: .6; }
.hudm .hrwin .ask2 { display: flex; flex-direction: column; gap: 5px; padding: 7px; border-radius: var(--rad); font-size: 11px; border: 1px solid #f2c94c; background: rgba(242,201,76,.08); }
.hudm .hrwin .ask2 div { display: flex; gap: 5px; } .hudm .hrwin .ask2 .btn { flex: 1; }
/* Centre */
.hudm .hrwin .mid { width: 400px; flex: none; display: flex; flex-direction: column; padding: 10px 12px; gap: 7px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hrwin .head { display: flex; align-items: center; gap: 10px; }
.hudm .hrwin .head .nm { flex: 1; } .hudm .hrwin .head h3 { margin: 0; font-size: 20px; color: #fff; letter-spacing: .02em; }
.hudm .hrwin .head small { font-size: 11px; color: var(--dim); } .hudm .hrwin .head small em { font-style: normal; font-weight: 700; }
.hudm .hrwin .head .chip { font-size: 10px; padding: 3px 9px; border-radius: 9px; }
.hudm .hrwin .dollrow { position: relative; height: 262px; display: flex; justify-content: space-between; }
.hudm .hrwin .dollrow .col { display: flex; flex-direction: column; gap: 4px; z-index: 1; }
.hudm .hrwin .figure { position: absolute; left: 52px; right: 52px; top: 0; bottom: 0; overflow: hidden; border-radius: var(--rad);
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%), radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .hrwin .figure.fallen { filter: grayscale(1) brightness(.55); }
.hudm .hrwin .figure.res .org { animation: hudm-hrfade 1.2s ease-out; }
@keyframes hudm-hrfade { from { opacity: 0; filter: brightness(3); } }
.hudm .hrwin .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
.hudm .hrwin .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .hrwin .figure .ghost { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: 12px; letter-spacing: .2em; color: #e88; text-shadow: 0 0 6px #000; z-index: 1; }
.hudm .hrwin .bottom { display: flex; justify-content: center; gap: 4px; }
.hudm .hrwin .bars { display: grid; gap: 4px; }
.hudm .hrwin .bar3 { position: relative; height: 15px; border-radius: 4px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .hrwin .bar3 i { position: absolute; left: 0; top: 0; bottom: 0; transition: width .4s; }
.hudm .hrwin .bar3 em { font-style: normal; }
.hudm .hrwin .bar3 span { position: absolute; inset: 0; display: flex; justify-content: space-between; align-items: center; padding: 0 6px; font-size: 10px; font-weight: 700; color: #fff; text-shadow: 0 1px 2px #000; font-variant-numeric: tabular-nums; }
.hudm .hrwin .bar3.hp i { background: linear-gradient(180deg, #e04848, #8a1616); } .hudm .hrwin .bar3.mp i { background: linear-gradient(180deg, #4a8cff, #16388a); }
.hudm .hrwin .bar3.xp { height: 9px; } .hudm .hrwin .bar3.xp i { background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 50%, #000), var(--ac)); box-shadow: 0 0 6px var(--glow); }
.hudm .hrwin .acts { display: flex; gap: 6px; } .hudm .hrwin .acts .btn { flex: 1; height: 30px; font-size: 12px; }
.hudm .hrwin .btn.hmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .hrwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .hrwin .feed { flex: 1; min-height: 0; overflow: hidden; display: flex; flex-direction: column; justify-content: flex-end; gap: 1px; font-size: 10px; color: var(--dim);
  mask-image: linear-gradient(180deg, transparent, #000 45%); }
.hudm .hrwin .feed div b { color: var(--tx); font-weight: 600; } .hudm .hrwin .feed .g { color: #7fdc8a; } .hudm .hrwin .feed .r { color: #ff8080; } .hudm .hrwin .feed .a { color: var(--ac); }
/* Right */
.hudm .hrwin .side { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .hrwin .tabs { display: flex; padding: 8px 10px 0; gap: 4px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hrwin .tab2 { flex: 1; text-align: center; padding: 7px 0; cursor: pointer; font-size: 11px; font-weight: 700; letter-spacing: .14em; color: var(--dim); border-bottom: 2px solid transparent; }
.hudm .hrwin .tab2:hover { color: var(--tx); } .hudm .hrwin .tab2.on { color: var(--ac); border-bottom-color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .hrwin .pane { flex: 1; min-height: 0; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 10px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hrwin .bhv { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .hrwin .bh { display: flex; gap: 8px; padding: 8px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .hrwin .bh:hover { border-color: color-mix(in srgb, var(--ac) 55%, transparent); }
.hudm .hrwin .bh.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 0 0 12px color-mix(in srgb, var(--ac) 18%, transparent); }
.hudm .hrwin .bh svg { width: 22px; height: 22px; flex: none; fill: none; stroke: var(--dim); stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hrwin .bh.on svg { stroke: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .hrwin .bh b { display: block; font-size: 12px; color: #fff; } .hudm .hrwin .bh small { font-size: 10px; line-height: 1.3; color: var(--dim); }
.hudm .hrwin .pot { display: flex; align-items: center; gap: 8px; padding: 6px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .hrwin .pot .eq { width: 40px; height: 40px; }
.hudm .hrwin .pot .mid2 { flex: 1; min-width: 0; font-size: 11px; } .hudm .hrwin .pot .mid2 b { color: #fff; }
.hudm .hrwin .pot input[type=range] { width: 100%; accent-color: var(--ac); margin: 4px 0 0; }
.hudm .hrwin .pot .pc { width: 38px; text-align: right; font-size: 14px; font-weight: 700; color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .hrwin .pots.off .pot { opacity: .45; }
.hudm .hrwin .sw { position: relative; width: 30px; height: 16px; flex: none; border-radius: 8px; cursor: pointer; background: var(--pb); border: 1px solid var(--fr); }
.hudm .hrwin .sw::after { content: ""; position: absolute; left: 2px; top: 2px; width: 10px; height: 10px; border-radius: 50%; background: var(--dim); transition: left .15s; }
.hudm .hrwin .sw.on { background: color-mix(in srgb, var(--ac) 45%, var(--pb)); border-color: var(--ac); } .hudm .hrwin .sw.on::after { left: 16px; background: #fff; box-shadow: 0 0 5px var(--glow); }
.hudm .hrwin .belt2 { display: flex; gap: 6px; align-items: center; }
.hudm .hrwin .belt2 .k { position: relative; } .hudm .hrwin .belt2 .k span { position: absolute; left: 3px; top: 1px; z-index: 2; font-size: 9px; font-weight: 700; color: var(--ac); text-shadow: 0 0 2px #000; }
.hudm .hrwin .grid { display: grid; grid-template-columns: repeat(8, 40px); gap: 3px; justify-content: center; }
.hudm .hrwin .grid .eq { width: 40px; height: 40px; }
.hudm .hrwin .wt { font-size: 11px; color: var(--dim); display: flex; justify-content: space-between; }
.hudm .hrwin .wt .bar3 { height: 6px; flex: 1; margin: 5px 0 0 8px; } .hudm .hrwin .wt .bar3 i { background: var(--ac); }
.hudm .hrwin .sk { display: flex; align-items: center; gap: 8px; padding: 4px 6px; border-radius: var(--rad); cursor: pointer; border: 1px solid transparent; }
.hudm .hrwin .sk:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .hrwin .sk.sel { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 10%, transparent); }
.hudm .hrwin .sk.lock { opacity: .4; cursor: default; }
.hudm .hrwin .ic { width: 32px; height: 32px; flex: none; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #000 center / auto no-repeat; image-rendering: pixelated; box-shadow: 0 0 0 1px #000; }
.hudm .hrwin .sk .t { flex: 1; min-width: 0; font-size: 12px; color: #fff; } .hudm .hrwin .sk .t small { display: block; font-size: 10px; color: var(--dim); }
.hudm .hrwin .sk .pip { display: inline-flex; gap: 2px; margin-left: 4px; } .hudm .hrwin .sk .pip i { width: 5px; height: 5px; border-radius: 50%; background: var(--fr); } .hudm .hrwin .sk .pip i.on { background: var(--ac); box-shadow: 0 0 3px var(--glow); }
.hudm .hrwin .sk .sw { margin-left: auto; }
.hudm .hrwin .hbar { display: grid; grid-template-columns: repeat(8, 1fr); gap: 4px; }
.hudm .hrwin .hbar div { position: relative; aspect-ratio: 1; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #000 center / auto no-repeat; image-rendering: pixelated; cursor: pointer; box-shadow: 0 0 0 1px #000; }
.hudm .hrwin .hbar div span { position: absolute; left: 2px; top: 0; font-size: 8px; font-weight: 700; color: var(--ac); text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .hrwin .hbar.armed div { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 6px var(--glow); }
.hudm .hrwin .st { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; }
.hudm .hrwin .st div { display: flex; justify-content: space-between; padding: 6px 8px; border-radius: var(--rad); font-size: 11px; color: var(--dim);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .hrwin .st b { color: #fff; font-variant-numeric: tabular-nums; } .hudm .hrwin .st .hi b { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .hrwin .hint2 { font-size: 10px; color: var(--dim); }
/* On-screen hero frame (HeroInfoPanel) */
.hudm .hframe { position: absolute; left: 12px; top: 41%; width: 196px; display: flex; gap: 7px; padding: 5px; pointer-events: auto; border-radius: var(--rad); color: var(--tx);
  background: linear-gradient(90deg, color-mix(in srgb, var(--pb) 88%, transparent), color-mix(in srgb, var(--pb) 45%, transparent)); border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent);
  box-shadow: 0 0 0 1px rgba(0,0,0,.6); }
.hudm .hframe.fallen { filter: grayscale(.8); }
.hudm .hframe .pt { position: relative; width: 42px; height: 42px; flex: none; border-radius: 50%; overflow: hidden; cursor: pointer; border: 2px solid var(--ac); box-shadow: 0 0 calc(8px * var(--gl)) var(--glow);
  background: var(--pb) center / cover no-repeat; image-rendering: pixelated; }
.hudm .hframe .pt b { position: absolute; left: 50%; bottom: -1px; transform: translateX(-50%); font-size: 9px; font-weight: 800; padding: 0 4px; border-radius: 5px; color: #fff; background: #000c; }
.hudm .hframe .in2 { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.hudm .hframe .n { display: flex; justify-content: space-between; align-items: center; font-size: 11px; font-weight: 700; color: #fff; text-shadow: 0 1px 1px #000; }
.hudm .hframe .md { font-size: 8px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; cursor: pointer; color: var(--ac); border: 1px solid var(--ac); }
.hudm .hframe .md:hover { color: #fff; background: color-mix(in srgb, var(--ac) 35%, transparent); }
.hudm .hframe .b { position: relative; height: 7px; border-radius: 3px; background: #04060a; overflow: hidden; } .hudm .hframe .b i { position: absolute; left: 0; top: 0; bottom: 0; transition: width .4s; }
.hudm .hframe .b.hp i { background: #d23a3a; } .hudm .hframe .b.mp i { background: #3a72e0; } .hudm .hframe .b.xp { height: 3px; } .hudm .hframe .b.xp i { background: var(--ac); }
`;

export class HeroMock extends HudWindow {
  /** The on-screen hero frame (in the HUD's window layer). */
  readonly frame: HTMLElement;
  private heroes: Hero[] = [
    makeHero("Seraphine", MirClass.Wizard, true, 46, "Summoned", [602, 58, 105], ["Weapon", "DragonStaff", 58], ["Armour", "TitanArmour(F)", 602], ["Helmet", "HelmetOfHero", 105], ["Necklace", "HeroNecklace", 682], ["Ring", "HeroRing", 1019]),
    makeHero("Rowan", MirClass.Warrior, false, 52, "Resting", [592, 57, 107], ["Weapon", "DragonSlayer", 57], ["Armour", "TitanArmour(M)", 592], ["Helmet", "BrassHelmet", 107], ["Necklace", "WhiteGoldNecklace(H)", 776], ["Ring", "SoulRing", 674]),
    makeHero("Lyra", MirClass.Taoist, true, 41, "Resting", [602, 52, 105], ["Weapon", "SoulSpringWand", 52], ["Armour", "TitanArmour(F)", 602], ["Helmet", "HelmetOfHero", 105], ["Necklace", "RedJadeNecklace(H)", 778], ["Ring", "GoldDragonRing", 1001]),
  ];
  private unlocked = 5; // stored-hero slots unlocked of SLOTS
  private behaviour: Behaviour = "Attack";
  private autoPot = true;
  private thr = { HP: 45, MP: 30 };
  private bag = ITEMS_BAG.map((r) => [...r] as [string, number, number]);
  private tab: "Tactics" | "Bag" | "Skills" | "Stats" = "Tactics";
  private asking = -1;
  private pickedSpell: string | null = null;
  /** [class, text, bold name before the text ("" = none)]: drawn as text nodes (hero names are player data). */
  private feed: [string, string, string][] = [];
  private summonCd = 0;
  private dollKey = "";
  private resFlash = false;
  private pane!: HTMLElement;

  constructor() {
    super("hrwin", 40, 18);
    injectCss("hudm-hero-css", CSS);
    this.frame = h("div", "hframe");
    window.setInterval(() => this.tick(), 1000);
    this.el.addEventListener("mousedown", () => { this.held = true; });
    window.addEventListener("mouseup", () => { this.held = false; });
    this.log("a", "Seraphine answers your call.");
    this.renderFrame();
  }
  protected override shown(): void { this.render(); }
  private get hero(): Hero { return this.heroes[0]!; }

  private log(cls: string, text: string, who = ""): void {
    this.feed.push([cls, text, who]);
    if (this.feed.length > 12) this.feed.shift();
  }
  private count(name: string): number { return this.bag.filter((b) => b[0] === name).reduce((n, b) => n + b[2], 0); }
  private take(name: string): boolean {
    const b = this.bag.find((x) => x[0] === name && x[2] > 0);
    if (!b) return false;
    if (--b[2] === 0) this.bag.splice(this.bag.indexOf(b), 1);
    return true;
  }

  /** The fight simulation: once a second while the hero is summoned. */
  private tick(): void {
    if (this.summonCd > 0) this.summonCd--;
    const x = this.hero;
    if (x.state !== "Summoned") { if (this.open) this.refresh(false); return; }
    const before = `${x.state}/${x.level}/${this.bag.length}/${this.bag.reduce((n, b) => n + b[2], 0)}`;
    const fighting = this.behaviour === "Attack" || (this.behaviour === "CounterAttack" && Math.random() < .35) || this.behaviour === "Custom";
    if (fighting) {
      const pool = x.spells.filter((s) => x.auto.has(s[0]));
      const cast = pool.length && Math.random() < .6 ? pool[Math.floor(Math.random() * pool.length)]! : null;
      const cost = cast ? 4 + cast[2] : 0;
      if (cast && x.mp >= cost) { x.mp -= cost; this.log("", ` casts ${pretty(cast[0])} (−${cost} MP)`, x.name); }
      else if (this.behaviour !== "Custom") this.log("", " strikes a Skeleton Soldier", x.name);
      const hit = Math.round(x.maxHp * (0.04 + Math.random() * 0.12));
      x.hp = Math.max(0, x.hp - hit);
      if (Math.random() < .5) this.log("r", `A Skeleton Soldier hits ${x.name} for ${hit}`);
      if (Math.random() < .3) this.gainExp(Math.round(expNeed(x.level) * (0.01 + Math.random() * 0.03)));
    } else {
      x.hp = Math.min(x.maxHp, x.hp + Math.round(x.maxHp * .02));
      x.mp = Math.min(x.maxMp, x.mp + Math.round(x.maxMp * .03));
    }
    if (this.autoPot) for (const stat of ["HP", "MP"] as const) {
      const cur = stat === "HP" ? x.hp / x.maxHp : x.mp / x.maxMp;
      if (cur * 100 >= this.thr[stat]) continue;
      const pot = POTS[stat].find(([n]) => this.count(n) > 0);
      if (!pot || !this.take(pot[0])) continue;
      const heal = Math.round((stat === "HP" ? x.maxHp : x.maxMp) * .35);
      if (stat === "HP") x.hp = Math.min(x.maxHp, x.hp + heal); else x.mp = Math.min(x.maxMp, x.mp + heal);
      this.log("g", `Auto potion: ${pretty(pot[0])} (+${heal} ${stat})`);
    }
    if (x.hp <= 0) { x.state = "Fallen"; this.log("r", " has fallen!", x.name); }
    this.renderFrame();
    if (this.open) this.refresh(before !== `${x.state}/${x.level}/${this.bag.length}/${this.bag.reduce((n, b) => n + b[2], 0)}`);
  }
  /** Each tick redraws the centre column only (bars, feed, button), so a slider being dragged or a hover on the sides is
   *  not interrupted; a full redraw when the sides changed (state, level, potion counts) and no mouse button is held. */
  private held = false;
  private refresh(full: boolean): void {
    if (full && !this.held) { this.render(); return; }
    this.el.querySelector(".hrwin .mid")?.replaceWith(this.middle());
  }
  private gainExp(amount: number): void {
    const x = this.hero;
    x.exp += amount / expNeed(x.level);
    this.log("a", `${x.name} gains ${amount.toLocaleString("en-GB")} EXP`);
    if (x.exp >= 1) {
      x.exp -= 1; x.level++;
      x.maxHp += x.cls === MirClass.Warrior ? 28 : 14; x.maxMp += x.cls === MirClass.Wizard ? 30 : 12; x.hp = x.maxHp; x.mp = x.maxMp;
      for (const s of SPELLS[x.cls] ?? []) if (s[2] === x.level) { x.spells.push(s); this.log("a", `${x.name} learns ${pretty(s[0])}!`); }
      this.log("a", ` reached level ${x.level}!`, x.name);
    }
  }

  private summon(): void {
    const x = this.hero;
    if (x.state === "Summoned") { x.state = "Resting"; this.log("", `${x.name} is dismissed.`); }
    else if (x.state === "Resting" && !this.summonCd) { x.state = "Summoned"; this.summonCd = 5; this.log("a", `${x.name} answers your call.`); }
    else if (x.state === "Fallen") { x.state = "Resting"; x.hp = Math.round(x.maxHp * .3); x.mp = Math.round(x.maxMp * .3); this.resFlash = true; this.log("g", `${x.name} is revived (−50,000 Gold).`); }
    this.renderFrame(); this.render();
  }

  private renderFrame(): void {
    const x = this.hero, f = this.frame;
    f.hidden = x.state === "Resting";
    f.classList.toggle("fallen", x.state === "Fallen");
    const pt = h("div", "pt", h("b", "", String(x.level)));
    pt.title = "Open the Hero window (H)";
    pt.addEventListener("click", () => this.toggle());
    avatar(pt, x, false);
    const md = h("span", "md", BEHAVIOURS.find((b) => b[0] === this.behaviour)![1]);
    md.title = "Hero behaviour (click to change)";
    md.addEventListener("click", () => { const i = BEHAVIOURS.findIndex((b) => b[0] === this.behaviour); this.setBehaviour(BEHAVIOURS[(i + 1) % 4]![0]); });
    const bar = (cls: string, v: number) => { const i = h("i"); i.style.width = Math.max(0, Math.min(100, v * 100)) + "%"; return h("div", "b " + cls, i); };
    f.replaceChildren(pt, h("div", "in2", h("div", "n", h("span", "", x.state === "Fallen" ? x.name + " ✝" : x.name), md),
      bar("hp", x.hp / x.maxHp), bar("mp", x.mp / x.maxMp), bar("xp", x.exp)));
  }
  private setBehaviour(b: Behaviour): void {
    this.behaviour = b;
    this.log("a", `Behaviour: ${BEHAVIOURS.find((x) => x[0] === b)![1]}`);
    this.renderFrame();
    if (this.open) this.render();
  }

  private render(): void {
    const scroll = this.pane?.scrollTop ?? 0;
    this.el.replaceChildren(h("div", "panel", this.titleBar("HERO", "Close (H)"), h("div", "hbody", this.roster(), this.middle(), this.side())));
    this.pane.scrollTop = scroll;
  }

  private roster(): HTMLElement {
    const r = h("div", "roster");
    const card = (x: Hero, i: number) => {
      const av = h("div", "av", h("i"), h("u"));
      avatar(av, x, true);
      (av.lastChild as HTMLElement).style.background = CLASS_COLOUR[x.cls] ?? "var(--ac)";
      const c = h("div", "hc" + (i === 0 ? " act" : "") + (this.asking === i ? " ask" : ""), av,
        h("div", "t", h("b", "", x.name), h("small", "", `Lv ${x.level} ${CLASS_NAME[x.cls]}`)), h("span", "chip " + x.state, i === 0 ? x.state : "Stored"));
      if (i > 0) c.addEventListener("click", () => { this.asking = this.asking === i ? -1 : i; this.render(); });
      return c;
    };
    r.append(h("div", "lbl", "Active hero"), card(this.hero, 0), h("div", "lbl", "Stored heroes", h("b", "", `${this.heroes.length - 1} / ${this.unlocked}`)));
    for (let i = 1; i < this.heroes.length; i++) {
      r.append(card(this.heroes[i]!, i));
      if (this.asking === i) {
        const busy = this.hero.state === "Summoned";
        const yes = h("div", "btn hmain" + (busy ? " dis" : ""), "Make active");
        yes.addEventListener("click", () => {
          const [a, b] = [this.heroes[0]!, this.heroes[i]!];
          this.heroes[0] = b; this.heroes[i] = a; this.asking = -1; this.pickedSpell = null;
          this.log("a", `${b.name} is now your active hero.`);
          this.renderFrame(); this.render();
        });
        const no = h("div", "btn", "Cancel");
        no.addEventListener("click", () => { this.asking = -1; this.render(); });
        r.append(h("div", "ask2", h("span", "", busy ? `Dismiss ${this.hero.name} first to swap heroes.` : `Make ${this.heroes[i]!.name} your active hero?`), h("div", "", yes, no)));
      }
    }
    for (let i = this.heroes.length - 1; i < SLOTS; i++) {
      if (i < this.unlocked) {
        const first = i === this.heroes.length - 1 && this.count("SealedHero") > 0;
        const e = h("div", "hc emp", ...(first ? [itemCell(this.el, "SealedHero", 789)] : []), h("span", "", first ? "Unseal a hero" : "Empty slot"));
        e.title = "Open a Sealed Hero from the hero's bag to fill this slot";
        e.addEventListener("click", () => {
          if (!this.take("SealedHero")) return;
          this.heroes.push(makeHero("Orin", MirClass.Wizard, false, 22, "Resting", [592, 58, 107], ["Weapon", "DragonStaff", 58], ["Armour", "TitanArmour(M)", 592], ["Helmet", "BrassHelmet", 107], ["Necklace", "HeroNecklace", 682], ["Ring", "HeroRing", 1019]));
          this.log("a", "A Sealed Hero is opened: Orin joins your roster.");
          this.render();
        });
        r.append(e);
      } else r.append(h("div", "hc lock", "🔒 Locked slot"));
    }
    return r;
  }

  private middle(): HTMLElement {
    const x = this.hero;
    const head = h("div", "head", h("div", "nm", h("h3", "", x.name), h("small", "", h("em", "", `Level ${x.level} ${CLASS_NAME[x.cls]}`), ` · ${x.female ? "Female" : "Male"} · Hero of GMRyu`)),
      h("span", "chip " + x.state, x.state));
    (head.querySelector("em") as HTMLElement).style.color = CLASS_COLOUR[x.cls] ?? "var(--ac)";

    const slot = ([slotName, item, image]: Gear) => itemCell(this.el, item ? `${slotName}: ${item}` : slotName, image);
    const col = (items: Gear[]) => { const c = h("div", "col"); for (const g of items) c.append(slot(g)); return c; };
    const g = x.gear; // weapon, armour, helmet, neck, brL, brR, ring, ring, belt, boots, torch, amulet, stone, mount
    // The doll needs the figure's size, so it is drawn just after the first render for a hero; the figure is kept between
    // renders (the window re-renders every second) so the async draw measures and fills the element that stays on screen.
    if (this.dollKey !== x.name || !this.figure) {
      this.dollKey = x.name;
      const org = h("div", "org");
      const fig = h("div", "figure", org);
      this.figure = fig;
      window.setTimeout(() => void dollInto(fig, org, x.doll, x.female));
    }
    const figure = this.figure;
    figure.classList.toggle("fallen", x.state === "Fallen");
    figure.classList.toggle("res", this.resFlash);
    this.resFlash = false;
    figure.querySelector(".ghost")?.remove();
    if (x.state === "Fallen") figure.append(h("div", "ghost", "FALLEN"));
    const dollRow = h("div", "dollrow", col([g[0]!, g[1]!, g[10]!, g[4]!, g[6]!]), figure, col([g[2]!, g[3]!, g[11]!, g[5]!, g[7]!]));
    const bottom = h("div", "bottom"); for (const s of [g[8]!, g[9]!, g[12]!, g[13]!]) bottom.append(slot(s));

    const bar = (cls: string, a: string, b: string, v: number) => { const i = h("i"); i.style.width = Math.max(0, Math.min(100, v * 100)) + "%"; return h("div", "bar3 " + cls, i, h("span", "", h("em", "", a), h("em", "", b))); };
    const bars = h("div", "bars", bar("hp", "HP", `${x.hp} / ${x.maxHp}`, x.hp / x.maxHp), bar("mp", "MP", `${x.mp} / ${x.maxMp}`, x.mp / x.maxMp),
      bar("xp", "", "", x.exp));
    bars.lastElementChild!.setAttribute("title", `EXP ${(x.exp * 100).toFixed(2)}%`);

    const main = h("div", "btn hmain", x.state === "Summoned" ? "Dismiss" : x.state === "Fallen" ? "Revive · 50,000 Gold" : this.summonCd ? `Summon (${this.summonCd}s)` : "Summon");
    if (x.state === "Resting" && this.summonCd) main.classList.add("dis");
    main.addEventListener("click", () => this.summon());
    const bagBtn = h("div", "btn", "Hero bag");
    bagBtn.addEventListener("click", () => { this.tab = "Bag"; this.render(); });
    const feed = h("div", "feed");
    for (const [cls, text, who] of this.feed) feed.append(h("div", cls, ...(who ? [h("b", "", who)] : []), text));
    return h("div", "mid", head, dollRow, bottom, bars, h("div", "acts", main, bagBtn), feed);
  }
  private figure: HTMLElement | null = null;

  private side(): HTMLElement {
    const tabs = h("div", "tabs");
    for (const t of ["Tactics", "Bag", "Skills", "Stats"] as const) {
      const e = h("div", "tab2" + (this.tab === t ? " on" : ""), t.toUpperCase());
      e.addEventListener("click", () => { this.tab = t; this.pane.scrollTop = 0; this.render(); });
      tabs.append(e);
    }
    this.pane = h("div", "pane");
    if (this.tab === "Tactics") this.tactics(this.pane); else if (this.tab === "Bag") this.bagPane(this.pane); else if (this.tab === "Skills") this.skillsPane(this.pane); else this.statsPane(this.pane);
    return h("div", "side", tabs, this.pane);
  }

  private tactics(p: HTMLElement): void {
    const bhv = h("div", "bhv");
    for (const [key, name, text, icon] of BEHAVIOURS) {
      const b = h("div", "bh" + (this.behaviour === key ? " on" : ""));
      b.innerHTML = `<svg viewBox="0 0 24 24"><path d="${icon}"/></svg>`;
      b.append(h("div", "", h("b", "", name), h("small", "", text)));
      b.addEventListener("click", () => this.setBehaviour(key));
      bhv.append(b);
    }
    const sw = h("div", "sw" + (this.autoPot ? " on" : ""));
    sw.addEventListener("click", () => { this.autoPot = !this.autoPot; this.render(); });
    const pots = h("div", "pots" + (this.autoPot ? "" : " off"));
    for (const stat of ["HP", "MP"] as const) {
      const pot = POTS[stat].find(([n]) => this.count(n) > 0) ?? POTS[stat][0]!;
      const n = this.count(pot[0]);
      const range = h("input");
      range.type = "range"; range.min = "5"; range.max = "90"; range.step = "5"; range.value = String(this.thr[stat]);
      const pc = h("div", "pc", this.thr[stat] + "%");
      range.addEventListener("input", () => { this.thr[stat] = +range.value; pc.textContent = range.value + "%"; });
      pots.append(h("div", "pot", itemCell(this.el, pot[0], n ? pot[1] : -1, n),
        h("div", "mid2", h("b", "", `${stat} below ${this.thr[stat]}%`), h("div", "", n ? `${pretty(pot[0])} · ${n} left` : `No ${stat} potions in the hero's bag`), range), pc));
    }
    const belt = h("div", "belt2");
    for (const [i, name] of [[7, "(HP)DrugXL"], [8, "(MP)DrugXL"]] as const) {
      const n = this.count(name);
      const cell = itemCell(this.el, name, n ? (name.startsWith("(HP)") ? 813 : 814) : -1, n);
      belt.append(h("div", "k", h("span", "", String(i)), cell));
    }
    belt.append(h("span", "hint2", "Keys 7 and 8 use the hero's belt."));
    p.append(h("div", "lbl", "Behaviour", h("b", "", BEHAVIOURS.find((b) => b[0] === this.behaviour)![1])), bhv,
      h("div", "lbl", "Auto potion", sw), pots, h("div", "lbl", "Hero belt"), belt);
  }

  private bagPane(p: HTMLElement): void {
    const grid = h("div", "grid");
    for (let i = 0; i < 40; i++) {
      const it = this.bag[i];
      grid.append(it ? itemCell(this.el, pretty(it[0]), it[1], it[2]) : itemCell(this.el, "", -1));
    }
    const wt = 38 + this.bag.length * 3, max = 120;
    const bar = h("div", "bar3", h("i")); (bar.firstChild as HTMLElement).style.width = (wt / max) * 100 + "%";
    p.append(h("div", "lbl", "Hero bag", h("b", "", `${this.bag.length} / 40`)), grid, h("div", "wt", `Weight ${wt} / ${max}`, bar),
      h("div", "hint2", "Potions here feed Auto potion and the hero belt. Open a Sealed Hero to add a hero to your roster."));
  }

  /** Mock stats from level and class (the hero's own Stats; the desktop shows them on the hero's character page). */
  private statsPane(p: HTMLElement): void {
    const x = this.hero, lv = x.level, w = x.cls === MirClass.Warrior, m = x.cls === MirClass.Wizard, t = x.cls === MirClass.Taoist;
    const r = (a: number, b: number) => `${Math.round(a)}-${Math.round(b)}`;
    const main: [string, string, boolean][] = [
      ["AC", r(lv * (w ? .5 : .25), lv * (w ? .95 : .55)), w], ["MAC", r(lv * .25, lv * .55), false],
      ["DC", r(lv * (w ? .6 : .2), lv * (w ? 1.9 : .5)), w], ["MC", m ? r(lv * .55, lv * 1.8) : "0-0", m], ["SC", t ? r(lv * .55, lv * 1.6) : "0-0", t],
      ["HP", `${x.hp} / ${x.maxHp}`, false], ["MP", `${x.mp} / ${x.maxMp}`, false],
    ];
    const extra: [string, string][] = [
      ["Accuracy", String(10 + Math.round(lv / 6))], ["Agility", String(12 + Math.round(lv / 5))], ["Attack speed", String(w ? 2 : 0)], ["Luck", "+2"],
      ["Magic resist", "4"], ["Poison resist", "3"], ["HP regen", "+6"], ["MP regen", "+4"], ["Bag weight", `${38 + this.bag.length * 3} / 120`], ["Hand weight", `${18 + lv / 4 | 0} / ${40 + lv}`],
    ];
    const grid = (rows: [string, string, boolean?][]) => { const g = h("div", "st"); for (const [k, v, hi] of rows) g.append(h("div", hi ? "hi" : "", h("span", "", k), h("b", "", v))); return g; };
    p.append(h("div", "lbl", "Combat", h("b", "", `Level ${lv} ${CLASS_NAME[x.cls]}`)), grid(main), h("div", "lbl", "Attributes"), grid(extra),
      h("div", "hint2", `The class's main stats are highlighted. EXP ${(x.exp * 100).toFixed(2)}% towards level ${lv + 1}.`));
  }

  private skillsPane(p: HTMLElement): void {
    const x = this.hero;
    const list = h("div", "");
    const all = SPELLS[x.cls] ?? [];
    for (const s of all) {
      const known = x.spells.includes(s);
      const ic = h("div", "ic");
      void libPicture(Lib.MagIcon2, s[1] * 2).then((pic) => { if (pic) ic.style.backgroundImage = `url(${pic.url})`; });
      const lv = known ? Math.min(3, Math.floor((x.level - s[2]) / 4)) : 0;
      const pip = h("span", "pip"); for (let i = 0; i < 3; i++) pip.append(h("i", i < lv ? "on" : ""));
      const row = h("div", "sk" + (known ? "" : " lock") + (this.pickedSpell === s[0] ? " sel" : ""), ic,
        h("div", "t", pretty(s[0]), h("small", "", known ? "" : `Learns at level ${s[2]}`)));
      if (known) {
        (row.querySelector("small") as HTMLElement).append(`Level`, pip);
        const sw = h("div", "sw" + (x.auto.has(s[0]) ? " on" : ""));
        sw.title = "Auto-cast in fights";
        sw.addEventListener("click", (e) => { e.stopPropagation(); if (!x.auto.delete(s[0])) x.auto.add(s[0]); this.render(); });
        row.append(sw);
        row.addEventListener("click", () => { this.pickedSpell = this.pickedSpell === s[0] ? null : s[0]; this.render(); });
      }
      list.append(row);
    }
    const bar = h("div", "hbar" + (this.pickedSpell ? " armed" : ""));
    x.bar.forEach((key, i) => {
      const d = h("div", "", h("span", "", `⇧F${i + 1}`));
      const sp = key ? all.find((s) => s[0] === key) : null;
      if (sp) { d.title = pretty(sp[0]); void libPicture(Lib.MagIcon2, sp[1] * 2).then((pic) => { if (pic) d.style.backgroundImage = `url(${pic.url})`; }); }
      d.addEventListener("click", () => {
        if (this.pickedSpell) { for (let j = 0; j < 8; j++) if (x.bar[j] === this.pickedSpell) x.bar[j] = null; x.bar[i] = this.pickedSpell; this.pickedSpell = null; }
        else x.bar[i] = null;
        this.render();
      });
      bar.append(d);
    });
    p.append(h("div", "lbl", "Hero skill bar", h("b", "", "Shift + F1-F8")), bar,
      h("div", "hint2", this.pickedSpell ? `Click a slot to put ${pretty(this.pickedSpell)} there.` : "Pick a skill, then a slot. Click a filled slot to clear it."),
      h("div", "lbl", `${CLASS_NAME[x.cls]} skills`, h("b", "", `${x.spells.length} / ${all.length} · switch = auto-cast`)), list);
  }
}

/** The hero's class portrait: Prguse HeroAvatar (1400 + class + 10 * gender), +370 for the larger HeroManageAvatar. */
function avatar(el: HTMLElement, x: Hero, big: boolean): void {
  const idx = 1400 + x.cls + 10 * (x.female ? 1 : 0) + (big ? 370 : 0);
  const target = (el.querySelector("i") as HTMLElement | null) ?? el;
  void libPicture(Lib.Prguse, idx).then((pic) => { if (pic) target.style.backgroundImage = `url(${pic.url})`; });
}
