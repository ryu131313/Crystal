// Server -> browser packets: hand-ported from Shared/ServerPackets.cs ReadPacket bodies (same field order and types).
// Only the packets the web client uses are ported; any other id decodes to null and is skipped (the relay delivers
// one whole frame per message, so skipping is safe). Every port has a golden in test-data/codec (WebRelay
// --emit-fixtures) checked by tools/codec-test.ts.
// Rule: a change to one of these packets in Shared/ServerPackets.cs must update this file and the fixture sample.

import { Reader, readPoint, parseFrame, gunzip, type WirePoint } from "./binaryCodec.ts";
import { ServerPacketIds as S } from "./enums.generated.ts";

export interface SelectInfo {
  Index: number; Name: string; Level: number; Class: number; Gender: number; LastAccess: bigint;
}

function readSelectInfo(r: Reader): SelectInfo {
  return { Index: r.readI32(), Name: r.readString(), Level: r.readU16(), Class: r.readU8(), Gender: r.readU8(), LastAccess: r.readI64() };
}

function readBuffs(r: Reader): number[] {
  const n = r.readI32();
  if (n < 0 || n > r.remaining) throw new Error("buff count");
  const out: number[] = [];
  for (let i = 0; i < n; i++) out.push(r.readU8());
  return out;
}

/** UserItem(reader) packet layout (Shared/Data/ItemData.cs, version = int.MaxValue). Slot flag is INVERTED: true = empty. */
export interface UserItem {
  UniqueID: bigint; ItemIndex: number; CurrentDura: number; MaxDura: number; Count: number; SoulBoundId: number;
  Identified: boolean; Cursed: boolean; Slots: (UserItem | null)[]; GemCount: number; AddedStats: Record<number, number>;
  AwakeType: number; AwakeLevels: number[]; RefinedValue: number; RefineAdded: number; RefineSuccessChance: number; WeddingRing: number;
  ExpiryDate: bigint | null; Rental: { OwnerName: string; BindingFlags: number; ExpiryDate: bigint; RentalLocked: boolean } | null;
  IsShopItem: boolean; Sealed: { ExpiryDate: bigint; NextSealDate: bigint } | null; GMMade: boolean;
}

function readCount(r: Reader, elementSize: number): number {
  const n = r.readI32();
  if (n < 0 || n * elementSize > r.remaining) throw new Error(`count ${n} exceeds payload`);
  return n;
}

/** Stats(reader): i32 count, count x (u8 Stat, i32 value). */
function readStats(r: Reader): Record<number, number> {
  const n = readCount(r, 5);
  const out: Record<number, number> = {};
  for (let i = 0; i < n; i++) { const k = r.readU8(); out[k] = r.readI32(); }
  return out;
}

export function readUserItem(r: Reader): UserItem {
  const UniqueID = r.readU64(), ItemIndex = r.readI32(), CurrentDura = r.readU16(), MaxDura = r.readU16(), Count = r.readU16();
  const SoulBoundId = r.readI32();
  const bools = r.readU8();
  const slotCount = readCount(r, 1);
  const Slots: (UserItem | null)[] = [];
  for (let i = 0; i < slotCount; i++) Slots.push(r.readBool() ? null : readUserItem(r));
  const GemCount = r.readU16();
  const AddedStats = readStats(r);
  const AwakeType = r.readU8();
  const awakeCount = readCount(r, 1);
  const AwakeLevels: number[] = [];
  for (let i = 0; i < awakeCount; i++) AwakeLevels.push(r.readU8());
  const RefinedValue = r.readU8(), RefineAdded = r.readU8(), RefineSuccessChance = r.readI32(), WeddingRing = r.readI32();
  const ExpiryDate = r.readBool() ? r.readI64() : null;
  const Rental = r.readBool() ? { OwnerName: r.readString(), BindingFlags: r.readI16(), ExpiryDate: r.readI64(), RentalLocked: r.readBool() } : null;
  const IsShopItem = r.readBool();
  const Sealed = r.readBool() ? { ExpiryDate: r.readI64(), NextSealDate: r.readI64() } : null;
  const GMMade = r.readBool();
  return {
    UniqueID, ItemIndex, CurrentDura, MaxDura, Count, SoulBoundId, Identified: (bools & 1) !== 0, Cursed: (bools & 2) !== 0, Slots, GemCount,
    AddedStats, AwakeType, AwakeLevels, RefinedValue, RefineAdded, RefineSuccessChance, WeddingRing, ExpiryDate, Rental, IsShopItem, Sealed, GMMade,
  };
}

/** ClientMagic(reader) (Shared/Data/ClientData.cs). */
export interface ClientMagic {
  Name: string; Spell: number; BaseCost: number; LevelCost: number; Icon: number; Level1: number; Level2: number; Level3: number;
  Need1: number; Need2: number; Need3: number; Level: number; Key: number; Experience: number; Delay: bigint; Range: number; CastTime: bigint;
}

export function readClientMagic(r: Reader): ClientMagic {
  return {
    Name: r.readString(), Spell: r.readU8(), BaseCost: r.readU8(), LevelCost: r.readU8(), Icon: r.readU8(), Level1: r.readU8(), Level2: r.readU8(),
    Level3: r.readU8(), Need1: r.readU16(), Need2: r.readU16(), Need3: r.readU16(), Level: r.readU8(), Key: r.readU8(), Experience: r.readU16(),
    Delay: r.readI64(), Range: r.readU8(), CastTime: r.readI64(),
  };
}

function readU32List(r: Reader): number[] {
  const n = readCount(r, 4);
  const out: number[] = [];
  for (let i = 0; i < n; i++) out.push(r.readU32());
  return out;
}

/** ClientBuff(reader) (Shared/Data/ClientData.cs). ExpireTime is ms left (the client adds CMain.Time unless paused). */
export interface ClientBuff {
  Type: number; Visible: boolean; ObjectID: number; ExpireTime: bigint; Infinite: boolean; Paused: boolean; Stats: Record<number, number>; Values: number[];
}

function readClientBuff(r: Reader): ClientBuff {
  const Type = r.readU8(), Visible = r.readBool(), ObjectID = r.readU32(), ExpireTime = r.readI64(), Infinite = r.readBool(), Paused = r.readBool();
  const Stats = readStats(r);
  const n = readCount(r, 4);
  const Values: number[] = [];
  for (let i = 0; i < n; i++) Values.push(r.readI32());
  return { Type, Visible, ObjectID, ExpireTime, Infinite, Paused, Stats, Values };
}

/** `bool present, i32 length, length x (bool present, UserItem)` as UserInformation writes Inventory / Equipment. */
function readItemArray(r: Reader): (UserItem | null)[] | null {
  if (!r.readBool()) return null;
  const n = readCount(r, 1);
  const out: (UserItem | null)[] = [];
  for (let i = 0; i < n; i++) out.push(r.readBool() ? readUserItem(r) : null);
  return out;
}

/** ItemInfo(reader) packet layout (Shared/Data/ItemData.cs, version = int.MaxValue). */
export interface ItemInfo {
  Index: number; Name: string; Type: number; Grade: number; RequiredType: number; RequiredClass: number; RequiredGender: number; Set: number;
  Shape: number; Weight: number; Light: number; RequiredAmount: number; Image: number; Durability: number; StackSize: number; Price: number;
  StartItem: boolean; Effect: number; NeedIdentify: boolean; ShowGroupPickup: boolean; ClassBased: boolean; LevelBased: boolean; CanMine: boolean; GlobalDropNotify: boolean;
  /** BindMode / SpecialItemMode flags (i16). */
  Bind: number; Unique: number; RandomStatsId: number; CanFastRun: boolean; CanAwakening: boolean; Slots: number; Stats: Record<number, number>; ToolTip: string | null;
}

function readItemInfo(r: Reader): ItemInfo {
  const Index = r.readI32(), Name = r.readString(), Type = r.readU8(), Grade = r.readU8(), RequiredType = r.readU8(), RequiredClass = r.readU8();
  const RequiredGender = r.readU8(), Set = r.readU8(), Shape = r.readI16(), Weight = r.readU8(), Light = r.readU8(), RequiredAmount = r.readU8();
  const Image = r.readU16(), Durability = r.readU16(), StackSize = r.readU16(), Price = r.readU32(), StartItem = r.readBool(), Effect = r.readU8();
  const b = r.readU8();
  const Bind = r.readI16(), Unique = r.readI16(), RandomStatsId = r.readU8(), CanFastRun = r.readBool(), CanAwakening = r.readBool(), Slots = r.readU8();
  const Stats = readStats(r);
  const ToolTip = r.readBool() ? r.readString() : null;
  return {
    Index, Name, Type, Grade, RequiredType, RequiredClass, RequiredGender, Set, Shape, Weight, Light, RequiredAmount, Image, Durability, StackSize, Price,
    StartItem, Effect, NeedIdentify: (b & 1) !== 0, ShowGroupPickup: (b & 2) !== 0, ClassBased: (b & 4) !== 0, LevelBased: (b & 8) !== 0,
    CanMine: (b & 0x10) !== 0, GlobalDropNotify: (b & 0x20) !== 0, Bind, Unique, RandomStatsId, CanFastRun, CanAwakening, Slots, Stats, ToolTip,
  };
}

/** ClientIntelligentCreature(reader) (Shared/Data/IntelligentCreatureData.cs): CreatureRules, then Filter + its PickupGrade byte. */
export interface IntelligentCreatureRules {
  MinimalFullness: number; MousePickupEnabled: boolean; MousePickupRange: number; AutoPickupEnabled: boolean; AutoPickupRange: number;
  SemiAutoPickupEnabled: boolean; SemiAutoPickupRange: number; CanProduceBlackStone: boolean;
}
export interface IntelligentCreatureItemFilter {
  PetPickupAll: boolean; PetPickupGold: boolean; PetPickupWeapons: boolean; PetPickupArmours: boolean; PetPickupHelmets: boolean;
  PetPickupBoots: boolean; PetPickupBelts: boolean; PetPickupAccessories: boolean; PetPickupOthers: boolean; PickupGrade: number;
}
export interface ClientIntelligentCreature {
  PetType: number; Icon: number; CustomName: string; Fullness: number; SlotIndex: number; Expire: bigint; BlackstoneTime: bigint;
  petMode: number; CreatureRules: IntelligentCreatureRules; Filter: IntelligentCreatureItemFilter; MaintainFoodTime: bigint;
}

export function readClientIntelligentCreature(r: Reader): ClientIntelligentCreature {
  const PetType = r.readU8(), Icon = r.readI32(), CustomName = r.readString(), Fullness = r.readI32(), SlotIndex = r.readI32();
  const Expire = r.readI64(), BlackstoneTime = r.readI64(), petMode = r.readU8();
  const CreatureRules: IntelligentCreatureRules = {
    MinimalFullness: r.readI32(), MousePickupEnabled: r.readBool(), MousePickupRange: r.readI32(), AutoPickupEnabled: r.readBool(),
    AutoPickupRange: r.readI32(), SemiAutoPickupEnabled: r.readBool(), SemiAutoPickupRange: r.readI32(), CanProduceBlackStone: r.readBool(),
  };
  const Filter: IntelligentCreatureItemFilter = {
    PetPickupAll: r.readBool(), PetPickupGold: r.readBool(), PetPickupWeapons: r.readBool(), PetPickupArmours: r.readBool(), PetPickupHelmets: r.readBool(),
    PetPickupBoots: r.readBool(), PetPickupBelts: r.readBool(), PetPickupAccessories: r.readBool(), PetPickupOthers: r.readBool(), PickupGrade: r.readU8(),
  };
  return { PetType, Icon, CustomName, Fullness, SlotIndex, Expire, BlackstoneTime, petMode, CreatureRules, Filter, MaintainFoodTime: r.readI64() };
}

/** GameShopItem(reader, packet: true) (Shared/Data/ItemData.cs): the wire ctor carries the ItemInfo. Stock = the StockLevel the handler copies in. */
export interface GameShopItem {
  ItemIndex: number; GIndex: number; Info: ItemInfo; GoldPrice: number; CreditPrice: number; Count: number; Class: string; Category: string;
  Stock: number; iStock: boolean; Deal: boolean; TopItem: boolean; Date: bigint; CanBuyCredit: boolean; CanBuyGold: boolean;
}

function readGameShopItem(r: Reader): GameShopItem {
  return {
    ItemIndex: r.readI32(), GIndex: r.readI32(), Info: readItemInfo(r), GoldPrice: r.readU32(), CreditPrice: r.readU32(), Count: r.readU16(),
    Class: r.readString(), Category: r.readString(), Stock: r.readI32(), iStock: r.readBool(), Deal: r.readBool(), TopItem: r.readBool(),
    Date: r.readI64(), CanBuyCredit: r.readBool(), CanBuyGold: r.readBool(),
  };
}

function readCreatureList(r: Reader): ClientIntelligentCreature[] {
  const n = readCount(r, 60);
  const out: ClientIntelligentCreature[] = [];
  for (let i = 0; i < n; i++) out.push(readClientIntelligentCreature(r));
  return out;
}

/** ClientGTMap(reader) (Shared/Data/SharedData.cs): one guild-territory listing. */
export interface ClientGTMap { index: number; Name: string; Owner: string; Leader: string; Leader2: string; price: number; days: number; begin: number }

function readClientGTMap(r: Reader): ClientGTMap {
  return {
    index: r.readI32(), Name: r.readString(), Owner: r.readString(), Leader: r.readString(), Leader2: r.readString(),
    price: r.readI32(), days: r.readI32(), begin: r.readI32(),
  };
}

/** BaseStats(reader) (Shared/BaseStats.cs): S.BaseStatsInfo and S.HeroBaseStatsInfo. */
function readBaseStats(r: Reader): BaseStats {
  const Job = r.readU8();
  const n = readCount(r, 18);
  const Stats: BaseStat[] = [];
  for (let i = 0; i < n; i++) Stats.push({ Type: r.readU8(), FormulaType: r.readU8(), Base: r.readI32(), Gain: r.readF32(), GainRate: r.readF32(), Max: r.readI32() });
  return { Job, Stats, Caps: readStats(r) };
}

/** S.ObjectPlayer.ReadPacket; S.ObjectHero reads the same fields, then OwnerName. */
function readObjectPlayer(r: Reader) {
  return {
    ObjectID: r.readU32(), Name: r.readString(), GuildName: r.readString(), GuildRankName: r.readString(), NameColour: r.readI32(),
    Class: r.readU8(), Gender: r.readU8(), Level: r.readU16(), Location: readPoint(r), Direction: r.readU8(), Hair: r.readU8(), Light: r.readU8(),
    Weapon: r.readI16(), WeaponEffect: r.readI16(), Armour: r.readI16(), Poison: r.readU16(), Dead: r.readBool(), Hidden: r.readBool(),
    Effect: r.readU8(), WingEffect: r.readU8(), Extra: r.readBool(), MountType: r.readI16(), RidingMount: r.readBool(), Fishing: r.readBool(),
    TransformType: r.readI16(), ElementOrbEffect: r.readU32(), ElementOrbLvl: r.readU32(), ElementOrbMax: r.readU32(),
    Buffs: readBuffs(r), LevelEffects: r.readU16(),
  };
}

/** ClientQuestInfo(reader) (S.NewQuestInfo, S.GameDataChanged). */
function readClientQuestInfo(r: Reader): ClientQuestInfo {
  return {
    Index: r.readI32(), NPCIndex: r.readU32(), Name: r.readString(), Group: r.readString(), Description: readStrings(r), TaskDescription: readStrings(r),
    ReturnDescription: readStrings(r), CompletionDescription: readStrings(r), MinLevelNeeded: r.readI32(), MaxLevelNeeded: r.readI32(), QuestNeeded: r.readI32(),
    ClassNeeded: r.readU8(), Type: r.readU8(), TimeLimitInSeconds: r.readI32(), RewardGold: r.readU32(), RewardExp: r.readU32(), RewardCredit: r.readU32(),
    RewardsFixedItem: readQuestRewards(r), RewardsSelectItem: readQuestRewards(r), FinishNPCIndex: r.readU32(),
  } as ClientQuestInfo;
}

/** ClientMonsterInfo(reader) (S.NewMonsterInfo, S.GameDataChanged). */
function readClientMonsterInfo(r: Reader): ClientMonsterInfo {
  return {
    Index: r.readI32(), Name: r.readString(), GameName: r.readString(), Image: r.readU16(), AI: r.readU8(), Effect: r.readU8(), Level: r.readU16(),
    ViewRange: r.readU8(), CoolEye: r.readU8(), Light: r.readU8(), AttackSpeed: r.readU16(), MoveSpeed: r.readU16(), Experience: r.readU32(),
    CanPush: r.readBool(), CanTame: r.readBool(), AutoRev: r.readBool(), Undead: r.readBool(), CanRecall: r.readBool(), Stats: readStats(r),
  } as ClientMonsterInfo;
}

/** ClientHeroInformation(reader) (Shared/Data/ClientData.cs): a hero in the ManageHeroes list / NewHeroInfo. */
export interface ClientHeroInformation { Index: number; Name: string; Level: number; Class: number; Gender: number }

function readClientHeroInformation(r: Reader): ClientHeroInformation {
  return { Index: r.readI32(), Name: r.readString(), Level: r.readU16(), Class: r.readU8(), Gender: r.readU8() };
}

/** GuildRank(reader) / GuildMember(reader) packet layout (Shared/Data/GuildData.cs, offline = false). */
export interface GuildMember { Name: string; Id: number; LastLogin: bigint; hasvoted: boolean; Online: boolean }
/** GuildStorageItem (Shared/Data/GuildData.cs). */
export interface GuildStorageItem { Item: UserItem; UserId: bigint }
/** GuildBuff(reader) (Id, Active, ActiveTimeRemaining in minutes) and GuildBuffInfo(reader) (Shared/Data/GuildData.cs). */
/** BaseStats(reader) (Shared/BaseStats.cs): per-class level formulas + caps. Gain / GainRate are floats (f32). */
export interface BaseStat { Type: number; FormulaType: number; Base: number; Gain: number; GainRate: number; Max: number }
export interface BaseStats { Job: number; Stats: BaseStat[]; Caps: Record<number, number> }
export interface GuildBuff { Id: number; Active: boolean; ActiveTimeRemaining: number }
export interface GuildBuffInfo {
  Id: number; Icon: number; Name: string; LevelRequirement: number; PointsRequirement: number; TimeLimit: number; ActivationCost: number; Stats: Record<number, number>;
}
/** ClientMonsterInfo(reader) (Shared/Data/MonsterData.cs). */
export interface ClientMonsterInfo {
  Index: number; Name: string; GameName: string; Image: number; AI: number; Effect: number; Level: number; ViewRange: number; CoolEye: number; Light: number;
  AttackSpeed: number; MoveSpeed: number; Experience: number; CanPush: boolean; CanTame: boolean; AutoRev: boolean; Undead: boolean; CanRecall: boolean;
  Stats: Record<number, number>;
}
/** ClientNPCInfo(reader) (Shared/Data/ClientData.cs); Icon falls back to BigMapIcon like the C# ctor. */
export interface ClientNPCInfo {
  Index: number; FileName: string; Name: string; MapIndex: number; Location: WirePoint; Image: number; Rate: number; ShowOnBigMap: boolean;
  BigMapIcon: number; ObjectID: number; Icon: number; CanTeleportTo: boolean;
}
export interface ClientMovementInfo { Destination: number; Title: string; Location: WirePoint; Icon: number }
export interface ClientMapInfo { Title: string; Width: number; Height: number; BigMap: number; Movements: ClientMovementInfo[]; NPCs: ClientNPCInfo[] }
export interface WorldMapIcon { ImageIndex: number; Title: string; MapIndex: number }

function readClientNPCInfo(r: Reader): ClientNPCInfo {
  const info: ClientNPCInfo = {
    Index: r.readI32(), FileName: r.readString(), Name: r.readString(), MapIndex: r.readI32(), Location: readPoint(r), Image: r.readU16(), Rate: r.readU16(),
    ShowOnBigMap: r.readBool(), BigMapIcon: r.readI32(), ObjectID: r.readU32(), Icon: r.readI32(), CanTeleportTo: r.readBool(),
  };
  if (info.Icon === 0 && info.BigMapIcon !== 0) info.Icon = info.BigMapIcon;
  return info;
}

/** ClientRecipeInfo(reader) (Shared/Data/ClientData.cs). */
export interface ClientRecipeInfo { Gold: number; Chance: number; Item: UserItem; Tools: UserItem[]; Ingredients: UserItem[] }
/** ClientAuction(reader): ConsignmentDate is DateTime.ToBinary; ItemType = MarketItemType. */
export interface ClientAuction { AuctionID: bigint; Item: UserItem; Seller: string; Price: number; ConsignmentDate: bigint; ItemType: number }
/** ItemRentalInformation(reader). */
export interface ItemRentalInformation { ItemId: bigint; ItemName: string; RentingPlayerName: string; ItemReturnDate: bigint }

function readUserItems(r: Reader): UserItem[] {
  const n = readCount(r, 1);
  const out: UserItem[] = [];
  for (let i = 0; i < n; i++) out.push(readUserItem(r));
  return out;
}

function readAuctions(r: Reader): ClientAuction[] {
  const n = readCount(r, 1);
  const out: ClientAuction[] = [];
  for (let i = 0; i < n; i++) out.push({ AuctionID: r.readU64(), Item: readUserItem(r), Seller: r.readString(), Price: r.readU32(), ConsignmentDate: r.readI64(), ItemType: r.readU8() });
  return out;
}

/** ClientQuestProgress(reader) (Shared/Data/ClientData.cs). */
export interface ClientQuestProgress { Id: number; TaskList: string[]; Taken: boolean; Completed: boolean; New: boolean }
/** QuestItemReward(reader): an ItemInfo + count. */
export interface QuestItemReward { Item: ItemInfo; Count: number }
/** ClientQuestInfo(reader). ClassNeeded = RequiredClass, Type = QuestType. */
export interface ClientQuestInfo {
  Index: number; NPCIndex: number; Name: string; Group: string; Description: string[]; TaskDescription: string[]; ReturnDescription: string[];
  CompletionDescription: string[]; MinLevelNeeded: number; MaxLevelNeeded: number; QuestNeeded: number; ClassNeeded: number; Type: number;
  TimeLimitInSeconds: number; RewardGold: number; RewardExp: number; RewardCredit: number; RewardsFixedItem: QuestItemReward[]; RewardsSelectItem: QuestItemReward[];
  FinishNPCIndex: number;
}
/** ClientMail(reader). DateSent is DateTime.ToBinary. */
export interface ClientMail {
  MailID: bigint; SenderName: string; Message: string; Opened: boolean; Locked: boolean; CanReply: boolean; Collected: boolean; DateSent: bigint; Gold: number; Items: UserItem[];
}
/** ClientFriend(reader). */
export interface ClientFriend { Index: number; Name: string; Memo: string; Blocked: boolean; Online: boolean }
/** RankCharacterInfo(reader) (Shared/Data/SharedData.cs). */
export interface RankCharacterInfo { PlayerId: bigint; Name: string; level: number; Class: number }

function readStrings(r: Reader): string[] {
  const n = readCount(r, 1);
  const out: string[] = [];
  for (let i = 0; i < n; i++) out.push(r.readString());
  return out;
}

function readQuestRewards(r: Reader): QuestItemReward[] {
  const n = readCount(r, 1);
  const out: QuestItemReward[] = [];
  for (let i = 0; i < n; i++) out.push({ Item: readItemInfo(r), Count: r.readU16() });
  return out;
}

export interface GuildRank { Name: string; Options: number; Index: number; Members: GuildMember[] }

function readGuildRank(r: Reader): GuildRank {
  const Name = r.readString(), Options = r.readU8(), Index = r.readI32();
  const n = readCount(r, 1);
  const Members: GuildMember[] = [];
  for (let i = 0; i < n; i++) Members.push({ Name: r.readString(), Id: r.readI32(), LastLogin: r.readI64(), hasvoted: r.readBool(), Online: r.readBool() });
  return { Name, Options, Index, Members };
}

const decoders = {
  KeepAlive: (r: Reader) => ({ Time: r.readI64() }),
  Connected: (_r: Reader) => ({}),
  ClientVersion: (r: Reader) => ({ Result: r.readU8() }),
  Disconnect: (r: Reader) => ({ Reason: r.readU8() }),
  NewAccount: (r: Reader) => ({ Result: r.readU8() }),
  Login: (r: Reader) => ({ Result: r.readU8() }),
  LoginBanned: (r: Reader) => ({ Reason: r.readString(), ExpiryDate: r.readI64() }),
  LoginSuccess: (r: Reader) => {
    const n = r.readI32();
    const Characters: SelectInfo[] = [];
    for (let i = 0; i < n; i++) Characters.push(readSelectInfo(r));
    return { Characters };
  },
  NewCharacter: (r: Reader) => ({ Result: r.readU8() }),
  NewCharacterSuccess: (r: Reader) => ({ CharInfo: readSelectInfo(r) }),
  StartGame: (r: Reader) => ({ Result: r.readU8(), Resolution: r.readI32() }),
  MapInformation: (r: Reader) => {
    const MapIndex = r.readI32(), FileName = r.readString(), Title = r.readString(), MiniMap = r.readU16(), BigMap = r.readU16();
    const Lights = r.readU8();
    const bools = r.readU8();
    const MapDarkLight = r.readU8(), Music = r.readU16(), WeatherParticles = r.readU16();
    return { MapIndex, FileName, Title, MiniMap, BigMap, Music, Lights, Lightning: (bools & 1) !== 0, Fire: (bools & 2) !== 0, MapDarkLight, WeatherParticles };
  },
  MapChanged: (r: Reader) => ({
    MapIndex: r.readI32(), FileName: r.readString(), Title: r.readString(), MiniMap: r.readU16(), BigMap: r.readU16(), Lights: r.readU8(),
    Location: readPoint(r), Direction: r.readU8(), MapDarkLight: r.readU8(), Music: r.readU16(), Weather: r.readU16(),
  }),
  UserInformation: (r: Reader) => ({
    ObjectID: r.readU32(), RealId: r.readU32(), Name: r.readString(), GuildName: r.readString(), GuildRank: r.readString(), NameColour: r.readI32(),
    Class: r.readU8(), Gender: r.readU8(), Level: r.readU16(), Location: readPoint(r), Direction: r.readU8(), Hair: r.readU8(),
    HP: r.readI32(), MP: r.readI32(), Experience: r.readI64(), MaxExperience: r.readI64(),
    LevelEffects: r.readU16(), HasHero: r.readBool(), HeroBehaviour: r.readU8(),
    Inventory: readItemArray(r), Equipment: readItemArray(r), QuestInventory: readItemArray(r),
    Gold: r.readU32(), Credit: r.readU32(), HasExpandedStorage: r.readBool(), HasStoragePassword: r.readBool(), RequireStoragePassword: r.readBool(),
    StoragePasswordLastSet: r.readI64(), ExpandedStorageExpiryTime: r.readI64(),
    Magics: (() => { const n = readCount(r, 1); const out: ClientMagic[] = []; for (let i = 0; i < n; i++) out.push(readClientMagic(r)); return out; })(),
    IntelligentCreatures: readCreatureList(r),
    SummonedCreatureType: r.readU8(), CreatureSummoned: r.readBool(), AllowObserve: r.readBool(), Observer: r.readBool(),
  }),
  // — Web admin LiveDb push (GameScene.GameDataChanged): upserted game data, sent in chunks; Last = the final chunk —
  GameDataChanged: (r: Reader) => {
    const list = <T>(min: number, read: () => T): T[] => { const n = readCount(r, min); const out: T[] = []; for (let i = 0; i < n; i++) out.push(read()); return out; };
    return {
      Items: list(1, () => readItemInfo(r)),
      Monsters: list(1, () => readClientMonsterInfo(r)),
      Npcs: list(1, () => readClientNPCInfo(r)),
      Quests: list(1, () => readClientQuestInfo(r)),
      Magics: list(1, () => readClientMagic(r)),
      HeroMagics: list(1, () => readClientMagic(r)),
      GameShop: list(1, () => ({ Item: readGameShopItem(r), StockLevel: r.readI32() })),
      GameShopRemoved: list(4, () => r.readI32()),
      MapInfoInvalidated: list(4, () => r.readI32()),
      Last: r.readBool(),
    };
  },
  // — Heroes (GameScene.ObjectHero / HeroInformation / Hero* / ManageHeroes / ChangeHero ...; MirScene.NewHeroInfo) —
  ObjectHero: (r: Reader) => ({ ...readObjectPlayer(r), OwnerName: r.readString() }),
  NewHeroInfo: (r: Reader) => ({ Info: readClientHeroInformation(r), StorageIndex: r.readI32() }),
  TakeBackHeroItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  TransferHeroItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  HeroHealthChanged: (r: Reader) => ({ HP: r.readI32(), MP: r.readI32() }),
  GainHeroExperience: (r: Reader) => ({ Amount: r.readU32() }),
  HeroLevelChanged: (r: Reader) => ({ Level: r.readU16(), Experience: r.readI64(), MaxExperience: r.readI64() }),
  HeroBaseStatsInfo: (r: Reader) => ({ Stats: readBaseStats(r) }),
  /** Indexed by MirClass. */
  HeroCreateRequest: (r: Reader) => {
    const n = readCount(r, 1);
    const CanCreateClass: boolean[] = [];
    for (let i = 0; i < n; i++) CanCreateClass.push(r.readBool());
    return { CanCreateClass };
  },
  /** Result: 0 disabled, 1 bad name, 2 bad gender, 3 bad class, 4 too many heroes, 5 name taken, 6 no bag space, 10 created. */
  NewHero: (r: Reader) => ({ Result: r.readU8() }),
  /** Its own ReadPacket (not UserInformation's): no guild, location, gold or storage fields. */
  HeroInformation: (r: Reader) => ({
    ObjectID: r.readU32(), Name: r.readString(), Class: r.readU8(), Gender: r.readU8(), Level: r.readU16(), Hair: r.readU8(),
    HP: r.readI32(), MP: r.readI32(), Experience: r.readI64(), MaxExperience: r.readI64(),
    Inventory: readItemArray(r), Equipment: readItemArray(r),
    Magics: (() => { const n = readCount(r, 1); const out: ClientMagic[] = []; for (let i = 0; i < n; i++) out.push(readClientMagic(r)); return out; })(),
    AutoPot: r.readBool(), AutoHPPercent: r.readU8(), AutoMPPercent: r.readU8(), HPItemIndex: r.readI32(), MPItemIndex: r.readI32(),
  }),
  UpdateHeroSpawnState: (r: Reader) => ({ State: r.readU8() }),
  UnlockHeroAutoPot: (_r: Reader) => ({}),
  SetAutoPotValue: (r: Reader) => ({ Stat: r.readU8(), Value: r.readU32() }),
  SetAutoPotItem: (r: Reader) => ({ Grid: r.readU8(), ItemIndex: r.readI32() }),
  SetHeroBehaviour: (r: Reader) => ({ Behaviour: r.readU8() }),
  /** Heroes = the storage list (null entries are empty slots). */
  ManageHeroes: (r: Reader) => {
    const MaximumCount = r.readI32();
    const CurrentHero = r.readBool() ? readClientHeroInformation(r) : null;
    let Heroes: (ClientHeroInformation | null)[] | null = null;
    if (r.readBool()) {
      const n = readCount(r, 1);
      Heroes = [];
      for (let i = 0; i < n; i++) Heroes.push(r.readBool() ? readClientHeroInformation(r) : null);
    }
    return { MaximumCount, CurrentHero, Heroes };
  },
  ChangeHero: (r: Reader) => ({ FromIndex: r.readI32() }),
  // — Mounts, transform, fishing, reincarnation (GameScene.MountUpdate / TransformUpdate / FishingUpdate / RequestReincarnation) —
  MountUpdate: (r: Reader) => ({ ObjectID: r.readU32(), MountType: r.readI16(), RidingMount: r.readBool() }),
  TransformUpdate: (r: Reader) => ({ ObjectID: r.readU32(), TransformType: r.readI16() }),
  FishingUpdate: (r: Reader) => ({
    ObjectID: r.readU32(), Fishing: r.readBool(), ProgressPercent: r.readI32(), ChancePercent: r.readI32(), FishingPoint: readPoint(r), FoundFish: r.readBool(),
  }),
  RequestReincarnation: (_r: Reader) => ({}),
  CancelReincarnation: (_r: Reader) => ({}),
  // — Game shop and credit (GameScene.GameShopUpdate / GameShopStock / GainedCredit / LoseCredit) —
  GameShopInfo: (r: Reader) => ({ Item: readGameShopItem(r), StockLevel: r.readI32() }),
  GameShopStock: (r: Reader) => ({ GIndex: r.readI32(), StockLevel: r.readI32() }),
  GainedCredit: (r: Reader) => ({ Credit: r.readU32() }),
  LoseCredit: (r: Reader) => ({ Credit: r.readU32() }),
  // — Guild territories (GameScene.GuildTerritoryPage) —
  GuildTerritoryPage: (r: Reader) => {
    const length = r.readI32();
    const n = readCount(r, 20); // min ClientGTMap size: 4 empty strings
    const Listings: ClientGTMap[] = [];
    for (let i = 0; i < n; i++) Listings.push(readClientGTMap(r));
    return { length, Listings };
  },
  // — Intelligent creatures (GameScene.NewIntelligentCreature / UpdateIntelligentCreatureList / ...): codec only, UI in a later pass —
  NewIntelligentCreature: (r: Reader) => ({ Creature: readClientIntelligentCreature(r) }),
  UpdateIntelligentCreatureList: (r: Reader) => ({
    CreatureList: readCreatureList(r), CreatureSummoned: r.readBool(), SummonedCreatureType: r.readU8(), PearlCount: r.readI32(),
  }),
  IntelligentCreatureEnableRename: (_r: Reader) => ({}),
  IntelligentCreaturePickup: (r: Reader) => ({ ObjectID: r.readU32() }),
  // — Group (GameScene.SwitchGroup / DeleteGroup / DeleteMember / GroupInvite / AddMember / GroupMembersMap / SendMemberLocation) —
  SwitchGroup: (r: Reader) => ({ AllowGroup: r.readBool() }),
  DeleteGroup: (_r: Reader) => ({}),
  DeleteMember: (r: Reader) => ({ Name: r.readString() }),
  GroupInvite: (r: Reader) => ({ Name: r.readString() }),
  AddMember: (r: Reader) => ({ Name: r.readString() }),
  GroupMembersMap: (r: Reader) => ({ PlayerName: r.readString(), PlayerMap: r.readString() }),
  SendMemberLocation: (r: Reader) => ({ MemberName: r.readString(), MemberLocation: readPoint(r) }),
  /** GameScene.Opendoor -> MapControl.OpenDoor. */
  Opendoor: (r: Reader) => ({ DoorIndex: r.readU8(), Close: r.readBool() }),
  // — Buffs, poison, hiding, sounds (GameScene.AddBuff / RemoveBuff / PauseBuff / ObjectHidden / Poisoned / ObjectPoisoned / PlaySound) —
  AddBuff: (r: Reader) => ({ Buff: readClientBuff(r) }),
  RemoveBuff: (r: Reader) => ({ Type: r.readU8(), ObjectID: r.readU32() }),
  PauseBuff: (r: Reader) => ({ Type: r.readU8(), ObjectID: r.readU32(), Paused: r.readBool() }),
  ObjectHidden: (r: Reader) => ({ ObjectID: r.readU32(), Hidden: r.readBool() }),
  Poisoned: (r: Reader) => ({ Poison: r.readU16() }),
  ObjectPoisoned: (r: Reader) => ({ ObjectID: r.readU32(), Poison: r.readU16() }),
  PlaySound: (r: Reader) => ({ Sound: r.readI32() }),
  // — Magic (GameScene.NewMagic / Magic / ObjectMagic / ...) —
  NewMagic: (r: Reader) => ({ Magic: readClientMagic(r), Hero: r.readBool() }),
  RemoveMagic: (r: Reader) => ({ PlaceId: r.readI32() }),
  MagicLeveled: (r: Reader) => ({ ObjectID: r.readU32(), Spell: r.readU8(), Level: r.readU8(), Experience: r.readU16() }),
  Magic: (r: Reader) => ({ Spell: r.readU8(), TargetID: r.readU32(), Target: readPoint(r), Cast: r.readBool(), Level: r.readU8(), SecondaryTargetIDs: readU32List(r) }),
  MagicDelay: (r: Reader) => ({ ObjectID: r.readU32(), Spell: r.readU8(), Delay: r.readI64() }),
  MagicCast: (r: Reader) => ({ Spell: r.readU8() }),
  ObjectMagic: (r: Reader) => ({
    ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Spell: r.readU8(), TargetID: r.readU32(), Target: readPoint(r),
    Cast: r.readBool(), Level: r.readU8(), SelfBroadcast: r.readBool(), SecondaryTargetIDs: readU32List(r),
  }),
  ObjectEffect: (r: Reader) => ({ ObjectID: r.readU32(), Effect: r.readU8(), EffectType: r.readU32(), DelayTime: r.readU32(), Time: r.readU32() }),
  ObjectProjectile: (r: Reader) => ({ Spell: r.readU8(), Source: r.readU32(), Destination: r.readU32() }),
  SpellToggle: (r: Reader) => ({ ObjectID: r.readU32(), Spell: r.readU8(), CanUse: r.readBool() }),
  MapEffect: (r: Reader) => ({ Location: readPoint(r), Effect: r.readU8(), Value: r.readU8() }),
  ObjectSpell: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Spell: r.readU8(), Direction: r.readU8(), Param: r.readBool() }),
  // — Items (GameScene.MoveItem / EquipItem / RemoveItem / ...) —
  MoveItem: (r: Reader) => ({ Grid: r.readU8(), From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  EquipItem: (r: Reader) => ({ Grid: r.readU8(), UniqueID: r.readU64(), To: r.readI32(), Success: r.readBool() }),
  MergeItem: (r: Reader) => ({ GridFrom: r.readU8(), GridTo: r.readU8(), IDFrom: r.readU64(), IDTo: r.readU64(), Success: r.readBool() }),
  RemoveItem: (r: Reader) => ({ Grid: r.readU8(), UniqueID: r.readU64(), To: r.readI32(), Success: r.readBool() }),
  SplitItem: (r: Reader) => ({ Item: r.readBool() ? readUserItem(r) : null, Grid: r.readU8() }),
  SplitItem1: (r: Reader) => ({ Grid: r.readU8(), UniqueID: r.readU64(), Count: r.readU16(), Success: r.readBool() }),
  UseItem: (r: Reader) => ({ UniqueID: r.readU64(), Success: r.readBool(), Grid: r.readU8() }),
  DropItem: (r: Reader) => ({ UniqueID: r.readU64(), Count: r.readU16(), HeroItem: r.readBool(), Success: r.readBool() }),
  GainedItem: (r: Reader) => ({ Item: readUserItem(r) }),
  DeleteItem: (r: Reader) => ({ UniqueID: r.readU64(), Count: r.readU16() }),
  RefreshItem: (r: Reader) => ({ Item: readUserItem(r) }),
  DuraChanged: (r: Reader) => ({ UniqueID: r.readU64(), CurrentDura: r.readU16() }),
  ItemRepaired: (r: Reader) => ({ UniqueID: r.readU64(), MaxDura: r.readU16(), CurrentDura: r.readU16() }),
  GainedGold: (r: Reader) => ({ Gold: r.readU32() }),
  LoseGold: (r: Reader) => ({ Gold: r.readU32() }),
  LevelChanged: (r: Reader) => ({ Level: r.readU16(), Experience: r.readI64(), MaxExperience: r.readI64() }),
  NewItemInfo: (r: Reader) => ({ Info: readItemInfo(r) }),
  PlayerUpdate: (r: Reader) => ({ ObjectID: r.readU32(), Light: r.readU8(), Weapon: r.readI16(), WeaponEffect: r.readI16(), Armour: r.readI16(), WingEffect: r.readU8() }),
  UserLocation: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  TimeOfDay: (r: Reader) => ({ Lights: r.readU8() }),
  ObjectPlayer: (r: Reader) => readObjectPlayer(r),
  ObjectMonster: (r: Reader) => ({
    ObjectID: r.readU32(), Name: r.readString(), NameColour: r.readI32(), Location: readPoint(r), Image: r.readU16(), Direction: r.readU8(),
    Effect: r.readU8(), AI: r.readU8(), Light: r.readU8(), Dead: r.readBool(), Skeleton: r.readBool(), Poison: r.readU16(), Hidden: r.readBool(),
    ShockTime: r.readI64(), BindingShotCenter: r.readBool(), Extra: r.readBool(), ExtraByte: r.readU8(), MasterObjectId: r.readU32(),
    Rarity: r.readU8(), Buffs: readBuffs(r),
  }),
  ObjectNPC: (r: Reader) => {
    const ObjectID = r.readU32(), Name = r.readString(), NameColour = r.readI32(), Image = r.readU16(), Colour = r.readI32();
    const Location = readPoint(r), Direction = r.readU8();
    const n = r.readI32();
    if (n < 0 || n * 4 > r.remaining) throw new Error("quest count");
    const QuestIDs: number[] = [];
    for (let i = 0; i < n; i++) QuestIDs.push(r.readI32());
    return { ObjectID, Name, NameColour, Image, Colour, Location, Direction, QuestIDs };
  },
  ObjectRemove: (r: Reader) => ({ ObjectID: r.readU32() }),
  // — Section 4: social (GameScene handlers of the same names) —
  /** QuestState: 0 add, 1 update, 2 remove. */
  ChangeQuest: (r: Reader) => ({
    Quest: { Id: r.readI32(), TaskList: readStrings(r), Taken: r.readBool(), Completed: r.readBool(), New: r.readBool() } as ClientQuestProgress,
    QuestState: r.readU8(), TrackQuest: r.readBool(),
  }),
  CompleteQuest: (r: Reader) => {
    const n = readCount(r, 4);
    const CompletedQuests: number[] = [];
    for (let i = 0; i < n; i++) CompletedQuests.push(r.readI32());
    return { CompletedQuests };
  },
  ShareQuest: (r: Reader) => ({ QuestIndex: r.readI32(), SharerName: r.readString() }),
  NewQuestInfo: (r: Reader) => ({ Info: readClientQuestInfo(r) }),
  GainedQuestItem: (r: Reader) => ({ Item: readUserItem(r) }),
  DeleteQuestItem: (r: Reader) => ({ UniqueID: r.readU64(), Count: r.readU16() }),
  ReceiveMail: (r: Reader) => {
    const n = readCount(r, 1);
    const Mail: ClientMail[] = [];
    for (let i = 0; i < n; i++) {
      Mail.push({
        MailID: r.readU64(), SenderName: r.readString(), Message: r.readString(), Opened: r.readBool(), Locked: r.readBool(), CanReply: r.readBool(),
        Collected: r.readBool(), DateSent: r.readI64(), Gold: r.readU32(), Items: readUserItems(r),
      });
    }
    return { Mail };
  },
  MailLockedItem: (r: Reader) => ({ UniqueID: r.readU64(), Locked: r.readBool() }),
  MailSendRequest: (_r: Reader) => ({}),
  MailSent: (r: Reader) => ({ Result: r.readI8() }),
  ParcelCollected: (r: Reader) => ({ Result: r.readI8() }),
  MailCost: (r: Reader) => ({ Cost: r.readU32() }),
  FriendUpdate: (r: Reader) => {
    const n = readCount(r, 1);
    const Friends: ClientFriend[] = [];
    for (let i = 0; i < n; i++) Friends.push({ Index: r.readI32(), Name: r.readString(), Memo: r.readString(), Blocked: r.readBool(), Online: r.readBool() });
    return { Friends };
  },
  MarriageRequest: (r: Reader) => ({ Name: r.readString() }),
  DivorceRequest: (r: Reader) => ({ Name: r.readString() }),
  LoverUpdate: (r: Reader) => ({ Name: r.readString(), Date: r.readI64(), MapName: r.readString(), MarriedDays: r.readI16() }),
  MentorRequest: (r: Reader) => ({ Name: r.readString(), Level: r.readU16() }),
  MentorUpdate: (r: Reader) => ({ Name: r.readString(), Level: r.readU16(), Online: r.readBool(), MenteeEXP: r.readI64() }),
  PlayerInspect: (r: Reader) => {
    const Name = r.readString(), GuildName = r.readString(), GuildRank = r.readString();
    const n = readCount(r, 1);
    const Equipment: (UserItem | null)[] = [];
    for (let i = 0; i < n; i++) Equipment.push(r.readBool() ? readUserItem(r) : null);
    return {
      Name, GuildName, GuildRank, Equipment, Class: r.readU8(), Gender: r.readU8(), Hair: r.readU8(), Level: r.readU16(), LoverName: r.readString(),
      AllowObserve: r.readBool(), IsHero: r.readBool(),
    };
  },
  AllowObserve: (r: Reader) => ({ Allow: r.readBool() }),
  Rankings: (r: Reader) => {
    const RankType = r.readU8(), MyRank = r.readI32();
    let n = readCount(r, 1);
    const ListingDetails: RankCharacterInfo[] = [];
    for (let i = 0; i < n; i++) ListingDetails.push({ PlayerId: r.readI64(), Name: r.readString(), level: r.readI32(), Class: r.readU8() });
    n = readCount(r, 8);
    const Listings: bigint[] = [];
    for (let i = 0; i < n; i++) Listings.push(r.readI64());
    return { RankType, MyRank, ListingDetails, Listings, Count: r.readI32() };
  },
  // — Section 3: NPC services (GameScene handlers of the same names) —
  NPCStorage: (_r: Reader) => ({}),
  /** `bool present, i32 length, length x (bool present, UserItem)`. */
  UserStorage: (r: Reader) => ({ Storage: readItemArray(r) }),
  StoreItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  TakeBackItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  /** Result: 0 success, 1 bad password, 2 wrong password, 3 not available, 4 no password set. */
  StorageUnlockResult: (r: Reader) => ({ Result: r.readU8(), HasPassword: r.readBool() }),
  /** Result: 0 not available, 1 bad current, 2 wrong current, 3 bad new, 4 success, 5 no password set. */
  StoragePasswordResult: (r: Reader) => ({ Result: r.readU8(), Removing: r.readBool(), HasPassword: r.readBool(), LastSetTime: r.readI64() }),
  ResizeInventory: (r: Reader) => ({ Size: r.readI32() }),
  ResizeStorage: (r: Reader) => ({ Size: r.readI32(), HasExpandedStorage: r.readBool(), ExpiryTime: r.readI64() }),
  NPCRefine: (r: Reader) => ({ Rate: r.readF32(), Refining: r.readBool() }),
  NPCCheckRefine: (_r: Reader) => ({}),
  NPCCollectRefine: (r: Reader) => ({ Success: r.readBool() }),
  DepositRefineItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  RetrieveRefineItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  RefineItem: (r: Reader) => ({ UniqueID: r.readU64() }),
  RefineCancel: (r: Reader) => ({ Unlock: r.readBool() }),
  NPCReplaceWedRing: (r: Reader) => ({ Rate: r.readF32() }),
  CraftItem: (r: Reader) => ({ Success: r.readBool() }),
  NewRecipeInfo: (r: Reader) => ({
    Info: { Gold: r.readU32(), Chance: r.readU8(), Item: readUserItem(r), Tools: readUserItems(r), Ingredients: readUserItems(r) } as ClientRecipeInfo,
  }),
  NPCAwakening: (_r: Reader) => ({}),
  /** Null arrays when the leading bool is false; an absent entry keeps its count slot 0 (C# byte default). */
  AwakeningNeedMaterials: (r: Reader) => {
    if (!r.readBool()) return { Materials: null as (ItemInfo | null)[] | null, MaterialsCount: null as number[] | null };
    const n = readCount(r, 1);
    const Materials: (ItemInfo | null)[] = [], MaterialsCount: number[] = [];
    for (let i = 0; i < n; i++) {
      if (!r.readBool()) { Materials.push(null); MaterialsCount.push(0); continue; }
      Materials.push(readItemInfo(r));
      MaterialsCount.push(r.readU8());
    }
    return { Materials, MaterialsCount };
  },
  AwakeningLockedItem: (r: Reader) => ({ UniqueID: r.readU64(), Locked: r.readBool() }),
  Awakening: (r: Reader) => ({ result: r.readI32(), removeID: r.readI64() }),
  NPCDisassemble: (_r: Reader) => ({}),
  NPCDowngrade: (_r: Reader) => ({}),
  NPCReset: (_r: Reader) => ({}),
  NPCPearlGoods: (r: Reader) => ({ List: readUserItems(r), Rate: r.readF32(), Type: r.readU8() }),
  NPCConsign: (_r: Reader) => ({}),
  NPCMarket: (r: Reader) => ({ Listings: readAuctions(r), Pages: r.readI32(), UserMode: r.readBool() }),
  NPCMarketPage: (r: Reader) => ({ Listings: readAuctions(r) }),
  ConsignItem: (r: Reader) => ({ UniqueID: r.readU64(), Success: r.readBool() }),
  /** Reason: 0 dead, 1 not at the TrustMerchant, 2 sold, 3 expired, 4 gold, 5 bag space, 6 own item, 7 too far, 8 too much gold. */
  MarketFail: (r: Reader) => ({ Reason: r.readU8() }),
  MarketSuccess: (r: Reader) => ({ Message: r.readString() }),
  RemoveSlotItem: (r: Reader) => ({ Grid: r.readU8(), GridTo: r.readU8(), UniqueID: r.readU64(), To: r.readI32(), Success: r.readBool() }),
  ItemSlotSizeChanged: (r: Reader) => ({ UniqueID: r.readU64(), SlotSize: r.readI32() }),
  EquipSlotItem: (r: Reader) => ({ Grid: r.readU8(), UniqueID: r.readU64(), To: r.readI32(), GridTo: r.readU8(), Success: r.readBool() }),
  ItemSealChanged: (r: Reader) => ({ UniqueID: r.readU64(), ExpiryDate: r.readI64() }),
  CombineItem: (r: Reader) => ({ Grid: r.readU8(), IDFrom: r.readU64(), IDTo: r.readU64(), Success: r.readBool(), Destroy: r.readBool() }),
  ItemUpgraded: (r: Reader) => ({ Item: readUserItem(r) }),
  GetRentedItems: (r: Reader) => {
    const n = readCount(r, 1);
    const RentedItems: ItemRentalInformation[] = [];
    for (let i = 0; i < n; i++) RentedItems.push({ ItemId: r.readU64(), ItemName: r.readString(), RentingPlayerName: r.readString(), ItemReturnDate: r.readI64() });
    return { RentedItems };
  },
  ItemRentalRequest: (r: Reader) => ({ Name: r.readString(), Renting: r.readBool() }),
  ItemRentalFee: (r: Reader) => ({ Amount: r.readU32() }),
  ItemRentalPeriod: (r: Reader) => ({ Days: r.readU32() }),
  DepositRentalItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  RetrieveRentalItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  UpdateRentalItem: (r: Reader) => { const HasData = r.readBool(); return { HasData, LoanItem: HasData ? readUserItem(r) : null }; },
  CancelItemRental: (_r: Reader) => ({}),
  ItemRentalLock: (r: Reader) => ({ Success: r.readBool(), GoldLocked: r.readBool(), ItemLocked: r.readBool() }),
  ItemRentalPartnerLock: (r: Reader) => ({ GoldLocked: r.readBool(), ItemLocked: r.readBool() }),
  CanConfirmItemRental: (_r: Reader) => ({}),
  ConfirmItemRental: (_r: Reader) => ({}),
  // — Section 2: objects and class moves (GameScene handlers of the same names) —
  ObjectLeveled: (r: Reader) => ({ ObjectID: r.readU32() }),
  ObjectName: (r: Reader) => ({ ObjectID: r.readU32(), Name: r.readString() }),
  ObjectHide: (r: Reader) => ({ ObjectID: r.readU32() }),
  ObjectShow: (r: Reader) => ({ ObjectID: r.readU32() }),
  TeleportIn: (_r: Reader) => ({}),
  ObjectSitDown: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Sitting: r.readBool() }),
  ObjectHarvest: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  ObjectHarvested: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  ObjectMana: (r: Reader) => ({ ObjectID: r.readU32(), Percent: r.readU8() }),
  ObjectLevelEffects: (r: Reader) => ({ ObjectID: r.readU32(), LevelEffects: r.readU16() }),
  ObjectDeco: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Image: r.readI32() }),
  ObjectSneaking: (r: Reader) => ({ ObjectID: r.readU32(), SneakingActive: r.readBool() }),
  DefaultNPC: (r: Reader) => ({ ObjectID: r.readU32() }),
  NPCImageUpdate: (r: Reader) => ({ ObjectID: r.readU32(), Image: r.readU16(), Colour: r.readI32() }),
  RangeAttack: (r: Reader) => ({ TargetID: r.readU32(), Target: readPoint(r), Spell: r.readU8() }),
  ObjectRangeAttack: (r: Reader) => ({
    ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), TargetID: r.readU32(), Target: readPoint(r), Type: r.readU8(), Spell: r.readU8(), Level: r.readU8(),
  }),
  SetConcentration: (r: Reader) => ({ ObjectID: r.readU32(), Enabled: r.readBool(), Interrupted: r.readBool() }),
  SetElemental: (r: Reader) => ({ ObjectID: r.readU32(), Enabled: r.readBool(), Casted: r.readBool(), Value: r.readU32(), ElementType: r.readU32(), ExpLast: r.readU32() }),
  SetBindingShot: (r: Reader) => ({ ObjectID: r.readU32(), Enabled: r.readBool(), Value: r.readI64() }),
  UserDash: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectDash: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  UserDashFail: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectDashFail: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  UserBackStep: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectBackStep: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Distance: r.readI32() }),
  UserDashAttack: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectDashAttack: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Distance: r.readI32() }),
  UserAttackMove: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  InTrapRock: (r: Reader) => ({ Trapped: r.readBool() }),
  RemoveDelayedExplosion: (r: Reader) => ({ ObjectID: r.readU32() }),
  // — Info (NPC page links, big map / world map) —
  NewMonsterInfo: (r: Reader) => ({ Info: readClientMonsterInfo(r) }),
  NewNPCInfo: (r: Reader) => ({ Info: readClientNPCInfo(r) }),
  NewMapInfo: (r: Reader) => {
    const MapIndex = r.readI32();
    const Title = r.readString(), Width = r.readI32(), Height = r.readI32(), BigMap = r.readI32();
    let n = readCount(r, 16);
    const Movements: ClientMovementInfo[] = [];
    for (let i = 0; i < n; i++) Movements.push({ Destination: r.readI32(), Title: r.readString(), Location: readPoint(r), Icon: r.readI32() });
    n = readCount(r, 30);
    const NPCs: ClientNPCInfo[] = [];
    for (let i = 0; i < n; i++) NPCs.push(readClientNPCInfo(r));
    return { MapIndex, Info: { Title, Width, Height, BigMap, Movements, NPCs } as ClientMapInfo };
  },
  /** ServerPacketIds.WorldMapSetup (class WorldMapSetupInfo). */
  WorldMapSetupInfo: (r: Reader) => {
    const Enabled = r.readBool();
    const n = readCount(r, 9);
    const Icons: WorldMapIcon[] = [];
    for (let i = 0; i < n; i++) Icons.push({ ImageIndex: r.readI32(), Title: r.readString(), MapIndex: r.readI32() });
    return { Setup: { Enabled, Icons }, TeleportToNPCCost: r.readI32() };
  },
  SearchMapResult: (r: Reader) => ({ MapIndex: r.readI32(), NPCIndex: r.readU32() }),
  // — Section 1: basic play (GameScene / SelectScene handlers) —
  GainExperience: (r: Reader) => ({ Amount: r.readU32() }),
  BaseStatsInfo: (r: Reader) => ({ Stats: readBaseStats(r) }),
  LogOutSuccess: (r: Reader) => {
    const n = readCount(r, 1);
    const Characters: SelectInfo[] = [];
    for (let i = 0; i < n; i++) Characters.push(readSelectInfo(r));
    return { Characters };
  },
  LogOutFailed: (_r: Reader) => ({}),
  ReturnToLogin: (_r: Reader) => ({}),
  ChangeAMode: (r: Reader) => ({ Mode: r.readU8() }),
  ChangePMode: (r: Reader) => ({ Mode: r.readU8() }),
  /** Result: 0 disabled, 1 bad account, 2 bad current password, 3 bad new password, 4 no account, 5 wrong password, 6 success. */
  ChangePassword: (r: Reader) => ({ Result: r.readU8() }),
  ChangePasswordBanned: (r: Reader) => ({ Reason: r.readString(), ExpiryDate: r.readI64() }),
  /** Result: 0 disabled, 1 character not found. */
  DeleteCharacter: (r: Reader) => ({ Result: r.readU8() }),
  DeleteCharacterSuccess: (r: Reader) => ({ CharacterIndex: r.readI32() }),
  StartGameBanned: (r: Reader) => ({ Reason: r.readString(), ExpiryDate: r.readI64() }),
  StartGameDelay: (r: Reader) => ({ Milliseconds: r.readI64() }),
  NPCRequestInput: (r: Reader) => ({ NPCID: r.readU32(), PageName: r.readString() }),
  /** Type: OutputMessageType (0 normal, 1 quest, 2 guild). */
  SendOutputMessage: (r: Reader) => ({ Message: r.readString(), Type: r.readU8() }),
  UpdateNotice: (r: Reader) => ({ Notice: { Title: r.readString(), Message: r.readString() } }),
  SetTimer: (r: Reader) => ({ Key: r.readString(), Type: r.readU8(), Seconds: r.readI32() }),
  ExpireTimer: (r: Reader) => ({ Key: r.readString() }),
  SetCompass: (r: Reader) => ({ Location: readPoint(r) }),
  Roll: (r: Reader) => ({ Type: r.readI32(), Page: r.readString(), Result: r.readI32(), AutoRoll: r.readBool() }),
  OpenBrowser: (r: Reader) => ({ Url: r.readString() }),
  NewChatItem: (r: Reader) => ({ Item: readUserItem(r) }),
  ChatItemStats: (r: Reader) => ({ ChatItemId: r.readU64(), Stats: readUserItem(r) }),
  // — Trade (GameScene.TradeRequest / TradeAccept / TradeGold / TradeItem / TradeConfirm / TradeCancel / DepositTradeItem / RetrieveTradeItem) —
  TradeRequest: (r: Reader) => ({ Name: r.readString() }),
  TradeAccept: (r: Reader) => ({ Name: r.readString() }),
  TradeGold: (r: Reader) => ({ Amount: r.readU32() }),
  TradeItem: (r: Reader) => {
    const n = readCount(r, 1);
    const TradeItems: (UserItem | null)[] = [];
    for (let i = 0; i < n; i++) TradeItems.push(r.readBool() ? readUserItem(r) : null);
    return { TradeItems };
  },
  TradeConfirm: (_r: Reader) => ({}),
  TradeCancel: (r: Reader) => ({ Unlock: r.readBool() }),
  DepositTradeItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  RetrieveTradeItem: (r: Reader) => ({ From: r.readI32(), To: r.readI32(), Success: r.readBool() }),
  // — Guild (GameScene.GuildStatus / GuildInvite / GuildNameRequest / GuildNoticeChange / GuildMemberChange / ...) —
  GuildStatus: (r: Reader) => ({
    GuildName: r.readString(), GuildRankName: r.readString(), Level: r.readU8(), Experience: r.readI64(), MaxExperience: r.readI64(), Gold: r.readU32(),
    SparePoints: r.readU8(), MemberCount: r.readI32(), MaxMembers: r.readI32(), Voting: r.readBool(), ItemCount: r.readU8(), BuffCount: r.readU8(),
    MyOptions: r.readU8(), MyRankId: r.readI32(),
  }),
  GuildInvite: (r: Reader) => ({ Name: r.readString() }),
  GuildNameRequest: (_r: Reader) => ({}),
  /** update = -1: "the notice changed, ask again" (no lines); otherwise update = line count. */
  GuildNoticeChange: (r: Reader) => {
    const update = r.readI32();
    if (update > r.remaining) throw new Error("notice count");
    const notice: string[] = [];
    for (let i = 0; i < update; i++) notice.push(r.readString());
    return { update, notice };
  },
  /** Status: 0 offline, 1 online, 2 joined, 3 kicked, 4 left, 5 rank change, 6 new rank, 7 rank options, 8 my rank, 255 whole list. Ranks only when Status > 5. */
  GuildMemberChange: (r: Reader) => {
    const Name = r.readString(), RankIndex = r.readU8(), Status = r.readU8();
    const Ranks: GuildRank[] = [];
    if (Status > 5) { const n = readCount(r, 1); for (let i = 0; i < n; i++) Ranks.push(readGuildRank(r)); }
    return { Name, RankIndex, Status, Ranks };
  },
  GuildExpGain: (r: Reader) => ({ Amount: r.readU32() }),
  /** Type: 0 donated (Name), 1 retrieved (Name), 2 silent decrease, 3 silent increase. */
  GuildStorageGoldChange: (r: Reader) => ({ Amount: r.readU32(), Type: r.readU8(), Name: r.readString() }),
  /** Type: 0 stored, 1 retrieved, 2 moved, 3 / 4 / 5 store / retrieve / move refused. User = the mover's CharacterInfo.Index (UserInformation.RealId). */
  GuildStorageItemChange: (r: Reader) => {
    const Type = r.readU8(), To = r.readI32(), From = r.readI32(), User = r.readI32();
    // The wire writes UserId BEFORE the item here (GuildStorageItem.Save writes it after: see GuildStorageList)
    const Item: GuildStorageItem | null = r.readBool() ? (() => { const UserId = r.readI64(); return { UserId, Item: readUserItem(r) }; })() : null;
    return { Type, To, From, User, Item };
  },
  GuildStorageList: (r: Reader) => {
    const n = readCount(r, 1);
    const Items: (GuildStorageItem | null)[] = [];
    for (let i = 0; i < n; i++) Items.push(r.readBool() ? (() => { const Item = readUserItem(r); return { Item, UserId: r.readI64() }; })() : null);
    return { Items };
  },
  GuildRequestWar: (_r: Reader) => ({}),
  /** Remove = 1: ActiveBuffs are removed; else added / replaced. GuildBuffs (the server's catalogue) comes at login and on request. */
  GuildBuffList: (r: Reader) => {
    const Remove = r.readU8();
    let n = readCount(r, 9);
    const ActiveBuffs: GuildBuff[] = [];
    for (let i = 0; i < n; i++) ActiveBuffs.push({ Id: r.readI32(), Active: r.readBool(), ActiveTimeRemaining: r.readI32() });
    n = readCount(r, 1);
    const GuildBuffs: GuildBuffInfo[] = [];
    for (let i = 0; i < n; i++) {
      GuildBuffs.push({
        Id: r.readI32(), Icon: r.readI32(), Name: r.readString(), LevelRequirement: r.readU8(), PointsRequirement: r.readU8(), TimeLimit: r.readI32(),
        ActivationCost: r.readI32(), Stats: readStats(r),
      });
    }
    return { Remove, ActiveBuffs, GuildBuffs };
  },
  ObjectGuildNameChanged: (r: Reader) => ({ ObjectID: r.readU32(), GuildName: r.readString() }),
  ColourChanged: (r: Reader) => ({ NameColour: r.readI32() }),
  ObjectColourChanged: (r: Reader) => ({ ObjectID: r.readU32(), NameColour: r.readI32() }),
  // — NPCs (GameScene.NPCResponse / NPCGoods / NPCSell / NPCRepair / ...) —
  NPCResponse: (r: Reader) => {
    const n = readCount(r, 1);
    const Page: string[] = [];
    for (let i = 0; i < n; i++) Page.push(r.readString());
    return { Page };
  },
  /** C# Compressed => true: decoded from the inflated payload (decodeServerPacketAsync). */
  NPCGoods: (r: Reader) => {
    const n = readCount(r, 1);
    const List: UserItem[] = [];
    for (let i = 0; i < n; i++) List.push(readUserItem(r));
    return { List, Rate: r.readF32(), Type: r.readU8(), HideAddedStats: r.readBool() };
  },
  NPCSell: (_r: Reader) => ({}),
  NPCRepair: (r: Reader) => ({ Rate: r.readF32() }),
  NPCSRepair: (r: Reader) => ({ Rate: r.readF32() }),
  SellItem: (r: Reader) => ({ UniqueID: r.readU64(), Count: r.readU16(), Success: r.readBool() }),
  RepairItem: (r: Reader) => ({ UniqueID: r.readU64() }),
  NPCUpdate: (r: Reader) => ({ NPCID: r.readU32() }),
  ObjectItem: (r: Reader) => ({ ObjectID: r.readU32(), Name: r.readString(), NameColour: r.readI32(), Location: readPoint(r), Image: r.readU16(), grade: r.readU8() }),
  ObjectGold: (r: Reader) => ({ ObjectID: r.readU32(), Gold: r.readU32(), Location: readPoint(r) }),
  ObjectTurn: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  ObjectWalk: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  ObjectRun: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  ObjectAttack: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Spell: r.readU8(), Level: r.readU8(), Type: r.readU8() }),
  Struck: (r: Reader) => ({ AttackerID: r.readU32() }),
  ObjectStruck: (r: Reader) => ({ ObjectID: r.readU32(), AttackerID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  DamageIndicator: (r: Reader) => ({ Damage: r.readI32(), Type: r.readU8(), ObjectID: r.readU32() }),
  HealthChanged: (r: Reader) => ({ HP: r.readI32(), MP: r.readI32() }),
  ObjectHealth: (r: Reader) => ({ ObjectID: r.readU32(), Percent: r.readU8(), Expire: r.readU8() }),
  Death: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectDied: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8(), Type: r.readU8() }),
  Revived: (_r: Reader) => ({}),
  ObjectRevived: (r: Reader) => ({ ObjectID: r.readU32(), Effect: r.readBool() }),
  ObjectTeleportOut: (r: Reader) => ({ ObjectID: r.readU32(), Type: r.readU8() }),
  ObjectTeleportIn: (r: Reader) => ({ ObjectID: r.readU32(), Type: r.readU8() }),
  Pushed: (r: Reader) => ({ Location: readPoint(r), Direction: r.readU8() }),
  ObjectPushed: (r: Reader) => ({ ObjectID: r.readU32(), Location: readPoint(r), Direction: r.readU8() }),
  Chat: (r: Reader) => ({ Message: r.readString(), Type: r.readU8() }),
  ObjectChat: (r: Reader) => ({ ObjectID: r.readU32(), Text: r.readString(), Type: r.readU8() }),
} satisfies Record<string, (r: Reader) => object>;

type Decoders = typeof decoders;
export type ServerPacketName = keyof Decoders;
/** Discriminated union of every ported server packet: `{ type: "ObjectWalk", ObjectID, Location, Direction }`. */
export type ServerPacket = { [K in ServerPacketName]: { type: K } & ReturnType<Decoders[K]> }[ServerPacketName];
export type PacketOf<K extends ServerPacketName> = Extract<ServerPacket, { type: K }>;
export type { WirePoint };

/** Ports that deliberately read only a prefix of the packet. */
const PREFIX_ONLY = new Set<ServerPacketName>([]);

/** Packet class names whose ServerPacketIds member is spelled differently. */
const ID_NAME: Partial<Record<ServerPacketName, string>> = { ObjectNPC: "ObjectNpc", WorldMapSetupInfo: "WorldMapSetup" };

const byId = new Map<number, [ServerPacketName, (r: Reader) => object]>();
for (const name of Object.keys(decoders) as ServerPacketName[]) {
  const id = (S as Record<string, number>)[ID_NAME[name] ?? name];
  if (id === undefined) throw new Error(`server.ts: ${name} is not a ServerPacketIds member`);
  byId.set(id, [name, decoders[name]]);
}

/** Decode one frame; null for an id this port does not handle yet. Throws on a malformed ported packet. */
/** Ported packets whose C# `Compressed` is true: the payload is gzip (Functions.CompressBytes). */
const COMPRESSED = new Set<number>([S.NPCGoods]);

/** True when the frame's payload must be inflated first (use decodeServerPacketAsync). */
export function isCompressedFrame(frame: Uint8Array): boolean {
  return frame.length >= 4 && COMPRESSED.has(new DataView(frame.buffer, frame.byteOffset, frame.byteLength).getInt16(2, true));
}

/** Decodes a compressed frame. The desktop inflates rawBytes[4..(length - 4)] (Known debt: drops the last 4 bytes and relies
 *  on GZipStream ignoring the trailer); DecompressionStream checks the trailer, so the whole payload is inflated here. */
export async function decodeServerPacketAsync(frame: Uint8Array, strict = false): Promise<ServerPacket | null> {
  const { id, payload } = parseFrame(frame);
  if (!byId.has(id)) return null;
  return decodePayload(id, await gunzip(payload), strict);
}

/** `strict` (tests): also throw when a full port leaves payload bytes unread (prefix-only ports are exempt). */
export function decodeServerPacket(frame: Uint8Array, strict = false): ServerPacket | null {
  const { id, payload } = parseFrame(frame);
  if (COMPRESSED.has(id)) throw new Error(`packet ${id} is compressed: use decodeServerPacketAsync`);
  return decodePayload(id, payload, strict);
}

function decodePayload(id: number, payload: Uint8Array, strict: boolean): ServerPacket | null {
  const entry = byId.get(id);
  if (!entry) return null;
  const [name, decode] = entry;
  const r = new Reader(payload);
  const packet = { type: name, ...decode(r) } as ServerPacket;
  if (strict && r.remaining !== 0 && !PREFIX_ONLY.has(name)) throw new Error(`${name}: ${r.remaining} payload bytes unread`);
  return packet;
}

export function isPorted(id: number): boolean {
  return byId.has(id);
}
