using Server.MirNetwork;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Roadmap P9-01 / Packet-Mutation-Audit.md condition 2: golden tests for the encode-once behaviour change on packets that
/// reference live objects (audit table G). Within one connection pass every recipient gets the bytes of the FIRST encode
/// (a snapshot), the next pass encodes afresh, and the sampled guard detects a change. Unit level: the scratch world has no
/// guild NPCs and StressClient has no observer flow, so these drive MirConnection.Encode directly.
/// </summary>
[Collection(PersistenceCollection.Name)] // touches Settings (static ctor reads ./Configs)
public class EncodeOnceGoldenTests
{
    private static UserItem NewItem(ushort count)
    {
        var info = new ItemInfo { Index = 7, Name = "Test Potion", Type = ItemType.Potion, StackSize = 100, Image = 1 };
        return new UserItem(info) { UniqueID = 99, ItemIndex = info.Index, Count = count, CurrentDura = 1, MaxDura = 1 };
    }

    private static void WithEncodeOnce(Action body)
    {
        var dir = NewTempFolder("encode-once");
        InFolder(dir, () =>
        {
            var old = Settings.EncodeOnce;
            try
            {
                Settings.EncodeOnce = true;
                MirConnection.EndEncodePass();
                body();
            }
            finally
            {
                MirConnection.EndEncodePass();
                Settings.EncodeOnce = old;
            }
            return 0;
        });
        Directory.Delete(dir, true);
    }

    /// <summary>Guild fan-out: GuildStorageItemChange to several members while a second member takes the item in the same tick.</summary>
    [Fact]
    public void Guild_storage_fan_out_sends_one_snapshot_per_pass()
    {
        WithEncodeOnce(() =>
        {
            var stored = new GuildStorageItem { UserId = 5, Item = NewItem(10) };
            var p = new ServerPackets.GuildStorageItemChange { Type = 0, To = 3, User = 1, Item = stored };

            var memberA = MirConnection.Encode(p);
            stored.Item.Count = 4;                 // a second member retrieves part of the stack before the pass reaches member B
            var memberB = MirConnection.Encode(p);

            Assert.Same(memberA, memberB);         // every recipient in the pass gets the first snapshot
            Assert.False(MirConnection.CheckNotMutated(p, memberA)); // and the guard notices the live object changed

            MirConnection.EndEncodePass();
            var nextPass = MirConnection.Encode(p);
            Assert.NotEqual(memberA, nextPass);    // the next pass encodes the current state
            Assert.True(MirConnection.CheckNotMutated(p, nextPass));
        });
    }

    /// <summary>Observer case: PlayerInspect carries the live Equipment array to the inspector and their observers.</summary>
    [Fact]
    public void Observer_copy_of_an_inspect_matches_the_inspector_within_a_pass()
    {
        WithEncodeOnce(() =>
        {
            var equipment = new UserItem[14];
            equipment[0] = NewItem(1);
            var p = new ServerPackets.PlayerInspect { Name = "Target", GuildName = "", GuildRank = "", Equipment = equipment, LoverName = "" };

            var inspector = MirConnection.Encode(p);
            equipment[1] = NewItem(1);             // target equips something before the pass reaches the observer
            var observer = MirConnection.Encode(p);
            Assert.Equal(inspector, observer);     // observer sees exactly what the inspector saw

            MirConnection.EndEncodePass();
            Assert.NotEqual(inspector, MirConnection.Encode(p));
        });
    }

    [Fact]
    public void Switch_off_encodes_every_time()
    {
        WithEncodeOnce(() =>
        {
            Settings.EncodeOnce = false;
            var stored = new GuildStorageItem { UserId = 5, Item = NewItem(10) };
            var p = new ServerPackets.GuildStorageItemChange { Item = stored };
            var first = MirConnection.Encode(p);
            stored.Item.Count = 4;
            Assert.NotEqual(first, MirConnection.Encode(p));
        });
    }
}
