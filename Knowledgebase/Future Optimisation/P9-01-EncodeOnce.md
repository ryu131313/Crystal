# P9-01 — Encode shared packets once per connection pass (2026-10-10)

**Change:** `MirConnection.Process` encodes each outgoing packet through a game-thread-only, per-pass cache keyed by instance (`Dictionary<Packet, byte[]>`, reference equality). `Envir.WorkLoop` clears it after every connection pass, and so does the shutdown drain. A packet broadcast to N players is encoded once instead of N times. Server-only: no `Shared` change, no client patch. Switch: `[Optimisation] EncodeOnce` (default True; also on the web admin settings page).

**Safety:**
- `Future Ideas/Packet-Mutation-Audit.md` found no server code that mutates a packet after its first send. Encoding is lazy and on the game thread (never in `Enqueue`, which mob threads call), as the audit requires.
- The audit's "frozen packet" condition needs a `Shared` change, so it waits for C5. Until then a sampled guard re-encodes 1 cache hit in 256 and logs `[Optimisation] EncodeOnce: <Packet> changed after its first send` once per packet type. Bot runs logged 0 such lines.
- The audit's golden tests for guild fan-out and observers are **not written**. The guard and the kill switch cover that gap for now.
- An independent read-only review found no correctness issues. Its Lows: the shutdown drain has no try/catch (existing behaviour), and the dictionary capacity never shrinks after a burst.

**Measurements:** 500 bots on one map, scratch server, `tools/run-baseline.ps1 -Bots 500 -NoIdle [-ExtraIni '[Optimisation]','EncodeOnce=False']`, 60 s windows. Bots share the machine, and **the desktop was busy during pair 2** (two msedge processes used ~3.7 cores).

| Run | CPU % (1 core) | busy | ms/s conn | alloc MB/s | max iter ms | KB out/s | decode errors |
|---|---|---|---|---|---|---|---|
| pair 1, off | 32.5 | 42.63 | 302.56 | 135.93 | 62.926 | 2121 | 0 |
| pair 1, on | 20.5 | 32.83 | 191.41 | 48.01 | 35.067 | 2099.9 | 0 |
| on + guard | 44.1 | 52.37 | 393.59 | 44.25 | 80.838 | 2263.6 | 0 |
| pair 2, off (noisy) | 47.9 | 55.59 | 426.58 | 134.73 | 125.285 | 2210 | 0 |
| pair 2, on (noisy) | 53.6 | 60.89 | 479.68 | 42.75 | 71.595 | 1961 | 0 |

**Conclusion:**
- **Proven:** allocation is down ~3x (135 → 45 MB/s), and the worst iteration is lower in both pairs. Fewer GCs means fewer long iterations. Output is byte-identical: 0 decode errors over 500 bots × 5 runs.
- **Not proven:** the CPU/connection-time gain. Pair 1 was −37 %; pair 2 was within noise, the wrong way. **Rerun both variants on a quiet machine** before claiming a CPU number.
- `conn` also includes socket `BeginSend` calls (one per connection per pass), so it is sensitive to machine contention.

**Next P9 candidates from the same numbers:**
- `obj` (~120 ms/s at 500 bots) is now the second-largest slice.
- The per-connection `List<byte>` + `ToArray` send buffer.
- Pooled `GetPacketBytes` buffers, which are in `Shared` and wait for C5.
