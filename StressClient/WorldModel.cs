using System.Collections.Concurrent;
using System.Drawing;

namespace StressClient;

internal sealed class MonsterState
{
    public uint Id;
    public string Name = string.Empty;
    public Monster Image;
    public byte AI;
    public ushort Level = 1;
    public Point Location;
    public MirDirection Direction;
    public bool Dead, Skeleton;
    public uint MasterId;
    public int HPPercent = -1;
    public long LastSeen;

    /// <summary>Town guards: AI 6 and 58 = <c>Guard</c> (players cannot hit them at all), 57 = <c>TownArcher</c>
    /// (Server/MirObjects/MonsterObject.cs GetMonster).</summary>
    public bool IsGuard => AI is 6 or 57 or 58;

    /// <summary>A monster a bot may attack: alive, not someone's pet (summon, tame, hero pet) and not a town guard.</summary>
    public bool Huntable => !Dead && MasterId == 0 && !IsGuard;
}

internal sealed class PlayerState
{
    public uint Id;
    public string Name = string.Empty;
    public ushort Level;
    public Point Location;
    public MirDirection Direction;
    public bool Dead;
}

internal sealed class NpcState
{
    public uint Id;
    public string Name = string.Empty;
    public Point Location;
}

internal sealed class GroundItemState
{
    public uint Id;
    public string Name = string.Empty;
    public Point Location;
}

internal sealed class GroundGoldState
{
    public uint Id;
    public uint Gold;
    public Point Location;
}

/// <summary>
/// One bot's mirror of what the server has told it: own state and vitals, visible monsters/players/NPCs/items,
/// the current map identity, and the monster/item information the client caches. Owned by one worker thread.
/// </summary>
internal sealed class WorldModel
{
    public uint ObjectId;
    public string CharacterName = string.Empty;
    public MirClass Class;
    public ushort Level;
    public Point Location;
    public MirDirection Direction;
    public int HP, MP;
    public int MaxHP, MaxMP;
    public long Experience, MaxExperience;
    public uint Gold;
    public bool Dead;

    private BaseStats _baseStats;

    public UserItem[] Inventory = Array.Empty<UserItem>();
    public UserItem[] Equipment = Array.Empty<UserItem>();

    public readonly Dictionary<Spell, ClientMagic> Magics = new();
    public readonly HashSet<BuffType> Buffs = new();

    public readonly Dictionary<int, ClientQuestProgress> Quests = new();
    public readonly Dictionary<int, ClientQuestInfo> QuestInfos = new();
    public readonly HashSet<int> OfferedQuests = new();
    public readonly HashSet<int> CompletedQuestIds = new();

    /// <summary>The last monster/player that hit us (S.Struck / S.ObjectStruck on our object).</summary>
    public uint LastAttackerId;
    public long LastAttackerAt;

    public bool InGroup;

    public int MapIndex = -1;
    public string MapFileName = string.Empty;
    public string MapTitle = string.Empty;
    public int MapWidth, MapHeight;
    public long MapChangedAt;

    public readonly Dictionary<uint, MonsterState> Monsters = new();
    public readonly Dictionary<uint, PlayerState> Players = new();
    public readonly Dictionary<uint, NpcState> Npcs = new();
    public readonly Dictionary<uint, GroundItemState> Items = new();
    public readonly Dictionary<uint, GroundGoldState> GoldPiles = new();
    public readonly HashSet<byte> OpenDoors = new();

    /// <summary>Monster information received through S.NewMonsterInfo (name → info; level, AI, stats).</summary>
    public readonly Dictionary<string, ClientMonsterInfo> MonsterInfo = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<int, ItemInfo> ItemInfos = new();

    public void ClearSession()
    {
        _occupancyDirty = true;
        Monsters.Clear();
        Players.Clear();
        Npcs.Clear();
        Items.Clear();
        GoldPiles.Clear();
        OpenDoors.Clear();
        Inventory = Array.Empty<UserItem>();
        Equipment = Array.Empty<UserItem>();
        Magics.Clear();
        Buffs.Clear();
        Quests.Clear();
        QuestInfos.Clear();
        OfferedQuests.Clear();
        CompletedQuestIds.Clear();
        LastAttackerId = 0;
        LastAttackerAt = 0;
        InGroup = false;
        MaxHP = MaxMP = 0;
        _baseStats = null;
        MapIndex = -1;
        MapFileName = string.Empty;
        MapTitle = string.Empty;
        MapWidth = MapHeight = 0;
        Dead = false;
        HP = MP = 0;
        Gold = 0;
    }

    public void ClearObjects()
    {
        _occupancyDirty = true;
        Monsters.Clear();
        Players.Clear();
        Npcs.Clear();
        Items.Clear();
        GoldPiles.Clear();
        OpenDoors.Clear();
    }

    public MonsterState AddMonster(ServerPackets.ObjectMonster om, long now)
    {
        _occupancyDirty = true;
        if (!Monsters.TryGetValue(om.ObjectID, out var mon))
        {
            mon = new MonsterState { Id = om.ObjectID };
            Monsters[om.ObjectID] = mon;
        }

        mon.Name = om.Name ?? string.Empty;
        mon.Image = om.Image;
        mon.AI = om.AI;
        mon.Location = om.Location;
        mon.Direction = om.Direction;
        mon.Dead = om.Dead;
        mon.Skeleton = om.Skeleton;
        mon.MasterId = om.MasterObjectId;
        mon.LastSeen = now;
        if (MonsterInfo.TryGetValue(mon.Name, out var info)) mon.Level = info.Level;
        return mon;
    }

    public void Track(uint id, Point location, MirDirection direction)
    {
        if (Monsters.TryGetValue(id, out var mon))
        {
            mon.Location = location;
            mon.Direction = direction;
            _occupancyDirty = true;
            return;
        }

        if (Players.TryGetValue(id, out var player))
        {
            player.Location = location;
            player.Direction = direction;
            _occupancyDirty = true;
        }
    }

    public void AddPlayer(PlayerState player)
    {
        Players[player.Id] = player;
        _occupancyDirty = true;
    }

    public void AddNpc(NpcState npc)
    {
        Npcs[npc.Id] = npc;
        _occupancyDirty = true;
    }

    public void Remove(uint id)
    {
        _occupancyDirty = true;
        Monsters.Remove(id);
        Players.Remove(id);
        Npcs.Remove(id);
        Items.Remove(id);
        GoldPiles.Remove(id);
    }

    /// <summary>True when another mobile occupies the cell. The bot's current target monster is ignored so a path
    /// can end next to (or on the far side of) it.</summary>
    public bool IsBlockedByObject(int x, int y, uint ignoreMonster)
    {
        if (_occupancyDirty) RebuildOccupancy();
        if (!_occupied.TryGetValue(CellKey(x, y), out int count)) return false;

        // The ignored monster is the only thing on its cell: free for this caller.
        if (count == 1 && ignoreMonster != 0 && Monsters.TryGetValue(ignoreMonster, out var target) &&
            !target.Dead && target.Location.X == x && target.Location.Y == y) return false;

        return true;
    }

    // Cell -> live mobiles on it. A* asks per neighbour cell, so a scan of every known object per question made
    // pathing O(cells x objects) and starved the workers once thousands of bots crowded together. Rebuilt lazily,
    // at most once per batch of object changes.
    private readonly Dictionary<int, int> _occupied = new();
    private bool _occupancyDirty = true;

    private static int CellKey(int x, int y) => (y << 16) | (x & 0xFFFF);

    private void RebuildOccupancy()
    {
        _occupied.Clear();
        foreach (var mon in Monsters.Values)
            if (!mon.Dead) Occupy(mon.Location);
        foreach (var player in Players.Values)
            if (!player.Dead) Occupy(player.Location);
        foreach (var npc in Npcs.Values)
            Occupy(npc.Location);
        _occupancyDirty = false;
    }

    private void Occupy(Point p)
    {
        int key = CellKey(p.X, p.Y);
        _occupied[key] = _occupied.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    // ------------------------------------------------------------------ vitals, inventory, magic, quests

    public void ApplyBaseStats(BaseStats stats)
    {
        _baseStats = stats;
        RecomputeMaxVitals();
    }

    public void RecomputeMaxVitals()
    {
        if (_baseStats == null) return;
        foreach (var stat in _baseStats.Stats)
        {
            if (stat.Type == Stat.HP) MaxHP = stat.Calculate(_baseStats.Job, Level);
            else if (stat.Type == Stat.MP) MaxMP = stat.Calculate(_baseStats.Job, Level);
        }
    }

    public void SetItems(UserItem[] inventory, UserItem[] equipment)
    {
        Inventory = inventory ?? Array.Empty<UserItem>();
        Equipment = equipment ?? Array.Empty<UserItem>();
        ResolveAllItems();
    }

    public ItemInfo ResolveItem(UserItem item)
    {
        if (item == null) return null;
        if (item.Info != null) return item.Info;
        if (ItemInfos.TryGetValue(item.ItemIndex, out var info))
        {
            item.Info = info;
            return info;
        }
        return null;
    }

    public void ResolveAllItems()
    {
        foreach (var item in Inventory) ResolveItem(item);
        foreach (var item in Equipment) ResolveItem(item);
    }

    public int FreeSlots
    {
        get
        {
            int free = 0;
            foreach (var item in Inventory) if (item == null) free++;
            return free;
        }
    }

    public bool BagFull => Inventory.Length > 0 && FreeSlots == 0;

    public bool AddItem(UserItem item)
    {
        if (item == null) return false;
        ResolveItem(item);
        for (int i = 0; i < Inventory.Length; i++)
        {
            if (Inventory[i] != null) continue;
            Inventory[i] = item;
            return true;
        }
        return false;
    }

    public UserItem FindInventory(ulong uniqueId)
    {
        foreach (var item in Inventory)
            if (item != null && item.UniqueID == uniqueId) return item;
        return null;
    }

    public bool RemoveItem(ulong uniqueId, int count = 1)
    {
        for (int i = 0; i < Inventory.Length; i++)
        {
            var item = Inventory[i];
            if (item == null || item.UniqueID != uniqueId) continue;
            if (count >= item.Count) Inventory[i] = null;
            else item.Count -= (ushort)count;
            return true;
        }

        for (int i = 0; i < Equipment.Length; i++)
            if (Equipment[i] != null && Equipment[i].UniqueID == uniqueId) { Equipment[i] = null; return true; }

        return false;
    }

    public void MoveInventory(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Inventory.Length || to >= Inventory.Length) return;
        (Inventory[from], Inventory[to]) = (Inventory[to], Inventory[from]);
    }

    /// <summary>Mirrors S.EquipItem (Grid = Inventory): the item leaves the bag and takes the equipment slot,
    /// swapping anything already there back into the bag.</summary>
    public bool ApplyEquip(ulong uniqueId, int to)
    {
        if (to < 0 || to >= Equipment.Length) return false;

        UserItem item = null;
        for (int i = 0; i < Inventory.Length; i++)
        {
            if (Inventory[i] == null || Inventory[i].UniqueID != uniqueId) continue;
            item = Inventory[i];
            Inventory[i] = null;
            break;
        }
        if (item == null) return false;

        ResolveItem(item);
        var old = Equipment[to];
        Equipment[to] = item;
        if (old != null && old != item) AddItem(old);
        return true;
    }

    public UserItem Equipped(EquipmentSlot slot)
    {
        int i = (int)slot;
        return i >= 0 && i < Equipment.Length ? Equipment[i] : null;
    }

    public bool HasPotion(out UserItem potion)
    {
        potion = null;
        foreach (var item in Inventory)
        {
            var info = ResolveItem(item);
            if (info == null || info.Type != ItemType.Potion || info.Stats[Stat.HP] <= 0) continue;
            potion = item;
            return true;
        }
        return false;
    }

    public int PotionCount()
    {
        int count = 0;
        foreach (var item in Inventory)
        {
            var info = ResolveItem(item);
            if (info != null && info.Type == ItemType.Potion) count += item.Count;
        }
        return count;
    }

    /// <summary>The next item worth selling: ore/trinkets, or a weapon/armour the bot cannot use better than what it wears.</summary>
    public UserItem FirstJunk()
    {
        foreach (var item in Inventory)
        {
            var info = ResolveItem(item);
            if (info == null || info.Type == ItemType.Potion) continue;

            if (info.Type == ItemType.Weapon || info.Type == ItemType.Armour)
            {
                var equipped = Equipped(info.Type == ItemType.Weapon ? EquipmentSlot.Weapon : EquipmentSlot.Armour);
                if (equipped == null || GearScore(equipped) <= GearScore(item)) return item;
                continue;
            }

            if (info.Type is ItemType.Ore or ItemType.Nothing) return item;
        }
        return null;
    }

    public bool HasDamagedEquipment(out UserItem damaged)
    {
        damaged = null;
        foreach (var item in Equipment)
        {
            if (item == null || item.MaxDura == 0 || item.CurrentDura >= item.MaxDura) continue;
            damaged = item;
            return true;
        }
        return false;
    }

    public static int GearScore(UserItem item)
    {
        var info = item?.Info;
        if (info == null) return 0;
        var s = info.Stats;
        return s[Stat.MaxDC] * 2 + s[Stat.MinDC] + s[Stat.MaxMC] * 2 + s[Stat.MinMC] + s[Stat.MaxSC] * 2 + s[Stat.MinSC]
             + s[Stat.MaxAC] * 2 + s[Stat.MinAC] + s[Stat.MaxMAC] * 2 + s[Stat.MinMAC];
    }

    public ClientMagic MagicFor(Spell spell) => Magics.TryGetValue(spell, out var magic) ? magic : null;

    public void ApplyQuest(ClientQuestProgress progress, QuestState state)
    {
        if (progress == null) return;
        if (state == QuestState.Remove) Quests.Remove(progress.Id);
        else Quests[progress.Id] = progress;
    }
}

/// <summary>What the fleet knows about one map index: portal exits, respawn regions and safe zones.</summary>
internal sealed class MapFacts
{
    public int Index;
    public string FileName = string.Empty;
    public string Title = string.Empty;
    public int Width, Height;
    public readonly List<ClientMovementInfo> Movements = new();
    public readonly List<ClientMapRespawn> Respawns = new();
    public readonly List<ClientMapSafeZone> SafeZones = new();
    public long UpdatedAt;
}

/// <summary>
/// Fleet-wide facts learned from server packets (map info, map details, monster info). Written by worker threads,
/// read by the Director thread; every mutation and every snapshot read takes the per-map lock.
/// </summary>
internal sealed class WorldDirectory
{
    private readonly ConcurrentDictionary<int, MapFacts> _maps = new();
    private readonly ConcurrentDictionary<string, ClientMonsterInfo> _monsters = new(StringComparer.OrdinalIgnoreCase);

    public MapFacts Get(int index) => _maps.TryGetValue(index, out var facts) ? facts : null;

    public int MapCount => _maps.Count;

    public IEnumerable<MapFacts> All => _maps.Values;

    public void MapChanged(int index, string fileName, string title)
    {
        if (index < 0) return;
        var facts = _maps.GetOrAdd(index, i => new MapFacts { Index = i });
        lock (facts)
        {
            facts.FileName = fileName ?? string.Empty;
            facts.Title = title ?? string.Empty;
            facts.UpdatedAt = MonoClock.Now;
        }
    }

    public void MapInfo(int index, ClientMapInfo info)
    {
        if (index < 0 || info == null) return;
        var facts = _maps.GetOrAdd(index, i => new MapFacts { Index = i });
        lock (facts)
        {
            facts.Width = info.Width;
            facts.Height = info.Height;
            if (!string.IsNullOrEmpty(info.Title)) facts.Title = info.Title;
            facts.Movements.Clear();
            if (info.Movements != null) facts.Movements.AddRange(info.Movements);
            facts.UpdatedAt = MonoClock.Now;
        }
    }

    public void MapDetails(int index, byte part, List<ClientMapRespawn> respawns, List<ClientMapSafeZone> safeZones)
    {
        if (index < 0) return;
        var facts = _maps.GetOrAdd(index, i => new MapFacts { Index = i });
        lock (facts)
        {
            if (part == 0)
            {
                facts.Respawns.Clear();
                facts.SafeZones.Clear();
            }
            if (respawns != null) facts.Respawns.AddRange(respawns);
            if (safeZones != null) facts.SafeZones.AddRange(safeZones);
            facts.UpdatedAt = MonoClock.Now;
        }
    }

    public void MonsterInfo(ClientMonsterInfo info)
    {
        if (info == null || string.IsNullOrEmpty(info.Name)) return;
        _monsters[info.Name] = info;
    }

    public ClientMonsterInfo GetMonsterInfo(string name) =>
        !string.IsNullOrEmpty(name) && _monsters.TryGetValue(name, out var info) ? info : null;
}
