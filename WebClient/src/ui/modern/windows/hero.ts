// Ported from play/hudHero.ts (HeroMock): the new UI's hero window, a modern take on the desktop's hero dialogs
// (HeroDialogs.cs: HeroManageDialog, HeroInventoryDialog, HeroBeltDialog, HeroDialog, HeroBehaviourPanel; the classic
// ports are src/ui/heroManageWindow.ts, heroInventoryWindow.ts, heroCharacterWindow.ts, heroPanels.ts), wired to the
// real models: Hero (src/game/hero.ts), HeroItems (heroItems.ts), HeroStats (heroStats.ts) and Magic's hero side.
// Changes from the mock:
//  - the roster is the server's manage data only (S.ManageHeroes from the NPC hero-manager page, S.NewHeroInfo,
//    S.ChangeHero): the current hero and the stored heroes up to MaximumHeroCount - 1, no locked-slot counts of its
//    own. "Make active" asks in a popup and sends C.ChangeHero { ListIndex = index + 1 } (allowed while summoned: the
//    server re-summons); the roster waits for the S.ChangeHero answer. Before any manage data it says where to go;
//  - a "New hero" panel only while the server has sent S.HeroCreateRequest (classes from CanCreateClass): the name
//    popup sends C.NewHero, the S.NewHero result shows in a popup (the window's `watch`, which also opens the window on
//    S.ManageHeroes / S.HeroCreateRequest like the desktop's dialogs);
//  - Summon / Dismiss = the classic HeroSummonButton (C.Chat "@SUMMONHERO", the server toggles), locked until
//    S.UpdateHeroSpawnState answers. No summon cooldown countdown (the server sends none), no Revive button (the NPC
//    script command REVIVEHERO: a fallen hero shows "Speak to an NPC"), no activity feed (no source);
//  - the paperdoll is character.ts's Paperdoll + dollLook with the hero's equipment; the 14 equipment cells, the
//    hero belt (inventory 0-1) and the bag (inventory 2.., cells past Inventory.Length shown locked) are core item
//    cells on the hero grids ("heroEq" / "heroInv"), so pick / drag / drop / right-click (TransferHeroItem,
//    TakeBackHeroItem, EquipItem, RemoveItem, MergeItem, UseItem) go through drag.ts + dragRoutes.ts and the cell stays
//    locked until the server answers; weight and SoulBound refusals are the server's;
//  - HP / MP max, the stats list and the bag weight come from HeroStats (UserHeroObject.RefreshStats); EXP / level
//    from S.HeroInformation / GainHeroExperience / HeroLevelChanged; the hero's buffs (AddBuff etc. with its ObjectID);
//  - behaviour cards send C.SetHeroBehaviour (only while the hero is out: the server ignores it otherwise) and show
//    the new mode when S.SetHeroBehaviour answers;
//  - auto potion: Crystal has locked / unlocked (S.UnlockHeroAutoPot), not an on / off switch, so the mock's switch is a
//    lock badge. Unlocked: the HP / MP potion cells ("heroHP" / "heroMP": drop a hero potion on them, click to clear;
//    C.SetAutoPotItem) and 0-99 % thresholds (C.SetAutoPotValue on release, shown pending until S.SetAutoPotValue);
//  - the Skills tab lists the hero's learned spells (no "learns at level" rows: unlearned spells are never sent) with
//    their key and auto-cast state; keys and auto-cast are set in the Skills window's Hero tab (one editor);
//  - no mock data, no fight simulation, no 1 s re-render: model events mark the window dirty (rendered while open,
//    else on the next open); a 1 s ticker while shown follows HeroStats' own refresh and the buff timers. Every write
//    is diffed (setText / keyed rebuilds).
// Hotkeys: the registry's hero binds (HeroInventory / HeroEquipment / HeroSkills, modernUi.ts EXTRA_BINDS).

import type { ClientHeroInformation } from "../../../protocol/server.ts";
import { ClientPackets } from "../../../protocol/client.ts";
import {
  BuffTypeName, EquipmentSlot, HeroBehaviour, HeroBehaviourName, MirClass, MirGender, MirGridType, Stat,
} from "../../../protocol/enums.generated.ts";
import { HERO_BELT_IDX, NEW_HERO_TEXT } from "../../../game/hero.ts";
import { buffIcon, buffText, type UserBuff } from "../../../game/buffs.ts";
import { HERO_KEY_OFFSET, isPassiveSpell, type UserMagic } from "../../../game/magic.ts";
import { KeybindOptions, keyLabel, onKeyBindsChange, type KeybindOption } from "../../../game/keybinds.ts";
import { itemFriendlyName } from "../../../game/gameInfo.ts";
import { Lib, libPicture, libPictureNow } from "../../mir/controls.ts";
import { h, injectCss, later, setStyle, setText } from "../core/dom.ts";
import { svg, type IconName } from "../core/icons.ts";
import { className, fmt, fmtShort, pretty } from "../core/format.ts";
import { classVar } from "../core/styles.ts";
import { button, segmented, tabs, type Btn, type Choice } from "../core/widgets.ts";
import { UiWindow } from "../core/wm.ts";
import { potionsOnly, type ItemCell } from "../core/itemCells.ts";
import { shortKey } from "../hud/belt.ts";
import { Paperdoll, dollLook } from "./character.ts";
import { registerWindow, type ModernUiHost } from "../windowRegistry.ts";

const E = EquipmentSlot;
/** [slot, slot name, empty-slot icon] (the character window's layout). */
type SlotDef = readonly [number, string, IconName];
const LEFT: readonly SlotDef[] = [
  [E.Weapon, "Weapon", "sword"], [E.Armour, "Armour", "shield"], [E.Torch, "Torch", "sun"],
  [E.BraceletL, "Bracelet", "ring"], [E.RingL, "Ring", "ring"],
];
const RIGHT: readonly SlotDef[] = [
  [E.Helmet, "Helmet", "cap"], [E.Necklace, "Necklace", "gem"], [E.Amulet, "Amulet", "tag"],
  [E.BraceletR, "Bracelet", "ring"], [E.RingR, "Ring", "ring"],
];
const BOTTOM: readonly SlotDef[] = [
  [E.Belt, "Belt", "pouch"], [E.Boots, "Boots", "walk"], [E.Stone, "Stone", "gem"], [E.Mount, "Mount", "horse"],
];

/** [behaviour, label, description, icon path (constant)]. */
const BEHAVIOURS: readonly (readonly [number, string, string, string])[] = [
  [HeroBehaviour.Attack, "Attack", "Fights any monster near you.", "M5 19L19 5M14 5h5v5M5 14l5 5M3 21l3-3"],
  [HeroBehaviour.CounterAttack, "Counter", "Only fights what attacks you or your hero.", "M12 3l8 3v6c0 5-4 8-8 9-4-1-8-4-8-9V6zM9 12l2 2 4-4"],
  [HeroBehaviour.Follow, "Follow", "Stays at your side and never fights.", "M7 20l3-8-3-3 4-5M14 20l2-6 3-2M15 4a2 2 0 110 4 2 2 0 010-4"],
  [HeroBehaviour.Custom, "Custom", "Casts only the skills that have auto-cast on.", "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M14 4v4M8 10v4M16 16v4"],
];
const behaviourLabel = (b: number): string => BEHAVIOURS.find((x) => x[0] === b)?.[1] ?? pretty(HeroBehaviourName[b] ?? "?");

/** The hero bag page (HeroInventoryDialog: 8 x 5 cells after the 2 belt cells). */
const BAG_CELLS = 40;
/** How long a request waits for its answer before the control unlocks again (a refused request never answers). */
const WAIT_MS = 2500;

/** The Stats tab (CharacterDialog STATS rows, hero values); null = a section break. */
type StatRow = readonly [string, (s: (k: number) => number, hp: number, mp: number, w: { bag: number; wear: number; hand: number }) => string];
const STAT_ROWS: readonly (StatRow | null)[] = [
  ["AC", (s) => `${s(Stat.MinAC)}-${s(Stat.MaxAC)}`],
  ["MAC", (s) => `${s(Stat.MinMAC)}-${s(Stat.MaxMAC)}`],
  ["DC", (s) => `${s(Stat.MinDC)}-${s(Stat.MaxDC)}`],
  ["MC", (s) => `${s(Stat.MinMC)}-${s(Stat.MaxMC)}`],
  ["SC", (s) => `${s(Stat.MinSC)}-${s(Stat.MaxSC)}`],
  ["HP", (s, hp) => `${fmt(hp)}/${fmt(s(Stat.HP))}`],
  ["MP", (s, _hp, mp) => `${fmt(mp)}/${fmt(s(Stat.MP))}`],
  null,
  ["Accuracy", (s) => `+${s(Stat.Accuracy)}`],
  ["Agility", (s) => `+${s(Stat.Agility)}`],
  ["Attack Speed", (s) => `${s(Stat.AttackSpeed)}`],
  ["Luck", (s) => `${s(Stat.Luck)}`],
  ["Critical Rate", (s) => `${s(Stat.CriticalRate)}%`],
  ["Critical Damage", (s) => `${s(Stat.CriticalDamage)}`],
  ["Magic Resist", (s) => `+${s(Stat.MagicResist)}`],
  ["Poison Resist", (s) => `+${s(Stat.PoisonResist)}`],
  ["Health Recovery", (s) => `+${s(Stat.HealthRecovery)}`],
  ["Mana Recovery", (s) => `+${s(Stat.SpellRecovery)}`],
  ["Poison Recovery", (s) => `+${s(Stat.PoisonRecovery)}`],
  ["Holy", (s) => `+${s(Stat.Holy)}`],
  null,
  ["Bag Weight", (s, _h, _m, w) => `${w.bag}/${s(Stat.BagWeight)}`],
  ["Wear Weight", (s, _h, _m, w) => `${w.wear}/${s(Stat.WearWeight)}`],
  ["Hand Weight", (s, _h, _m, w) => `${w.hand}/${s(Stat.HandWeight)}`],
];

const CSS = `
.hudm .hrwin { width: 900px; }
.hudm .hrwin .panel { height: 584px; box-sizing: border-box; display: flex; flex-direction: column; }
.hudm .hrwin .hbody { flex: 1; min-height: 0; display: flex; }
.hudm .hrwin .lbl { display: flex; justify-content: space-between; align-items: center; gap: 6px; margin: 2px 0 4px; }
.hudm .hrwin .lbl b { letter-spacing: 0; color: var(--tx); font-weight: 600; text-transform: none; }
.hudm .hrwin .hint2 { font-size: 10px; line-height: 1.35; color: var(--dim); }
.hudm .hrwin .note { font-size: 11px; line-height: 1.4; color: var(--dim); padding: 6px 8px; border-radius: var(--rad);
  border: 1px dashed color-mix(in srgb, var(--fr) 70%, transparent); }
/* Roster */
.hudm .hrwin .roster { width: 200px; flex: none; box-sizing: border-box; padding: 10px; display: flex; flex-direction: column; gap: 6px; overflow-y: auto;
  scrollbar-width: thin; scrollbar-color: var(--fr) transparent; border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hrwin .hc { position: relative; display: flex; align-items: center; gap: 8px; padding: 6px; border-radius: var(--rad); pointer-events: auto;
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); font: inherit; color: inherit; text-align: left; }
.hudm .hrwin button.hc { cursor: pointer; width: 100%; }
.hudm .hrwin button.hc:hover, .hudm .hrwin button.hc:focus-visible { border-color: color-mix(in srgb, var(--ac) 55%, transparent); }
.hudm .hrwin .hc.act { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 12%, transparent); box-shadow: inset 3px 0 0 var(--ac); }
.hudm .hrwin .hc.wait { opacity: .55; cursor: progress; }
.hudm .hrwin .av { position: relative; width: 40px; height: 40px; flex: none; border-radius: var(--rad); overflow: hidden; border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000;
  background: radial-gradient(circle at 50% 35%, var(--pc), var(--pb) 75%); }
.hudm .hrwin .av i { position: absolute; inset: 0; background: center / cover no-repeat; image-rendering: pixelated; }
.hudm .hrwin .av u { position: absolute; left: 0; right: 0; bottom: 0; height: 3px; text-decoration: none; background: var(--cc, var(--ac)); }
.hudm .hrwin .hc .t { flex: 1; min-width: 0; }
.hudm .hrwin .hc .t b { display: block; font-size: 12px; color: #fff; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .hrwin .hc .t small { font-size: 10px; color: var(--dim); }
.hudm .hrwin .hc.emp { justify-content: center; min-height: 40px; color: var(--dim); font-size: 11px; border-style: dashed; }
.hudm .hrwin .chip { flex: none; font-size: 8px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; padding: 1px 6px; border-radius: 6px; color: var(--tx);
  border: 1px solid var(--fr); background: var(--pb); }
.hudm .hrwin .chip.Summoned { color: #fff; border-color: var(--ac); background: color-mix(in srgb, var(--ac) 45%, var(--pb)); }
.hudm .hrwin .chip.Fallen { color: #fff; border-color: var(--bad); background: color-mix(in srgb, var(--bad) 40%, var(--pb)); }
.hudm .hrwin .newh { display: flex; flex-direction: column; gap: 6px; padding: 8px; border-radius: var(--rad); border: 1px solid var(--gold);
  background: color-mix(in srgb, var(--gold) 8%, transparent); }
.hudm .hrwin .newh .ui-seg > button { padding: 0 4px; font-size: 10px; }
.hudm .hrwin .newh .acts { display: flex; gap: 5px; } .hudm .hrwin .newh .acts .btn { flex: 1; }
/* Centre */
.hudm .hrwin .mid { width: 350px; flex: none; box-sizing: border-box; display: flex; flex-direction: column; padding: 10px 12px; gap: 7px;
  border-right: 1px solid color-mix(in srgb, var(--fr) 60%, transparent); }
.hudm .hrwin .head { display: flex; align-items: center; gap: 10px; min-height: 40px; }
.hudm .hrwin .head .nm { flex: 1; min-width: 0; }
.hudm .hrwin .head h3 { margin: 0; font-size: 18px; color: #fff; letter-spacing: .02em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hudm .hrwin .head small { font-size: 11px; color: var(--dim); }
.hudm .hrwin .head small em { font-style: normal; font-weight: 700; color: var(--cc, var(--ac)); }
.hudm .hrwin .head .chip { font-size: 10px; padding: 3px 9px; border-radius: 9px; }
.hudm .hrwin .buffs { display: flex; flex-wrap: wrap; gap: 3px; min-height: 0; }
.hudm .hrwin .buffs:empty { display: none; }
.hudm .hrwin .bf { position: relative; width: 22px; text-align: center; pointer-events: auto; }
.hudm .hrwin .bf i { display: block; width: 22px; height: 22px; background: center / contain no-repeat; image-rendering: pixelated; border-radius: calc(var(--rad) * .7);
  border: 1px solid var(--fr); box-shadow: 0 0 0 1px #000; }
.hudm .hrwin .bf span { display: block; font-size: 8px; font-weight: 600; color: var(--tx); text-shadow: 0 0 2px #000; }
.hudm .hrwin .dollrow { position: relative; height: 228px; display: flex; justify-content: space-between; }
.hudm .hrwin .dollrow .col { display: flex; flex-direction: column; justify-content: space-between; z-index: 1; }
.hudm .hrwin .figure { position: absolute; left: 50px; right: 50px; top: 0; bottom: 0; overflow: hidden; border-radius: var(--rad); pointer-events: none;
  background: radial-gradient(ellipse 60% 45% at 50% 55%, color-mix(in srgb, var(--ac) 14%, transparent), transparent 70%), radial-gradient(ellipse 45% 7% at 50% 93%, rgba(0,0,0,.6), transparent); }
.hudm .hrwin .figure .org { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
.hudm .hrwin .figure .org div { position: absolute; background-repeat: no-repeat; image-rendering: pixelated; }
.hudm .hrwin .figure .org div.wing { mix-blend-mode: plus-lighter; }
.hudm .hrwin .figure.fallen { opacity: .45; }
.hudm .hrwin .figure .ghost { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: 12px; letter-spacing: .2em; color: var(--bad); text-shadow: 0 0 6px #000; }
.hudm .hrwin .bottom { display: flex; justify-content: center; gap: 6px; }
.hudm .hrwin .bars { display: grid; gap: 4px; }
.hudm .hrwin .bar3 { position: relative; height: 15px; border-radius: 4px; background: #04060a; border: 1px solid var(--fr); overflow: hidden; }
.hudm .hrwin .bar3 i { position: absolute; inset: 0; transform-origin: 0 50%; transform: scaleX(0); transition: transform .4s; }
.hudm .hrwin .bar3 span { position: absolute; inset: 0; display: flex; justify-content: space-between; align-items: center; padding: 0 6px; font-size: 10px; font-weight: 700;
  color: #fff; text-shadow: 0 1px 2px #000; font-variant-numeric: tabular-nums; }
.hudm .hrwin .bar3 span em { font-style: normal; } .hudm .hrwin .bar3 span b { font-weight: 700; }
.hudm .hrwin .bar3.hp i { background: linear-gradient(180deg, #e04848, #8a1616); } .hudm .hrwin .bar3.mp i { background: linear-gradient(180deg, #4a8cff, #16388a); }
.hudm .hrwin .bar3.xp i { background: linear-gradient(90deg, color-mix(in srgb, var(--ac) 50%, #000), var(--ac)); }
.hudm .hrwin .acts2 { display: flex; gap: 6px; } .hudm .hrwin .acts2 .btn { flex: 1; }
.hudm .hrwin .mid.none > :not(.nohero) { display: none; }
.hudm .hrwin .mid .nohero { display: none; } .hudm .hrwin .mid.none .nohero { display: block; }
/* Right */
.hudm .hrwin .side { flex: 1; min-width: 0; display: flex; flex-direction: column; }
.hudm .hrwin .side .ui-tabs { padding: 8px 10px 0; }
.hudm .hrwin .pane { flex: 1; min-height: 0; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 9px; scrollbar-width: thin; scrollbar-color: var(--fr) transparent; }
.hudm .hrwin .pane[hidden] { display: none; }
.hudm .hrwin .need[hidden] { display: none; }
.hudm .hrwin .bhv { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }
.hudm .hrwin .bh { display: flex; gap: 8px; padding: 7px; border-radius: var(--rad); cursor: pointer; pointer-events: auto; text-align: left; font: inherit; color: inherit;
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .hrwin .bh:hover, .hudm .hrwin .bh:focus-visible { border-color: color-mix(in srgb, var(--ac) 55%, transparent); }
.hudm .hrwin .bh.on { border-color: var(--ac); background: color-mix(in srgb, var(--ac) 14%, transparent); }
.hudm .hrwin .bh.wait { cursor: progress; border-style: dashed; }
.hudm .hrwin .bh.dis { cursor: not-allowed; opacity: .5; }
.hudm .hrwin .bh svg { width: 22px; height: 22px; flex: none; fill: none; stroke: var(--dim); stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
.hudm .hrwin .bh.on svg { stroke: var(--ac); }
.hudm .hrwin .bh b { display: block; font-size: 12px; color: #fff; } .hudm .hrwin .bh small { font-size: 10px; line-height: 1.3; color: var(--dim); }
.hudm .hrwin .lockb { display: inline-flex; align-items: center; gap: 4px; font-size: 10px; letter-spacing: .04em; text-transform: none; color: var(--dim); }
.hudm .hrwin .lockb .ic { width: 12px; height: 12px; }
.hudm .hrwin .lockb.on { color: var(--ac); }
.hudm .hrwin .pot { display: flex; align-items: center; gap: 8px; padding: 6px; border-radius: var(--rad); background: color-mix(in srgb, var(--frl) 5%, transparent);
  border: 1px solid color-mix(in srgb, var(--fr) 55%, transparent); }
.hudm .hrwin .pot .mid2 { flex: 1; min-width: 0; font-size: 11px; } .hudm .hrwin .pot .mid2 b { color: #fff; }
.hudm .hrwin .pot .mid2 > div { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--dim); }
.hudm .hrwin .pot input[type=range] { width: 100%; accent-color: var(--ac); margin: 4px 0 0; pointer-events: auto; }
.hudm .hrwin .pot .pc { width: 40px; text-align: right; font-size: 14px; font-weight: 700; color: var(--ac); font-variant-numeric: tabular-nums; }
.hudm .hrwin .pot .pc.wait { opacity: .55; }
.hudm .hrwin .pots.off .pot { opacity: .45; }
.hudm .hrwin .pots.off input { pointer-events: none; }
.hudm .hrwin .belt2 { display: flex; gap: 6px; align-items: center; }
.hudm .hrwin .grid { display: grid; grid-template-columns: repeat(8, 36px); gap: 3px; justify-content: center; }
.hudm .hrwin .lk { width: 36px; height: 36px; box-sizing: border-box; display: flex; align-items: center; justify-content: center; border-radius: var(--rad);
  border: 1px dashed var(--fr); background: color-mix(in srgb, var(--pb) 60%, #000); color: var(--dim); }
.hudm .hrwin .lk .ic { width: 12px; height: 12px; opacity: .5; }
.hudm .hrwin .lk[hidden], .hudm .hrwin .grid .icell[hidden] { display: none; }
.hudm .hrwin .wt { display: flex; align-items: center; gap: 8px; font-size: 11px; color: var(--dim); }
.hudm .hrwin .wt b { color: var(--tx); font-weight: 600; font-variant-numeric: tabular-nums; }
.hudm .hrwin .wt .bar3 { flex: 1; height: 7px; } .hudm .hrwin .wt .bar3 i { background: var(--ac); }
.hudm .hrwin .sk { display: flex; align-items: center; gap: 8px; padding: 4px 6px; border-radius: var(--rad); }
.hudm .hrwin .sk:nth-child(odd) { background: color-mix(in srgb, var(--frl) 5%, transparent); }
.hudm .hrwin .sk .ic2 { width: 30px; height: 30px; flex: none; border-radius: calc(var(--rad) * .8); border: 1px solid var(--fr); background: #000 center / auto no-repeat;
  image-rendering: pixelated; box-shadow: 0 0 0 1px #000; }
.hudm .hrwin .sk .t { flex: 1; min-width: 0; font-size: 12px; color: #fff; } .hudm .hrwin .sk .t small { display: block; font-size: 10px; color: var(--dim); }
.hudm .hrwin .sk .kb { font-size: 10px; font-weight: 700; color: var(--ac); white-space: nowrap; }
.hudm .hrwin .sk .badge { font-size: 9px; font-weight: 700; padding: 0 5px; border-radius: 6px; border: 1px solid var(--fr); color: var(--dim); }
.hudm .hrwin .sk .badge.on { color: var(--ac); border-color: var(--ac); }
.hudm .hrwin .st { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; }
.hudm .hrwin .st div { display: flex; justify-content: space-between; gap: 6px; padding: 5px 8px; border-radius: var(--rad); font-size: 11px; color: var(--dim);
  background: color-mix(in srgb, var(--frl) 5%, transparent); border: 1px solid color-mix(in srgb, var(--fr) 45%, transparent); }
.hudm .hrwin .st b { color: #fff; font-weight: 600; font-variant-numeric: tabular-nums; white-space: nowrap; }
.hudm .hrwin .st .gap { grid-column: 1 / -1; height: 2px; padding: 0; border: 0; background: none; }
/* Narrow layer (1024 px): slimmer columns. */
.hudm.narrow .hrwin { width: 820px; }
.hudm.narrow .hrwin .roster { width: 172px; }
.hudm.narrow .hrwin .mid { width: 324px; padding: 10px; }
.hudm.narrow .hrwin .grid { grid-template-columns: repeat(8, 33px); }
.hudm.narrow .hrwin .grid .icell, .hudm.narrow .hrwin .lk { --cs: 33px; width: 33px; height: 33px; }
.hudm.narrow .hrwin .bhv { grid-template-columns: 1fr; }
@media (prefers-reduced-motion: reduce) { .hudm .hrwin .bar3 i { transition: none; } }
`;

/** GameScene.HeroAvatar: Prguse 1400 + class + 10 x gender (+370 for HeroManageAvatar's larger picture). */
const avatarIndex = (cls: number, gender: number, big: boolean): number => 1400 + cls + 10 * gender + (big ? 370 : 0);
const frac = (v: number, max: number): number => (max > 0 ? Math.max(0, Math.min(1, v / max)) : 0);
/** A picture as an element's background (cached pictures at once). */
function picInto(el: HTMLElement, lib: string, index: number, alive: () => boolean): void {
  const now = libPictureNow(lib, index);
  if (now) { el.style.backgroundImage = `url(${now.url})`; return; }
  void libPicture(lib, index).then((p) => { if (p && alive()) el.style.backgroundImage = `url(${p.url})`; });
}
/** The binding label of a hero magic key (17-24 = HeroSkill1-8), or the desktop default. */
function heroKeyText(key: number): string {
  const n = key - HERO_KEY_OFFSET;
  if (n < 1 || n > 8) return "";
  return keyLabel((KeybindOptions.HeroSkill1 + n - 1) as KeybindOption) || `Shift + F${n}`;
}

/** One pot row (HP or MP). */
interface PotRow { el: HTMLElement; cell: ItemCell; name: HTMLElement; left: HTMLElement; range: HTMLInputElement; pc: HTMLElement; title: HTMLElement; shown: number }

export class HeroWindow extends UiWindow {
  private readonly ui: ModernUiHost;
  // Roster
  private readonly roster = h("div", "roster");
  private rosterKey = "";
  /** C.ChangeHero sent, waiting for S.ChangeHero (the roster is locked meanwhile). */
  private changing = false;
  private cancelChange: (() => void) | null = null;
  /** The S.HeroCreateRequest whose panel was closed with Cancel (it stays hidden until a new request). */
  private dismissedReq: boolean[] | null = null;
  private createCls = -1;
  private createGender: number = MirGender.Male;
  private creating = false;
  // Centre
  private readonly mid = h("div", "mid");
  private readonly nameEl = h("h3");
  private readonly lineEm = h("em");
  private readonly lineRest = h("span");
  private readonly stateChip = h("span", "chip");
  private readonly buffRow = h("div", "buffs");
  private buffKey = "";
  private buffList: UserBuff[] = [];
  private buffEls: { el: HTMLElement; t: HTMLElement }[] = [];
  private readonly doll = new Paperdoll();
  private readonly ghost = h("div", "ghost", "FALLEN");
  private readonly eqHints = new Map<number, string>();
  private readonly eqCells = new Map<number, ItemCell>();
  private readonly slotNames = new Map<number, string>();
  private readonly hpFill = h("i");
  private readonly mpFill = h("i");
  private readonly xpFill = h("i");
  private readonly hpText = h("b");
  private readonly mpText = h("b");
  private readonly xpText = h("b");
  private readonly xpBar: HTMLElement;
  private readonly summonBtn: Btn;
  private readonly midNote = h("div", "hint2");
  private readonly noHero = h("div", "nohero note");
  /** The spawn state when @SUMMONHERO went out (locked until it changes), -1 = none pending. */
  private summonFrom = -1;
  private cancelSummon: (() => void) | null = null;
  private lastCls = -1;
  // Side
  private readonly tabBar: ReturnType<typeof tabs>;
  private tab = 0;
  private readonly panes: HTMLElement[] = [];
  /** Per pane: the "summon first" note and the content shown with a live hero. */
  private readonly needs: { note: HTMLElement; body: HTMLElement }[] = [];
  private readonly bhvBtns: HTMLButtonElement[] = [];
  private readonly bhvLabel = h("b");
  private askedBehaviour = -1;
  private cancelBehaviour: (() => void) | null = null;
  private readonly lockBadge = h("span", "lockb");
  private lockOn: boolean | null = null;
  private readonly pots = h("div", "pots");
  private readonly potRows: PotRow[] = [];
  /** Threshold sent per row (HP 0 / MP 1) waiting for S.SetAutoPotValue, -1 = none. */
  private readonly potPending = [-1, -1];
  private readonly potCancel: ((() => void) | null)[] = [null, null];
  private readonly beltCells: ItemCell[] = [];
  private readonly beltKbds: (HTMLElement | null)[] = [];
  private readonly bagCells: ItemCell[] = [];
  private readonly bagLocks: HTMLElement[] = [];
  private readonly bagCount = h("b");
  private readonly wtText = h("b");
  private readonly wtFill = h("i");
  private readonly skillList = h("div", "");
  private readonly skillCount = h("b");
  private skillKey = "";
  private readonly statVals: HTMLElement[] = [];

  constructor(ui: ModernUiHost) {
    super(ui.rt, "hrwin", 40, 50);
    this.ui = ui;
    injectCss("win-hero", CSS);
    const m = ui.ctx.models;

    // — Centre: head, buffs, doll + equipment, bars, summon —
    const head = h("div", "head", h("div", "nm", this.nameEl, h("small", "", this.lineEm, this.lineRest)), this.stateChip);
    const cell = (d: SlotDef): HTMLElement => {
      const c = ui.cells.cell("heroEq", d[0], { size: 40, empty: svg(d[2]) });
      this.eqCells.set(d[0], c);
      this.slotNames.set(d[0], d[1]);
      return c.el;
    };
    const col = (defs: readonly SlotDef[]) => h("div", "col", ...defs.map(cell));
    this.doll.el.append(this.ghost);
    this.ghost.hidden = true;
    const dollRow = h("div", "dollrow", col(LEFT), this.doll.el, col(RIGHT));
    const bottom = h("div", "bottom", ...BOTTOM.map(cell));
    const bar = (cls: string, fill: HTMLElement, text: HTMLElement, label: string) => h("div", "bar3 " + cls, fill, h("span", "", h("em", "", label), text));
    // (text is a <b>: the bar's own <span> is the absolutely placed label row)
    this.xpBar = bar("xp", this.xpFill, this.xpText, "EXP");
    const bars = h("div", "bars", bar("hp", this.hpFill, this.hpText, "HP"), bar("mp", this.mpFill, this.mpText, "MP"), this.xpBar);
    this.summonBtn = button("Summon", () => this.summon(), { cls: "main big", title: "Summon or dismiss your hero" });
    this.mid.append(head, this.buffRow, dollRow, bottom, bars, h("div", "acts2", this.summonBtn.el), this.midNote, this.noHero);
    this.listen(this.buffRow, "pointerover", (e) => this.buffTitle(e.target));

    // — Side: Tactics / Bag / Skills / Stats —
    const names = ["TACTICS", "BAG", "SKILLS", "STATS"];
    this.tabBar = tabs(names, 0, (i) => this.selectTab(i));
    const side = h("div", "side", this.tabBar.el);
    for (let i = 0; i < names.length; i++) {
      const p = h("div", "pane");
      p.hidden = i !== 0;
      this.panes.push(p);
      side.append(p);
    }
    this.buildTactics(this.panes[0]!);
    this.buildBag(this.panes[1]!);
    this.buildSkills(this.panes[2]!);
    this.buildStats(this.panes[3]!);

    this.el.append(h("div", "panel", this.titleBar("HERO", "Close"), h("div", "hbody", this.roster, this.mid, side)));
    this.el.addEventListener("contextmenu", (e) => e.preventDefault(), { signal: this.signal });

    // Model events only mark the window dirty (rendered on the next frame while open, else on the next open).
    const off = [
      m.hero.onChange(() => this.onHero()),
      m.inventory.subscribe(() => this.invalidate()), // cell locks (HeroItems locks through Inventory)
      m.magic.onHeroChange(() => this.invalidate()),
      ui.events.on((e) => { if (e.kind === "itemInfo") this.invalidate(); }),
      onKeyBindsChange(() => { this.skillKey = ""; this.relabelBelt(); this.invalidate(); }),
      // The answers that unlock the roster: S.ChangeHero (after C.ChangeHero) and S.NewHero (after C.NewHero; the
      // window's watch shows the result box).
      ui.packets.on((p) => {
        if (p.type === "ChangeHero" && this.changing) this.endChange();
        else if (p.type === "NewHero" && this.creating) { this.creating = false; this.rosterKey = ""; this.invalidate(); }
      }),
    ];
    this.signal.addEventListener("abort", () => {
      for (const f of off) f();
      this.cancelSummon?.(); this.cancelBehaviour?.(); this.cancelChange?.();
      for (const c of this.potCancel) c?.();
    }, { once: true });
    // HeroStats refreshes itself at most once a second (item infos / magic levels arriving later) and the buff timers
    // count down: follow both while shown (every write is diffed).
    this.everyShown(1000, () => this.invalidate());
  }

  protected override shown(): void { this.doll.reset(); this.invalidate(); }

  /** Hero model changes: answers to pending requests, then a redraw. */
  private onHero(): void {
    const hero = this.ui.ctx.models.hero;
    if (this.summonFrom >= 0 && hero.spawnState !== this.summonFrom) this.endSummon();
    if (this.askedBehaviour >= 0 && hero.behaviour === this.askedBehaviour) this.endBehaviour();
    const req = hero.createRequest;
    if (this.creating && !req) this.creating = false;
    if (this.dismissedReq && !req) this.dismissedReq = null;
    this.invalidate();
  }

  // — Tabs —

  private selectTab(i: number): void {
    this.tab = i;
    this.panes.forEach((p, k) => { p.hidden = k !== i; });
    this.panes[i]!.scrollTop = 0;
    this.invalidate();
  }

  /** A pane section shown only with a live hero (S.HeroInformation), with a note otherwise. */
  private need(p: HTMLElement, ...kids: HTMLElement[]): HTMLElement {
    const note = h("div", "need note");
    const body = h("div", "need", ...kids);
    body.style.display = "contents";
    this.needs.push({ note, body });
    p.append(note, body);
    return body;
  }

  private buildTactics(p: HTMLElement): void {
    const grid = h("div", "bhv");
    for (const [value, label, text, icon] of BEHAVIOURS) {
      const b = h("button", "bh", svg(icon, ""), h("div", "", h("b", "", label), h("small", "", text)));
      b.type = "button";
      b.addEventListener("click", () => this.setBehaviour(value), { signal: this.signal });
      this.bhvBtns.push(b);
      grid.append(b);
    }
    p.append(h("div", "lbl", "Behaviour", this.bhvLabel), grid);

    // Auto potion (HeroInventoryDialog: HP / MP item cells and percent buttons, lock bars until UnlockHeroAutoPot)
    const rows: HTMLElement[] = [];
    for (const [k, stat, grid2] of [[0, "HP", "heroHP"], [1, "MP", "heroMP"]] as const) {
      const c = this.ui.cells.cell(grid2, 0, { size: 40, empty: svg("drop") });
      c.el.title = `Drop a ${stat} potion from the hero's bag here · click to clear`;
      const range = h("input");
      range.type = "range"; range.min = "0"; range.max = "99"; range.step = "1"; range.value = "0";
      range.setAttribute("aria-label", `Use a ${stat} potion below this percent`);
      const pc = h("div", "pc", "0%");
      const title = h("b"), name = h("span"), left = h("span");
      const row: PotRow = { el: h("div", "pot", c.el, h("div", "mid2", title, h("div", "", name, left), range), pc), cell: c, name, left, range, pc, title, shown: -1 };
      range.addEventListener("input", () => { setText(pc, range.value + "%"); setText(title, `${stat} below ${range.value}%`); }, { signal: this.signal });
      range.addEventListener("change", () => this.sendThreshold(k, Number(range.value)), { signal: this.signal });
      this.potRows.push(row);
      rows.push(row.el);
    }
    this.pots.append(...rows);
    const potHint = h("div", "hint2", "The hero drinks the chosen potion from its own bag when HP or MP drops below the line.");
    // Hero belt (inventory 0-1; keys HeroBelt1 / HeroBelt2)
    const belt = h("div", "belt2");
    const opts = [KeybindOptions.HeroBelt1, KeybindOptions.HeroBelt2] as const;
    for (let i = 0; i < HERO_BELT_IDX; i++) {
      const c = this.ui.cells.cell("heroInv", i, { size: 40, filter: potionsOnly, key: shortKey(keyLabel(opts[i] ?? KeybindOptions.HeroBelt1)) || " " });
      c.el.title = "Hero belt: potions only";
      this.beltCells.push(c);
      this.beltKbds.push(c.el.querySelector<HTMLElement>(":scope > kbd"));
      belt.append(c.el);
    }
    belt.append(h("span", "hint2", "The hero belt's keys use these potions."));
    this.need(p, h("div", "lbl", "Auto potion", this.lockBadge), this.pots, potHint, h("div", "lbl", "Hero belt"), belt);
  }

  private relabelBelt(): void {
    const opts = [KeybindOptions.HeroBelt1, KeybindOptions.HeroBelt2] as const;
    this.beltKbds.forEach((k, i) => { if (k) setText(k, shortKey(keyLabel(opts[i] ?? KeybindOptions.HeroBelt1)) || " "); });
  }

  private buildBag(p: HTMLElement): void {
    const grid = h("div", "grid");
    for (let i = 0; i < BAG_CELLS; i++) {
      const c = this.ui.cells.cell("heroInv", HERO_BELT_IDX + i, { size: 36 });
      const lk = h("div", "lk", svg("lock"));
      lk.title = "Locked";
      lk.hidden = true;
      this.bagCells.push(c);
      this.bagLocks.push(lk);
      grid.append(c.el, lk);
    }
    const wbar = h("div", "bar3", this.wtFill);
    this.need(p, h("div", "lbl", "Hero bag", this.bagCount), grid, h("div", "wt", "Weight", this.wtText, wbar),
      h("div", "hint2", "Drag items between your bag and the hero's. Weight and bound items are checked by the server."));
  }

  private buildSkills(p: HTMLElement): void {
    const open = button("Set keys and auto-cast", () => { this.ui.openWindow("skills", true); }, { icon: "book", title: "Opens the Skills window; switch to its Hero tab" });
    this.need(p, h("div", "lbl", "Hero skills", this.skillCount), this.skillList, open.el,
      h("div", "hint2", "Keys and auto-cast are set on the Hero tab of the Skills window."));
  }

  private buildStats(p: HTMLElement): void {
    const g = h("div", "st");
    for (const row of STAT_ROWS) {
      if (!row) { g.append(h("div", "gap")); continue; }
      const v = h("b");
      this.statVals.push(v);
      g.append(h("div", "", h("span", "", row[0]), v));
    }
    this.need(p, g);
  }

  // — Requests —

  /** MainDialog.HeroSummonButton: C.Chat "@SUMMONHERO" (the server summons or dismisses); locked until the spawn state
   *  changes (a refusal, e.g. the dismiss countdown or a no-hero map, only prints a chat line). */
  private summon(): void {
    const hero = this.ui.ctx.models.hero;
    if (!hero.hasHero || this.summonFrom >= 0) return;
    this.summonFrom = hero.spawnState;
    this.ui.ctx.send(ClientPackets.Chat({ Message: "@SUMMONHERO", LinkedItems: [] }));
    this.cancelSummon = later(() => this.endSummon(), WAIT_MS, this.signal);
    this.invalidate();
  }
  private endSummon(): void {
    this.summonFrom = -1;
    this.cancelSummon?.(); this.cancelSummon = null;
    this.invalidate();
  }

  /** HeroBehaviourPanel: C.SetHeroBehaviour (the server ignores it unless the hero is out); shown when it answers. */
  private setBehaviour(b: number): void {
    const hero = this.ui.ctx.models.hero;
    if (!hero.summoned || hero.dead || !hero.info || hero.behaviour === b || this.askedBehaviour >= 0) return;
    this.askedBehaviour = b;
    this.ui.ctx.send(ClientPackets.SetHeroBehaviour({ Behaviour: b as HeroBehaviour }));
    this.cancelBehaviour = later(() => this.endBehaviour(), WAIT_MS, this.signal);
    this.invalidate();
  }
  private endBehaviour(): void {
    this.askedBehaviour = -1;
    this.cancelBehaviour?.(); this.cancelBehaviour = null;
    this.invalidate();
  }

  /** HeroInventoryDialog HP / MP button: C.SetAutoPotValue (0-99); shown pending until S.SetAutoPotValue. */
  private sendThreshold(k: 0 | 1, value: number): void {
    const info = this.ui.ctx.models.hero.info;
    if (!info?.autoPot) return;
    const v = Math.min(99, Math.max(0, Math.trunc(value)));
    if (v === (k === 0 ? info.autoHPPercent : info.autoMPPercent)) { this.potPending[k] = -1; this.invalidate(); return; }
    this.potPending[k] = v;
    this.ui.ctx.models.heroItems.setAutoPotValue(k === 0 ? Stat.HP : Stat.MP, v);
    this.potCancel[k]?.();
    this.potCancel[k] = later(() => { this.potPending[k] = -1; this.potCancel[k] = null; this.invalidate(); }, WAIT_MS, this.signal);
    this.invalidate();
  }

  /** HeroManageAvatar click: ClientTextKeys.MakeActiveHero, then C.ChangeHero { ListIndex = index + 1 }. */
  private askChange(index: number, info: ClientHeroInformation): void {
    if (this.changing) return;
    void this.ui.rt.popups.ask(`Would you like to make ${info.Name} your active Hero?`, { title: "Hero", kind: "question" }).then((yes) => {
      if (!yes || this.changing || this.signal.aborted) return;
      this.changing = true;
      this.ui.ctx.send(ClientPackets.ChangeHero({ ListIndex: index + 1 }));
      this.cancelChange = later(() => this.endChange(), WAIT_MS, this.signal);
      this.rosterKey = "";
      this.invalidate();
    });
  }
  private endChange(): void {
    this.changing = false;
    this.cancelChange?.(); this.cancelChange = null;
    this.rosterKey = "";
    this.invalidate();
  }

  /** NewHeroDialog OK: the name popup, then C.NewHero (the result arrives as S.NewHero: the window's watch shows it). */
  private create(req: boolean[]): void {
    if (this.creating || this.createCls < 0 || !req[this.createCls]) return;
    const cls = this.createCls, gender = this.createGender;
    void this.ui.rt.popups.input({
      title: "New hero", text: `Name your ${className(cls)} hero.`, placeholder: "Hero name", min: 3, max: 15, chars: "A-Za-z0-9",
      check: (v) => v.length < 3 ? "At least 3 letters or digits." : "",
    }).then((name) => {
      if (name === null || this.signal.aborted || this.ui.ctx.models.hero.createRequest !== req) return;
      this.creating = true;
      this.ui.ctx.send(ClientPackets.NewHero({ Name: name, Gender: gender, Class: cls }));
      // Envir.CanCreateHero refusals send no S.NewHero: unlock after a while
      later(() => { if (this.creating) { this.creating = false; this.rosterKey = ""; this.invalidate(); } }, WAIT_MS * 2, this.signal);
      this.rosterKey = "";
      this.invalidate();
    });
  }

  // — Render —

  protected override render(): void {
    const ctx = this.ui.ctx, m = ctx.models, hero = m.hero, x = hero.info;
    this.renderRoster();

    // Centre
    const known = x ? { name: x.name, level: x.level, cls: x.cls, gender: x.gender } : hero.currentHero
      ? { name: hero.currentHero.Name, level: hero.currentHero.Level, cls: hero.currentHero.Class, gender: hero.currentHero.Gender } : null;
    const none = !hero.hasHero && !x;
    this.mid.classList.toggle("none", none);
    setText(this.noHero, hero.createRequest ? "You have no hero yet. Create one in the New hero panel."
      : "You have no hero. Speak to the hero manager to create one, or use a Sealed Hero from your bag.");
    const state = hero.dead ? "Fallen" : hero.summoned ? "Summoned" : "Resting";
    setText(this.nameEl, known?.name ?? "Your hero");
    setText(this.lineEm, known ? `Level ${known.level} ${className(known.cls)}` : "");
    setText(this.lineRest, known ? ` · ${known.gender === MirGender.Female ? "Female" : "Male"}` : "Summon your hero to see it here.");
    const cls = known?.cls ?? -1;
    if (cls !== this.lastCls) { this.lastCls = cls; if (cls >= 0) this.mid.style.setProperty("--cc", classVar(cls)); else this.mid.style.removeProperty("--cc"); }
    setText(this.stateChip, state);
    if (this.stateChip.className !== "chip " + state) this.stateChip.className = "chip " + state;

    const stats = m.heroStats.stats();
    const maxHp = stats?.stats[Stat.HP] ?? 0, maxMp = stats?.stats[Stat.MP] ?? 0;
    setStyle(this.hpFill, "transform", `scaleX(${frac(x?.hp ?? 0, maxHp).toFixed(3)})`);
    setStyle(this.mpFill, "transform", `scaleX(${frac(x?.mp ?? 0, maxMp).toFixed(3)})`);
    setText(this.hpText, x ? `${fmt(x.hp)} / ${fmt(maxHp)}` : "—");
    setText(this.mpText, x ? `${fmt(x.mp)} / ${fmt(maxMp)}` : "—");
    const xp = x && x.maxExperience > 0n ? Number((x.experience * 10000n) / x.maxExperience) / 10000 : 0;
    setStyle(this.xpFill, "transform", `scaleX(${Math.max(0, Math.min(1, xp)).toFixed(4)})`);
    setText(this.xpText, x ? `${(xp * 100).toFixed(2)}%` : "—");
    const xpTitle = x ? `EXP ${fmt(x.experience)} / ${fmt(x.maxExperience)}` : "";
    if (this.xpBar.title !== xpTitle) this.xpBar.title = xpTitle;

    // Summon / Dismiss (a fallen hero can still be dismissed; reviving is an NPC's job)
    this.summonBtn.setLabel(hero.summoned ? "Dismiss" : "Summon");
    this.summonBtn.setDisabled(!hero.hasHero || this.summonFrom >= 0, this.summonFrom >= 0 ? "Waiting for the server" : "You have no hero");
    setText(this.midNote, hero.dead ? "Your hero has fallen. Speak to an NPC to revive it."
      : !hero.summoned ? "Summon your hero to see its gear, bag, skills and stats." : x ? "" : "Waiting for your hero's details…");

    // Doll + equipment hints
    this.doll.el.hidden = !x;
    this.doll.el.classList.toggle("fallen", hero.dead);
    this.ghost.hidden = !hero.dead;
    if (x) {
      this.doll.set(dollLook({ equipment: x.equipment, infos: m.itemInfos, level: x.level, cls: x.cls, gender: x.gender, hair: x.hair, wingEffect: stats?.wingEffect ?? 0 }));
    }
    for (const [slot, c] of this.eqCells) {
      const hint = x && !x.equipment[slot] ? `${this.slotNames.get(slot) ?? ""} (empty)` : "";
      if (this.eqHints.get(slot) !== hint) { this.eqHints.set(slot, hint); if (hint) c.el.title = hint; else c.el.removeAttribute("title"); }
    }

    this.drawBuffs(x ? hero.buffs : [], ctx.now());

    // Side panes: a live hero (HeroInformation) or the note
    const noteText = hero.dead ? "Your hero has fallen. Speak to an NPC to revive it." : !hero.hasHero ? "You have no hero."
      : "Summon your hero to see this.";
    for (const n of this.needs) {
      n.note.hidden = !!x;
      n.body.hidden = !x;
      n.body.style.display = x ? "contents" : "none";
      if (!x) setText(n.note, noteText);
    }
    this.renderTactics();
    if (!x) return;
    if (this.tab === 1) this.renderBag();
    else if (this.tab === 2) this.renderSkills();
    else if (this.tab === 3) this.renderStats();
  }

  private renderRoster(): void {
    const hero = this.ui.ctx.models.hero, x = hero.info;
    const req = hero.createRequest && hero.createRequest !== this.dismissedReq ? hero.createRequest : null;
    const cur = hero.currentHero;
    const key = JSON.stringify([cur, hero.storage, hero.maximumCount, x ? [x.name, x.level, x.cls, x.gender] : null, hero.hasHero, hero.dead,
      hero.summoned, this.changing, req, this.createCls, this.createGender, this.creating]);
    if (key === this.rosterKey) return;
    this.rosterKey = key;
    const r = this.roster;
    r.replaceChildren();
    const alive = () => !this.signal.aborted;
    const card = (name: string, level: number, cls: number, gender: number, chip: string, act: boolean, onClick: (() => void) | null): HTMLElement => {
      const pic = h("i");
      picInto(pic, Lib.Prguse, avatarIndex(cls, gender, true), alive);
      const av = h("div", "av", pic, h("u"));
      av.style.setProperty("--cc", classVar(cls));
      const kids = [av, h("div", "t", h("b", "", name), h("small", "", `Lv ${level} ${className(cls)}`)), h("span", "chip " + chip, chip)];
      if (!onClick) return h("div", "hc" + (act ? " act" : ""), ...kids);
      const b = h("button", "hc" + (this.changing ? " wait" : ""), ...kids);
      b.type = "button";
      b.title = this.changing ? "Waiting for the server" : `Make ${name} your active hero`;
      b.addEventListener("click", onClick);
      return b;
    };

    // The active hero: the manage data's current hero, else the summoned hero's details.
    r.append(h("div", "lbl", "Active hero"));
    const chip = hero.dead ? "Fallen" : hero.summoned ? "Summoned" : "Resting";
    if (cur) r.append(card(cur.Name, x?.name === cur.Name ? x.level : cur.Level, cur.Class, cur.Gender, chip, true, null));
    else if (x) r.append(card(x.name, x.level, x.cls, x.gender, chip, true, null));
    else r.append(h("div", "hc emp", hero.hasHero ? "Summon your hero to see it" : "No hero"));

    // Stored heroes (HeroManageDialog: storage slots up to MaximumHeroCount - 1)
    const slots = Math.max(0, hero.maximumCount - 1);
    if (hero.maximumCount > 0) {
      let used = 0;
      for (let i = 0; i < slots; i++) if (hero.storage[i]) used++;
      r.append(h("div", "lbl", "Stored heroes", h("b", "", `${used} / ${slots}`)));
      for (let i = 0; i < slots; i++) {
        const s = hero.storage[i] ?? null;
        if (s) r.append(card(s.Name, s.Level, s.Class, s.Gender, "Stored", false, () => this.askChange(i, s)));
        else {
          const e = h("div", "hc emp", "Empty slot");
          e.title = "Use a Sealed Hero from your bag to fill this slot";
          r.append(e);
        }
      }
      if (!slots) r.append(h("div", "hint2", "You have no slots for stored heroes."));
    } else {
      r.append(h("div", "lbl", "Stored heroes"), h("div", "note", "Speak to the hero manager to see and switch your stored heroes."));
    }

    // New hero (only while the server's S.HeroCreateRequest stands)
    if (req) {
      const classes: Choice<number>[] = [];
      for (const c of [MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin, MirClass.Archer]) {
        if (req[c]) classes.push({ value: c, label: className(c).slice(0, 4), title: className(c) });
      }
      if (!classes.some((c) => c.value === this.createCls)) this.createCls = classes[0]?.value ?? -1;
      const clsSeg = segmented(classes, this.createCls, (v) => { this.createCls = v; this.rosterKey = ""; this.invalidate(); });
      const genders: Choice<number>[] = [{ value: MirGender.Male, label: "Male" }, { value: MirGender.Female, label: "Female" }];
      const genSeg = segmented(genders, this.createGender, (v) => { this.createGender = v; });
      const ok = button(this.creating ? "Creating…" : "Create hero", () => this.create(req), { cls: "main" });
      ok.setDisabled(this.creating || this.createCls < 0, this.creating ? "Waiting for the server" : "No class can be created");
      const cancel = button("Cancel", () => { this.dismissedReq = req; this.rosterKey = ""; this.invalidate(); });
      r.append(h("div", "newh", h("div", "lbl", "New hero"), clsSeg.el, genSeg.el, h("div", "acts", ok.el, cancel.el)));
    }
  }

  private drawBuffs(list: readonly UserBuff[], now: number): void {
    let key = "";
    for (const b of list) key += `${b.Type}:${b.Infinite ? 1 : 0}:${b.Paused ? 1 : 0}:${b.expire}|`;
    if (key !== this.buffKey) {
      this.buffKey = key;
      this.buffList = list.slice();
      this.buffEls = [];
      this.buffRow.replaceChildren();
      const alive = () => !this.signal.aborted;
      for (const b of this.buffList) {
        const i = h("i"), t = h("span");
        const el = h("div", "bf", i, t);
        el.setAttribute("aria-label", pretty(BuffTypeName[b.Type] ?? "Buff"));
        const [lib, index] = buffIcon(b.Type);
        picInto(i, lib, index, alive);
        this.buffEls.push({ el, t });
        this.buffRow.append(el);
      }
    }
    for (let k = 0; k < this.buffList.length; k++) {
      const b = this.buffList[k]!, e = this.buffEls[k]!;
      setText(e.t, b.Infinite ? "∞" : b.Paused ? "II" : fmtShort((b.expire - now) / 1000));
    }
  }

  private buffTitle(target: EventTarget | null): void {
    const el = target instanceof Element ? target.closest(".bf") : null;
    const k = el ? this.buffEls.findIndex((x) => x.el === el) : -1;
    const b = this.buffList[k];
    if (el instanceof HTMLElement && b) el.title = buffText(b, BuffTypeName[b.Type] ?? `Buff ${b.Type}`, this.ui.ctx.now());
  }

  private renderTactics(): void {
    const m = this.ui.ctx.models, hero = m.hero, x = hero.info;
    const canSet = hero.summoned && !hero.dead && !!x;
    setText(this.bhvLabel, behaviourLabel(hero.behaviour));
    this.bhvBtns.forEach((b, i) => {
      const value = BEHAVIOURS[i]![0];
      b.classList.toggle("on", hero.behaviour === value);
      b.classList.toggle("wait", this.askedBehaviour === value);
      b.classList.toggle("dis", !canSet);
      const title = !canSet ? "Summon your hero to change its behaviour" : this.askedBehaviour >= 0 ? "Waiting for the server" : "";
      if (b.title !== title) b.title = title;
      b.setAttribute("aria-pressed", String(hero.behaviour === value));
    });
    if (!x) return;

    // Auto potion: a lock badge (locked until S.UnlockHeroAutoPot), the potion cells and thresholds once unlocked.
    const unlocked = x.autoPot;
    if (unlocked !== this.lockOn) {
      this.lockOn = unlocked;
      this.lockBadge.replaceChildren(svg(unlocked ? "unlock" : "lock"), unlocked ? "Unlocked" : "Locked");
      this.lockBadge.classList.toggle("on", unlocked);
      this.lockBadge.title = unlocked ? "Auto potion is unlocked for this hero" : "Auto potion is locked: an item from the server unlocks it";
      this.pots.classList.toggle("off", !unlocked);
      for (const r of this.potRows) r.range.disabled = !unlocked;
    }
    this.potRows.forEach((r, k) => {
      const stat = k === 0 ? "HP" : "MP";
      const server = k === 0 ? x.autoHPPercent : x.autoMPPercent;
      if (this.potPending[k] === server) { this.potPending[k] = -1; this.potCancel[k]?.(); this.potCancel[k] = null; }
      const pending = this.potPending[k]!;
      const v = pending >= 0 ? pending : server;
      if (r.shown !== v) {
        r.shown = v;
        r.range.value = String(v);
        setText(r.pc, v + "%");
        setText(r.title, `${stat} below ${v}%`);
      }
      r.pc.classList.toggle("wait", pending >= 0);
      const item = m.heroItems.autoPotItem(k === 0 ? MirGridType.HeroHPItem : MirGridType.HeroMPItem);
      const info = item ? m.itemInfos.get(item.ItemIndex) : undefined;
      setText(r.name, !unlocked ? "Locked" : item ? (info ? itemFriendlyName(info) : "…") : `No ${stat} potion chosen`);
      setText(r.left, unlocked && item ? ` · ${fmt(m.heroItems.countItem(item.ItemIndex))} left` : "");
    });
  }

  private renderBag(): void {
    const m = this.ui.ctx.models, x = m.hero.info!;
    const len = x.inventory.length;
    let used = 0;
    for (let i = 0; i < BAG_CELLS; i++) {
      const open = HERO_BELT_IDX + i < len;
      const c = this.bagCells[i]!, lk = this.bagLocks[i]!;
      if (c.el.hidden === open) c.el.hidden = !open;
      if (lk.hidden !== open) lk.hidden = open;
      if (open && x.inventory[HERO_BELT_IDX + i]) used++;
    }
    setText(this.bagCount, `${used} / ${Math.max(0, Math.min(BAG_CELLS, len - HERO_BELT_IDX))}`);
    const st = m.heroStats.stats();
    const cur = st?.currentBagWeight ?? 0, max = st?.stats[Stat.BagWeight] ?? 0;
    setText(this.wtText, `${fmt(cur)} / ${fmt(max)}`);
    setStyle(this.wtFill, "transform", `scaleX(${frac(cur, max).toFixed(3)})`);
  }

  private renderSkills(): void {
    const magic = this.ui.ctx.models.magic;
    const list = magic.heroMagics;
    let key = "";
    for (const s of list) key += `${s.Spell}:${s.Level}:${s.Key}:${magic.heroAutoCast(s.Spell) ? 1 : 0}|`;
    if (key === this.skillKey) return;
    this.skillKey = key;
    setText(this.skillCount, String(list.length));
    this.skillList.replaceChildren();
    if (!list.length) { this.skillList.append(h("div", "empty-note", "Your hero has not learned any skills yet.")); return; }
    const alive = () => !this.signal.aborted;
    for (const s of list as readonly UserMagic[]) {
      const ic = h("div", "ic2");
      picInto(ic, Lib.MagIcon2, s.Icon * 2, alive);
      const passive = isPassiveSpell(s.Spell);
      const auto = !passive && magic.heroAutoCast(s.Spell);
      const keyText = heroKeyText(s.Key);
      const row = h("div", "sk", ic, h("div", "t", s.Name, h("small", "", `Level ${s.Level}${passive ? " · passive" : ""}`)),
        keyText ? h("span", "kb", keyText) : null,
        passive ? null : h("span", "badge" + (auto ? " on" : ""), auto ? "Auto" : "Manual"));
      row.title = passive ? "Passive skill" : auto ? "The hero casts this on its own" : "Auto-cast is off for this skill";
      this.skillList.append(row);
    }
  }

  private renderStats(): void {
    const m = this.ui.ctx.models, x = m.hero.info!, st = m.heroStats.stats();
    const s = (k: number) => st?.stats[k] ?? 0;
    const w = { bag: st?.currentBagWeight ?? 0, wear: st?.currentWearWeight ?? 0, hand: st?.currentHandWeight ?? 0 };
    let i = 0;
    for (const row of STAT_ROWS) if (row) setText(this.statVals[i++]!, row[1](s, x.hp, x.mp, w));
  }
}

registerWindow("hero", (host) => new HeroWindow(host), {
  // HeroManageDialog.Show on S.ManageHeroes, NewHeroDialog on S.HeroCreateRequest, and the S.NewHero result boxes
  // (GameScene.ManageHeroes / HeroCreateRequest / NewHero): these run while the window is not built yet.
  watch: (host, open) => {
    const hero = host.ctx.models.hero;
    let shownFor: boolean[] | null = null;
    return hero.onChange(() => {
      const result = hero.newHeroResult;
      if (result !== null) {
        hero.newHeroResult = null;
        const text = NEW_HERO_TEXT[result];
        if (text) void host.rt.popups.confirm({ title: "New hero", text, kind: result === 10 ? "info" : "warning", buttons: "ok" });
      }
      if (hero.openManage) { hero.openManage = false; open(true); }
      const req = hero.createRequest;
      if (req && req !== shownFor) { shownFor = req; open(true); }
      if (!req) shownFor = null;
    });
  },
});
