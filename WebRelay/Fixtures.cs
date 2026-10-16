using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using S = ServerPackets;
using C = ClientPackets;

namespace WebRelay;

/// <summary>
/// --emit-fixtures &lt;dir&gt;: one golden file per ported packet, `{ name, dir, hex, fields }`, where `hex` is the frame the
/// REAL Shared codec writes and `fields` the public fields it was built from. WebClient/tools/codec-test.ts decodes
/// each server frame (and re-encodes each client packet) with the TS port and compares whole values.
/// Add a sample here whenever a packet is ported to WebClient/src/protocol.
/// </summary>
internal static class Fixtures
{
    private static IEnumerable<Packet> ServerSamples()
    {
        var p = new Point(123, 456);
        var red = Color.FromArgb(unchecked((int)0xFFFF2010));
        yield return new S.Connected();
        yield return new S.ClientVersion { Result = 1 };
        yield return new S.Disconnect { Reason = 2 };
        yield return new S.KeepAlive { Time = 1234567890123 };
        yield return new S.NewAccount { Result = 8 };
        yield return new S.Login { Result = 4 };
        yield return new S.LoginBanned { Reason = "Botting — 封号", ExpiryDate = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        yield return new S.LoginSuccess
        {
            Characters =
            {
                new SelectInfo { Index = 7, Name = "Alpha", Level = 22, Class = MirClass.Wizard, Gender = MirGender.Female, LastAccess = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc) },
                new SelectInfo { Index = 9, Name = "Bêta", Level = 1, Class = MirClass.Assassin, Gender = MirGender.Male },
            }
        };
        yield return new S.NewCharacter { Result = 5 };
        yield return new S.NewCharacterSuccess { CharInfo = new SelectInfo { Index = 3, Name = "Gamma", Level = 1, Class = MirClass.Taoist, Gender = MirGender.Male } };
        yield return new S.StartGame { Result = 4, Resolution = 1024 };
        yield return new S.MapInformation { MapIndex = 12, FileName = "0", Title = "Bichon Province", MiniMap = 101, BigMap = 202, Music = 3, Lights = LightSetting.Normal, Lightning = true, Fire = false, MapDarkLight = 5, WeatherParticles = WeatherSetting.Rain };
        yield return new S.MapChanged { MapIndex = 2, FileName = "D001", Title = "Cave", MiniMap = 4, BigMap = 0, Music = 7, Lights = LightSetting.Night, Location = p, Direction = MirDirection.DownLeft, MapDarkLight = 1, Weather = WeatherSetting.None };
        yield return new S.UserLocation { Location = p, Direction = MirDirection.Right };
        yield return new S.TimeOfDay { Lights = LightSetting.Evening };
        yield return new S.ObjectPlayer
        {
            ObjectID = 4000000001, Name = "Hero", GuildName = "Guild", GuildRankName = "Leader", NameColour = red, Class = MirClass.Warrior,
            Gender = MirGender.Female, Level = 40, Location = p, Direction = MirDirection.UpRight, Hair = 3, Light = 2, Weapon = 12, WeaponEffect = 1,
            Armour = 5, Poison = PoisonType.Green | PoisonType.Slow, Dead = false, Hidden = true, Effect = SpellEffect.None, WingEffect = 1, Extra = true,
            MountType = -1, RidingMount = false, Fishing = true, TransformType = -1, ElementOrbEffect = 1, ElementOrbLvl = 2, ElementOrbMax = 3,
            Buffs = { BuffType.Haste, BuffType.Hiding }, LevelEffects = LevelEffects.Mist,
        };
        yield return new S.ObjectMonster
        {
            ObjectID = 77, Name = "Hen", NameColour = Color.White, Location = p, Image = Monster.Hen, Direction = MirDirection.Left, Effect = 1, AI = 2,
            Light = 3, Dead = false, Skeleton = true, Poison = PoisonType.Red, Hidden = false, ShockTime = 99, BindingShotCenter = true, Extra = true,
            ExtraByte = 4, MasterObjectId = 55, Rarity = MonsterType.Elite, Buffs = { BuffType.Hiding },
        };
        yield return new S.ObjectNPC { ObjectID = 88, Name = "Shopkeeper", NameColour = Color.Lime, Image = 14, Colour = red, Location = p, Direction = MirDirection.Down, QuestIDs = { 1, 2, 300 } };
        yield return new S.ObjectRemove { ObjectID = 77 };
        yield return new S.NPCResponse { Page = new List<string> { "Hello <Buy/@BUY>, {friend/Yellow}.", "<<Big/@BIG/Red>>", "" } };
        var goodsInfo = new ItemInfo { Index = 40, Name = "(HP)DrugSmall", Type = ItemType.Potion, StackSize = 50, Price = 10 };
        var goods = new UserItem(goodsInfo) { UniqueID = 4040, Count = 50, CurrentDura = 0, MaxDura = 0 };
        goods.AddedStats[Stat.HP] = 5;
        yield return new S.NPCGoods { List = { goods, new UserItem(goodsInfo) { UniqueID = 4041, Count = 1 } }, Rate = 1.25F, Type = PanelType.Buy, HideAddedStats = true };
        yield return new S.NPCSell();
        yield return new S.NPCRepair { Rate = 1.5F };
        yield return new S.NPCSRepair { Rate = 2.75F };
        yield return new S.SellItem { UniqueID = 9_000_000_001UL, Count = 3, Success = true };
        yield return new S.RepairItem { UniqueID = 12 };
        yield return new S.NPCUpdate { NPCID = 88 };
        yield return new S.ObjectItem { ObjectID = 501, Name = "WoodenSword", NameColour = Color.Yellow, Location = p, Image = 42, grade = ItemGrade.Legendary };
        yield return new S.ObjectGold { ObjectID = 502, Gold = 4_000_000_000, Location = p };
        yield return new S.ObjectTurn { ObjectID = 77, Location = p, Direction = MirDirection.Up };
        yield return new S.ObjectWalk { ObjectID = 77, Location = p, Direction = MirDirection.DownRight };
        yield return new S.ObjectRun { ObjectID = 77, Location = p, Direction = MirDirection.UpLeft };
        yield return new S.ObjectAttack { ObjectID = 77, Location = p, Direction = MirDirection.Left, Spell = Spell.Slaying, Level = 3, Type = 1 };
        yield return new S.Struck { AttackerID = 66 };
        yield return new S.ObjectStruck { ObjectID = 77, AttackerID = 66, Location = p, Direction = MirDirection.Right };
        yield return new S.DamageIndicator { Damage = -15, Type = DamageType.Critical, ObjectID = 77 };
        yield return new S.HealthChanged { HP = 120, MP = 45 };
        yield return new S.ObjectHealth { ObjectID = 77, Percent = 60, Expire = 5 };
        yield return new S.Death { Location = p, Direction = MirDirection.Down };
        yield return new S.ObjectDied { ObjectID = 77, Location = p, Direction = MirDirection.Down, Type = 1 };
        yield return new S.Revived();
        yield return new S.ObjectRevived { ObjectID = 77, Effect = true };
        yield return new S.ObjectTeleportOut { ObjectID = 77, Type = 1 };
        yield return new S.ObjectTeleportIn { ObjectID = 77, Type = 0 };
        yield return new S.Pushed { Location = p, Direction = MirDirection.Left };
        yield return new S.ObjectPushed { ObjectID = 77, Location = p, Direction = MirDirection.Right };
        var armourInfo = new ItemInfo
        {
            Index = 31, Name = "HeavyArmour(M)", Type = ItemType.Armour, Grade = ItemGrade.Rare, RequiredType = RequiredType.Level, RequiredClass = RequiredClass.Warrior,
            RequiredGender = RequiredGender.Male, Shape = 4, Weight = 23, Light = 0, RequiredAmount = 22, Image = 812, Durability = 25000, StackSize = 1, Price = 9000,
            Effect = 2, ClassBased = true, LevelBased = false, NeedIdentify = true, CanMine = false,
            Bind = BindMode.DontTrade | BindMode.DontStore, Unique = SpecialItemMode.Paralize, RandomStatsId = 3, CanFastRun = true, CanAwakening = true, Slots = 2,
            ToolTip = "Heavy — 重",
        };
        armourInfo.Stats[Stat.MaxAC] = 9;
        armourInfo.Stats[Stat.HP] = -20;
        var torchInfo = new ItemInfo { Index = 77, Name = "Torch", Type = ItemType.Torch, Shape = 0, Light = 9, Durability = 3000, StackSize = 1, LevelBased = true, CanMine = true };
        var armour = new UserItem(armourInfo) { UniqueID = 9_000_000_001UL, CurrentDura = 24000, MaxDura = 25000, Count = 1, Identified = true, GemCount = 2, WeddingRing = -1, IsShopItem = true, GMMade = true };
        armour.AddedStats[Stat.MaxAC] = 3;
        armour.AddedStats[Stat.Luck] = -1;
        armour.ExpireInfo = new ExpireInfo { ExpiryDate = new DateTime(2027, 5, 6, 0, 0, 0, DateTimeKind.Utc) };
        armour.RentalInformation = new RentalInformation { OwnerName = "Owner", BindingFlags = BindMode.DontDrop, ExpiryDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), RentalLocked = true };
        armour.SealedInfo = new SealedInfo { ExpiryDate = new DateTime(2027, 2, 2, 0, 0, 0, DateTimeKind.Utc), NextSealDate = new DateTime(2027, 3, 3, 0, 0, 0, DateTimeKind.Utc) };
        var torch = new UserItem(torchInfo) { UniqueID = 5, CurrentDura = 0, MaxDura = 3000, Count = 1 };
        var fireBall = new ClientMagic
        {
            Name = "FireBall", Spell = Spell.FireBall, BaseCost = 3, LevelCost = 2, Icon = 6, Level1 = 7, Level2 = 11, Level3 = 16, Need1 = 50, Need2 = 350,
            Need3 = 1000, Level = 2, Key = 1, Experience = 123, Delay = 1800, Range = 9, CastTime = 123456789,
        };
        var creature = new ClientIntelligentCreature
        {
            PetType = IntelligentCreatureType.Kitten, Icon = 501, CustomName = "Kitty", Fullness = 7500, SlotIndex = 2,
            Expire = new DateTime(2027, 4, 5, 0, 0, 0, DateTimeKind.Utc), BlackstoneTime = 123456, MaintainFoodTime = 654321,
            petMode = IntelligentCreaturePickupMode.Automatic,
            CreatureRules = new IntelligentCreatureRules { MinimalFullness = 1000, MousePickupEnabled = true, MousePickupRange = 11, AutoPickupEnabled = true, AutoPickupRange = 7, SemiAutoPickupEnabled = false, SemiAutoPickupRange = 3, CanProduceBlackStone = true },
            Filter = new IntelligentCreatureItemFilter { PetPickupAll = false, PetPickupGold = true, PetPickupOthers = true, PickupGrade = ItemGrade.Rare },
        };
        yield return new S.UserInformation
        {
            ObjectID = 1234, RealId = 99, Name = "WebHero", GuildName = "G", GuildRank = "R", NameColour = Color.White, Class = MirClass.Warrior, Gender = MirGender.Male,
            Level = 22, Location = p, Direction = MirDirection.Down, Hair = 1, HP = 300, MP = 40, Experience = 123456789012, MaxExperience = 223456789012,
            LevelEffects = LevelEffects.None, HasHero = true, HeroBehaviour = HeroBehaviour.Attack,
            Inventory = new UserItem[] { null, torch, null }, Equipment = new UserItem[] { null, armour, null, torch }, QuestInventory = null,
            Gold = 1234, Credit = 5,
            Magics = { fireBall, new ClientMagic { Name = "Healing", Spell = Spell.Healing, Key = 0, Delay = 0, CastTime = -1 } },
            IntelligentCreatures = { creature }, SummonedCreatureType = IntelligentCreatureType.Kitten, CreatureSummoned = true, AllowObserve = true, Observer = false,
        };
        var buffStats = new Stats();
        buffStats[Stat.MaxAC] = 7;
        buffStats[Stat.AttackSpeed] = -2;
        yield return new S.AddBuff { Buff = new ClientBuff { Type = BuffType.Haste, Visible = true, ObjectID = 1234, ExpireTime = 60_000, Infinite = false, Paused = true, Stats = buffStats, Values = new[] { 3, -4 } } };
        yield return new S.RemoveBuff { Type = BuffType.Hiding, ObjectID = 1234 };
        yield return new S.PauseBuff { Type = BuffType.Exp, ObjectID = 1234, Paused = true };
        yield return new S.ObjectHidden { ObjectID = 77, Hidden = true };
        yield return new S.Poisoned { Poison = PoisonType.Green | PoisonType.Frozen };
        yield return new S.ObjectPoisoned { ObjectID = 77, Poison = PoisonType.Paralysis };
        yield return new S.PlaySound { Sound = 10156 };
        yield return new S.Opendoor { DoorIndex = 12, Close = true };
        yield return new S.SwitchGroup { AllowGroup = true };
        yield return new S.DeleteGroup();
        yield return new S.DeleteMember { Name = "Ally" };
        yield return new S.GroupInvite { Name = "Leader" };
        yield return new S.AddMember { Name = "Ally" };
        yield return new S.GroupMembersMap { PlayerName = "Ally", PlayerMap = "BichonProvince" };
        yield return new S.SendMemberLocation { MemberName = "Ally", MemberLocation = p };
        yield return new S.NewMagic { Magic = fireBall, Hero = false };
        yield return new S.RemoveMagic { PlaceId = 3 };
        yield return new S.MagicLeveled { ObjectID = 1234, Spell = Spell.FireBall, Level = 2, Experience = 77 };
        yield return new S.Magic { Spell = Spell.FireBall, TargetID = 77, Target = p, Cast = true, Level = 3, SecondaryTargetIDs = { 5, 6 } };
        yield return new S.MagicDelay { ObjectID = 1234, Spell = Spell.FireBall, Delay = 2500 };
        yield return new S.MagicCast { Spell = Spell.Healing };
        yield return new S.ObjectMagic { ObjectID = 77, Location = p, Direction = MirDirection.Left, Spell = Spell.ThunderBolt, TargetID = 9, Target = p, Cast = false, Level = 1, SelfBroadcast = true, SecondaryTargetIDs = { 4 } };
        yield return new S.ObjectEffect { ObjectID = 77, Effect = SpellEffect.Healing, EffectType = 3, DelayTime = 100, Time = 2000 };
        yield return new S.ObjectProjectile { Spell = Spell.FireBall, Source = 1, Destination = 2 };
        yield return new S.SpellToggle { ObjectID = 1234, Spell = Spell.Thrusting, CanUse = true };
        yield return new S.MapEffect { Location = p, Effect = SpellEffect.Healing, Value = 9 };
        yield return new S.ObjectSpell { ObjectID = 88, Location = p, Spell = Spell.FireWall, Direction = MirDirection.Up, Param = true };
        yield return new S.NewItemInfo { Info = armourInfo };
        yield return new S.PlayerUpdate { ObjectID = 4000000001, Light = 9, Weapon = 12, WeaponEffect = 3, Armour = 5, WingEffect = 2 };
        yield return new S.MoveItem { Grid = MirGridType.Inventory, From = 7, To = 12, Success = true };
        yield return new S.EquipItem { Grid = MirGridType.Inventory, UniqueID = 9_000_000_001UL, To = 1, Success = true };
        yield return new S.MergeItem { GridFrom = MirGridType.Equipment, GridTo = MirGridType.Inventory, IDFrom = 11, IDTo = 12, Success = false };
        yield return new S.RemoveItem { Grid = MirGridType.Inventory, UniqueID = 5, To = 9, Success = true };
        yield return new S.SplitItem { Item = torch, Grid = MirGridType.Inventory };
        yield return new S.SplitItem1 { Grid = MirGridType.Storage, UniqueID = 6, Count = 3, Success = true };
        yield return new S.UseItem { UniqueID = 7, Success = true, Grid = MirGridType.Inventory };
        yield return new S.DropItem { UniqueID = 8, Count = 2, HeroItem = false, Success = true };
        yield return new S.GainedItem { Item = armour };
        yield return new S.DeleteItem { UniqueID = 9, Count = 1 };
        yield return new S.RefreshItem { Item = torch };
        yield return new S.DuraChanged { UniqueID = 10, CurrentDura = 1234 };
        yield return new S.ItemRepaired { UniqueID = 10, MaxDura = 5000, CurrentDura = 4999 };
        yield return new S.GainedGold { Gold = 3_000_000_000 };
        yield return new S.LoseGold { Gold = 15 };
        yield return new S.LevelChanged { Level = 23, Experience = 5, MaxExperience = 9_000_000_000 };
        yield return new S.TradeRequest { Name = "WebAlly" };
        yield return new S.TradeAccept { Name = "WebAlly" };
        yield return new S.TradeGold { Amount = 3_000_000_000 };
        yield return new S.TradeItem { TradeItems = new UserItem[] { torch, null, armour, null } };
        yield return new S.TradeConfirm();
        yield return new S.TradeCancel { Unlock = true };
        yield return new S.DepositTradeItem { From = 12, To = 3, Success = true };
        yield return new S.RetrieveTradeItem { From = 3, To = 40, Success = false };
        yield return new S.GuildStatus
        {
            GuildName = "Crystal", GuildRankName = "Leader", Level = 2, Experience = 12_345_678_901, MaxExperience = 99_000_000_000, Gold = 4_000_000_000,
            SparePoints = 3, MemberCount = 7, MaxMembers = 20, Voting = true, ItemCount = 8, BuffCount = 2,
            MyOptions = GuildRankOptions.CanRecruit | GuildRankOptions.CanActivateBuff, MyRankId = 1,
        };
        yield return new S.GuildInvite { Name = "Crystal" };
        yield return new S.GuildNameRequest();
        yield return new S.GuildNoticeChange { update = 2, notice = { "Welcome", "Raid at 9 — 欢迎" } };
        var leaderRank = new GuildRank { Name = "Leader", Index = 0, Options = (GuildRankOptions)255 };
        leaderRank.Members.Add(new GuildMember { Name = "WebHero", Id = 11, LastLogin = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc), hasvoted = true, Online = true });
        var memberRank = new GuildRank { Name = "Members", Index = 1, Options = GuildRankOptions.CanStoreItem };
        memberRank.Members.Add(new GuildMember { Name = "WebAlly", Id = 12, Online = false });
        memberRank.Members.Add(new GuildMember { Name = "Third", Id = 13, Online = true });
        yield return new S.GuildMemberChange { Name = "", RankIndex = 0, Status = 255, Ranks = { leaderRank, memberRank } };
        yield return new S.GuildExpGain { Amount = 1234 };
        yield return new S.GuildStorageGoldChange { Amount = 3_000_000_000, Type = 1, Name = "WebHero" };
        yield return new S.GuildStorageItemChange { Type = 2, To = 5, From = 111, User = 42, Item = new GuildStorageItem { Item = armour, UserId = 9_000_000_000 } };
        yield return new S.GuildStorageList { Items = new[] { null, new GuildStorageItem { Item = torch, UserId = 7 }, null, new GuildStorageItem { Item = armour, UserId = -1 } } };
        yield return new S.GuildRequestWar();
        var buffStats2 = new Stats();
        buffStats2[Stat.HP] = 50;
        buffStats2[Stat.ExpRatePercent] = 5;
        yield return new S.GuildBuffList
        {
            Remove = 1,
            ActiveBuffs = { new GuildBuff { Id = 3, Active = true, ActiveTimeRemaining = 42 }, new GuildBuff { Id = 9, Active = false, ActiveTimeRemaining = 0 } },
            GuildBuffs = { new GuildBuffInfo { Id = 3, Icon = 12, Name = "Max Health — 生命", LevelRequirement = 2, PointsRequirement = 1, TimeLimit = 120, ActivationCost = 30000, Stats = buffStats2 } },
        };
        yield return new S.ObjectGuildNameChanged { ObjectID = 4000000001, GuildName = "Crystal" };
        yield return new S.ColourChanged { NameColour = red };
        yield return new S.ObjectColourChanged { ObjectID = 77, NameColour = Color.Blue };
        yield return new S.ObjectLeveled { ObjectID = 77 };
        yield return new S.ObjectName { ObjectID = 77, Name = "Renamed — 名" };
        yield return new S.ObjectHide { ObjectID = 77 };
        yield return new S.ObjectShow { ObjectID = 77 };
        yield return new S.TeleportIn();
        yield return new S.ObjectSitDown { ObjectID = 77, Location = p, Direction = MirDirection.Left, Sitting = true };
        yield return new S.ObjectHarvest { ObjectID = 77, Location = p, Direction = MirDirection.Up };
        yield return new S.ObjectHarvested { ObjectID = 77, Location = p, Direction = MirDirection.Down };
        yield return new S.ObjectMana { ObjectID = 77, Percent = 55 };
        yield return new S.ObjectLevelEffects { ObjectID = 77, LevelEffects = LevelEffects.Mist | LevelEffects.RedDragon };
        yield return new S.ObjectDeco { ObjectID = 99, Location = p, Image = 123456 };
        yield return new S.ObjectSneaking { ObjectID = 77, SneakingActive = true };
        yield return new S.DefaultNPC { ObjectID = 4000000001 };
        yield return new S.NPCImageUpdate { ObjectID = 88, Image = 33, Colour = red };
        yield return new S.RangeAttack { TargetID = 77, Target = p, Spell = Spell.Focus };
        yield return new S.ObjectRangeAttack { ObjectID = 77, Location = p, Direction = MirDirection.Right, TargetID = 9, Target = new Point(5, 6), Type = 2, Spell = Spell.Focus, Level = 3 };
        yield return new S.SetConcentration { ObjectID = 77, Enabled = true, Interrupted = false };
        yield return new S.SetElemental { ObjectID = 77, Enabled = true, Casted = true, Value = 4_000_000_000, ElementType = 3, ExpLast = 99 };
        yield return new S.SetBindingShot { ObjectID = 77, Enabled = true, Value = 9_000_000_000 };
        yield return new S.UserDash { Location = p, Direction = MirDirection.UpLeft };
        yield return new S.ObjectDash { ObjectID = 77, Location = p, Direction = MirDirection.Up };
        yield return new S.UserDashFail { Location = p, Direction = MirDirection.Down };
        yield return new S.ObjectDashFail { ObjectID = 77, Location = p, Direction = MirDirection.Left };
        yield return new S.UserBackStep { Location = p, Direction = MirDirection.Right };
        yield return new S.ObjectBackStep { ObjectID = 77, Location = p, Direction = MirDirection.DownRight, Distance = 3 };
        yield return new S.UserDashAttack { Location = p, Direction = MirDirection.UpRight };
        yield return new S.ObjectDashAttack { ObjectID = 77, Location = p, Direction = MirDirection.DownLeft, Distance = 4 };
        yield return new S.UserAttackMove { Location = p, Direction = MirDirection.Left };
        yield return new S.InTrapRock { Trapped = true };
        yield return new S.RemoveDelayedExplosion { ObjectID = 77 };
        var monStats = new Stats();
        monStats[Stat.MaxDC] = 40;
        yield return new S.NewMonsterInfo { Info = new ClientMonsterInfo { Index = 12, Name = "OmaKing", GameName = "Oma King", Image = Monster.OmaKing, AI = 3, Effect = 1, Level = 45, ViewRange = 7, CoolEye = 50, Light = 2, AttackSpeed = 2500, MoveSpeed = 1000, Experience = 4_000_000_000, CanPush = true, CanTame = false, AutoRev = true, Undead = false, CanRecall = true, Stats = monStats } };
        var npcInfo = new ClientNPCInfo { Index = 5, FileName = "Bichon/Shop", Name = "Merchant_John", MapIndex = 1, Location = p, Image = 14, Rate = 120, ShowOnBigMap = true, BigMapIcon = 7, ObjectID = 88, Icon = 3, CanTeleportTo = true }; // Icon != 0: the reader keeps it (Icon == 0 would become BigMapIcon)
        yield return new S.NewNPCInfo { Info = npcInfo };
        yield return new S.NewMapInfo
        {
            MapIndex = 3,
            Info = new ClientMapInfo
            {
                Title = "Bichon Province", Width = 700, Height = 700, BigMap = 2,
                Movements = { new ClientMovementInfo { Destination = 4, Title = "Cave", Location = p, Icon = 2 } },
                NPCs = { npcInfo },
            },
        };
        yield return new S.WorldMapSetupInfo { Setup = new WorldMapSetup { Enabled = true, Icons = { new WorldMapIcon { ImageIndex = 3, Title = "Bichon", MapIndex = 1 } } }, TeleportToNPCCost = 5000 };
        yield return new S.SearchMapResult { MapIndex = 3, NPCIndex = 4000000001 };
        yield return new S.ChangeQuest { Quest = new ClientQuestProgress { Id = 12, TaskList = { "Kill 5 Hens (2/5)", "Bring 皮 x3" }, Taken = true, Completed = false, New = true }, QuestState = QuestState.Update, TrackQuest = true };
        yield return new S.CompleteQuest { CompletedQuests = { 1, 2, 300 } };
        yield return new S.ShareQuest { QuestIndex = 12, SharerName = "WebAlly" };
        yield return new S.NewQuestInfo
        {
            Info = new ClientQuestInfo
            {
                Index = 12, NPCIndex = 4000000001, Name = "Hen Hunt", Group = "Bichon", Description = { "Line 1", "Line 2" }, TaskDescription = { "Kill hens" },
                ReturnDescription = { "Return to me" }, CompletionDescription = { "Well done — 好" }, MinLevelNeeded = 1, MaxLevelNeeded = 40, QuestNeeded = 3,
                ClassNeeded = RequiredClass.Warrior, Type = QuestType.Repeatable, TimeLimitInSeconds = 600, RewardGold = 4_000_000_000, RewardExp = 500, RewardCredit = 7,
                RewardsFixedItem = { new QuestItemReward { Item = torchInfo, Count = 2 } }, RewardsSelectItem = { new QuestItemReward { Item = armourInfo, Count = 1 } },
                FinishNPCIndex = 88,
            },
        };
        yield return new S.GainedQuestItem { Item = torch };
        yield return new S.DeleteQuestItem { UniqueID = 9_000_000_001UL, Count = 3 };
        yield return new S.ReceiveMail { Mail = { new ClientMail { MailID = 9_000_000_003UL, SenderName = "WebAlly", Message = "Hi — 你好", Opened = true, Locked = false, CanReply = true, Collected = false, DateSent = new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc), Gold = 3_000_000_000, Items = { torch } } } };
        yield return new S.MailLockedItem { UniqueID = 5, Locked = true };
        yield return new S.MailSendRequest();
        yield return new S.MailSent { Result = -1 };
        yield return new S.ParcelCollected { Result = -2 };
        yield return new S.MailCost { Cost = 3_000_000_000 };
        yield return new S.FriendUpdate { Friends = { new ClientFriend { Index = 3, Name = "WebAlly", Memo = "trade partner", Blocked = false, Online = true }, new ClientFriend { Index = 9, Name = "Spammer", Memo = "", Blocked = true, Online = false } } };
        yield return new S.MarriageRequest { Name = "WebAlly" };
        yield return new S.DivorceRequest { Name = "WebAlly" };
        yield return new S.LoverUpdate { Name = "WebAlly", Date = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), MapName = "Bichon", MarriedDays = 283 };
        yield return new S.MentorRequest { Name = "WebAlly", Level = 30 };
        yield return new S.MentorUpdate { Name = "WebAlly", Level = 30, Online = true, MenteeEXP = 9_000_000_000 };
        yield return new S.PlayerInspect { Name = "WebAlly", GuildName = "WebGuild", GuildRank = "Officers", Equipment = new UserItem[] { torch, null, armour }, Class = MirClass.Warrior, Gender = MirGender.Female, Hair = 2, Level = 30, LoverName = "WebHero", AllowObserve = true, IsHero = false };
        yield return new S.AllowObserve { Allow = true };
        yield return new S.Rankings { RankType = 2, MyRank = 5, ListingDetails = { new RankCharacterInfo { PlayerId = 9_000_000_004, Name = "WebHero", level = 30, Class = MirClass.Warrior } }, Listings = { 9_000_000_004, 7 }, Count = 2 };
        yield return new S.NPCStorage();
        yield return new S.UserStorage { Storage = new UserItem[] { torch, null, armour } };
        yield return new S.UserSlotsRefresh { Inventory = new UserItem[] { torch, null, armour } }; // Equipment stays null (not sent)
        yield return new S.StoreItem { From = 7, To = 80, Success = true };
        yield return new S.TakeBackItem { From = 80, To = 7, Success = false };
        yield return new S.StorageUnlockResult { Result = 2, HasPassword = true };
        yield return new S.StoragePasswordResult { Result = 4, Removing = false, HasPassword = true, LastSetTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        yield return new S.ResizeInventory { Size = 86 };
        yield return new S.ResizeStorage { Size = 160, HasExpandedStorage = true, ExpiryTime = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        yield return new S.NPCRefine { Rate = 1.5F, Refining = true };
        yield return new S.NPCCheckRefine();
        yield return new S.NPCCollectRefine { Success = true };
        yield return new S.DepositRefineItem { From = 9, To = 2, Success = true };
        yield return new S.RetrieveRefineItem { From = 2, To = 9, Success = false };
        yield return new S.RefineItem { UniqueID = 9_000_000_001UL };
        yield return new S.RefineCancel { Unlock = true };
        yield return new S.NPCReplaceWedRing { Rate = 0.75F };
        yield return new S.CraftItem { Success = true };
        yield return new S.NewRecipeInfo { Info = new ClientRecipeInfo { Gold = 3_000_000_000, Chance = 75, Item = armour, Tools = { torch }, Ingredients = { torch, armour } } };
        yield return new S.NPCAwakening();
        yield return new S.AwakeningNeedMaterials { Materials = new[] { armourInfo, null, torchInfo }, MaterialsCount = new byte[] { 3, 0, 9 } };
        yield return new S.AwakeningLockedItem { UniqueID = 5, Locked = true };
        yield return new S.Awakening { result = -2, removeID = 9_000_000_001 };
        yield return new S.NPCDisassemble();
        yield return new S.NPCDowngrade();
        yield return new S.NPCReset();
        yield return new S.NPCPearlGoods { List = { torch, armour }, Rate = 2.5F, Type = PanelType.Buy };
        yield return new S.NPCConsign();
        var auction = new ClientAuction { AuctionID = 9_000_000_002UL, Item = armour, Seller = "Seller — 卖", Price = 4_000_000_000, ConsignmentDate = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), ItemType = MarketItemType.Auction };
        yield return new S.NPCMarket { Listings = { auction }, Pages = 3, UserMode = true };
        yield return new S.NPCMarketPage { Listings = { auction, auction } };
        yield return new S.ConsignItem { UniqueID = 5, Success = true };
        yield return new S.MarketFail { Reason = 6 };
        yield return new S.MarketSuccess { Message = "Sold for 100 gold" };
        yield return new S.RemoveSlotItem { Grid = MirGridType.Socket, GridTo = MirGridType.Inventory, UniqueID = 5, To = 12, Success = true };
        yield return new S.ItemSlotSizeChanged { UniqueID = 5, SlotSize = 3 };
        yield return new S.EquipSlotItem { Grid = MirGridType.Inventory, UniqueID = 5, To = 1, GridTo = MirGridType.Socket, Success = true };
        yield return new S.ItemSealChanged { UniqueID = 5, ExpiryDate = new DateTime(2027, 2, 2, 0, 0, 0, DateTimeKind.Utc) };
        yield return new S.CombineItem { Grid = MirGridType.Inventory, IDFrom = 5, IDTo = 9_000_000_001UL, Success = true, Destroy = true };
        yield return new S.ItemUpgraded { Item = armour };
        yield return new S.GetRentedItems { RentedItems = { new ItemRentalInformation { ItemId = 5, ItemName = "Torch", RentingPlayerName = "WebAlly", ItemReturnDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) } } };
        yield return new S.ItemRentalRequest { Name = "WebAlly", Renting = true };
        yield return new S.ItemRentalFee { Amount = 3_000_000_000 };
        yield return new S.ItemRentalPeriod { Days = 7 };
        yield return new S.DepositRentalItem { From = 12, To = 0, Success = true };
        yield return new S.RetrieveRentalItem { From = 0, To = 12, Success = true };
        yield return new S.UpdateRentalItem { HasData = true, LoanItem = torch };
        yield return new S.CancelItemRental();
        yield return new S.ItemRentalLock { Success = true, GoldLocked = true, ItemLocked = false };
        yield return new S.ItemRentalPartnerLock { GoldLocked = false, ItemLocked = true };
        yield return new S.CanConfirmItemRental();
        yield return new S.ConfirmItemRental();
        yield return new S.GainExperience { Amount = 4_000_000_000 };
        var baseStats = new BaseStats(MirClass.Warrior);
        baseStats.Stats[0].Max = 9999;
        baseStats.Caps[Stat.Luck] = 10;
        yield return new S.BaseStatsInfo { Stats = baseStats };
        yield return new S.LogOutSuccess { Characters = { new SelectInfo { Index = 3, Name = "Gamma", Level = 9, Class = MirClass.Taoist, Gender = MirGender.Male } } };
        yield return new S.LogOutFailed();
        yield return new S.ReturnToLogin();
        yield return new S.ChangeAMode { Mode = AttackMode.RedBrown };
        yield return new S.ChangePMode { Mode = PetMode.FocusMasterTarget };
        yield return new S.ChangePassword { Result = 6 };
        yield return new S.ChangePasswordBanned { Reason = "Spam", ExpiryDate = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        yield return new S.DeleteCharacter { Result = 1 };
        yield return new S.DeleteCharacterSuccess { CharacterIndex = 42 };
        yield return new S.StartGameBanned { Reason = "Botting", ExpiryDate = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        yield return new S.StartGameDelay { Milliseconds = 12_345 };
        yield return new S.NPCRequestInput { NPCID = 88, PageName = "[@INPUT]" };
        yield return new S.SendOutputMessage { Message = "Quest updated", Type = OutputMessageType.Quest };
        yield return new S.UpdateNotice { Notice = new Notice { Title = "Maintenance", Message = "Back at 10:00 — 维护" } };
        yield return new S.SetTimer { Key = "event", Type = 1, Seconds = 300 };
        yield return new S.ExpireTimer { Key = "event" };
        yield return new S.SetCompass { Location = p };
        yield return new S.Roll { Type = 2, Page = "[@ROLLED]", Result = 5, AutoRoll = true };
        yield return new S.OpenBrowser { Url = "https://example.invalid/" };
        yield return new S.NewChatItem { Item = torch };
        yield return new S.ChatItemStats { ChatItemId = 5, Stats = armour };
        yield return new S.Chat { Message = "Hello 世界", Type = ChatType.Shout };
        yield return new S.ObjectChat { ObjectID = 77, Text = "Hero: hi", Type = ChatType.Normal };
        // — Section 5: mounts, transform, fishing, reincarnation, game shop, guild territories, intelligent creatures —
        yield return new S.MountUpdate { ObjectID = 1234, MountType = 7, RidingMount = true };
        yield return new S.TransformUpdate { ObjectID = 1234, TransformType = -1 };
        yield return new S.FishingUpdate { ObjectID = 1234, Fishing = true, ProgressPercent = 45, ChancePercent = 80, FishingPoint = p, FoundFish = true };
        yield return new S.RequestReincarnation();
        yield return new S.CancelReincarnation();
        yield return new S.GameShopInfo
        {
            Item = new GameShopItem
            {
                ItemIndex = 31, GIndex = 4, Info = armourInfo, GoldPrice = 100_000, CreditPrice = 25, Count = 3, Class = "Warrior", Category = "Armours",
                Stock = 9, iStock = true, Deal = true, TopItem = false, Date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), CanBuyCredit = true, CanBuyGold = false,
            },
            StockLevel = 5,
        };
        yield return new S.GameShopStock { GIndex = 4, StockLevel = 0 };
        yield return new S.GainedCredit { Credit = 150 };
        yield return new S.LoseCredit { Credit = 25 };
        yield return new S.GuildTerritoryPage
        {
            length = 12,
            Listings =
            {
                new ClientGTMap { index = 0, Name = "Bichon Wall", Owner = "WebGuild", Leader = "WebHero", Leader2 = "", price = 5_000_000, days = 7, begin = 1 },
                new ClientGTMap { index = 1, Name = "Mongchon", Owner = "", Leader = "", Leader2 = "", price = 0, days = 0, begin = 0 },
            }
        };
        yield return new S.NewIntelligentCreature { Creature = creature };
        yield return new S.UpdateIntelligentCreatureList { CreatureList = { creature, creature }, CreatureSummoned = true, SummonedCreatureType = IntelligentCreatureType.Kitten, PearlCount = 42 };
        yield return new S.IntelligentCreatureEnableRename();
        yield return new S.IntelligentCreaturePickup { ObjectID = 77 };
        // — Section 5: heroes —
        yield return new S.ObjectHero
        {
            ObjectID = 4000000002, Name = "Sidekick", GuildName = "", GuildRankName = "", NameColour = red, Class = MirClass.Taoist,
            Gender = MirGender.Male, Level = 12, Location = p, Direction = MirDirection.Left, Hair = 1, Light = 0, Weapon = 3, WeaponEffect = 0,
            Armour = 2, Poison = PoisonType.None, Dead = false, Hidden = false, Effect = SpellEffect.None, WingEffect = 0, Extra = false,
            MountType = -1, RidingMount = false, Fishing = false, TransformType = -1, ElementOrbEffect = 0, ElementOrbLvl = 0, ElementOrbMax = 0,
            Buffs = { BuffType.Haste }, LevelEffects = LevelEffects.None, OwnerName = "WebHero",
        };
        var heroInfo = new ClientHeroInformation { Index = 5, Name = "Sidekick", Level = 12, Class = MirClass.Taoist, Gender = MirGender.Female };
        yield return new S.NewHeroInfo { Info = heroInfo, StorageIndex = 2 };
        yield return new S.TakeBackHeroItem { From = 3, To = 46, Success = true };
        yield return new S.TransferHeroItem { From = 46, To = 3, Success = false };
        yield return new S.HeroHealthChanged { HP = 120, MP = 45 };
        yield return new S.GainHeroExperience { Amount = 3_000_000_000 };
        yield return new S.HeroLevelChanged { Level = 13, Experience = 12345, MaxExperience = 9_876_543_210 };
        var heroStats = new BaseStats(MirClass.Taoist);
        heroStats.Caps[Stat.Luck] = 7;
        yield return new S.HeroBaseStatsInfo { Stats = heroStats };
        yield return new S.HeroCreateRequest { CanCreateClass = new[] { true, true, false, true, false } };
        yield return new S.NewHero { Result = 10 };
        yield return new S.HeroInformation
        {
            ObjectID = 4000000002, Name = "Sidekick", Class = MirClass.Taoist, Gender = MirGender.Female, Level = 12, Hair = 2, HP = 120, MP = 45,
            Experience = 777, MaxExperience = 88_888_888_888,
            Inventory = new UserItem[] { torch, null }, Equipment = null,
            Magics = { new ClientMagic { Name = "Healing", Spell = Spell.Healing, Key = 17, Delay = 0, CastTime = 5 } },
            AutoPot = true, AutoHPPercent = 40, AutoMPPercent = 20, HPItemIndex = 40, MPItemIndex = 0,
        };
        yield return new S.UpdateHeroSpawnState { State = HeroSpawnState.Summoned };
        yield return new S.UnlockHeroAutoPot();
        yield return new S.SetAutoPotValue { Stat = Stat.MP, Value = 35 };
        yield return new S.SetAutoPotItem { Grid = MirGridType.HeroHPItem, ItemIndex = 40 };
        yield return new S.SetHeroBehaviour { Behaviour = HeroBehaviour.Follow };
        yield return new S.ManageHeroes { MaximumCount = 4, CurrentHero = heroInfo, Heroes = new ClientHeroInformation[] { null, heroInfo, null } };
        yield return new S.ChangeHero { FromIndex = 1 };
        // — Section 6: web admin LiveDb push —
        var gdcStats = new Stats();
        gdcStats[Stat.MaxAC] = 4;
        yield return new S.GameDataChanged
        {
            Items = { torchInfo, armourInfo },
            Monsters = { new ClientMonsterInfo { Index = 13, Name = "Hen", GameName = "Hen", Image = Monster.Hen, AI = 1, Effect = 0, Level = 1, ViewRange = 7, CoolEye = 0, Light = 0, AttackSpeed = 2500, MoveSpeed = 1800, Experience = 5, CanPush = true, CanTame = true, AutoRev = false, Undead = false, CanRecall = false, Stats = gdcStats } },
            Npcs = { new ClientNPCInfo { Index = 6, FileName = "Bichon/Inn", Name = "InnKeeper", MapIndex = 1, Location = p, Image = 9, Rate = 100, ShowOnBigMap = false, BigMapIcon = 2, ObjectID = 0, Icon = 4, CanTeleportTo = false } },
            Quests = { new ClientQuestInfo { Index = 13, NPCIndex = 5, Name = "Errand", Group = "", Description = { "Go" }, TaskDescription = { }, ReturnDescription = { }, CompletionDescription = { }, MinLevelNeeded = 1, MaxLevelNeeded = 99, QuestNeeded = 0, ClassNeeded = RequiredClass.Taoist, Type = QuestType.General, TimeLimitInSeconds = 0, RewardGold = 10, RewardExp = 20, RewardCredit = 0, FinishNPCIndex = 5 } },
            Magics = { fireBall },
            HeroMagics = { new ClientMagic { Name = "Healing", Spell = Spell.Healing, Key = 17, Delay = 900, CastTime = 0 } },
            GameShop = { new S.GameDataChanged.ShopEntry { Item = new GameShopItem { ItemIndex = 77, GIndex = 8, Info = torchInfo, GoldPrice = 50, CreditPrice = 5, Count = 1, Class = "All", Category = "Misc", Stock = 0, iStock = false, Deal = false, TopItem = true, Date = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), CanBuyCredit = true, CanBuyGold = true }, StockLevel = 12 } },
            GameShopRemoved = { 4, 9 },
            MapInfoInvalidated = { 3 },
            Last = true,
        };
        yield return new S.MapAtmosphere { Lights = LightSetting.Night, MapDarkLight = 2, Weather = WeatherSetting.Fog | WeatherSetting.Rain };
    }

    private static IEnumerable<Packet> ClientSamples()
    {
        yield return new C.ClientVersion { VersionHash = new byte[16] };
        yield return new C.KeepAlive { Time = 42 };
        yield return new C.Login { AccountID = "acct", Password = "pässword" };
        yield return new C.NewAccount
        {
            AccountID = "acct", Password = "pw123456", BirthDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), UserName = "User",
            SecretQuestion = "Q", SecretAnswer = "A", EMailAddress = "a@b.c",
        };
        yield return new C.NewCharacter { Name = "Hero", Gender = MirGender.Female, Class = MirClass.Archer };
        yield return new C.StartGame { CharacterIndex = 7 };
        yield return new C.LogOut();
        yield return new C.Turn { Direction = MirDirection.Left };
        yield return new C.Walk { Direction = MirDirection.Up };
        yield return new C.Run { Direction = MirDirection.DownRight };
        yield return new C.Attack { Direction = MirDirection.Right, Spell = Spell.None };
        yield return new C.Chat { Message = "look <Torch/5>", LinkedItems = { new ChatItem { UniqueID = 5, Title = "Torch", Grid = MirGridType.Inventory } } };
        yield return new C.MoveItem { Grid = MirGridType.Inventory, From = 6, To = 45 };
        yield return new C.EquipItem { Grid = MirGridType.Inventory, UniqueID = 9_000_000_001UL, To = 1 };
        yield return new C.RemoveItem { Grid = MirGridType.Inventory, UniqueID = 5, To = 10 };
        yield return new C.MergeItem { GridFrom = MirGridType.Equipment, GridTo = MirGridType.Inventory, IDFrom = 11, IDTo = 12 };
        yield return new C.UseItem { UniqueID = 7, Grid = MirGridType.Inventory };
        yield return new C.DropItem { UniqueID = 8, Count = 2, HeroInventory = false };
        yield return new C.PickUp();
        yield return new C.Opendoor { DoorIndex = 200 };
        yield return new C.SwitchGroup { AllowGroup = false };
        yield return new C.AddMember { Name = "Ally" };
        yield return new C.DelMember { Name = "Ally" };
        yield return new C.GroupInvite { AcceptInvite = true };
        yield return new C.CallNPC { ObjectID = 88, Key = "[@Main]" };
        yield return new C.Magic { ObjectID = 1234, Spell = Spell.FireBall, Direction = MirDirection.Right, TargetID = 77, Location = new Point(10, 20), SpellTargetLock = true };
        yield return new C.MagicKey { Spell = Spell.FireBall, Key = 2, OldKey = 1 };
        yield return new C.SpellToggle { Spell = Spell.Thrusting, canUse = SpellToggleState.False };
        yield return new C.BuyItem { ItemIndex = 9_000_000_001UL, Count = 20, Type = PanelType.BuySub };
        yield return new C.SellItem { UniqueID = 5, Count = 2 };
        yield return new C.RepairItem { UniqueID = 6 };
        yield return new C.SRepairItem { UniqueID = 7 };
        yield return new C.AcceptQuest { NPCIndex = 4000000001, QuestIndex = 12 };
        yield return new C.FinishQuest { QuestIndex = 12, SelectedItemIndex = -1 };
        yield return new C.AbandonQuest { QuestIndex = 12 };
        yield return new C.ShareQuest { QuestIndex = 12 };
        yield return new C.SendMail { Name = "WebAlly", Message = "Hi — 你好", Gold = 3_000_000_000, ItemsIdx = new ulong[] { 5, 0, 9_000_000_001UL, 0, 0 }, Stamped = true };
        yield return new C.ReadMail { MailID = 9_000_000_003UL };
        yield return new C.CollectParcel { MailID = 9_000_000_003UL };
        yield return new C.DeleteMail { MailID = 9_000_000_003UL };
        yield return new C.LockMail { MailID = 9_000_000_003UL, Lock = true };
        yield return new C.MailLockedItem { UniqueID = 5, Locked = true };
        yield return new C.MailCost { Gold = 100, ItemsIdx = new ulong[] { 5, 0, 0, 0, 9_000_000_001UL }, Stamped = false };
        yield return new C.AddFriend { Name = "WebAlly", Blocked = true };
        yield return new C.RemoveFriend { CharacterIndex = 3 };
        yield return new C.RefreshFriends();
        yield return new C.AddMemo { CharacterIndex = 3, Memo = "trade partner 商" };
        yield return new C.MarriageRequest();
        yield return new C.MarriageReply { AcceptInvite = true };
        yield return new C.ChangeMarriage();
        yield return new C.DivorceRequest();
        yield return new C.DivorceReply { AcceptInvite = false };
        yield return new C.AddMentor { Name = "WebAlly" };
        yield return new C.MentorReply { AcceptInvite = true };
        yield return new C.AllowMentor();
        yield return new C.CancelMentor();
        yield return new C.Inspect { ObjectID = 4000000001, Ranking = true, Hero = false };
        yield return new C.Observe { Name = "WebAlly" };
        yield return new C.GetRanking { RankType = 2, RankIndex = 20, OnlineOnly = true };
        yield return new C.StoreItem { From = 7, To = 80 };
        yield return new C.TakeBackItem { From = 80, To = 7 };
        yield return new C.UnlockStorage { Password = "pässwörd" };
        yield return new C.SetStoragePassword { CurrentPassword = "old", NewPassword = "new" };
        yield return new C.BuyItemBack { UniqueID = 9_000_000_001UL, Count = 3 };
        yield return new C.DepositRefineItem { From = 9, To = 2 };
        yield return new C.RetrieveRefineItem { From = 2, To = 9 };
        yield return new C.RefineCancel();
        yield return new C.RefineItem { UniqueID = 5 };
        yield return new C.CheckRefine { UniqueID = 6 };
        yield return new C.ReplaceWedRing { UniqueID = 7 };
        yield return new C.CraftItem { UniqueID = 8, Count = 2, Slots = new[] { 6, 7, 45 } };
        yield return new C.AwakeningNeedMaterials { UniqueID = 9, Type = AwakeType.MAC };
        yield return new C.AwakeningLockedItem { UniqueID = 10, Locked = true };
        yield return new C.Awakening { UniqueID = 11, Type = AwakeType.HPMP, PositionIdx = 4_000_000_000 };
        yield return new C.DisassembleItem { UniqueID = 12 };
        yield return new C.DowngradeAwakening { UniqueID = 13 };
        yield return new C.ResetAddedItem { UniqueID = 14 };
        yield return new C.ConsignItem { UniqueID = 15, Price = 4_000_000_000, Type = MarketPanelType.Auction };
        yield return new C.MarketSearch { Match = "Sword 剑", Type = ItemType.Weapon, Usermode = true, MinShape = -1, MaxShape = 300, MarketType = MarketPanelType.Consign };
        yield return new C.MarketRefresh();
        yield return new C.MarketPage { Page = 4 };
        yield return new C.MarketBuy { AuctionID = 9_000_000_002UL, BidPrice = 4_000_000_000 };
        yield return new C.MarketGetBack { Mode = MarketCollectionMode.Expired, AuctionID = 9_000_000_002UL };
        yield return new C.MarketSellNow { AuctionID = 9_000_000_002UL };
        yield return new C.RemoveSlotItem { Grid = MirGridType.Socket, GridTo = MirGridType.Inventory, UniqueID = 5, To = 12, FromUniqueID = 9_000_000_001UL };
        yield return new C.EquipSlotItem { Grid = MirGridType.Inventory, UniqueID = 5, To = 1, GridTo = MirGridType.Socket, ToUniqueID = 9_000_000_001UL };
        yield return new C.CombineItem { Grid = MirGridType.Inventory, IDFrom = 5, IDTo = 9_000_000_001UL };
        yield return new C.ItemRentalRequest();
        yield return new C.ItemRentalFee { Amount = 3_000_000_000 };
        yield return new C.ItemRentalPeriod { Days = 7 };
        yield return new C.DepositRentalItem { From = 12, To = 0 };
        yield return new C.RetrieveRentalItem { From = 0, To = 12 };
        yield return new C.CancelItemRental();
        yield return new C.ItemRentalLockFee();
        yield return new C.ItemRentalLockItem();
        yield return new C.ConfirmItemRental();
        yield return new C.GetRentedItems();
        yield return new C.Harvest { Direction = MirDirection.DownLeft };
        yield return new C.RangeAttack { Direction = MirDirection.Right, Location = new Point(10, 20), TargetID = 4000000001, TargetLocation = new Point(15, 22) };
        yield return new C.RequestMapInfo { MapIndex = 3 };
        yield return new C.RequestMonsterInfo { MonsterIndex = 12 };
        yield return new C.RequestNPCInfo { NPCIndex = 5 };
        yield return new C.RequestItemInfo { ItemIndex = 31 };
        yield return new C.SearchMap { Text = "Merchant 商" };
        yield return new C.TeleportToNPC { ObjectID = 88 };
        yield return new C.TownRevive();
        yield return new C.SplitItem { Grid = MirGridType.Inventory, UniqueID = 9_000_000_001UL, Count = 7 };
        yield return new C.DropGold { Amount = 3_000_000_000 };
        yield return new C.ChangeAMode { Mode = AttackMode.Guild };
        yield return new C.ChangePMode { Mode = PetMode.None };
        yield return new C.ChangePassword { AccountID = "acct", CurrentPassword = "old", NewPassword = "nëw" };
        yield return new C.DeleteCharacter { CharacterIndex = 42 };
        yield return new C.NPCConfirmInput { NPCID = 88, PageName = "[@INPUT]", Value = "yes" };
        yield return new C.RequestChatItem { ChatItemID = 9_000_000_001UL };
        yield return new C.TradeRequest();
        yield return new C.TradeReply { AcceptInvite = true };
        yield return new C.TradeGold { Amount = 3_000_000_000 };
        yield return new C.TradeConfirm { Locked = true };
        yield return new C.TradeCancel();
        yield return new C.DepositTradeItem { From = 12, To = 3 };
        yield return new C.RetrieveTradeItem { From = 3, To = 40 };
        yield return new C.EditGuildMember { ChangeType = 2, RankIndex = 1, Name = "WebAlly", RankName = "Members" };
        yield return new C.EditGuildNotice { notice = { "Line one", "", "第三" } };
        yield return new C.GuildInvite { AcceptInvite = false };
        yield return new C.RequestGuildInfo { Type = 1 };
        yield return new C.GuildNameReturn { Name = "Crystal" };
        yield return new C.GuildBuffUpdate { Action = 2, Id = 1234567 };
        yield return new C.GuildWarReturn { Name = "EnemyGuild" };
        yield return new C.GuildStorageGoldChange { Type = 1, Amount = 3_000_000_000 };
        yield return new C.GuildStorageItemChange { Type = 0, From = 12, To = 111 };
        yield return new C.FishingCast { CastOut = true };
        yield return new C.FishingChangeAutocast { AutoCast = true };
        yield return new C.AcceptReincarnation();
        yield return new C.CancelReincarnation();
        yield return new C.GameshopBuy { GIndex = 4, Quantity = 3, PType = 1 };
        yield return new C.GuildTerritoryPage { Page = 2 };
        yield return new C.TakeBackHeroItem { From = 3, To = 46 };
        yield return new C.TransferHeroItem { From = 46, To = 3 };
        yield return new C.NewHero { Name = "Sidekick", Gender = MirGender.Female, Class = MirClass.Taoist };
        yield return new C.SetAutoPotValue { Stat = Stat.HP, Value = 40 };
        yield return new C.SetAutoPotItem { Grid = MirGridType.HeroMPItem, ItemIndex = 41 };
        yield return new C.SetHeroBehaviour { Behaviour = HeroBehaviour.CounterAttack };
        yield return new C.ChangeHero { ListIndex = 2 };
        yield return new C.UpdateIntelligentCreature
        {
            Creature = new ClientIntelligentCreature
            {
                PetType = IntelligentCreatureType.Chick, Icon = 502, CustomName = "Peep", Fullness = 9000, SlotIndex = 1,
                Expire = new DateTime(2027, 6, 7, 0, 0, 0, DateTimeKind.Utc), BlackstoneTime = 4321, MaintainFoodTime = 8765,
                petMode = IntelligentCreaturePickupMode.SemiAutomatic,
                CreatureRules = new IntelligentCreatureRules { MinimalFullness = 500, MousePickupEnabled = true, MousePickupRange = 9, SemiAutoPickupEnabled = true, SemiAutoPickupRange = 4 },
                Filter = new IntelligentCreatureItemFilter { PetPickupAll = false, PetPickupWeapons = true, PickupGrade = ItemGrade.Mythical },
            },
            SummonMe = false, UnSummonMe = true, ReleaseMe = false,
        };
        yield return new C.IntelligentCreaturePickup { MouseMode = true, Location = new Point(301, 257) };
        yield return new C.RequestIntelligentCreatureUpdates { Update = true };
        yield return new C.PurchaseGuildTerritory { Owner = "WebGuild" };
        yield return new C.DeleteItem { UniqueID = 8, Count = 2, HeroInventory = true };
        yield return new C.RemoveStoragePassword { CurrentPassword = "old" };
        yield return new C.SellItems { Items = { (8UL, (ushort)2), (9_000_000_003UL, (ushort)1) } };
        yield return new C.RepairItems { Special = true, UniqueIDs = { 8, 9_000_000_003UL } };
        yield return new C.CollectAllParcels();
        yield return new C.ReadMails { MailIDs = { 9_000_000_003UL, 7 } };
        yield return new C.SortItems { Grid = MirGridType.Storage, Mode = 1 };
    }

    public static int Emit(string dir)
    {
        Directory.CreateDirectory(dir);
        var json = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new PointConverter(), new ColorConverter(), new DateTimeConverter(), new UserItemConverter(), new ItemInfoLiteConverter(), new StatsConverter(), new ByteArrayConverter(), new QuestProgressConverter(), new SellEntryConverter() },
        };
        int n = 0;
        foreach (var (packet, direction) in ServerSamples().Select(p => (p, "server")).Concat(ClientSamples().Select(p => (p, "client"))))
        {
            string name = packet.GetType().Name;
            byte[] bytes = packet.GetPacketBytes().ToArray();
            var fields = packet.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .ToDictionary(f => f.Name, f => f.GetValue(packet));
            var doc = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["dir"] = direction,
                ["id"] = packet.Index,
                ["hex"] = Convert.ToHexString(bytes),
                ["fields"] = fields,
            };
            File.WriteAllText(Path.Combine(dir, $"{direction}.{name}.json"), JsonSerializer.Serialize(doc, json).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
            n++;
        }
        Console.WriteLine($"Wrote {n} fixtures to {dir}");
        return 0;
    }

    /// <summary>UserItem as WebClient/src/protocol/server.ts `UserItem` (the ported packet fields, TS names).</summary>
    private sealed class UserItemConverter : JsonConverter<UserItem>
    {
        public override UserItem Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, UserItem v, JsonSerializerOptions o)
        {
            static string Bin(DateTime d) => d.ToBinary().ToString();
            w.WriteStartObject();
            w.WriteString("UniqueID", v.UniqueID.ToString());
            w.WriteNumber("ItemIndex", v.ItemIndex);
            w.WriteNumber("CurrentDura", v.CurrentDura);
            w.WriteNumber("MaxDura", v.MaxDura);
            w.WriteNumber("Count", v.Count);
            w.WriteNumber("SoulBoundId", v.SoulBoundId);
            w.WriteBoolean("Identified", v.Identified);
            w.WriteBoolean("Cursed", v.Cursed);
            w.WritePropertyName("Slots");
            JsonSerializer.Serialize(w, v.Slots, o);
            w.WriteNumber("GemCount", v.GemCount);
            w.WriteStartObject("AddedStats");
            foreach (var pair in v.AddedStats.Values) w.WriteNumber(((byte)pair.Key).ToString(), pair.Value);
            w.WriteEndObject();
            w.WriteNumber("AwakeType", (byte)v.Awake.Type);
            w.WriteStartArray("AwakeLevels");
            for (int i = 0; i < v.Awake.GetAwakeLevel(); i++) w.WriteNumberValue(v.Awake.GetAwakeLevelValue(i));
            w.WriteEndArray();
            w.WriteNumber("RefinedValue", (byte)v.RefinedValue);
            w.WriteNumber("RefineAdded", v.RefineAdded);
            w.WriteNumber("RefineSuccessChance", v.RefineSuccessChance);
            w.WriteNumber("WeddingRing", v.WeddingRing);
            if (v.ExpireInfo == null) w.WriteNull("ExpiryDate"); else w.WriteString("ExpiryDate", Bin(v.ExpireInfo.ExpiryDate));
            if (v.RentalInformation == null) w.WriteNull("Rental");
            else
            {
                w.WriteStartObject("Rental");
                w.WriteString("OwnerName", v.RentalInformation.OwnerName);
                w.WriteNumber("BindingFlags", (short)v.RentalInformation.BindingFlags);
                w.WriteString("ExpiryDate", Bin(v.RentalInformation.ExpiryDate));
                w.WriteBoolean("RentalLocked", v.RentalInformation.RentalLocked);
                w.WriteEndObject();
            }
            w.WriteBoolean("IsShopItem", v.IsShopItem);
            if (v.SealedInfo == null) w.WriteNull("Sealed");
            else
            {
                w.WriteStartObject("Sealed");
                w.WriteString("ExpiryDate", Bin(v.SealedInfo.ExpiryDate));
                w.WriteString("NextSealDate", Bin(v.SealedInfo.NextSealDate));
                w.WriteEndObject();
            }
            w.WriteBoolean("GMMade", v.GMMade);
            w.WriteEndObject();
        }
    }

    /// <summary>ItemInfo as server.ts `ItemInfo` (the packet fields, TS names).</summary>
    private sealed class ItemInfoLiteConverter : JsonConverter<ItemInfo>
    {
        public override ItemInfo Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, ItemInfo v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteNumber("Index", v.Index); w.WriteString("Name", v.Name); w.WriteNumber("Type", (byte)v.Type); w.WriteNumber("Grade", (byte)v.Grade);
            w.WriteNumber("RequiredType", (byte)v.RequiredType); w.WriteNumber("RequiredClass", (byte)v.RequiredClass); w.WriteNumber("RequiredGender", (byte)v.RequiredGender);
            w.WriteNumber("Set", (byte)v.Set); w.WriteNumber("Shape", v.Shape); w.WriteNumber("Weight", v.Weight); w.WriteNumber("Light", v.Light);
            w.WriteNumber("RequiredAmount", v.RequiredAmount); w.WriteNumber("Image", v.Image); w.WriteNumber("Durability", v.Durability);
            w.WriteNumber("StackSize", v.StackSize); w.WriteNumber("Price", v.Price); w.WriteBoolean("StartItem", v.StartItem); w.WriteNumber("Effect", v.Effect);
            w.WriteBoolean("NeedIdentify", v.NeedIdentify); w.WriteBoolean("ShowGroupPickup", v.ShowGroupPickup); w.WriteBoolean("ClassBased", v.ClassBased);
            w.WriteBoolean("LevelBased", v.LevelBased); w.WriteBoolean("CanMine", v.CanMine); w.WriteBoolean("GlobalDropNotify", v.GlobalDropNotify);
            w.WriteNumber("Bind", (short)v.Bind); w.WriteNumber("Unique", (short)v.Unique); w.WriteNumber("RandomStatsId", v.RandomStatsId);
            w.WriteBoolean("CanFastRun", v.CanFastRun); w.WriteBoolean("CanAwakening", v.CanAwakening); w.WriteNumber("Slots", v.Slots);
            w.WritePropertyName("Stats"); JsonSerializer.Serialize(w, v.Stats, o);
            if (v.ToolTip == null) w.WriteNull("ToolTip"); else w.WriteString("ToolTip", v.ToolTip);
            w.WriteEndObject();
        }
    }

    /// <summary>Stats as server.ts reads them: { "statByte": value }.</summary>
    private sealed class StatsConverter : JsonConverter<Stats>
    {
        public override Stats Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, Stats v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            foreach (var pair in v.Values) w.WriteNumber(((byte)pair.Key).ToString(), pair.Value);
            w.WriteEndObject();
        }
    }

    /// <summary>byte[] as a JSON number array (System.Text.Json writes base64 by default; the TS port decodes number[]).</summary>
    private sealed class ByteArrayConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, byte[] v, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            foreach (var b in v) w.WriteNumberValue(b);
            w.WriteEndArray();
        }
    }

    /// <summary>ClientQuestProgress wire fields only (its computed properties need the client's QuestInfo).</summary>
    private sealed class QuestProgressConverter : JsonConverter<ClientQuestProgress>
    {
        public override ClientQuestProgress Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, ClientQuestProgress v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteNumber("Id", v.Id);
            w.WritePropertyName("TaskList"); JsonSerializer.Serialize(w, v.TaskList, o);
            w.WriteBoolean("Taken", v.Taken); w.WriteBoolean("Completed", v.Completed); w.WriteBoolean("New", v.New);
            w.WriteEndObject();
        }
    }

    private sealed class PointConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, Point v, JsonSerializerOptions o)
        {
            w.WriteStartObject(); w.WriteNumber("X", v.X); w.WriteNumber("Y", v.Y); w.WriteEndObject();
        }
    }

    /// <summary>Colours travel as Int32 ARGB (Color.ToArgb) on the wire, so the fixture compares that int.</summary>
    private sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, Color v, JsonSerializerOptions o) => w.WriteNumberValue(v.ToArgb());
    }

    /// <summary>DateTimes travel as Int64 DateTime.ToBinary(); the TS codec keeps that as a bigint, compared as a decimal string.</summary>
    private sealed class DateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, DateTime v, JsonSerializerOptions o) => w.WriteStringValue(v.ToBinary().ToString());
    }

    /// <summary>C.SellItems entry as client.ts `{ UniqueID, Count }` (a ValueTuple would serialise as Item1/Item2).</summary>
    private sealed class SellEntryConverter : JsonConverter<(ulong UniqueID, ushort Count)>
    {
        public override (ulong UniqueID, ushort Count) Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, (ulong UniqueID, ushort Count) v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteString("UniqueID", v.UniqueID.ToString());
            w.WriteNumber("Count", v.Count);
            w.WriteEndObject();
        }
    }
}
