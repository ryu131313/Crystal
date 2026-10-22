# Allocation tail: pooled flush arrays, info-packet buff lists, in-place receive parse (2026-10-22)

**Status: implemented, not committed. No wire change.** Follow-up to `Packet-Encode-Allocation-2026-10-22.md` and `NPCSegment-Regex-2026-10-22.md`. After those, a 200-bot trace put the remaining ~1.5 MB/s at: the per-flush send array (31 %), `GetInfo` buff lists (~13 %) and the receive path (~7 %).

## Changes
1. **`MirConnection.Flush` rents the flush array** from `ArrayPool<byte>.Shared` when the flush is ≤ 64 KB (`PooledSendMax`). A larger flush, such as a login burst, still gets an exact-size array, so the pool never keeps big buffers.
   - **Length travels with the array:** `BeginSend(data, length, pooled)`, and the P9-09 outbox holds `OutChunk { Data, Length, Pooled }`. Every send uses `length`, never `data.Length`, because a rented array is larger.
   - **Each array is returned exactly once, and only when nothing can still read it:**
     - sender-thread path: `CompleteSend`, after the `SocketAsyncEventArgs` has let go of the buffers. It covers sync and async completion, partial sends (the stream is already counted as faulted), sends after a fault, and sends after close.
     - APM path (`SendThreads=0`): the `SendData` callback, after `EndSend`. The array is passed as the async state.
     - never sent: `QueueSend` returns the array at once when the connection is already faulted or over `MaxQueuedBytes`; `BeginSend` returns it when the connection is gone.
     - **left to the GC (never returned):** when `SendAsync` or the APM `BeginSend` throws, because then it is not certain whether a send is still in flight. These cases are rare.
   - **Disconnect:** queued chunks drain through `CloseWhenDrained`/`StartSend` (the closed state drops them through `CompleteSend`, which returns them). A chunk that is somehow stranded is only collected by the GC; it is never returned twice.
   - `SendData` now calls `EndSend` on `_socket`. It used `_client.Client`, which `Disconnect` nulls, so `EndSend` was silently skipped after a disconnect.
2. **`GetInfo` of Player, Monster and Hero** fills the packet's own `Buffs` list with `MapObject.AddVisibleBuffTypes(p.Buffs)`. That list is the one the packet's field initializer already creates. This replaces `GetVisibleBuffTypes()`, which made a second list. The list is sized once to the visible count, and no array is allocated when nothing visible is active, which covers most monsters. The wire layout of `S.ObjectPlayer`/`ObjectMonster`/`ObjectHero` is unchanged: same list, same order.
3. **Receive path (server only):**
   - `ReceiveData` parses the receive buffer in place with a new **`Packet.ReceivePacket(buffer, offset, count, out consumed)`** (Shared, additive; the client keeps the copying form).
   - Only a leftover partial frame is copied: it must outlive `_rawBytes`, which the next `BeginReceive` refills. Before, there was a new concat array per read and a copy of the rest of the buffer per packet.
   - The `AsyncCallback` is now one per connection instead of one per `BeginReceive`.
   - Behaviour is pinned by `Shared.Tests/PacketReceiveOffsetTests.cs`, which compares it with the copying loop: same packets, same leftover, unknown id drops the rest, malformed frame → `InvalidDataException`. A compressed packet (no client packet is compressed today) decodes a copy of its frame through the old method, so it reads the same bytes.

## Measurement
- Two Release `Server.exe` builds from a detached worktree at `661d57e7`: before = HEAD; after = HEAD + only this patch. Other sessions' uncommitted edits (including `Shared/Data/Stat.cs`) were kept out of both.
- Data: a copy of the 463-map live-data scratch folder, port 17130.
- Load: the same pre-change StressClient build, 200 bots, ramp 20/s, 170 s, seed 7.
- Trace: `dotnet-trace --profile gc-verbose` for 60 s, starting 60 s in.
- One run per side. Bot traffic was nearly equal: 12 980 vs 12 819 packets/s received on average.

| Run | alloc MB/s | `Flush` byte[] | `GetInfo` (Player+Monster+buff list) | receive (BeginReceive + ReceivePacket + ReceiveData) | GCs / 60 s | pauses ms |
|---|---|---|---|---|---|---|
| before | 0.93 | 0.32 MB/s (34.5 %) | 0.16 | 0.16 | 3 (gen0) | 6.6 / 7.9 / 9.0 |
| after | 0.55 | — (not sampled) | 0.11 | 0.12 | 2 (gen0) | 10.0 / 12.1 |

- **Total allocation −41 % at the same traffic.** The flush array disappeared from the trace, and the pool's misses are not visible either.
- **GetInfo:** the second `List<BuffType>` and the `BuffType[]` are gone. What is left is the packet object and its initializer list.
- **Receive:** the per-read and per-packet `byte[]` are gone. What is left is the APM `BeginReceive` Task wrapper (`ValueTaskSourceAsTask`, `TaskAsyncResult`, `AwaitTaskContinuation`, `Action`) and the per-packet `MemoryStream` + `BinaryReader`.
- **Pauses:** with 2–3 GCs a minute, single pause figures are noise. Promoted bytes per GC are unchanged (~1.4 MB). No gen1 or gen2 GCs in either run. Both runs PASS: no decode errors, RTT 2 ms. The after-run server logs contain no exceptions.
- **HasFlag:** the Release trace shows no `PoisonType`/`SpecialItemMode` boxing (`CanMove`/`ProcessBuffs`). Release JIT turns `HasFlag` into a bit test, as expected; that boxing only appears in Debug builds.

## What is left (≈0.55 MB/s at 200 bots)
1. `Packet.GetPacketBytes` frames (~27 %). They are shared across recipients by EncodeOnce, so this is the floor without pooling frames.
2. `S.ObjectPlayer`/`ObjectMonster` built per viewer in `HumanObject.Add`/`MonsterObject.Add` (~20 %). Sharing one info packet per pass would also let EncodeOnce encode it once. But `IntelligentCreatureObject.GetInfo` mutates the base packet, and callers enqueue it per viewer, so it needs a mutation audit first.
3. APM receive wrappers (~14 %). Moving `BeginReceive` to one reused `SocketAsyncEventArgs` per connection removes them. This is IOCP-thread work with its own sync-completion loop, so it was left out here.
4. `SMain.InterfaceTimer_Tick` (~10 %, WinForms host UI). This is not game-thread work.

Artifacts: `%TEMP%\mir2-alloctail\` (`before|after.nettrace`, `*-alloc.txt`, `*-bots.log`, `*-logs`, `measure.ps1`, `tail.patch`, `verify.log`).
