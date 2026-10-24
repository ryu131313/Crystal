using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin
{
    // DTOs for the Players Online / Characters feature (B2). Never serialize Crystal objects directly (cycles): project into these.

    /// <summary>GET /api/admin/players-online row.</summary>
    public sealed class OnlinePlayerRow
    {
        public int CharacterIndex { get; set; }
        public string Name { get; set; }
        public int AccountIndex { get; set; }
        public string AccountId { get; set; }
        public int Level { get; set; }
        public string ClassName { get; set; }
        public string Gender { get; set; }
        public int MapIndex { get; set; }
        public string MapTitle { get; set; }
        public string MapFile { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string Ip { get; set; }
        public DateTime? OnlineSince { get; set; }
        public string Guild { get; set; }
        public bool IsGM { get; set; }
        public int Pk { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Mp { get; set; }
        public int MaxMp { get; set; }
        public bool Dead { get; set; }
        public bool ChatBanned { get; set; }
    }

    /// <summary>GET /api/admin/characters row.</summary>
    public sealed class CharacterListRow
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int AccountIndex { get; set; }
        public string AccountId { get; set; }
        public int Level { get; set; }
        public string ClassName { get; set; }
        public string Gender { get; set; }
        public int Pk { get; set; }
        public string Guild { get; set; }
        public bool Online { get; set; }
        public bool Deleted { get; set; }
        public DateTime? LastLogin { get; set; }
        public DateTime? LastLogout { get; set; }
        public string MapTitle { get; set; }
        public bool Banned { get; set; }
    }

    public sealed class CharacterListResult
    {
        public List<CharacterListRow> Rows { get; set; } = new List<CharacterListRow>();
        public int Total { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int PageSize { get; set; }
        public string Sort { get; set; }
        public string Dir { get; set; }
        public string World { get; set; }
    }

    public sealed class CharacterLocationDto
    {
        public int MapIndex { get; set; }
        public string MapTitle { get; set; }
        public string MapFile { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class CharacterItemDto
    {
        /// <summary>UserItem.UniqueID as a string (ulong; avoids JS precision loss).</summary>
        public string UniqueId { get; set; }
        public int ItemIndex { get; set; }
        public string Name { get; set; }
        public string ItemType { get; set; }
        public int Count { get; set; }
        public int StackSize { get; set; }
        public int CurrentDura { get; set; }
        public int MaxDura { get; set; }
        public Dictionary<string, int> AddedStats { get; set; }
        public string Container { get; set; }
        public int Slot { get; set; }
        public string SlotName { get; set; }
        public bool Identified { get; set; }
        public bool GmMade { get; set; }
        public int SoulBoundId { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }

    public sealed class CharacterItemsDto
    {
        public List<CharacterItemDto> Equipment { get; set; } = new List<CharacterItemDto>();
        public List<CharacterItemDto> Inventory { get; set; } = new List<CharacterItemDto>();
        public List<CharacterItemDto> QuestInventory { get; set; } = new List<CharacterItemDto>();
        public List<CharacterItemDto> Storage { get; set; } = new List<CharacterItemDto>();
        public int InventorySize { get; set; }
        public int QuestInventorySize { get; set; }
        public int StorageSize { get; set; }
        public int UsableStorageSize { get; set; }
    }

    public sealed class CharacterMagicDto
    {
        public int Spell { get; set; }
        public string SpellName { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public int Experience { get; set; }
        public int? Need { get; set; }
        public int Key { get; set; }
        public string KeyLabel { get; set; }
        public bool IsTempSpell { get; set; }
    }

    public sealed class CharacterQuestDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Group { get; set; }
        public string Status { get; set; }
        public DateTime? Started { get; set; }
        // Quest V2 (lane F): the run's current step (-1 = turn-in pending), its time limits, completion count / last time, and the
        // stored objective values of the run.
        public int? Step { get; set; }
        public DateTime? Expires { get; set; }
        public DateTime? StepExpires { get; set; }
        public int Times { get; set; }
        public DateTime? LastCompleted { get; set; }
        public List<CharacterQuestObjectiveDto> Objectives { get; set; } = new List<CharacterQuestObjectiveDto>();
    }

    public sealed class CharacterQuestObjectiveDto
    {
        public int Objective { get; set; }
        public int Value { get; set; }
    }

    public sealed class CharacterPetDto
    {
        public int MonsterIndex { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public int MaxPetLevel { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public long Experience { get; set; }
        public bool Live { get; set; }
        public string MapTitle { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class CharacterHeroDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public string ClassName { get; set; }
        public string Gender { get; set; }
        public bool Deleted { get; set; }
        public bool Spawned { get; set; }
        public int Magics { get; set; }
        public int Items { get; set; }
    }

    public sealed class CharacterMailDto
    {
        public string MailId { get; set; }
        public string Sender { get; set; }
        public string Preview { get; set; }
        public DateTime? DateSent { get; set; }
        public bool Opened { get; set; }
        public bool Locked { get; set; }
        public bool Collected { get; set; }
        public uint Gold { get; set; }
        public int ItemCount { get; set; }
    }

    public sealed class CharacterMailSummaryDto
    {
        public int Count { get; set; }
        public int Unread { get; set; }
        public List<CharacterMailDto> Recent { get; set; } = new List<CharacterMailDto>();
    }

    public sealed class CharacterFlagDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
    }

    public sealed class CharacterLiveDto
    {
        public string Ip { get; set; }
        public bool IsGM { get; set; }
        public bool Dead { get; set; }
        public CharacterLocationDto Location { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Mp { get; set; }
        public int MaxMp { get; set; }
        public long MaxExperience { get; set; }
        public string Ac { get; set; }
        public string Mac { get; set; }
        public string Dc { get; set; }
        public string Mc { get; set; }
        public string Sc { get; set; }
        public int Accuracy { get; set; }
        public int Agility { get; set; }
        public int AttackSpeed { get; set; }
        public int Luck { get; set; }
        public Dictionary<string, int> Stats { get; set; }
    }

    public sealed class CharacterAccountDto
    {
        public int Index { get; set; }
        public string AccountId { get; set; }
        public uint Gold { get; set; }
        public uint Credit { get; set; }
        public bool Banned { get; set; }
        public string BanReason { get; set; }
        public DateTime? BanExpiry { get; set; }
        public bool Admin { get; set; }
        public bool ExpandedStorage { get; set; }
        public string LastIp { get; set; }
        public int CharacterCount { get; set; }
    }

    public sealed class CharacterInfoDto
    {
        public int Level { get; set; }
        public string ClassName { get; set; }
        public int ClassValue { get; set; }
        public string Gender { get; set; }
        public int GenderValue { get; set; }
        public int Hair { get; set; }
        public int GuildIndex { get; set; }
        public string CreationIp { get; set; }
        public DateTime? CreationDate { get; set; }
        public string LastIp { get; set; }
        public DateTime? LastLogin { get; set; }
        public DateTime? LastLogout { get; set; }
        public bool Banned { get; set; }
        public string BanReason { get; set; }
        public DateTime? BanExpiry { get; set; }
        public bool ChatBanned { get; set; }
        public DateTime? ChatBanExpiry { get; set; }
        public bool Deleted { get; set; }
        public DateTime? DeleteDate { get; set; }
        public int MarriedIndex { get; set; }
        public string MarriedTo { get; set; }
        public DateTime? MarriedDate { get; set; }
        public int MentorIndex { get; set; }
        public string Mentor { get; set; }
        public DateTime? MentorDate { get; set; }
        public bool IsMentor { get; set; }
        public long MentorExp { get; set; }
        public CharacterLocationDto Location { get; set; }
        public CharacterLocationDto Bind { get; set; }
        public int Hp { get; set; }
        public int Mp { get; set; }
        public long Experience { get; set; }
        public int PkPoints { get; set; }
        public int Pearls { get; set; }
        public string AttackMode { get; set; }
        public string PetMode { get; set; }
        public bool AllowGroup { get; set; }
        public bool AllowTrade { get; set; }
        public bool AllowObserve { get; set; }
        public int FriendCount { get; set; }
        public int BuffCount { get; set; }
    }

    public sealed class CharacterGuildDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Rank { get; set; }
    }

    public sealed class CharacterSpellOption
    {
        public int Value { get; set; }
        public string Name { get; set; }
    }

    /// <summary>GET /api/admin/characters/{index}.</summary>
    public sealed class CharacterDetailDto
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public bool Online { get; set; }
        public string World { get; set; }
        public CharacterInfoDto Info { get; set; }
        public CharacterAccountDto Account { get; set; }
        public CharacterLiveDto Live { get; set; }
        public CharacterGuildDto Guild { get; set; }
        public List<CharacterFlagDto> Flags { get; set; } = new List<CharacterFlagDto>();
        public int FlagCount { get; set; }
        public Dictionary<string, string> FlagNames { get; set; } = new Dictionary<string, string>();
        public List<CharacterQuestDto> CompletedQuests { get; set; } = new List<CharacterQuestDto>();
        public List<CharacterQuestDto> CurrentQuests { get; set; } = new List<CharacterQuestDto>();
        public List<CharacterMagicDto> Magics { get; set; } = new List<CharacterMagicDto>();
        public List<CharacterSpellOption> SpellOptions { get; set; } = new List<CharacterSpellOption>();
        public List<CharacterPetDto> Pets { get; set; } = new List<CharacterPetDto>();
        public List<CharacterHeroDto> Heroes { get; set; } = new List<CharacterHeroDto>();
        public int MaximumHeroCount { get; set; }
        public CharacterMailSummaryDto Mail { get; set; }
        public CharacterItemsDto Items { get; set; }
        public DateTime ServerTimeUtc { get; set; }
    }

    /// <summary>Projections from live Crystal objects into DTOs. Call on the game thread (or inline while the world is stopped).</summary>
    internal static class CharacterProjection
    {
        /// <summary>A saved DateTime.ToBinary() value (quest V2 records), null for 0.</summary>
        public static DateTime? FromBinary(long value) => value == 0 ? null : DateTime.FromBinary(value);

        public static string MapTitle(Envir envir, int mapIndex)
        {
            var list = envir.MapInfoList;
            for (var i = 0; i < list.Count; i++)
                if (list[i].Index == mapIndex) return list[i].Title;
            return null;
        }

        public static MapInfo FindMapInfo(Envir envir, int mapIndex)
        {
            var list = envir.MapInfoList;
            for (var i = 0; i < list.Count; i++)
                if (list[i].Index == mapIndex) return list[i];
            return null;
        }

        public static GuildInfo FindGuild(Envir envir, int guildIndex)
        {
            if (guildIndex < 0) return null;
            var list = envir.GuildList;
            for (var i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].GuildIndex == guildIndex) return list[i];
            return null;
        }

        public static Dictionary<int, string> MapTitles(Envir envir)
        {
            var d = new Dictionary<int, string>();
            foreach (var m in envir.MapInfoList)
                if (m != null) d[m.Index] = m.Title;
            return d;
        }

        public static Dictionary<int, string> GuildNames(Envir envir)
        {
            var d = new Dictionary<int, string>();
            foreach (var g in envir.GuildList)
                if (g != null) d[g.GuildIndex] = g.Name;
            return d;
        }

        public static CharacterLocationDto Location(Envir envir, int mapIndex, System.Drawing.Point point)
        {
            var info = FindMapInfo(envir, mapIndex);
            return new CharacterLocationDto
            {
                MapIndex = mapIndex,
                MapTitle = info?.Title,
                MapFile = info?.FileName,
                X = point.X,
                Y = point.Y,
            };
        }

        public static OnlinePlayerRow OnlineRow(PlayerObject p)
        {
            var ci = p.Info;
            var acc = p.Account ?? ci.AccountInfo;
            var map = p.CurrentMap?.Info;

            return new OnlinePlayerRow
            {
                CharacterIndex = ci.Index,
                Name = ci.Name,
                AccountIndex = acc?.Index ?? 0,
                AccountId = acc?.AccountID,
                Level = ci.Level,
                ClassName = ci.Class.ToString(),
                Gender = ci.Gender.ToString(),
                MapIndex = map?.Index ?? ci.CurrentMapIndex,
                MapTitle = map?.Title,
                MapFile = map?.FileName,
                X = ci.CurrentLocation.X,
                Y = ci.CurrentLocation.Y,
                Ip = p.Connection?.IPAddress,
                OnlineSince = CharacterLogic.NullIfMin(ci.LastLoginDate),
                Guild = p.MyGuild?.Name,
                IsGM = p.IsGM,
                Pk = ci.PKPoints,
                Hp = ci.HP,
                MaxHp = p.Stats != null ? p.Stats[Stat.HP] : 0,
                Mp = ci.MP,
                MaxMp = p.Stats != null ? p.Stats[Stat.MP] : 0,
                Dead = p.Dead,
                ChatBanned = ci.ChatBanned && ci.ChatBanExpiryDate > Envir.Main.Now,
            };
        }

        public static CharacterListRow ListRow(CharacterInfo ci, Dictionary<int, string> mapTitles, Dictionary<int, string> guildNames)
        {
            var acc = ci.AccountInfo;
            mapTitles.TryGetValue(ci.CurrentMapIndex, out var title);
            string guild = null;
            if (ci.GuildIndex >= 0) guildNames.TryGetValue(ci.GuildIndex, out guild);

            return new CharacterListRow
            {
                Index = ci.Index,
                Name = ci.Name,
                AccountIndex = acc?.Index ?? 0,
                AccountId = acc?.AccountID,
                Level = ci.Level,
                ClassName = ci.Class.ToString(),
                Gender = ci.Gender.ToString(),
                Pk = ci.PKPoints,
                Guild = guild,
                Online = ci.Player != null,
                Deleted = ci.Deleted,
                LastLogin = CharacterLogic.NullIfMin(ci.LastLoginDate),
                LastLogout = CharacterLogic.NullIfMin(ci.LastLogoutDate),
                MapTitle = title,
                Banned = ci.Banned,
            };
        }

        public static CharacterItemDto Item(UserItem item, string container, int slot)
        {
            var info = item.Info;
            return new CharacterItemDto
            {
                UniqueId = item.UniqueID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ItemIndex = item.ItemIndex,
                Name = info != null ? info.FriendlyName : "Item #" + item.ItemIndex,
                ItemType = info?.Type.ToString(),
                Count = item.Count,
                StackSize = info?.StackSize ?? 1,
                CurrentDura = item.CurrentDura,
                MaxDura = item.MaxDura,
                AddedStats = CharacterLogic.StatsToDictionary(item.AddedStats),
                Container = container,
                Slot = slot,
                SlotName = CharacterLogic.SlotName(container, slot),
                Identified = item.Identified,
                GmMade = item.GMMade,
                SoulBoundId = item.SoulBoundId,
                ExpiresUtc = item.ExpireInfo != null ? CharacterLogic.NullIfMin(item.ExpireInfo.ExpiryDate) : null,
            };
        }

        public static List<CharacterItemDto> Items(UserItem[] array, string container)
        {
            var list = new List<CharacterItemDto>();
            if (array == null) return list;

            for (var i = 0; i < array.Length; i++)
            {
                var it = array[i];
                if (it == null) continue;
                list.Add(Item(it, container, i));
            }

            return list;
        }

        public static string ItemLabel(UserItem item)
        {
            if (item == null) return "?";
            var name = item.Info != null ? item.Info.FriendlyName : "Item #" + item.ItemIndex;
            return name + (item.Count > 1 ? " x" + item.Count : string.Empty) + " (uid " + item.UniqueID + ")";
        }

        public static string StatRange(Stats stats, Stat min, Stat max)
        {
            return stats[min] + "-" + stats[max];
        }

        public static CharacterDetailDto Detail(Envir envir, CharacterInfo ci, IReadOnlyDictionary<int, string> flagNames)
        {
            var acc = ci.AccountInfo;
            var player = ci.Player;
            var now = envir.Now;

            var married = ci.Married != 0 ? envir.GetCharacterInfo(ci.Married) : null;
            var mentor = ci.Mentor != 0 ? envir.GetCharacterInfo(ci.Mentor) : null;

            var dto = new CharacterDetailDto
            {
                Index = ci.Index,
                Name = ci.Name,
                Online = player != null,
                World = WebAdminGame.WorldState,
                ServerTimeUtc = DateTime.UtcNow,
                MaximumHeroCount = ci.MaximumHeroCount,
                FlagCount = ci.Flags?.Length ?? 0,
                Info = new CharacterInfoDto
                {
                    Level = ci.Level,
                    ClassName = ci.Class.ToString(),
                    ClassValue = (int)ci.Class,
                    Gender = ci.Gender.ToString(),
                    GenderValue = (int)ci.Gender,
                    Hair = ci.Hair,
                    GuildIndex = ci.GuildIndex,
                    CreationIp = ci.CreationIP,
                    CreationDate = CharacterLogic.NullIfMin(ci.CreationDate),
                    LastIp = ci.LastIP,
                    LastLogin = CharacterLogic.NullIfMin(ci.LastLoginDate),
                    LastLogout = CharacterLogic.NullIfMin(ci.LastLogoutDate),
                    Banned = ci.Banned,
                    BanReason = ci.BanReason,
                    BanExpiry = CharacterLogic.NullIfMin(ci.ExpiryDate),
                    ChatBanned = ci.ChatBanned && ci.ChatBanExpiryDate > now,
                    ChatBanExpiry = CharacterLogic.NullIfMin(ci.ChatBanExpiryDate),
                    Deleted = ci.Deleted,
                    DeleteDate = CharacterLogic.NullIfMin(ci.DeleteDate),
                    MarriedIndex = ci.Married,
                    MarriedTo = married?.Name,
                    MarriedDate = ci.Married != 0 ? CharacterLogic.NullIfMin(ci.MarriedDate) : null,
                    MentorIndex = ci.Mentor,
                    Mentor = mentor?.Name,
                    MentorDate = ci.Mentor != 0 ? CharacterLogic.NullIfMin(ci.MentorDate) : null,
                    IsMentor = ci.IsMentor,
                    MentorExp = ci.MentorExp,
                    Location = Location(envir, ci.CurrentMapIndex, ci.CurrentLocation),
                    Bind = Location(envir, ci.BindMapIndex, ci.BindLocation),
                    Hp = ci.HP,
                    Mp = ci.MP,
                    Experience = ci.Experience,
                    PkPoints = ci.PKPoints,
                    Pearls = ci.PearlCount,
                    AttackMode = ci.AMode.ToString(),
                    PetMode = ci.PMode.ToString(),
                    AllowGroup = ci.AllowGroup,
                    AllowTrade = ci.AllowTrade,
                    AllowObserve = ci.AllowObserve,
                    FriendCount = ci.Friends?.Count ?? 0,
                    BuffCount = ci.Buffs?.Count ?? 0,
                },
            };

            if (acc != null)
            {
                dto.Account = new CharacterAccountDto
                {
                    Index = acc.Index,
                    AccountId = acc.AccountID,
                    Gold = acc.Gold,
                    Credit = acc.Credit,
                    Banned = acc.Banned && acc.ExpiryDate > now,
                    BanReason = acc.BanReason,
                    BanExpiry = acc.Banned ? CharacterLogic.NullIfMin(acc.ExpiryDate) : null,
                    Admin = acc.AdminAccount,
                    ExpandedStorage = acc.HasExpandedStorage,
                    LastIp = acc.LastIP,
                    CharacterCount = acc.Characters?.Count ?? 0,
                };
            }

            if (player != null)
            {
                var stats = player.Stats ?? new Stats();
                var map = player.CurrentMap?.Info;
                dto.Live = new CharacterLiveDto
                {
                    Ip = player.Connection?.IPAddress,
                    IsGM = player.IsGM,
                    Dead = player.Dead,
                    Location = new CharacterLocationDto
                    {
                        MapIndex = map?.Index ?? ci.CurrentMapIndex,
                        MapTitle = map?.Title,
                        MapFile = map?.FileName,
                        X = ci.CurrentLocation.X,
                        Y = ci.CurrentLocation.Y,
                    },
                    Hp = ci.HP,
                    MaxHp = stats[Stat.HP],
                    Mp = ci.MP,
                    MaxMp = stats[Stat.MP],
                    MaxExperience = player.MaxExperience,
                    Ac = StatRange(stats, Stat.MinAC, Stat.MaxAC),
                    Mac = StatRange(stats, Stat.MinMAC, Stat.MaxMAC),
                    Dc = StatRange(stats, Stat.MinDC, Stat.MaxDC),
                    Mc = StatRange(stats, Stat.MinMC, Stat.MaxMC),
                    Sc = StatRange(stats, Stat.MinSC, Stat.MaxSC),
                    Accuracy = stats[Stat.Accuracy],
                    Agility = stats[Stat.Agility],
                    AttackSpeed = stats[Stat.AttackSpeed],
                    Luck = stats[Stat.Luck],
                    Stats = CharacterLogic.StatsToDictionary(stats),
                };
            }

            var guild = FindGuild(envir, ci.GuildIndex);
            if (guild != null)
            {
                string rank = null;
                foreach (var r in guild.Ranks)
                {
                    if (r?.Members == null) continue;
                    if (r.Members.Any(m => m != null && m.Id == ci.Index))
                    {
                        rank = r.Name;
                        break;
                    }
                }

                dto.Guild = new CharacterGuildDto { Index = guild.GuildIndex, Name = guild.Name, Rank = rank };
            }

            // Flags
            foreach (var f in CharacterLogic.ActiveFlags(ci.Flags))
            {
                string name = null;
                flagNames?.TryGetValue(f, out name);
                dto.Flags.Add(new CharacterFlagDto { Index = f, Name = name });
            }

            if (flagNames != null)
                foreach (var pair in flagNames)
                    dto.FlagNames[pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = pair.Value;

            // Quests (V2 records: completion history and runs; the status of an online run comes from its live PlayerQuests,
            // since Ready is derived)
            foreach (var h in ci.QuestHistory)
            {
                if (h == null) continue;
                var qi = envir.GetQuestInfo(h.Quest);
                dto.CompletedQuests.Add(new CharacterQuestDto
                {
                    Index = h.Quest, Name = qi?.Name, Group = qi?.Group, Status = "Completed", Times = h.Times,
                    Started = FromBinary(h.FirstAt), LastCompleted = FromBinary(h.LastAt),
                });
            }

            foreach (var run in ci.QuestRuns)
            {
                if (run == null) continue;
                var qi = envir.GetQuestInfo(run.Quest);
                var status = run.Status == QuestRunStatus.Failed ? QuestRunStatus.Failed
                    : ci.Player != null && ci.Player.Info == ci ? ci.Player.Quests.StatusOf(run) : QuestRunStatus.Active;
                var quest = new CharacterQuestDto
                {
                    Index = run.Quest,
                    Name = qi?.Name,
                    Group = qi?.Group,
                    Status = status == QuestRunStatus.Ready ? "Ready to turn in" : status == QuestRunStatus.Failed ? "Failed" : "In progress",
                    Started = FromBinary(run.AcceptedAt),
                    Step = run.Step,
                    Expires = FromBinary(run.ExpiresAt),
                    StepExpires = FromBinary(run.StepExpiresAt),
                };
                foreach (var o in ci.QuestObjectives)
                    if (o != null && o.Quest == run.Quest) quest.Objectives.Add(new CharacterQuestObjectiveDto { Objective = o.Objective, Value = o.Value });
                dto.CurrentQuests.Add(quest);
            }
            // Magics
            foreach (var m in ci.Magics)
            {
                if (m == null) continue;
                var info = m.Info;
                dto.Magics.Add(new CharacterMagicDto
                {
                    Spell = (int)m.Spell,
                    SpellName = m.Spell.ToString(),
                    Name = info?.Name ?? m.Spell.ToString(),
                    Level = m.Level,
                    Experience = m.Experience,
                    Need = info != null ? CharacterLogic.MagicNeed(m.Level, info.Need1, info.Need2, info.Need3) : null,
                    Key = m.Key,
                    KeyLabel = CharacterLogic.MagicKeyLabel(m.Key),
                    IsTempSpell = m.IsTempSpell,
                });
            }

            foreach (var mi in envir.MagicInfoList)
            {
                if (mi == null || mi.Spell == Spell.None) continue;
                dto.SpellOptions.Add(new CharacterSpellOption { Value = (int)mi.Spell, Name = mi.Name + " (" + mi.Spell + ")" });
            }

            dto.SpellOptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Pets (saved pets while offline; live pets while online)
            if (player != null && player.Pets != null && player.Pets.Count > 0)
            {
                foreach (var pet in player.Pets)
                {
                    if (pet == null) continue;
                    var map = pet.CurrentMap?.Info;
                    dto.Pets.Add(new CharacterPetDto
                    {
                        MonsterIndex = pet.Info?.Index ?? 0,
                        Name = pet.Name,
                        Level = pet.PetLevel,
                        MaxPetLevel = pet.MaxPetLevel,
                        Hp = pet.Health,
                        MaxHp = pet.MaxHealth,
                        Experience = pet.PetExperience,
                        Live = true,
                        MapTitle = map?.Title,
                        X = pet.CurrentLocation.X,
                        Y = pet.CurrentLocation.Y,
                    });
                }
            }
            else
            {
                foreach (var pet in ci.Pets)
                {
                    if (pet == null) continue;
                    var mi = envir.MonsterInfoList.FirstOrDefault(x => x.Index == pet.MonsterIndex);
                    dto.Pets.Add(new CharacterPetDto
                    {
                        MonsterIndex = pet.MonsterIndex,
                        Name = mi?.Name ?? ("Monster #" + pet.MonsterIndex),
                        Level = pet.Level,
                        MaxPetLevel = pet.MaxPetLevel,
                        Hp = pet.HP,
                        Experience = pet.Experience,
                        Live = false,
                    });
                }
            }

            // Heroes
            if (ci.Heroes != null)
            {
                for (var i = 0; i < ci.Heroes.Length; i++)
                {
                    var h = ci.Heroes[i];
                    if (h == null) continue;
                    dto.Heroes.Add(new CharacterHeroDto
                    {
                        Index = h.Index,
                        Name = h.Name,
                        Level = h.Level,
                        ClassName = h.Class.ToString(),
                        Gender = h.Gender.ToString(),
                        Deleted = h.Deleted,
                        Spawned = ci.HeroSpawned && ci.CurrentHeroIndex == i,
                        Magics = h.Magics?.Count ?? 0,
                        Items = (h.Inventory?.Count(x => x != null) ?? 0) + (h.Equipment?.Count(x => x != null) ?? 0),
                    });
                }
            }

            // Mail
            var mail = new CharacterMailSummaryDto();
            if (ci.Mail != null)
            {
                mail.Count = ci.Mail.Count;
                mail.Unread = ci.Mail.Count(m => m != null && !m.Opened);
                foreach (var m in ci.Mail.Where(x => x != null).OrderByDescending(x => x.DateSent).Take(20))
                {
                    mail.Recent.Add(new CharacterMailDto
                    {
                        MailId = m.MailID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Sender = m.Sender,
                        Preview = CharacterLogic.MailPreview(m.Message),
                        DateSent = CharacterLogic.NullIfMin(m.DateSent),
                        Opened = m.Opened,
                        Locked = m.Locked,
                        Collected = m.Collected,
                        Gold = m.Gold,
                        ItemCount = m.Items?.Count(x => x != null) ?? 0,
                    });
                }
            }

            dto.Mail = mail;

            // Items
            var storage = acc?.Storage;
            dto.Items = new CharacterItemsDto
            {
                Equipment = Items(ci.Equipment, CharacterLogic.ContainerEquipment),
                Inventory = Items(ci.Inventory, CharacterLogic.ContainerInventory),
                QuestInventory = Items(ci.QuestInventory, CharacterLogic.ContainerQuestInventory),
                Storage = Items(storage, CharacterLogic.ContainerStorage),
                InventorySize = ci.Inventory?.Length ?? 0,
                QuestInventorySize = ci.QuestInventory?.Length ?? 0,
                StorageSize = storage?.Length ?? 0,
                UsableStorageSize = CharacterLogic.UsableStorageSize(storage?.Length ?? 0,
                    acc != null && CharacterLogic.IsExpandedStorageActive(acc.HasExpandedStorage, acc.ExpandedStorageExpiryDate, envir.Now)),
            };

            return dto;
        }
    }
}
