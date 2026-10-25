using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Quests;

namespace Server.Persistence.Tests;

/// <summary>
/// Builds a small but non-trivial world directly into an <see cref="Envir"/>'s lists, so the
/// harness exercises the real Save/ctor pairs without needing live data. Deliberately includes
/// shapes the load path treats specially (a zero-character account, a Concentration buff with
/// Buff.Data, an item in storage, mail with an item, quest progress).
/// </summary>
internal static class SyntheticWorld
{
    public const int ZeroCharacterAccountsBeyondFirst = 0; // kept since roadmap P5 (were dropped by LoadAccounts before)

    /// <param name="fixedNow">Fixed clock for committed fixtures. A far-future date keeps characters from ever
    /// aging into the archive rules (AccountInfo ctor archives on LastLoginDate + Settings months).</param>
    /// <summary>
    /// Quest system V2 definitions (SystemData.db only; the binary Server.MirDB and the oracle carry none of it): the first quest gets
    /// every Definition list, plus conditions, texts, a storyline, a group and a pool with members, and every V2 counter.
    /// </summary>
    private static void AddQuestDefinitions(Envir e)
    {
        var d = e.QuestInfoList[0].Definition;
        d.AuthorKey = "tutorial.first"; d.NameKey = "first.name"; d.Category = QuestCategory.Story; d.AcceptCondition = 1; d.Flags = QuestFlags.HiddenInLog;
        d.StorylineIndex = 1; d.PoolIndex = 1; d.GroupName = "Tutorial";
        d.Steps.Add(new QuestStepInfo { Index = 1, Order = 1, TextKey = "first.task", NextStep = 2 });
        d.Steps.Add(new QuestStepInfo { Index = 2, Order = 2, NextStep = -1, CompletionMode = QuestStepCompletion.NOf, NRequired = 1 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 1, Step = 1, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 1, Count = 5 });
        d.Objectives.Add(new QuestObjectiveInfo { Index = 2, Step = 2, Order = 1, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 3, Count = 3, Params = "" });
        d.Branches.Add(new QuestBranchInfo { Step = 1, ChoiceKey = "first.task", NextStep = 2, Outcome = 1 });
        d.Endpoints.Add(new QuestEndpointInfo { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 1 });
        d.Rewards.Add(new QuestRewardInfo { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 5_000_000_000L });
        d.Rewards.Add(new QuestRewardInfo { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, TargetId = 1, Amount = 1, Condition = 2, ChoiceGroup = 1 });
        d.Vars.Add(new QuestVarInfo { Index = 1, AuthorKey = "count", Default = 2 });

        e.QuestConditionList.Add(new QuestConditionInfo { Index = 1, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Level, Arg1 = 1, Cmp = QuestCompare.Ge, OwnerQuest = 1 });
        e.QuestConditionList.Add(new QuestConditionInfo { Index = 2, Op = QuestConditionOp.Leaf, Leaf = QuestConditionLeaf.Gender, Arg1 = 1, OwnerQuest = 1 });
        e.QuestTextList.Add(new QuestTextInfo { Index = 1, TextKey = "first.name", Body = "First Steps" });
        e.QuestTextList.Add(new QuestTextInfo { Index = 2, TextKey = "first.task", Body = "Kill {5/Red} hens.\nThen come back." });
        e.StorylineList.Add(new StorylineInfo { Index = 1, AuthorKey = "tutorial", NameKey = "first.name", Order = 1 });
        var g = new QuestGroupInfo { Index = 1, AuthorKey = "first.kill.Hen", Kind = QuestGroupKind.Monster };
        g.Members.Add(new QuestGroupMemberInfo { TargetId = 1 });
        g.Members.Add(new QuestGroupMemberInfo { TargetId = 2 });
        e.QuestGroupList.Add(g);
        var p = new QuestPoolInfo { Index = 1, AuthorKey = "daily", Picks = 1, RotatePolicy = QuestRotatePolicy.Weekly };
        p.Members.Add(new QuestPoolMemberInfo { Quest = 1, Weight = 3 });
        e.QuestPoolList.Add(p);
        e.QuestStepIndex = 2; e.QuestObjectiveIndex = 2; e.QuestConditionIndex = 2; e.QuestGroupIndex = 1; e.QuestTextIndex = 2; e.QuestVarIndex = 1;
        e.StorylineIndex = 1; e.QuestPoolIndex = 1;
    }

    public static void Populate(Envir e, DateTime? fixedNow = null)
    {
        var now = fixedNow ?? DateTime.UtcNow;

        // ---- system data ----
        e.MapIndex = 2; e.ItemIndex = 4; e.MonsterIndex = 2; e.NPCIndex = 1; e.QuestIndex = 1; e.GameshopIndex = 1; e.RespawnIndex = 1;

        var map1 = new MapInfo { Index = 1, FileName = "0", Title = "Bichon Province", MiniMap = 101, BigMap = 102, Light = LightSetting.Day };
        map1.SafeZones.Add(new SafeZoneInfo { Info = map1, Location = new Point(300, 300), Size = 10, StartPoint = true });
        map1.Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = new Point(320, 320), Count = 5, Spread = 10, Delay = 60, RespawnIndex = 1 });
        var map2 = new MapInfo { Index = 2, FileName = "1", Title = "Dungeon", NoTeleport = true, NoReconnect = true, NoReconnectMap = "0" };
        map1.Movements.Add(new MovementInfo { MapIndex = 2, Source = new Point(310, 310), Destination = new Point(10, 10) });
        e.MapInfoList.Add(map1);
        e.MapInfoList.Add(map2);

        var sword = new ItemInfo { Index = 1, Name = "WoodenSword", Type = ItemType.Weapon, Shape = 1, Durability = 5000, Price = 100, StackSize = 1 };
        sword.Stats[Stat.MinDC] = 2; sword.Stats[Stat.MaxDC] = 5;
        var armour = new ItemInfo { Index = 2, Name = "BaseDress(M)", Type = ItemType.Armour, Durability = 5000, Price = 120, StackSize = 1 };
        armour.Stats[Stat.MinAC] = 1; armour.Stats[Stat.MaxAC] = 3;
        var potion = new ItemInfo { Index = 3, Name = "(HP)DrugSmall", Type = ItemType.Potion, Price = 10, StackSize = 50 };
        potion.Stats[Stat.HP] = 30;
        var gold = new ItemInfo { Index = 4, Name = "Gold", Type = ItemType.Nothing, StackSize = 1 };
        e.ItemInfoList.AddRange(new[] { sword, armour, potion, gold });

        e.MonsterInfoList.Add(new MonsterInfo { Index = 1, Name = "Hen", Level = 1, Experience = 5, IsBoss = false });
        e.MonsterInfoList.Add(new MonsterInfo { Index = 2, Name = "BossHen", Level = 20, Experience = 500, IsBoss = true });
        e.NPCInfoList.Add(new NPCInfo { Index = 1, MapIndex = 1, FileName = "Default", Name = "Guide", Location = new Point(301, 301) });
        e.QuestInfoList.Add(new QuestInfo { Index = 1, Name = "First Steps", Group = "Tutorial" });
        AddQuestDefinitions(e);
        e.GameShopList.Add(new GameShopItem { GIndex = 1, ItemIndex = 3, GoldPrice = 500, CreditPrice = 5, Count = 10, Class = "All", Category = "Potions", Date = now, CanBuyGold = true, DealGoldPrice = 750, DealCreditPrice = 8 }); // v119 deal prices

        // ---- user data ----
        e.NextAccountID = 4; e.NextCharacterID = 3; e.NextUserItemID = 100; e.NextHeroID = 1; e.NextMailID = 2; e.NextAuctionID = 1;

        e.AccountList.Add(NewAccount(1, "admin0", now)); // index 0: kept even with zero characters

        var acc = NewAccount(2, "player1", now);
        acc.Gold = 1234; acc.Credit = 5;
        acc.Storage[0] = Item(e, sword, 10);
        var c1 = NewCharacter(1, "Warrior1", MirClass.Warrior, acc, now);
        c1.Inventory[0] = Item(e, potion, 11, count: 20);
        c1.Equipment[(int)EquipmentSlot.Weapon] = Item(e, sword, 12);
        c1.Equipment[(int)EquipmentSlot.Armour] = Item(e, armour, 13);
        // v121 quest V2 state, statistics (incl. an id this build does not know) and the rankings opt-out, all non-default.
        // The legacy CompletedQuests / CurrentQuests are no longer saved (v120 and older fixtures carry one completed quest).
        c1.QuestRuns.Add(new QuestRunState { Quest = 1, Status = QuestRunStatus.Active, Step = 2, Revision = 3, AcceptedAt = now.AddHours(-2).ToBinary(), ExpiresAt = now.AddDays(1).ToBinary(), StepExpiresAt = now.AddHours(1).ToBinary(), SharedFrom = 2, Branch = 1 });
        c1.QuestRuns.Add(new QuestRunState { Quest = 7, Status = QuestRunStatus.Failed, Step = -1, Revision = 1, AcceptedAt = now.AddDays(-1).ToBinary() });
        c1.QuestObjectives.Add(new QuestObjectiveState { Quest = 1, Objective = 1, Value = 4 });
        c1.QuestObjectives.Add(new QuestObjectiveState { Quest = 1, Objective = 2, Value = unchecked((int)0x80000005) }); // multi-spot bitmask
        c1.QuestHistory.Add(new QuestHistoryEntry { Quest = 1, Times = 3, FirstAt = now.AddDays(-9).ToBinary(), LastAt = now.AddDays(-2).ToBinary(), LastOutcome = 2 });
        c1.QuestVars.Add(new QuestVarState { Quest = 1, Key = 1, Value = -7 });
        c1.QuestRewardsPaid.Add(new QuestRewardPaid { Quest = 1, Scope = QuestRewardScope.Step, ScopeId = 2 });
        c1.Statistics.Add(PlayerStat.MonstersKilled, 1234);
        c1.Statistics.Add(PlayerStat.GoldEarned, 5_000_000_000L);
        c1.Statistics.Max(PlayerStat.HighestHit, 987);
        c1.Statistics.Add(PlayerStat.HighestPkPoints, 0); // a zero Add stores nothing
        c1.Statistics.Set((PlayerStat)60000, 42);         // unknown to this build: kept and written back
        c1.HideFromRankings = true;
        c1.Flags[3] = true;
        // v119 character fields, all non-default
        c1.PlayTime = 5 * 3600 + 7;
        c1.ProfileText = "Hunting hens since 2099";
        c1.EnableGroupRecall = true; c1.AllowMarriage = true; c1.AllowMentor = true;
        c1.AllowFriendRequests = false;
        // v120 chat ban, non-default
        c1.ChatBanned = true;
        c1.ChatBanExpiryDate = now.AddDays(3);
        c1.Magics.Add(new UserMagic(Spell.Fencing) { Level = 1, Key = 2, AutoCast = true });
        var mail = new MailInfo(c1.Index) { MailID = 1, Sender = "Archer2", Message = "Welcome", Gold = 10, DateSent = now, SenderIndex = 2, SenderHidden = true };
        mail.Items.Add(Item(e, potion, 14, count: 5));
        c1.Mail.Add(mail);
        acc.Characters.Add(c1);

        var c2 = NewCharacter(2, "Archer2", MirClass.Archer, acc, now);
        c2.Magics.Add(new UserMagic(Spell.Concentration) { Level = 2, Key = 1, Experience = 10, AutoCast = false }); // v119
        c2.PlayTime = 42;
        c2.EnableGuildInvite = true; c2.AllowLoverRecall = true;
        // Concentration is the only Buff.Data writer (HumanObject.cs). Its Data entries are
        // BinaryFormatter-encoded in the v117 format: the case roadmap P3 must migrate.
        var conc = NewBuff(e, BuffType.Concentration, 1, long.MaxValue);
        conc.Set("Interrupted", true);
        conc.Set("InterruptTime", 12345L);
        c2.Buffs.Add(conc);
        c2.Buffs.Add(NewBuff(e, BuffType.Exp, 0, 60000));
        acc.Characters.Add(c2);
        e.AccountList.Add(acc);

        e.AccountList.Add(NewAccount(3, "empty2", now)); // zero characters, not index 0: kept since roadmap P5 (was dropped on load)

        e.CharacterList.AddRange(acc.Characters);

        // v119 market sales log (Server.MirADB, after the respawn block): one item past the ring capacity, one short
        var potionSales = new MarketSaleHistory();
        for (int i = 0; i < MarketSaleHistory.Capacity + 3; i++) potionSales.Add((uint)(100 + i), now.AddHours(-i));
        e.MarketSales[potion.Index] = potionSales;
        var swordSales = new MarketSaleHistory();
        swordSales.Add(2500, now.AddDays(-2));
        e.MarketSales[sword.Index] = swordSales;
    }

    internal static AccountInfo NewAccount(int index, string id, DateTime now) => new AccountInfo
    {
        Index = index,
        AccountID = id,
        UserName = id + " name",
        BirthDate = now.AddYears(-20),
        SecretQuestion = "q",
        SecretAnswer = "a",
        EMailAddress = id + "@example.test",
        CreationIP = "127.0.0.1",
        CreationDate = now.AddDays(-1),
        ExpiryDate = now,
        LastIP = "127.0.0.1",
        LastDate = now,
    };

    internal static CharacterInfo NewCharacter(int index, string name, MirClass cls, AccountInfo acc, DateTime now) => new CharacterInfo
    {
        Index = index,
        Name = name,
        Level = 10,
        Class = cls,
        Gender = MirGender.Male,
        CreationIP = "127.0.0.1",
        CreationDate = now.AddDays(-1),
        LastLoginDate = now,
        LastLogoutDate = now,
        CurrentMapIndex = 1,
        CurrentLocation = new Point(300, 300),
        BindMapIndex = 1,
        BindLocation = new Point(300, 300),
        HP = 50,
        MP = 20,
        Experience = 1000,
        AccountInfo = acc,
        Heroes = new HeroInfo[1],
    };

    internal static UserItem Item(Envir e, ItemInfo info, ulong uid, ushort count = 1) =>
        new UserItem(info) { UniqueID = uid, ItemIndex = info.Index, Count = count, CurrentDura = 1000, MaxDura = 5000 };

    private static Buff NewBuff(Envir e, BuffType type, uint objectId, long expire)
    {
        var b = new Buff(type) { ObjectID = objectId, ExpireTime = expire, Values = new[] { 1, 2 } };
        return b;
    }
}
