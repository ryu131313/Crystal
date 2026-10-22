using System.Reflection;
using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Packet.GetPacketBytes encodes through a reused per-thread stream (2026-10-22). Pins it to the frame the old two-stream
/// encoder built (length, Index, payload; payload compressed when Compressed) for every packet the fixture can fill.
/// </summary>
public class PacketEncodeTests
{
    private static readonly MethodInfo CompressMethod =
        typeof(Packet).GetMethod("CompressPacket", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The encoder as it was before 2026-10-22, step for step.</summary>
    private static byte[] LegacyFrame(Packet p)
    {
        if (p.Index < 0) return new byte[0];
        var payload = Write(p);
        var mem = p.Compressed ? (byte[])CompressMethod.Invoke(p, new object[] { payload })! : payload;
        using var ms = new MemoryStream();
        ms.SetLength(2);
        ms.Seek(2, SeekOrigin.Begin);
        using var writer = new BinaryWriter(ms);
        writer.Write(p.Index);
        writer.Write(mem);
        ms.Seek(0, SeekOrigin.Begin);
        writer.Write((ushort)ms.Length);
        return ms.ToArray();
    }

    [Fact]
    public void Every_packet_encodes_byte_identically_to_the_legacy_encoder()
    {
        var mismatches = new List<string>();
        var checkedCount = 0;
        foreach (var t in ServerPacketTypes.Concat(ClientPacketTypes))
        {
            var p = CreateInstance(t);
            Fill(p);
            byte[] legacy;
            try { legacy = LegacyFrame(p); }
            catch { continue; } // fixture gap, as in the round-trip test

            var frame = p.GetPacketBytes();
            checkedCount++;
            if (!legacy.AsSpan().SequenceEqual(frame))
                mismatches.Add($"{t.Namespace}.{t.Name}: {legacy.Length} vs {frame.Length} bytes");
        }

        Assert.True(checkedCount > 400, $"only {checkedCount} packets checked");
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Compressed_packet_matches_the_legacy_encoder()
    {
        var p = new ServerPackets.NPCGoods();
        for (int i = 0; i < 50; i++) p.List.Add(new UserItem(new ItemInfo { Index = i, Name = "Item" + i }) { UniqueID = (ulong)i });
        Assert.True(p.Compressed);
        Assert.Equal(LegacyFrame(p), p.GetPacketBytes());
    }

    [Fact]
    public void Each_call_returns_its_own_array_and_a_big_frame_does_not_leak_into_the_next()
    {
        var big = new ServerPackets.Chat { Message = new string('x', 100_000), Type = ChatType.Normal };
        var small = new ServerPackets.Chat { Message = "hi", Type = ChatType.Normal };

        var a = small.GetPacketBytes();
        var bigFrame = big.GetPacketBytes();
        var b = small.GetPacketBytes();

        Assert.NotSame(a, b);
        Assert.Equal(a, b);
        Assert.Equal(LegacyFrame(big), bigFrame);
        Assert.Equal(LegacyFrame(small), b);
    }

    [Fact]
    public void Encoding_on_several_threads_at_once_gives_the_legacy_bytes()
    {
        var expected = LegacyFrame(new ServerPackets.Chat { Message = "parallel", Type = ChatType.Normal });
        Parallel.For(0, 2000, _ =>
        {
            var frame = new ServerPackets.Chat { Message = "parallel", Type = ChatType.Normal }.GetPacketBytes();
            Assert.Equal(expected, frame);
        });
    }
}
