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
  /** The struck-through "was" prices of a deal (uint32; 0 = none). DB v119, always on the wire. */
  DealGoldPrice: number; DealCreditPrice: number;
}

function readGameShopItem(r: Reader): GameShopItem {
  return {
    ItemIndex: r.readI32(), GIndex: r.readI32(), Info: readItemInfo(r), GoldPrice: r.readU32(), CreditPrice: r.readU32(), Count: r.readU16(),
    Class: r.readString(), Category: r.readString(), Stock: r.readI32(), iStock: r.readBool(), Deal: r.readBool(), TopItem: r.readBool(),
    Date: r.readI64(), CanBuyCredit: r.readBool(), CanBuyGold: r.readBool(), DealGoldPrice: r.readU32(), DealCreditPrice: r.readU32(),
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
/** ClientFriend(reader). Class = MirClass, Gender = MirGender, GuildName "" when guildless,
 *  LastSeen = the friend's last logout as DateTime.ToBinary (0n while online). */
export interface ClientFriend {
  Index: number; Name: string; Memo: string; Blocked: boolean; Online: boolean;
  Class: number; Level: number; Gender: number; GuildName: string; LastSeen: bigint;
}
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

/** ClientPoisonStatus (Shared/Data/DetailData.cs): one poison on the player. Type = PoisonType (u16 flag value), TimeLeft in ms (>= 0). */
export interface ClientPoisonStatus { Type: number; TimeLeft: number; Value: number }

/** ClientMapRespawn (Shared/Data/DetailData.cs): MonsterIndex = MonsterInfo.Index. 16 bytes on the wire. */
export interface ClientMapRespawn { MonsterIndex: number; Location: WirePoint; Spread: number; Count: number }

/** ClientMapSafeZone (Shared/Data/DetailData.cs). 11 bytes on the wire. */
export interface ClientMapSafeZone { Location: WirePoint; Size: number; StartPoint: boolean }

/** ClientSelectDetail (Shared/Data/DetailData.cs): select-screen extras per character. PlayTime in seconds. */
export interface ClientSelectDetail { Index: number; GuildName: string; MapTitle: string; PlayTime: bigint }

// — Phase 2.5 rows (Shared/Data/DetailData.cs). Dates are DateTime.ToBinary (bigint); Class = MirClass. —
/** ClientGuildMemberDetail: RankIndex = index into the guild's ranks (S.GuildMemberChange / GuildRank order). >= 17 bytes. */
export interface ClientGuildMemberDetail { Name: string; Class: number; Level: number; LastLogin: bigint; Online: boolean; RankIndex: number }
/** ClientGuildWar: MillisecondsLeft >= 0 at send time. >= 9 bytes. */
export interface ClientGuildWar { GuildName: string; MillisecondsLeft: bigint }
/** ClientGuildLogEntry: Kind = server GuildLogKind (GUILD_LOG_KIND); Text <= 128 chars, already localised by the server. >= 10 bytes. */
export interface ClientGuildLogEntry { Time: bigint; Kind: number; Text: string }
/** ClientRankingDetail: matched to an S.Rankings row by PlayerId. ExpPerTenThousand 0..10000. >= 12 bytes. */
export interface ClientRankingDetail { PlayerId: bigint; GuildName: string; Online: boolean; ExpPerTenThousand: number }
/** ClientGuildRanking: one guild-board row. ExpPerTenThousand 0..10000. >= 10 bytes. */
export interface ClientGuildRanking { Name: string; Level: number; ExpPerTenThousand: number; Members: number; Leader: string }
/** ClientMentorCandidate: an online player who could mentor the asker. >= 4 bytes. */
export interface ClientMentorCandidate { Name: string; Class: number; Level: number }
/** ClientMarketSale: one completed market sale. Date = DateTime.ToBinary. 12 bytes. */
export interface ClientMarketSale { Price: number; Date: bigint }
/** ClientSentMail: one sent-folder row. RecipientName <= 32 chars, Message <= 200 chars (list preview); DateSent = DateTime.ToBinary;
 *  Opened / Collected = the recipient's mail flags. >= 28 bytes. */
export interface ClientSentMail { MailID: bigint; RecipientName: string; Message: string; Gold: number; ItemCount: number; DateSent: bigint; Opened: boolean; Collected: boolean }

/** ClientGuildLogEntry.Kind values (server Server.MirDatabase.GuildLogKind; not in Shared, so not generated). */
export const GUILD_LOG_KIND = { Join: 0, Leave: 1, Kick: 2, RankChange: 3, Notice: 4, Buff: 5, War: 6, Territory: 7, Gold: 8 } as const;

/** Phase 2.5 list caps: the C# readers throw above these. */
export const GUILD_MEMBER_DETAILS_MAX_ROWS_PER_PART = 800;
export const GUILD_WAR_LIST_MAX_WARS = 200;
export const GUILD_LOG_MAX_ENTRIES = 100;
export const RANKINGS_DETAIL_MAX_ROWS = 100;
export const GUILD_RANKINGS_PAGE_SIZE = 20;
export const MENTOR_CANDIDATES_MAX_ROWS = 20;
/** Phase 2.6 list caps: the C# readers throw above these. */
export const MARKET_PRICE_HISTORY_MAX_SALES = 14;
export const SENT_MAIL_MAX_ROWS = 50;

/** readCount plus the C# reader's upper bound. */
function readCappedCount(r: Reader, elementSize: number, max: number): number {
  const n = readCount(r, elementSize);
  if (n > max) throw new Error(`count ${n} exceeds ${max}`);
  return n;
}

/** S.GroupMemberStatus.MaxBuffs: the C# reader throws above this. */
export const GROUP_MEMBER_STATUS_MAX_BUFFS = 20;

/** S.ServerRules (Settings + the Shared Awake statics): the server's refine / awakening odds, mentor / marriage / mail /
 *  territory costs, class gating and feature flags. Sent after LoginSuccess and again on StartGame success. */
export interface ServerRules {
  // Refine
  OnlyRefineWeapon: boolean; RefineBaseChance: number; RefineTime: number; RefineIncrease: number; RefineCritChance: number;
  RefineCritIncrease: number; RefineWepStatReduce: number; RefineItemStatReduce: number; RefineCost: number; RefineOreName: string;
  // Awakening
  AwakeSuccessRate: number; AwakeHitRate: number; MaxAwakeLevel: number; Awake_WeaponRate: number; Awake_HelmetRate: number; Awake_ArmorRate: number;
  /** byte[] (one per ItemGrade slot). */
  AwakeChanceMax: number[];
  /** float[] (f32). */
  AwakeMaterialRate: number[];
  /** Per AwakeType (index = type - 1): [0] = Material1 counts, [1] = Material2 counts, one byte per ItemGrade. */
  AwakeMaterials: number[][][];
  // Mentor
  MentorLevelGap: number; MentorSkillBoost: boolean; MentorLength: number; MentorDamageBoost: number; MentorExpBoost: number; MenteeExpBank: number;
  // Marriage
  LoverEXPBonus: number; MarriageCooldown: number; WeddingRingRecall: boolean; MarriageLevelRequired: number; ReplaceWedRingCost: number;
  // Mail
  MailAutoSendGold: boolean; MailAutoSendItems: boolean; MailFreeWithStamp: boolean; MailCostPer1KGold: number; MailItemInsurancePercentage: number;
  MailCapacity: number;
  // Guild territories
  BuyGTGold: number; ExtendGT: number; GTDays: number;
  // Gems
  GemStatIndependent: boolean;
  // Account (select-screen class gating)
  AllowCreateAssassin: boolean; AllowCreateArcher: boolean;
  // Features
  MapDetailsEnabled: boolean; FriendRequestsEnabled: boolean; MaxFriends: number;
}

function readServerRules(r: Reader): ServerRules {
  const OnlyRefineWeapon = r.readBool(), RefineBaseChance = r.readU8(), RefineTime = r.readI32(), RefineIncrease = r.readU8();
  const RefineCritChance = r.readU8(), RefineCritIncrease = r.readU8(), RefineWepStatReduce = r.readU8(), RefineItemStatReduce = r.readU8();
  const RefineCost = r.readI32(), RefineOreName = r.readString();

  const AwakeSuccessRate = r.readU8(), AwakeHitRate = r.readU8(), MaxAwakeLevel = r.readI32();
  const Awake_WeaponRate = r.readU8(), Awake_HelmetRate = r.readU8(), Awake_ArmorRate = r.readU8();
  let n = readCount(r, 1);
  const AwakeChanceMax: number[] = [];
  for (let i = 0; i < n; i++) AwakeChanceMax.push(r.readU8());
  n = readCount(r, 4);
  const AwakeMaterialRate: number[] = [];
  for (let i = 0; i < n; i++) AwakeMaterialRate.push(r.readF32());
  n = readCount(r, 4);
  const AwakeMaterials: number[][][] = [];
  for (let i = 0; i < n; i++) {
    const arrayLength = readCount(r, 4);
    const lists: number[][] = [];
    for (let j = 0; j < arrayLength; j++) {
      const listCount = readCount(r, 1);
      const list: number[] = [];
      for (let k = 0; k < listCount; k++) list.push(r.readU8());
      lists.push(list);
    }
    AwakeMaterials.push(lists);
  }

  const MentorLevelGap = r.readU8(), MentorSkillBoost = r.readBool(), MentorLength = r.readU8(), MentorDamageBoost = r.readU8();
  const MentorExpBoost = r.readU8(), MenteeExpBank = r.readU8();
  const LoverEXPBonus = r.readI32(), MarriageCooldown = r.readI32(), WeddingRingRecall = r.readBool(), MarriageLevelRequired = r.readI32();
  const ReplaceWedRingCost = r.readI32();
  const MailAutoSendGold = r.readBool(), MailAutoSendItems = r.readBool(), MailFreeWithStamp = r.readBool();
  const MailCostPer1KGold = r.readU32(), MailItemInsurancePercentage = r.readU32(), MailCapacity = r.readU32();
  const BuyGTGold = r.readI32(), ExtendGT = r.readI32(), GTDays = r.readI32();
  const GemStatIndependent = r.readBool();
  const AllowCreateAssassin = r.readBool(), AllowCreateArcher = r.readBool();
  const MapDetailsEnabled = r.readBool(), FriendRequestsEnabled = r.readBool(), MaxFriends = r.readI32();
  return {
    OnlyRefineWeapon, RefineBaseChance, RefineTime, RefineIncrease, RefineCritChance, RefineCritIncrease, RefineWepStatReduce, RefineItemStatReduce,
    RefineCost, RefineOreName,
    AwakeSuccessRate, AwakeHitRate, MaxAwakeLevel, Awake_WeaponRate, Awake_HelmetRate, Awake_ArmorRate, AwakeChanceMax, AwakeMaterialRate, AwakeMaterials,
    MentorLevelGap, MentorSkillBoost, MentorLength, MentorDamageBoost, MentorExpBoost, MenteeExpBank,
    LoverEXPBonus, MarriageCooldown, WeddingRingRecall, MarriageLevelRequired, ReplaceWedRingCost,
    MailAutoSendGold, MailAutoSendItems, MailFreeWithStamp, MailCostPer1KGold, MailItemInsurancePercentage, MailCapacity,
    BuyGTGold, ExtendGT, GTDays, GemStatIndependent, AllowCreateAssassin, AllowCreateArcher, MapDetailsEnabled, FriendRequestsEnabled, MaxFriends,
  };
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
  /** S.MapAtmosphere (LiveDb: the current map's light / dark light / weather changed; same encodings as MapChanged). */
  MapAtmosphere: (r: Reader) => ({ Lights: r.readU8(), MapDarkLight: r.readU8(), Weather: r.readU16() }),
  /** S.ServerRules (after LoginSuccess and on StartGame success): see the ServerRules interface. */
  ServerRules: (r: Reader) => readServerRules(r),
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
    return { ObjectID, Name, NameColour, Image, Colour, Location, Direction }; // quest V2: no QuestIDs (S.QuestAvailability)
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
    // Split by size since 2026-10-24: Part 0 replaces the box, parts 1 .. PartCount - 1 append (after the list on the wire).
    const Part = r.readU8(), PartCount = r.readU8();
    return { Mail, Part, PartCount };
  },
  MailLockedItem: (r: Reader) => ({ UniqueID: r.readU64(), Locked: r.readBool() }),
  MailSendRequest: (_r: Reader) => ({}),
  MailSent: (r: Reader) => ({ Result: r.readI8() }),
  ParcelCollected: (r: Reader) => ({ Result: r.readI8() }),
  MailCost: (r: Reader) => ({ Cost: r.readU32() }),
  FriendUpdate: (r: Reader) => {
    const n = readCount(r, 1);
    const Friends: ClientFriend[] = [];
    for (let i = 0; i < n; i++) {
      Friends.push({
        Index: r.readI32(), Name: r.readString(), Memo: r.readString(), Blocked: r.readBool(), Online: r.readBool(),
        Class: r.readU8(), Level: r.readU16(), Gender: r.readU8(), GuildName: r.readString(), LastSeen: r.readI64(),
      });
    }
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
  /** Whole-grid replace (C.SortItems reply for the bag, GameScene.UserSlotsRefresh): each array is null when not sent. */
  UserSlotsRefresh: (r: Reader) => ({ Inventory: readItemArray(r), Equipment: readItemArray(r) }),
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
  // — Phase 2.4 detail packets —
  /** S.PoisonStatus (player only, on change): the full current poison set with time left. */
  PoisonStatus: (r: Reader) => {
    const n = readCount(r, 10);
    const Poisons: ClientPoisonStatus[] = [];
    for (let i = 0; i < n; i++) Poisons.push({ Type: r.readU16(), TimeLeft: r.readI32(), Value: r.readI32() });
    return { Poisons };
  },
  /** S.GroupMemberStatus (<= 1/s per member): Class = MirClass, HP/MPPercent 0-100, Buffs = BuffType values (<= 20). */
  GroupMemberStatus: (r: Reader) => {
    const Name = r.readString(), Class = r.readU8(), Level = r.readU16(), HPPercent = r.readU8(), MPPercent = r.readU8();
    const n = r.readI32();
    if (n < 0 || n > GROUP_MEMBER_STATUS_MAX_BUFFS || n > r.remaining) throw new Error(`buff count ${n}`);
    const Buffs: number[] = [];
    for (let i = 0; i < n; i++) Buffs.push(r.readU8());
    return { Name, Class, Level, HPPercent, MPPercent, Buffs };
  },
  /** S.HeroAutoCast (after HeroInformation and as the C.HeroSpellToggle reply): the FULL list of hero spells with auto-cast off. */
  HeroAutoCast: (r: Reader) => {
    const n = readCount(r, 1);
    const Disabled: number[] = [];
    for (let i = 0; i < n; i++) Disabled.push(r.readU8());
    return { Disabled };
  },
  /** S.MapDetails (COMPRESSED; reply to C.RequestMapDetails). Part 0 replaces the details for MapIndex, parts 1 .. PartCount - 1 append. */
  MapDetails: (r: Reader) => {
    const MapIndex = r.readI32(), Part = r.readU8(), PartCount = r.readU8();
    let n = readCount(r, 16);
    const Respawns: ClientMapRespawn[] = [];
    for (let i = 0; i < n; i++) Respawns.push({ MonsterIndex: r.readI32(), Location: readPoint(r), Spread: r.readU16(), Count: r.readU16() });
    n = readCount(r, 11);
    const SafeZones: ClientMapSafeZone[] = [];
    for (let i = 0; i < n; i++) SafeZones.push({ Location: readPoint(r), Size: r.readU16(), StartPoint: r.readBool() });
    return { MapIndex, Part, PartCount, Respawns, SafeZones };
  },
  /** S.LoginFailed (right after S.Login on a failed login). Tries -1 = not applicable; LockedUntil = DateTime.ToBinary, 0n = not locked. */
  LoginFailed: (r: Reader) => ({ Result: r.readU8(), AccountTriesLeft: r.readI32(), ConnectionTriesLeft: r.readI32(), LockedUntil: r.readI64() }),
  /** S.SelectDetails (after LoginSuccess, LogOutSuccess and NewCharacterSuccess): account gold + per-character guild / map / play time. */
  SelectDetails: (r: Reader) => {
    const AccountGold = r.readU32();
    const n = readCount(r, 14);
    const Characters: ClientSelectDetail[] = [];
    for (let i = 0; i < n; i++) Characters.push({ Index: r.readI32(), GuildName: r.readString(), MapTitle: r.readString(), PlayTime: r.readI64() });
    return { AccountGold, Characters };
  },
  // — Phase 2.5 detail packets —
  /** S.GuildMemberDetails (reply to C.RequestGuildMemberDetails). Part 0 replaces the member list, parts 1 .. PartCount - 1 append. */
  GuildMemberDetails: (r: Reader) => {
    const Part = r.readU8(), PartCount = r.readU8();
    const n = readCappedCount(r, 17, GUILD_MEMBER_DETAILS_MAX_ROWS_PER_PART);
    const Members: ClientGuildMemberDetail[] = [];
    for (let i = 0; i < n; i++) Members.push({ Name: r.readString(), Class: r.readU8(), Level: r.readU16(), LastLogin: r.readI64(), Online: r.readBool(), RankIndex: r.readI32() });
    return { Part, PartCount, Members };
  },
  /** S.GuildWarList (guild status at login / join, and on war start / end): the FULL list; empty = no wars. */
  GuildWarList: (r: Reader) => {
    const n = readCappedCount(r, 9, GUILD_WAR_LIST_MAX_WARS);
    const Wars: ClientGuildWar[] = [];
    for (let i = 0; i < n; i++) Wars.push({ GuildName: r.readString(), MillisecondsLeft: r.readI64() });
    return { Wars };
  },
  /** S.GuildLog (reply to C.RequestGuildLog): oldest first, at most the newest 100. */
  GuildLog: (r: Reader) => {
    const n = readCappedCount(r, 10, GUILD_LOG_MAX_ENTRIES);
    const Entries: ClientGuildLogEntry[] = [];
    for (let i = 0; i < n; i++) Entries.push({ Time: r.readI64(), Kind: r.readU8(), Text: r.readString() });
    return { Entries };
  },
  /** S.RankingsDetail (right after S.Rankings): extras for that page's rows, RankType as in C.GetRanking / S.Rankings. */
  RankingsDetail: (r: Reader) => {
    const RankType = r.readU8();
    const n = readCappedCount(r, 12, RANKINGS_DETAIL_MAX_ROWS);
    const Rows: ClientRankingDetail[] = [];
    for (let i = 0; i < n; i++) Rows.push({ PlayerId: r.readI64(), GuildName: r.readString(), Online: r.readBool(), ExpPerTenThousand: r.readU16() });
    return { RankType, Rows };
  },
  /** S.GuildRankings (reply to C.GetGuildRanking): Page 0-based (server-clamped), Total = ranked guilds; rank of row i = Page * 20 + i + 1. */
  GuildRankings: (r: Reader) => {
    const Page = r.readI32(), Total = r.readI32();
    const n = readCappedCount(r, 10, GUILD_RANKINGS_PAGE_SIZE);
    const Guilds: ClientGuildRanking[] = [];
    for (let i = 0; i < n; i++) Guilds.push({ Name: r.readString(), Level: r.readU16(), ExpPerTenThousand: r.readU16(), Members: r.readI32(), Leader: r.readString() });
    return { Page, Total, Guilds };
  },
  /** S.MentorDetails (right after S.MentorUpdate when mentored): PartnerClass = MirClass; MentorDate / EndsAt = DateTime.ToBinary. */
  MentorDetails: (r: Reader) => ({ PartnerClass: r.readU8(), MentorDate: r.readI64(), EndsAt: r.readI64() }),
  /** S.MentorCandidates (reply to C.FindMentor): at most 20 online players. */
  MentorCandidates: (r: Reader) => {
    const n = readCappedCount(r, 4, MENTOR_CANDIDATES_MAX_ROWS);
    const Candidates: ClientMentorCandidate[] = [];
    for (let i = 0; i < n; i++) Candidates.push({ Name: r.readString(), Class: r.readU8(), Level: r.readU16() });
    return { Candidates };
  },
  /** S.PlayerProfile (right after S.PlayerInspect): Name = the inspected player; Text may be empty. */
  PlayerProfile: (r: Reader) => ({ Name: r.readString(), Text: r.readString() }),
  // — Phase 2.6 —
  /** S.MarketPriceHistory (reply to C.MarketPriceHistory): ItemIndex = ItemInfo.Index; at most 14 sales, oldest first. */
  MarketPriceHistory: (r: Reader) => {
    const ItemIndex = r.readI32();
    const n = readCappedCount(r, 12, MARKET_PRICE_HISTORY_MAX_SALES);
    const Sales: ClientMarketSale[] = [];
    for (let i = 0; i < n; i++) Sales.push({ Price: r.readU32(), Date: r.readI64() });
    return { ItemIndex, Sales };
  },
  /** S.SentMail (reply to C.RequestSentMail / C.HideSentMail): mail still in the recipient's box and not hidden, newest first, at most 50. */
  SentMail: (r: Reader) => {
    const n = readCappedCount(r, 28, SENT_MAIL_MAX_ROWS);
    const Mails: ClientSentMail[] = [];
    for (let i = 0; i < n; i++) {
      Mails.push({
        MailID: r.readU64(), RecipientName: r.readString(), Message: r.readString(), Gold: r.readU32(),
        ItemCount: r.readI32(), DateSent: r.readI64(), Opened: r.readBool(), Collected: r.readBool(),
      });
    }
    return { Mails };
  },
  /** S.FriendRequest (private): an online player asks to be this player's friend; Class = MirClass.
   *  Answer with C.FriendRequestReply { Name, Accept }. Requests expire after 60 s (server memory only). */
  FriendRequest: (r: Reader) => ({ Name: r.readString(), Class: r.readU8(), Level: r.readU16() }),
  /** S.FriendRequestResult (private): the outcome of this player's request to Name; Result = FriendRequestResults. */
  FriendRequestResult: (r: Reader) => ({ Name: r.readString(), Result: r.readU8() }),
  /** S.AllowToggles (private): every social allow toggle as one bit mask; bit (1 << AllowToggle.X) set = on.
   *  Sent at login and after any toggle change (C.SwitchGroup, C.ChangeTrade, C.SetAllowToggle, NPC @ALLOW* commands, ...).
   *  Test a bit with allowToggleOn(Flags, AllowToggle.Trade). */
  AllowToggles: (r: Reader) => ({ Flags: r.readU16() }),
  /** S.ObjectMoveBatch (P9-08, [Optimisation] MoveBatch): consecutive ObjectTurn/ObjectWalk/ObjectRun the server packed at
   *  flush; apply each in order exactly as the single packet. Kind 0 = turn, 1 = walk, 2 = run (ObjectMoveBatch.Turn/Walk/Run). */
  ObjectMoveBatch: (r: Reader) => {
    const count = r.readU16();
    const Moves: { Kind: number; ObjectID: number; Location: WirePoint; Direction: number }[] = [];
    for (let i = 0; i < count; i++) {
      const kindDir = r.readU8();
      Moves.push({ Kind: kindDir >> 3, ObjectID: r.readU32(), Location: { X: r.readU16(), Y: r.readU16() }, Direction: kindDir & 7 });
    }
    return { Moves };
  },
  /** S.DungeonMap: one part of a generated dungeon map (gzipped .map bytes). Every part arrives before the S.MapChanged
   *  that enters the map; mapFiles.ts joins and inflates them and serves the map instead of a relay file. */
  DungeonMap: (r: Reader) => {
    const FileName = r.readString(), Part = r.readU16(), Parts = r.readU16();
    return { FileName, Part, Parts, Data: r.readFixedBytes(r.readU16()) };
  },
  // — Quest V2 (wave 2 kick-off; Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md Part B "Wire protocol"). All private.
  //   Enum fields are numbers (enums.generated.ts); times are DateTime.ToBinary() (UTC) as bigint, 0n = none. —
  /** S.QuestDefinition: one quest's definition, sent once per connection. Rewards are already filtered by class/gender. */
  QuestDefinition: (r: Reader) => readQuestDefinition(r),
  /** S.QuestStates: Full = the complete set (replace the list); Status = QuestRunStatus, None = remove the run. */
  QuestStates: (r: Reader) => {
    const Full = r.readBool();
    const n = readCount(r, 26);
    const States: QuestStateEntry[] = [];
    for (let i = 0; i < n; i++) {
      States.push({ Quest: r.readI32(), Status: r.readU8(), Step: r.readI32(), ExpiresAt: r.readI64(), StepExpiresAt: r.readI64(), TrackHint: r.readBool() });
    }
    return { Full, States };
  },
  /** S.QuestProgress: objective values (Objective = objective id, not a monster/item id). */
  QuestProgress: (r: Reader) => {
    const n = readCount(r, 12);
    const Progress: { Quest: number; Objective: number; Value: number }[] = [];
    for (let i = 0; i < n; i++) Progress.push({ Quest: r.readI32(), Objective: r.readI32(), Value: r.readI32() });
    return { Progress };
  },
  /** S.QuestHistory: Full = the complete list. AvailableAt 0n = no timer (not repeatable, or available now). */
  QuestHistory: (r: Reader) => {
    const Full = r.readBool();
    const n = readCount(r, 25);
    const History: QuestHistoryEntry[] = [];
    for (let i = 0; i < n; i++) {
      History.push({ Quest: r.readI32(), Name: r.readString(), Times: r.readI32(), LastAt: r.readI64(), AvailableAt: r.readI64() });
    }
    return { Full, History };
  },
  /** S.QuestAvailability: per NPC in view, the server-chosen QuestIcon and the quests offered / finished for this player. */
  QuestAvailability: (r: Reader) => {
    const n = readCount(r, 13);
    const Npcs: QuestAvailabilityEntry[] = [];
    for (let i = 0; i < n; i++) {
      const ObjectID = r.readU32(), Icon = r.readU8();
      const Offered = readIntList(r), Finishing = readIntList(r);
      Npcs.push({ ObjectID, Icon, Offered, Finishing });
    }
    return { Npcs };
  },
  /** S.QuestOffer: accept prompt from a non-NPC endpoint; Kind = QuestEndpointKind; SharedBy "" = not a share. */
  QuestOffer: (r: Reader) => ({ Quest: r.readI32(), Kind: r.readU8(), SharedBy: r.readString() }),
} satisfies Record<string, (r: Reader) => object>;

// — Quest V2 records (field names and order as the C# nested classes of S.QuestDefinition / S.QuestStates / ...) —
export interface QuestDefStep {
  Index: number; Order: number; Title: string; Text: string; ReadyText: string; CompletionMode: number; NRequired: number;
  NextStep: number; TimeLimitSeconds: number;
}
export interface QuestDefObjective {
  Index: number; Step: number; Order: number; Type: number; TargetKind: number; TargetId: number; TargetName: string; Count: number;
  Map: number; X: number; Y: number; Radius: number; Optional: boolean; Hidden: boolean; Text: string;
}
export interface QuestDefReward { Scope: number; ScopeId: number; Kind: number; TargetId: number; Amount: bigint; ChoiceGroup: number; FirstTimeOnly: boolean }
export interface QuestDefEndpoint { Role: number; Kind: number; TargetId: number; Name: string; Map: number }
export interface QuestDefBranch { Step: number; ChoiceKey: string; Text: string; Outcome: number }
export interface QuestDefinitionInfo {
  Quest: number; Name: string; Summary: string; CompleteText: string; Category: number; StorylineIndex: number; LevelHint: number;
  RepeatPolicy: number; CooldownSeconds: number; TimeLimitSeconds: number; FailPolicy: number; AbandonAllowed: boolean; Flags: number;
  GroupName: string; Steps: QuestDefStep[]; Objectives: QuestDefObjective[]; Rewards: QuestDefReward[]; Endpoints: QuestDefEndpoint[];
  Branches: QuestDefBranch[];
}
export interface QuestStateEntry { Quest: number; Status: number; Step: number; ExpiresAt: bigint; StepExpiresAt: bigint; TrackHint: boolean }
export interface QuestHistoryEntry { Quest: number; Name: string; Times: number; LastAt: bigint; AvailableAt: bigint }
export interface QuestAvailabilityEntry { ObjectID: number; Icon: number; Offered: number[]; Finishing: number[] }

function readIntList(r: Reader): number[] {
  const n = readCount(r, 4);
  const out: number[] = [];
  for (let i = 0; i < n; i++) out.push(r.readI32());
  return out;
}

function readQuestDefinition(r: Reader): QuestDefinitionInfo {
  const Quest = r.readI32(), Name = r.readString(), Summary = r.readString(), CompleteText = r.readString();
  const Category = r.readU8(), StorylineIndex = r.readI32(), LevelHint = r.readI32(), RepeatPolicy = r.readU8();
  const CooldownSeconds = r.readI32(), TimeLimitSeconds = r.readI32(), FailPolicy = r.readU8(), AbandonAllowed = r.readBool();
  const Flags = r.readI32(), GroupName = r.readString();
  const Steps: QuestDefStep[] = [];
  for (let i = 0, n = readCount(r, 24); i < n; i++) {
    Steps.push({
      Index: r.readI32(), Order: r.readI32(), Title: r.readString(), Text: r.readString(), ReadyText: r.readString(),
      CompletionMode: r.readU8(), NRequired: r.readI32(), NextStep: r.readI32(), TimeLimitSeconds: r.readI32(),
    });
  }
  const Objectives: QuestDefObjective[] = [];
  for (let i = 0, n = readCount(r, 40); i < n; i++) {
    Objectives.push({
      Index: r.readI32(), Step: r.readI32(), Order: r.readI32(), Type: r.readU8(), TargetKind: r.readU8(), TargetId: r.readI32(),
      TargetName: r.readString(), Count: r.readI32(), Map: r.readI32(), X: r.readI32(), Y: r.readI32(), Radius: r.readI32(),
      Optional: r.readBool(), Hidden: r.readBool(), Text: r.readString(),
    });
  }
  const Rewards: QuestDefReward[] = [];
  for (let i = 0, n = readCount(r, 23); i < n; i++) {
    Rewards.push({
      Scope: r.readU8(), ScopeId: r.readI32(), Kind: r.readU8(), TargetId: r.readI32(), Amount: r.readI64(), ChoiceGroup: r.readI32(),
      FirstTimeOnly: r.readBool(),
    });
  }
  const Endpoints: QuestDefEndpoint[] = [];
  for (let i = 0, n = readCount(r, 11); i < n; i++) {
    Endpoints.push({ Role: r.readU8(), Kind: r.readU8(), TargetId: r.readI32(), Name: r.readString(), Map: r.readI32() });
  }
  const Branches: QuestDefBranch[] = [];
  for (let i = 0, n = readCount(r, 10); i < n; i++) {
    Branches.push({ Step: r.readI32(), ChoiceKey: r.readString(), Text: r.readString(), Outcome: r.readI32() });
  }
  return {
    Quest, Name, Summary, CompleteText, Category, StorylineIndex, LevelHint, RepeatPolicy, CooldownSeconds, TimeLimitSeconds, FailPolicy,
    AbandonAllowed, Flags, GroupName, Steps, Objectives, Rewards, Endpoints, Branches,
  };
}

/** S.FriendRequestResult.Result codes (byte constants on the C# class, not an enum). */
export const FriendRequestResults = { Accepted: 0, Declined: 1, Expired: 2, Offline: 3, ListFull: 4, NotAccepting: 5 } as const;

/** True when S.AllowToggles.Flags has the bit for toggle (an AllowToggle value from enums.generated.ts). */
export function allowToggleOn(flags: number, toggle: number): boolean {
  return (flags & (1 << toggle)) !== 0;
}

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
const COMPRESSED = new Set<number>([S.NPCGoods, S.MapDetails, S.FriendUpdate]);

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
