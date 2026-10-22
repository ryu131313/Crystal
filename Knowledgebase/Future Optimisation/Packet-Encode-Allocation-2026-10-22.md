# Packet encode allocation — one reused stream per thread (2026-10-22)

**Status: implemented, not committed. No wire change.** Follow-up to the 2026-10-22 allocation trace (200 bots), which put `Packet.GetPacketBytes` and the packets' `WritePacket` buffers at ~65 % of all managed allocation.

## Change
- **`Shared/Packet.cs` `GetPacketBytes`** used two `MemoryStream`s, two `BinaryWriter`s and about four `byte[]` per encode (both stream buffers, a payload `ToArray`, the frame `ToArray`). Now:
  - One `[ThreadStatic]` `MemoryStream` + `BinaryWriter` per thread. Encoding runs on the server game thread, web admin HTTP threads, the client UI thread and tool threads, so the stream is never shared.
  - It writes a placeholder length, then `Index`, then `WritePacket`. The result is one exact-size `byte[]`, and the length is patched into it. That array is the only allocation for an uncompressed packet.
  - `Compressed` packets (`S.NPCGoods`, `S.FriendList`, …) still pass the payload as a `byte[]` to the virtual `CompressPacket`, so the bytes are the same as before.
  - A `WritePacket` that encodes another packet gets a private stream (an `_encoding` flag). A thread whose stream grew past 64 KB drops it after the call.
  - The length header still wraps above 65 535, as before (server.md §3.5).
- **The return type is now `byte[]` (it was `IEnumerable<byte>`, which always held a `byte[]`).** Every existing caller compiles unchanged. `.ToArray()` / `AddRange` still work, but the client, WebRelay and StressClient still make an extra copy where they call `.ToArray()`. The casts were removed in `MirConnection.ToBytes`, `LiveDbPush.EncodedSize` and `S.FriendList.FrameLength`.
- **Tests:** `Shared.Tests/PacketEncodeTests.cs` pins every packet the fixture can fill (> 400, both directions) byte-for-byte to the old two-stream encoder. It also covers a compressed `S.NPCGoods`, a 100 KB frame followed by a small one, and 2000 parallel encodes. `packet-manifest.txt` is unchanged.

## Measurement
Method:
- **Server:** two Release `Server.exe` builds from a detached worktree at `b9845ea9`: before = HEAD, after = HEAD + only this patch. Other sessions' uncommitted edits (e.g. `NPCSegment.cs`) were kept out of both.
- **World:** a copy of the map-memory scratch run folder (463 real maps), on port 17120.
- **Load:** StressClient with 200 bots, ramp 20/s, 170 s, seed 7. The same pre-change StressClient build drove every run.
- **Trace:** `dotnet-trace collect --profile gc-verbose` for 60 s, starting 60 s after the bots began.
- **Analysis:** AllocationTick events grouped by type and game method (TraceEvent 3.1.16).

Two runs per side, interleaved: before, after, after2, before2. The bots' traffic varied between runs: the packets per second the bots received ranged from 12 k to 21 k (random paths, NPC visits). The table therefore also gives allocation per 1000 packets received.

| Run | bot pkt/s in | alloc MB/s | KB per 1000 pkts | encode path MB/s | flush array MB/s | GCs / min (all gen1) | pauses (ms) total / median / max | promoted per GC |
|---|---|---|---|---|---|---|---|---|
| before | 20 877 | 4.9 | 235 | 2.73 | 0.48 | 20 | 69 / 3.4 / 3.8 | 1.5 MB |
| before2 | 12 005 | 3.5 | 292 | 1.96 | 0.38 | 14 | 53 / 3.8 / 4.3 | 1.7 MB |
| after | 17 217 | 2.2 | 128 | 0.17 | 0.44 | 8 | 43 / 4.7 / 10.4 | 1.7 MB (first 4.3) |
| after2 | 16 135 | 2.0 | 124 | 0.17 | 0.43 | 8 | 45 / 4.9 / 9.3 | 1.7 MB (one 3.9) |

The columns:
- **Encode path** sums `GetPacketBytes` and every `*.WritePacket <- GetPacketBytes` row.
- **Flush array** is the one array per connection per send in `MirConnection.Flush`.

Results:
- **Encode-path allocation per packet: −93 %.** Per 1000 packets it fell from 131–163 KB to 10–11 KB. `MemoryStream`/`BinaryWriter` dropped out of the type list; the only `MemoryStream` left is in `Packet.ReceivePacket`. The remaining encode bytes are the frames themselves, shared across recipients by EncodeOnce.
- **Total allocation per packet: about −50 %.** GCs went down from 14–20 to 8 per minute, and total pause time per minute from 53–69 ms to 43–45 ms.
- **Single pauses got longer: median 3.4–3.8 → 4.7–4.9 ms, max 3.8–4.3 → 9.3–10.4 ms.** The bytes promoted per GC stayed the same (~1.7 MB), except for one GC per after run that promoted ~4 MB. That GC was the max pause in both runs.
  - Likely cause: with ~2.5× longer between gen1 GCs, more gen2→gen0 card marking and occasional larger survivor batches land in a single GC. This is an inference, not measured.
  - Still zero gen2 collections. This is the trade-off of allocating less with the same gen0 budget. If max pause matters more than total, look at `GCgen0size` / `GCConserveMemory` separately; nothing was changed here.
- **The bots saw no difference:** every run PASS, keepalive RTT ~2 ms, no decode errors.

## What is left on the send path (after)
1. **`NPCSegment.ReplaceValue` Regex (~40 % of what is left; 0.75–0.90 MB/s).** It is not in the packet path. Another session was editing `NPCSegment.cs` at the time.
2. **The per-flush send array in `MirConnection.Flush` (0.35 + 0.08 MB/s, ~20 %).** Renting it from `ArrayPool<byte>` needs:
   - `ArraySegment` lengths in `_outbox`;
   - a return in `CompleteSend` and in the APM `SendData` path;
   - care with arrays still queued on disconnect.
   That is concurrency work on P9-09, so it was left as a follow-up and not done here.
3. **`PlayerObject.GetInfo` / `GetVisibleBuffTypes`** (List<BuffType> per object add), `BeginReceive`, `ReceivePacket` (one MemoryStream + BinaryReader per received packet).

Artifacts: `%TEMP%\mir2-pktenc\` (traces `before*.nettrace` / `after*.nettrace`, `*-alloc.txt`, `*-bots.log`, `measure.ps1`, `pktenc.patch`).
