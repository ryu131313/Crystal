// The item hover label, ported from the desktop client (Client/MirScenes/GameScene.cs):
// GameScene.CreateItemLabel and its section builders NameInfoLabel, AttackInfoLabel, DefenceInfoLabel, WeightInfoLabel,
// AwakeInfoLabel, SocketInfoLabel, NeedInfoLabel, BindInfoLabel, OverlapInfoLabel, StoryInfoLabel, GMMadeLabel, plus
// GradeNameColor, GetTotalAddedStats, DisposeItemLabel and the ItemLabel positioning in GameScene.Process (mouse + 28,
// clamped to the 1024 x 768 screen). Also Functions.PrintTimeSpanFromSeconds, UserItem.Weight / FriendlyName,
// ItemInfo.FriendlyName, Awake.GetAwakeLevel / GetAwakeValue and Enum.ToLocalizedString for RequiredClass / AwakeType.
// Strings are the English ClientTextKeys texts (Client/Localization/English.json).
//
// Each desktop section is an "outline" control (BackColour (50,50,50), Opacity 0.4, Gray border) sized to the label
// width; here each non-empty section is a block div holding its MirLabel lines (flow layout instead of measuring).
// Not ported: the Hero line of AttackInfoLabel (needs GameScene.HeroInfoList, which the web client does not keep) and
// the soulbound owner's name (GameScene.GetUserName / C.RequestUserName: no user-name cache in the web client; the
// desktop also shows an empty name until the answer arrives).

import type { ItemInfo, UserItem } from "../protocol/server.ts";
import {
  AwakeTypeName, BindMode, ItemGrade, ItemType, MirClass, RequiredClass, RequiredType, SpecialItemMode, Stat,
} from "../protocol/enums.generated.ts";
import { itemPrice } from "../game/items.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { getUiRoot, styleTooltip, SCREEN_HEIGHT, SCREEN_WIDTH } from "./mir/controls.ts";

/** What the label needs about the wearer (MapObject.User, or InspectDialog for inspect) and the item table. */
export interface ItemLabelContext {
  /** GameScene.ItemInfoList lookup (UserItem.Info). */
  info(item: UserItem): ItemInfo | undefined;
  /** Functions.GetRealItem(info, level, job, ItemInfoList). */
  realItem(info: ItemInfo): ItemInfo;
  level: number;
  job: number;      // MirClass
  gender: number;   // MirGender
  /** The wearer's stats (MapObject.User.Stats[stat]); 0 when unknown. */
  stat(stat: number): number;
}
export interface ItemLabelOptions { inspect?: boolean; hideDura?: boolean; hideAdded?: boolean; /** overrides ctx.info(item) (the game shop carries its own ItemInfo) */ info?: ItemInfo }

// — System.Drawing colours —
const C = {
  White: "#ffffff", Yellow: "#ffff00", Cyan: "#00ffff", Red: "#ff0000", DeepSkyBlue: "#00bfff", DarkOrange: "#ff8c00",
  Plum: "#dda0dd", Orchid: "#da70d6", DarkKhaki: "#bdb76b", Khaki: "#f0e68c",
} as const;

// — English ClientTextKeys —
const T = {
  ItemGradeCommon: "Common", ItemGradeRare: "Rare", ItemGradeLegendary: "Legendary", ItemGradeMythical: "Mythical", ItemGradeHeroic: "Heroic",
  UsageCurrentMax: " Usage {0}/{1}", PurityValue: " Purity {0}", QualityValue: " Quality {0}", LoyaltyValues: " Loyalty {0} / {1}",
  NutritionValue: " Nutrition {0}", DurationValue: " Duration {0}", Durability: "Durability:", WeddingRing: "WeddingRing", Weight: "W:",
  AddsDurability: "Adds +{0} Durability", SealsFor: "Seals for {0}",
  DC: "DC + {0}~{1}", AddsDC: "Adds +{0} DC", MC: "MC + {0}~{1}", AddsMC: "Adds +{0} MC", SC: "SC + {0}~{1}", AddsSC: "Adds +{0} SC",
  BagWeightPercent: "BagWeight + {0}% ", ExpPercent: "Exp + {0}% ", DropPercent: "Drop + {0}% ", Luck: "Luck + {0}", CursePlusValue: "Curse + {0}",
  Accuracy: "Accuracy: + {0}", AddsAccuracyValue: "Adds +{0} Accuracy", Holy: "Holy: + {0}",
  AttackSpeedValue: "A.Speed: {0}{1}", AddAttackSpeed: "Adds +{0} A.Speed", FreezingPlus: "Freezing: + {0}", AddsFreezingPlus: "Adds +{0} Freezing",
  PoisonPlus: "Poison: + {0}", AddsPoisonPlus: "Adds +{0} Poison", CriticalChancePlus: "Critical Chance: + {0}", FlexibilityPlus: "Flexibility: + {0}",
  CriticalDamagePlus: "Critical Damage: + {0}", ReflectChance: "Reflect chance: {0}", HPDrainRate: "HP Drain Rate: {0}%",
  ExpRate: "Exp Rate: {0}{1}%", DropRate: "Drop Rate: {0}{1}%", GoldRate: "Gold Rate: {0}{1}%",
  AC: "AC + {0}~{1}", AddsAC: "Adds +{0} AC", NibbleChance: "Nibble Chance + {0}~{1}%", FinderIncrease: "Finder Increase + {0}~{1}%",
  SuccessChance: "Success Chance + {0}%", MAC: "MAC + {0}~{1}", AddsMAC: "Adds +{0} MAC", AutoReelChance: "AutoReel Chance + {0}%",
  MaxHpPlus: "Max HP + {0}", MaxMpPlus: "Max MP + {0}", MaxHpPlusPercent: "Max HP + {0}%", MaxMpPlusPercent: "Max MP + {0}%",
  MaxAcPlusPercent: "Max AC + {0}%", MaxMacPlusPercent: "Max MAC + {0}%", HealthRecoveryPlus: "Health Recovery + {0}",
  ManaRecoveryPlus: "Mana Recovery + {0}", PoisonRecoveryPlus: "Poison Recovery + {0}", Agility: "Agility: + {0}", AddsAgilityPlus: "Adds +{0} Agility",
  StrongPlus: "Strong + {0}", PoisonResistPlus: "Poison Resist + {0}", AddPoisonResistPlus: "Adds +{0} Poison Resist",
  MagicResistPlus: "Magic Resist + {0}", AddMagicResistPlus: "Adds +{0} Magic Resist", MaxDcPlusPercent: "Max DC + {0}%",
  MaxMcPlusPercent: "Max MC + {0}%", MaxScPlusPercent: "Max SC + {0}%", AllDamageReductionPlusPercent: "All Damage Reduction + {0}%",
  HandWeightPlus: "Hand Weight + {0}", WearWeightPlus: "Wear Weight + {0}", BagWeightPlus: "Bag Weight + {0}", InstantRun: "Instant Run",
  TimeValue: "Time : {0}", AwakeningWithValue: "{0} Awakening({1})", SocketWithValue: "Socket : {0}", Empty: "Empty",
  OpenSocketsTips: "Ctrl + Right Click To Open Sockets",
  RequiredLevel: "Required Level : {0}", RequiredAC: "Required AC : {0}", RequiredMAC: "Required MAC : {0}", RequiredDC: "Required DC : {0}",
  RequiredMC: "Required MC : {0}", RequiredSC: "Required SC : {0}", MaximumLevel: "Maximum Level : {0}", RequiredBaseAC: "Required Base AC : {0}",
  RequiredBaseMAC: "Required Base MAC : {0}", RequiredBaseDC: "Required Base DC : {0}", RequiredBaseMC: "Required Base MC : {0}",
  RequiredBaseSC: "Required Base SC : {0}", UnknownTypeRequired: "Unknown Type Required", ClassRequired: "Class Required : {0}",
  SellingPriceGold: "Selling Price : {0} Gold",
  CantDropOnDeath: "Can't drop on death", CantDrop: "Can't drop", CantUpgrade: "Can't upgrade", CantSell: "Can't sell", CantTrade: "Can't trade",
  CantStore: "Can't store", CantRepair: "Can't repair", CantSpecialRepair: "Can't special repair", BreaksOnDeath: "Breaks on death",
  DestroyedWhenDropped: "Destroyed when dropped", CannotBeWeddingRing: "Cannot be a Wedding Ring", CannotBeUsedByHero: "Cannot be used by Hero",
  SoulBindsOnEquip: "SoulBinds on equip", SoulboundTo: "Soulbound to: ", Cursed: "Cursed",
  CannotBeUsedOnAnyItem: "Cannot be used on any item.", CanBeUsedOn: "Can be used on: ", AfterWeapon: "-Weapon", AfterArmour: "-Armour",
  AfterHelmet: "-Helmet", AfterNecklace: "-Necklace", AfterBracelet: "-Bracelet", AfterRing: "-Ring", AfterAmulet: "-Amulet", AfterBelt: "-Belt",
  AfterBoots: "-Boots", Stone: "Stone", AfterCandle: "-Candle",
  ExpiresIn: "Expires in {0}", Expired: "Expired", SealedFor: "Sealed for {0}", ItemRentedFrom: "Item rented from: ",
  RentalExpiresIn: "Rental expires in: {0}", RentalLockExpiresIn: "Rental lock expires in: {0}",
  HoldCtrlPartialRepairWeaponsAccessories: "Hold CTRL and left click to partially repair\nweapons and accessory items.",
  HoldCtrlPartialRepairArmourDrapery: "Hold CTRL and left click to partially repair\narmour and drapery items.",
  HoldCtrlCombineDestroyChance: "Hold CTRL and left click to combine with an item.\nHas chance to destroy combining item.",
  HoldCtrlCombineNoDestroy: "Hold CTRL and left click to combine with an item.\nWill NOT destroy combining item.",
  HoldCtrlFullRepairWeaponsAccessories: "Hold CTRL and left click to completely repair\nweapons and accessory items.",
  HoldCtrlFullRepairArmourDrapery: "Hold CTRL and left click to completely repair\narmour and drapery items.",
  HoldCtrlSealItem: "Hold CTRL and left click to seal an item.",
  MaxCombine: "Max Combine Count : {0}{1}Shift + Left click to split the stack",
  AddsCreditsToAccount: "Adds {0} Credits to your Account.", ItemDescription: "Item Description", CreatedByGameMaster: "Created by Game Master",
} as const;

/** ItemType -> ClientTextKeys.ItemTypeX (NameInfoLabel's baseText switch). */
const ITEM_TYPE_TEXT: Partial<Record<number, string>> = {
  [ItemType.Weapon]: "Weapon", [ItemType.Armour]: "Armour", [ItemType.Helmet]: "Helmet", [ItemType.Necklace]: "Necklace",
  [ItemType.Bracelet]: "Bracelet", [ItemType.Ring]: "Ring", [ItemType.Amulet]: "Amulet", [ItemType.Belt]: "Belt", [ItemType.Boots]: "Boots",
  [ItemType.Stone]: "Stone", [ItemType.Torch]: "Torch", [ItemType.Potion]: "Potion", [ItemType.Ore]: "Ore", [ItemType.Meat]: "Meat",
  [ItemType.CraftingMaterial]: "CraftingMaterial", [ItemType.Scroll]: "Scroll", [ItemType.Gem]: "Gem", [ItemType.Mount]: "Mount",
  [ItemType.Book]: "Book", [ItemType.Script]: "Script", [ItemType.Reins]: "Reins", [ItemType.Bells]: "Bells", [ItemType.Saddle]: "Saddle",
  [ItemType.Ribbon]: "Ribbon", [ItemType.Mask]: "Mask", [ItemType.Food]: "Food", [ItemType.Hook]: "Hook", [ItemType.Float]: "Float",
  [ItemType.Bait]: "Bait", [ItemType.Finder]: "Finder", [ItemType.Reel]: "Reel", [ItemType.Fish]: "Fish", [ItemType.Quest]: "Quest",
  [ItemType.Awakening]: "Awakening", [ItemType.Pets]: "Pets", [ItemType.Transform]: "Transform", [ItemType.Deco]: "Deco",
  [ItemType.MonsterSpawn]: "SpawnEgg", [ItemType.SealedHero]: "SealedHero",
};

/** English.json Enum "RequiredClass_X" (Enum.ToLocalizedString). */
const REQUIRED_CLASS_TEXT: Partial<Record<string, string>> = {
  Warrior: "Warrior", Wizard: "Wizard", Taoist: "Taoist", Assassin: "Assassin", Archer: "Archer", WarWizTao: "WarWizTao", None: "All Class",
};

/** Globals.FishingRodShapes. */
const FISHING_ROD_SHAPES = [49, 50];

/** string.Format with C# `{0}` / `{0:fmt}` placeholders (numeric `0.##` style formats round to the given decimals). */
function fmt(text: string, ...args: unknown[]): string {
  return text.replace(/\{(\d+)(?::([^}]*))?\}/g, (_m, i: string, f: string | undefined) => {
    const v = args[Number(i)];
    if (v === undefined || v === null) return "";
    if (f && typeof v === "number") {
      const decimals = (f.split(".")[1] ?? "").length;
      return String(Number(v.toFixed(decimals)));
    }
    return String(v);
  });
}

/** Functions.PrintTimeSpanFromSeconds. */
function printTimeSpanFromSeconds(secs: number, accurate = true): string {
  const total = Math.max(0, Math.floor(secs));
  const days = Math.floor(total / 86400), hours = Math.floor(total / 3600) % 24, minutes = Math.floor(total / 60) % 60, seconds = total % 60;
  const totalHours = Math.floor(total / 3600);
  const d2 = (n: number) => String(n).padStart(2, "0");
  if (total < 60) return `${seconds}s`;
  if (total < 3600) return accurate ? `${minutes}m ${d2(seconds)}s` : `${minutes}m`;
  if (total < 86400) return accurate ? `${hours}h ${d2(minutes)}m ${d2(seconds)}s` : `${totalHours}h ${d2(minutes)}m`;
  return accurate ? `${days}d ${d2(hours)}h ${d2(minutes)}m ${d2(seconds)}s` : `${days}d ${hours}h ${d2(minutes)}m`;
}

/** Enum.HasFlag (works for the i16 BindMode.NoHero = -32768 too). */
const hasFlag = (value: number, flag: number) => (value & flag) === flag;

/** ItemInfo.FriendlyName: end digits and [..] removed. */
function infoFriendlyName(info: ItemInfo): string {
  return info.Name.replace(/\d+$/, "").replace(/\[[^\]]*\]/g, "");
}

/** UserItem.FriendlyName. */
function itemFriendlyName(item: UserItem, info: ItemInfo | undefined): string {
  const name = info ? infoFriendlyName(info) : `Item #${item.ItemIndex}`;
  return item.Count > 1 ? `${name} (${item.Count})` : name;
}

/** UserItem.Weight. */
function itemWeight(item: UserItem, info: ItemInfo): number {
  return info.Type === ItemType.Amulet || info.Type === ItemType.Bait ? info.Weight : info.Weight * item.Count;
}

/** RequiredClass.ToLocalizedString: a defined name, else the [Flags] Enum.ToString ("Warrior, Wizard"). */
function requiredClassText(value: number): string {
  const named = Object.entries(RequiredClass).find(([, v]) => v === value);
  if (named) return REQUIRED_CLASS_TEXT[named[0]] ?? named[0];
  // Enum.ToString of a [Flags] combination: greedy from the highest value, listed in ascending order
  const parts: [string, number][] = [];
  let rest = value;
  for (const [k, v] of Object.entries(RequiredClass).sort((a, b) => b[1] - a[1]) as [string, number][]) {
    if (v !== 0 && (rest & v) === v) { parts.push([k, v]); rest -= v; }
  }
  if (rest !== 0 || parts.length === 0) return String(value);
  return parts.sort((a, b) => a[1] - b[1]).map((p) => p[0]).join(", ");
}

/** "###,###,##0". */
const groupThousands = (n: number) => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ",");

/** GameScene.GradeNameColor. */
function gradeNameColor(grade: number): string {
  switch (grade) {
    case ItemGrade.Common: return C.Yellow;
    case ItemGrade.Rare: return C.DeepSkyBlue;
    case ItemGrade.Legendary: return C.DarkOrange;
    case ItemGrade.Mythical: return C.Plum;
    case ItemGrade.Heroic: return C.Red;
    default: return C.Yellow;
  }
}

// — Label model —

interface Line { text: string; color: string }
type Section = Line[];

interface Build {
  item: UserItem;
  info: ItemInfo;
  real: ItemInfo;
  ctx: ItemLabelContext;
  inspect: boolean;
  hideDura: boolean;
  hideAdded: boolean;
}

const stat = (stats: Record<number, number> | null | undefined, s: number) => stats?.[s] ?? 0;
const plusAdd = (add: number) => (add > 0 ? ` (+${add})` : "");

/** GameScene.GetTotalAddedStats. */
function getTotalAddedStats(item: UserItem, ctx: ItemLabelContext): Record<number, number> {
  const total: Record<number, number> = {};
  const add = (stats: Record<number, number> | null | undefined) => {
    if (!stats) return;
    for (const [k, v] of Object.entries(stats)) {
      const sum = (total[Number(k)] ?? 0) + v;
      if (sum === 0) delete total[Number(k)];
      else total[Number(k)] = sum;
    }
  };
  add(item.AddedStats);
  for (const socketItem of item.Slots ?? []) {
    if (!socketItem) continue;
    const socketBase = ctx.info(socketItem);
    const socketInfo = socketBase ? ctx.realItem(socketBase) : undefined;
    if (socketItem.CurrentDura === 0 && (socketInfo?.Durability ?? 0) > 0) continue;
    add(socketInfo?.Stats); // unknown socket ItemInfo: only its AddedStats count
    add(socketItem.AddedStats);
  }
  return total;
}

/** GameScene.NameInfoLabel. */
function nameInfoLabel(b: Build): Section {
  const { item, info } = b;
  const lines: Section = [];
  let gradeString = "";
  switch (info.Grade) {
    case ItemGrade.Common: gradeString = T.ItemGradeCommon; break;
    case ItemGrade.Rare: gradeString = T.ItemGradeRare; break;
    case ItemGrade.Legendary: gradeString = T.ItemGradeLegendary; break;
    case ItemGrade.Mythical: gradeString = T.ItemGradeMythical; break;
    case ItemGrade.Heroic: gradeString = T.ItemGradeHeroic; break;
  }
  let name = info.Grade !== ItemGrade.None ? `${infoFriendlyName(info)}\n${gradeString}` : infoFriendlyName(info);
  if (item.RefineAdded > 0) name = "(*)" + name;
  lines.push({ text: name, color: gradeNameColor(info.Grade) });

  let text = "";
  if (info.Durability > 0 && !b.hideDura) {
    switch (info.Type) {
      case ItemType.Amulet:
        if (item.CurrentDura > 0 || item.MaxDura > 0) text = fmt(T.UsageCurrentMax, item.CurrentDura, item.MaxDura);
        break;
      case ItemType.Ore:
        if (item.CurrentDura > 0) text = fmt(T.PurityValue, Math.floor(item.CurrentDura / 1000));
        break;
      case ItemType.Meat:
        if (item.CurrentDura > 0) text = fmt(T.QualityValue, Math.floor(item.CurrentDura / 1000));
        break;
      case ItemType.Mount:
        if (item.CurrentDura > 0 || item.MaxDura > 0) text = fmt(T.LoyaltyValues, item.CurrentDura, item.MaxDura);
        break;
      case ItemType.Food:
        if (item.CurrentDura > 0) text = fmt(T.NutritionValue, item.CurrentDura);
        break;
      case ItemType.Gem:
      case ItemType.Potion:
      case ItemType.Transform:
      case ItemType.SealedHero:
        break;
      case ItemType.Pets:
        if ((info.Shape === 26 || info.Shape === 28) && item.CurrentDura > 0) { // WonderDrug, Knapsack
          text = fmt(T.DurationValue, printTimeSpanFromSeconds(item.CurrentDura * 3600, false));
        }
        break;
      default: {
        const current = Math.floor(item.CurrentDura / 1000);
        const maximum = Math.floor(item.MaxDura / 1000);
        if (current > 0 || maximum > 0) text = `${T.Durability} ${current}/${maximum}`;
        break;
      }
    }
  }

  let baseText = ITEM_TYPE_TEXT[info.Type] ?? "";
  if (item.WeddingRing !== -1) baseText += T.WeddingRing;

  const tailParts: string[] = [];
  const weight = itemWeight(item, info);
  if (weight > 0) tailParts.push(`${T.Weight} ${weight}`);
  if (text) tailParts.push(text);
  if (tailParts.length > 0) {
    const tailText = tailParts.join("  ");
    baseText = baseText ? `${baseText}\n${tailText}` : tailText;
  }
  lines.push({ text: baseText, color: C.White });
  return lines;
}

/** GameScene.AttackInfoLabel. */
function attackInfoLabel(b: Build, addedStats: Record<number, number>): Section {
  const { item, info, real } = b;
  const lines: Section = [];
  const addOk = !b.hideAdded && (!info.NeedIdentify || item.Identified);
  const added = (s: number) => (addOk ? stat(addedStats, s) : 0);
  const push = (text: string, add: number) => lines.push({ text, color: add > 0 ? C.Cyan : C.White });

  let fishingItem = false;
  switch (info.Type) {
    case ItemType.Hook: case ItemType.Float: case ItemType.Bait: case ItemType.Finder: case ItemType.Reel:
      fishingItem = true; break;
    case ItemType.Weapon:
      if (FISHING_ROD_SHAPES.includes(info.Shape)) fishingItem = true;
      break;
  }

  let minValue: number, maxValue: number, addValue: number, text: string;

  // Dura gem
  minValue = real.Durability;
  if (minValue > 0 && real.Type === ItemType.Gem) {
    text = real.Shape === 8 ? fmt(T.SealsFor, printTimeSpanFromSeconds(minValue * 60)) : fmt(T.AddsDurability, Math.trunc(minValue / 1000));
    lines.push({ text, color: C.White });
  }

  // DC / MC / SC
  for (const [min, max, normal, gem] of [
    [Stat.MinDC, Stat.MaxDC, T.DC, T.AddsDC], [Stat.MinMC, Stat.MaxMC, T.MC, T.AddsMC], [Stat.MinSC, Stat.MaxSC, T.SC, T.AddsSC],
  ] as const) {
    minValue = stat(real.Stats, min);
    maxValue = stat(real.Stats, max);
    addValue = added(max);
    if (minValue > 0 || maxValue > 0 || addValue > 0) {
      text = info.Type !== ItemType.Gem ? fmt(normal, minValue, maxValue + addValue) + plusAdd(addValue) : fmt(gem, minValue + maxValue + addValue);
      push(text, addValue);
    }
  }

  // LUCK / SUCCESS
  minValue = stat(real.Stats, Stat.Luck);
  addValue = added(Stat.Luck);
  if (minValue !== 0 || addValue !== 0) {
    if (real.Type === ItemType.Pets && real.Shape === 28) text = fmt(T.BagWeightPercent, minValue + addValue);
    else if (real.Type === ItemType.Potion && real.Shape === 4) text = fmt(T.ExpPercent, minValue + addValue);
    else if (real.Type === ItemType.Potion && real.Shape === 5) text = fmt(T.DropPercent, minValue + addValue);
    else text = fmt(minValue + addValue > 0 ? T.Luck : T.CursePlusValue, Math.abs(minValue + addValue));
    push(text, addValue);
  }

  // ACC
  minValue = stat(real.Stats, Stat.Accuracy);
  addValue = added(Stat.Accuracy);
  if (minValue > 0 || addValue > 0) {
    text = info.Type !== ItemType.Gem ? fmt(T.Accuracy, minValue + addValue) + plusAdd(addValue) : fmt(T.AddsAccuracyValue, minValue + addValue);
    push(text, addValue);
  }

  // HOLY
  minValue = stat(real.Stats, Stat.Holy);
  if (minValue > 0) push(fmt(T.Holy, minValue), 0);

  // ASPEED
  minValue = stat(real.Stats, Stat.AttackSpeed);
  addValue = added(Stat.AttackSpeed);
  if (minValue !== 0 || addValue !== 0) {
    const plus = addValue + minValue < 0 ? "" : "+";
    if (info.Type !== ItemType.Gem) {
      const negative = addValue < 0 ? "" : "+";
      text = fmt(T.AttackSpeedValue, plus, minValue + addValue) + (addValue !== 0 ? ` (${negative}${addValue})` : "");
    } else {
      text = fmt(T.AddAttackSpeed, minValue + addValue);
    }
    push(text, addValue);
  }

  // FREEZING / POISON
  for (const [s, normal, gem] of [[Stat.Freezing, T.FreezingPlus, T.AddsFreezingPlus], [Stat.PoisonAttack, T.PoisonPlus, T.AddsPoisonPlus]] as const) {
    minValue = stat(real.Stats, s);
    addValue = added(s);
    if (minValue > 0 || addValue > 0) {
      text = info.Type !== ItemType.Gem ? fmt(normal, minValue + addValue) + plusAdd(addValue) : fmt(gem, minValue + addValue);
      push(text, addValue);
    }
  }

  // CRITICALRATE / FLEXIBILITY
  minValue = stat(real.Stats, Stat.CriticalRate);
  addValue = added(Stat.CriticalRate);
  if ((minValue > 0 || addValue > 0) && real.Type !== ItemType.Gem) {
    push(fmt(fishingItem ? T.FlexibilityPlus : T.CriticalChancePlus, minValue + addValue) + plusAdd(addValue), addValue);
  }

  // CRITICALDAMAGE
  minValue = stat(real.Stats, Stat.CriticalDamage);
  addValue = added(Stat.CriticalDamage);
  if ((minValue > 0 || addValue > 0) && real.Type !== ItemType.Gem) {
    push(fmt(T.CriticalDamagePlus, minValue + addValue) + plusAdd(addValue), addValue);
  }

  // Reflect
  minValue = stat(real.Stats, Stat.Reflect);
  if (minValue > 0 && real.Type !== ItemType.Gem) push(fmt(T.ReflectChance, minValue), 0);

  // Hpdrain
  minValue = stat(real.Stats, Stat.HPDrainRatePercent);
  if (minValue > 0 && real.Type !== ItemType.Gem) push(fmt(T.HPDrainRate, minValue), 0);

  // Exp Rate / Drop Rate / Gold Rate
  for (const [s, template] of [[Stat.ExpRatePercent, T.ExpRate], [Stat.ItemDropRatePercent, T.DropRate], [Stat.GoldDropRatePercent, T.GoldRate]] as const) {
    minValue = stat(real.Stats, s);
    addValue = added(s);
    if (minValue !== 0 || addValue !== 0) {
      const plus = addValue + minValue < 0 ? "" : "+";
      const negative = addValue < 0 ? "" : "+";
      push(fmt(template, plus, minValue + addValue) + (addValue !== 0 ? ` (${negative}${addValue}%)` : ""), addValue);
    }
  }

  // Hero: not ported (addedStats[Stat.Hero] -> GameScene.HeroInfoList entry's ToString; the web client has no HeroInfoList)

  return lines;
}

/** GameScene.DefenceInfoLabel. */
function defenceInfoLabel(b: Build, addedStats: Record<number, number>): Section {
  const { item, info, real } = b;
  const lines: Section = [];
  const addOk = !b.hideAdded && (!info.NeedIdentify || item.Identified);
  const added = (s: number) => (addOk ? stat(addedStats, s) : 0);
  const push = (text: string, add: number) => lines.push({ text, color: add > 0 ? C.Cyan : C.White });

  let fishingItem = false;
  switch (info.Type) {
    case ItemType.Hook: case ItemType.Float: case ItemType.Bait: case ItemType.Finder: case ItemType.Reel:
      fishingItem = true; break;
    case ItemType.Weapon:
      if (info.Shape === 49 || info.Shape === 50) fishingItem = true;
      break;
  }

  let minValue: number, maxValue: number, addValue: number, text: string;

  // AC
  minValue = stat(real.Stats, Stat.MinAC);
  maxValue = stat(real.Stats, Stat.MaxAC);
  addValue = added(Stat.MaxAC);
  if (minValue > 0 || maxValue > 0 || addValue > 0) {
    text = info.Type !== ItemType.Gem ? fmt(T.AC, minValue, maxValue + addValue) + plusAdd(addValue) : fmt(T.AddsAC, minValue + maxValue + addValue);
    if (fishingItem) {
      if (info.Type === ItemType.Float) text = fmt(T.NibbleChance, minValue, maxValue + addValue) + plusAdd(addValue);
      else if (info.Type === ItemType.Finder) text = fmt(T.FinderIncrease, minValue, maxValue + addValue) + plusAdd(addValue);
      else text = fmt(T.SuccessChance, maxValue + addValue) + plusAdd(addValue);
    }
    push(text, addValue);
  }

  // MAC
  minValue = stat(real.Stats, Stat.MinMAC);
  maxValue = stat(real.Stats, Stat.MaxMAC);
  addValue = added(Stat.MaxMAC);
  if (minValue > 0 || maxValue > 0 || addValue > 0) {
    text = info.Type !== ItemType.Gem ? fmt(T.MAC, minValue, maxValue + addValue) + plusAdd(addValue) : fmt(T.AddsMAC, minValue + maxValue + addValue);
    if (fishingItem) text = fmt(T.AutoReelChance, maxValue + addValue);
    push(text, addValue);
  }

  // MAXHP
  if (info.Type !== ItemType.MonsterSpawn) {
    minValue = stat(real.Stats, Stat.HP);
    addValue = added(Stat.HP);
    if (minValue > 0 || addValue > 0) push(fmt(T.MaxHpPlus, minValue + addValue) + plusAdd(addValue), addValue);
  }

  // MAXMP
  minValue = stat(real.Stats, Stat.MP);
  addValue = added(Stat.MP);
  if (minValue > 0 || addValue > 0) push(fmt(T.MaxMpPlus, minValue + addValue) + plusAdd(addValue), addValue);

  // MAXHPRATE / MAXMPRATE / MAXACRATE / MAXMACRATE (no added value)
  for (const [s, template] of [
    [Stat.HPRatePercent, T.MaxHpPlusPercent], [Stat.MPRatePercent, T.MaxMpPlusPercent],
    [Stat.MaxACRatePercent, T.MaxAcPlusPercent], [Stat.MaxMACRatePercent, T.MaxMacPlusPercent],
  ] as const) {
    minValue = stat(real.Stats, s);
    if (minValue > 0) push(fmt(template, minValue), 0);
  }

  // HEALTH_RECOVERY / MANA_RECOVERY / POISON_RECOVERY
  for (const [s, template] of [
    [Stat.HealthRecovery, T.HealthRecoveryPlus], [Stat.SpellRecovery, T.ManaRecoveryPlus], [Stat.PoisonRecovery, T.PoisonRecoveryPlus],
  ] as const) {
    minValue = stat(real.Stats, s);
    addValue = added(s);
    if (minValue > 0 || addValue > 0) push(fmt(template, minValue + addValue) + plusAdd(addValue), addValue);
  }

  // AGILITY
  minValue = stat(real.Stats, Stat.Agility);
  addValue = added(Stat.Agility);
  if (minValue > 0 || addValue > 0) {
    text = info.Type !== ItemType.Gem ? fmt(T.Agility, minValue + addValue) + plusAdd(addValue) : fmt(T.AddsAgilityPlus, minValue + addValue);
    push(text, addValue);
  }

  // STRONG
  minValue = stat(real.Stats, Stat.Strong);
  addValue = added(Stat.Strong);
  if (minValue > 0 || addValue > 0) push(fmt(T.StrongPlus, minValue + addValue) + plusAdd(addValue), addValue);

  // POISON_RESIST / MAGIC_RESIST
  for (const [s, normal, gem] of [
    [Stat.PoisonResist, T.PoisonResistPlus, T.AddPoisonResistPlus], [Stat.MagicResist, T.MagicResistPlus, T.AddMagicResistPlus],
  ] as const) {
    minValue = stat(real.Stats, s);
    addValue = added(s);
    if (minValue > 0 || addValue > 0) {
      text = info.Type !== ItemType.Gem ? fmt(normal, minValue + addValue) + plusAdd(addValue) : fmt(gem, minValue + addValue);
      push(text, addValue);
    }
  }

  // MAX_DC_RATE / MAX_MC_RATE / MAX_SC_RATE / DAMAGE_REDUCTION (no added value)
  for (const [s, template] of [
    [Stat.MaxDCRatePercent, T.MaxDcPlusPercent], [Stat.MaxMCRatePercent, T.MaxMcPlusPercent], [Stat.MaxSCRatePercent, T.MaxScPlusPercent],
    [Stat.DamageReductionPercent, T.AllDamageReductionPlusPercent],
  ] as const) {
    minValue = stat(real.Stats, s);
    if (minValue > 0) push(fmt(template, minValue), 0);
  }

  return lines;
}

/** GameScene.WeightInfoLabel. */
function weightInfoLabel(b: Build): Section {
  const { info, real } = b;
  const lines: Section = [];
  // HANDWEIGHT / WEARWEIGHT / BAGWEIGHT
  for (const [s, template] of [[Stat.HandWeight, T.HandWeightPlus], [Stat.WearWeight, T.WearWeightPlus], [Stat.BagWeight, T.BagWeightPlus]] as const) {
    const minValue = stat(real.Stats, s);
    if (minValue > 0) lines.push({ text: fmt(template, minValue), color: C.White });
  }
  // FASTRUN
  if (real.CanFastRun) lines.push({ text: T.InstantRun, color: C.White });
  // TIME & RANGE
  if (info.Type === ItemType.Potion && info.Durability > 0) {
    lines.push({ text: fmt(T.TimeValue, printTimeSpanFromSeconds(info.Durability * 60)), color: C.White });
  }
  if (info.Type === ItemType.Transform && info.Durability > 0) {
    lines.push({ text: fmt(T.TimeValue, printTimeSpanFromSeconds(info.Durability, false)), color: C.White });
  }
  return lines;
}

/** GameScene.AwakeInfoLabel. */
function awakeInfoLabel(b: Build): Section {
  const { item, info, real } = b;
  const lines: Section = [];
  const levels = item.AwakeLevels ?? [];
  const awakeLevel = levels.length; // Awake.GetAwakeLevel
  const awakeValue = levels.reduce((sum, v) => (sum + v) & 0xff, 0); // Awake.GetAwakeValue (byte total)
  const typeName = AwakeTypeName[item.AwakeType] ?? String(item.AwakeType); // AwakeType.ToLocalizedString (English = the name)

  // AWAKENAME
  if (awakeLevel > 0) lines.push({ text: fmt(T.AwakeningWithValue, typeName, awakeLevel), color: gradeNameColor(info.Grade) });
  // AWAKE_TOTAL_VALUE
  if (awakeValue > 0) {
    lines.push({ text: fmt(real.Type !== ItemType.Armour ? "{0} + {1}~{2}" : "MAX {0} + {1}", typeName, awakeValue, awakeValue), color: C.White });
  }
  // AWAKE_LEVEL_VALUE
  for (let i = 0; i < awakeLevel; i++) {
    const template = real.Type !== ItemType.Armour ? "Level {0} : {1} + {2}~{3}" : "Level {0} : MAX {1} + {2}~{3}";
    lines.push({ text: fmt(template, i + 1, typeName, levels[i], levels[i]), color: C.White });
  }
  return lines;
}

/** GameScene.SocketInfoLabel. */
function socketInfoLabel(b: Build): Section {
  const { item, real, ctx } = b;
  const lines: Section = [];
  const slots = item.Slots ?? [];
  let count = 0;
  for (let i = 0; i < slots.length; i++) {
    count++;
    const slot = slots[i];
    const cyan = count > real.Slots && !FISHING_ROD_SHAPES.includes(real.Shape) && real.Type !== ItemType.Mount;
    lines.push({ text: fmt(T.SocketWithValue, slot ? itemFriendlyName(slot, ctx.info(slot)) : T.Empty), color: cyan ? C.Cyan : C.White });
  }
  if (count > 0) lines.push({ text: T.OpenSocketsTips, color: C.White });
  return lines;
}

/** GameScene.NeedInfoLabel (the desktop colours by MapObject.User; here by the context's wearer). */
function needInfoLabel(b: Build): Section {
  const { item, info, real, ctx } = b;
  const lines: Section = [];

  // LEVEL
  if (real.RequiredAmount > 0) {
    let text: string;
    let colour: string = C.White;
    const amount = real.RequiredAmount;
    const below = (s: number) => { if (ctx.stat(s) < amount) colour = C.Red; };
    switch (real.RequiredType) {
      case RequiredType.Level: text = fmt(T.RequiredLevel, amount); if (ctx.level < amount) colour = C.Red; break;
      case RequiredType.MaxAC: text = fmt(T.RequiredAC, amount); below(Stat.MaxAC); break;
      case RequiredType.MaxMAC: text = fmt(T.RequiredMAC, amount); below(Stat.MaxMAC); break;
      case RequiredType.MaxDC: text = fmt(T.RequiredDC, amount); below(Stat.MaxDC); break;
      case RequiredType.MaxMC: text = fmt(T.RequiredMC, amount); below(Stat.MaxMC); break;
      case RequiredType.MaxSC: text = fmt(T.RequiredSC, amount); below(Stat.MaxSC); break;
      case RequiredType.MaxLevel: text = fmt(T.MaximumLevel, amount); if (ctx.level > amount) colour = C.Red; break;
      case RequiredType.MinAC: text = fmt(T.RequiredBaseAC, amount); below(Stat.MinAC); break;
      case RequiredType.MinMAC: text = fmt(T.RequiredBaseMAC, amount); below(Stat.MinMAC); break;
      case RequiredType.MinDC: text = fmt(T.RequiredBaseDC, amount); below(Stat.MinDC); break;
      case RequiredType.MinMC: text = fmt(T.RequiredBaseMC, amount); below(Stat.MinMC); break;
      case RequiredType.MinSC: text = fmt(T.RequiredBaseSC, amount); below(Stat.MinSC); break;
      default: text = T.UnknownTypeRequired; break;
    }
    lines.push({ text, color: colour });
  }

  // CLASS
  if (real.RequiredClass !== RequiredClass.None) {
    let colour: string = C.White;
    const need = (flag: number) => { if (!hasFlag(real.RequiredClass, flag)) colour = C.Red; };
    switch (ctx.job) {
      case MirClass.Warrior: need(RequiredClass.Warrior); break;
      case MirClass.Wizard: need(RequiredClass.Wizard); break;
      case MirClass.Taoist: need(RequiredClass.Taoist); break;
      case MirClass.Assassin: need(RequiredClass.Assassin); break;
      case MirClass.Archer: need(RequiredClass.Archer); break;
    }
    lines.push({ text: fmt(T.ClassRequired, requiredClassText(real.RequiredClass)), color: colour });
  }

  // BUYING - SELLING PRICE (UserItem.Price uses item.Info)
  const price = itemPrice(item, info);
  if (price > 0) lines.push({ text: fmt(T.SellingPriceGold, groupThousands(Math.floor(price / 2))), color: C.White });

  return lines;
}

/** GameScene.BindInfoLabel. */
function bindInfoLabel(b: Build): Section {
  const { item, info } = b;
  const lines: Section = [];
  const yellow = (text: string) => lines.push({ text, color: C.Yellow });
  const white = (text: string) => lines.push({ text, color: C.White });
  const bind = info.Bind;
  const bound = (flag: number) => bind !== BindMode.None && hasFlag(bind, flag);

  if (bound(BindMode.DontDeathdrop)) yellow(T.CantDropOnDeath);
  if (bound(BindMode.DontDrop)) yellow(T.CantDrop);
  if (bound(BindMode.DontUpgrade)) yellow(T.CantUpgrade);
  if (bound(BindMode.DontSell)) yellow(T.CantSell);
  if (bound(BindMode.DontTrade)) yellow(T.CantTrade);
  if (bound(BindMode.DontStore)) yellow(T.CantStore);
  if (bound(BindMode.DontRepair)) yellow(T.CantRepair);
  if (bound(BindMode.NoSRepair)) yellow(T.CantSpecialRepair);
  if (bound(BindMode.BreakOnDeath)) yellow(T.BreaksOnDeath);
  if (bound(BindMode.DestroyOnDrop)) yellow(T.DestroyedWhenDropped);
  if (bound(BindMode.NoWeddingRing)) yellow(T.CannotBeWeddingRing);
  if (bound(BindMode.NoHero)) yellow(T.CannotBeUsedByHero);

  // BIND_ON_EQUIP
  if (hasFlag(bind, BindMode.BindOnEquip) && item.SoulBoundId === -1) yellow(T.SoulBindsOnEquip);
  else if (item.SoulBoundId !== -1) yellow(T.SoulboundTo); // not ported: + GetUserName(SoulBoundId) (no user-name cache)

  // CURSED
  if (!b.hideAdded && (!info.NeedIdentify || item.Identified) && item.Cursed) yellow(T.Cursed);

  // Gems
  if (info.Type === ItemType.Gem) {
    const unique = info.Unique;
    yellow(unique === SpecialItemMode.None ? T.CannotBeUsedOnAnyItem : T.CanBeUsedOn);
    if (hasFlag(unique, SpecialItemMode.Paralize)) white(T.AfterWeapon);
    if (hasFlag(unique, SpecialItemMode.Teleport)) white(T.AfterArmour);
    if (hasFlag(unique, SpecialItemMode.ClearRing)) white(T.AfterHelmet);
    if (hasFlag(unique, SpecialItemMode.Protection)) white(T.AfterNecklace);
    if (hasFlag(unique, SpecialItemMode.Revival)) white(T.AfterBracelet);
    if (hasFlag(unique, SpecialItemMode.Muscle)) white(T.AfterRing);
    if (hasFlag(unique, SpecialItemMode.Flame)) white(T.AfterAmulet);
    if (hasFlag(unique, SpecialItemMode.Healing)) white(T.AfterBelt);
    if (hasFlag(unique, SpecialItemMode.Probe)) white(T.AfterBoots);
    if (hasFlag(unique, SpecialItemMode.Skill)) white(T.Stone);
    if (hasFlag(unique, SpecialItemMode.NoDuraLoss)) white(T.AfterCandle);
  }

  const now = Date.now();
  const secondsLeft = (binary: bigint) => (dateFromBinary(binary).getTime() - now) / 1000;

  // EXPIRE (UserItem.ExpireInfo)
  if (item.ExpiryDate !== null && item.ExpiryDate !== undefined) {
    const remainingSeconds = secondsLeft(item.ExpiryDate);
    yellow(remainingSeconds > 0 ? fmt(T.ExpiresIn, printTimeSpanFromSeconds(remainingSeconds)) : T.Expired);
  }

  // SEALED
  if (item.Sealed) {
    const remainingSeconds = secondsLeft(item.Sealed.ExpiryDate);
    if (remainingSeconds > 0) lines.push({ text: fmt(T.SealedFor, printTimeSpanFromSeconds(remainingSeconds)), color: C.Red });
  }

  // Rental
  const rental = item.Rental;
  if (rental && !rental.RentalLocked) {
    lines.push({ text: T.ItemRentedFrom + rental.OwnerName, color: C.DarkKhaki });
    const remainingTime = secondsLeft(rental.ExpiryDate);
    lines.push({ text: remainingTime > 0 ? fmt(T.RentalExpiresIn, printTimeSpanFromSeconds(remainingTime)) : "Rental expired", color: C.Khaki });
  } else if (rental && rental.RentalLocked && secondsLeft(rental.ExpiryDate) > 0) {
    const remainingTime = secondsLeft(rental.ExpiryDate);
    lines.push({
      text: remainingTime > 0 ? fmt(T.RentalLockExpiresIn, printTimeSpanFromSeconds(remainingTime)) : "Rental lock expired", color: C.DarkKhaki,
    });
  }

  return lines;
}

/** GameScene.OverlapInfoLabel. */
function overlapInfoLabel(b: Build): Section {
  const { real } = b;
  const lines: Section = [];
  // GEM
  if (real.Type === ItemType.Gem) {
    let text = "";
    switch (real.Shape) {
      case 1: text = T.HoldCtrlPartialRepairWeaponsAccessories; break;
      case 2: text = T.HoldCtrlPartialRepairArmourDrapery; break;
      case 3: text = T.HoldCtrlCombineDestroyChance; break;
      case 4: text = T.HoldCtrlCombineNoDestroy; break;
      case 5: text = T.HoldCtrlFullRepairWeaponsAccessories; break;
      case 6: text = T.HoldCtrlFullRepairArmourDrapery; break;
      case 8: text = T.HoldCtrlSealItem; break;
    }
    lines.push({ text, color: C.White });
  }
  // SPLITUP
  if (real.StackSize > 1 && real.Type !== ItemType.Gem) lines.push({ text: fmt(T.MaxCombine, real.StackSize, "\n"), color: C.White });
  return lines;
}

/** GameScene.StoryInfoLabel. */
function storyInfoLabel(b: Build): Section {
  const { info, real } = b;
  const lines: Section = [];
  // TOOLTIP (the desktop writes the credit-scroll text into Info.ToolTip; here it is computed without mutating the ItemInfo)
  let toolTip = info.ToolTip ?? "";
  if (real.Type === ItemType.Scroll && real.Shape === 7) toolTip = fmt(T.AddsCreditsToAccount, info.Price); // Credit Scroll
  if (toolTip) {
    lines.push({ text: T.ItemDescription, color: C.DarkKhaki });
    lines.push({ text: toolTip, color: C.Khaki });
  }
  return lines;
}

/** GameScene.GMMadeLabel. */
function gmMadeLabel(b: Build): Section {
  return b.item.GMMade ? [{ text: T.CreatedByGameMaster, color: C.Orchid }] : [];
}

/** GameScene.CreateItemLabel's section list (outlines[0..10]); empty sections are dropped like the desktop's null outlines. */
function buildSections(item: UserItem, ctx: ItemLabelContext, opts: ItemLabelOptions): Section[] {
  const info = opts.info ?? ctx.info(item);
  if (!info) return [[{ text: `Item #${item.ItemIndex}`, color: C.Yellow }]];
  const b: Build = {
    item, info, real: ctx.realItem(info), ctx, inspect: !!opts.inspect, hideDura: !!opts.hideDura, hideAdded: !!opts.hideAdded,
  };
  const addedStats = getTotalAddedStats(item, ctx);
  return [
    nameInfoLabel(b),
    attackInfoLabel(b, addedStats),
    defenceInfoLabel(b, addedStats),
    weightInfoLabel(b),
    awakeInfoLabel(b),
    socketInfoLabel(b),
    needInfoLabel(b),
    bindInfoLabel(b),
    overlapInfoLabel(b),
    storyInfoLabel(b),
    gmMadeLabel(b),
  ].filter((s) => s.length > 0);
}

// — DOM —

const OUTLINE_TEXT = "1px 0 0 #000, -1px 0 0 #000, 0 1px 0 #000, 0 -1px 0 #000";

/** GameScene.CreateItemLabel: builds the label box (not attached). */
export function createItemLabel(item: UserItem, ctx: ItemLabelContext, opts: ItemLabelOptions = {}): HTMLDivElement {
  const box = document.createElement("div");
  styleTooltip(box, item.CurrentDura === 0 && item.MaxDura !== 0);
  Object.assign(box.style, {
    position: "absolute", left: "0px", top: "0px", width: "max-content", padding: "0", margin: "0",
    pointerEvents: "none", zIndex: "100000", boxSizing: "border-box",
  });

  const sections = buildSections(item, ctx, opts);
  sections.forEach((section, i) => {
    // the section "outline": BackColour (50,50,50) at Opacity 0.4, Gray border, the label's full width, +4 px above and below its lines
    const block = document.createElement("div");
    Object.assign(block.style, {
      display: "block", background: "rgba(50,50,50,0.4)", outline: "1px solid #808080", outlineOffset: "-1px",
      padding: "4px 0", marginTop: i > 0 ? "-1px" : "0",
    });
    for (const line of section) {
      if (!line.text) continue; // an AutoSize MirLabel with no text has no size
      const label = document.createElement("div");
      label.textContent = line.text;
      Object.assign(label.style, {
        display: "block", font: "8pt Arial, sans-serif", lineHeight: "1.15", color: line.color, textShadow: OUTLINE_TEXT,
        whiteSpace: "pre", padding: "0 4px", margin: "0",
      });
      block.append(label);
    }
    box.append(block);
  });
  return box;
}

/** The label's lines as plain text joined with "\n" (for title attributes and the chat-link hover). */
export function itemLabelText(item: UserItem, ctx: ItemLabelContext, opts: ItemLabelOptions = {}): string {
  return buildSections(item, ctx, opts).flat().map((l) => l.text).filter((t) => t).join("\n");
}

// — Hover label (GameScene.ItemLabel / HoverItem) —

let itemLabel: HTMLDivElement | null = null;
let hoverItem: UserItem | null = null;

/** Creates (or keeps, when the same item is already shown, like the desktop's HoverItem check) the hover label and positions it at the mouse + 28 (GameScene.Process clamping). */
export function showItemLabel(item: UserItem, ctx: ItemLabelContext, clientX: number, clientY: number, opts: ItemLabelOptions = {}): void {
  if (hoverItem !== item || !itemLabel || !itemLabel.isConnected) {
    hideItemLabel();
    hoverItem = item;
    itemLabel = createItemLabel(item, ctx, opts);
    getUiRoot().append(itemLabel);
  }
  moveItemLabel(clientX, clientY);
}

/** Re-positions the current label (pointermove). */
export function moveItemLabel(clientX: number, clientY: number): void {
  if (!itemLabel || !itemLabel.isConnected) return;
  const root = getUiRoot();
  root.append(itemLabel); // ItemLabel.BringToFront
  const r = root.getBoundingClientRect();
  const sx = r.width / root.offsetWidth || 1;
  const screenWidth = root.offsetWidth || SCREEN_WIDTH, screenHeight = root.offsetHeight || SCREEN_HEIGHT;
  let x = Math.round((clientX - r.left) / sx) + 28, y = Math.round((clientY - r.top) / sx) + 28;
  const w = itemLabel.offsetWidth, h = itemLabel.offsetHeight;
  if (x + w > screenWidth) x = screenWidth - w;
  if (y + h > screenHeight) y = screenHeight - h;
  itemLabel.style.left = x + "px";
  itemLabel.style.top = y + "px";
}

/** GameScene.DisposeItemLabel. */
export function hideItemLabel(): void {
  itemLabel?.remove();
  itemLabel = null;
  hoverItem = null;
}
