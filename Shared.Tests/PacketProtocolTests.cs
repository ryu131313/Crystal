using System.Text;
using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Protocol safety net (roadmap P1). A missing factory case makes the receiver discard its whole
/// receive buffer silently; a read/write mismatch IP-blocks the player for 24 h (server) or kills
/// the client's socket thread. These tests catch both before a build ships.
/// </summary>
public class PacketProtocolTests
{
    // Known gaps (client.md §3.5 / server.md §3.5 Known defects). Fixing one must also remove it
    // from these lists: the tests assert the gap set EXACTLY, so new gaps and fixed gaps both fail.
    private static readonly string[] KnownClientFactoryGaps = { "PurchaseGuildTerritory" };
    private static readonly string[] KnownServerFactoryGaps = { "RefineCancel" };
    // Known read/write asymmetries (latent: not sent today). Fix in the roadmap C5 Shared batch.
    // S.ChatItemStats: WritePacket skips a null Stats but ReadPacket always reads a UserItem
    //   (the only send site, PlayerObject.cs ~7819, is commented out).
    private static readonly string[] KnownAsymmetries = Array.Empty<string>();

    [Fact]
    public void Every_client_packet_id_has_a_factory_case()
    {
        var missing = new List<string>();
        foreach (ClientPacketIds id in Enum.GetValues(typeof(ClientPacketIds)))
        {
            var p = GetClientPacket((short)id);
            if (p == null) missing.Add(id.ToString());
            else Assert.True(p.Index == (short)id, $"C factory for {id} returned {p.GetType().Name} with Index {p.Index}");
        }
        Assert.Equal(KnownClientFactoryGaps.OrderBy(x => x), missing.OrderBy(x => x));
    }

    [Fact]
    public void Every_server_packet_id_has_a_factory_case()
    {
        var missing = new List<string>();
        foreach (ServerPacketIds id in Enum.GetValues(typeof(ServerPacketIds)))
        {
            var p = GetServerPacket((short)id);
            if (p == null) missing.Add(id.ToString());
            else Assert.True(p.Index == (short)id, $"S factory for {id} returned {p.GetType().Name} with Index {p.Index}");
        }
        Assert.Equal(KnownServerFactoryGaps.OrderBy(x => x), missing.OrderBy(x => x));
    }

    [Fact]
    public void Packet_indexes_are_unique_per_direction_and_reachable_from_the_factory()
    {
        CheckSide(ServerPacketTypes, GetServerPacket, "S");
        CheckSide(ClientPacketTypes, GetClientPacket, "C");
    }

    private static void CheckSide(IEnumerable<Type> types, Func<short, Packet> factory, string side)
    {
        var seen = new Dictionary<short, string>();
        var problems = new List<string>();
        foreach (var t in types)
        {
            var p = CreateInstance(t);
            if (seen.TryGetValue(p.Index, out var other)) problems.Add($"{side}.{t.Name} and {side}.{other} share Index {p.Index}");
            seen[p.Index] = t.Name;
            var viaFactory = factory(p.Index);
            if (viaFactory != null && viaFactory.GetType() != t)
                problems.Add($"{side}.{t.Name} (Index {p.Index}) but the factory returns {viaFactory.GetType().Name}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Write_then_read_round_trips_byte_identically()
    {
        var mismatches = new List<string>();
        var fixtureGaps = new List<string>();
        foreach (var t in ServerPacketTypes.Concat(ClientPacketTypes))
        {
            var side = t.Namespace == "ServerPackets" ? "S" : "C";
            var p1 = CreateInstance(t);
            Fill(p1);
            byte[] b1;
            try { b1 = Write(p1); }
            catch (Exception e)
            {
                fixtureGaps.Add($"{side}.{t.Name}: {e.GetType().Name}");
                continue;
            }

            var p2 = CreateInstance(t);
            long consumed;
            try { consumed = Read(p2, b1); }
            catch (Exception e)
            {
                mismatches.Add($"{side}.{t.Name}: ReadPacket threw {e.GetType().Name} on its own WritePacket output ({b1.Length} bytes)");
                continue;
            }
            if (consumed != b1.Length)
                mismatches.Add($"{side}.{t.Name}: ReadPacket consumed {consumed} of {b1.Length} bytes (under-read)");

            byte[] b2;
            try { b2 = Write(p2); }
            catch (Exception e)
            {
                mismatches.Add($"{side}.{t.Name}: WritePacket threw {e.GetType().Name} after a read");
                continue;
            }
            if (!b1.AsSpan().SequenceEqual(b2))
                mismatches.Add($"{side}.{t.Name}: re-written bytes differ ({b1.Length} vs {b2.Length})");
        }

        var unexpected = mismatches.Where(m => !KnownAsymmetries.Any(k => m.StartsWith(k + ":"))).ToList();
        var fixedKnown = KnownAsymmetries.Where(k => !mismatches.Any(m => m.StartsWith(k + ":"))).ToList();
        Assert.True(fixedKnown.Count == 0, "Known asymmetry no longer reproduces; remove it from KnownAsymmetries: " + string.Join(", ", fixedKnown));
        mismatches = unexpected;
        Assert.True(mismatches.Count == 0,
            "Read/write asymmetry:" + Environment.NewLine + string.Join(Environment.NewLine, mismatches));
        // Fixture gaps are packets the reflection filler cannot populate; they are covered by the
        // factory/manifest tests but not by this round trip. Keep the list visible and small.
        Assert.True(fixtureGaps.Count <= KnownFixtureGapBudget,
            $"{fixtureGaps.Count} packets could not be built by the fixture (budget {KnownFixtureGapBudget}):" +
            Environment.NewLine + string.Join(Environment.NewLine, fixtureGaps));
    }

    /// <summary>
    /// Known defect pinned (roadmap C5 batch): a null Stats writes 8 bytes, but ReadPacket always
    /// reads a UserItem -> EndOfStreamException on the receiver (client socket thread). Not sent today.
    /// When fixed, flip this test to assert a clean round trip.
    /// </summary>
    [Fact]
    public void Known_defect_ChatItemStats_with_null_stats_does_not_round_trip()
    {
        var p = new ServerPackets.ChatItemStats { ChatItemId = 1, Stats = null };
        var bytes = Write(p);
        Assert.Equal(8, bytes.Length);
        Assert.Throws<EndOfStreamException>(() => Read(new ServerPackets.ChatItemStats(), bytes));
    }

    // Raise only with a reason; lower it whenever the filler improves.
    private const int KnownFixtureGapBudget = 0;

    [Fact]
    public void Wire_manifest_is_append_only()
    {
        var path = RepoFile("Shared.Tests", "packet-manifest.txt");
        var current = BuildManifest();
        if (Environment.GetEnvironmentVariable("MIR2_REGEN_PACKET_MANIFEST") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, string.Join("\n", current) + "\n", new UTF8Encoding(false));
            if (!File.Exists(path)) Assert.Fail("Could not write manifest");
            return;
        }

        var recorded = File.ReadAllLines(path).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
        var cur = current.Where(l => !l.StartsWith("#")).ToList();
        var problems = new List<string>();
        var curByKey = cur.ToDictionary(l => Key(l));
        foreach (var line in recorded)
        {
            if (!curByKey.TryGetValue(Key(line), out var now))
                problems.Add($"removed or renumbered: {line}");
            else if (now != line)
                problems.Add($"changed: {line}  =>  {now}");
        }
        Assert.True(problems.Count == 0,
            "Wire manifest drift. Packet IDs are implicit ordinals: append only. If the change is deliberate " +
            "(e.g. a fixed known defect or a new field), regenerate with MIR2_REGEN_PACKET_MANIFEST=1 and commit the diff." +
            Environment.NewLine + string.Join(Environment.NewLine, problems));

        static string Key(string l) { var p = l.Split('\t'); return p[0] + "\t" + p[1] + "\t" + p[2]; }
    }

    /// <summary>One line per packet ID: side, ordinal, enum name, class, observable, field-signature hash.</summary>
    internal static List<string> BuildManifest()
    {
        var lines = new List<string> { "# side\tid\tname\tclass\tobservable\tfields (regenerate: MIR2_REGEN_PACKET_MANIFEST=1 dotnet test Shared.Tests)" };
        foreach (ServerPacketIds id in Enum.GetValues(typeof(ServerPacketIds)))
            lines.Add(Line("S", (short)id, id.ToString(), GetServerPacket((short)id)));
        foreach (ClientPacketIds id in Enum.GetValues(typeof(ClientPacketIds)))
            lines.Add(Line("C", (short)id, id.ToString(), GetClientPacket((short)id)));
        return lines;

        static string Line(string side, short id, string name, Packet p) =>
            p == null
                ? $"{side}\t{id}\t{name}\t-\t-\t-"
                : $"{side}\t{id}\t{name}\t{p.GetType().Name}\t{(p.Observable ? 1 : 0)}\t{ShortHash(FieldSignature(p.GetType()))}";
    }
}
