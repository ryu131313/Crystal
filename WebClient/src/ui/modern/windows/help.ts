// Ported from play/hudHelp.ts (HelpMock): the new UI's help window, a modern take on the desktop's HelpDialog (the
// classic port is src/ui/helpWindow.ts: three shortcut tables, then Help.Lib pictures 0-41 under 22 titles). Kept from
// the mock: a home page (tip, popular topics, a new-player checklist ticked by reading topics), a sectioned topic list
// with read marks and page counts, search across topics, shortcuts and chat commands, and topic pages with the Help.Lib
// pictures in a framed viewer (arrows, page dots, click to enlarge), a summary, an "Open ..." button and related topics.
// Changes from the mock:
//  - shortcuts and every key named in a summary or tip are the LIVE bindings (keybinds.ts keyLabel), re-rendered when a
//    binding changes (onKeyBindsChange: the window is only marked dirty while closed);
//  - the page arrows go through the input router (the key() hook, so only while this is the front window), never a
//    window key listener; Esc closes the lightbox first, and clears the search while typing in it;
//  - "Open ..." opens the real new-UI window by key (hidden while no window is registered for it); "Trading" sends a
//    trade request to the player you face (Trade.request -> C.TradeRequest, the T key);
//  - hidden: the helpful / not helpful vote (no backend) and the "Open merchant" buttons (merchants need an NPC: the
//    topics say to speak to one); the endless tip sweep animation is dropped (Performance.md);
//  - the 8-cell player belt and the hero belt on 9 / 0 (phase 2.9) in the shortcut table;
//  - read marks kept per browser in localStorage (a per-viewer convenience; works without it);
//  - showTopic(host, name) opens the window on a topic by HelpDialog title ("Quests", "Mounts", "Purchasing", ...) or
//    topic id, like the classic HelpWindow.displayPageNamed;
//  - search is debounced (core/widgets.ts searchBox); a narrow layout for a 1024 px layer.

import { isTextTarget } from "../../../game/input.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange } from "../../../game/keybinds.ts";
import { Lib, libPicture } from "../../mir/controls.ts";
import { asButton, h, injectCss, later, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { button, progressRing, searchBox } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { WINDOW_KEYS } from "../hud/gameMenu.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

type BindName = keyof typeof KeybindOptions;
/** A text that names keys: a function of the live key label. */
type Txt = string | ((k: (name: BindName) => string) => string);
/** Where a topic's "Open" button goes: a new-UI window key, or "trade" (a trade request). */
type Target = string;
type Special = "shortcuts" | "chat";
interface Topic { id: string; title: string; pages: number; first: number; summary: Txt; icon: IconName; open?: [Target, string]; special?: Special; related: string[] }

/** HelpDialog's titles and Help.Lib picture counts in order (the pictures are numbered on from 0). */
const ORDER: [string, number][] = [
  ["Movements", 1], ["Attacking", 1], ["Collecting Items", 1], ["Health", 1], ["Skills", 2], ["Mana", 1], ["Chatting", 1], ["Groups", 1],
  ["Durability", 1], ["Purchasing", 1], ["Selling", 1], ["Repairing", 1], ["Trading", 1], ["Inspecting", 1], ["Statistics", 6], ["Quests", 4],
  ["Mounts", 2], ["Fishing", 1], ["Gems and Orbs", 1], ["Heroes", 5], ["Guild Buffs", 3], ["Awakening", 5],
];
const FIRST = new Map<string, number>();
{ let n = 0; for (const [t, c] of ORDER) { FIRST.set(t, n); n += c; } }

const W = WINDOW_KEYS;
const t = (id: string, title: string, summary: Txt, icon: IconName, related: string[], open?: [Target, string], special?: Special): Topic =>
  ({ id, title, pages: special ? 1 : ORDER.find((o) => o[0] === title)![1], first: special ? -1 : FIRST.get(title)!, summary, icon, related, open, special });
const SECTIONS: [string, Topic[]][] = [
  ["Getting started", [
    t("shortcuts", "Shortcuts", "Every key the game listens to, read from your current key bindings.", "key", ["chat", "movements"], [W.keybinds, "Key bindings"], "shortcuts"),
    t("chat", "Chat commands", "Prefixes that send a message to a whisper, your group, your guild or everyone nearby, and @commands.", "chat", ["chatting", "groups"], undefined, "chat"),
    t("movements", "Movements", "Left-click the ground to walk, right-click to run; hold the button to keep moving.", "walk", ["attacking", "mounts"]),
    t("attacking", "Attacking", "Click a monster to attack it. Hold Shift to attack on the spot without stepping.", "sword", ["movements", "health", "skills"]),
    t("collecting", "Collecting Items", (k) => `Stand on an item and press ${k("Pickup")} to pick it up; it goes into your bag.`, "bag", ["purchasing", "selling"], [W.inventory, "Inventory"]),
    t("health", "Health", (k) => `The red bar is your HP. Drink potions from the belt (${k("Belt1")} to ${k("Belt8")}) before it runs out.`, "heart", ["mana", "attacking"]),
  ]],
  ["Skills and stats", [
    t("skills", "Skills", (k) => `Learn skills from books, put them on the skill bar (${k("Bar1Skill1")} to ${k("Bar1Skill8")}) and train them by using them.`, "star", ["mana", "statistics"], [W.skills, "Skills"]),
    t("mana", "Mana", "The blue bar is your MP. Spells cost it; it comes back over time and with potions.", "drop", ["skills", "health"]),
    t("statistics", "Statistics", "What AC, MAC, DC, MC, SC, Accuracy, Agility and the rest do for you.", "chart", ["skills", "durability"], [W.character, "Character"]),
  ]],
  ["Playing together", [
    t("chatting", "Chatting", "Type in the chat box and press Enter. Prefixes pick who hears it.", "chat", ["chat", "groups"]),
    t("groups", "Groups", "Invite players with the group window; members nearby share EXP.", "group", ["chatting", "trading"], [W.group, "Group"]),
    t("trading", "Trading", (k) => `Face a player and press ${k("Trade")} to ask for a trade; nothing moves until both of you confirm.`, "swap", ["groups", "inspecting"], ["trade", "Trade request"]),
    t("inspecting", "Inspecting", "Ctrl + right-click a player to see what they are wearing.", "eye", ["trading", "statistics"]),
  ]],
  ["Merchants and gear", [
    t("purchasing", "Purchasing", "Speak to a merchant NPC and pick Buy to see what they sell.", "coinGold", ["selling", "repairing"]),
    t("selling", "Selling", "Speak to a merchant NPC to sell items; each merchant deals in its own kind of goods.", "tag", ["purchasing", "collecting"]),
    t("repairing", "Repairing", "Speak to a merchant NPC to repair worn gear for gold; special repair keeps the maximum durability.", "wrench", ["durability", "purchasing"]),
    t("durability", "Durability", "Gear wears down as you fight. At zero it stops working until it is repaired.", "shield", ["repairing", "statistics"]),
    t("gems", "Gems and Orbs", "Put a gem or orb on an item to add to its stats. It can fail.", "gem", ["awakening", "statistics"]),
    t("awakening", "Awakening", "Raise an item past its normal limits with materials, at the awakening NPC.", "spark", ["gems", "statistics"]),
  ]],
  ["Adventures", [
    t("quests", "Quests", "NPCs with quests show a mark over their head. Track up to five on screen.", "scroll", ["groups", "heroes"], [W.quests, "Quests"]),
    t("mounts", "Mounts", (k) => `Equip a mount and press ${k("Mount")} to ride. Keep its loyalty up and fit it with gear.`, "horse", ["movements", "fishing"], [W.mount, "Mount"]),
    t("fishing", "Fishing", "With a rod, face the water and cast; reel in when the float dips.", "fish", ["mounts", "quests"], [W.fishing, "Fishing"]),
    t("heroes", "Heroes", "A companion who fights beside you, with its own gear, bag, skills and behaviour.", "hero", ["groups", "skills"], [W.hero, "Hero"]),
    t("guildbuffs", "Guild Buffs", "Guilds spend buff points to give every member a boost.", "flag", ["groups", "heroes"], [W.guild, "Guild"]),
  ]],
];
const TOPICS = SECTIONS.flatMap(([, ts]) => ts);
const TOPIC_BY_ID = new Map(TOPICS.map((x) => [x.id, x]));
const byId = (id: string) => TOPIC_BY_ID.get(id)!;
const CHECKLIST: [string, string][] = [["movements", "Move and run"], ["attacking", "Fight a monster"], ["collecting", "Pick up loot"], ["health", "Drink a potion"],
  ["skills", "Use a skill"], ["groups", "Join a group"], ["purchasing", "Buy from a merchant"], ["quests", "Take a quest"]];
const TIPS: Txt[] = [
  "Hold Shift while you attack to stay on the spot instead of walking into the monster.",
  "Ctrl + right-click a player to inspect their equipment.",
  "Start a chat line with !! to talk to your group, or !~ for your guild.",
  (k) => `Put potions on the belt (${k("Belt1")} to ${k("Belt8")}) so you can drink them without opening your bag.`,
  "Merchants only buy the kind of goods they sell.",
  "A special repair costs more but keeps your item's maximum durability.",
  (k) => `${k("Bar2Skill1")} to ${k("Bar2Skill8")} use the second skill bar.`,
  "Track up to five quests at once; their objectives show on the right of the screen.",
];
/** Shortcut rows: [label, KeybindOptions name(s)]; a range "F1 to F8" when two names. */
const SHORTCUTS: [string, [string, BindName[]][]][] = [
  ["Windows", [["Inventory", ["Inventory"]], ["Character", ["Equipment"]], ["Skills", ["Skills"]], ["Quests", ["Quests"]], ["Group", ["Group"]], ["Friends", ["Friends"]],
    ["Guild", ["Guilds"]], ["Big map", ["Bigmap"]], ["Minimap", ["Minimap"]], ["Game shop", ["GameShop"]], ["Options", ["Options"]], ["Help", ["Help"]],
    ["Mount", ["MountWindow"]], ["Fishing", ["Fishing"]], ["Creatures", ["Creature"]], ["Ranking", ["Ranking"]], ["Key bindings", ["Keybind"]], ["Close all", ["Closeall"]]]],
  ["Combat", [["Skill bar", ["Bar1Skill1", "Bar1Skill8"]], ["Second skill bar", ["Bar2Skill1", "Bar2Skill8"]], ["Hero skill bar", ["HeroSkill1", "HeroSkill8"]],
    ["Attack mode", ["ChangeAttackmode"]], ["Pet mode", ["ChangePetmode"]], ["Spell lock-on (hold)", ["TargetSpellLockOn"]]]],
  ["Moving and items", [["Belt", ["Belt1", "Belt8"]], ["Hero belt", ["HeroBelt1", "HeroBelt2"]], ["Pick up", ["Pickup"]], ["Mount / dismount", ["Mount"]],
    ["Auto run", ["Autorun"]], ["Trade", ["Trade"]], ["Creature pickup", ["CreaturePickup"]], ["Hide interface", ["Cameramode"]], ["Screenshot", ["Screenshot"]]]],
  ["Session", [["Log out", ["Logout"]], ["Exit game", ["Exit"]]]],
];
/** PlayerObject.Chat prefixes, and @commands any player may use. */
const PREFIXES: [string, string][] = [["/name text", "Whisper to a player"], ["!!text", "Group chat"], ["!~text", "Guild chat"], ["!#text", "Mentor chat"],
  ["!text", "Shout to everyone nearby"], [":)text", "Talk to your partner (married)"], ["@command", "Run a command (below)"]];
const COMMANDS: [string, string][] = [["@allowtrade", "Allow or refuse trade requests"], ["@allowguild", "Allow or refuse guild invites"], ["@allowobserve", "Allow others to watch you"],
  ["@enablegrouprecall", "Let your group leader recall you"], ["@grouprecall", "Recall your group to you (leader)"], ["@recalllover", "Recall your partner"],
  ["@ride", "Mount or dismount"], ["@toggletransform", "Show or hide your transform"], ["@time", "Show the server time"], ["@roll", "Roll a die"], ["@map", "Show the map you are on"],
  ["@leaveguild", "Leave your guild"]];

/** keyLabel split into keycaps with short key names ("Alt", "Q"; "D1" -> "1", "NumPad1" -> "Num 1"); [] when unbound. */
function keyParts(name: BindName): string[] {
  const s = keyLabel(KeybindOptions[name]);
  if (!s) return [];
  return s.split(" + ").map((p) => p.replace(/^D(\d)$/, "$1").replace(/^NumPad(\d)$/, "Num $1").replace(/^Escape$/, "Esc").replace(/^PrintScreen$/, "PrtSc"));
}
/** A key as text in a sentence ("Ctrl + F1"; "(unbound)"). */
const keyText = (name: BindName): string => keyParts(name).join(" + ") || "(unbound)";
const txt = (s: Txt): string => typeof s === "string" ? s : s(keyText);

/** Classic HelpDialog titles that are not topic titles here. */
const CLASSIC_ALIASES: Readonly<Record<string, string>> = { "shortcut information": "shortcuts", "chat shortcuts": "chat", "durability:": "durability" };
/** A topic id by id, HelpDialog title or alias (case-insensitive). */
function topicOf(name: string): string | undefined {
  const n = name.trim().toLowerCase();
  if (TOPIC_BY_ID.has(n)) return n;
  return TOPICS.find((x) => x.title.toLowerCase() === n)?.id ?? CLASSIC_ALIASES[n];
}

const READ_STORE = "crystal.ui.helpRead";
function loadRead(): Set<string> {
  try {
    const v: unknown = JSON.parse(localStorage.getItem(READ_STORE) ?? "[]");
    return new Set(Array.isArray(v) ? v.filter((x): x is string => typeof x === "string" && TOPIC_BY_ID.has(x)) : []);
  } catch { return new Set(); }
}
function saveRead(read: ReadonlySet<string>): void {
  try { localStorage.setItem(READ_STORE, JSON.stringify([...read])); } catch { /* storage off: in memory only */ }
}

const CSS = `
.hudm .hpwin { width: 940px; }
.hudm .hpwin .panel { height: 660px; display: flex; flex-direction: column; }
.hudm .hpwin .top { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hpwin .top .ui-search { flex: 1; }
.hudm .hpwin .prog { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .hpwin .prog b { display: block; font-size: 13px; color: #fff; }
.hudm .hpwin .hbody { flex: 1; min-height: 0; display: flex; }
.hudm .hpwin .nav { width: 220px; flex: none; overflow-y: auto; padding: 8px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hpwin .sec { padding: 10px 8px 4px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hpwin .ti { display: flex; align-items: center; gap: 8px; padding: 5px 8px; border-radius: var(--rad); cursor: pointer; font-size: 12px; color: var(--tx); border: 1px solid transparent; }
.hudm .hpwin .ti:hover { background: color-mix(in srgb, var(--frl) 6%, transparent); }
.hudm .hpwin .ti.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 14%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .hpwin svg.ic { stroke-width: 1.7; }
.hudm .hpwin .ti .ic { color: var(--dim); } .hudm .hpwin .ti.on .ic { color: var(--ac); }
.hudm .hpwin .ti span { flex: 1; } .hudm .hpwin .ti small { font-size: 9px; color: var(--dim); }
.hudm .hpwin .ti i { width: 6px; height: 6px; border-radius: 50%; flex: none; background: var(--ac); box-shadow: 0 0 5px var(--glow); }
.hudm .hpwin .ti.read i { background: transparent; box-shadow: none; }
.hudm .hpwin .page { flex: 1; min-width: 0; overflow-y: auto; padding: 14px 18px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hpwin h2 { margin: 0; font-size: 20px; color: #fff; display: flex; align-items: center; gap: 10px; }
.hudm .hpwin h2 .ic { width: 22px; height: 22px; color: var(--ac); }
.hudm .hpwin .crumb { font-size: 10px; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); margin-bottom: 4px; }
.hudm .hpwin .crumb a { color: var(--ac); cursor: pointer; }
.hudm .hpwin .sum { margin: 4px 0 10px; font-size: 13px; line-height: 1.5; color: var(--tx); }
/* Home */
.hudm .hpwin .tip { display: flex; gap: 14px; align-items: center; padding: 14px 16px; border-radius: var(--rad);
  border: 1px solid color-mix(in srgb, var(--ac) 50%, transparent); background: linear-gradient(110deg, color-mix(in srgb, var(--ac) 22%, var(--pb)), color-mix(in srgb, var(--pb) 70%, #000) 70%); }
.hudm .hpwin .tip .bulb { width: 40px; height: 40px; flex: none; border-radius: 50%; display: flex; align-items: center; justify-content: center; color: #fff; background: color-mix(in srgb, var(--ac) 40%, #000); box-shadow: 0 0 14px var(--glow); }
.hudm .hpwin .tip .bulb svg { width: 20px; height: 20px; }
.hudm .hpwin .tip small { display: block; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--ac); }
.hudm .hpwin .tip p { margin: 3px 0 0; font-size: 13px; color: #fff; } .hudm .hpwin .tip .nx { margin-left: auto; }
.hudm .hpwin .lbl2 { margin: 16px 0 8px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .hpwin .cards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
.hudm .hpwin .card { display: flex; gap: 10px; padding: 10px; border-radius: var(--rad); cursor: pointer; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); transition: transform .15s; }
.hudm .hpwin .card:hover { border-color: var(--ac); transform: translateY(-2px); }
.hudm .hpwin .card .ic { width: 24px; height: 24px; color: var(--ac); }
.hudm .hpwin .card b { display: block; font-size: 12px; color: #fff; } .hudm .hpwin .card small { font-size: 10px; line-height: 1.35; color: var(--dim); }
.hudm .hpwin .check { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .hpwin .ck { display: flex; align-items: center; gap: 8px; padding: 7px 10px; border-radius: var(--rad); cursor: pointer; font-size: 12px; border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .hpwin .ck:hover { border-color: var(--ac); }
.hudm .hpwin .ck u { width: 16px; height: 16px; flex: none; border-radius: 50%; text-decoration: none; display: flex; align-items: center; justify-content: center; font-size: 10px; border: 1px solid var(--fr); }
.hudm .hpwin .ck.done { color: var(--dim); } .hudm .hpwin .ck.done u { color: #fff; border-color: var(--ac); background: var(--ac); }
/* Viewer */
.hudm .hpwin .view { position: relative; display: flex; align-items: center; justify-content: center; height: 412px; border-radius: var(--rad); overflow: hidden; cursor: zoom-in;
  border: 1px solid var(--fr); background: radial-gradient(ellipse at 50% 40%, color-mix(in srgb, var(--ac) 10%, #05070c), #020305); box-shadow: inset 0 0 30px rgba(0,0,0,.8); }
.hudm .hpwin .view img { max-width: calc(100% - 20px); max-height: calc(100% - 20px); }
.hudm .hpwin .view .ld { color: var(--dim); font-size: 12px; }
.hudm .hpwin .view .arw { position: absolute; top: 50%; translate: 0 -50%; width: 32px; height: 52px; display: flex; align-items: center; justify-content: center; cursor: pointer; font-size: 22px; color: #fff;
  border-radius: var(--rad); background: rgba(0,0,0,.55); border: 1px solid var(--fr); opacity: .6; }
.hudm .hpwin .view .arw:hover { opacity: 1; border-color: var(--ac); }
.hudm .hpwin .view .arw.l { left: 8px; } .hudm .hpwin .view .arw.r { right: 8px; }
.hudm .hpwin .view .pg { position: absolute; right: 10px; top: 8px; font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 9px; color: #fff; background: rgba(0,0,0,.6); }
.hudm .hpwin .dots { display: flex; justify-content: center; gap: 6px; margin: 8px 0 0; }
.hudm .hpwin .dots i { width: 8px; height: 8px; border-radius: 50%; cursor: pointer; background: var(--fr); transition: width .2s; }
.hudm .hpwin .dots i.on { width: 22px; border-radius: 4px; background: var(--ac); }
.hudm .hpwin .acts { display: flex; align-items: center; gap: 8px; margin-top: 12px; flex-wrap: wrap; }
.hudm .hpwin .rel { display: flex; gap: 6px; flex-wrap: wrap; }
.hudm .hpwin .rel div { display: flex; align-items: center; gap: 5px; padding: 3px 10px; border-radius: 12px; cursor: pointer; font-size: 11px; border: 1px solid var(--fr); background: var(--pb); }
.hudm .hpwin .rel div:hover { border-color: var(--ac); color: #fff; } .hudm .hpwin .rel .ic { width: 13px; height: 13px; color: var(--ac); }
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
.hudm .hpwin .cp:hover { color: #fff; border-color: var(--ac); } .hudm .hpwin .cp.ok { color: var(--ok); border-color: currentColor; }
.hudm .hpwin .res { display: flex; flex-direction: column; gap: 6px; }
.hudm .hpwin .rs { display: flex; gap: 10px; align-items: center; padding: 8px 10px; border-radius: var(--rad); cursor: pointer; border: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); }
.hudm .hpwin .rs:hover { border-color: var(--ac); } .hudm .hpwin .rs .ic { width: 20px; height: 20px; color: var(--ac); }
.hudm .hpwin .rs b { font-size: 12px; color: #fff; } .hudm .hpwin .rs small { display: block; font-size: 11px; color: var(--dim); }
.hudm .hpwin mark { background: color-mix(in srgb, var(--ac) 40%, transparent); color: #fff; border-radius: 2px; }
.hudm .hpwin .none { padding: 30px; text-align: center; color: var(--dim); }
/* Narrow layout (a 1024 px layer) */
.hudm.narrow .hpwin { width: 760px; }
.hudm.narrow .hpwin .panel { height: 600px; }
.hudm.narrow .hpwin .nav { width: 176px; }
.hudm.narrow .hpwin .page { padding: 12px 14px; }
.hudm.narrow .hpwin .cards { grid-template-columns: repeat(2, 1fr); }
.hudm.narrow .hpwin .view { height: 340px; }
.hudm.narrow .hpwin .sc { gap: 4px 10px; }
`;

export class HelpWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  private topic: string | null = null;
  private pageNo = 0;
  private readonly read = loadRead();
  private tip = Math.floor(Math.random() * TIPS.length);
  private query = "";
  private readonly nav = h("div", "nav");
  private readonly page = h("div", "page");
  private readonly panel: HTMLElement;
  private readonly ring = progressRing(34, 4);
  private readonly progText = h("b");
  private readonly search: ReturnType<typeof searchBox>;
  /** The title bar's close cross: its hint names the live Help key (render). */
  private readonly closeX: HTMLElement | null;
  private lightbox: HTMLElement | null = null;
  /** Bumped on every page render: a picture that loads after the page changed is dropped. */
  private viewToken = 0;

  constructor(ui: ModernUiHost) {
    super(ui.rt, "hpwin", 70, 12);
    this.ui = ui;
    injectCss("win-help", CSS);
    this.search = searchBox("Search topics, keys and commands", (q) => { this.query = q; this.renderPage(); }, { signal: this.signal });
    const prog = h("div", "prog", this.ring.el, h("div", "", this.progText, "topics read"));
    const title = this.titleBar("HELP", "Close");
    this.closeX = title.querySelector<HTMLElement>(".x");
    this.panel = h("div", "panel", title, h("div", "top", this.search.el, prog), h("div", "hbody", this.nav, this.page));
    this.el.append(this.panel);
    // Key labels are live: re-render on any rebind (dirty-when-closed).
    const off = onKeyBindsChange(() => this.invalidate());
    this.signal.addEventListener("abort", off, { once: true });
  }

  /** Opens the window on a topic id (null = home). */
  show(topic: string | null): void {
    this.go(topic);
    this.toggle(true);
  }

  /** Front window keys (WindowManager.handleKey): Esc closes the lightbox / clears the search; arrows turn the pages. */
  override key(e: KeyboardEvent): boolean {
    if (e.ctrlKey || e.altKey || e.metaKey) return false;
    if (e.key === "Escape") {
      if (this.lightbox) { this.closeLightbox(); return true; }
      if (e.target === this.search.input && this.search.value) { this.search.set(""); this.query = ""; this.renderPage(); return true; }
      return false;
    }
    if (isTextTarget(e) || this.query || !this.topic || e.shiftKey) return false;
    if (e.code !== "ArrowLeft" && e.code !== "ArrowRight") return false;
    if (byId(this.topic).pages < 2) return false;
    this.turn(e.code === "ArrowLeft" ? -1 : 1);
    return true;
  }

  protected override closed(): void { this.closeLightbox(); }

  protected override render(): void {
    if (this.closeX) this.closeX.title = `Close (${keyText("Help")}) · Esc closes the front window, Shift+Esc all`;
    this.renderProg();
    this.renderNav();
    this.renderPage();
  }

  private go(id: string | null, page = 0): void {
    this.topic = id && TOPIC_BY_ID.has(id) ? id : null;
    this.pageNo = page;
    if (this.topic && !this.read.has(this.topic)) { this.read.add(this.topic); saveRead(this.read); }
    this.query = "";
    this.search.set("");
    if (this.open) this.render();
    else this.invalidate();
    this.page.scrollTop = 0;
  }

  private turn(d: number): void {
    const tp = this.topic ? byId(this.topic) : null;
    if (!tp || tp.pages < 2) return;
    this.pageNo = (this.pageNo + d + tp.pages) % tp.pages;
    this.renderPage();
  }

  private renderProg(): void {
    let n = 0;
    for (const x of TOPICS) if (this.read.has(x.id)) n++;
    this.ring.set(n / TOPICS.length);
    setText(this.progText, `${n} / ${TOPICS.length}`);
  }

  private renderNav(): void {
    const home = asButton(h("div", "ti read" + (this.topic === null ? " on" : ""), svg("home"), h("span", "", "Home"), h("i")));
    home.addEventListener("click", () => this.go(null));
    const nodes: HTMLElement[] = [home];
    for (const [sec, ts] of SECTIONS) {
      nodes.push(h("div", "sec", sec));
      for (const tp of ts) {
        const read = this.read.has(tp.id);
        const e = asButton(h("div", "ti" + (this.topic === tp.id ? " on" : "") + (read ? " read" : ""), svg(tp.icon), h("span", "", tp.title), h("small", "", tp.pages > 1 ? `${tp.pages} pages` : ""), h("i")));
        e.title = read ? "Read" : "Not read yet";
        e.addEventListener("click", () => this.go(tp.id));
        nodes.push(e);
      }
    }
    this.nav.replaceChildren(...nodes);
  }

  private renderPage(): void {
    this.viewToken++;
    this.closeLightbox();
    const q = this.query.toLowerCase();
    if (q) { this.renderSearch(q); return; }
    if (!this.topic) { this.renderHome(); return; }
    const tp = byId(this.topic);
    const sec = SECTIONS.find(([, ts]) => ts.includes(tp))![0];
    const crumbHome = h("a", "", "Help");
    crumbHome.addEventListener("click", () => this.go(null));
    const nodes: HTMLElement[] = [h("div", "crumb", crumbHome, ` › ${sec}`), h("h2", "", svg(tp.icon), tp.title), h("div", "sum", txt(tp.summary))];
    if (tp.special === "shortcuts") nodes.push(this.shortcuts(""));
    else if (tp.special === "chat") nodes.push(this.chat(""));
    else nodes.push(...this.viewer(tp));
    const acts = h("div", "acts");
    const open = tp.open && this.opener(tp.open[0]);
    if (tp.open && open) acts.append(button(`Open ${tp.open[1]}`, open, { cls: "main" }).el);
    const rel = h("div", "rel");
    for (const id of tp.related) {
      const r = byId(id);
      const c = asButton(h("div", "", svg(r.icon), r.title));
      c.addEventListener("click", () => this.go(id));
      rel.append(c);
    }
    acts.append(rel);
    nodes.push(h("div", "lbl2", "Related"), acts);
    this.page.replaceChildren(...nodes);
  }

  /** The "Open" action of a topic, or null when that window is not registered (the button stays hidden). */
  private opener(target: Target): (() => void) | null {
    if (target === "trade") return () => this.ui.ctx.models.trade.request();
    if (!this.ui.rt.hasWindow(target)) return null;
    return () => { this.ui.openWindow(target, true); };
  }

  /** The Help.Lib picture viewer: arrows, page badge, dots, click for the lightbox. */
  private viewer(tp: Topic): HTMLElement[] {
    const ld = h("div", "ld", "Loading picture…");
    const view = h("div", "view", ld);
    const page = this.pageNo, token = this.viewToken;
    void libPicture(Lib.Help, tp.first + page).then((pic) => {
      if (token !== this.viewToken || !ld.isConnected) return;
      if (!pic) { ld.textContent = "Picture not found"; return; }
      const img = h("img");
      img.src = pic.url;
      img.alt = `${tp.title} ${page + 1}`;
      img.draggable = false;
      ld.replaceWith(img);
    });
    view.addEventListener("click", () => {
      const img = view.querySelector("img");
      if (img) this.openLightbox(img.src);
    });
    const out: HTMLElement[] = [view];
    if (tp.pages > 1) {
      for (const [cls, d, s] of [["l", -1, "‹"], ["r", 1, "›"]] as const) {
        const a = h("div", "arw " + cls, s);
        a.title = d < 0 ? "Previous page (←)" : "Next page (→)";
        a.setAttribute("role", "button");
        a.addEventListener("click", (e) => { e.stopPropagation(); this.turn(d); });
        view.append(a);
      }
      view.append(h("div", "pg", `${page + 1} / ${tp.pages}`));
      const dots = h("div", "dots");
      for (let i = 0; i < tp.pages; i++) {
        const d = h("i", i === page ? "on" : "");
        d.title = `Page ${i + 1}`;
        d.addEventListener("click", () => { this.pageNo = i; this.renderPage(); });
        dots.append(d);
      }
      out.push(dots);
    }
    return out;
  }

  private openLightbox(src: string): void {
    this.closeLightbox();
    const img = h("img");
    img.src = src;
    img.draggable = false;
    const lb = h("div", "lb", img);
    lb.title = "Click or Esc to close";
    lb.addEventListener("click", () => this.closeLightbox());
    this.lightbox = lb;
    this.panel.append(lb);
  }

  private closeLightbox(): void {
    this.lightbox?.remove();
    this.lightbox = null;
  }

  private renderHome(): void {
    const tipTxt = h("p", "", txt(TIPS[this.tip]!));
    const next = button("Next tip", () => { this.tip = (this.tip + 1) % TIPS.length; setText(tipTxt, txt(TIPS[this.tip]!)); }, { cls: "nx" });
    const tip = h("div", "tip", h("div", "bulb", svg("info", "")), h("div", "", h("small", "", "Tip"), tipTxt), next.el);
    const cards = h("div", "cards");
    for (const id of ["shortcuts", "attacking", "skills", "quests", "heroes", "chat"]) {
      const tp = byId(id);
      const c = asButton(h("div", "card", svg(tp.icon), h("div", "", h("b", "", tp.title), h("small", "", txt(tp.summary)))));
      c.addEventListener("click", () => this.go(id));
      cards.append(c);
    }
    const check = h("div", "check");
    let n = 0;
    for (const [id, label] of CHECKLIST) {
      const done = this.read.has(id);
      if (done) n++;
      const c = asButton(h("div", "ck" + (done ? " done" : ""), h("u", "", done ? "✓" : ""), label));
      c.addEventListener("click", () => this.go(id));
      check.append(c);
    }
    this.page.replaceChildren(h("div", "crumb", "Help"), h("h2", "", svg("book"), "Welcome to Mir"),
      h("div", "sum", "Pick a topic on the left, search above, or start with the basics below. ← → turn the pages of a topic."),
      tip, h("div", "lbl2", "Popular topics"), cards, h("div", "lbl2", `New player checklist · ${n} / ${CHECKLIST.length}`), check);
  }

  /** The shortcut table from the current key binds; `q` highlights matching rows. */
  private shortcuts(q: string): HTMLElement {
    const wrap = h("div", "");
    const cap = (parts: string[]) => parts.map((p, i) => h("span", "kc" + (i < parts.length - 1 ? " m" : ""), p));
    for (const [group, rows] of SHORTCUTS) {
      const grid = h("div", "sc");
      for (const [label, names] of rows) {
        const kc = h("div", "kcs");
        const a = keyParts(names[0]!);
        if (!a.length) kc.append(h("span", "kc un", "Unbound"));
        else if (names.length === 2) {
          const b = keyParts(names[1]!);
          kc.append(...cap(a));
          if (b.length) kc.append(h("em", "", "to"), ...cap([b[b.length - 1]!]));
        } else kc.append(...cap(a));
        const hit = !!q && (label.toLowerCase().includes(q) || a.join(" ").toLowerCase().includes(q));
        grid.append(h("div", "sr" + (hit ? " hit" : ""), h("span", "", label), kc));
      }
      wrap.append(h("div", "lbl2", group), grid);
    }
    return wrap;
  }

  private chat(q: string): HTMLElement {
    const row = ([cmd, info]: [string, string]) => {
      const cp = asButton(h("span", "cp", "Copy"));
      cp.title = "Copy to the clipboard";
      cp.addEventListener("click", () => {
        void navigator.clipboard?.writeText(cmd.split(" ")[0]!.replace(/text$/, "")).catch(() => {});
        setText(cp, "Copied");
        cp.classList.add("ok");
        later(() => { setText(cp, "Copy"); cp.classList.remove("ok"); }, 1200, this.signal);
      });
      const hit = !!q && (cmd.toLowerCase().includes(q) || info.toLowerCase().includes(q));
      return h("div", "sr" + (hit ? " hit" : ""), h("span", "", h("code", "", cmd), "  ", info), cp);
    };
    const a = h("div", "sc"); for (const r of PREFIXES) a.append(row(r));
    const b = h("div", "sc"); for (const r of COMMANDS) b.append(row(r));
    return h("div", "", h("div", "lbl2", "Chat prefixes"), a, h("div", "lbl2", "Player commands"), b);
  }

  private renderSearch(q: string): void {
    const mark = (text: string) => {
      const i = text.toLowerCase().indexOf(q);
      if (i < 0) return h("span", "", text);
      return h("span", "", text.slice(0, i), h("mark", "", text.slice(i, i + q.length)), text.slice(i + q.length));
    };
    const res = h("div", "res");
    for (const tp of TOPICS) {
      const sum = txt(tp.summary);
      if (!(tp.title + " " + sum).toLowerCase().includes(q)) continue;
      const r = asButton(h("div", "rs", svg(tp.icon), h("div", "", h("b", "", mark(tp.title)), h("small", "", mark(sum)))));
      r.addEventListener("click", () => this.go(tp.id));
      res.append(r);
    }
    const nodes: HTMLElement[] = [h("div", "crumb", "Search"), h("h2", "", `Results for “${this.query}”`)];
    nodes.push(h("div", "lbl2", "Topics"), res.childElementCount ? res : h("div", "none", "No topics match."));
    const keyHits = this.shortcuts(q).querySelectorAll(".sr.hit");
    if (keyHits.length) { const g = h("div", "sc"); g.append(...keyHits); nodes.push(h("div", "lbl2", "Shortcuts"), g); }
    const chatHits = this.chat(q).querySelectorAll(".sr.hit");
    if (chatHits.length) { const g = h("div", "sc"); g.append(...chatHits); nodes.push(h("div", "lbl2", "Chat commands"), g); }
    this.page.replaceChildren(...nodes);
  }
}

/** Opens the help window on a topic: a HelpDialog title ("Quests", "Mounts", "Purchasing", "Shortcut Information", ...;
 *  the classic HelpWindow.displayPageNamed names) or a topic id ("quests"); null / an unknown name opens the home page.
 *  False when no help window is registered. Other windows' help buttons call it. */
export function showTopic(host: ModernUiHost, name: string | null = null): boolean {
  const w = host.rt.window<HelpWindow>(WINDOW_KEYS.help);
  if (!w) return false;
  w.show(name ? topicOf(name) ?? null : null);
  return true;
}

registerWindow(WINDOW_KEYS.help, (host) => new HelpWindow(host));
