using System.Drawing;
﻿namespace Server.MirDatabase
{
    public class MineSet
    {
        public string Name = string.Empty;
        public byte SpotRegenRate = 5;
        public byte MaxStones = 80;
        public byte HitRate = 25;
        public byte DropRate = 10;
        public byte TotalSlots = 100;
        public List<MineDrop> Drops = new List<MineDrop>();
        private bool DropsSet = false;

        public MineSet(byte mineType = 0)
        {
            switch (mineType)
            {
                case 1:
                    TotalSlots = 120;
                    Drops.Add(new MineDrop() { ItemName = "GoldOre", MinSlot = 1, MaxSlot = 2, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "SilverOre", MinSlot = 3, MaxSlot = 20, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "CopperOre", MinSlot = 21, MaxSlot = 45, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "BlackIronOre", MinSlot = 46, MaxSlot = 56, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    break;
                case 2:
                    TotalSlots = 100;
                    Drops.Add(new MineDrop() { ItemName = "PlatinumOre", MinSlot = 1, MaxSlot = 2, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "RubyOre", MinSlot = 3, MaxSlot = 20, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "NephriteOre", MinSlot = 21, MaxSlot = 45, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    Drops.Add(new MineDrop() { ItemName = "AmethystOre", MinSlot = 46, MaxSlot = 56, MinDura = 3, MaxDura = 16, BonusChance = 20, MaxBonusDura = 10 });
                    break;
            }
        }

        public void SetDrops(List<ItemInfo> items)
        {
            if (DropsSet) return;
            for (int i = 0; i < Drops.Count; i++)
            {
                for (int j = 0; j < items.Count; j++)
                {
                    ItemInfo info = items[j];
                    if (String.Compare(info.Name.Replace(" ", ""), Drops[i].ItemName, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    Drops[i].Item = info;
                    break;
                }
            }
            DropsSet = true;
        }
    }

    public class MineSpot
    {
        public byte StonesLeft = 0;
        public long LastRegenTick = 0;
        public MineSet Mine;
    }

    /// <summary>
    /// A map's mine: the mine set of every cell (the map's MineIndex, overridden by its mine zones, later zones winning) and the
    /// spot state of the cells mined so far. A spot is created on first use with the state a spot had at map load
    /// (StonesLeft 0, LastRegenTick 0), so only mined cells cost memory. Game thread only (mining is player/hero code).
    /// </summary>
    public sealed class MineGrid
    {
        private readonly struct Zone
        {
            public readonly int Left, Top, Right, Bottom;
            public readonly MineSet Set;

            public Zone(int left, int top, int right, int bottom, MineSet set)
            {
                Left = left; Top = top; Right = right; Bottom = bottom; Set = set;
            }
        }

        private readonly int _width, _height;
        private readonly MineSet _baseSet;
        private readonly List<Zone> _zones = new List<Zone>();
        private readonly Dictionary<int, MineSpot> _spots = new Dictionary<int, MineSpot>();

        public MineGrid(int width, int height, MineSet baseSet)
        {
            _width = width;
            _height = height;
            _baseSet = baseSet;
        }

        /// <summary>Cells left &lt;= x &lt;= right, top &lt;= y &lt;= bottom use <paramref name="set"/> (null = no mine).</summary>
        public void AddZone(int left, int top, int right, int bottom, MineSet set)
        {
            _zones.Add(new Zone(left, top, right, bottom, set));
        }

        /// <summary>The spot at (x, y), or null when the cell has no mine set. Throws outside the map, as the old array did.</summary>
        public MineSpot this[int x, int y]
        {
            get
            {
                if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) throw new IndexOutOfRangeException();

                var key = x * _height + y;
                if (_spots.TryGetValue(key, out var spot)) return spot;

                var set = SetAt(x, y);
                if (set == null) return null;

                spot = new MineSpot { Mine = set };
                _spots.Add(key, spot);
                return spot;
            }
        }

        private MineSet SetAt(int x, int y)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                var zone = _zones[i];
                if (x >= zone.Left && x <= zone.Right && y >= zone.Top && y <= zone.Bottom) return zone.Set;
            }

            return _baseSet;
        }
    }

    public class MineDrop
    {
        public string ItemName;
        public ItemInfo Item;
        public byte MinSlot = 0;
        public byte MaxSlot = 0;
        public byte MinDura = 1;
        public byte MaxDura = 1;
        public byte BonusChance = 0;
        public byte MaxBonusDura = 1;
    }

    public class MineZone
    {
        public byte Mine;
        public Point Location;
        public ushort Size;

        public MineZone()
        {
        }

        public MineZone(BinaryReader reader)
        {
            Location = new Point(reader.ReadInt32(), reader.ReadInt32());
            Size = reader.ReadUInt16();
            Mine = reader.ReadByte();
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Location.X);
            writer.Write(Location.Y);
            writer.Write(Size);
            writer.Write(Mine);
        }
        public override string ToString()
        {
            return string.Format("Mine: {0}- {1}", Functions.PointToString(Location), Mine);
        }
    }
}
