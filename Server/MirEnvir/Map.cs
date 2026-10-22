using Server.MirEnvir.Storage;
using System.Drawing;
﻿using Server.MirDatabase;
using Server.MirObjects;
using Shared;
using S = ServerPackets;

namespace Server.MirEnvir
{
    public class Map
    {
        private static Envir Envir
        {
            get { return Envir.Main; }
        }

        protected static MessageQueue MessageQueue
        {
            get { return MessageQueue.Instance; }
        }

        public MapInfo Info;

        public int Thread = 0;

        public int Width, Height;
        public Cell[,] Cells;
        /// <summary>Walkable cells in x-major, y-minor order (exact capacity); <see cref="WalkableColumnStart"/> indexes it by column.</summary>
        public List<Point> WalkableCells;
        /// <summary>Index of column x's first entry in <see cref="WalkableCells"/>; entry Width is the count (Width + 1 entries).</summary>
        public int[] WalkableColumnStart;
        /// <summary>Door of each door cell, keyed x * Height + y; null when the map has no doors (most maps).</summary>
        private Dictionary<int, Door> _doorCells;
        public List<Door> Doors = new List<Door>();
        /// <summary>Mine spots, created on first use; null when the map has no mine.</summary>
        public MineGrid Mine;
        public long LightningTime, FireTime;
        public int MonsterCount;

        public List<NPCObject> NPCs = new List<NPCObject>();
        public List<SpellObject> Spells = new List<SpellObject>();
        public List<PlayerObject> Players = new List<PlayerObject>();
        /// <summary>Players by sector (P9-07, [Optimisation] SectorGrid, read at world start); null when off.</summary>
        public PlayerGrid Grid;

        /// <summary>Players a reader must check around <paramref name="center"/> (allocation-free; callers check the exact range).</summary>
        public PlayersNear PlayersNear(Point center, int range) => new PlayersNear(this, center, range);
        public List<MapRespawn> Respawns = new List<MapRespawn>();
        public readonly MapActionList ActionList;
        public List<HeroObject> Heroes = new List<HeroObject>();

        public List<ConquestObject> Conquest = new List<ConquestObject>();
        public ConquestObject tempConquest;

        public Map(MapInfo info)
        {
            Info = info;
            Thread = Envir.Random.Next(Settings.ThreadLimit);
            ActionList = new MapActionList(this);
        }

        public Door AddDoor(byte DoorIndex, Point location)
        {
            DoorIndex = (byte)(DoorIndex & 0x7F);
            for (int i = 0; i < Doors.Count; i++)
                if (Doors[i].index == DoorIndex)
                    return Doors[i];
            Door DoorInfo = new Door() { index = DoorIndex, Location = location };
            Doors.Add(DoorInfo);
            return DoorInfo;
        }
        
        public bool OpenDoor(byte DoorIndex)
        {
            for (int i = 0; i < Doors.Count; i++)
                if (Doors[i].index == DoorIndex)
                {
                    Doors[i].DoorState = DoorState.Open;
                    Doors[i].LastTick = Envir.Time;
                    return true;
                }
            return false;
        }

        /// <summary>
        /// True when a cell of door <paramref name="doorIndex"/> lies within <paramref name="range"/> cells of
        /// <paramref name="location"/> (X1-26: C.Opendoor had no range check). The client asks to open the door of the cell
        /// it is about to step into (up to a run ahead), so the range allows for that and a little position lag.
        /// </summary>
        public bool IsDoorNear(byte doorIndex, Point location, int range)
        {
            if (_doorCells == null) return false;
            doorIndex = (byte)(doorIndex & 0x7F);
            for (var x = location.X - range; x <= location.X + range; x++)
                for (var y = location.Y - range; y <= location.Y + range; y++)
                {
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    var door = GetDoor(x, y);
                    if (door != null && door.index == doorIndex) return true;
                }
            return false;
        }

        /// <summary>The door on cell (x, y), or null. x and y must be inside the map.</summary>
        public Door GetDoor(int x, int y)
        {
            if (_doorCells == null) return null;
            _doorCells.TryGetValue(x * Height + y, out var door);
            return door;
        }

        private void SetDoorCell(int x, int y, Door door)
        {
            _doorCells ??= new Dictionary<int, Door>();
            _doorCells[x * Height + y] = door;
        }

        // Map loaders: a wall cell is a shared instance, so a fishing value picks the shared wall that carries it.
        private void SetFishingAttribute(int x, int y, sbyte value)
        {
            var cell = Cells[x, y];
            if (cell.IsShared)
                Cells[x, y] = Cell.SharedWall(cell.Attribute, value);
            else
                cell.FishingAttribute = value;
        }

        private byte FindType(byte[] input)
        {
            //c# custom map format
            if ((input[2] == 0x43) && (input[3] == 0x23))
            {
                return 100;
            }
            //wemade mir3 maps have no title they just start with blank bytes
            if (input[0] == 0)
                return 5;
            //shanda mir3 maps start with title: (C) SNDA, MIR3.
            if ((input[0] == 0x0F) && (input[5] == 0x53) && (input[14] == 0x33))
                return 6;

            //wemades antihack map (laby maps) title start with: Mir2 AntiHack
            if ((input[0] == 0x15) && (input[4] == 0x32) && (input[6] == 0x41) && (input[19] == 0x31))
                return 4;

            //wemades 2010 map format i guess title starts with: Map 2010 Ver 1.0
            if ((input[0] == 0x10) && (input[2] == 0x61) && (input[7] == 0x31) && (input[14] == 0x31))
                return 1;

            //shanda's 2012 format and one of shandas(wemades) older formats share same header info, only difference is the filesize
            if ((input[4] == 0x0F) || (input[4] == 0x03) && (input[18] == 0x0D) && (input[19] == 0x0A))
            {
                int W = input[0] + (input[1] << 8);
                int H = input[2] + (input[3] << 8);
                if (input.Length > (52 + (W * H * 14)))
                    return 3;
                else
                    return 2;
            }

            //3/4 heroes map format (myth/lifcos i guess)
            if ((input[0] == 0x0D) && (input[1] == 0x4C) && (input[7] == 0x20) && (input[11] == 0x6D))
                return 7;
            return 0;
        }

        private void LoadMapCellsv0(byte[] fileBytes)
        {
            int offSet = 0;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 52;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 12
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.

                    offSet += 2;

                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //No Floor Tile.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };

                    offSet += 4;

                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));

                    offSet += 3;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));
                }
        }
        
        private void LoadMapCellsv1(byte[] fileBytes)
        {
            int offSet = 21;

            int w = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            int xor = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            int h = BitConverter.ToInt16(fileBytes, offSet);
            Width = w ^ xor;
            Height = h ^ xor;
            Cells = new Cell[Width, Height];

            offSet = 54;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {
                    if (((BitConverter.ToInt32(fileBytes, offSet) ^ 0xAA38AA38) & 0x20000000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.

                    offSet += 6;
                    if (((BitConverter.ToInt16(fileBytes, offSet) ^ xor) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //No Floor Tile.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 2;
                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));
                    offSet += 5;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));

                    offSet += 1;
                }
        }

        private void LoadMapCellsv2(byte[] fileBytes)
        {
            int offSet = 0;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 52;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 14
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //No Floor Tile.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };

                    offSet += 2;
                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));
                    offSet += 5;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));

                    offSet += 2;
                }
        }

        private void LoadMapCellsv3(byte[] fileBytes)
        {
            int offSet = 0;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 52;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 36
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //No Floor Tile.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 2;
                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));
                    offSet += 12;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));

                    offSet += 17;
                }
        }

        private void LoadMapCellsv4(byte[] fileBytes)
        {
            int offSet = 31;
            int w = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            int xor = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            int h = BitConverter.ToInt16(fileBytes, offSet);
            Width = w ^ xor;
            Height = h ^ xor;
            Cells = new Cell[Width, Height];

            offSet = 64;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 12
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.

                    offSet += 2;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 4;
                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));
                    offSet += 6;
                }
        }

        private void LoadMapCellsv5(byte[] fileBytes)
        {
            int offSet = 22;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 28 + (3 * ((Width / 2) + (Width % 2)) * (Height / 2));
            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 14
                    if ((fileBytes[offSet] & 0x01) != 1)
                        Cells[x, y] = Cell.HighWall;
                    else if ((fileBytes[offSet] & 0x02) != 2)
                        Cells[x, y] = Cell.LowWall;
                    else
                        Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 13;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));
                }
        }

        private void LoadMapCellsv6(byte[] fileBytes)
        {
            int offSet = 16;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 2;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 40;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 20
                    if ((fileBytes[offSet] & 0x01) != 1)
                        Cells[x, y] = Cell.HighWall;
                    else if ((fileBytes[offSet] & 0x02) != 2)
                        Cells[x, y] = Cell.LowWall;
                    else
                        Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 20;
                }
        }

        private void LoadMapCellsv7(byte[] fileBytes)
        {
            int offSet = 21;
            Width = BitConverter.ToInt16(fileBytes, offSet);
            offSet += 4;
            Height = BitConverter.ToInt16(fileBytes, offSet);
            Cells = new Cell[Width, Height];

            offSet = 54;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {//total 15
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.
                    offSet += 6;
                    if ((BitConverter.ToInt16(fileBytes, offSet) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.
                    //offSet += 2;
                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offSet += 2;
                    if (fileBytes[offSet] > 0)
                        SetDoorCell(x, y, AddDoor(fileBytes[offSet], new Point(x, y)));
                    offSet += 4;

                    byte light = fileBytes[offSet++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));

                    offSet += 2;
                }
        }

        private void LoadMapCellsV100(byte[] Bytes)
        {
            int offset = 4;
            if ((Bytes[0] != 1) || (Bytes[1] != 0)) return;//only support version 1 atm
            Width = BitConverter.ToInt16(Bytes, offset);
            offset += 2;
            Height = BitConverter.ToInt16(Bytes, offset);
            Cells = new Cell[Width, Height];

            offset = 8;

            for (int x = 0; x < Width; x++)
                for (int y = 0; y < Height; y++)
                {
                    offset += 2;
                    if ((BitConverter.ToInt32(Bytes, offset) & 0x20000000) != 0)
                        Cells[x, y] = Cell.HighWall; //Can Fire Over.
                    offset += 10;
                    if ((BitConverter.ToInt16(Bytes, offset) & 0x8000) != 0)
                        Cells[x, y] = Cell.LowWall; //Can't Fire Over.

                    if (Cells[x, y] == null) Cells[x, y] = new Cell { Attribute = CellAttribute.Walk };
                    offset += 2;
                    if (Bytes[offset] > 0)
                        SetDoorCell(x, y, AddDoor(Bytes[offset], new Point(x, y)));
                    offset += 11;

                    byte light = Bytes[offset++];

                    if (light >= 100 && light <= 119)
                        SetFishingAttribute(x, y, (sbyte)(light - 100));
                }
                
        }

        public bool Load()
        {
            try
            {
                string fileName = Path.Combine(Settings.MapPath, Info.FileName + ".map");
                if (File.Exists(fileName))
                {
                    byte[] fileBytes = File.ReadAllBytes(fileName);
                    switch(FindType(fileBytes))
                    {
                        case 0:
                            LoadMapCellsv0(fileBytes);
                            break;
                        case 1:
                            LoadMapCellsv1(fileBytes);
                            break;
                        case 2:
                            LoadMapCellsv2(fileBytes);
                            break;
                        case 3:
                            LoadMapCellsv3(fileBytes);
                            break;
                        case 4:
                            LoadMapCellsv4(fileBytes);
                            break;
                        case 5:
                            LoadMapCellsv5(fileBytes);
                            break;
                        case 6:
                            LoadMapCellsv6(fileBytes);
                            break;
                        case 7:
                            LoadMapCellsv7(fileBytes);
                            break;
                        case 100:
                            LoadMapCellsV100(fileBytes);
                            break;
                    }

                    GetWalkableCells();
                    if (Settings.SectorGrid) Grid = new PlayerGrid(this);
                    
                    for (int i = 0; i < Info.Respawns.Count; i++)
                    {
                        MapRespawn info = new MapRespawn(Info.Respawns[i]);
                        if (info.Monster == null) continue;
                        info.Map = this;
                        info.WalkableCells = BuildRespawnCells(info.Info);

                        Respawns.Add(info);

                        if ((info.Info.SaveRespawnTime) && (info.Info.RespawnTicks != 0))
                            Envir.SavedSpawns.Add(info);
                    }

                    for (int i = 0; i < Info.NPCs.Count; i++)
                    {
                        NPCInfo info = Info.NPCs[i];
                        if (!ValidPoint(info.Location)) continue;

                        AddObject(new NPCObject(info) { CurrentMap = this });
                    }

                    for (int i = 0; i < Info.SafeZones.Count; i++)
                        CreateSafeZone(Info.SafeZones[i]);

                    CreateMine();

                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue(ex);
            }

            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.FailedToLoadMap) + Info.Title);
            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.Filename) + Info.FileName);
            return false;
        }

        public void GetWalkableCells()
        {
            if (WalkableCells == null)
            {
                var count = 0;
                for (int x = 0; x < Width; x++)
                    for (int y = 0; y < Height; y++)
                        if (Cells[x, y].Attribute == CellAttribute.Walk)
                            count++;

                var cells = new List<Point>(count);
                var columnStart = new int[Width + 1];
                for (int x = 0; x < Width; x++)
                {
                    columnStart[x] = cells.Count;
                    for (int y = 0; y < Height; y++)
                        if (Cells[x, y].Attribute == CellAttribute.Walk)
                            cells.Add(new Point(x, y));
                }

                columnStart[Width] = cells.Count;
                WalkableColumnStart = columnStart;
                WalkableCells = cells;
            }
        }

        private void CreateSafeZone(SafeZoneInfo info)
        {
            if (Settings.SafeZoneBorder)
            {
                for (int y = info.Location.Y - info.Size; y <= info.Location.Y + info.Size; y++)
                {
                    if (y < 0) continue;
                    if (y >= Height) break;
                    for (int x = info.Location.X - info.Size; x <= info.Location.X + info.Size; x += Math.Abs(y - info.Location.Y) == info.Size ? 1 : info.Size * 2)
                    {
                        if (x < 0) continue;
                        if (x >= Width) break;
                        if (!Cells[x, y].Valid) continue;

                        SpellObject spell = new SpellObject
                        {
                            ExpireTime = long.MaxValue,
                            Spell = Spell.TrapHexagon,
                            TickSpeed = int.MaxValue,
                            CurrentLocation = new Point(x, y),
                            CurrentMap = this,
                            Decoration = true
                        };

                        Cells[x, y].Add(spell);

                        spell.Spawned();
                    }
                }
            }

            if (Settings.SafeZoneHealing)
            {
                for (int y = info.Location.Y - info.Size; y <= info.Location.Y + info.Size; y++)
                {
                    if (y < 0) continue;
                    if (y >= Height) break;
                    for (int x = info.Location.X - info.Size; x <= info.Location.X + info.Size; x++)
                    {
                        if (x < 0) continue;
                        if (x >= Width) break;
                        if (!Cells[x, y].Valid) continue;

                        SpellObject spell = new SpellObject
                            {
                                ExpireTime = long.MaxValue,
                                Value = 25,
                                TickSpeed = 2000,
                                Spell = Spell.Healing,
                                CurrentLocation = new Point(x, y),
                                CurrentMap = this
                            };

                        Cells[x, y].Add(spell);

                        spell.Spawned();
                    }
                }
            }


        }

        // ---- LiveDb (web admin live edits; game thread only, inside an admin action after the mob barrier) ---------------

        /// <summary>
        /// LiveDb: recreates the spell objects of one safe zone (border decorations / healing cells, as at map load). Called after
        /// <see cref="RemoveSafeZoneSpells"/> removed the zone's previous objects.
        /// </summary>
        internal void CreateSafeZoneSpells(SafeZoneInfo info)
        {
            CreateSafeZone(info);
        }

        /// <summary>
        /// LiveDb: removes the spell objects a safe zone at <paramref name="location"/> / <paramref name="size"/> created (map load or
        /// <see cref="CreateSafeZoneSpells"/>): only objects with no caster that never expire and are a zone decoration
        /// (TrapHexagon, Decoration) or a zone healing cell (Healing, not Decoration). Spells players cast always have a caster or an
        /// expiry; conquest war borders (also caster-less decorations) are skipped. Returns the number removed. One pass over the
        /// zone's square of cells.
        /// </summary>
        internal int RemoveSafeZoneSpells(Point location, int size)
        {
            var removed = 0;
            for (int y = location.Y - size; y <= location.Y + size; y++)
            {
                if (y < 0) continue;
                if (y >= Height) break;
                for (int x = location.X - size; x <= location.X + size; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    var objects = Cells[x, y].Objects;
                    if (objects == null) continue;

                    for (int i = objects.Count - 1; i >= 0; i--)
                    {
                        if (objects[i].Race != ObjectType.Spell) continue;
                        var spell = (SpellObject)objects[i];
                        if (!IsSafeZoneSpell(spell) || IsConquestWarEffect(spell)) continue;

                        objects.RemoveAt(i);
                        if (spell.Node != null) spell.Despawn();
                        removed++;
                    }
                }
            }

            return removed;
        }

        private static bool IsSafeZoneSpell(SpellObject spell)
        {
            if (spell.Caster != null || spell.ExpireTime != long.MaxValue) return false;
            return (spell.Decoration && spell.Spell == Spell.TrapHexagon) || (!spell.Decoration && spell.Spell == Spell.Healing);
        }

        private bool IsConquestWarEffect(SpellObject spell)
        {
            for (int i = 0; i < Conquest.Count; i++)
                if (Conquest[i].WarEffects.Contains(spell)) return true;
            return false;
        }

        /// <summary>LiveDb: rebuilds the mine from Info.MineIndex / Info.MineZones (mine state resets, as at map load).</summary>
        internal void RebuildMine()
        {
            Mine = null;
            CreateMine();
        }

        /// <summary>
        /// The walkable cells a respawn spawns on: the map's walkable cells inside the respawn's spread box, in the map list's order
        /// (map load and LiveDb). A view over <see cref="WalkableCells"/>, not a copy.
        /// </summary>
        internal WalkableBox BuildRespawnCells(RespawnInfo info)
        {
            return new WalkableBox(this, info.Location.X - info.Spread, info.Location.Y - info.Spread,
                info.Location.X + info.Spread, info.Location.Y + info.Spread);
        }

        private void CreateMine()
        {
            if ((Info.MineIndex == 0) && (Info.MineZones.Count == 0)) return;
            MineSet baseSet = null;
            if ((Info.MineIndex != 0) && (Settings.MineSetList.Count > Info.MineIndex - 1))
            {
                Settings.MineSetList[Info.MineIndex - 1].SetDrops(Envir.ItemInfoList);
                baseSet = Settings.MineSetList[Info.MineIndex - 1];
            }

            var mine = new MineGrid(Width, Height, baseSet);
            if (Info.MineZones.Count > 0)
            {
                for (int i = 0; i < Info.MineZones.Count; i++)
                {
                    MineZone Zone = Info.MineZones[i];
                    if (Zone.Mine != 0)
                        Settings.MineSetList[Zone.Mine - 1].SetDrops(Envir.ItemInfoList);
                    if (Settings.MineSetList.Count < Zone.Mine) continue;
                    // The old per-cell fill covered [Location - Size, Location + Size) on both axes.
                    mine.AddZone(Zone.Location.X - Zone.Size, Zone.Location.Y - Zone.Size, Zone.Location.X + Zone.Size - 1,
                        Zone.Location.Y + Zone.Size - 1, Zone.Mine == 0 ? null : Settings.MineSetList[Zone.Mine - 1]);
                }
            }

            Mine = mine;
        }

        public Cell GetCell(Point location)
        {
            return Cells[location.X, location.Y];
        }

        public Cell GetCell(int x, int y)
        {
            return Cells[x, y];
        }

        public bool ValidPoint(Point location)
        {
            return location.X >= 0 && location.X < Width && location.Y >= 0 && location.Y < Height && GetCell(location).Valid;
        }
        public bool ValidPoint(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height && GetCell(x, y).Valid;
        }

        public bool CheckDoorOpen(Point location)
        {
            var door = GetDoor(location.X, location.Y);
            if (door == null) return true;
            if (door.DoorState != DoorState.Open) return false;
            return true;
        }

        // Respawn delays are minute-scale, so respawns don't need checking on every loop iteration (roadmap P9). With a real world
        // ProcessRespawns for every map costs ~1.5 ms per sweep, hence the 1 s default.
        private long NextRespawnCheckTime;
        private int LastRespawnCheckMs = -1;

        public void Process()
        {
            if (Envir.Time >= NextRespawnCheckTime)
            {
                // A random phase after the first check (and after a live interval change), so the maps' respawn sweeps don't all
                // land in one iteration.
                var interval = Settings.MapRespawnCheckMs;
                var rephase = interval != LastRespawnCheckMs && interval > 1;
                LastRespawnCheckMs = interval;
                NextRespawnCheckTime = Envir.Time + (rephase ? 1 + Envir.Random.Next(interval) : interval);

                // P9-07 EmptyMapSleep: with nobody here, check respawns only every EmptyMapSleepMs; the first player or hero to
                // arrive, or a script reading MonsterCount, catches them up at once (WakeRespawns / CatchUpRespawns).
                RespawnsAsleep = Settings.EmptyMapSleep && Players.Count == 0 && Heroes.Count == 0 && !RespawnSleepExempt();
                if (RespawnsAsleep) NextRespawnCheckTime = Envir.Time + EmptyMapSleepMs;

                ProcessRespawns();
            }

            // Doors close 5 s after opening; the close time is folded into NextProcessTime below, so this runs on time.
            for (int i = 0; i < Doors.Count; i++)
            {
                if ((Doors[i].DoorState == DoorState.Open) && (Doors[i].LastTick + 5000 < Envir.Time))
                {
                    Doors[i].DoorState = 0;

                    Broadcast(new S.Opendoor() { DoorIndex = Doors[i].index, Close = true }, Doors[i].Location);
                }
            }

            if ((Info.Lightning) && Envir.Time > LightningTime)
            {
                LightningTime = Envir.Time + Envir.Random.Next(3000, 15000);
                for (int i = Players.Count - 1; i >= 0; i--)
                {
                    PlayerObject player = Players[i];
                    Point location;
                    if (Envir.Random.Next(4) == 0)
                    {
                        location = player.CurrentLocation;          
                    }
                    else
                        location = new Point(player.CurrentLocation.X - 10 + Envir.Random.Next(20), player.CurrentLocation.Y - 10 + Envir.Random.Next(20));

                    if (!ValidPoint(location)) continue;

                    SpellObject lightning = new SpellObject
                    {
                        Spell = Spell.MapLightning,
                        Value = Envir.Random.Next(Info.LightningDamage),
                        ExpireTime = Envir.Time + (1000),
                        TickSpeed = 500,
                        Caster = null,
                        CurrentLocation = location,
                        CurrentMap = this,
                        Direction = MirDirection.Up
                    };
                    AddObject(lightning);
                    lightning.Spawned();
                }
            }

            if ((Info.Fire) && Envir.Time > FireTime)
            {
                FireTime = Envir.Time + Envir.Random.Next(3000, 15000);
                for (int i = Players.Count - 1; i >= 0; i--)
                {
                    PlayerObject player = Players[i];
                    Point location;
                    if (Envir.Random.Next(4) == 0)
                    {
                        location = player.CurrentLocation;

                    }
                    else
                        location = new Point(player.CurrentLocation.X - 10 + Envir.Random.Next(20), player.CurrentLocation.Y - 10 + Envir.Random.Next(20));

                    if (!ValidPoint(location)) continue;

                    SpellObject lightning = new SpellObject
                    {
                        Spell = Spell.MapLava,
                        Value = Envir.Random.Next(Info.FireDamage),
                        ExpireTime = Envir.Time + (1000),
                        TickSpeed = 500,
                        Caster = null,
                        CurrentLocation = location,
                        CurrentMap = this,
                        Direction = MirDirection.Up
                    };
                    AddObject(lightning);
                    lightning.Spawned();
                }
            }

            for (int i = 0; i < ActionList.Count; i++)
            {
                if (Envir.Time < ActionList[i].Time) continue;
                Process(ActionList[i]);
                ActionList.RemoveAt(i);
            }

            var next = NextRespawnCheckTime;
            if (Info.Lightning && LightningTime + 1 < next) next = LightningTime + 1;
            if (Info.Fire && FireTime + 1 < next) next = FireTime + 1;
            for (int i = 0; i < ActionList.Count; i++)
                if (ActionList[i].Time < next) next = ActionList[i].Time;
            for (int i = 0; i < Doors.Count; i++)
                if (Doors[i].DoorState == DoorState.Open && Doors[i].LastTick + 5001 < next) next = Doors[i].LastTick + 5001;
            NextProcessTime = next;
            ActionCountAfterProcess = ActionList.Count;
        }

        /// <summary>
        /// Earliest time Process has timed work: respawn/door check, lightning, fire, or a queued map action (roadmap P9). The game
        /// loop runs the map then, or earlier when <see cref="MapActionList.Add"/> wakes it (actions are removed only here).
        /// </summary>
        internal long NextProcessTime;
        internal int ActionCountAfterProcess;

        /// <summary>Old-walk loop (DueScheduler off), game thread after the mob barrier: true when Process has work this iteration.</summary>
        internal bool ProcessDue(long time) => time >= NextProcessTime || ActionList.Count != ActionCountAfterProcess;

        // Map scheduler state (Envir.RunMaps, game thread): the live heap entry's due, the world run that scheduled this map, and
        // whether a wake is already queued (set by MapActionList.Add from any thread).
        internal long ScheduledDue = long.MaxValue;
        internal int ScheduledGeneration;
        internal int WakePending;

        /// <summary>Respawn check interval of an empty, sleeping map (EmptyMapSleep).</summary>
        private const int EmptyMapSleepMs = 60000;

        /// <summary>True while this map checks respawns at the slow empty-map interval (game thread).</summary>
        internal bool RespawnsAsleep;

        /// <summary>Maps that never sleep: tick-counter respawns (saved RespawnTick) and conquest maps.</summary>
        private bool RespawnSleepExempt()
        {
            if (Conquest.Count > 0) return true;
            for (int i = 0; i < Respawns.Count; i++)
                if (Respawns[i].Info.RespawnTicks != 0) return true;
            return false;
        }

        /// <summary>A player or hero arrived on a sleeping map: run the respawn check on the map's next Process (game thread).</summary>
        private void WakeRespawns()
        {
            RespawnsAsleep = false;
            NextRespawnCheckTime = 0;
            NextProcessTime = 0;
            Envir.Main.WakeMap(this);
        }

        /// <summary>
        /// Brings a sleeping map's respawns up to date now, so <see cref="MonsterCount"/> is right. Call before reading
        /// MonsterCount from scripts (game thread).
        /// </summary>
        public void CatchUpRespawns()
        {
            if (!RespawnsAsleep) return;
            ProcessRespawns();
        }

        private void ProcessRespawns()
        {
            bool Success = true;
            for (int i = 0; i < Respawns.Count; i++)
            {
                MapRespawn respawn = Respawns[i];
                if ((respawn.Info.RespawnTicks != 0) && (Envir.RespawnTick.CurrentTickcounter < respawn.NextSpawnTick)) continue;
                if ((respawn.Info.RespawnTicks == 0) && (Envir.Time < respawn.RespawnTime)) continue;

                if (respawn.Count < (respawn.Info.Count * Envir.SpawnMultiplier))
                {
                    int count = (respawn.Info.Count * Envir.SpawnMultiplier) - respawn.Count;

                    for (int c = 0; c < count; c++)
                        Success = respawn.Spawn();
                }
                if (Success)
                {
                    respawn.ErrorCount = 0;
                    long delay = Math.Max(1, respawn.Info.Delay - respawn.Info.RandomDelay + Envir.Random.Next(respawn.Info.RandomDelay * 2));
                    respawn.RespawnTime = Envir.Time + (delay * Settings.Minute);
                    if (respawn.Info.RespawnTicks != 0)
                    {
                        respawn.NextSpawnTick = Envir.RespawnTick.CurrentTickcounter + (ulong)respawn.Info.RespawnTicks;
                        if (respawn.NextSpawnTick > long.MaxValue)//since nextspawntick is ulong this simple thing allows an easy way of preventing the counter from overflowing
                            respawn.NextSpawnTick -= long.MaxValue;
                    }
                }
                else
                {
                    respawn.RespawnTime = Envir.Time + 1 * Settings.Minute; // each time it fails to spawn, give it a 1 minute cooldown
                    if (respawn.ErrorCount < 5)
                        respawn.ErrorCount++;
                    else
                    {
                        if (respawn.ErrorCount == 5)
                        {
                            respawn.ErrorCount++;

                            Logger.GetLogger(LogType.Spawn).Info($"Failed to spawn: " +
                                $"mapindex: {respawn.Map.Info.Index}, " +
                                $"mob info: index: {respawn.Info.MonsterIndex}, " +
                                $"name: {respawn.Monster.Name}, " +
                                $"spawncoords ({respawn.Info.Location.X}:{respawn.Info.Location.Y}), " +
                                $"range {respawn.Info.Spread}");
                        }

                    }
                }
            }
        }

        public void Process(DelayedAction action)
        {
            switch (action.Type)
            {
                case DelayedType.Magic:
                    CompleteMagic(action.Params);
                    break;
                case DelayedType.Spawn:
                    MapObject obj = (MapObject)action.Params[0];

                    switch(obj.Race)
                    {
                        case ObjectType.Monster:
                            {
                                MonsterObject mob = (MonsterObject)action.Params[0];
                                mob.Spawn(this, (Point)action.Params[1]);
                                if (action.Params.Length > 2) ((MonsterObject)action.Params[2]).SlaveList.Add(mob);
                            }
                            break;
                        case ObjectType.Spell:
                            {
                                SpellObject spell = (SpellObject)action.Params[0];
                                AddObject(spell);
                                spell.Spawned();
                            }
                            break;
                    }
                    break;
            }
        }

         /**
         * return the coordinates of effect coordinates within an n x n square (n should be odd number. i.e. 3x3, 5x5, 7x7)
         * then use GetCell() in Map.cs to retrive real objects
         * default 3x3
         */
        public static List<Point> GetPointsInEffectiveSquare(Point location, int mapWidth, int mapHeight, int squareEdgeLength = 3)
        {
            var pointsWithinTheMap = new List<Point>();
            int fallBackSpread = 1;
            int spread;
            if (squareEdgeLength > 1)
            {
                spread = (int)((squareEdgeLength - 1) / 2);
            } else
            {
                spread = fallBackSpread; // 3x3
            }

            for (int y = location.Y - spread; y <= location.Y + spread; y++)
            {
                if (y < 0) continue;
                if (y >= mapHeight) break;

                for (int x = location.X - spread; x <= location.X + spread; x++)
                {
                    if (x < 0) continue;
                    if (x >= mapWidth) break;
                    pointsWithinTheMap.Add(new Point(x, y));
                }
            }
            return pointsWithinTheMap;
        }

        private void CompleteMagic(IList<object> data)
        {
            bool train = false;
            HumanObject player = (HumanObject)data[0];
            UserMagic magic = (UserMagic)data[1];

            if (player == null || player.Info == null || player.Node == null) return;

            int value, value2;
            Point location;
            Cell cell;
            MirDirection dir;
            MonsterObject monster;
            Point front;
            switch (magic.Spell)
            {

                #region HellFire

                case Spell.HellFire:
                    value = (int)data[2];
                    dir = (MirDirection)data[4];
                    location = Functions.PointMove((Point)data[3], dir, 1);
                    int count = (int)data[5] - 1;

                    if (!ValidPoint(location)) return;

                    if (count > 0)
                    {
                        DelayedAction action = new DelayedAction(DelayedType.Magic, Envir.Time + 100, player, magic, value, location, dir, count);
                        ActionList.Add(action);
                    }

                    cell = GetCell(location);

                    if (cell.Objects == null) return;

                    for (int i = 0; i < cell.Objects.Count; i++)
                    {
                        MapObject target = cell.Objects[i];
                        switch (target.Race)
                        {
                            case ObjectType.Monster:
                            case ObjectType.Player:
                                //Only targets
                                if (target.IsAttackTarget(player))
                                {
                                    if (target.Attacked(player, value, DefenceType.MAC, false) > 0)
                                        train = true;
                                    break;
                                }
                                break;
                        }
                    }
                    break;

                #endregion

                #region SummonSkeleton, SummonShinsu, SummonHolyDeva, ArcherSummons

                case Spell.SummonSkeleton:
                case Spell.SummonShinsu:
                case Spell.SummonHolyDeva:
                case Spell.SummonVampire:
                case Spell.SummonToad:
                case Spell.SummonSnakes:
                    monster = (MonsterObject)data[2];
                    front = (Point)data[3];

                    if (monster.Master.Dead) return;

                    if (ValidPoint(front))
                        monster.Spawn(this, front);
                    else
                        monster.Spawn(player.CurrentMap, player.CurrentLocation);

                    monster.Master.Pets.Add(monster);
                    break;
                case Spell.Stonetrap:
                    monster = (MonsterObject)data[2];
                    front = (Point)data[3];

                    if (monster.Master.Dead) return;

                    if (ValidPoint(front))
                        monster.Spawn(this, front);
                    else
                        monster.Spawn(player.CurrentMap, player.CurrentLocation);

                    monster.Master.Pets.Add(monster);
                    break;
                #endregion

                #region FireBang, IceStorm

                case Spell.IceStorm:
                case Spell.FireBang:
                    value = (int)data[2];
                    location = (Point)data[3];

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (target.IsAttackTarget(player))
                                        {
                                            if (target.Attacked(player, value, DefenceType.MAC, false) > 0)
                                                train = true;
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region MassHiding

                case Spell.MassHiding:
                    value = (int)data[2];
                    location = (Point)data[3];

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];

                                if (target.Node == null) continue;

                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (target.IsFriendlyTarget(player))
                                        {
                                            target.AddBuff(BuffType.Hiding, player, (Settings.Second * value), new Stats());
                                            target.OperateTime = 0;
                                            train = true;
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region SoulShield, BlessedArmour

                case Spell.SoulShield:
                case Spell.BlessedArmour:
                    value = (int)data[2];
                    location = (Point)data[3];
                    BuffType type = magic.Spell == Spell.SoulShield ? BuffType.SoulShield : BuffType.BlessedArmour;

                    for (int y = location.Y - 3; y <= location.Y + 3; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 3; x <= location.X + 3; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];

                                if (target.Node == null) continue;

                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                    case ObjectType.Hero:
                                        //Only targets
                                        if (target.IsFriendlyTarget(player))
                                        {
                                            var stats = new Stats
                                            {
                                                [type == BuffType.SoulShield ? Stat.MaxMAC : Stat.MaxAC] = target.Level / 7 + 4
                                            };

                                            target.AddBuff(type, player, Settings.Second * value, stats);
                                            target.OperateTime = 0;
                                            train = true;
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region FireWall

                case Spell.FireWall:
                    value = (int)data[2];
                    location = (Point)data[3];

                    int castId = 0;
                    if (data.Count >= 5 && data[4] is int ci) castId = ci;

                    player.LevelMagic(magic);

                    if (ValidPoint(location))
                    {
                        cell = GetCell(location);

                        bool cast = true;
                        if (cell.Objects != null)
                            for (int o = 0; o < cell.Objects.Count; o++)
                            {
                                MapObject target = cell.Objects[o];
                                if (target.Race != ObjectType.Spell || ((SpellObject)target).Spell != Spell.FireWall) continue;

                                cast = false;
                                break;
                            }

                        if (cast)
                        {
                            SpellObject ob = new SpellObject
                            {
                                Spell = Spell.FireWall,
                                Value = value,
                                ExpireTime = Envir.Time + (10 + value / 2) * 1000,
                                TickSpeed = 2000,
                                Caster = player,
                                CurrentLocation = location,
                                CurrentMap = this,
                                CastInstanceId = castId
                            };
                            AddObject(ob);
                            ob.Spawned();
                        }
                    }

                    dir = MirDirection.Up;
                    for (int i = 0; i < 4; i++)
                    {
                        location = Functions.PointMove((Point)data[3], dir, 1);
                        dir += 2;

                        if (!ValidPoint(location)) continue;

                        cell = GetCell(location);
                        bool cast = true;

                        if (cell.Objects != null)
                            for (int o = 0; o < cell.Objects.Count; o++)
                            {
                                MapObject target = cell.Objects[o];
                                if (target.Race != ObjectType.Spell || ((SpellObject)target).Spell != Spell.FireWall) continue;

                                cast = false;
                                break;
                            }

                        if (!cast) continue;

                        SpellObject ob = new SpellObject
                        {
                            Spell = Spell.FireWall,
                            Value = value,
                            ExpireTime = Envir.Time + (10 + value / 2) * 1000,
                            TickSpeed = 2000,
                            Caster = player,
                            CurrentLocation = location,
                            CurrentMap = this,
                            CastInstanceId = castId
                        };
                        AddObject(ob);
                        ob.Spawned();
                    }

                    break;

                #endregion

                #region Lightning

                case Spell.Lightning:
                    value = (int)data[2];
                    location = (Point)data[3];
                    dir = (MirDirection)data[4];

                    for (int i = 0; i < 6; i++)
                    {
                        location = Functions.PointMove(location, dir, 1);

                        if (!ValidPoint(location)) continue;

                        cell = GetCell(location);

                        if (cell.Objects == null) continue;

                        for (int o = 0; o < cell.Objects.Count; o++)
                        {
                            MapObject target = cell.Objects[o];
                            if (target.Race != ObjectType.Player && target.Race != ObjectType.Monster) continue;

                            if (!target.IsAttackTarget(player)) continue;
                            if (target.Attacked(player, value, DefenceType.MAC, false) > 0)
                                train = true;
                            break;
                        }
                    }

                    break;

                #endregion

                #region HeavenlySword

                case Spell.HeavenlySword:
                    value = (int)data[2];
                    location = (Point)data[3];
                    dir = (MirDirection)data[4];

                    for (int i = 0; i < 3; i++)
                    {
                        location = Functions.PointMove(location, dir, 1);

                        if (!ValidPoint(location)) continue;

                        cell = GetCell(location);

                        if (cell.Objects == null) continue;

                        for (int o = 0; o < cell.Objects.Count; o++)
                        {
                            MapObject target = cell.Objects[o];
                            if (target.Race != ObjectType.Player && target.Race != ObjectType.Monster) continue;

                            if (!target.IsAttackTarget(player)) continue;
                            if (target.Attacked(player, value, DefenceType.MAC, false) > 0)
                                train = true;
                            break;
                        }
                    }

                    break;

                #endregion

                #region MassHealing

                case Spell.MassHealing:
                    value = (int)data[2];
                    location = (Point)data[3];

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                    case ObjectType.Hero:
                                        //Only targets
                                        if (target.IsFriendlyTarget(player))
                                        {
                                            if (target.Health >= target.MaxHealth) continue;
                                            target.HealAmount = (ushort)Math.Min(ushort.MaxValue, target.HealAmount + value);
                                            target.OperateTime = 0;
                                            train = true;
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region ThunderStorm

                case Spell.ThunderStorm:
                case Spell.FlameField:
                case Spell.NapalmShot:
                case Spell.StormEscape:
                    value = (int)data[2];
                    location = (Point)data[3];
                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (!target.IsAttackTarget(player)) break;
                                        target.Attacked(player, magic.Spell == Spell.ThunderStorm && !target.Undead ? value / 10 : value, DefenceType.MAC, false);
                                        train = true;
                                        break;
                                }
                            }

                        }
                    }

                    break;

                #endregion

                #region MoonMist

                case Spell.MoonMist:

                    value = (int)data[2];
                    location = (Point)data[3];
                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (!target.IsAttackTarget(player)) break;

                                        if (target.Attacked(player, magic.Spell == Spell.ThunderStorm && !target.Undead ? value / 10 : value, DefenceType.AC, false) <= 0)
                                        {
                                            if (target.Undead)
                                            {
                                                target.ApplyPoison(new Poison { PType = PoisonType.Stun, Duration = magic.Level + 2, TickSpeed = 1000 }, player);
                                            }
                                            break;
                                        }

                                        train = true;
                                        break;
                                }
                            }

                        }
                    }

                    break;

                #endregion

                #region LionRoar

                case Spell.LionRoar:
                    location = (Point)data[2];

                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                if (target.Race != ObjectType.Monster) continue;
                                //Only targets
                                if (!target.IsAttackTarget(player) || player.Level + 3 < target.Level) continue;
                                target.ApplyPoison(new Poison { PType = PoisonType.LRParalysis, Duration = magic.Level + 2, TickSpeed = 1000 }, player);
                                target.OperateTime = 0;
                                train = true;
                            }

                        }

                    }

                    break;

                #endregion

                #region PoisonCloud

                case Spell.PoisonCloud:
                    value = (int)data[2];
                    location = (Point)data[3];
                    byte bonusdmg = (byte)data[4];
                    train = true;
                    bool show = true;

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid) continue;

                            bool cast = true;
                            if (cell.Objects != null)
                                for (int o = 0; o < cell.Objects.Count; o++)
                                {
                                    MapObject target = cell.Objects[o];
                                    if (target.Race != ObjectType.Spell || ((SpellObject)target).Spell != Spell.PoisonCloud) continue;

                                    cast = false;
                                    break;
                                }

                            if (!cast) continue;

                            SpellObject ob = new SpellObject
                                {
                                    Spell = Spell.PoisonCloud,
                                    Value = value,
                                    BonusDmg = bonusdmg,
                                    ExpireTime = Envir.Time + 6000,
                                    TickSpeed = 1000,
                                    Caster = player,
                                    CurrentLocation = new Point(x, y),
                                    CastLocation = location,
                                    Show = show,
                                    CurrentMap = this,
                                };

                            show = false;

                            AddObject(ob);
                            ob.Spawned();
                        }
                    } 

                    break;

                #endregion

                #region IceThrust

                case Spell.IceThrust:
                    {
                        location = (Point)data[2];
                        MirDirection direction = (MirDirection)data[3];

                        int nearDamage = (int)data[4];
                        int farDamage = (int)data[5];

                        int col = 3;
                        int row = 3;

                        Point[] loc = new Point[col]; //0 = left 1 = center 2 = right
                        loc[0] = Functions.PointMove(location, Functions.PreviousDir(direction), 1);
                        loc[1] = Functions.PointMove(location, direction, 1);
                        loc[2] = Functions.PointMove(location, Functions.NextDir(direction), 1);

                        for (int i = 0; i < col; i++)
                        {
                            Point startPoint = loc[i];
                            for (int j = 0; j < row; j++)
                            {
                                Point hitPoint = Functions.PointMove(startPoint, direction, j);

                                if (!ValidPoint(hitPoint)) continue;

                                cell = GetCell(hitPoint);

                                if (cell.Objects == null) continue;

                                for (int k = 0; k < cell.Objects.Count; k++)
                                {
                                    MapObject target = cell.Objects[k];
                                    switch (target.Race)
                                    {
                                        case ObjectType.Monster:
                                        case ObjectType.Player:
                                            if (target.IsAttackTarget(player))
                                            {
                                                //Only targets
                                                if (target.Attacked(player, j <= 1 ? nearDamage : farDamage, DefenceType.MAC, false) > 0)
                                                {
                                                    if (player.Level + (target.Race == ObjectType.Player ? 2 : 10) >= target.Level && Envir.Random.Next(target.Race == ObjectType.Player ? 100 : 20) <= magic.Level)
                                                    {
                                                        target.ApplyPoison(new Poison
                                                        {
                                                            Owner = player,
                                                            Duration = target.Race == ObjectType.Player ? 4 : 5 + Envir.Random.Next(5),
                                                            PType = PoisonType.Slow,
                                                            TickSpeed = 1000,
                                                        }, player);
                                                        target.OperateTime = 0;
                                                    }

                                                    if (player.Level + (target.Race == ObjectType.Player ? 2 : 10) >= target.Level && Envir.Random.Next(target.Race == ObjectType.Player ? 100 : 40) <= magic.Level)
                                                    {
                                                        target.ApplyPoison(new Poison
                                                        {
                                                            Owner = player,
                                                            Duration = target.Race == ObjectType.Player ? 2 : 5 + Envir.Random.Next(player.Stats[Stat.Freezing]),
                                                            PType = PoisonType.Frozen,
                                                            TickSpeed = 1000,
                                                        }, player);
                                                        target.OperateTime = 0;
                                                    }

                                                    train = true;
                                                }
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                    }

                    break;

                #endregion

                #region SlashingBurst

                case Spell.SlashingBurst:
                    value = (int)data[2];
                    location = (Point)data[3];
                    dir = (MirDirection)data[4];
                    count = (int)data[5];

                    for (int i = 0; i < count; i++)
                    {
                        location = Functions.PointMove(location, dir, 1);

                        if (!ValidPoint(location)) continue;

                        cell = GetCell(location);

                        if (cell.Objects == null) continue;

                        for (int o = 0; o < cell.Objects.Count; o++)
                        {
                            MapObject target = cell.Objects[o];
                            if (target.Race != ObjectType.Player && target.Race != ObjectType.Monster) continue;

                            if (!target.IsAttackTarget(player)) continue;
                            if (target.Attacked(player, value, DefenceType.AC, false) > 0)
                                train = true;
                            break;
                        }
                    }
                    break;

                #endregion

                #region Mirroring

                case Spell.Mirroring:
                    monster = (MonsterObject)data[2];
                    front = (Point)data[3];
                    bool finish = (bool)data[4];

                    if (finish)
                    {
                        monster.Die();
                        return;
                    };

                    if (ValidPoint(front))
                        monster.Spawn(this, front);
                    else
                        monster.Spawn(player.CurrentMap, player.CurrentLocation);
                    break;

                #endregion

                #region Blizzard

                case Spell.Blizzard:
                    value = (int)data[2];
                    location = (Point)data[3];

                    train = true;
                    show = true;

                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid) continue;

                            bool cast = true;
                            if (cell.Objects != null)
                                for (int o = 0; o < cell.Objects.Count; o++)
                                {
                                    MapObject target = cell.Objects[o];
                                    if (target.Race != ObjectType.Spell || ((SpellObject) target).Spell != Spell.Blizzard) continue;

                                    cast = false;
                                    break;
                                }

                            if (!cast) continue;

                            SpellObject ob = new SpellObject
                                {
                                    Spell = Spell.Blizzard,
                                    Value = value,
                                    ExpireTime = Envir.Time + 3000,
                                    TickSpeed = 440,
                                    Caster = player,
                                    CurrentLocation = new Point(x, y),
                                    CastLocation = location,
                                    Show = show,
                                    CurrentMap = this,
                                    StartTime = Envir.Time + 800,
                                };

                            show = false;

                            AddObject(ob);
                            ob.Spawned();
                        }
                    } 

                    break;

                #endregion

                #region MeteorStrike

                case Spell.MeteorStrike:
                    value = (int)data[2];
                    location = (Point)data[3];

                    train = true;
                    show = true;

                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid) continue;

                            bool cast = true;
                            if (cell.Objects != null)
                                for (int o = 0; o < cell.Objects.Count; o++)
                                {
                                    MapObject target = cell.Objects[o];
                                    if (target.Race != ObjectType.Spell || ((SpellObject)target).Spell != Spell.MeteorStrike) continue;

                                    cast = false;
                                    break;
                                }

                            if (!cast) continue;

                            SpellObject ob = new SpellObject
                            {
                                Spell = Spell.MeteorStrike,
                                Value = value,
                                ExpireTime = Envir.Time + 3000,
                                TickSpeed = 440,
                                Caster = player,
                                CurrentLocation = new Point(x, y),
                                CastLocation = location,
                                Show = show,
                                CurrentMap = this,
                                StartTime = Envir.Time + 800,
                            };

                            show = false;

                            AddObject(ob);
                            ob.Spawned();
                        }
                    }

                    break;

                #endregion

                #region TrapHexagon

                case Spell.TrapHexagon:
                    value = (int)data[2];
                    location = (Point)data[3];

                    MonsterObject centerTarget = null;

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];

                                if (y == location.Y && x == location.X && target.Race == ObjectType.Monster)
                                {
                                    centerTarget = (MonsterObject)target;
                                }
                                
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                        if (target == null || !target.IsAttackTarget(player) || target.Node == null || target.Level > player.Level + 2) continue;

                                        MonsterObject mobTarget = (MonsterObject)target;

                                        if (centerTarget == null) centerTarget = mobTarget;

                                        mobTarget.ShockTime = Envir.Time + value;
                                        mobTarget.Target = null;
                                        break;
                                }
                            }

                        }
                    }

                    if (centerTarget == null) return;

                    for (byte i = 0; i < 8; i += 2)
                    {
                        Point startpoint = Functions.PointMove(location, (MirDirection)i, 2);
                        for (byte j = 0; j <= 4; j += 4)
                        {
                            MirDirection spawndirection = i == 0 || i == 4 ? MirDirection.Right : MirDirection.Up;
                            Point spawnpoint = Functions.PointMove(startpoint, spawndirection + j, 1);
                            if (spawnpoint.X <= 0 || spawnpoint.X > centerTarget.CurrentMap.Width) continue;
                            if (spawnpoint.Y <= 0 || spawnpoint.Y > centerTarget.CurrentMap.Height) continue;
                            SpellObject ob = new SpellObject
                            {
                                Spell = Spell.TrapHexagon,
                                ExpireTime = Envir.Time + value,
                                TickSpeed = 100,
                                Caster = player,
                                CurrentLocation = spawnpoint,
                                CastLocation = location,
                                CurrentMap = centerTarget.CurrentMap,
                                Target = centerTarget,
                            };

                            centerTarget.CurrentMap.AddObject(ob);
                            ob.Spawned();
                        }
                    }

                    train = true;

                    break;

                #endregion

                #region Curse

                case Spell.Curse:
                    value = (int)data[2];
                    location = (Point)data[3];
                    value2 = (int)data[4];

                    for (int y = location.Y - 3; y <= location.Y + 3; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 3; x <= location.X + 3; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];

                                if (target.Node == null) continue;

                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:

                                        if (Envir.Random.Next(10) >= 4) continue;

                                        //Only targets
                                        if (target.IsAttackTarget(player))
                                        {
                                            target.ApplyPoison(new Poison { PType = PoisonType.Slow, Duration = value, TickSpeed = 1000, Value = value2, Owner = player }, player);

                                            var stats = new Stats
                                            {
                                                [Stat.MaxDCRatePercent] = value2 * -1,
                                                [Stat.MaxMCRatePercent] = value2 * -1,
                                                [Stat.MaxSCRatePercent] = value2 * -1,
                                                [Stat.AttackSpeedRatePercent] = target.Race == ObjectType.Player ? value2 * -1 : 0
                                            };

                                            target.AddBuff(BuffType.Curse, player, Settings.Second * value, stats);
                                            target.OperateTime = 0;
                                            train = true;
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region ExplosiveTrap

                case Spell.ExplosiveTrap:
                    value = (int)data[2];
                    front = (Point)data[3];
                    int trapID = (int)data[4];

                    if (ValidPoint(front))
                    {
                        cell = GetCell(front);

                        bool cast = true;
                        if (cell.Objects != null)
                            for (int o = 0; o < cell.Objects.Count; o++)
                            {
                                MapObject target = cell.Objects[o];
                                if (target.Race != ObjectType.Spell || (((SpellObject)target).Spell != Spell.FireWall && ((SpellObject)target).Spell != Spell.ExplosiveTrap)) continue;

                                cast = false;
                                break;
                            }

                        if (cast)
                        {
                            player.LevelMagic(magic);

                            System.Drawing.Point[] traps = new Point[3];
                            traps[0] = front;
                            traps[1] = Functions.Left(front, player.Direction);
                            traps[2] = Functions.Right(front, player.Direction);

                            for (int i = 0; i <= 2; i++)
                            {
                                bool skip = false;
                                //for the extra spots make sure to not overlap new traps with old ones since it creates invisible double/tripple /... trap spots
                                if (i > 0)
                                {
                                    cell = GetCell(traps[i]);

                                    if (cell.Objects != null)
                                        for (int o = 0; o < cell.Objects.Count; o++)
                                        {
                                            MapObject target = cell.Objects[o];
                                            if (target.Race != ObjectType.Spell || (((SpellObject)target).Spell != Spell.FireWall && ((SpellObject)target).Spell != Spell.ExplosiveTrap)) continue;

                                            skip = true;
                                            break;
                                        }
                                }
                                if (skip) continue;
                                SpellObject ob = new SpellObject
                                {
                                    Spell = Spell.ExplosiveTrap,
                                    Value = value,
                                    ExpireTime = Envir.Time + (10 + value / 2) * 1000,
                                    TickSpeed = 500,
                                    Caster = player,
                                    CurrentLocation = traps[i],
                                    CurrentMap = this,
                                    ExplosiveTrapID = trapID,
                                    ExplosiveTrapCount = i
                                };

                                AddObject(ob);
                                ob.Spawned();
                            }
                        }
                    }
                    break;

                #endregion

                #region Plague

                case Spell.Plague:
                    // DelayedAction action = new DelayedAction(DelayedType.Magic, Envir.Time + delay, this, magic, magic.GetDamage(GetAttackPower(Stats[Stat.MinSC], Stats[Stat.MaxSC])), location, pType);
                    // (int)data[2] === magic.GetDamage(GetAttackPower(Stats[Stat.MinSC], Stats[Stat.MaxSC]))
                    // (Point)data[3] === location
                    // (PoisonType)data[4] === pType
                    value = (int)data[2];
                    location = (Point)data[3];

                    // the skill affect a 3x3 square
                    var points= GetPointsInEffectiveSquare(location, Width, Height, 3);
                    foreach (var point in points)
                    {
                        cell = GetCell(point.X, point.Y);

                        if (!cell.Valid || cell.Objects == null) continue;

                        for (int i = 0; i < cell.Objects.Count; i++)
                        {
                            MapObject target = cell.Objects[i];
                            switch (target.Race)
                            {
                                case ObjectType.Monster:
                                case ObjectType.Player:
                                    //Only targets
                                    if (target.IsAttackTarget(player))
                                    {

                                        int chance = Envir.Random.Next(15);
                                        PoisonType poison;
                                        if (new int[] { 0, 1, 2 }.Contains(chance)) //3 in 15 chances it'll slow
                                            poison = PoisonType.Slow;
                                        else if (new int[] { 3, 4 }.Contains(chance)) //2 in 15 chances it'll freeze
                                            poison = PoisonType.Frozen;
                                        else if (new int[] { 5, 6, 7, 8, 9 }.Contains(chance)) //5 in 15 chances it'll red/green
                                            // (whatever type of poison player is holding)
                                            poison = (PoisonType)data[4];
                                        else //5 in 15 chances it'll do nothing
                                            poison = PoisonType.None;

                                        int tempValue = 0;

                                        if (poison == PoisonType.Red)
                                        {
                                            tempValue = value / 15 + magic.Level + 1;
                                        }
                                        else
                                        {
                                            tempValue = value + (magic.Level + 1) * 2;
                                        }

                                        if (poison != PoisonType.None)
                                        {
                                            target.ApplyPoison(new Poison { PType = poison, Duration = (2 * (magic.Level + 1)) + (value / 10), TickSpeed = 1000, Value = tempValue, Owner = player }, player, false, true);
                                        }

                                        if (target.Race == ObjectType.Player)
                                        {
                                            PlayerObject tempOb = (PlayerObject)target;

                                            tempOb.ChangeMP(-tempValue);
                                        }

                                        target.Attacked(player, player.Stats[Stat.MaxSC] * 2, DefenceType.MAC, true);

                                        train = true;
                                    }
                                    break;
                            }
                        }
                    }
                    break;

                #endregion

                #region Trap

                case Spell.Trap:
                    value = (int)data[2];
                    //location = (Point)data[3];
                    MapObject originalTarget = (MapObject)data[3];
                    location = originalTarget.CurrentLocation;
                    MonsterObject selectTarget = null;

                    if (!ValidPoint(location)) break;

                    cell = GetCell(location);

                    if (!cell.Valid || cell.Objects == null) break;

                    for (int i = 0; i < cell.Objects.Count; i++)
                    {
                        MapObject target = cell.Objects[i];
                        if (target.Race == ObjectType.Monster)
                        {
                            selectTarget = (MonsterObject)target;

                            if (selectTarget == null || !selectTarget.IsAttackTarget(player) || selectTarget.Node == null || selectTarget.Level >= player.Level + 2) continue;
                            selectTarget.ShockTime = Envir.Time + value;
                            selectTarget.Target = null;
                            break;
                        }
                    }

                    if (selectTarget == null) return;

                    if (location.X <= 0 || location.X > selectTarget.CurrentMap.Width) break;
                    if (location.Y <= 0 || location.Y > selectTarget.CurrentMap.Height) break;
                    SpellObject spellOb = new SpellObject
                    {
                        Spell = Spell.Trap,
                        ExpireTime = Envir.Time + value,
                        TickSpeed = 100,
                        Caster = player,
                        CurrentLocation = location,
                        CastLocation = location,
                        CurrentMap = selectTarget.CurrentMap,
                        Target = selectTarget,
                    };

                    selectTarget.CurrentMap.AddObject(spellOb);
                    spellOb.Spawned();

                    train = true;
                    break;

                #endregion

                #region OneWithNature

                case Spell.OneWithNature:
                    value = (int)data[2];
                    location = (Point)data[3];

                    bool hasVampBuff = (player.Buffs.Any(ex => ex.Type == BuffType.VampireShot));
                    bool hasPoisonBuff = (player.Buffs.Any(ex => ex.Type == BuffType.PoisonShot));

                    for (int y = location.Y - 2; y <= location.Y + 2; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 2; x <= location.X + 2; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (!target.IsAttackTarget(player) || target.Dead) break;

                                        //knockback
                                        //int distance = 1 + Math.Max(0, magic.Level - 1) + Envir.Random.Next(2);
                                        //dir = Functions.DirectionFromPoint(location, target.CurrentLocation);
                                        //if(target.Level < player.Level)
                                        //    target.Pushed(player, dir, distance);// <--crashes server somehow?

                                        if (target.Attacked(player, value, DefenceType.MAC, false) <= 0) break;

                                        if (hasVampBuff)//Vampire Effect
                                        {
                                            if (player.VampAmount == 0) player.VampTime = Envir.Time + 1000;
                                            player.VampAmount += (ushort)(value * (magic.Level + 1) * 0.25F);
                                        }
                                        if (hasPoisonBuff)//Poison Effect
                                        {
                                            target.ApplyPoison(new Poison
                                            {
                                                Duration = (value * 2) + (magic.Level + 1) * 7,
                                                Owner = player,
                                                PType = PoisonType.Green,
                                                TickSpeed = 2000,
                                                Value = value / 15 + magic.Level + 1 + Envir.Random.Next(player.Stats[Stat.PoisonAttack])
                                            }, player);
                                            target.OperateTime = 0;
                                        }
                                        train = true;
                                        break;
                                }
                            }

                        }
                    }

                    if (hasVampBuff)
                    {
                        //Expire
                        player.AddBuff(BuffType.VampireShot, player, Settings.Second * 1, new Stats());
                    }
                    if (hasPoisonBuff)
                    {
                        //Expire
                        player.AddBuff(BuffType.PoisonShot, player, Settings.Second * 1, new Stats());
                    }
                    break;

                #endregion

                #region Portal

                case Spell.Portal:                  
                    value = (int)data[2];
                    location = (Point)data[3];
                    value2 = (int)data[4];

                    spellOb = new SpellObject
                    {
                        Spell = Spell.Portal,
                        Value = value2,
                        ExpireTime = Envir.Time + value * 1000,
                        TickSpeed = 2000,
                        Caster = player,
                        CurrentLocation = location,
                        CurrentMap = this,
                    };

                    AddObject(spellOb);
                    spellOb.Spawned();
                    train = true;
                    break;

                #endregion

                #region DelayedExplosion

                case Spell.DelayedExplosion:
                    value = (int)data[2];
                    location = (Point)data[3];

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid || cell.Objects == null) continue;

                            for (int i = 0; i < cell.Objects.Count; i++)
                            {
                                MapObject target = cell.Objects[i];
                                switch (target.Race)
                                {
                                    case ObjectType.Monster:
                                    case ObjectType.Player:
                                        //Only targets
                                        if (target.IsAttackTarget(player))
                                        {
                                            if (target.Attacked(player, value, DefenceType.MAC, false) > 0)
                                                train = false;//wouldnt want to make the skill give twice the points
                                        }
                                        break;
                                }
                            }

                        }

                    }

                    break;

                #endregion

                #region BattleCry
                case Spell.BattleCry:
                    location = (Point)data[2];

                    int startX = Math.Max(location.X - 2, 0);
                    int endX = Math.Min(location.X + 2, Width - 1);
                    int startY = Math.Max(location.Y - 2, 0);
                    int endY = Math.Min(location.Y + 2, Height - 1);

                    int randomValue = Envir.Random.Next(100);

                    for (int y = startY; y <= endY; y++)
                    {
                        for (int x = startX; x <= endX; x++)
                        {
                            cell = GetCell(x, y);
                            if (!cell.Valid || cell.Objects == null || cell.Objects.Count == 0) continue;

                            foreach (var target in cell.Objects)
                            {
                                if (target.Race != ObjectType.Monster) continue;

                                int threshold = magic.Level switch
                                {
                                    0 => 90, // 90% chance of failure (10% success)
                                    1 => 70, // 70% chance of failure (30% success)
                                    2 => 50, // 50% chance of failure (50% success)
                                    3 => 30, // 30% chance of failure (70% success)
                                    _ => 100 // Default case, should not occur
                                };


                                if (randomValue > threshold) continue;


                                if (((MonsterObject)target).Info.CoolEye == 100) continue;
                                target.Target = player;
                                target.OperateTime = 0;

                                train = true;
                            }
                        }
                    }
                    break;

                #endregion
                #region HealingCircle

                case Spell.HealingCircle:
                    value = (int)data[2];
                    location = (Point)data[3];

                    train = true;
                    show = true;

                    for (int y = location.Y - 1; y <= location.Y + 1; y++)
                    {
                        if (y < 0) continue;
                        if (y >= Height) break;

                        for (int x = location.X - 1; x <= location.X + 1; x++)
                        {
                            if (x < 0) continue;
                            if (x >= Width) break;

                            cell = GetCell(x, y);

                            if (!cell.Valid) continue;

                            bool cast = true;
                            if (cell.Objects != null)
                                for (int o = 0; o < cell.Objects.Count; o++)
                                {
                                    MapObject target = cell.Objects[o];
                                    if (target.Race != ObjectType.Spell || ((SpellObject)target).Spell != Spell.HealingCircle) continue;

                                    cast = false;
                                    break;
                                }

                            if (!cast) continue;

                            SpellObject ob = new SpellObject
                            {
                                Spell = Spell.HealingCircle,
                                Value = value,
                                ExpireTime = Envir.Time + (10000) + (5000 * magic.Level),
                                TickSpeed = 400,
                                Caster = player,
                                CurrentLocation = new Point(x, y),
                                CastLocation = location,
                                Show = show,
                                CurrentMap = this,
                            };

                            show = false;

                            AddObject(ob);
                            ob.Spawned();
                        }
                    }

                    break;

                    #endregion
        }

            if (train)
                player.LevelMagic(magic);

        }

        public void AddObject(MapObject ob)
        {
            if (ob.Race == ObjectType.Player)
            {
                Players.Add((PlayerObject)ob);
                Grid?.Add((PlayerObject)ob);
                if (RespawnsAsleep) WakeRespawns();
            }

            if (ob.Race == ObjectType.Merchant) NPCs.Add((NPCObject)ob);
            if (ob.Race == ObjectType.Spell) Spells.Add((SpellObject)ob);
            if (ob.Race == ObjectType.Hero)
            {
                Heroes.Add((HeroObject)ob);
                if (RespawnsAsleep) WakeRespawns();
            }

            // AddObject knows the map and cell, so it swaps in a map-owned copy of a shared wall itself: some callers (hero and
            // monster spawns, login) set CurrentMap only after this call, which Cell.Add's own fallback relies on.
            var location = ob.CurrentLocation;
            var cell = Cells[location.X, location.Y];
            if (cell.IsShared)
                Cells[location.X, location.Y] = cell = cell.CreateOwnCopy();
            cell.Add(ob);
        }

        public void RemoveObject(MapObject ob)
        {
            if (ob.Race == ObjectType.Player)
            {
                Players.Remove((PlayerObject)ob);
                Grid?.Remove((PlayerObject)ob);
            }
            if (ob.Race == ObjectType.Merchant) NPCs.Remove((NPCObject)ob);
            if (ob.Race == ObjectType.Spell) Spells.Remove((SpellObject)ob);
            if (ob.Race == ObjectType.Hero) Heroes.Remove((HeroObject)ob);

            GetCell(ob.CurrentLocation).Remove(ob);
        }


        public SafeZoneInfo GetSafeZone(Point location)
        {
            for (int i = 0; i < Info.SafeZones.Count; i++)
            {
                SafeZoneInfo szi = Info.SafeZones[i];
                if (Functions.InRange(szi.Location, location, szi.Size))
                    return szi;
            }
            return null;
        }

        public List<SpellObject> GetSpellObjects(Spell spell ,MapObject caster)
        {
            List<SpellObject> spellObjects = new List<SpellObject>();

            for (int i = 0; i < Spells.Count; i++)
            {
                if (spell != Spells[i].Spell) continue;
                if (caster != null && Spells[i].Caster != caster) continue;

                spellObjects.Add(Spells[i]);
            }

            return spellObjects;
        }

        public ConquestObject GetConquest(Point location)
        {
            for (int i = 0; i < Conquest.Count; i++)
            {
                ConquestObject swi = Conquest[i];

                if ((swi.Info.FullMap || Functions.InRange(swi.Info.Location, location, swi.Info.Size)) && swi.WarIsOn)
                    return swi;
            }
            return null;
        }

        //public ConquestObject GetInnerConquest(Map map, Point location)
        //{
        //    for (int i = 0; i < Conquest.Count; i++)
        //    {
        //        ConquestObject swi = Conquest[i];
        //        if (map.Info.Index != swi.Info.MapIndex) continue;

        //        if (Functions.InRange(swi.Info.KingLocation, location, swi.Info.KingSize) && swi.WarIsOn)
        //            return swi;
        //    }
        //    return null;
        //}

        public void Broadcast(Packet p, Point location)
        {
            if (p == null) return;

            int sent = 0, scanned = 0;
            foreach (var player in PlayersNear(location, Globals.DataRange))
            {
                scanned++;
                if (Functions.InRange(location, player.CurrentLocation, Globals.DataRange))
                {
                    player.Enqueue(p);
                    sent++;
                }
            }
            LoopProfiler.CountBroadcast(scanned, sent);
        }

        public void BroadcastNPC(Packet p, Point location)
        {
            if (p == null) return;

            int sent = 0, scanned = 0;
            foreach (var player in PlayersNear(location, Globals.DataRange))
            {
                scanned++;
                if (Functions.InRange(location, player.CurrentLocation, Globals.DataRange))
                {
                    player.Enqueue(p);
                    sent++;
                }
            }
            LoopProfiler.CountBroadcast(scanned, sent);
        }


        public void Broadcast(Packet p, Point location, PlayerObject Player)
        {
            if (p == null) return;

            if (Functions.InRange(location, Player.CurrentLocation, Globals.DataRange))
            {
                Player.Enqueue(p);
            }    
        }
    }
    public class Cell
    {
        // Wall cells hold no per-cell state at load, so every map shares one instance per (attribute, fishing value): most of a
        // map's cells are walls. A shared cell turns into a map-owned copy on its first Add (see Add), so nothing a script or
        // spell places on a wall ends up on other maps' walls.
        private static readonly Cell[] SharedLowWalls = CreateSharedWalls(CellAttribute.LowWall);
        private static readonly Cell[] SharedHighWalls = CreateSharedWalls(CellAttribute.HighWall);

        public static Cell LowWall { get { return SharedLowWalls[0]; } }
        public static Cell HighWall { get { return SharedHighWalls[0]; } }

        /// <summary>The shared wall cell for <paramref name="attribute"/> with <paramref name="fishingAttribute"/> (-1 = none).</summary>
        public static Cell SharedWall(CellAttribute attribute, sbyte fishingAttribute)
        {
            var walls = attribute == CellAttribute.LowWall ? SharedLowWalls : SharedHighWalls;
            var i = fishingAttribute + 1;
            return i >= 0 && i < walls.Length ? walls[i] : new Cell { Attribute = attribute, FishingAttribute = fishingAttribute };
        }

        private static Cell[] CreateSharedWalls(CellAttribute attribute)
        {
            // Fishing values come from map light bytes 100-119, so -1..19.
            var walls = new Cell[21];
            for (int i = 0; i < walls.Length; i++)
                walls[i] = new Cell { Attribute = attribute, FishingAttribute = (sbyte)(i - 1), IsShared = true };
            return walls;
        }

        private static readonly List<MapObject> NoObjects = new List<MapObject>(0);

        public bool Valid
        {
            get { return Attribute == CellAttribute.Walk; }
        }

        private List<MapObject> _objects;

        /// <summary>
        /// The objects on this cell. Read-only for callers: change it only through <see cref="Add"/> / <see cref="Remove"/>. A cell
        /// that never held an object returns one shared empty list (the list is allocated on the first Add and kept after that).
        /// </summary>
        public List<MapObject> Objects
        {
            get { return _objects ?? NoObjects; }
        }

        public CellAttribute Attribute;
        public sbyte FishingAttribute = -1;
        /// <summary>One of the shared wall cells: never holds objects (Add moves the object to a map-owned copy).</summary>
        public bool IsShared { get; private set; }

        public void Add(MapObject mapObject)
        {
            if (mapObject == null)
            {
                ReportCellIssue("Attempted to add a null MapObject to a Cell.");
                return;
            }

            if (IsShared)
            {
                AddToOwnCopy(mapObject);
                return;
            }

            if (_objects == null)
                _objects = new List<MapObject>();
            else if (_objects.Contains(mapObject))
            {
                ReportCellIssue($"Duplicate MapObject add detected for ObjectID {mapObject.ObjectID}.");
                return;
            }

            _objects.Add(mapObject);
        }

        /// <summary>A map-owned cell with this cell's attributes and no objects.</summary>
        internal Cell CreateOwnCopy()
        {
            return new Cell { Attribute = Attribute, FishingAttribute = FishingAttribute };
        }

        // Direct Cells[x, y].Add / GetCell(...).Add callers: a shared wall cell only knows where it is through the object (its
        // CurrentMap / CurrentLocation). Map.AddObject swaps the copy in itself, so it does not depend on CurrentMap being set yet.
        private void AddToOwnCopy(MapObject mapObject)
        {
            var map = mapObject.CurrentMap;
            var location = mapObject.CurrentLocation;
            if (map == null || map.Cells == null || location.X < 0 || location.Y < 0 || location.X >= map.Width || location.Y >= map.Height ||
                !ReferenceEquals(map.Cells[location.X, location.Y], this))
            {
                ReportCellIssue($"MapObject {mapObject.ObjectID} added to a shared wall cell it is not on; not added.");
                return;
            }

            var own = CreateOwnCopy();
            map.Cells[location.X, location.Y] = own;
            own.Add(mapObject);
        }

        public void Remove(MapObject mapObject)
        {
            if (mapObject == null)
            {
                ReportCellIssue("Attempted to remove a null MapObject from a Cell.");
                return;
            }

            if (_objects == null || !_objects.Remove(mapObject))
            {
                ReportCellIssue($"Failed to remove MapObject {mapObject.ObjectID} from Cell collection.");
            }
            // DO NOT set _objects = null; keep the list to avoid re-alloc (and so readers never see it vanish)
        }

        private static void ReportCellIssue(string message)
        {
            try
            {
                throw new System.InvalidOperationException(message);
            }
            catch (System.Exception ex)
            {
                MessageQueue.Instance.Enqueue(ex);
            }
        }
    }
    public class MapRespawn
    {
        protected static Envir Envir
        {
            get { return Envir.Main; }
        }

        public RespawnInfo Info;
        public MonsterInfo Monster;
        public Map Map;
        public int Count;
        public long RespawnTime;
        public ulong NextSpawnTick;
        public byte ErrorCount = 0;

        public List<RouteInfo> Route;
        /// <summary>The cells this respawn spawns on (a view over the map's walkable cells; <see cref="Map.BuildRespawnCells"/>).</summary>
        public WalkableBox WalkableCells;

        public MapRespawn(RespawnInfo info)
        {
            Info = info;
            Monster = Envir.GetMonsterInfo(info.MonsterIndex);

            LoadRoutes();
        }
        public bool Spawn()
        {
            MonsterObject ob = MonsterObject.GetMonster(Monster);
            if (ob == null) return true;

            MonsterType type = Settings.MonsterRarityEnabled
                ? MonsterRarityData.Roll(RandomProvider.GetThreadRandom())
                : MonsterType.Normal;

            ob.SetMonsterType(type);
            return ob.Spawn(this);
        }

        public void LoadRoutes()
        {
            Route = new List<RouteInfo>();

            if (string.IsNullOrEmpty(Info.RoutePath)) return;

            string fileName = Path.Combine(Settings.RoutePath, Info.RoutePath + ".txt");

            if (!DesignFiles.Exists(fileName)) return;

            List<string> lines = DesignFiles.ReadAllLines(fileName).ToList();

            foreach (string line in lines)
            {
                RouteInfo info = RouteInfo.FromText(line);

                if (info == null) continue;

                Route.Add(info);
            }
        }
    }
}
