// Help window mock for the scene lab's UI tester (window style from hudWindow.ts), a modern take on the desktop's
// HelpDialog (three shortcut tables, then Help.Lib pictures 0-41 under 22 titles, Previous / Next): a home page (tip of the
// day, popular topics, a new-player checklist that ticks as topics are read), a sectioned topic list with read marks and
// page counts, search across topics, shortcuts and chat commands, and topic pages that show the desktop's Help.Lib
// pictures in a framed viewer (page dots, arrows, click to enlarge) with a one-line summary, an "Open the window" button
// into the matching window mock, related topics and a helpful / not helpful vote. Shortcuts are read from the client's
// current key binds (src/game/keybinds.ts) as keycaps; chat commands are the server's prefixes and player @commands
// (PlayerObject.Chat). Topic titles and picture counts are HelpDialog's; summaries, tips and votes are mock.

import { getKeyBind, KeybindOptions, type KeybindOption } from "../src/game/keybinds.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss } from "./hudWindow.ts";

/** Windows the help can open (hudMock.ts maps them). */
export type HelpTarget = "keybinds" | "inventory" | "skills" | "character" | "group" | "trade" | "merchant" | "quests" | "mount" | "fishing" | "hero" | "guild";
type Special = "shortcuts" | "chat";
interface Topic { id: string; title: string; pages: number; first: number; summary: string; icon: string; open?: [HelpTarget, string]; special?: Special; related: string[] }

/** HelpDialog's titles and Help.Lib picture counts in order (the pictures are numbered on from 0). */
const ORDER: [string, number][] = [
  ["Movements", 1], ["Attacking", 1], ["Collecting Items", 1], ["Health", 1], ["Skills", 2], ["Mana", 1], ["Chatting", 1], ["Groups", 1],
  ["Durability", 1], ["Purchasing", 1], ["Selling", 1], ["Repairing", 1], ["Trading", 1], ["Inspecting", 1], ["Statistics", 6], ["Quests", 4],
  ["Mounts", 2], ["Fishing", 1], ["Gems and Orbs", 1], ["Heroes", 5], ["Guild Buffs", 3], ["Awakening", 5],
];
const FIRST = new Map<string, number>();
{ let n = 0; for (const [t, c] of ORDER) { FIRST.set(t, n); n += c; } }
const I = {
  key: "M3 7h18v10H3zM7 11h.01M11 11h.01M15 11h.01M7 14h10", chat: "M4 5h16v11H9l-5 4z", walk: "M13 4a2 2 0 110 4 2 2 0 010-4M10 21l2-7 3 3v4M8 12l3-3 4 1 2 3",
  sword: "M5 19L19 5M14 5h5v5M5 14l5 5", bag: "M6 8h12l-1 12H7zM9 8V6a3 3 0 016 0v2", heart: "M12 20s-7-4.5-7-10a4 4 0 017-2.5A4 4 0 0119 10c0 5.5-7 10-7 10z",
  star: "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z", drop: "M12 3s6 7 6 11a6 6 0 01-12 0c0-4 6-11 6-11z",
  group: "M9 11a3 3 0 100-6 3 3 0 000 6zM3 20c0-3 3-5 6-5s6 2 6 5M17 11a2.5 2.5 0 100-5M21 20c0-2.5-1.5-4-4-4.5", shield: "M12 3l8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6z",
  coin: "M12 4a8 8 0 110 16 8 8 0 010-16zM12 8v8M9.5 10.5c0-1 1-1.5 2.5-1.5s2.5.5 2.5 1.5-1 1.5-2.5 1.5-2.5.5-2.5 1.5 1 1.5 2.5 1.5 2.5-.5 2.5-1.5",
  tag: "M3 12V4h8l10 10-8 8zM7 8h.01", wrench: "M14 6a4 4 0 015 5l-9 9-4-4 9-9zM5 19l-1 1", swap: "M4 8h14l-3-3M20 16H6l3 3", eye: "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 110 6 3 3 0 010-6",
  chart: "M4 20V10M10 20V4M16 20v-7M22 20H2", scroll: "M6 4h11a2 2 0 012 2v12a2 2 0 01-2 2H8M6 4a2 2 0 00-2 2v2h4M6 4a2 2 0 012 2v12M11 9h5M11 13h5",
  horse: "M5 20v-6l3-5 2-5 3 2 5 1 1 3-3 1-1 4v5M9 14h7", fish: "M3 12c3-5 9-6 14-2l4-3v10l-4-3c-5 4-11 3-14-2zM15 11h.01", gem: "M6 3h12l3 6-9 12L3 9zM3 9h18M9 3l3 6 3-6",
  hero: "M12 3l2 4 4 .5-3 3 1 4.5-4-2-4 2 1-4.5-3-3 4-.5zM6 20h12", flag: "M5 21V4M5 4h12l-2 4 2 4H5", spark: "M12 2v6M12 16v6M2 12h6M16 12h6M5 5l4 4M15 15l4 4M5 19l4-4M15 9l4-4",
  home: "M3 11l9-8 9 8M5 9v11h14V9", book: "M4 5a2 2 0 012-2h14v16H6a2 2 0 00-2 2zM4 19V5",
};
const t = (id: string, title: string, summary: string, icon: string, related: string[], open?: [HelpTarget, string], special?: Special): Topic =>
  ({ id, title, pages: special ? 1 : ORDER.find((o) => o[0] === title)![1], first: special ? -1 : FIRST.get(title)!, summary, icon, related, open, special });
const SECTIONS: [string, Topic[]][] = [
  ["Getting started", [
    t("shortcuts", "Shortcuts", "Every key the game listens to, read from your current key bindings.", I.key, ["chat", "movements"], ["keybinds", "Key bindings"], "shortcuts"),
    t("chat", "Chat commands", "Prefixes that send a message to a whisper, your group, your guild or everyone nearby, and @commands.", I.chat, ["chatting", "groups"], undefined, "chat"),
    t("movements", "Movements", "Left-click the ground to walk, right-click to run; hold the button to keep moving.", I.walk, ["attacking", "mounts"]),
    t("attacking", "Attacking", "Click a monster to attack it. Hold Shift to attack on the spot without stepping.", I.sword, ["movements", "health", "skills"]),
    t("collecting", "Collecting Items", "Stand on an item and press Tab or click your character to pick it up; it goes into your bag.", I.bag, ["purchasing", "selling"], ["inventory", "Inventory"]),
    t("health", "Health", "The red bar is your HP. Drink potions from the belt (1-6) before it runs out.", I.heart, ["mana", "attacking"]),
  ]],
  ["Skills and stats", [
    t("skills", "Skills", "Learn skills from books, put them on F1-F8 and train them by using them.", I.star, ["mana", "statistics"], ["skills", "Skills"]),
    t("mana", "Mana", "The blue bar is your MP. Spells cost it; it comes back over time and with potions.", I.drop, ["skills", "health"]),
    t("statistics", "Statistics", "What AC, MAC, DC, MC, SC, Accuracy, Agility and the rest do for you.", I.chart, ["skills", "durability"], ["character", "Character"]),
  ]],
  ["Playing together", [
    t("chatting", "Chatting", "Type in the chat box and press Enter. Prefixes pick who hears it.", I.chat, ["chat", "groups"]),
    t("groups", "Groups", "Invite players with the group window; members nearby share EXP.", I.group, ["chatting", "trading"], ["group", "Group"]),
    t("trading", "Trading", "Ask a player to trade; nothing moves until both of you confirm.", I.swap, ["groups", "inspecting"], ["trade", "Trade request"]),
    t("inspecting", "Inspecting", "Ctrl + right-click a player to see what they are wearing.", I.eye, ["trading", "statistics"]),
  ]],
  ["Merchants and gear", [
    t("purchasing", "Purchasing", "Talk to a merchant NPC and pick Buy to see what they sell.", I.coin, ["selling", "repairing"], ["merchant", "Merchant"]),
    t("selling", "Selling", "Sell items to merchants that deal in that kind of goods.", I.tag, ["purchasing", "collecting"], ["merchant", "Merchant"]),
    t("repairing", "Repairing", "Merchants repair worn gear for gold; special repair keeps the maximum durability.", I.wrench, ["durability", "purchasing"], ["merchant", "Merchant"]),
    t("durability", "Durability", "Gear wears down as you fight. At zero it stops working until it is repaired.", I.shield, ["repairing", "statistics"]),
    t("gems", "Gems and Orbs", "Put a gem or orb on an item to add to its stats. It can fail.", I.gem, ["awakening", "statistics"]),
    t("awakening", "Awakening", "Raise an item past its normal limits with materials, at the awakening NPC.", I.spark, ["gems", "statistics"]),
  ]],
  ["Adventures", [
    t("quests", "Quests", "NPCs with quests show a mark over their head. Track up to five on screen.", I.scroll, ["groups", "heroes"], ["quests", "Quests"]),
    t("mounts", "Mounts", "Equip a mount and press M to ride. Keep its loyalty up and fit it with gear.", I.horse, ["movements", "fishing"], ["mount", "Mount"]),
    t("fishing", "Fishing", "With a rod, face the water and cast; reel in when the float dips.", I.fish, ["mounts", "quests"], ["fishing", "Fishing"]),
    t("heroes", "Heroes", "A companion who fights beside you, with its own gear, bag, skills and behaviour.", I.hero, ["groups", "skills"], ["hero", "Hero"]),
    t("guildbuffs", "Guild Buffs", "Guilds spend buff points to give every member a boost.", I.flag, ["groups", "heroes"], ["guild", "Guild"]),
  ]],
];
const TOPICS = SECTIONS.flatMap(([, ts]) => ts);
const byId = (id: string) => TOPICS.find((x) => x.id === id)!;
const CHECKLIST: [string, string][] = [["movements", "Move and run"], ["attacking", "Fight a monster"], ["collecting", "Pick up loot"], ["health", "Drink a potion"],
  ["skills", "Use a skill"], ["groups", "Join a group"], ["purchasing", "Buy from a merchant"], ["quests", "Take a quest"]];
const TIPS = [
  "Hold Shift while you attack to stay on the spot instead of walking into the monster.",
  "Ctrl + right-click a player to inspect their equipment.",
  "Start a chat line with !! to talk to your group, or !~ for your guild.",
  "Put potions on the belt (1-6) so you can drink them without opening your bag.",
  "Merchants only buy the kind of goods they sell.",
  "A special repair costs more but keeps your item's maximum durability.",
  "Ctrl + F1-F8 uses the second skill bar.",
  "Track up to five quests at once; their objectives show on the right of the screen.",
];
/** Shortcut rows: [label, KeybindOptions name(s)]; a range "F1-F8" when two names. */
const SHORTCUTS: [string, [string, (keyof typeof KeybindOptions)[]][]][] = [
  ["Windows", [["Inventory", ["Inventory"]], ["Character", ["Equipment"]], ["Skills", ["Skills"]], ["Quests", ["Quests"]], ["Group", ["Group"]], ["Friends", ["Friends"]],
    ["Guild", ["Guilds"]], ["Big map", ["Bigmap"]], ["Minimap", ["Minimap"]], ["Game shop", ["GameShop"]], ["Options", ["Options"]], ["Help", ["Help"]],
    ["Mount", ["MountWindow"]], ["Fishing", ["Fishing"]], ["Creatures", ["Creature"]], ["Ranking", ["Ranking"]], ["Key bindings", ["Keybind"]], ["Close all", ["Closeall"]]]],
  ["Combat", [["Skill bar", ["Bar1Skill1", "Bar1Skill8"]], ["Second skill bar", ["Bar2Skill1", "Bar2Skill8"]], ["Hero skill bar", ["HeroSkill1", "HeroSkill8"]],
    ["Attack mode", ["ChangeAttackmode"]], ["Pet mode", ["ChangePetmode"]], ["Spell lock-on (hold)", ["TargetSpellLockOn"]]]],
  ["Moving and items", [["Belt", ["Belt1", "Belt6"]], ["Pick up", ["Pickup"]], ["Mount / dismount", ["Mount"]], ["Auto run", ["Autorun"]], ["Trade", ["Trade"]],
    ["Creature pickup", ["CreaturePickup"]], ["Hide interface", ["Cameramode"]], ["Screenshot", ["Screenshot"]]]],
  ["Session", [["Log out", ["Logout"]], ["Exit game", ["Exit"]]]],
];
/** PlayerObject.Chat prefixes, and @commands any player may use. */
const PREFIXES: [string, string][] = [["/name text", "Whisper to a player"], ["!!text", "Group chat"], ["!~text", "Guild chat"], ["!#text", "Mentor chat"],
  ["!text", "Shout to everyone nearby"], [":)text", "Talk to your partner (married)"], ["@command", "Run a command (below)"]];
export const COMMANDS: [string, string][] = [["@allowtrade", "Allow or refuse trade requests"], ["@allowguild", "Allow or refuse guild invites"], ["@allowobserve", "Allow others to watch you"],
  ["@enablegrouprecall", "Let your group leader recall you"], ["@grouprecall", "Recall your group to you (leader)"], ["@recalllover", "Recall your partner"],
  ["@ride", "Mount or dismount"], ["@toggletransform", "Show or hide your transform"], ["@time", "Show the server time"], ["@roll", "Roll a die"], ["@map", "Show the map you are on"],
  ["@leaveguild", "Leave your guild"]];

/** KeyBindSettings.GetKey-style text of a bind: "Ctrl + F1", "" when unbound. */
function keyText(name: keyof typeof KeybindOptions): string[] {
  const b = getKeyBind(KeybindOptions[name] as KeybindOption);
  if (!b || !b.key) return [];
  const out: string[] = [];
  if (b.requireTilde === 1) out.push("~");
  if (b.requireCtrl === 1) out.push("Ctrl");
  if (b.requireAlt === 1) out.push("Alt");
  if (b.requireShift === 1) out.push("Shift");
  out.push(b.key.replace(/^D(\d)$/, "$1").replace(/^NumPad(\d)$/, "Num $1").replace("Escape", "Esc").replace("PrintScreen", "PrtSc"));
  return out;
}

const CSS = `
.hudm .hpwin { width: 940px; }
.hudm .hpwin .panel { height: 660px; display: flex; flex-direction: column; }
.hudm .hpwin .top { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hpwin .srch { flex: 1; position: relative; }
.hudm .hpwin .srch svg { position: absolute; left: 10px; top: 8px; width: 15px; height: 15px; fill: none; stroke: var(--dim); stroke-width: 2; stroke-linecap: round; }
.hudm .hpwin .srch input { width: 100%; height: 32px; box-sizing: border-box; padding: 0 12px 0 32px; border-radius: 16px; border: 1px solid var(--fr); background: #05070c; color: var(--tx); font-size: 13px; outline: none; }
.hudm .hpwin .srch input:focus { border-color: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .hpwin .prog { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .hpwin .prog svg { width: 34px; height: 34px; transform: rotate(-90deg); }
.hudm .hpwin .prog circle { fill: none; stroke-width: 4; } .hudm .hpwin .prog .bg { stroke: #05070c; } .hudm .hpwin .prog .fg { stroke: var(--ac); stroke-linecap: round; filter: drop-shadow(0 0 3px var(--glow)); transition: stroke-dashoffset .5s; }
.hudm .hpwin .prog b { display: block; font-size: 13px; color: #fff; }
.hudm .hpwin .hbody { flex: 1; min-height: 0; display: flex; }
.hudm .hpwin .nav { width: 220px; flex: none; overflow-y: auto; padding: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hpwin .sec { padding: 10px 8px 4px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hpwin .ti { display: flex; align-items: center; gap: 8px; padding: 5px 8px; border-radius: var(--rad); cursor: pointer; font-size: 12px; color: var(--tx); border: 1px solid transparent; }
.hudm .hpwin .ti:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .hpwin .ti.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .hpwin svg.ic { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hpwin .ti .ic { color: var(--dim); } .hudm .hpwin .ti.on .ic { color: var(--ac); }
.hudm .hpwin .ti span { flex: 1; } .hudm .hpwin .ti small { font-size: 9px; color: var(--dim); }
.hudm .hpwin .ti i { width: 6px; height: 6px; border-radius: 50%; flex: none; background: var(--ac); box-shadow: 0 0 5px var(--glow); }
.hudm .hpwin .ti.read i { background: transparent; box-shadow: none; }
.hudm .hpwin .page { flex: 1; min-width: 0; overflow-y: auto; padding: 14px 18px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hpwin h2 { margin: 0; font-size: 20px; color: #fff; display: flex; align-items: center; gap: 10px; }
.hudm .hpwin h2 .ic { width: 22px; height: 22px; color: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .hpwin .crumb { font-size: 10px; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 4px; }
.hudm .hpwin .crumb a { color: var(--ac); cursor: pointer; }
.hudm .hpwin .sum { margin: 4px 0 10px; font-size: 13px; line-height: 1.5; color: var(--tx); }
/* Home */
.hudm .hpwin .tip { position: relative; display: flex; gap: 14px; align-items: center; padding: 14px 16px; border-radius: var(--rad); overflow: hidden;
  border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); background: linear-gradient(110deg, color-mix(in srgb, var(--ac) 22%, var(--pb)), color-mix(in srgb, var(--pb) 70%, #000) 70%); }
.hudm .hpwin .tip::after { content: ""; position: absolute; inset: 0; background: linear-gradient(105deg, transparent 35%, rgba(255,255,255,.07) 50%, transparent 65%); animation: hudm-tipsweep 6s ease-in-out infinite; }
@keyframes hudm-tipsweep { from { transform: translateX(-100%); } to { transform: translateX(100%); } }
.hudm .hpwin .tip .bulb { width: 40px; height: 40px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: #fff; background: color-mix(in srgb, var(--ac) 40%, #000); box-shadow: 0 0 14px var(--glow); }
.hudm .hpwin .tip .bulb svg { width: 20px; height: 20px; fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hpwin .tip small { display: block; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac); }
.hudm .hpwin .tip p { margin: 3px 0 0; font-size: 13px; color: #fff; } .hudm .hpwin .tip .nx { margin-left: auto; position: relative; z-index: 1; }
.hudm .hpwin .lbl2 { margin: 16px 0 8px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hpwin .cards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
.hudm .hpwin .card { display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); transition: transform .15s; }
.hudm .hpwin .card:hover { border-color: var(--ac); transform: translateY(-2px); box-shadow: 0 0 calc(10px * var(--gl)) var(--glow); }
.hudm .hpwin .card .ic { width: 24px; height: 24px; color: var(--ac); }
.hudm .hpwin .card b { display: block; font-size: 12px; color: #fff; } .hudm .hpwin .card small { font-size: 10px; line-height: 1.35; color: var(--dim); }
.hudm .hpwin .check { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .hpwin .ck { display: flex; align-items: center; gap: 8px; padding: 7px 10px; border-radius: var(--rad); cursor: pointer; font-size: 12px; border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .hpwin .ck:hover { border-color: var(--ac); }
.hudm .hpwin .ck u { width: 16px; height: 16px; flex: none; border-radius: 50%; text-decoration: none; display: flex; align-items: center; justify-content: center; font-size: 10px; border: 1px solid var(--fr); }
.hudm .hpwin .ck.done { color: var(--dim); } .hudm .hpwin .ck.done u { color: #fff; border-color: var(--ac); background: var(--ac); box-shadow: 0 0 6px var(--glow); }
/* Viewer */
.hudm .hpwin .view { position: relative; display: flex; align-items: center; justify-content: center; height: 412px; border-radius: var(--rad); overflow: hidden; cursor: zoom-in;
  border: 1px solid var(--fr); background: radial-gradient(ellipse at 50% 40%, color-mix(in srgb, var(--ac) 10%, #05070c), #020305); box-shadow: inset 0 0 30px rgba(0,0,0,.8); }
.hudm .hpwin .view img { max-width: calc(100% - 20px); max-height: calc(100% - 20px); image-rendering: auto; transition: opacity .25s; }
.hudm .hpwin .view .ld { color: var(--dim); font-size: 12px; }
.hudm .hpwin .view .arw { position: absolute; top: 50%; translate: 0 -50%; width: 32px; height: 52px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-size: 22px; color: #fff;
  border-radius: var(--rad); background: rgba(0,0,0,.55); border: 1px solid var(--fr); opacity: .6; }
.hudm .hpwin .view .arw:hover { opacity: 1; border-color: var(--ac); box-shadow: 0 0 8px var(--glow); }
.hudm .hpwin .view .arw.l { left: 8px; } .hudm .hpwin .view .arw.r { right: 8px; }
.hudm .hpwin .view .pg { position: absolute; right: 10px; top: 8px; font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 9px; color: #fff; background: rgba(0,0,0,.6); }
.hudm .hpwin .dots { display: flex; justify-content: center; gap: 6px; margin: 8px 0 0; }
.hudm .hpwin .dots i { width: 8px; height: 8px; border-radius: 50%; cursor: pointer; background: var(--fr); transition: width .2s; }
.hudm .hpwin .dots i.on { width: 22px; border-radius: 4px; background: var(--ac); box-shadow: 0 0 6px var(--glow); }
.hudm .hpwin .acts { display: flex; align-items: center; gap: 8px; margin-top: 12px; flex-wrap: wrap; }
.hudm .hpwin .btn.pmain { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(8px * var(--gl)) var(--glow); }
.hudm .hpwin .rel { display: flex; gap: 6px; flex-wrap: wrap; }
.hudm .hpwin .rel div { display: flex; align-items: center; gap: 5px; padding: 3px 10px; border-radius: 12px; cursor: pointer; font-size: 11px; border: 1px solid var(--fr); background: var(--pb); }
.hudm .hpwin .rel div:hover { border-color: var(--ac); color: #fff; } .hudm .hpwin .rel .ic { width: 13px; height: 13px; color: var(--ac); }
.hudm .hpwin .vote { margin-left: auto; display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--dim); }
.hudm .hpwin .vote div { width: 28px; height: 24px; display: flex; align-items: center; justify-content: center; cursor: pointer; border-radius: calc(var(--rad) * .7); border: 1px solid var(--fr); font-size: 13px; }
.hudm .hpwin .vote div:hover { border-color: var(--ac); } .hudm .hpwin .vote div.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); }
/* Lightbox */
.hudm .hpwin .lb { position: absolute; inset: 0; z-index: 5; display: flex; align-items: center; justify-content: center; cursor: zoom-out; background: rgba(0,0,0,.85); border-radius: var(--rad); animation: hudm-lbin .2s ease-out; }
.hudm .hpwin .lb img { max-width: 96%; max-height: 92%; box-shadow: 0 0 40px rgba(0,0,0,.9); border: 1px solid var(--fr); }
@keyframes hudm-lbin { from { opacity: 0; } }
/* Shortcuts / chat */
.hudm .hpwin .sc { display: grid; grid-template-columns: 1fr 1fr; gap: 4px 18px; }
.hudm .hpwin .sr { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 5px 8px; border-radius: var(--rad); font-size: 12px; }
.hudm .hpwin .sr:nth-child(4n+1), .hudm .hpwin .sr:nth-child(4n+2) { background: color-mix(in srgb, var(--frl) 4%, transparent); }
.hudm .hpwin .sr.hit { box-shadow: inset 0 0 0 1px var(--ac); }
.hudm .hpwin .kcs { display: flex; align-items: center; gap: 3px; flex: none; } .hudm .hpwin .kcs em { font-style: normal; font-size: 10px; color: var(--dim); }
.hudm .hpwin .kc { display: inline-flex; align-items: center; justify-content: center; min-width: 22px; height: 20px; padding: 0 6px; box-sizing: border-box; font: 600 10px ui-monospace, Consolas, monospace;
  color: #fff; border-radius: 4px; border: 1px solid var(--fr); border-bottom-width: 2px; background: linear-gradient(180deg, var(--pc), var(--pb)); }
.hudm .hpwin .kc.m { color: var(--ac); } .hudm .hpwin .kc.un { color: var(--dim); font-style: italic; font-family: inherit; }
.hudm .hpwin code { font: 600 12px ui-monospace, Consolas, monospace; color: var(--ac); }
.hudm .hpwin .cp { cursor: pointer; font-size: 10px; color: var(--dim); padding: 2px 8px; border-radius: 9px; border: 1px solid var(--fr); }
.hudm .hpwin .cp:hover { color: #fff; border-color: var(--ac); } .hudm .hpwin .cp.ok { color: #7fdc8a; border-color: #4a9a5a; }
.hudm .hpwin .res { display: flex; flex-direction: column; gap: 6px; }
.hudm .hpwin .rs { display: flex; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .hpwin .rs:hover { border-color: var(--ac); } .hudm .hpwin .rs .ic { width: 20px; height: 20px; color: var(--ac); }
.hudm .hpwin .rs b { font-size: 12px; color: #fff; } .hudm .hpwin .rs small { display: block; font-size: 11px; color: var(--dim); }
.hudm .hpwin mark { background: color-mix(in srgb, var(--ac) 40%, transparent); color: #fff; border-radius: 2px; }
.hudm .hpwin .none { padding: 30px; text-align: center; color: var(--dim); }
`;

const svg = (d: string, cls = "ic") => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("viewBox", "0 0 24 24"); s.setAttribute("class", cls); s.innerHTML = `<path d="${d}"/>`; return s; };

export class HelpMock extends HudWindow {
  /** Opens one of the HUD's window mocks (hudMock.ts). */
  onOpen: (target: HelpTarget) => void = () => {};
  private topic: string | null = null;
  private pageNo = 0;
  private read = new Set<string>();
  private votes = new Map<string, 1 | -1>();
  private tip = Math.floor(Math.random() * TIPS.length);
  private query = "";
  private nav!: HTMLElement;
  private page!: HTMLElement;
  private prog!: HTMLElement;
  private input!: HTMLInputElement;

  constructor() {
    super("hpwin", 70, 12);
    injectCss("hudm-help-css", CSS);
    this.build();
    window.addEventListener("keydown", (e) => {
      // Only the front window takes the arrows (the lab pans with them otherwise).
      if (!this.isFront || !this.topic || (e.target as HTMLElement).closest?.("input, textarea, select")) return;
      if (e.code === "ArrowLeft" || e.code === "ArrowRight") { e.preventDefault(); e.stopImmediatePropagation(); this.turn(e.code === "ArrowLeft" ? -1 : 1); }
    }, { capture: true });
  }
  protected override shown(): void { this.render(); }

  /** Opens the window on a topic (or home). */
  show(topic: string | null): void { this.go(topic); this.toggle(true); }

  private build(): void {
    this.input = h("input");
    this.input.placeholder = "Search topics, keys and commands…";
    this.input.addEventListener("keydown", (e) => { e.stopPropagation(); if (e.key === "Escape") { this.input.value = ""; this.query = ""; this.render(); } });
    this.input.addEventListener("input", () => { this.query = this.input.value; this.renderPage(); });
    const srch = h("div", "srch", this.input);
    srch.prepend(svg("M11 4a7 7 0 110 14 7 7 0 010-14zM20 20l-4-4", ""));
    this.prog = h("div", "prog");
    this.nav = h("div", "nav");
    this.page = h("div", "page");
    this.el.append(h("div", "panel", this.titleBar("HELP", "Close (?)"), h("div", "top", srch, this.prog), h("div", "hbody", this.nav, this.page)));
  }

  private go(id: string | null, page = 0): void {
    this.topic = id; this.pageNo = page;
    if (id) this.read.add(id);
    this.query = ""; this.input.value = "";
    this.render();
    this.page.scrollTop = 0;
  }
  private turn(d: number): void {
    const tp = this.topic ? byId(this.topic) : null;
    if (!tp || tp.pages < 2) return;
    this.pageNo = (this.pageNo + d + tp.pages) % tp.pages;
    this.renderPage();
  }

  private render(): void { this.renderProg(); this.renderNav(); this.renderPage(); }

  private renderProg(): void {
    const n = TOPICS.filter((x) => this.read.has(x.id)).length, c = 2 * Math.PI * 14;
    const ring = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    ring.setAttribute("viewBox", "0 0 34 34");
    ring.innerHTML = `<circle class="bg" cx="17" cy="17" r="14"/><circle class="fg" cx="17" cy="17" r="14" stroke-dasharray="${c}" stroke-dashoffset="${c * (1 - n / TOPICS.length)}"/>`;
    this.prog.replaceChildren(ring, h("div", "", h("b", "", `${n} / ${TOPICS.length}`), "topics read"));
  }

  private renderNav(): void {
    const home = h("div", "ti read" + (this.topic === null ? " on" : ""), svg(I.home), h("span", "", "Home"), h("i"));
    home.addEventListener("click", () => this.go(null));
    const nodes: HTMLElement[] = [home];
    for (const [sec, ts] of SECTIONS) {
      nodes.push(h("div", "sec", sec));
      for (const tp of ts) {
        const e = h("div", "ti" + (this.topic === tp.id ? " on" : "") + (this.read.has(tp.id) ? " read" : ""), svg(tp.icon), h("span", "", tp.title), h("small", "", tp.pages > 1 ? `${tp.pages} pages` : ""), h("i"));
        e.title = this.read.has(tp.id) ? "Read" : "Not read yet";
        e.addEventListener("click", () => this.go(tp.id));
        nodes.push(e);
      }
    }
    this.nav.replaceChildren(...nodes);
  }

  private renderPage(): void {
    const q = this.query.trim().toLowerCase();
    if (q) { this.search(q); return; }
    if (!this.topic) { this.home(); return; }
    const tp = byId(this.topic);
    const sec = SECTIONS.find(([, ts]) => ts.includes(tp))![0];
    const crumbHome = h("a", "", "Help");
    crumbHome.addEventListener("click", () => this.go(null));
    const nodes: (HTMLElement | SVGElement)[] = [h("div", "crumb", crumbHome, ` › ${sec}`), h("h2", "", svg(tp.icon), tp.title), h("div", "sum", tp.summary)];
    if (tp.special === "shortcuts") nodes.push(this.shortcuts(""));
    else if (tp.special === "chat") nodes.push(this.chat(""));
    else nodes.push(...this.viewer(tp));
    const acts = h("div", "acts");
    if (tp.open) {
      const b = h("div", "btn pmain", `Open ${tp.open[1]}`);
      b.addEventListener("click", () => this.onOpen(tp.open![0]));
      acts.append(b);
    }
    const rel = h("div", "rel");
    for (const id of tp.related) { const r = byId(id); const c = h("div", "", svg(r.icon), r.title); c.addEventListener("click", () => this.go(id)); rel.append(c); }
    const vote = h("div", "vote", "Was this helpful?");
    for (const [v, s] of [[1, "👍"], [-1, "👎"]] as const) {
      const d = h("div", this.votes.get(tp.id) === v ? "on" : "", s);
      d.addEventListener("click", () => { if (this.votes.get(tp.id) === v) this.votes.delete(tp.id); else this.votes.set(tp.id, v); this.renderPage(); });
      vote.append(d);
    }
    if (this.votes.has(tp.id)) vote.firstChild!.textContent = "Thanks for the feedback!";
    acts.append(rel, vote);
    nodes.push(h("div", "lbl2", "Related"), acts);
    this.page.replaceChildren(...nodes);
  }

  /** The Help.Lib picture viewer: arrows, page badge, dots, click for the lightbox. */
  private viewer(tp: Topic): HTMLElement[] {
    const view = h("div", "view", h("div", "ld", "Loading picture…"));
    const idx = tp.first + this.pageNo;
    void libPicture(Lib.Help, idx).then((pic) => {
      const ld = view.querySelector(".ld");
      if (!ld) return;
      if (!pic) { ld.textContent = "Picture not found"; return; }
      const img = h("img");
      img.src = pic.url; img.alt = `${tp.title} ${this.pageNo + 1}`;
      ld.replaceWith(img);
    });
    view.addEventListener("click", () => {
      const img = view.querySelector("img");
      if (!img) return;
      const lb = h("div", "lb", h("img"));
      (lb.firstChild as HTMLImageElement).src = img.src;
      lb.addEventListener("click", () => lb.remove());
      this.el.querySelector(".panel")!.append(lb);
    });
    const out: HTMLElement[] = [view];
    if (tp.pages > 1) {
      for (const [cls, d, s] of [["l", -1, "‹"], ["r", 1, "›"]] as const) {
        const a = h("div", "arw " + cls, s);
        a.title = d < 0 ? "Previous page (←)" : "Next page (→)";
        a.addEventListener("click", (e) => { e.stopPropagation(); this.turn(d); });
        view.append(a);
      }
      view.append(h("div", "pg", `${this.pageNo + 1} / ${tp.pages}`));
      const dots = h("div", "dots");
      for (let i = 0; i < tp.pages; i++) { const d = h("i", i === this.pageNo ? "on" : ""); d.addEventListener("click", () => { this.pageNo = i; this.renderPage(); }); dots.append(d); }
      out.push(dots);
    }
    return out;
  }

  private home(): void {
    const tipTxt = h("p", "", TIPS[this.tip]!);
    const next = h("div", "btn nx", "Next tip");
    next.addEventListener("click", () => { this.tip = (this.tip + 1) % TIPS.length; tipTxt.textContent = TIPS[this.tip]!; });
    const bulb = h("div", "bulb", svg("M9 18h6M10 21h4M12 3a6 6 0 00-4 10.5c.7.7 1 1.5 1 2.5h6c0-1 .3-1.8 1-2.5A6 6 0 0012 3z", ""));
    const tip = h("div", "tip", bulb, h("div", "", h("small", "", "Tip of the day"), tipTxt), next);
    const cards = h("div", "cards");
    for (const id of ["shortcuts", "attacking", "skills", "quests", "heroes", "chat"]) {
      const tp = byId(id);
      const c = h("div", "card", svg(tp.icon), h("div", "", h("b", "", tp.title), h("small", "", tp.summary)));
      c.addEventListener("click", () => this.go(id));
      cards.append(c);
    }
    const check = h("div", "check");
    for (const [id, label] of CHECKLIST) {
      const done = this.read.has(id);
      const c = h("div", "ck" + (done ? " done" : ""), h("u", "", done ? "✓" : ""), label);
      c.addEventListener("click", () => this.go(id));
      check.append(c);
    }
    const n = CHECKLIST.filter(([id]) => this.read.has(id)).length;
    this.page.replaceChildren(h("div", "crumb", "Help"), h("h2", "", svg(I.book), "Welcome to Mir"), h("div", "sum", "Pick a topic on the left, search above, or start with the basics below. ← → turn the pages of a topic."),
      tip, h("div", "lbl2", "Popular topics"), cards, h("div", "lbl2", `New player checklist · ${n} / ${CHECKLIST.length}`), check);
  }

  /** The shortcut table from the current key binds; `q` highlights matching rows. */
  private shortcuts(q: string): HTMLElement {
    const wrap = h("div", "");
    for (const [group, rows] of SHORTCUTS) {
      const grid = h("div", "sc");
      for (const [label, names] of rows) {
        const kc = h("div", "kcs");
        const a = keyText(names[0]!);
        const cap = (parts: string[]) => parts.map((p, i) => h("span", "kc" + (i < parts.length - 1 ? " m" : ""), p));
        if (!a.length) kc.append(h("span", "kc un", "Unbound"));
        else if (names.length === 2) { const b = keyText(names[1]!); kc.append(...cap(a), h("em", "", "to"), ...cap(b.length ? [b[b.length - 1]!] : [])); }
        else kc.append(...cap(a));
        const row = h("div", "sr" + (q && (label.toLowerCase().includes(q) || a.join(" ").toLowerCase().includes(q)) ? " hit" : ""), h("span", "", label), kc);
        grid.append(row);
      }
      wrap.append(h("div", "lbl2", group), grid);
    }
    return wrap;
  }

  private chat(q: string): HTMLElement {
    const row = ([cmd, info]: [string, string]) => {
      const cp = h("span", "cp", "Copy");
      cp.addEventListener("click", () => {
        void navigator.clipboard?.writeText(cmd.split(" ")[0]!.replace(/text$/, "")).catch(() => {});
        cp.textContent = "Copied"; cp.classList.add("ok");
        window.setTimeout(() => { cp.textContent = "Copy"; cp.classList.remove("ok"); }, 1200);
      });
      return h("div", "sr" + (q && (cmd.toLowerCase().includes(q) || info.toLowerCase().includes(q)) ? " hit" : ""), h("span", "", h("code", "", cmd), "  ", info), cp);
    };
    const a = h("div", "sc"); for (const r of PREFIXES) a.append(row(r));
    const b = h("div", "sc"); for (const r of COMMANDS) b.append(row(r));
    return h("div", "", h("div", "lbl2", "Chat prefixes"), a, h("div", "lbl2", "Player commands"), b);
  }

  private search(q: string): void {
    const mark = (text: string) => {
      const i = text.toLowerCase().indexOf(q);
      const s = h("span", "");
      if (i < 0) { s.textContent = text; return s; }
      s.append(text.slice(0, i), h("mark", "", text.slice(i, i + q.length)), text.slice(i + q.length));
      return s;
    };
    const res = h("div", "res");
    for (const tp of TOPICS.filter((x) => (x.title + " " + x.summary).toLowerCase().includes(q))) {
      const r = h("div", "rs", svg(tp.icon), h("div", "", h("b", "", mark(tp.title)), h("small", "", mark(tp.summary))));
      r.addEventListener("click", () => this.go(tp.id));
      res.append(r);
    }
    const nodes: HTMLElement[] = [h("div", "crumb", "Search"), h("h2", "", `Results for “${this.query.trim()}”`)];
    nodes.push(h("div", "lbl2", "Topics"), res.childElementCount ? res : h("div", "none", "No topics match."));
    const keys = this.shortcuts(q);
    const keyHits = keys.querySelectorAll(".sr.hit");
    if (keyHits.length) { const g = h("div", "sc"); for (const k of keyHits) g.append(k); nodes.push(h("div", "lbl2", "Shortcuts"), g); }
    const chat = this.chat(q);
    const chatHits = chat.querySelectorAll(".sr.hit");
    if (chatHits.length) { const g = h("div", "sc"); for (const k of chatHits) g.append(k); nodes.push(h("div", "lbl2", "Chat commands"), g); }
    this.page.replaceChildren(...nodes);
  }
}
