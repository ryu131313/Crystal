// Guild window mock for the scene lab's UI tester (window style from hudWindow.ts): a wide guild hub replacing the
// desktop's GuildDialog pages. A banner (emblem, name, guild level + EXP bar, members, fund, buff points, your rank)
// over a side menu: Overview (notice board with edit, stat cards, who is online, recent activity), Members (search,
// filters, sortable table, a member card with whisper / group / promote / demote / kick), Ranks (Crystal's eight
// GuildRankOptions as toggles per rank), Buffs (the server's 16 guild buffs with their GuildSkill icons: Icon =
// obtained, +1 active, +2 not obtained; obtain with points, activate with a countdown), Storage (item grid + fund
// deposit / withdraw), Diplomacy (allies and wars with timers) and Log (filterable activity feed).
// Buff names / icons from Build/Server/Release/Configs/GuildSettings.ini (read only, 2026-10-15; this server's buffs
// carry no stats, so the effect lines are mock), guild EXP per level from the same file; everything else is mock.

import { canon } from "./hudPeople.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { HudWindow, h, injectCss, itemCell } from "./hudWindow.ts";

type Page = "Overview" | "Members" | "Ranks" | "Buffs" | "Storage" | "Diplomacy" | "Log";
const PAGES: [Page, string][] = [
  ["Overview", "M3 11l9-7 9 7v9a1 1 0 01-1 1h-5v-6H9v6H4a1 1 0 01-1-1z"],
  ["Members", "M9 11a3 3 0 100-6 3 3 0 000 6zM17 11a2.5 2.5 0 100-5 2.5 2.5 0 000 5zM3 20c0-3.5 2.7-6 6-6s6 2.5 6 6M15 14.5c3 0 6 2 6 5"],
  ["Ranks", "M12 2l3 6 6 .9-4.5 4.3 1 6.3L12 16.5 6.5 19.5l1-6.3L3 8.9 9 8z"],
  ["Buffs", "M12 3v18M5 8l7-5 7 5M5 16l7 5 7-5"],
  ["Storage", "M4 8l8-4 8 4v10l-8 4-8-4zM4 8l8 4 8-4M12 12v10"],
  ["Diplomacy", "M3 3l12 12M21 3L9 15M13 17l4 4M17 17l-4 4M7 17l4 4M11 17l-4 4"],
  ["Log", "M6 3h10l2 2v16H8l-2-2zM9 8h6M9 12h6M9 16h4"],
];

const GUILD = { name: "GameMaster", level: 12, exp: 4_180_000, expNeed: 6_200_000, cap: 60, fund: 12_450_000, points: 3, notice:
  "Welcome to GameMaster!\n\n• Guild war with Nightfall on Saturday 20:00 server time; be online by 19:45.\n• Officers: please keep the storage tidy and log what you take.\n• Bichon Wall boss runs every evening; whisper GMRyu for a spot.\n\nBe kind, be loud, be legendary." };

const RANKS = ["Leader", "Officer", "Elite", "Member", "Recruit"];
/** GuildRankOptions (Shared/Enums.cs), in flag order. */
const PERMS: [string, string][] = [
  ["CanChangeRank", "Change ranks"], ["CanRecruit", "Recruit"], ["CanKick", "Kick members"], ["CanStoreItem", "Store items"],
  ["CanRetrieveItem", "Take items"], ["CanAlterAlliance", "Alliances & wars"], ["CanChangeNotice", "Edit notice"], ["CanActivateBuff", "Activate buffs"],
];
const RANK_PERMS: boolean[][] = [
  [true, true, true, true, true, true, true, true], [true, true, true, true, true, false, true, true],
  [false, true, false, true, true, false, false, false], [false, false, false, true, false, false, false, false], [false, false, false, false, false, false, false, false],
];

const CLASSES = ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"];
const MAPS = ["Bichon Province", "Bichon Wall", "Woomyon Woods", "Serpent Valley", "Bone Cave", "Mongchon", "Gi-Ryoong", "Stone Temple"];
interface Member { name: string; cls: string; level: number; rank: number; online: boolean; where: string; seen: string; joined: string; contrib: number }
const NAMES = ["GMRyu", "Kaelith", "Brannoc", "Seren", "Mordrake", "Lysha", "Tobin", "Vexa", "Grimwald", "Nyra", "Orrin", "Ashka", "Doran", "Rowena", "Fenrik", "Bastian",
  "Jarek", "Kira", "Lucan", "Mirelle", "Nox", "Oona", "Pavel", "Quill", "Rhea", "Soren", "Talia", "Ulric", "Vale", "Wren", "Xander", "Yara", "Zed", "Corin", "Mira", "Halden", "Briar", "Esk"];
const MEMBERS: Member[] = NAMES.map((name, i) => {
  const rank = i === 0 ? 0 : i < 4 ? 1 : i < 10 ? 2 : i < 32 ? 3 : 4;
  const online = i === 0 || (i * 7) % 3 === 0;
  return { name, cls: i === 0 ? "Warrior" : CLASSES[(i * 3) % 5]!, level: i === 0 ? 42 : 22 + ((i * 13 + i * i * 7) % 31), rank, online, where: MAPS[(i * 5) % MAPS.length]!,
    seen: online ? "Online" : ["2 h ago", "yesterday", "3 days ago", "1 week ago", "5 h ago"][(i * 3) % 5]!, joined: `${1 + (i % 28)}/${1 + (i % 9)}/2026`, contrib: 120_000 - i * 2900 + ((i * 7919) % 9000) };
});

/** GuildSettings.ini [Buff-N]: Name, Icon (GuildSkill), TimeLimit (min), PointsReq. Effects are mock (this server's buffs carry no stats). */
const BUFFS: [string, number, string][] = [
  ["Reputation", 24, "+10% guild EXP from members"], ["Specialized Crafter", 27, "+15% crafting success"], ["Specialized Miner", 30, "+20% mining rate"],
  ["Gem Success Rate", 33, "+10% gem success"], ["Specialized Fisher", 36, "+20% fishing rate"], ["Skill Experiance", 39, "+15% skill training"],
  ["Field Experience", 42, "+10% EXP from monsters"], ["Hero Experience", 45, "+15% hero EXP"], ["Max Health", 6, "+150 max HP"], ["Max Mana", 9, "+120 max MP"],
  ["Physical Defence", 12, "+3 AC"], ["Health Recovery", 0, "+2 HP regeneration"], ["Mana Recovery", 3, "+2 MP regeneration"], ["Guild House Rate", 21, "-10% guild house costs"],
  ["Physical Damage", 18, "+3 DC"], ["Magic Defence", 15, "+3 MAC"],
];
type BuffState = { obtained: boolean; ends: number };

const STORAGE: ([string, number, number] | null)[] = [
  ["SunPotion(M)", 312, 40], ["(HP)DrugXL", 813, 60], ["(MP)DrugXL", 814, 60], ["TownTeleport", 402, 25], ["DungeonEscape", 408, 10], ["Amulet", 270, 900], ["GreenPoison", 259, 120], ["RedPoison", 258, 120],
  ["DragonSlayer", 57, 1], ["DragonStaff", 58, 1], ["HolyBloodSpear", 77, 1], ["BlackTigerArmour(M)", 1030, 1], ["TitanArmour(M)", 592, 1], ["CrystalArmour(M)", 597, 1], ["WhiteGoldRing", 773, 2], ["WhiteGoldBracelet", 766, 2],
  ["WhiteGoldNecklace", 781, 1], ["RecallNecklace", 247, 1], ["RedOrchidRing", 433, 1], ["SmashWheel", 541, 1], ["PowerStone(M)", 576, 3], ["HealthStone(M)", 586, 3], ["MagicStone(L)", 582, 2], ["BenedictionOil", 26, 6],
  ["WhiteTiger", 3757, 1], ["DragonTorch", 132, 2], null, null, null, null, null, null,
  ...Array<null>(24).fill(null),
];

const ALLIES: [string, number, number, string][] = [["Lionheart", 10, 44, "since 3/9/2026"], ["Silver Dawn", 8, 31, "since 21/8/2026"]];
const WARS: [string, number, number, number, number][] = [["Nightfall", 11, 52, 38, 27], ["Red Lotus", 7, 25, 12, 15]]; // name, level, members, our kills, their kills

type LogKind = "Members" | "Storage" | "Buffs" | "Wars";
const LOG: [LogKind, string, string][] = [
  ["Wars", "Nightfall declared war on the guild", "4 min ago"], ["Members", "Wren reached level 29", "12 min ago"], ["Storage", "Kaelith stored DragonStaff", "25 min ago"],
  ["Buffs", "GMRyu activated Field Experience", "41 min ago"], ["Members", "Esk joined the guild (recruited by Seren)", "1 h ago"], ["Storage", "Brannoc took SunPotion(M) x10", "1 h ago"],
  ["Members", "Tobin was promoted to Elite", "2 h ago"], ["Buffs", "Max Health obtained (1 point)", "3 h ago"], ["Wars", "War with Red Lotus: 12 - 15", "5 h ago"],
  ["Storage", "Seren deposited 500,000 gold", "6 h ago"], ["Members", "Halden left the guild", "yesterday"], ["Members", "Guild reached level 12", "2 days ago"],
];
const LOG_ICON: Record<LogKind, string> = { Members: PAGES[1]![1], Storage: PAGES[4]![1], Buffs: PAGES[3]![1], Wars: PAGES[5]![1] };

const fmt = (n: number) => n.toLocaleString("en-GB");
const svgIcon = (d: string, cls = "gw-ic") => {
  const s = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  s.setAttribute("viewBox", "0 0 24 24");
  s.setAttribute("class", cls);
  const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
  p.setAttribute("d", d);
  s.append(p);
  return s;
};
const mmss = (ms: number) => {
  const s = Math.max(0, Math.ceil(ms / 1000)), p = (n: number) => String(n).padStart(2, "0");
  return s >= 3600 ? `${Math.floor(s / 3600)}:${p(Math.floor(s / 60) % 60)}:${p(s % 60)}` : `${Math.floor(s / 60)}:${p(s % 60)}`;
};

const CSS = `
.hudm .gwin { --winh: 560px; width: 920px; }
.hudm .gwin .panel { height: var(--winh); display: flex; flex-direction: column; }
.hudm .gw-ic { width: 14px; height: 14px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
/* Banner */
.hudm .gwin .banner { position: relative; display: flex; align-items: center; gap: 16px; padding: 12px 18px; overflow: hidden;
  background: radial-gradient(ellipse 50% 140% at 0% 50%, color-mix(in srgb, var(--ac) 18%, transparent), transparent 70%),
    linear-gradient(180deg, color-mix(in srgb, var(--pa) 60%, transparent), transparent);
  border-bottom: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .gwin .emblem { width: 64px; height: 72px; flex: none; filter: drop-shadow(0 0 calc(8px * var(--gl)) var(--glow)); }
.hudm .gwin .gname h2 { margin: 0; font-size: 22px; letter-spacing: .04em; color: #fff; }
.hudm .gwin .gname .sub { margin-top: 2px; font-size: 11px; color: var(--dim); }
.hudm .gwin .gname .sub b { color: var(--ac); }
.hudm .gwin .gexp { width: 260px; margin-top: 7px; }
.hudm .gwin .gexp .t { display: flex; justify-content: space-between; font-size: 10px; color: var(--dim); margin-bottom: 3px; }
.hudm .gwin .bar { height: 6px; border-radius: 3px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .gwin .bar i { display: block; height: 100%; background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 45%, #000), var(--ac)); box-shadow: 0 0 6px var(--glow); }
.hudm .gwin .bstats { margin-left: auto; display: flex; gap: 10px; }
.hudm .gwin .bstat { min-width: 92px; padding: 7px 12px; border-radius: var(--rad); text-align: center; background: color-mix(in srgb, var(--pb) 55%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 70%, transparent); }
.hudm .gwin .bstat b { display: block; font-size: 15px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gwin .bstat span { font-size: 9px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--dim); }
.hudm .gwin .bstat.hot b { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
/* Side menu + page */
.hudm .gwin .gmain { flex: 1; min-height: 0; display: flex; }
.hudm .gwin .nav { width: 150px; flex: none; padding: 10px 8px; display: flex; flex-direction: column; gap: 3px; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .gwin .nav div { display: flex; align-items: center; gap: 9px; height: 32px; padding: 0 10px; cursor: pointer; user-select: none; border-radius: var(--rad);
  font-size: 12px; font-weight: 600; color: var(--dim); border: 1px solid transparent; }
.hudm .gwin .nav div:hover { color: var(--tx); background: color-mix(in srgb, var(--ac) 8%, transparent); }
.hudm .gwin .nav div.on { color: #fff; border-color: color-mix(in srgb, var(--ac) 60%, transparent); background: color-mix(in srgb, var(--ac) 16%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .gwin .nav div.on .gw-ic { color: var(--ac); filter: drop-shadow(0 0 4px var(--glow)); }
.hudm .gwin .nav i { margin-left: auto; font-style: normal; font-size: 10px; padding: 0 6px; border-radius: 7px; background: color-mix(in srgb, var(--fr) 50%, transparent); color: var(--tx); }
.hudm .gwin .nav .sp { flex: 1; }
.hudm .gwin .nav .you { height: auto; padding: 8px 10px; cursor: default; flex-direction: column; align-items: flex-start; gap: 2px; font-size: 10px; border-top: 1px solid color-mix(in srgb, var(--fr) 50%, transparent); border-radius: 0; }
.hudm .gwin .nav .you:hover { background: none; color: var(--dim); }
.hudm .gwin .nav .you b { font-size: 12px; color: var(--ac); }
.hudm .gwin .page { flex: 1; min-width: 0; min-height: 0; padding: 14px 16px; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gwin .card { border-radius: var(--rad); padding: 12px 14px; background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .gwin .ch { display: flex; align-items: center; gap: 8px; margin-bottom: 9px; font-size: 10px; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; color: var(--acd); }
.hudm .gwin .ch .btn { margin-left: auto; height: 20px; font-size: 10px; letter-spacing: .04em; text-transform: none; }
.hudm .gwin .grid2 { display: grid; grid-template-columns: 1.35fr 1fr; gap: 12px; }
.hudm .gwin .cards4 { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 12px; }
.hudm .gwin .kpi b { display: block; font-size: 20px; color: #fff; font-variant-numeric: tabular-nums; }
.hudm .gwin .kpi span { font-size: 10px; color: var(--dim); }
.hudm .gwin .kpi small { display: block; margin-top: 5px; font-size: 10px; color: var(--acd); }
.hudm .gwin .notice { white-space: pre-wrap; font-size: 12px; line-height: 1.6; color: color-mix(in srgb, var(--tx) 90%, var(--dim)); }
.hudm .gwin textarea, .hudm .gwin input.in { width: 100%; box-sizing: border-box; font: 12px/1.6 "Segoe UI", system-ui, sans-serif; color: var(--tx); background: color-mix(in srgb, var(--pb) 60%, #000);
  border: 1px solid var(--fr); border-radius: calc(var(--rad) * .8); padding: 6px 8px; outline: none; resize: none; }
.hudm .gwin textarea { height: 110px; } /* was inherited from lab.html's page-wide textarea rule, now scoped to its panel */
.hudm .gwin textarea:focus, .hudm .gwin input.in:focus { border-color: var(--ac); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .gwin .who { display: flex; align-items: center; gap: 8px; padding: 4px 2px; font-size: 12px; }
.hudm .gwin .dot { width: 7px; height: 7px; border-radius: 50%; flex: none; background: #59606e; }
.hudm .gwin .dot.on { background: #4fe07a; box-shadow: 0 0 6px rgba(80, 230, 120, .7); }
.hudm .gwin .who .m { margin-left: auto; font-size: 10px; color: var(--dim); }
.hudm .gwin .tag { font-size: 9px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; padding: 1px 5px; border-radius: 6px; color: var(--acd); border: 1px solid color-mix(in srgb, var(--ac) 35%, transparent); white-space: nowrap; }
.hudm .gwin .tag.lead { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 30%, transparent); }
.hudm .gwin .feed div { display: flex; align-items: center; gap: 9px; padding: 6px 0; font-size: 12px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); }
.hudm .gwin .feed div:last-child { border-bottom: 0; }
.hudm .gwin .feed .gw-ic { color: var(--acd); }
.hudm .gwin .feed span { margin-left: auto; font-size: 10px; color: var(--dim); white-space: nowrap; }
/* Members */
.hudm .gwin .tools { display: flex; gap: 8px; align-items: center; margin-bottom: 10px; }
.hudm .gwin .tools .in { width: 220px; height: 26px; padding: 0 8px; }
.hudm .gwin .chip { height: 22px; padding: 0 10px; display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 600; cursor: pointer; user-select: none;
  color: var(--dim); border: 1px solid color-mix(in srgb, var(--fr) 80%, transparent); border-radius: 11px; background: color-mix(in srgb, var(--pb) 50%, transparent); }
.hudm .gwin .chip:hover { color: var(--tx); }
.hudm .gwin .chip.on { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 22%, transparent); box-shadow: 0 0 calc(6px * var(--gl)) var(--glow); }
.hudm .gwin .mgrid { display: grid; grid-template-columns: 1fr 230px; gap: 12px; min-height: 0; }
.hudm .gwin table { width: 100%; border-collapse: collapse; font-size: 12px; }
.hudm .gwin th { position: sticky; top: 0; text-align: left; padding: 6px 8px; font-size: 10px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim);
  background: color-mix(in srgb, var(--pb) 90%, #000); cursor: pointer; user-select: none; white-space: nowrap; }
.hudm .gwin th:hover { color: var(--tx); } .hudm .gwin th.on { color: var(--ac); }
.hudm .gwin td { padding: 6px 8px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); white-space: nowrap; }
.hudm .gwin tbody tr { cursor: pointer; }
.hudm .gwin tbody tr:hover td { background: color-mix(in srgb, var(--ac) 7%, transparent); }
.hudm .gwin tbody tr.sel td { background: color-mix(in srgb, var(--ac) 16%, transparent); }
.hudm .gwin td.dim { color: var(--dim); font-size: 11px; }
.hudm .gwin .nmc { display: inline-flex; align-items: center; gap: 7px; }
.hudm .gwin .tblwrap { max-height: 362px; overflow-y: auto; border-radius: var(--rad); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .gwin .mcard h3 { margin: 0 0 2px; font-size: 16px; color: #fff; display: flex; align-items: center; gap: 7px; }
.hudm .gwin .mcard .ln { display: flex; justify-content: space-between; font-size: 11px; padding: 4px 0; border-bottom: 1px solid color-mix(in srgb, var(--fr) 25%, transparent); }
.hudm .gwin .mcard .ln span { color: var(--dim); } .hudm .gwin .mcard .ln b { color: var(--tx); font-weight: 600; }
.hudm .gwin .acts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px; margin-top: 10px; }
.hudm .gwin .acts .btn { padding: 0 6px; white-space: nowrap; }
.hudm .gwin .btn.warn { border-color: #b33; color: #fbb; }
.hudm .gwin .btn.dis { opacity: .4; pointer-events: none; }
.hudm .gwin .btn.main { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 35%, var(--pb)); box-shadow: 0 0 0 1px #000, 0 0 calc(6px * var(--gl)) var(--glow); }
/* Ranks */
.hudm .gwin .perm th, .hudm .gwin .perm td { text-align: center; }
.hudm .gwin .perm td:first-child, .hudm .gwin .perm th:first-child { text-align: left; }
.hudm .gwin .tog { display: inline-block; width: 30px; height: 16px; border-radius: 8px; position: relative; cursor: pointer; background: color-mix(in srgb, var(--fr) 60%, #000); border: 1px solid var(--fr); vertical-align: middle; }
.hudm .gwin .tog::after { content: ""; position: absolute; left: 2px; top: 2px; width: 10px; height: 10px; border-radius: 50%; background: var(--dim); transition: left .15s, background .15s; }
.hudm .gwin .tog.on { background: color-mix(in srgb, var(--ac) 45%, #000); border-color: var(--ac); }
.hudm .gwin .tog.on::after { left: 16px; background: #fff; box-shadow: 0 0 6px var(--ac); }
.hudm .gwin .tog.lock { cursor: not-allowed; }
/* Buffs */
.hudm .gwin .bgrid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 8px; }
.hudm .gwin .gbuff { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .gwin .gbuff.act { border-color: var(--ac); box-shadow: inset 0 0 14px color-mix(in srgb, var(--ac) 18%, transparent); }
.hudm .gwin .gbuff i { width: 36px; height: 36px; flex: none; border-radius: calc(var(--rad) * .8); background: center / auto no-repeat #000; image-rendering: pixelated; border: 1px solid var(--fr); }
.hudm .gwin .gbuff .mid { flex: 1; min-width: 0; }
.hudm .gwin .gbuff .mid b { display: block; font-size: 12px; color: var(--tx); }
.hudm .gwin .gbuff .mid span { font-size: 11px; color: var(--dim); }
.hudm .gwin .gbuff .st { font-size: 10px; text-align: right; color: var(--dim); min-width: 74px; }
.hudm .gwin .gbuff .st b { display: block; font-size: 12px; color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .gwin .gbuff .btn { height: 24px; padding: 0 10px; }
/* Storage */
.hudm .gwin .sgrid { display: grid; grid-template-columns: repeat(14, 44px); gap: 4px; }
.hudm .gwin .money { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
.hudm .gwin .money > * { flex: none; }
.hudm .gwin .money .in { width: 160px; height: 26px; padding: 0 8px; }
.hudm .gwin .money .btn { height: 26px; }
.hudm .gwin .money .fund { margin-left: auto; font-size: 12px; color: var(--dim); }
.hudm .gwin .money .fund b { color: #fff; font-size: 14px; font-variant-numeric: tabular-nums; }
/* Diplomacy */
.hudm .gwin .drow { display: flex; align-items: center; gap: 10px; padding: 9px 4px; border-bottom: 1px solid color-mix(in srgb, var(--fr) 30%, transparent); font-size: 12px; }
.hudm .gwin .drow:last-child { border-bottom: 0; }
.hudm .gwin .drow .nm { font-weight: 700; color: #fff; min-width: 110px; }
.hudm .gwin .drow .dim { color: var(--dim); font-size: 11px; }
.hudm .gwin .score { margin-left: auto; display: flex; align-items: center; gap: 8px; font-variant-numeric: tabular-nums; }
.hudm .gwin .score b { font-size: 15px; color: #fff; } .hudm .gwin .score b.win { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .gwin .timer { font-size: 11px; color: var(--acd); font-variant-numeric: tabular-nums; min-width: 70px; text-align: right; }
.hudm .gwin .empty { padding: 24px; text-align: center; color: var(--dim); font-size: 12px; }
`;

export class GuildMock extends HudWindow {
  private page: Page = "Overview";
  private body!: HTMLElement;
  private nav!: HTMLElement;
  private members = MEMBERS.map((m) => ({ ...m }));
  private perms = RANK_PERMS.map((r) => [...r]);
  private buffs: BuffState[] = BUFFS.map((_, i) => ({ obtained: [6, 8, 10].includes(i), ends: i === 6 ? performance.now() + 42 * 60_000 + 10_000 : 0 }));
  private points = GUILD.points;
  private fund = GUILD.fund;
  private notice = GUILD.notice;
  private editing = false;
  private memberQuery = "";
  private memberFilter: "All" | "Online" | "Officers" = "All";
  private sort: { key: keyof Member | "rank"; dir: 1 | -1 } = { key: "rank", dir: 1 };
  private selMember = "GMRyu";
  private kickConfirm = "";
  private logFilter: LogKind | "All" = "All";
  private log = LOG.map((l) => [...l] as [LogKind, string, string]);
  private warEnds = [performance.now() + 2 * 3600_000 + 14 * 60_000, performance.now() + 37 * 60_000];
  private stats!: Record<string, HTMLElement>;

  constructor() {
    super("gwin", 52, 40);
    injectCss("hudm-guild-css", CSS);
    this.build();
    // Countdowns (active buffs, wars) tick once a second while the window is open.
    window.setInterval(() => { if (this.open) this.tick(); }, 1000);
  }

  protected override shown(): void { this.render(); }

  private build(): void {
    const emblem = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    emblem.setAttribute("viewBox", "0 0 64 72");
    emblem.setAttribute("class", "emblem");
    emblem.innerHTML = `<defs><linearGradient id="hudm-gem" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color: var(--pc)"/><stop offset="1" style="stop-color: var(--pb)"/></linearGradient></defs>
      <path d="M32 2l28 9v22c0 18-12 31-28 37C16 64 4 51 4 33V11z" fill="url(#hudm-gem)" style="stroke: var(--ac)" stroke-width="2.5"/>
      <path d="M32 9l21 7v17c0 13-9 23-21 28-12-5-21-15-21-28V16z" fill="none" style="stroke: color-mix(in srgb, var(--ac) 45%, transparent)" stroke-width="1"/>
      <text x="32" y="44" text-anchor="middle" font-family="Georgia, serif" font-weight="700" font-size="22" fill="#fff">GM</text>`;
    const pct = GUILD.exp / GUILD.expNeed * 100;
    const expBar = h("div", "bar", h("i"));
    (expBar.firstChild as HTMLElement).style.width = pct.toFixed(1) + "%";
    this.stats = { members: h("b"), online: h("b"), fund: h("b"), points: h("b") };
    const bstat = (label: string, el: HTMLElement, hot = false) => h("div", "bstat" + (hot ? " hot" : ""), el, h("span", "", label));
    const banner = h("div", "banner", emblem,
      h("div", "gname", h("h2", "", GUILD.name), h("div", "sub", "Level ", h("b", "", String(GUILD.level)), " guild · founded 14/2/2026 by ", h("b", "", "GMRyu")),
        h("div", "gexp", h("div", "t", h("span", "", "Guild EXP"), h("span", "", `${fmt(GUILD.exp)} / ${fmt(GUILD.expNeed)} (${pct.toFixed(1)}%)`)), expBar)),
      h("div", "bstats", bstat("Members", this.stats.members!), bstat("Online", this.stats.online!), bstat("Fund", this.stats.fund!), bstat("Buff points", this.stats.points!, true)));
    this.nav = h("div", "nav");
    this.body = h("div", "page");
    this.el.append(h("div", "panel", this.titleBar("GUILD", "Close (G)"), banner, h("div", "gmain", this.nav, this.body)));
  }

  private render(): void {
    for (const m of this.members) canon(m); // the player follows the HUD knobs; known names the roster (hudPeople.ts)
    this.stats.members!.textContent = `${this.members.length} / ${GUILD.cap}`;
    this.stats.online!.textContent = String(this.members.filter((m) => m.online).length);
    this.stats.fund!.textContent = this.fund >= 1e6 ? (this.fund / 1e6).toFixed(2) + "M" : fmt(this.fund);
    this.stats.points!.textContent = String(this.points);
    this.nav.replaceChildren();
    for (const [p, d] of PAGES) {
      const count = p === "Members" ? String(this.members.length) : p === "Buffs" ? String(this.buffs.filter((b) => b.ends > performance.now()).length) : p === "Diplomacy" ? String(WARS.length) : "";
      const it = h("div", p === this.page ? "on" : "", svgIcon(d), p);
      if (count) it.append(h("i", "", count));
      it.addEventListener("click", () => { this.page = p; this.editing = false; this.kickConfirm = ""; this.focusSearch = p === "Members"; this.render(); });
      this.nav.append(it);
    }
    this.nav.append(h("div", "sp"), h("div", "you", "You are", h("b", "", "GMRyu · Leader"), "All permissions"));
    this.body.replaceChildren();
    this.body.scrollTop = 0;
    ({ Overview: () => this.overview(), Members: () => this.membersPage(), Ranks: () => this.ranksPage(), Buffs: () => this.buffsPage(),
      Storage: () => this.storagePage(), Diplomacy: () => this.diplomacyPage(), Log: () => this.logPage() })[this.page]();
  }

  private card(title: string, ...kids: (Node | string)[]): HTMLElement {
    return h("div", "card", h("div", "ch", title), ...kids);
  }

  private overview(): void {
    const online = this.members.filter((m) => m.online);
    const active = this.buffs.filter((b) => b.ends > performance.now()).length;
    const kpi = (v: string, label: string, note: string) => h("div", "card kpi", h("b", "", v), h("span", "", label), h("small", "", note));
    this.body.append(h("div", "cards4",
      kpi(`${online.length}`, `members online of ${this.members.length}`, `${Math.round(online.length / this.members.length * 100)}% active now`),
      kpi(`${GUILD.level}`, "guild level", `${fmt(GUILD.expNeed - GUILD.exp)} EXP to level ${GUILD.level + 1}`),
      kpi(this.fund >= 1e6 ? (this.fund / 1e6).toFixed(2) + "M" : fmt(this.fund), "gold in the fund", "+1.2M this week"),
      kpi(`${active}`, "buffs active", `${this.points} buff point${this.points === 1 ? "" : "s"} to spend`)));
    // Notice board (edit toggles a text area, Save keeps it)
    const notice = h("div", "card");
    const head = h("div", "ch", "Notice board");
    const btn = h("div", "btn", this.editing ? "Save" : "Edit");
    head.append(btn);
    let area: HTMLTextAreaElement | null = null;
    if (this.editing) {
      area = h("textarea");
      area.rows = 11;
      area.value = this.notice;
      area.addEventListener("keydown", (e) => e.stopPropagation());
    }
    btn.addEventListener("click", () => {
      if (this.editing && area) { this.notice = area.value; this.log.unshift(["Members", "GMRyu updated the notice", "just now"]); }
      this.editing = !this.editing;
      this.render();
    });
    notice.append(head, area ?? h("div", "notice", this.notice));
    const who = this.card(`Online now · ${online.length}`);
    for (const m of online.slice(0, 9)) who.append(h("div", "who", h("span", "dot on"), m.name, h("span", "tag" + (m.rank === 0 ? " lead" : ""), RANKS[m.rank]!), h("span", "m", `${m.cls} ${m.level} · ${m.where}`)));
    if (online.length > 9) who.append(h("div", "who", h("span", "m", `and ${online.length - 9} more`)));
    const feed = this.card("Recent activity");
    feed.append(this.feed(this.log.slice(0, 5)));
    this.body.append(h("div", "grid2", h("div", "", notice, h("div", "", "")), h("div", "", who)));
    feed.style.marginTop = "12px";
    this.body.append(feed);
  }

  private feed(rows: [LogKind, string, string][]): HTMLElement {
    const f = h("div", "feed");
    for (const [k, text, when] of rows) f.append(h("div", "", svgIcon(LOG_ICON[k]), text, h("span", "", when)));
    if (!rows.length) f.append(h("div", "", "Nothing yet."));
    return f;
  }

  private membersPage(): void {
    const q = h("input", "in");
    q.placeholder = "Search members, classes, maps…";
    q.value = this.memberQuery;
    q.addEventListener("keydown", (e) => e.stopPropagation());
    q.addEventListener("input", () => { this.memberQuery = q.value; this.renderTable(tbody); });
    const tools = h("div", "tools", q);
    for (const f of ["All", "Online", "Officers"] as const) {
      const c = h("div", "chip" + (this.memberFilter === f ? " on" : ""), f);
      c.addEventListener("click", () => { this.memberFilter = f; this.render(); });
      tools.append(c);
    }
    const cols: [keyof Member | "rank", string][] = [["name", "Name"], ["cls", "Class"], ["level", "Level"], ["rank", "Rank"], ["where", "Location"], ["contrib", "Contribution"]];
    const tr = h("tr");
    for (const [k, label] of cols) {
      const th = h("th", this.sort.key === k ? "on" : "", label + (this.sort.key === k ? (this.sort.dir === 1 ? " ▴" : " ▾") : ""));
      th.addEventListener("click", () => { this.sort = { key: k, dir: this.sort.key === k ? (-this.sort.dir as 1 | -1) : 1 }; this.render(); });
      tr.append(th);
    }
    const tbody = h("tbody");
    const table = h("table", "", h("thead", "", tr), tbody);
    this.renderTable(tbody);
    this.body.append(tools, h("div", "mgrid", h("div", "tblwrap", table), this.memberCard()));
    // Only when the page is opened: a focused search box would swallow Esc / G on every later redraw (row clicks, sorting).
    if (this.focusSearch) { this.focusSearch = false; setTimeout(() => q.focus({ preventScroll: true }), 0); }
  }
  private focusSearch = false;

  private renderTable(tbody: HTMLElement): void {
    const q = this.memberQuery.trim().toLowerCase();
    const rows = this.members.filter((m) => (this.memberFilter === "All" || (this.memberFilter === "Online" ? m.online : m.rank <= 1))
      && (!q || [m.name, m.cls, m.where, RANKS[m.rank]!].some((s) => s.toLowerCase().includes(q))));
    const k = this.sort.key, d = this.sort.dir;
    rows.sort((a, b) => {
      const va = a[k], vb = b[k];
      const c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
      return (c || a.name.localeCompare(b.name)) * d;
    });
    tbody.replaceChildren();
    for (const m of rows) {
      const tr = h("tr", m.name === this.selMember ? "sel" : "",
        h("td", "", h("span", "nmc", h("span", "dot" + (m.online ? " on" : "")), m.name)), h("td", "dim", m.cls), h("td", "", String(m.level)),
        h("td", "", h("span", "tag" + (m.rank === 0 ? " lead" : ""), RANKS[m.rank]!)), h("td", "dim", m.online ? m.where : m.seen), h("td", "dim", fmt(m.contrib)));
      tr.addEventListener("click", () => { this.selMember = m.name; this.kickConfirm = ""; this.render(); });
      tbody.append(tr);
    }
    if (!rows.length) tbody.append(h("tr", "", Object.assign(h("td", "dim", "No members match."), { colSpan: 6 })));
  }

  private memberCard(): HTMLElement {
    const m = this.members.find((x) => x.name === this.selMember) ?? this.members[0]!;
    const ln = (k: string, v: string) => h("div", "ln", h("span", "", k), h("b", "", v));
    const me = m.name === "GMRyu";
    const act = (label: string, fn: () => void, cls = "") => { const b = h("div", "btn " + cls + (me ? " dis" : ""), label); b.addEventListener("click", fn); return b; };
    const promote = act("Promote", () => { if (m.rank > 1) { m.rank--; this.log.unshift(["Members", `${m.name} was promoted to ${RANKS[m.rank]}`, "just now"]); this.render(); } });
    const demote = act("Demote", () => { if (m.rank < RANKS.length - 1) { m.rank++; this.log.unshift(["Members", `${m.name} was demoted to ${RANKS[m.rank]}`, "just now"]); this.render(); } });
    const kick = act(this.kickConfirm === m.name ? "Click again to kick" : "Kick", () => {
      if (this.kickConfirm !== m.name) { this.kickConfirm = m.name; this.render(); return; }
      this.members = this.members.filter((x) => x !== m);
      this.log.unshift(["Members", `${m.name} was removed from the guild`, "just now"]);
      this.selMember = "GMRyu"; this.kickConfirm = "";
      this.render();
    }, this.kickConfirm === m.name ? "warn" : "");
    return h("div", "card mcard", h("h3", "", h("span", "dot" + (m.online ? " on" : "")), m.name),
      h("div", "", h("span", "tag" + (m.rank === 0 ? " lead" : ""), RANKS[m.rank]!)),
      h("div", "", ln("Class", m.cls), ln("Level", String(m.level)), ln(m.online ? "Location" : "Last seen", m.online ? m.where : m.seen), ln("Joined", m.joined), ln("Contribution", fmt(m.contrib))),
      h("div", "acts", act("Whisper", () => {}), act("Group invite", () => {}), promote, demote, kick));
  }

  private ranksPage(): void {
    const tr = h("tr", "", h("th", "", "Permission"));
    for (const [i, r] of RANKS.entries()) tr.append(h("th", "", `${r} · ${this.members.filter((m) => m.rank === i).length}`));
    const tbody = h("tbody");
    PERMS.forEach(([, label], p) => {
      const row = h("tr", "", h("td", "", label));
      RANKS.forEach((_, r) => {
        const on = this.perms[r]![p]!;
        const t = h("span", "tog" + (on ? " on" : "") + (r === 0 ? " lock" : ""));
        t.title = r === 0 ? "The leader always has every permission" : `${on ? "Revoke" : "Grant"} for ${RANKS[r]}`;
        if (r) t.addEventListener("click", () => { this.perms[r]![p] = !on; this.render(); });
        row.append(h("td", "", t));
      });
      tbody.append(row);
    });
    this.body.append(this.card("Rank permissions", h("table", "perm", h("thead", "", tr), tbody)),
      Object.assign(h("div", "card"), { style: "margin-top: 12px" }));
    const hint = this.body.lastElementChild as HTMLElement;
    hint.append(h("div", "ch", "About ranks"), h("div", "notice", "Ranks are checked by the server's GuildRankOptions flags. The leader always keeps every permission; members can only be promoted to ranks below the person promoting them. New recruits join as Recruit."));
  }

  private buffsPage(): void {
    const head = h("div", "tools", h("span", "", `Guild level ${GUILD.level} · `, h("b", "", `${this.points} point${this.points === 1 ? "" : "s"}`), " to spend · buffs last 60 minutes once activated"));
    const grid = h("div", "bgrid");
    const now = performance.now();
    BUFFS.forEach(([name, icon, effect], i) => {
      const s = this.buffs[i]!;
      const active = s.ends > now;
      const pic = h("i");
      const idx = !s.obtained ? icon + 2 : active ? icon + 1 : icon; // GuildDialog's GuildSkill rule
      void libPicture(Lib.GuildSkill, idx).then((p) => { if (p) pic.style.backgroundImage = `url(${p.url})`; });
      const st = h("div", "st");
      st.dataset.buff = String(i);
      const btn = !s.obtained ? h("div", "btn" + (this.points < 1 ? " dis" : ""), "Obtain · 1 pt") : active ? h("div", "btn dis", "Active") : h("div", "btn main", "Activate");
      btn.addEventListener("click", () => {
        if (!s.obtained) { if (this.points < 1) return; this.points--; s.obtained = true; this.log.unshift(["Buffs", `${name} obtained (1 point)`, "just now"]); }
        else if (!active) { s.ends = performance.now() + 60 * 60_000; this.log.unshift(["Buffs", `GMRyu activated ${name}`, "just now"]); }
        this.render();
      });
      grid.append(h("div", "gbuff" + (active ? " act" : ""), pic, h("div", "mid", h("b", "", name), h("span", "", effect)), st, btn));
    });
    this.body.append(head, grid);
    this.tick();
  }

  private storagePage(): void {
    const g = h("div", "sgrid");
    for (const it of STORAGE) g.append(it ? itemCell(this.el, it[2] > 1 ? `${it[0]} (${it[2]})` : it[0], it[1], it[2]) : itemCell(this.el, "", -1));
    const amount = h("input", "in");
    amount.placeholder = "Amount of gold";
    amount.addEventListener("keydown", (e) => e.stopPropagation());
    const move = (sign: 1 | -1) => {
      const n = Math.floor(Number(amount.value.replace(/[^0-9]/g, "")) || 0);
      if (!n || (sign < 0 && n > this.fund)) return;
      this.fund += sign * n;
      this.log.unshift(["Storage", `GMRyu ${sign > 0 ? "deposited" : "withdrew"} ${fmt(n)} gold`, "just now"]);
      this.render();
    };
    const dep = h("div", "btn main", "Deposit"); dep.addEventListener("click", () => move(1));
    const wd = h("div", "btn", "Withdraw"); wd.addEventListener("click", () => move(-1));
    this.body.append(this.card(`Guild storage · ${STORAGE.filter(Boolean).length} / ${STORAGE.length}`, g,
      h("div", "money", amount, dep, wd, h("div", "fund", "Fund ", h("b", "", fmt(this.fund)), " gold"))));
  }

  private diplomacyPage(): void {
    const allies = this.card(`Alliances · ${ALLIES.length}`);
    for (const [n, lv, mem, since] of ALLIES) allies.append(h("div", "drow", h("span", "dot on"), h("span", "nm", n), h("span", "dim", `Level ${lv} · ${mem} members`), h("span", "score", h("span", "dim", since))));
    const wars = this.card(`Wars · ${WARS.length}`);
    WARS.forEach(([n, lv, mem, ours, theirs], i) => {
      const t = h("span", "timer");
      t.dataset.war = String(i);
      wars.append(h("div", "drow", svgIcon(PAGES[5]![1]), h("span", "nm", n), h("span", "dim", `Level ${lv} · ${mem} members`),
        h("span", "score", h("b", ours >= theirs ? "win" : "", String(ours)), h("span", "dim", "kills"), h("b", theirs > ours ? "win" : "", String(theirs))), t));
    });
    const actions = h("div", "acts");
    actions.style.gridTemplateColumns = "repeat(3, max-content)";
    actions.append(h("div", "btn main", "Declare war"), h("div", "btn", "Propose alliance"), h("div", "btn", "Guild rankings"));
    wars.style.marginTop = "12px";
    this.body.append(allies, wars, actions);
    this.tick();
  }

  private logPage(): void {
    const tools = h("div", "tools");
    for (const f of ["All", "Members", "Storage", "Buffs", "Wars"] as const) {
      const c = h("div", "chip" + (this.logFilter === f ? " on" : ""), f);
      c.addEventListener("click", () => { this.logFilter = f; this.render(); });
      tools.append(c);
    }
    this.body.append(tools, this.card("Activity", this.feed(this.log.filter((l) => this.logFilter === "All" || l[0] === this.logFilter))));
  }

  private tick(): void {
    const now = performance.now();
    for (const el of this.body.querySelectorAll<HTMLElement>("[data-buff]")) {
      const s = this.buffs[Number(el.dataset.buff)]!;
      el.replaceChildren(...(s.ends > now ? [h("b", "", mmss(s.ends - now)), "remaining"] : s.obtained ? [h("b", "", "Ready"), "obtained"] : [h("b", "", "—"), "not obtained"]));
    }
    for (const el of this.body.querySelectorAll<HTMLElement>("[data-war]")) el.textContent = "ends in " + mmss(this.warEnds[Number(el.dataset.war)]! - now);
  }
}
