using static Shared.Tests.PacketTestSupport;

namespace Shared.Tests;

/// <summary>
/// Packet.ReceivePacket(buffer, offset, count, out consumed) (2026-10-22, server receive path) parses the frames of one receive
/// buffer in place. Pins it to the copying ReceivePacket(raw, out extra) loop: same packets, same leftover, same drops.
/// </summary>
public class PacketReceiveOffsetTests
{
    private static List<byte[]> ClientFrames()
    {
        var frames = new List<byte[]>();
        foreach (var t in ClientPacketTypes)
        {
            var p = CreateInstance(t);
            Fill(p);
            byte[] frame;
            try { frame = p.GetPacketBytes(); }
            catch { continue; } // fixture gap
            if (frame.Length < 4) continue;
            frames.Add(frame);
        }
        return frames;
    }

    private static (List<byte[]> packets, byte[] leftover) CopyingParse(byte[] raw)
    {
        var packets = new List<byte[]>();
        Packet p;
        while ((p = Packet.ReceivePacket(raw, out raw)) != null) packets.Add(p.GetPacketBytes());
        return (packets, raw);
    }

    private static (List<byte[]> packets, byte[] leftover) OffsetParse(byte[] raw, int count)
    {
        var packets = new List<byte[]>();
        int offset = 0, used;
        Packet p;
        while ((p = Packet.ReceivePacket(raw, offset, count - offset, out used)) != null)
        {
            packets.Add(p.GetPacketBytes());
            offset += used;
        }
        offset += used;
        return (packets, offset >= count ? Array.Empty<byte>() : raw.AsSpan(offset, count - offset).ToArray());
    }

    private static void AssertSame((List<byte[]> packets, byte[] leftover) a, (List<byte[]> packets, byte[] leftover) b)
    {
        Assert.Equal(a.packets.Count, b.packets.Count);
        for (int i = 0; i < a.packets.Count; i++) Assert.True(a.packets[i].AsSpan().SequenceEqual(b.packets[i]), $"packet {i}");
        Assert.True(a.leftover.AsSpan().SequenceEqual(b.leftover), "leftover");
    }

    [Fact]
    public void Offset_parse_matches_the_copying_parse()
    {
        var was = Packet.IsServer;
        Packet.IsServer = true;
        try
        {
            var frames = ClientFrames();
            Assert.True(frames.Count > 100, $"only {frames.Count} client frames");

            var all = frames.SelectMany(f => f).ToArray();
            // Whole buffer, then a trailing partial frame, then an oversized buffer with slack after count (a socket buffer).
            AssertSame(CopyingParse(all), OffsetParse(all, all.Length));

            var partial = all.Concat(frames[0].Take(frames[0].Length - 1)).ToArray();
            var partialResult = OffsetParse(partial, partial.Length);
            AssertSame(CopyingParse(partial), partialResult);
            Assert.Equal(frames[0].Length - 1, partialResult.leftover.Length);

            var slack = new byte[all.Length + 100];
            Buffer.BlockCopy(all, 0, slack, 0, all.Length);
            Array.Fill(slack, (byte)0xFF, all.Length, 100);
            AssertSame(CopyingParse(all), OffsetParse(slack, all.Length));

            // Under 4 bytes: nothing consumed.
            AssertSame(CopyingParse(all.Take(3).ToArray()), OffsetParse(all, 3));
        }
        finally { Packet.IsServer = was; }
    }

    [Fact]
    public void Unknown_packet_id_drops_the_rest_like_the_copying_parse()
    {
        var was = Packet.IsServer;
        Packet.IsServer = true;
        try
        {
            var frames = ClientFrames();
            var bad = new byte[] { 6, 0, 0x7F, 0x7F, 1, 2 }; // length 6, id 32639
            var raw = frames[0].Concat(bad).Concat(frames[1]).ToArray();
            var offset = OffsetParse(raw, raw.Length);
            AssertSame(CopyingParse(raw), offset);
            Assert.Single(offset.packets);
            Assert.Empty(offset.leftover);
        }
        finally { Packet.IsServer = was; }
    }

    [Fact]
    public void Malformed_payload_throws_InvalidDataException_like_the_copying_parse()
    {
        var was = Packet.IsServer;
        Packet.IsServer = true;
        try
        {
            var frame = new byte[] { 3, 0, 0, 0 }; // length 3 < header
            Assert.Throws<InvalidDataException>(() => Packet.ReceivePacket(frame, out _));
            Assert.Throws<InvalidDataException>(() => Packet.ReceivePacket(frame, 0, frame.Length, out _));
        }
        finally { Packet.IsServer = was; }
    }
}
