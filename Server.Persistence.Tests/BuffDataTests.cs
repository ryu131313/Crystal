using Server.MirDatabase;
using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Roadmap P3: the v118 typed Buff.Data encoding round-trips every supported type without BinaryFormatter.</summary>
[Collection(PersistenceCollection.Name)]
public class BuffDataTests
{
    [Fact]
    public void Typed_buff_data_round_trips_every_supported_type()
    {
        lock (Gate)
        {
            EnsureBuffInfo();
            var when = new DateTime(2030, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var b = new Buff(BuffType.Concentration) { ObjectID = 7, ExpireTime = 99, Values = new[] { 3 } };
            b.Set("bool", true); b.Set("byte", (byte)200); b.Set("short", (short)-3); b.Set("int", 123456);
            b.Set("long", 1L << 40); b.Set("float", 1.5f); b.Set("double", 2.25); b.Set("string", "xé");
            b.Set("date", when);

            var bytes = Save(b);
            var r = new Buff(new BinaryReader(new MemoryStream(bytes)), Envir.Version, Envir.CustomVersion);

            Assert.True(r.Get<bool>("bool"));
            Assert.Equal((byte)200, r.Get<byte>("byte"));
            Assert.Equal((short)-3, r.Get<short>("short"));
            Assert.Equal(123456, r.Get<int>("int"));
            Assert.Equal(1L << 40, r.Get<long>("long"));
            Assert.Equal(1.5f, r.Get<float>("float"));
            Assert.Equal(2.25, r.Get<double>("double"));
            Assert.Equal("xé", r.Get<string>("string"));
            Assert.Equal(when, r.Get<DateTime>("date"));
            Assert.Equal(bytes, Save(r));
        }
    }

    [Fact]
    public void Unsupported_buff_data_value_is_skipped_not_fatal()
    {
        lock (Gate)
        {
            EnsureBuffInfo();
            var b = new Buff(BuffType.Concentration) { Values = Array.Empty<int>() };
            b.Set("ok", 5L);
            b.Set("object", new object());
            var r = new Buff(new BinaryReader(new MemoryStream(Save(b))), Envir.Version, Envir.CustomVersion);
            Assert.Equal(5L, r.Get<long>("ok"));
            Assert.Null(r.Get<object>("object"));
        }
    }

    private static byte[] Save(Buff b)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) b.Save(w);
        return ms.ToArray();
    }
}
