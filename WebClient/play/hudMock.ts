// UI tester for the scene lab (play/lab.ts): a DOM mock of a proposed bottom HUD drawn over the lab's view, so a layout
// and colour theme can be judged on a real scene before any of it is built into src/ui. Mock data only: no session,
// inventory or spells. Layout (the user's sketch, 2026-10-15): two wings around a centre diamond. Each wing has three
// stat plates, the HP (left) / MP (right) bar, a belt row, and a skill row whose outer slot is the attack mode (left) or
// the pet mode (right). The diamond shows the level and the EXP % with the EXP bar running round its edge; a menu strip
// sits above it and a buff row above the left wing. This is meant to become the only HUD at every resolution, so it
// scales with the screen height (Auto scale). Theme: Lost Ark style (dark plates, bevelled metal frames, glow), one dark blue palette with an electric blue accent, all from
// a few theme variables the lab can tune (HP / MP fills keep their own colours).
// Belt: 6 slots (keys 1-6), or 10 where keys 1-8 are the player's and 9 / 0 the hero's potions (hudItems.ts heroBelt).
// A small simulation (hits, potions, casts, loot, EXP, level up) drives the states; it never touches a game.

import { AttackMode, PetMode, MirClass, BuffType } from "../src/protocol/enums.generated.ts";
import { Lib, libPicture } from "../src/ui/mir/controls.ts";
import { buffIcon } from "../src/game/buffs.ts";
import { CharacterMock } from "./hudCharacter.ts";
import { SkillsMock, type SkillEntry } from "./hudSkills.ts";
import { InventoryMock } from "./hudInventory.ts";
import { QuestsMock } from "./hudQuests.ts";
import { GuildMock } from "./hudGuild.ts";
import { FriendsMock } from "./hudFriends.ts";
import { OptionsMock } from "./hudOptions.ts";
import { BigMapMock, loadMapData } from "./hudBigMap.ts";
import { GameMenu } from "./hudGameMenu.ts";
import { NpcBoxMock } from "./hudNpcBox.ts";
import { TerritoryMock } from "./hudTerritory.ts";
import { setMe } from "./hudPeople.ts";
import { HudWindow, restartAnim, setText } from "./hudWindow.ts";
import { MailMock } from "./hudMail.ts";
import { TradeMock } from "./hudTrade.ts";
import { MinimapMock, type WorldHost } from "./hudMinimap.ts";
import { GroupMock } from "./hudGroup.ts";
import { StorageMock } from "./hudStorage.ts";
import { MerchantMock } from "./hudMerchant.ts";
import { GameShopMock } from "./hudGameShop.ts";
import { RankingMock } from "./hudRanking.ts";
import { CreatureMock } from "./hudCreature.ts";
import { MountMock } from "./hudMount.ts";
import { FishingMock } from "./hudFishing.ts";
import { HeroMock } from "./hudHero.ts";
import { KeybindsMock } from "./hudKeybinds.ts";
import { HelpMock } from "./hudHelp.ts";
import { MentorMock } from "./hudMentor.ts";
import { RelationshipMock } from "./hudRelationship.ts";
import { ChatMock } from "./hudChat.ts";
import { ItemTooltip } from "./hudTooltip.ts";
import { StatPicker, loadPlates, savePlates, plateStat, type PlateStat } from "./hudStatPick.ts";
import { NpcMock } from "./hudNpc.ts";
import { InspectMock } from "./hudInspect.ts";
import { TrustMock } from "./hudTrust.ts";
import { CraftMock } from "./hudCraft.ts";
import { RefineMock } from "./hudRefine.ts";
import { AwakenMock } from "./hudAwaken.ts";
import { SocketMock } from "./hudSocket.ts";
import { RentMock } from "./hudRent.ts";
import { LoginMock } from "./hudLogin.ts";
import { SelectMock } from "./hudSelect.ts";
import { LoadingMock } from "./hudLoading.ts";
import { HudExtras } from "./hudExtras.ts";
import { Popups } from "./hudPopup.ts";
import { BAG_SLOTS, HEAL, PLAYER_BELT, WEIGHT_MAX, addToBag, bag, bagChanged, bagWeight, belt, heroBelt, info, onBag, resetItems, type Stack } from "./hudItems.ts";
import { bestCell, bindCell, cellFor, host, installDrag, registerHost, toast, transfer, type DragHost } from "./hudDrag.ts";

export interface HudMockOptions {
  scale: number; autoScale: boolean;
  opacity: number;
  /** Theme: panel hue (0-360), saturation and brightness (x), accent / frame / text colours (#rrggbb), glow (x). */
  hue: number; sat: number; lit: number; accent: string; frame: string; text: string; glow: number; radius: number;
  belt: number;
  cls: number;
  hp: number; mp: number; exp: number; level: number;
  backplate: boolean; keys: boolean; menu: boolean; buffs: boolean;
  demo: boolean;
}

// — Mock data —

const HP_MAX = 4820, MP_MAX = 2150;

/** F1-F8 per class: [name, MagicInfo.Icon, MP cost, cooldown s] (names / icons from the server's default spell list,
 *  Envir.cs; drawn as MagIcon2[Icon * 2]). Costs and cooldowns are mock values sized so a few casts run MP short. */
const SKILLS: Record<number, [string, number, number, number][]> = {
  [MirClass.Warrior]: [["Fencing", 2, 0, 0.6], ["Slaying", 6, 0, 0.6], ["Thrusting", 11, 0, 0.6], ["HalfMoon", 24, 30, 1.5],
    ["FlamingSword", 25, 120, 10], ["TwinDrakeBlade", 37, 160, 8], ["CrossHalfMoon", 33, 220, 4], ["BladeAvalanche", 43, 380, 15]],
  [MirClass.Wizard]: [["FireBall", 0, 40, 1.2], ["ThunderBolt", 10, 70, 2], ["FireWall", 21, 140, 8], ["Lightning", 9, 180, 4],
    ["FrostCrunch", 38, 260, 6], ["ThunderStorm", 23, 380, 12], ["MagicShield", 30, 520, 30], ["IceStorm", 32, 700, 15]],
  [MirClass.Taoist]: [["Healing", 1, 40, 1.5], ["Poisoning", 5, 60, 2], ["SoulFireBall", 12, 90, 1.5], ["SummonSkeleton", 16, 260, 20],
    ["SoulShield", 13, 300, 30], ["BlessedArmour", 14, 300, 30], ["MassHealing", 28, 420, 8], ["SummonShinsu", 29, 650, 30]],
};
/** The slot shown "armed" (gold) by default per class: a toggled / charged skill. Right-click toggles any slot. */
const ARMED: Record<number, number> = { [MirClass.Warrior]: 4, [MirClass.Wizard]: 6, [MirClass.Taoist]: 4 };
/** Belt items that are used up when clicked (the belt's items are hudItems.ts `belt`; the 6-slot belt shows the first
 *  six). The belt takes any item: gear on it is worn with one click or key, the worn piece taking its place, so players
 *  keep a second set there to swap quickly. */
const USE_TYPES = new Set(["Potion", "Scroll"]);
/** What a looted kill drops into the bag: [name, Image, count]. */
const LOOT: [string, number, number][] = [["(HP)DrugLarge", 28, 1], ["(MP)DrugLarge", 29, 1], ["Amulet", 270, 20], ["Apple", 426, 1], ["TownTeleport", 402, 1], ["Timber", 368, 1]];
/** Mock buffs: [type, seconds (0 = permanent)], icons from buffs.ts buffIcon (the game's own map). */
const BUFFS: [number, number][] = [
  [BuffType.MagicShield, 95], [BuffType.SoulShield, 240], [BuffType.BlessedArmour, 600], [BuffType.Haste, 14], [BuffType.Exp, 3600], [BuffType.Rested, 0],
];
const BUFF_NAMES: Record<number, string> = Object.fromEntries(Object.entries(BuffType).map(([k, v]) => [v, k]));

// — Icons (24 x 24 stroke paths; placeholders until the art is drawn) —

const I = {
  guild: "M6 21V3M6 4h12l-3 4.5L18 13H6",
  // Backpack (the user's reference, 2026-10-15): carry loop, top straps, domed body, side pockets, front pocket with a zip.
  bag: "M6.5 11a5.5 5.5 0 0111 0v9a1.5 1.5 0 01-1.5 1.5h-8A1.5 1.5 0 016.5 20zM9.5 5.9V4a1.5 1.5 0 011.5-1.5h2A1.5 1.5 0 0114.5 4v1.9M7.6 8.4V5.6a.8.8 0 011.6 0v.9M16.4 8.4V5.6a.8.8 0 00-1.6 0v.9M6.5 13.5H5a1 1 0 00-1 1v3.5a1 1 0 001 1h1.5M17.5 13.5H19a1 1 0 011 1v3.5a1 1 0 01-1 1h-1.5M9 14.5h6a1 1 0 011 1v2.5a1 1 0 01-1 1H9a1 1 0 01-1-1v-2.5a1 1 0 011-1zM10.2 16.4h3.6",
  // Character (the user's reference): a large round head over closed rounded shoulders.
  person: "M12 2.5a4.6 4.6 0 110 9.2 4.6 4.6 0 010-9.2zM4.5 20.5c0-3.6 2.9-6.5 6.5-6.5h2c3.6 0 6.5 2.9 6.5 6.5 0 .6-.4 1-1 1h-13c-.6 0-1-.4-1-1z",
  star: "M12 2l2.8 6.2L21 9l-4.7 4.4 1.3 6.6L12 16.8 6.4 20l1.3-6.6L3 9l6.2-.8z",
  scroll: "M6 3h10l2 2v16H8l-2-2zM9 8h6M9 12h6M9 16h4",
  map: "M2 5l6-2 8 3 6-2v15l-6 2-8-3-6 2zM8 3v15M16 6v15",
  back: "M9 14L4 9l5-5M4 9h10a6 6 0 010 12h-3",
  cross: "M6 6l12 12M18 6L6 18",
  group: "M12 3l8 3v5c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6zM9 10a2 2 0 104 0 2 2 0 00-4 0M7.5 16c.5-2 2-3 4.5-3s4 1 4.5 3",
  shop: "M6 3h12l3 6-9 12L3 9zM3 9h18M9 3l3 6 3-6M12 9v12",
  mail: "M3 6h18v12H3zM3 6l9 7 9-7",
  friends: "M9 11a3 3 0 100-6 3 3 0 000 6zM17 11a2.5 2.5 0 100-5 2.5 2.5 0 000 5zM3 20c0-3.5 2.7-6 6-6s6 2.5 6 6M15 14.5c3 0 6 2 6 5",
  menu: "M4 6h16M4 12h16M4 18h16",
  ranking: "M4 20V10h4v10M10 20V4h4v16M16 20v-7h4v7",
  craft: "M14 4l6 6-3 3-6-6zM11 7l-8 8 3 3 8-8M4 20l2-2",
  creature: "M7 8a2 2 0 100-4 2 2 0 000 4zM17 8a2 2 0 100-4 2 2 0 000 4zM4.5 13a2 2 0 100-4 2 2 0 000 4zM19.5 13a2 2 0 100-4 2 2 0 000 4zM12 11c3 0 5.5 3.5 5.5 6.5 0 2-1.5 3-3 3-1 0-1.5-.6-2.5-.6s-1.5.6-2.5.6c-1.5 0-3-1-3-3C6.5 14.5 9 11 12 11z",
  mount: "M4 15c1-4 4-6 8-6h3l3-4 1 4 2 2-2 2h-3l-1 7M7 15v5M11 15l1 5M4 15l-1 3",
  fishing: "M18 3v11a4 4 0 01-8 0v-1M18 3l-3 3M10 13l-2 2M3 21c2-2 4-2 6 0s4 2 6 0 4-2 6 0",
  mentor: "M12 4l10 5-10 5L2 9zM6 11v5c3 2 9 2 12 0v-5M22 9v6",
  heart: "M12 20s-7.5-4.5-7.5-10A4.5 4.5 0 0112 7a4.5 4.5 0 017.5 3c0 5.5-7.5 10-7.5 10z",
  hero: "M12 3l2.5 5 5.5.8-4 3.9 1 5.5-5-2.6-5 2.6 1-5.5-4-3.9 5.5-.8zM12 8v5",
  gear: "M12 8.5a3.5 3.5 0 110 7 3.5 3.5 0 010-7zM12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1",
};

/** Mode slots: [value, short name for the slot, the desktop's name (Client Localization AttackMode_* / PetMode_*) for the list]. */
const ATTACK_MODES: [number, string, string][] = [
  [AttackMode.Peace, "Peaceful", "Peaceful"], [AttackMode.Group, "Group", "Group"], [AttackMode.Guild, "Guild", "Guild"],
  [AttackMode.EnemyGuild, "Enemy Guild", "Enemy Guild"], [AttackMode.RedBrown, "Red/Brown", "Red/Brown"], [AttackMode.All, "Attack All", "Attack All"],
];
const PET_MODES: [number, string, string][] = [
  [PetMode.Both, "Attack", "Attack and Move"], [PetMode.MoveOnly, "No Attack", "Do Not Attack"], [PetMode.AttackOnly, "No Move", "Do Not Move"],
  [PetMode.None, "Idle", "Do Not Attack or Move"], [PetMode.FocusMasterTarget, "Focus", "Focus Master Target"],
];
/** The menu strip: [name, key (desktop default binds, keybinds.ts), icon]. */
const MENU: [string, string, string][] = [
  ["Bag", "I / F9", I.bag], ["Character", "C / F10", I.person], ["Skills", "S / F11", I.star], ["Quests", "Q", I.scroll],
  ["Guild", "G", I.guild], ["Group", "P", I.group], ["Friends", "F", I.friends], ["Mail", "M", I.mail], ["Shop", "Y", I.shop], ["Big map", "B", I.map], ["Options", "O", I.gear],
];

const CSS = `
/* Theme: every colour comes from these (set by HudMock.set): panel hue / saturation / brightness, accent, frame, text, glow.
   HP / MP fills and the big-hit vignette keep their own colours. */
.hudm { --h: 218; --sat: 1; --lit: 1; --ac: #3fb4ff; --fr: #3a5a8c; --tx: #d6e2f5; --gl: 1; --rad: 5px;
  --pa: hsl(var(--h) calc(45% * var(--sat)) calc(15% * var(--lit)) / .94);
  --pb: hsl(var(--h) calc(55% * var(--sat)) calc(6.5% * var(--lit)) / .95);
  --pc: hsl(var(--h) calc(40% * var(--sat)) calc(18% * var(--lit)) / .95);
  --frl: color-mix(in srgb, var(--fr) 55%, #fff);
  --frd: color-mix(in srgb, var(--fr) 45%, #000);
  --acd: color-mix(in srgb, var(--ac) 70%, var(--tx));
  --dim: color-mix(in srgb, var(--tx) 55%, var(--fr));
  --glow: color-mix(in srgb, var(--ac) min(100%, calc(60% * var(--gl))), transparent);
  --glr: calc(10px * var(--gl));
  position: absolute; left: 0; top: 0; pointer-events: none; transform-origin: 0 0; overflow: hidden; font: 12px "Segoe UI", system-ui, sans-serif; color: var(--tx); }
.hudm * { box-sizing: border-box; }
.hudm .vign { position: absolute; inset: 0; opacity: 0; background: radial-gradient(ellipse 75% 70% at 50% 50%, transparent 55%, rgba(200, 10, 20, .55)); }
.hudm .vign.on { animation: hudm-vign .7s ease-out; }
@keyframes hudm-vign { 0% { opacity: 1; } 100% { opacity: 0; } }
.hudm .rig { position: absolute; left: 50%; bottom: 3px; transform-origin: 50% 100%; display: flex; align-items: flex-end; gap: 6px; }
.hudm .hm-plate { position: absolute; left: -46px; right: -46px; top: -16px; bottom: -3px; pointer-events: none;
  background: radial-gradient(ellipse 60% 120% at 50% 100%, var(--pb), color-mix(in srgb, var(--pb) 60%, transparent) 55%, transparent 80%); }
.hudm .hm-plate::before { content: ""; position: absolute; left: 12%; right: 12%; top: 6px; height: 1px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 45%, transparent), transparent); }
.hudm .hm-wing { position: relative; width: 240px; display: flex; flex-direction: column; gap: 3px; }
/* "hrow", not "row": lab.html styles .row (grid, 3 px margins) for its panel. */
.hudm .hm-hrow { display: flex; gap: 4px; }
.hudm .hm-hrow.stats { gap: 2px; }
.hudm .hm-belt { justify-content: space-between; }
.hudm .hm-belt.few { justify-content: center; }
.hudm .hm-ic { width: 13px; height: 13px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }

/* Stat plates */
.hudm .hm-stat { flex: 1; height: 19px; display: flex; align-items: center; gap: 6px; padding: 0 8px; pointer-events: auto;
  background: linear-gradient(180deg, var(--pa), var(--pb));
  box-shadow: inset 0 1px 0 color-mix(in srgb, var(--frl) 25%, transparent), inset 0 0 0 1px var(--fr);
  clip-path: polygon(6px 0, calc(100% - 6px) 0, 100% 50%, calc(100% - 6px) 100%, 6px 100%, 0 50%); font-size: 11px; }
.hudm .hm-stat b { color: var(--dim); font-weight: 700; letter-spacing: .04em; }
.hudm .hm-stat span { margin-left: auto; color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .hm-stat.warn span, .hudm .hm-stat.warn b { color: var(--ac); text-shadow: 0 0 6px var(--glow); }
.hudm .hm-stat.full { animation: hudm-pulse 1s ease-in-out infinite; }
.hudm .hm-stat.full span, .hudm .hm-stat.full b { color: #fff; text-shadow: 0 0 8px var(--ac); }

/* HP / MP bars: angled outer cap, flat fill, dark trail on a drop */
/* 28 px: makes the wings 136 tall, so the stat plates start at the diamond cap's apex (core y = -6). */
.hudm .hm-bar { position: relative; height: 28px; background: linear-gradient(180deg, var(--frl), var(--frd)); }
.hudm .hm-bar.hm-hp { clip-path: polygon(12px 0, 100% 0, 100% 100%, 12px 100%, 0 50%); }
.hudm .hm-bar.hm-mp { clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%); }
.hudm .hm-bar .in { position: absolute; inset: 1px; background: #04060a; box-shadow: inset 0 2px 4px #000; overflow: hidden; clip-path: inherit; }
.hudm .hm-bar .fill { position: absolute; top: 0; bottom: 0; transition: width .25s ease-out; }
.hudm .hm-bar .lag { position: absolute; top: 0; bottom: 0; transition: width .8s ease-in .45s; }
.hudm .hm-hp .fill { right: 0; background: linear-gradient(90deg, #4a080e, #a01a24 55%, #e0414a); }
.hudm .hm-hp .lag { right: 0; background: linear-gradient(90deg, #2a0306, #5c0a10); }
.hudm .hm-mp .fill { left: 0; background: linear-gradient(90deg, #3a8ef0, #1a50b8 45%, #0c2262); }
.hudm .hm-mp .lag { left: 0; background: linear-gradient(90deg, #0a1a40, #06102a); }
.hudm .hm-hp.low .fill { animation: hudm-pulse 1s ease-in-out infinite; }
.hudm .hm-bar.struck { animation: hudm-shake .3s linear; }
@keyframes hudm-pulse { 50% { filter: brightness(1.6); } }
@keyframes hudm-shake { 20% { transform: translateX(-3px); } 40% { transform: translateX(3px); } 60% { transform: translateX(-2px); } 80% { transform: translateX(1px); } }
.hudm .hm-bar .txt { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 600;
  text-shadow: 0 0 2px #000, 0 1px 1px #000; letter-spacing: .03em; font-variant-numeric: tabular-nums; }
.hudm .hm-bar .tag { position: absolute; top: 0; bottom: 0; display: flex; align-items: center; padding: 0 14px; font-size: 10px; font-weight: 700; color: rgba(255,255,255,.75); text-shadow: 0 1px 1px #000; }
.hudm .hm-hp .tag { left: 0; } .hudm .hm-mp .tag { right: 0; }

/* Slots */
.hudm .hm-slot { position: relative; flex: none; width: 40px; height: 40px; pointer-events: auto; cursor: pointer; border-radius: var(--rad); overflow: hidden;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%);
  border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hm-slot::before { content: ""; position: absolute; inset: 0; pointer-events: none; z-index: 3; border-radius: inherit; padding: 1px;
  background: linear-gradient(160deg, color-mix(in srgb, var(--frl) 70%, transparent), transparent 40%, transparent 60%, color-mix(in srgb, var(--frl) 50%, transparent));
  mask: linear-gradient(#000 0 0) content-box exclude, linear-gradient(#000 0 0); }
.hudm .hm-slot::after { content: ""; position: absolute; inset: 1px; pointer-events: none; z-index: 2; border-radius: calc(var(--rad) - 1px);
  box-shadow: inset 0 0 0 1px rgba(0,0,0,.9), inset 0 0 6px 1px rgba(0,0,0,.75); }
.hudm .hm-slot:hover { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
/* Icons draw 1:1 (MagIcon2 36 x 34, Items up to 36 x 32): a 40 px slot leaves a 36 x 36 icon box, never scaled. */
.hudm .hm-slot .ico { position: absolute; inset: 1px; background: center / auto no-repeat; image-rendering: pixelated; transition: filter .2s; }
.hudm .hm-slot .tint { position: absolute; inset: 2px; border-radius: calc(var(--rad) - 2px); z-index: 1; pointer-events: none; opacity: 0; transition: opacity .2s; background: color-mix(in srgb, var(--pb) 70%, transparent); }
.hudm .hm-slot .key { position: absolute; left: 2px; top: 0; z-index: 4; font-size: 9px; font-weight: 700; color: var(--tx); text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .hm-slot .cnt { position: absolute; right: 3px; bottom: 1px; z-index: 4; font-size: 10px; font-weight: 700; color: #fff; text-shadow: 0 0 2px #000, 0 0 2px #000; }
.hudm .hm-slot .cd { position: absolute; inset: 0; z-index: 2; pointer-events: none; }
.hudm .hm-slot .cdt { position: absolute; inset: 0; z-index: 4; display: flex; align-items: center; justify-content: center; font-size: 14px; font-weight: 700; color: #fff; text-shadow: 0 0 3px #000, 0 0 3px #000; }
.hudm .hm-slot .glint { position: absolute; inset: 0; z-index: 3; overflow: hidden; pointer-events: none; }
.hudm .hm-slot .glint::before { content: ""; position: absolute; top: -20%; bottom: -20%; width: 40%; left: -60%; transform: skewX(-20deg);
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--ac) 70%, #fff), transparent); }
.hudm .hm-slot.ready .glint::before { animation: hudm-glint .5s ease-out; }
@keyframes hudm-glint { to { left: 130%; } }
.hudm .hm-slot.nomp .ico { filter: grayscale(.8) brightness(.5); }
.hudm .hm-slot.nomp .tint { opacity: 1; }
/* Armed pulse: the big glow sits on .halo and only its opacity pulses (composited; animating the slot's box-shadow
   repainted it every frame). The slot stops clipping so the halo shows; its children clip themselves instead. */
.hudm .hm-slot.armed { border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 calc(var(--glr) * .8) var(--glow); overflow: visible; --halo: 0 0 calc(var(--glr) * 1.5) var(--ac); }
.hudm .hm-slot .halo { display: none; } .hudm .hm-slot.armed .halo { display: block; }
.hudm .hm-slot.armed .ico { clip-path: inset(0 round max(0px, calc(var(--rad) - 2px))); }
.hudm .hm-slot.armed .cd { clip-path: inset(0 round max(0px, calc(var(--rad) - 1px))); }
.hudm .hm-slot.armed .glint { border-radius: max(0px, calc(var(--rad) - 1px)); }
.hudm .hm-slot.empty .ico { filter: grayscale(1) brightness(.4); }
.hudm .hm-slot.empty .cnt { color: var(--dim); }
/* Hero belt cells (keys 9 / 0 on the 10-slot belt): an H badge, greyed out while no hero is summoned or it has fallen. */
.hudm .hm-slot .hb { position: absolute; left: 2px; top: 1px; z-index: 4; font-size: 8px; font-weight: 800; line-height: 1; padding: 1px 2px; border-radius: 2px; color: #000; background: var(--ac); }
.hudm .hm-slot.hero.off .ico { filter: grayscale(1) brightness(.45); }
.hudm .hm-slot.hero.off .hb { background: var(--dim); }
.hudm .hm-slot.flash { animation: hudm-flash .35s ease-out; }
@keyframes hudm-flash { from { box-shadow: 0 0 0 1px #000, 0 0 16px var(--ac), inset 0 0 18px var(--glow); } }
.hudm .hm-slot.deny { animation: hudm-shake .25s linear; }

/* Attack / pet mode slots */
.hudm .hm-slot.mode { width: 64px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 1px; text-align: center; }
.hudm .hm-slot.mode small { font-size: 9px; line-height: 11px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: var(--dim); }
.hudm .hm-slot.mode span { font-size: 10.5px; line-height: 13px; font-weight: 600; color: var(--ac); text-shadow: 0 0 calc(6px * var(--gl)) var(--glow); white-space: nowrap; }
.hudm .hm-modes { position: absolute; bottom: calc(100% + 6px); z-index: 10; min-width: 130px; padding: 4px; border-radius: var(--rad); pointer-events: auto;
  background: linear-gradient(180deg, var(--pa), var(--pb)); border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000, 0 6px 14px #000; }
.hudm .hm-modes.l { left: 0; } .hudm .hm-modes.r { right: 0; }
.hudm .hm-modes div { display: flex; align-items: center; gap: 7px; padding: 4px 8px; border-radius: calc(var(--rad) * .6); cursor: pointer; white-space: nowrap; }
.hudm .hm-modes div:hover { background: color-mix(in srgb, var(--ac) 22%, transparent); }
.hudm .hm-modes div.on { box-shadow: inset 2px 0 0 var(--ac); }

/* Centre diamond (SVG colours are set as styles so the theme variables reach them) */
.hudm .hm-core { position: relative; width: 120px; height: 130px; flex: none; }
.hudm .hm-core svg.dia { position: absolute; inset: 0; overflow: visible; pointer-events: none; }
.hudm .hm-core .hit { position: absolute; left: 20px; right: 20px; top: 20px; bottom: 20px; pointer-events: auto; transform: rotate(45deg) scale(.85); }
.hudm .hm-core .lv { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; pointer-events: none; }
.hudm .hm-core .lv small { font-size: 9px; letter-spacing: .2em; color: var(--dim); }
.hudm .hm-core .lv b { font: 700 30px/1 Georgia, "Times New Roman", serif; color: #f2f6ff; text-shadow: 0 0 var(--glr) var(--glow), 0 2px 2px #000; }
.hudm .hm-core .lv span { margin-top: 3px; font-size: 11px; font-weight: 600; color: var(--acd); font-variant-numeric: tabular-nums; }
.hudm .hm-core.lvup .lv b { animation: hudm-pop .9s ease-out; }
@keyframes hudm-pop { 0% { transform: scale(1); } 25% { transform: scale(1.5); color: #fff; text-shadow: 0 0 22px var(--ac); } 100% { transform: scale(1); } }
.hudm .hm-core .burst { transform-box: fill-box; transform-origin: center; opacity: 0; }
.hudm .hm-core.lvup .burst { animation: hudm-burst 1s ease-out; }
.hudm .hm-core.lvup .burst.b2 { animation-delay: .15s; }
@keyframes hudm-burst { 0% { opacity: 1; transform: scale(1); } 100% { opacity: 0; transform: scale(1.9); } }
.hudm .hm-float { position: absolute; left: 50%; top: -6px; transform: translateX(-50%); white-space: nowrap; font-weight: 700; pointer-events: none;
  text-shadow: 0 0 4px #000, 0 1px 2px #000; animation: hudm-rise 1.4s ease-out forwards; }
.hudm .hm-float.big { font: 700 18px Georgia, serif; color: #fff; text-shadow: 0 0 10px var(--ac), 0 2px 2px #000; }
.hudm .hm-float.exp { font-size: 12px; color: var(--acd); }
@keyframes hudm-rise { 0% { opacity: 0; margin-top: 8px; } 15% { opacity: 1; } 100% { opacity: 0; margin-top: -34px; } }

/* Corner triangles under the diamond (the user's sketches, 2026-10-15), drawn in the diamond's SVG: a right angle in each
   bottom corner of the core, the long edge parallel to the diamond's nearest edge, 9 px out from it (past the black stroke).
   Bottom corners only: Log out / Exit game (top Inventory / Character triangles tried and dropped, 2026-10-15). */
.hudm .hm-core .tri { cursor: pointer; }
.hudm .hm-core .tri polygon { pointer-events: auto; stroke-width: 1.5; stroke-linejoin: round; }
.hudm .hm-core .tri path { pointer-events: none; fill: none; stroke: var(--acd); stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hm-core .tri:hover polygon { stroke: var(--ac); filter: drop-shadow(0 0 calc(4px * var(--gl)) var(--ac)); }
.hudm .hm-core .tri:hover path { stroke: #fff; }
.hudm .hm-core .tri.flash polygon { animation: hudm-triflash .35s ease-out; }
@keyframes hudm-triflash { from { fill: var(--ac); } }

/* Menu strip (above the diamond) and buff row (above the left wing) */
.hudm .hm-menu { position: absolute; left: 50%; bottom: calc(100% + 14px); transform: translateX(-50%); display: flex; gap: 4px; }
.hudm .hm-mbtn { position: relative; width: 26px; height: 26px; display: flex; align-items: center; justify-content: center; pointer-events: auto; cursor: pointer;
  color: var(--acd); background: linear-gradient(180deg, var(--pa), var(--pb));
  border: 1px solid var(--fr); border-radius: calc(var(--rad) * .7); box-shadow: 0 0 0 1px #000, inset 0 1px 0 color-mix(in srgb, var(--frl) 25%, transparent); transform: rotate(45deg) scale(.82); }
.hudm .hm-mbtn .hm-ic { transform: rotate(-45deg); width: 15px; height: 15px; }
.hudm .hm-mbtn .badge { position: absolute; right: -7px; top: -7px; min-width: 14px; height: 14px; padding: 0 3px; box-sizing: border-box; transform: rotate(-45deg);
  border-radius: 7px; font: 700 9px/14px "Segoe UI", system-ui, sans-serif; text-align: center; color: #fff; background: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 6px var(--glow); }
.hudm .hm-mbtn:hover { color: #fff; border-color: var(--ac); box-shadow: 0 0 0 1px #000, 0 0 var(--glr) var(--glow); }
.hudm .hm-buffs { position: absolute; left: 0; bottom: calc(100% + 8px); display: flex; gap: 4px; }
.hudm .hm-buff { position: relative; width: 26px; pointer-events: auto; text-align: center; }
.hudm .hm-buff i { display: block; width: 26px; height: 26px; background: center / contain no-repeat; image-rendering: pixelated; border-radius: calc(var(--rad) * .7);
  border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hm-buff span { display: block; margin-top: 1px; font-size: 9px; font-weight: 600; color: var(--tx); text-shadow: 0 0 2px #000, 0 0 2px #000; font-variant-numeric: tabular-nums; }
.hudm .hm-buff.ending i { animation: hudm-blink .6s steps(2) infinite; }
@keyframes hudm-blink { 50% { opacity: .35; } }
.hudm.nokeys .hm-slot .key { display: none; }
`;

/** A skill bar slot; `key` = the Spell enum key (the Skills window matches spells by it). */
type Spell = { key: string; ico: HTMLElement; el: HTMLElement; cd: SVGPathElement; cdt: HTMLElement; until: number; len: number; cost: number; active: boolean };
type BeltSlot = { el: HTMLElement; ico: HTMLElement; cnt: HTMLElement; until: number; cd: SVGPathElement; cdt: HTMLElement; active: boolean };

const h = <K extends keyof HTMLElementTagNameMap>(tag: K, cls = "", ...kids: (Node | string)[]): HTMLElementTagNameMap[K] => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  n.append(...kids);
  return n;
};
const SVG = "http://www.w3.org/2000/svg";
const svg = <K extends keyof SVGElementTagNameMap>(tag: K, attrs: Record<string, string | number>): SVGElementTagNameMap[K] => {
  const n = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v));
  return n;
};
const icon = (d: string, cls = "hm-ic") => { const s = svg("svg", { class: cls, viewBox: "0 0 24 24" }); s.append(svg("path", { d })); return s; };
const restart = restartAnim;
const fmtTime = (s: number) => (s >= 3600 ? `${Math.floor(s / 3600)}h` : s >= 60 ? `${Math.floor(s / 60)}m` : `${Math.ceil(s)}s`);
const fmtGold = (g: number) => (g >= 1e6 ? (g / 1e6).toFixed(2) + "M" : g >= 1e3 ? (g / 1e3).toFixed(1) + "K" : String(g));
const rand = (a: number, b: number) => a + Math.random() * (b - a);

/** Cooldown sweep: a pie from 12 o'clock, clockwise, covering the remaining fraction of a 44 x 44 slot. */
function piePath(frac: number): string {
  if (frac <= 0) return "";
  if (frac >= 1) return "M0 0H44V44H0Z";
  const a = (1 - frac) * Math.PI * 2;
  const r = 40, cx = 22, cy = 22;
  const x = cx + r * Math.sin(a), y = cy - r * Math.cos(a);
  return `M${cx} ${cy}L${x.toFixed(2)} ${y.toFixed(2)}A${r} ${r} 0 ${frac > 0.5 ? 1 : 0} 1 ${cx} ${cy - r}Z`;
}

/** Diamond geometry (viewBox 0 0 120 130, the wings' height): the EXP ring runs on D from the top vertex, clockwise. */
const D = "M60 4L116 65L60 126L4 65Z";
const D_OUT = "M60 -2L122 65L60 132L-2 65Z";
const D_IN = "M60 13L108 65L60 117L12 65Z";
const VERTS: [number, number, number][] = [[25, 116, 65], [50, 60, 126], [75, 4, 65]];

export class HudMock {
  readonly root: HTMLElement;
  private rig: HTMLElement;
  private plate: HTMLElement;
  private vign: HTMLElement;
  private menu: HTMLElement;
  /** Windows (Character for now), scaled with the HUD. */
  private wins: HTMLElement;
  readonly character = new CharacterMock();
  readonly inventory = new InventoryMock();
  readonly quests = new QuestsMock();
  readonly guild = new GuildMock();
  readonly friends = new FriendsMock();
  readonly options = new OptionsMock();
  /** The lab fills bigMap.host (its map, camera, moving the camera, loading a map). */
  readonly bigMap = new BigMapMock();
  readonly mail = new MailMock();
  readonly trade = new TradeMock();
  readonly minimap = new MinimapMock();
  readonly group = new GroupMock();
  /** Opened at the storage NPC (the lab's Simulate > Storage NPC). */
  readonly storage = new StorageMock();
  /** Opened at a merchant NPC (Simulate > Merchant NPC). */
  readonly merchant = new MerchantMock({
    gold: () => this.gold, spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, earn: (n) => { this.gold += n; },
    say: (t) => this.chat.system(t), gearChanged: () => this.extras.drawDura(),
    confirm: (o) => this.popups.confirm(o), // plain text: item names come from data
  });
  readonly gameShop = new GameShopMock();
  readonly ranking = new RankingMock();
  readonly creature = new CreatureMock();
  readonly mount = new MountMock();
  readonly fishing = new FishingMock();
  readonly hero = new HeroMock();
  readonly keybinds = new KeybindsMock();
  readonly help = new HelpMock();
  readonly mentor = new MentorMock();
  readonly relationship = new RelationshipMock();
  readonly chat = new ChatMock();
  readonly tooltip: ItemTooltip;
  readonly npc = new NpcMock({
    gold: () => this.gold,
    level: () => this.level,
    spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; },
    weapon: () => "DragonSlayer",
    world: () => this.minimap.host ?? undefined,
    open: (s) => {
      if (s === "storage") this.storage.openAtNpc();
      else if (s === "disassemble" || s === "downgrade" || s === "reset") this.npcBox.openService(s);
      // Repair and special repair are the merchant's Repair tab (worn gear and bag in one list, hudRepair.ts).
      else if (s === "repair" || s === "srepair") this.merchant.openAtNpc("Repair", s === "srepair");
      else this.merchant.openAtNpc(s === "buy" ? "Buy" : "Sell");
    },
    toast: (t) => this.chat.system(t),
  });
  readonly trust = new TrustMock({
    gold: () => this.gold, level: () => this.level, cls: () => ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][this.opts?.cls ?? 0] ?? "Warrior",
    spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, gain: (n) => { this.gold += n; }, say: (t) => this.chat.system(t),
  });
  readonly craft = new CraftMock({
    gold: () => this.gold, level: () => this.level, cls: () => ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][this.opts?.cls ?? 0] ?? "Warrior",
    spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, say: (t) => this.chat.system(t), market: (n) => this.trust.search(n),
  });
  readonly refine = new RefineMock({
    gold: () => this.gold, spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, say: (t) => this.chat.system(t),
  });
  readonly awaken = new AwakenMock({
    gold: () => this.gold, spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, say: (t) => this.chat.system(t),
  });
  readonly socket = new SocketMock({ say: (t) => this.chat.system(t) });
  readonly rent = new RentMock({
    gold: () => this.gold, spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; }, gain: (n) => { this.gold += n; }, say: (t) => this.chat.system(t),
  });
  readonly login = new LoginMock();
  readonly select = new SelectMock();
  readonly loading = new LoadingMock();
  readonly inspect = new InspectMock({ whisper: (n) => this.chat.whisperTo(n), say: (t) => this.chat.system(t) });
  /** The lab's world (map, camera, objects, time / weather) for the minimap and the big map. */
  setWorld(host: WorldHost): void {
    this.bigMap.host = host;
    this.minimap.host = host;
    this.minimap.party = () => this.group.positions(host.cam());
  }
  readonly skills = new SkillsMock({
    level: () => this.level,
    cls: () => this.opts?.cls ?? MirClass.Wizard,
    keyOf: (key) => this.spells.findIndex((sp) => sp.key === key),
    assign: (slot, entry) => this.assignSkill(slot, entry),
  });
  private buffRow: HTMLElement;
  private core!: HTMLElement;
  private spells: Spell[] = [];
  private belt: BeltSlot[] = [];
  private buffs: { el: HTMLElement; t: HTMLElement; type: number; len: number; ends: number }[] = [];
  private hpFill!: HTMLElement; private hpLag!: HTMLElement; private hpTxt!: HTMLElement; private hpBar!: HTMLElement;
  private mpFill!: HTMLElement; private mpLag!: HTMLElement; private mpTxt!: HTMLElement; private mpBar!: HTMLElement;
  private expPath!: SVGPathElement; private expHead!: SVGCircleElement; private ticks: SVGPathElement[] = [];
  private lvNum!: HTMLElement; private lvPct!: HTMLElement;
  /** The six stat plates (left wing first), the stat each tracks (hudStatPick.ts) and the open stat list. */
  private plateEls: { el: HTMLElement; b: HTMLElement; v: HTMLElement }[] = [];
  private plates = loadPlates();
  private readonly statPick = new StatPicker();
  /** The live plates' texts this frame (bag count, bag weight, gold). */
  private liveText: Record<string, string> = { Bag: "", Weight: "", Gold: "" };
  private modePop: HTMLElement | null = null;
  private attack: number = AttackMode.Group;
  private pet: number = PetMode.Both;
  private built = "";
  private opts!: HudMockOptions;
  private screenH = 768;
  private screenW = 1024;
  // Simulation state (the knobs seed it; the events change it).
  private hp = 0; private mp = 0; private exp = 0; private expShown = 0; private level = 1;
  private gold = 1_240_000;
  private nextDemo = 0;
  private shown = { hp: -1, mp: -1, exp: -1, level: -1 };
  private shownTicks = -1;
  private bagDirty = true; private bagAt = 0; private bagUsed = 0; private bagKg = 0;

  constructor(parent: HTMLElement) {
    if (!document.getElementById("hudm-css")) {
      const style = document.createElement("style");
      style.id = "hudm-css";
      style.textContent = CSS;
      document.head.append(style);
    }
    this.root = h("div", "hudm");
    this.vign = h("div", "vign");
    this.plate = h("div", "hm-plate");
    this.menu = h("div", "hm-menu");
    this.buffRow = h("div", "hm-buffs");
    this.rig = h("div", "rig");
    this.wins = h("div", "wins", this.chat.notice, this.extras.timers, this.extras.compass, this.extras.status, this.extras.dura, this.chat.el, this.quests.tracker, this.character.el, this.inventory.el, this.skills.el, this.quests.el, this.guild.el, this.friends.el, this.options.el, this.bigMap.el, this.mail.el, this.trade.el, this.trade.request, this.minimap.el, this.group.frames, this.group.el, this.storage.el, this.merchant.el, this.gameShop.el, this.ranking.el, this.creature.el, this.mount.el, this.fishing.el, this.hero.frame, this.hero.el, this.keybinds.el, this.help.el, this.mentor.el, this.relationship.el, this.npc.el, this.inspect.el, this.trust.el, this.craft.el, this.refine.el, this.awaken.el, this.socket.el, this.rent.el, this.npcBox.el, this.territory.el, this.extras.dice, this.gameMenu.el, this.popups.requests, this.popups.veil, this.login.el, this.select.el, this.loading.el);
    this.minimap.onBigMap = () => this.bigMap.toggle();
    this.help.onOpen = (w) => {
      if (w === "trade") this.trade.ask();
      else if (w === "merchant") this.merchant.openAtNpc();
      else this[w].toggle(true);
    };
    this.root.append(this.extras.fx, this.vign, this.rig, this.wins);
    this.extras.onPoison = (f) => this.hpBar?.classList.toggle("psn", !!(f & 1));
    this.tooltip = new ItemTooltip(this.root, this.wins);
    this.wins.append(this.tooltip.el);
    this.tooltip.onLink = (name) => this.chat.insertLink(name);
    // Drag and drop between the bag, equipment, the belt, storage and trade (hudDrag.ts); the belt is a host here.
    installDrag(this.root, (t) => this.chat.system(t));
    const hud = this;
    const beltHost: DragHost = registerHost({
      id: "belt", get size() { return hud.playerCells(); }, live: () => !this.root.hidden,
      get: (i) => i < this.playerCells() ? belt[i] ?? null : null,
      set: (i, st) => { belt[i] = st; this.showBelt(i); bagChanged(); },
      accepts: () => "",
      verb: () => "Belt",
      quick: (i) => { const st = belt[i]; return st ? bestCell(host("bag"), st, beltHost) : null; },
    });
    // The hero's belt (cells 8-9 of the 10-slot belt): potions only, drunk by the hero.
    const heroHost: DragHost = registerHost({
      id: "herobelt", size: heroBelt.length, live: () => !this.root.hidden && this.belt.length > PLAYER_BELT,
      get: (i) => heroBelt[i] ?? null,
      set: (i, st) => { heroBelt[i] = st; this.showBelt(PLAYER_BELT + i); bagChanged(); },
      accepts: (_i, st) => HEAL[st.name] ? "" : "Hero belt: potions only",
      verb: () => "Hero belt",
      quick: (i) => { const st = heroBelt[i]; return st ? bestCell(host("bag"), st, heroHost) : null; },
    });
    onBag(() => { this.bagDirty = true; this.belt.forEach((_, i) => this.showBelt(i)); });
    this.chat.onInspect = (name) => this.inspect.show(name);
    // Login → character select → game, and back.
    this.login.onDone = () => this.select.show();
    this.select.onBack = () => this.login.show();
    this.select.onStart = (c) => { void this.loading.show(c.map, c.level); this.loading.onDone = () => this.chat.system(`Welcome to Mir, ${c.name}!`); };
    // C / F10 and I / F9 (the desktop's Equipment / Inventory binds) toggle the Character / Inventory windows while the HUD is shown.
    window.addEventListener("keydown", (e) => {
      if (this.root.hidden) return; // Show HUD mock off: every key is the lab's
      if (!this.select.el.hidden) { this.select.key(e); e.stopImmediatePropagation(); return; }
      if (!this.login.el.hidden || e.ctrlKey || e.altKey || (e.target as HTMLElement).closest?.("input, textarea, select")) return;
      // Esc with nothing open: the game menu (an open window takes Esc first: hudWindow.ts).
      if (e.key === "Escape" && !e.shiftKey && !this.gameMenu.open && !HudWindow.anyOpen() && this.loading.el.hidden) { e.preventDefault(); this.gameMenu.show(); return; }
      // 1-6 (or 1-0 on the 10-slot belt): the belt, like the desktop's belt keys.
      const digit = /^Digit(\d)$/.exec(e.code);
      if (digit && !e.shiftKey) { const n = (+digit[1]! + 9) % 10; if (n < this.belt.length) { e.preventDefault(); this.useBelt(n); } }
      if (e.code === "KeyC" || e.code === "F10") { e.preventDefault(); this.character.toggle(); }
      if (e.code === "KeyI" || e.code === "F9") { e.preventDefault(); this.inventory.toggle(); }
      if (e.code === "KeyQ") { e.preventDefault(); this.quests.toggle(); }
      if (e.code === "KeyF") { e.preventDefault(); this.friends.toggle(); }
      if (e.code === "KeyB") { e.preventDefault(); this.bigMap.toggle(); }
      if (e.code === "KeyM") { e.preventDefault(); this.mail.toggle(); }
      if (e.code === "KeyP") { e.preventDefault(); this.group.toggle(); }
      if (e.code === "KeyY") { e.preventDefault(); this.gameShop.toggle(); }
      if (e.code === "KeyK") { e.preventDefault(); this.ranking.toggle(); }
      if (e.code === "KeyE") { e.preventDefault(); this.creature.toggle(); }
      if (e.code === "KeyJ") { e.preventDefault(); this.mount.toggle(); }
      if (e.code === "KeyN") { e.preventDefault(); this.fishing.toggle(); }
      if (e.code === "KeyH") { e.preventDefault(); e.stopImmediatePropagation(); this.hero.toggle(); }
      if (e.code === "KeyU") { e.preventDefault(); this.keybinds.toggle(); }
      if (e.code === "Slash") { e.preventDefault(); this.help.toggle(); }
      if (e.code === "KeyX") { e.preventDefault(); this.mentor.toggle(); }
      if (e.code === "KeyL") { e.preventDefault(); this.relationship.toggle(); }
      if (e.code === "Enter" || e.code === "NumpadEnter") { e.preventDefault(); this.chat.focus(); }
      // T: a simulated trade request (trades start from another player, not a key).
      if (e.code === "KeyT") { e.preventDefault(); this.trade.ask(); }
      // O opens Options (the desktop's F12 is the browser's developer tools here).
      if (e.code === "KeyO") { e.preventDefault(); this.options.toggle(); }
      // G (Guilds); G is also the lab's grade toggle, so it stops here while the HUD is shown.
      if (e.code === "KeyG") { e.preventDefault(); e.stopImmediatePropagation(); this.guild.toggle(); }
      // S / F11 (Skills / Skills2); S also pans the lab's camera, so it stops here while the HUD is shown.
      if (e.code === "KeyS" || e.code === "F11") { e.preventDefault(); e.stopImmediatePropagation(); this.skills.toggle(); }
    });
    this.root.addEventListener("mousedown", (e) => { if ((e.target as HTMLElement).closest(".hm-slot, .hit, .hm-mbtn, .hm-modes, .hm-spick, .hm-buff, .hm-stat, .tri, .hwin, .qtrack, .treq, .mmap, .party")) e.stopPropagation(); });
    this.root.addEventListener("contextmenu", (e) => { if ((e.target as HTMLElement).closest(".hm-slot")) e.preventDefault(); });
    document.addEventListener("mousedown", (e) => { if (this.modePop && !(e.target instanceof Element && e.target.closest(".hm-modes, .hm-slot.mode"))) this.closeModes(); });
    parent.append(this.root);
  }

  /** Lay the overlay exactly over the canvas (its game-pixel size, scale and offset). */
  place(width: number, height: number, scale: number, left: string, top: string): void {
    this.screenH = height;
    this.screenW = width;
    Object.assign(this.root.style, { width: width + "px", height: height + "px", transform: `scale(${scale})`, left, top });
    if (this.opts) this.layout();
  }

  /** The HUD's level (the windows read it). */
  get playerLevel(): number { return this.level; }
  /** Generic popups: message / input / amount boxes and invite toasts (hudPopup.ts). */
  readonly popups = new Popups();
  private sellNoAsk = false;
  /** The lab's Simulate > Popup buttons: the desktop's common boxes with mock outcomes. */
  async popupDemo(which: string): Promise<void> {
    const P = this.popups, say = (t: string) => this.chat.system(t);
    switch (which) {
      case "teleport": {
        const r = await P.confirm({ title: "Teleport", text: "Teleport to <b>Bill the Merchant</b> in Bichon Province?", html: true, buttons: "yesno", gold: 2000 });
        if (r?.answer !== "yes") break;
        if (this.gold < 2000) { await P.confirm({ kind: "warning", title: "Not enough gold", text: "You need <span class=gold>2,000 gold</span> to teleport.", html: true }); break; }
        this.gold -= 2000; say("Teleported to Bill the Merchant (-2,000 gold).");
        break;
      }
      case "notice": await P.confirm({ text: "You can't fish without a <b>fishing rod</b> equipped.\nBuy one from the fishing merchant in Bichon.", html: true }); break;
      case "delete": {
        const r = await P.confirm({ kind: "danger", title: "Delete parcel?", text: "This parcel still has <b>items or gold</b> in it.\nDeleting it destroys them for good.", html: true, buttons: "yesno", hold: 900,
          labels: { yes: "Delete", no: "Keep" }, item: { name: "DragonSword", img: 48 } });
        say(r?.answer === "yes" ? "Parcel deleted." : "Kept the parcel.");
        break;
      }
      case "reconnect": {
        const r = await P.confirm({ kind: "warning", title: "Connection lost", text: "The connection to the server was lost.\nReconnecting automatically…", buttons: "okcancel", countdown: 10,
          timeoutAnswer: "ok", labels: { ok: "Reconnect", cancel: "Back to login" } });
        say(r?.answer === "ok" ? "Reconnected." : "Returned to the login screen (mock).");
        break;
      }
      case "save": {
        const r = await P.confirm({ title: "Unsaved key bindings", text: "Save your changed key bindings before closing?", buttons: "yesnocancel", labels: { yes: "Save", no: "Discard" } });
        say(r?.answer === "yes" ? "Key bindings saved (mock)." : r?.answer === "no" ? "Changes discarded." : "Kept editing.");
        break;
      }
      case "sell": {
        if (this.sellNoAsk) { say("Sold DragonSword for 6,000 gold without asking (you ticked Don't ask again)."); break; }
        const r = await P.confirm({ title: "Sell item", text: "Sell this to the merchant for <span class=gold>6,000 gold</span>?", html: true, buttons: "yesno", item: { name: "DragonSword", img: 48 }, dontAsk: true });
        if (r?.answer === "yes") { this.sellNoAsk = r.dontAsk; say(`Sold DragonSword for 6,000 gold${r.dontAsk ? "; you won't be asked again this session" : ""}.`); }
        break;
      }
      case "rename": {
        const v = await P.input({ title: "Name your creature", text: "Pick a name for <b>Bengal Tiger</b> (3-12 letters or numbers).", html: true, placeholder: "New name", min: 3, max: 12, chars: "A-Za-z0-9",
          check: (x) => /^(gm|admin)/i.test(x) ? "That name is reserved" : "" });
        if (v) say(`Your creature is now called ${v}.`);
        break;
      }
      case "mail": {
        const v = await P.input({ title: "New mail", text: "Who is the parcel for?", placeholder: "Character name", max: 14, chars: "A-Za-z0-9", suggestions: ["Seren", "Wren", "Kaelith", "Garrick", "Elowen"] });
        if (v) say(`Writing a parcel to ${v}.`);
        break;
      }
      case "gold": {
        const n = await P.amount({ title: "Deposit gold", text: "Put gold in the <b>guild storage</b>.", html: true, gold: true, max: this.gold, value: Math.min(100_000, this.gold) });
        if (n) { this.gold -= n; say(`Deposited ${n.toLocaleString("en-GB")} gold in the guild storage.`); }
        break;
      }
      case "stack": {
        const st = bag.find((b) => b && b.count > 1);
        if (!st) { say("No stack in the bag to sell."); break; }
        const n = await P.amount({ title: "Sell amount", item: st, max: st.count, value: st.count });
        if (!n) break;
        const i = bag.indexOf(st);
        if (i >= 0) bag[i] = st.count - n > 0 ? { ...st, count: st.count - n } : null;
        this.gold += n * 25; bagChanged();
        say(`Sold ${n} ${st.name} for ${(n * 25).toLocaleString("en-GB")} gold.`);
        break;
      }
      case "group": {
        const ok = await P.request({ from: "Wren", tag: "Group invite", text: "invites you to their group", seconds: 20 });
        say(ok ? "You joined Wren's group." : "You declined Wren's group invite.");
        break;
      }
      case "guild": {
        const ok = await P.request({ from: "Garrick", tag: "Guild invite", text: "invites you to join Nightfall", seconds: 25, accept: "Join" });
        say(ok ? "You joined the guild Nightfall (mock)." : "You declined the guild invite.");
        break;
      }
      case "burst":
        void this.popupDemo("notice"); void this.popupDemo("teleport"); void this.popupDemo("rename");
        break;
    }
  }
  /** When the character last fought (a hit taken or a spell cast): Log out / Exit wait 10 s after it (GameScene.LogTime). */
  private lastCombat = -1e9; // never fought yet
  /** The game menu (hudGameMenu.ts): Esc with no window open, or the menu strip's last button. */
  readonly gameMenu: GameMenu = new GameMenu({
    profile: () => {
      const map = this.minimap.host?.map() ?? "";
      return { name: "GMRyu", level: this.level, cls: ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][this.opts?.cls ?? 0] ?? "Warrior", guild: "GameMaster",
        map: loadMapData().then((rows) => (rows.find((r) => r.f === map)?.t ?? (map || "Unknown map")).replace(/([a-z])([A-Z])/g, "$1 $2")) };
    },
    combatLock: () => Math.max(0, Math.ceil((this.lastCombat + 10_000 - performance.now()) / 1000)),
    windows: () => [
      { name: "Character", key: "C", code: "KeyC", icon: I.person, open: () => this.character.toggle(true) },
      { name: "Bag", key: "I", code: "KeyI", icon: I.bag, open: () => this.inventory.toggle(true) },
      { name: "Skills", key: "S", code: "KeyS", icon: I.star, open: () => this.skills.toggle(true) },
      { name: "Quests", key: "Q", code: "KeyQ", icon: I.scroll, open: () => this.quests.toggle(true) },
      { name: "World map", key: "B", code: "KeyB", icon: I.map, open: () => this.bigMap.toggle(true) },
      { name: "Mail", key: "M", code: "KeyM", icon: I.mail, open: () => this.mail.toggle(true), badge: () => { const n = this.mail.unread(); return n ? String(n) : ""; } },
      { name: "Guild", key: "G", code: "KeyG", icon: I.guild, open: () => this.guild.toggle(true) },
      { name: "Group", key: "P", code: "KeyP", icon: I.group, open: () => this.group.toggle(true) },
      { name: "Friends", key: "F", code: "KeyF", icon: I.friends, open: () => this.friends.toggle(true) },
      { name: "Mentor", key: "X", code: "KeyX", icon: I.mentor, open: () => this.mentor.toggle(true) },
      { name: "Relationship", key: "L", code: "KeyL", icon: I.heart, open: () => this.relationship.toggle(true) },
      { name: "Hero", key: "H", code: "KeyH", icon: I.hero, open: () => this.hero.toggle(true) },
      { name: "Ranking", key: "K", code: "KeyK", icon: I.ranking, open: () => this.ranking.toggle(true) },
      { name: "Territories", key: "", icon: "M3 21h18M5 21V9l7-5 7 5v12M9 21v-6h6v6M12 4V1l3 1-3 1", open: () => this.territory.toggle(true) },
      { name: "Crafting", key: "", icon: I.craft, open: () => this.craft.toggle(true) },
      { name: "Creatures", key: "E", code: "KeyE", icon: I.creature, open: () => this.creature.toggle(true) },
      { name: "Mount", key: "J", code: "KeyJ", icon: I.mount, open: () => this.mount.toggle(true) },
      { name: "Fishing", key: "N", code: "KeyN", icon: I.fishing, open: () => this.fishing.toggle(true) },
      { name: "Game shop", key: "Y", code: "KeyY", icon: I.shop, open: () => this.gameShop.toggle(true) },
      { name: "Options", key: "O", code: "KeyO", icon: I.gear, open: () => this.options.toggle(true) },
      { name: "Key bindings", key: "U", code: "KeyU", icon: "M3 7h18v11H3zM6 10h1M9 10h1M12 10h1M15 10h1M7 14h10", open: () => this.keybinds.toggle(true) },
    ],
    options: () => this.options.toggle(true), keybinds: () => this.keybinds.toggle(true), help: () => this.help.toggle(true),
    confirm: async (title, text, danger) => (await this.popups.confirm({ title, text, kind: danger ? "danger" : "question", buttons: "yesno" }))?.answer === "yes",
    logOut: () => { HudWindow.closeAll(); this.chat.system("Logged out (mock)."); this.select.show(); },
    exit: () => { HudWindow.closeAll(); this.login.show(); },
    say: (t) => this.chat.system(t),
  });
  /** Guild territories (hudTerritory.ts): the estates, rent / extend / sell / buy, teleport in. */
  readonly territory: TerritoryMock = new TerritoryMock({
    world: () => this.minimap.host ?? undefined,
    say: (t) => this.chat.system(t),
    confirm: async (title, text) => (await this.popups.confirm({ title, text, buttons: "yesno" }))?.answer === "yes", // plain text: owner names come from data
    amount: (title, text, min, max, value) => this.popups.amount({ title, text, gold: true, min, max, value, leftLabel: "more than the minimum" }),
  });
  /** The NPC item box (hudNpcBox.ts): disassemble, downgrade, reset, wedding ring. */
  readonly npcBox: NpcBoxMock = new NpcBoxMock({
    gold: () => this.gold, spend: (n) => { if (this.gold < n) return false; this.gold -= n; return true; },
    say: (t) => this.chat.system(t), gearChanged: () => this.extras.drawDura(),
  });
  /** Durability strip, poisons, compass, timers, dice (hudExtras.ts). */
  readonly extras: HudExtras = new HudExtras({
    world: () => this.minimap.host ?? undefined,
    hp: (pct) => { this.hp = Math.max(1, Math.min(100, this.hp + pct)); },
    bigHit: () => this.hit(true),
    say: (t) => this.chat.system(t),
    openCharacter: () => this.character.toggle(true),
  });

  /** The HUD's scale on the game screen: the knob, times screen height / 768 when Auto scale is on. */
  effectiveScale(): number {
    return (this.opts?.scale ?? 1) * (this.opts?.autoScale ? this.screenH / 768 : 1);
  }
  /** Width of the HUD in game pixels at the current scale. */
  widthPx(): number {
    return this.rig.offsetWidth * this.effectiveScale();
  }

  set(o: HudMockOptions): void {
    const prev = this.opts;
    this.opts = o;
    const key = `${o.belt}/${o.cls}`;
    if (this.built !== key) { this.build(o.belt, o.cls); setMe(this.level || o.level, ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][o.cls] ?? "Warrior"); this.character.setProfile(this.level || o.level, o.cls); this.mentor.setLevel(this.level || o.level); this.relationship.setLevel(this.level || o.level); this.tooltip.setProfile(this.level || o.level, ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][o.cls] ?? "Warrior"); this.skills.refresh(); }
    // The knobs seed the simulation only when they change (other lab checkboxes call set too).
    if (!prev || prev.hp !== o.hp) this.hp = o.hp;
    if (!prev || prev.mp !== o.mp) this.mp = o.mp;
    if (!prev || prev.exp !== o.exp) this.exp = this.expShown = o.exp;
    if (!prev || prev.level !== o.level) this.level = o.level;
    const st = this.root.style;
    st.setProperty("--h", String(o.hue));
    st.setProperty("--sat", String(o.sat));
    st.setProperty("--lit", String(o.lit));
    st.setProperty("--ac", o.accent);
    st.setProperty("--fr", o.frame);
    st.setProperty("--tx", o.text);
    st.setProperty("--gl", String(o.glow));
    st.setProperty("--rad", o.radius + "px");
    this.root.style.opacity = String(o.opacity);
    this.root.classList.toggle("nokeys", !o.keys);
    this.plate.hidden = !o.backplate;
    this.menu.hidden = !o.menu;
    this.buffRow.hidden = !o.buffs;
    this.layout();
    this.render(performance.now());
  }

  private layout(): void {
    this.rig.style.transform = `translateX(-50%) scale(${this.effectiveScale()})`;
    const k = this.effectiveScale();
    Object.assign(this.wins.style, { transform: `scale(${k})`, width: this.screenW / k + "px", height: this.screenH / k + "px" });
    HudWindow.reclampAll(); // the layer changed size (resolution preview, HUD scale): keep open windows on screen
  }

  // — Build —

  private build(beltSize: number, cls: number): void {
    this.built = `${beltSize}/${cls}`;
    this.spells = [];
    this.belt = [];
    this.closeModes();
    this.statPick.close();
    this.rig.replaceChildren(this.plate);
    const skills = SKILLS[cls] ?? SKILLS[MirClass.Warrior]!;
    const perSide = beltSize / 2;
    // 10-slot belt: cells 0-7 (keys 1-8) are the player's, 8-9 (keys 9 / 0) the hero's. In the real port all eight
    // player cells are inventory belt cells (Inventory[0..7], belt grown from 6): see Knowledgebase/Future Ideas/
    // SceneLab-UI-Review/Server-Changes.md "Belt: 8 player cells".
    const beltKeys = beltSize === 10 ? ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"] : ["1", "2", "3", "4", "5", "6"];

    const wing = (side: 0 | 1) => {
      const w = h("div", "hm-wing");
      const stats = h("div", "hm-hrow stats");
      // Three stat plates per wing; a click opens the stat list to pick what the plate tracks (hudStatPick.ts).
      for (let i = 0; i < 3; i++) {
        const slot = side * 3 + i;
        const b = h("b"), v = h("span");
        const el = h("div", "hm-stat", b, v);
        el.addEventListener("click", () => this.statPick.toggle(el, w, !!side, slot, this.plates, (s) => this.plateValue(s), (key) => this.setPlate(slot, key)));
        this.plateEls[slot] = { el, b, v };
        stats.append(el);
        this.showPlate(slot);
      }
      const fill = h("div", "fill"), lag = h("div", "lag"), txt = h("div", "txt"), tag = h("div", "tag", side ? "MP" : "HP");
      const bar = h("div", "hm-bar " + (side ? "hm-mp" : "hm-hp"), h("div", "in", lag, fill), txt, tag);
      if (side) { this.mpFill = fill; this.mpLag = lag; this.mpTxt = txt; this.mpBar = bar; } else { this.hpFill = fill; this.hpLag = lag; this.hpTxt = txt; this.hpBar = bar; }

      const beltRow = h("div", "hm-hrow hm-belt" + (perSide < 5 ? " few" : ""));
      for (let i = 0; i < perSide; i++) {
        const n = side * perSide + i;
        beltRow.append(this.beltSlot(n, beltKeys[n] ?? ""));
      }

      const skillRow = h("div", "hm-hrow skills");
      for (let i = 0; i < 4; i++) {
        const n = side * 4 + i;
        skillRow.append(this.spellSlot(n, skills[n]!, n === ARMED[cls]));
      }
      const mode = this.modeSlot(side);
      if (side) skillRow.append(mode); else skillRow.prepend(mode);

      w.append(stats, bar, beltRow, skillRow);
      if (!side) w.append(this.buffRow);
      else { w.append(this.extras.debuffRow); bar.classList.toggle("psn", !!(this.extras.poisonFlags & 1)); }
      return w;
    };

    this.rig.append(wing(0), this.buildCore(), wing(1));
    this.buildMenu();
    this.buildBuffs();
    this.shown = { hp: -1, mp: -1, exp: -1, level: -1 };
    this.shownTicks = -1;
  }

  private buildCore(): HTMLElement {
    const core = h("div", "hm-core");
    this.core = core;
    const s = svg("svg", { class: "dia", viewBox: "0 0 120 130" });
    const defs = svg("defs", {});
    const fillGrad = svg("linearGradient", { id: "hudm-core", x1: 0, y1: 0, x2: 0, y2: 1 });
    fillGrad.append(svg("stop", { offset: 0, style: "stop-color: var(--pc)" }), svg("stop", { offset: 1, style: "stop-color: var(--pb)" }));
    const metal = svg("linearGradient", { id: "hudm-metal", x1: 0, y1: 0, x2: 0, y2: 1 });
    metal.append(svg("stop", { offset: 0, style: "stop-color: var(--frl)" }), svg("stop", { offset: 0.45, style: "stop-color: var(--fr)" }),
      svg("stop", { offset: 0.55, style: "stop-color: var(--frd)" }), svg("stop", { offset: 1, style: "stop-color: var(--frl)" }));
    const glow = svg("filter", { id: "hudm-glow", x: "-50%", y: "-50%", width: "200%", height: "200%" });
    glow.append(svg("feGaussianBlur", { stdDeviation: 2.5, result: "b" }));
    const merge = svg("feMerge", {});
    merge.append(svg("feMergeNode", { in: "b" }), svg("feMergeNode", { in: "SourceGraphic" }));
    glow.append(merge);
    defs.append(fillGrad, metal, glow);

    // Side fins at the left / right points.
    const fin = (pts: string) => svg("polygon", { points: pts, fill: "url(#hudm-metal)", stroke: "#000", "stroke-width": 1 });
    // Corner buttons: Log out (left) and Exit game (right), icons from the user's sketch; mock only, nothing is bound.
    const tri = (points: string, iconAt: string, d: string, title: string) => {
      const g = svg("g", { class: "tri" });
      const t = svg("title", {});
      t.textContent = title;
      g.append(t, svg("polygon", { points, fill: "url(#hudm-core)", stroke: "url(#hudm-metal)" }), svg("path", { d, transform: `${iconAt} scale(.82)` }));
      g.addEventListener("click", () => restart(g, "flash"));
      return g;
    };
    const parts: SVGElement[] = [
      tri("0,74 0,130 52,130", "translate(3 106.3)", I.back, "Log out"),
      tri("120,74 120,130 68,130", "translate(97.3 106.3)", I.cross, "Exit game"),
      fin("12,57 -6,65 12,73"), fin("108,57 126,65 108,73"),
      svg("path", { d: D_OUT, fill: "none", stroke: "url(#hudm-metal)", "stroke-width": 2.5 }),
      svg("path", { d: D, fill: "url(#hudm-core)", stroke: "#000", "stroke-width": 10, "stroke-linejoin": "miter" }),
      svg("path", { d: D, fill: "none", style: "stroke: color-mix(in srgb, var(--fr) 70%, #000)", "stroke-width": 6 }), // the EXP groove
      svg("path", { d: D, fill: "none", stroke: "rgba(0,0,0,.6)", "stroke-width": 2 }),
    ];
    this.expPath = svg("path", { d: D, fill: "none", style: "stroke: var(--ac)", "stroke-width": 6, pathLength: 100, "stroke-dasharray": "0 100", filter: "url(#hudm-glow)" });
    this.expHead = svg("circle", { r: 3.2, style: "fill: color-mix(in srgb, var(--ac) 30%, #fff)", filter: "url(#hudm-glow)" });
    parts.push(this.expPath, this.expHead);
    // Quarter marks at the side and bottom vertices (25 / 50 / 75 %); they light once the ring passes them.
    this.ticks = VERTS.map(([, x, y]) => svg("path", { d: `M${x} ${y - 4}L${x + 4} ${y}L${x} ${y + 4}L${x - 4} ${y}Z`, style: "fill: var(--fr)", stroke: "#000", "stroke-width": 1 }));
    parts.push(...this.ticks);
    parts.push(svg("path", { d: D_IN, fill: "none", style: "stroke: color-mix(in srgb, var(--frl) 40%, transparent)", "stroke-width": 1 }));
    parts.push(svg("path", { d: "M60 -6L65 2L60 9L55 2Z", fill: "url(#hudm-metal)", stroke: "#000", "stroke-width": 1 }));
    parts.push(svg("path", { class: "burst", d: D, fill: "none", style: "stroke: color-mix(in srgb, var(--ac) 50%, #fff)", "stroke-width": 3 }));
    parts.push(svg("path", { class: "burst b2", d: D, fill: "none", style: "stroke: var(--ac)", "stroke-width": 2 }));
    s.append(defs, ...parts);

    this.lvNum = h("b");
    this.lvPct = h("span");
    const hit = h("div", "hit");
    hit.title = "EXP: the ring fills clockwise from the top";
    core.append(s, hit, h("div", "lv", h("small", "", "LEVEL"), this.lvNum, this.lvPct), this.menu);
    return core;
  }

  private buildMenu(): void {
    this.menu.replaceChildren();
    for (const [name, key, d] of MENU) {
      const b = h("div", "hm-mbtn", icon(d));
      b.title = `${name} (${key})`;
      const win = { Bag: this.inventory, Character: this.character, Skills: this.skills, Quests: this.quests, Guild: this.guild, Friends: this.friends,
        Mail: this.mail, Group: this.group, Shop: this.gameShop, "Big map": this.bigMap, Options: this.options }[name];
      b.addEventListener("click", () => { restart(b, "flash"); win?.toggle(); });
      if (name === "Mail") {
        // Unread mail count on the button (MailMock.onUnread).
        const badge = h("span", "badge");
        b.append(badge);
        this.mail.onUnread = (n) => { badge.textContent = n > 9 ? "9+" : String(n); badge.hidden = n === 0; };
        this.mail.onUnread(this.mail.unread());
      }
      this.menu.append(b);
    }
    const gm = h("div", "hm-mbtn gm", icon(I.menu));
    gm.title = "Game menu (Esc)";
    gm.addEventListener("click", () => { restart(gm, "flash"); this.gameMenu.toggle(); });
    this.menu.append(gm);
  }

  private buildBuffs(): void {
    this.buffRow.replaceChildren();
    this.buffs = [];
    const now = performance.now();
    for (const [type, len] of BUFFS) {
      const i = h("i"), t = h("span");
      const el = h("div", "hm-buff", i, t);
      el.title = (BUFF_NAMES[type] ?? "Buff").replace(/([a-z])([A-Z])/g, "$1 $2");
      const [lib, index] = buffIcon(type);
      void libPicture(lib, index).then((pic) => { if (pic) i.style.backgroundImage = `url(${pic.url})`; });
      this.buffs.push({ el, t, type, len, ends: len ? now + len * 1000 : 0 });
      this.buffRow.append(el);
    }
  }

  private slotShell(key: string, name: string, library: string, index: number): { el: HTMLElement; ico: HTMLElement; cd: SVGPathElement; cdt: HTMLElement } {
    const el = h("div", "hm-slot");
    el.title = name;
    const ico = h("div", "ico");
    if (index >= 0) void libPicture(library, index).then((pic) => { if (pic) ico.style.backgroundImage = `url(${pic.url})`; });
    const cdSvg = svg("svg", { class: "cd", viewBox: "0 0 44 44", preserveAspectRatio: "none" });
    const cd = svg("path", { d: "", fill: "rgba(0,0,0,.68)" });
    cdSvg.append(cd);
    const cdt = h("div", "cdt");
    el.append(h("div", "halo"), ico, h("div", "tint"), cdSvg, h("div", "glint"), cdt, h("div", "key", key));
    return { el, ico, cd, cdt };
  }

  private spellSlot(n: number, [name, iconIndex, cost, cooldown]: [string, number, number, number], armed: boolean): HTMLElement {
    const { el, ico, cd, cdt } = this.slotShell(`F${n + 1}`, `${name}${cost ? ` (${cost} MP)` : ""}\nClick: cast. Right-click: toggle armed.`, Lib.MagIcon2, iconIndex * 2);
    const sp: Spell = { key: name, ico, el, cd, cdt, until: 0, len: cooldown, cost, active: false };
    el.classList.toggle("armed", armed);
    el.addEventListener("click", () => this.cast(sp));
    el.addEventListener("contextmenu", () => el.classList.toggle("armed"));
    this.spells.push(sp);
    return el;
  }

  /** The Skills window's F1-F8: put a spell on a bar slot (a spell already on another slot swaps with it, like the
   *  desktop clearing the key from any other spell). */
  private assignSkill(slot: number, e: SkillEntry): void {
    const sp = this.spells[slot];
    if (!sp) return;
    const other = this.spells.findIndex((x) => x.key === e.key);
    if (other === slot) return;
    const show = (t: Spell, key: string, name: string, icon: number, cost: number, len: number) => {
      t.key = key; t.cost = cost; t.len = len; t.until = 0;
      t.el.title = `${name}${cost ? ` (${cost} MP)` : ""}\nClick: cast. Right-click: toggle armed.`;
      t.ico.style.backgroundImage = "";
      void libPicture(Lib.MagIcon2, icon * 2).then((pic) => { if (pic && t.key === key) t.ico.style.backgroundImage = `url(${pic.url})`; });
    };
    if (other >= 0) {
      const o = this.spells[other]!;
      [o.key, o.ico.style.backgroundImage, o.el.title, o.cost, o.len] = [sp.key, sp.ico.style.backgroundImage, sp.el.title, sp.cost, sp.len];
      o.until = 0;
    }
    show(sp, e.key, e.name, e.icon, e.cost, e.delay ? e.delay / 1000 : 1.2);
    restart(sp.el, "flash");
  }

  private beltSlot(n: number, key: string): HTMLElement {
    const { el, ico, cd, cdt } = this.slotShell(key, "", Lib.Items, -1);
    const cnt = h("div", "cnt");
    el.append(cnt);
    const b: BeltSlot = { el, ico, cnt, until: 0, cd, cdt, active: false };
    el.addEventListener("click", () => this.useBelt(n));
    if (n >= PLAYER_BELT) { bindCell(el, "herobelt", n - PLAYER_BELT); el.classList.add("hero"); el.append(h("div", "hb", "H")); }
    else bindCell(el, "belt", n);
    this.belt.push(b);
    this.showBelt(n);
    return el;
  }

  /** Player belt cells shown: 6, or 8 on the 10-slot belt (its last two cells are the hero's). */
  private playerCells(): number { return Math.min(this.belt.length, PLAYER_BELT); }
  /** Belt cell n's store and index: the player's belt, or (cells 8-9 of the 10-slot belt) the hero's. */
  private cellOf(n: number): [(Stack | null)[], number] { return n >= PLAYER_BELT ? [heroBelt, n - PLAYER_BELT] : [belt, n]; }
  /** A hero is out and alive (read from the hero frame, hudHero.ts: hidden while resting, .fallen when dead). */
  private heroUp(): boolean { const f = this.hero.frame; return !f.hidden && !f.classList.contains("fallen"); }

  /** Draws belt cell n from hudItems.ts `belt` / `heroBelt` (icon, count, name; empty when there's nothing in it). */
  private showBelt(n: number): void {
    const b = this.belt[n], [store, si] = this.cellOf(n), st = store[si];
    if (!b) return;
    if (store === heroBelt) {
      b.cnt.textContent = st && st.count > 1 ? String(st.count) : "";
      b.el.classList.toggle("empty", !st);
      b.el.title = st ? `${st.name}${st.count > 1 ? ` (${st.count})` : ""}\nHero belt. Click: your hero drinks it. Drag to move; right-click: back to the bag.` : "Hero belt: drag a potion here for your hero";
      if (st) b.el.dataset.item = st.name; else delete b.el.dataset.item;
      this.beltIcon(b, st);
      return;
    }
    const gear = !!st && !USE_TYPES.has(info(st.name)?.type ?? "Potion");
    b.cnt.textContent = gear ? "⇄" : st && st.count > 1 ? String(st.count) : ""; // gear: a swap mark
    b.el.classList.toggle("empty", !st);
    b.el.title = st ? `${st.name}${st.count > 1 ? ` (${st.count})` : ""}\n${gear ? "Click: wear it (what you wear now takes this slot)" : "Click: use"}. Drag to move; right-click: back to the bag.` : "Empty belt slot: drag any item here (potions to drink, gear to swap in)";
    b.el.classList.toggle("gear", gear);
    if (st) b.el.dataset.item = st.name; else delete b.el.dataset.item;
    this.beltIcon(b, st);
  }
  private beltIcon(b: BeltSlot, st: Stack | null): void {
    const want = st ? String(st.img) : "";
    if (b.ico.dataset.img === want) return;
    b.ico.dataset.img = want;
    b.ico.style.backgroundImage = "";
    if (st) void libPicture(Lib.Items, st.img).then((pic) => { if (pic && b.ico.dataset.img === want) b.ico.style.backgroundImage = `url(${pic.url})`; });
  }

  private modeSlot(side: 0 | 1): HTMLElement {
    const list = side ? PET_MODES : ATTACK_MODES;
    const el = h("div", "hm-slot mode");
    const show = () => {
      const cur = side ? this.pet : this.attack;
      const [, short, full] = list.find(([v]) => v === cur)!;
      el.replaceChildren(h("small", "", side ? "Pet" : "Mode"), h("span", "", short));
      el.title = `${side ? "Pet" : "Mode"}: ${full}\nClick for the list.`;
    };
    show();
    el.addEventListener("click", () => {
      if (this.modePop?.parentElement === el.parentElement) { this.closeModes(); return; }
      this.closeModes();
      const pop = h("div", "hm-modes " + (side ? "r" : "l"));
      for (const [v, , full] of list) {
        const row = h("div", (side ? this.pet : this.attack) === v ? "on" : "", full);
        row.addEventListener("click", () => {
          if (side) this.pet = v; else this.attack = v;
          show();
          restart(el, "flash");
          this.closeModes();
        });
        pop.append(row);
      }
      el.parentElement!.style.position = "relative";
      el.parentElement!.append(pop);
      this.modePop = pop;
    });
    return el;
  }

  private closeModes(): void {
    this.modePop?.remove();
    this.modePop = null;
  }

  // — Simulation events —

  private cast(sp: Spell): void {
    const now = performance.now();
    if (now < sp.until) return;
    if (this.mp / 100 * MP_MAX < sp.cost || !this.extras.canAct()) { restart(sp.el, "deny"); return; }
    this.lastCombat = now;
    this.mp -= sp.cost / MP_MAX * 100;
    sp.until = now + sp.len * 1000;
    restart(sp.el, "flash");
  }

  useBelt(n: number): void {
    const b = this.belt[n], [store, si] = this.cellOf(n), st = store[si];
    if (!b || !st || performance.now() < b.until || !this.extras.canAct()) { if (b) restart(b.el, "deny"); return; }
    if (store === heroBelt) { this.heroDrink(si, b, st); return; }
    if (!USE_TYPES.has(info(st.name)?.type ?? "Potion")) { this.wearFromBelt(n); return; }
    const [hpUp, mpUp] = HEAL[st.name] ?? [0, 0];
    belt[n] = st.count > 1 ? { ...st, count: st.count - 1 } : null;
    b.until = performance.now() + 1500;
    this.hp = Math.min(100, this.hp + hpUp);
    this.mp = Math.min(100, this.mp + mpUp);
    bagChanged(); // the weight and the windows showing the bag
    restart(b.el, "flash");
  }

  /** A hero belt potion (C.UseItem { Grid = HeroInventory }): the hero drinks it; refused with no hero out or a fallen
   *  one, like the server. The hero's HP lives in hudHero.ts, which has no heal hook yet, so this only uses the potion. */
  private heroDrink(i: number, b: BeltSlot, st: Stack): void {
    const f = this.hero.frame;
    if (!this.heroUp()) { const r = b.el.getBoundingClientRect(); restart(b.el, "deny"); toast(r.left, r.top, f.hidden ? "No hero summoned" : "Your hero has fallen"); return; }
    heroBelt[i] = st.count > 1 ? { ...st, count: st.count - 1 } : null;
    b.until = performance.now() + 1500;
    bagChanged();
    restart(b.el, "flash");
    this.chat.system(`Hero drinks ${st.name}.`);
  }

  /** Gear on the belt: put it on; what was worn in that slot goes into the belt cell (a quick gear swap). */
  private wearFromBelt(n: number): void {
    const b = this.belt[n]!, st = belt[n]!, from = host("belt")!, equip = host("equip")!;
    const r = b.el.getBoundingClientRect();
    const slot = cellFor(equip, st, from, true);
    // No slot: the reason from a slot of the right type (level, class, gender), else it isn't gear at all.
    const why = slot ? transfer(from, n, equip, slot[1])
      : Array.from({ length: equip.size }, (_, i) => equip.accepts(i, st, from)).find((r) => r && !r.includes(" slot: ")) ?? "That can't be worn or used";
    if (why) { restart(b.el, "deny"); toast(r.left, r.top, why); return; }
    restart(b.el, "flash");
    this.chat.system(`You put on ${st.name}${belt[n] ? `; ${belt[n]!.name} is on the belt` : ""}.`);
  }

  hit(big = false): void {
    this.lastCombat = performance.now();
    this.hp = Math.max(1, this.hp - (big ? rand(30, 45) : rand(6, 14)));
    restart(this.hpBar, "struck");
    this.extras.wear("hit", big ? 3 : 1);
    if (big) restart(this.vign, "on");
  }

  gainExp(pct = rand(4, 9)): void {
    this.exp += pct;
    this.quests.onKill(); // EXP stands for a kill: advance a tracked quest's kill objective
    this.extras.wear("kill");
    this.float(`+${pct.toFixed(2)}% EXP`, "exp");
  }

  levelUp(): void {
    this.level++;
    this.exp = this.expShown = 0;
    this.hp = this.mp = 100;
    restart(this.core, "lvup");
    this.float("LEVEL UP!", "big");
  }

  loot(): void {
    const [name, img, count] = LOOT[Math.floor(Math.random() * LOOT.length)]!;
    if (!addToBag({ name, img, count })) bagChanged();
    this.gold += Math.round(rand(800, 25_000));
  }

  /** Back to the knob values, full stacks, no cooldowns. */
  resetSim(): void {
    const o = this.opts;
    this.hp = o.hp; this.mp = o.mp; this.exp = this.expShown = o.exp; this.level = o.level;
    this.gold = 1_240_000;
    for (const b of this.belt) b.until = 0;
    resetItems();
    this.extras.reset();
    this.popups.closeAll();
    for (const s of this.spells) s.until = 0;
    this.buildBuffs();
  }

  private float(text: string, cls: string): void {
    const f = h("div", "hm-float " + cls, text);
    this.core.append(f);
    setTimeout(() => f.remove(), 1500);
  }

  private demo(now: number): void {
    if (now < this.nextDemo) return;
    this.nextDemo = now + rand(500, 1300);
    const r = Math.random();
    if (r < 0.3) this.hit(Math.random() < 0.15);
    else if (r < 0.6) { const ready = this.spells.filter((s) => now >= s.until); if (ready.length) this.cast(ready[Math.floor(Math.random() * ready.length)]!); }
    else if (r < 0.72 && this.hp < 55) this.useBelt(0);
    else if (r < 0.8 && this.mp < 40) this.useBelt(1);
    else if (r < 0.88) this.gainExp();
    else if (r < 0.9 && !this.extras.poisonFlags) this.extras.poison();
    else this.loot();
    this.mp = Math.min(100, this.mp + 1.5); // regen
  }

  // — Per frame —

  tick(now: number): void {
    if (this.root.hidden || !this.opts) return;
    if (this.opts.demo) this.demo(now);
    this.extras.tick(now);
    const sweep = (s: { el: HTMLElement; cd: SVGPathElement; cdt: HTMLElement; until: number; active: boolean }, len: number) => {
      const left = (s.until - now) / 1000;
      if (left <= 0) {
        if (s.active) { s.active = false; s.cd.setAttribute("d", ""); setText(s.cdt, ""); restart(s.el, "ready"); }
        return;
      }
      s.active = true;
      s.cd.setAttribute("d", piePath(left / len));
      setText(s.cdt, left >= 1 ? String(Math.ceil(left)) : left.toFixed(1));
    };
    for (const s of this.spells) {
      sweep(s, s.len);
      s.el.classList.toggle("nomp", this.mp / 100 * MP_MAX < s.cost);
    }
    for (const b of this.belt) sweep(b, 1.5);
    if (this.belt.length > PLAYER_BELT) { const off = !this.heroUp(); for (let i = PLAYER_BELT; i < this.belt.length; i++) this.belt[i]!.el.classList.toggle("off", off); }
    for (const b of this.buffs) {
      if (!b.ends) { setText(b.t, "∞"); continue; }
      let left = (b.ends - now) / 1000;
      if (left <= 0) { b.ends = now + b.len * 1000; left = b.len; } // the mock re-casts it
      setText(b.t, fmtTime(left));
      b.el.classList.toggle("ending", left < 10);
    }
    // EXP eases towards its target; a full ring levels up.
    if (this.expShown < this.exp) this.expShown = Math.min(this.exp, this.expShown + Math.max(0.05, (this.exp - this.expShown) * 0.08));
    if (this.expShown >= 100) { const over = this.exp - 100; this.levelUp(); this.exp = this.expShown = 0; if (over > 0) this.exp = over; }
    this.render(now);
  }

  private render(now: number): void {
    const hp = Math.round(this.hp * 10) / 10, mp = Math.round(this.mp * 10) / 10, exp = Math.round(this.expShown * 100) / 100;
    if (hp !== this.shown.hp) {
      this.shown.hp = hp;
      this.hpFill.style.width = hp + "%";
      this.hpLag.style.width = hp + "%";
      this.hpTxt.textContent = `${Math.round(HP_MAX * hp / 100)} / ${HP_MAX}`;
      this.hpBar.classList.toggle("low", hp < 30);
    }
    if (mp !== this.shown.mp) {
      this.shown.mp = mp;
      this.mpFill.style.width = mp + "%";
      this.mpLag.style.width = mp + "%";
      this.mpTxt.textContent = `${Math.round(MP_MAX * mp / 100)} / ${MP_MAX}`;
    }
    if (exp !== this.shown.exp) {
      this.shown.exp = exp;
      this.expPath.setAttribute("stroke-dasharray", `${exp} ${100 - exp}`);
      const len = this.expPath.getTotalLength();
      const p = this.expPath.getPointAtLength(len * exp / 100);
      this.expHead.setAttribute("cx", String(p.x));
      this.expHead.setAttribute("cy", String(p.y));
      this.expHead.style.display = exp > 0.2 ? "" : "none";
      this.lvPct.textContent = exp.toFixed(2) + "%";
      const lit = VERTS.reduce((n, [at]) => n + (exp >= at ? 1 : 0), 0);
      if (lit !== this.shownTicks) { this.shownTicks = lit; VERTS.forEach(([at], i) => this.ticks[i]!.style.fill = exp >= at ? "var(--ac)" : "var(--fr)"); }
    }
    if (this.level !== this.shown.level) {
      this.shown.level = this.level;
      this.lvNum.textContent = String(this.level);
      setMe(this.level, ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][this.opts.cls] ?? "Warrior");
      this.character.setProfile(this.level, this.opts.cls);
      this.mentor.setLevel(this.level);
      this.tooltip.setProfile(this.level, ["Warrior", "Wizard", "Taoist", "Assassin", "Archer"][this.opts.cls] ?? "Warrior");
      this.relationship.setLevel(this.level);
      this.skills.refresh();
    }
    // Bag count and weight walk the bag (and look items up by name): only on a bag change, plus a 4 Hz catch-up for
    // code paths that change the bag without calling bagChanged().
    if (this.bagDirty || now >= this.bagAt) {
      this.bagDirty = false; this.bagAt = now + 250;
      this.bagUsed = bag.filter(Boolean).length; this.bagKg = bagWeight();
    }
    const used = this.bagUsed, weight = this.bagKg;
    const live = this.liveText;
    live.Bag = `${used}/${BAG_SLOTS}`;
    live.Weight = `${weight}/${WEIGHT_MAX}`;
    live.Gold = fmtGold(this.gold);
    this.inventory.setGold(this.gold);
    for (let slot = 0; slot < this.plateEls.length; slot++) {
      const key = this.plates[slot]!, p = this.plateEls[slot];
      if (!p || !(key in live)) continue;
      setText(p.v, live[key]!);
      if (key === "Weight") {
        p.el.classList.toggle("warn", weight >= WEIGHT_MAX * 0.8 && weight < WEIGHT_MAX * 0.95);
        p.el.classList.toggle("full", weight >= WEIGHT_MAX * 0.95);
      }
    }
  }

  // — Stat plates —

  /** A stat's value as the plate (and the stat list) shows it. */
  private plateValue(s: PlateStat): string { return s.live ? this.liveText[s.key] ?? "" : s.value; }

  /** Draws plate `slot` for the stat it tracks (live values are kept up to date by the frame update). */
  private showPlate(slot: number): void {
    const p = this.plateEls[slot];
    if (!p) return;
    const s = plateStat(this.plates[slot]!);
    p.b.textContent = s.short;
    p.v.textContent = this.plateValue(s);
    p.el.title = `${s.name} · click to track another stat`;
    p.el.classList.remove("warn", "full");
  }

  /** Plate `slot` tracks `key`; a plate already tracking it takes this plate's old stat (a swap). */
  private setPlate(slot: number, key: string): void {
    const other = this.plates.indexOf(key);
    if (other === slot) return;
    if (other >= 0) { this.plates[other] = this.plates[slot]!; this.showPlate(other); }
    this.plates[slot] = key;
    this.showPlate(slot);
    savePlates(this.plates);
  }
}
