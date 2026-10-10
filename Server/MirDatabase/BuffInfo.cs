using Server.MirEnvir;
using Server.MirObjects;

namespace Server.MirDatabase
{
    public class BuffInfo
    {
        public BuffType Type { get; set; }
        public BuffStackType StackType { get; set; }
        public BuffProperty Properties { get; set; }
        public int Icon { get; set; }
        public bool Visible { get; set; }

        public static List<BuffInfo> Load()
        {
            List<BuffInfo> info = new List<BuffInfo>
            {
                //Magics
                new BuffInfo { Type = BuffType.TemporalFlux, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.Hiding, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.Haste, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.SwiftFeet, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.Fury, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.SoulShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.BlessedArmour, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.LightBody, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.UltimateEnhancer, Properties = BuffProperty.None, StackType = BuffStackType.ResetStatAndDuration },
                new BuffInfo { Type = BuffType.ProtectionField, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.Rage, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.Curse, Properties = BuffProperty.RemoveOnDeath | BuffProperty.Debuff, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.MoonLight, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.DarkBody, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.Concentration, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.VampireShot, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.PoisonShot, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.CounterAttack, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.MentalState, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.EnergyShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.MagicBooster, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.PetEnhancer, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.ImmortalSkin, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.MagicShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.ElementalBarrier, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },

                //Monsters
                new BuffInfo { Type = BuffType.HornedArcherBuff, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.ColdArcherBuff, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.GeneralMeowMeowShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.RhinoPriestDebuff, Properties = BuffProperty.Debuff, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.PowerBeadBuff, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.HornedWarriorShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.HornedCommanderShield, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration, Visible = true },
                new BuffInfo { Type = BuffType.Blindness, Properties = BuffProperty.RemoveOnDeath | BuffProperty.Debuff, StackType = BuffStackType.ResetDuration },

                //Special
                new BuffInfo { Type = BuffType.GameMaster, Properties = BuffProperty.None, StackType = BuffStackType.Infinite, Visible = Settings.GameMasterEffect },
                new BuffInfo { Type = BuffType.Mentee, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.Mentor, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.Guild, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.Skill, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.ClearRing, Properties = BuffProperty.None, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.Transform, Properties = BuffProperty.None, StackType = BuffStackType.None },
                new BuffInfo { Type = BuffType.Lover, Properties = BuffProperty.RemoveOnExit, StackType = BuffStackType.Infinite },
                new BuffInfo { Type = BuffType.Rested, Properties = BuffProperty.None, StackType = BuffStackType.ResetDuration },
                new BuffInfo { Type = BuffType.Prison, Properties = BuffProperty.None, StackType = BuffStackType.None }, //???
                new BuffInfo { Type = BuffType.General, Properties = BuffProperty.None, StackType = BuffStackType.None }, //???
                new BuffInfo { Type = BuffType.Newbie, Properties = BuffProperty.RemoveOnExit, StackType = BuffStackType.Infinite }, //???

                //Stats
                new BuffInfo { Type = BuffType.Exp, Properties = BuffProperty.PauseInSafeZone, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Drop, Properties = BuffProperty.PauseInSafeZone, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Gold, Properties = BuffProperty.PauseInSafeZone, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.BagWeight, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Impact, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Magic, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Taoist, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Storm, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.HealthAid, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.ManaAid, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Defence, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.MagicDefence, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.WonderDrug, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration },
                new BuffInfo { Type = BuffType.Knapsack, Properties = BuffProperty.None, StackType = BuffStackType.StackDuration }
            };

            return info;
        }
    }

    public class Buff
    {
        protected static Envir Envir
        {
            get { return Envir.Main; }
        }

        private Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        public BuffInfo Info;
        public MapObject Caster;
        public uint ObjectID;
        public long ExpireTime;

        public long LastTime, NextTime;

        public Stats Stats;

        public int[] Values;

        public bool FlagForRemoval;
        public bool Paused;

        public BuffType Type
        {
            get { return Info.Type; }
        }

        public BuffStackType StackType
        {
            get { return Info.StackType; }
        }

        public BuffProperty Properties
        {
            get { return Info.Properties; }
        }

        public Buff(BuffType type)
        {
            Info = Envir.GetBuffInfo(type);
            Stats = new Stats();
            Data = new Dictionary<string, object>();
        }

        public Buff(BinaryReader reader, int version, int customVersion)
        {
            var type = (BuffType)reader.ReadByte();

            Info = Envir.GetBuffInfo(type);

            Caster = null;

            if (version < 88)
            {
                var visible = reader.ReadBoolean();
            }

            ObjectID = reader.ReadUInt32();
            ExpireTime = reader.ReadInt64();

            if (version <= 84)
            {
                Values = new int[reader.ReadInt32()];

                for (int i = 0; i < Values.Length; i++)
                {
                    Values[i] = reader.ReadInt32();
                }

                if (version < 88)
                {
                    var infinite = reader.ReadBoolean();
                }

                Stats = new Stats();
                Data = new Dictionary<string, object>();
            }
            else
            {
                if (version < 88)
                {
                    var stackable = reader.ReadBoolean();
                }

                Values = new int[0];
                Stats = new Stats(reader, version, customVersion);
                Data = new Dictionary<string, object>();

                int count = reader.ReadInt32();

                for (int i = 0; i < count; i++)
                {
                    var key = reader.ReadString();

                    if (version >= 118)
                    {
                        if (TryReadDataValue(reader, out var value))
                            Data[key] = value;
                        continue;
                    }

                    // v117 and older stored each value as a BinaryFormatter blob. BinaryFormatter throws on .NET 9+, so the
                    // blob is skipped (roadmap P3, decision: skip and reset). The only writer is Archer Concentration, whose
                    // keys ("Interrupted", "InterruptTime") then read back as default: not interrupted, which is what the
                    // buff returns to within 3 s anyway.
                    var length = reader.ReadInt32();
                    reader.BaseStream.Seek(length, SeekOrigin.Current);
                }

                if (version > 86)
                {
                    count = reader.ReadInt32();

                    Values = new int[count];

                    for (int i = 0; i < count; i++)
                    {
                        Values[i] = reader.ReadInt32();
                    }
                }
            }
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write((byte)Type);
            writer.Write(ObjectID);
            writer.Write(ExpireTime);

            Stats.Save(writer);

            // v118+ (roadmap P3): each value is a one-byte type tag plus the value, never BinaryFormatter.
            // Values of an unsupported type are not persisted (logged once): failing the whole account save over a
            // transient buff detail would be worse.
            var count = 0;
            foreach (KeyValuePair<string, object> pair in Data)
                if (DataTagOf(pair.Value) != DataTag.Unsupported) count++;

            writer.Write(count);

            foreach (KeyValuePair<string, object> pair in Data)
            {
                var tag = DataTagOf(pair.Value);
                if (tag == DataTag.Unsupported)
                {
                    ReportUnsupportedDataValue(pair.Key, pair.Value);
                    continue;
                }

                writer.Write(pair.Key);
                writer.Write((byte)tag);
                switch (tag)
                {
                    case DataTag.Bool: writer.Write((bool)pair.Value); break;
                    case DataTag.Byte: writer.Write((byte)pair.Value); break;
                    case DataTag.Short: writer.Write((short)pair.Value); break;
                    case DataTag.Int: writer.Write((int)pair.Value); break;
                    case DataTag.Long: writer.Write((long)pair.Value); break;
                    case DataTag.Float: writer.Write((float)pair.Value); break;
                    case DataTag.Double: writer.Write((double)pair.Value); break;
                    case DataTag.String: writer.Write((string)pair.Value); break;
                    case DataTag.DateTime: writer.Write(((DateTime)pair.Value).ToBinary()); break;
                }
            }

            writer.Write(Values.Length);

            for (int i = 0; i < Values.Length; i++)
            {
                writer.Write(Values[i]);
            }
        }

        // Persisted Buff.Data value tags (v118+). Append only: the byte value is stored in Server.MirADB.
        private enum DataTag : byte
        {
            Unsupported = 0,
            Bool = 1,
            Byte = 2,
            Short = 3,
            Int = 4,
            Long = 5,
            Float = 6,
            Double = 7,
            String = 8,
            DateTime = 9,
        }

        private static DataTag DataTagOf(object value) => value switch
        {
            bool => DataTag.Bool,
            byte => DataTag.Byte,
            short => DataTag.Short,
            int => DataTag.Int,
            long => DataTag.Long,
            float => DataTag.Float,
            double => DataTag.Double,
            string => DataTag.String,
            DateTime => DataTag.DateTime,
            _ => DataTag.Unsupported,
        };

        private static bool TryReadDataValue(BinaryReader reader, out object value)
        {
            switch ((DataTag)reader.ReadByte())
            {
                case DataTag.Bool: value = reader.ReadBoolean(); return true;
                case DataTag.Byte: value = reader.ReadByte(); return true;
                case DataTag.Short: value = reader.ReadInt16(); return true;
                case DataTag.Int: value = reader.ReadInt32(); return true;
                case DataTag.Long: value = reader.ReadInt64(); return true;
                case DataTag.Float: value = reader.ReadSingle(); return true;
                case DataTag.Double: value = reader.ReadDouble(); return true;
                case DataTag.String: value = reader.ReadString(); return true;
                case DataTag.DateTime: value = DateTime.FromBinary(reader.ReadInt64()); return true;
                default:
                    // An unknown tag means the rest of this buff cannot be framed: fail the load loudly rather than misread.
                    throw new InvalidDataException("Unknown Buff.Data value tag in Server.MirADB.");
            }
        }

        private static int _unsupportedDataReported;

        private static void ReportUnsupportedDataValue(string key, object value)
        {
            if (Interlocked.Exchange(ref _unsupportedDataReported, 1) == 1) return;
            MessageQueue.Instance.Enqueue($"[Save] Buff.Data value '{key}' of type {value?.GetType().FullName ?? "null"} is not persisted (supported: bool, byte, short, int, long, float, double, string, DateTime). Reported once per process.");
        }

        public T Get<T>(string key)
        {
            if (!Data.TryGetValue(key, out object result))
            {
                return default;
            }

            return (T)result;
        }

        public void Set(string key, object val)
        {
            Data[key] = val;
        }

        public ClientBuff ToClientBuff()
        {
            return new ClientBuff
            {
                Type = Type,
                Caster = Caster?.Name ?? "",
                ObjectID = ObjectID,
                Visible = Info.Visible,
                Infinite = StackType == BuffStackType.Infinite,
                Paused = Paused,
                ExpireTime = ExpireTime,
                Stats = new Stats(Stats),
                Values = Values
            };
        }
    }
}
