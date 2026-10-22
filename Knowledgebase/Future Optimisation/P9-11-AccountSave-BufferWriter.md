# P9-11 — Account save: a BinaryWriter straight into a byte[] (2026-10-22)

**Status: implemented on branch `acct-save-writer` (not merged).** Phase 2 of the lean plan in `Future Ideas/Offline-Account-Cache-Plan.md`. Follows P9-10 (`P9-10-AccountSave-OffThread.md`).
- **No DB change:** Server.MirADB is byte-identical (no `Envir.Version` bump, no `Save` signature change).

## Change
- **`Server/MirEnvir/Storage/BufferBinaryWriter.cs`:** a sealed `BinaryWriter` that owns a growable `byte[]`.
  - It overrides `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `byte[]`, `ReadOnlySpan<byte>` and `string`. These write into the array with an unaligned store (little-endian, like `BinaryPrimitives`), not through a Stream call.
  - `string` uses the same encoding as before (`AccountSaveEncoding`: UTF-8, no BOM, throws on invalid surrogates) and the same 7-bit length prefix of the byte count. Strings of up to 42 chars are encoded in place behind a 1-byte prefix, as `BinaryWriter` does. An invalid string throws `EncoderFallbackException` before any byte counts as written.
  - Every other base method (`char`, `char[]`, `decimal`, `Half`, `Write7BitEncodedInt`) writes through `OutStream`, which is an append view of the same array. So every overload gives the same bytes as `BinaryWriter` over a `MemoryStream`. `Seek` throws (append only), and `Flush` does nothing.
  - `Reset()` keeps the array, which never shrinks. It doubles when full. `Envir` sizes it from the live file plus 1/8 on the first save, so it normally never grows.
- **`Envir.BeginSaveAccountsOffThread`:** uses it (`_accountSaveWriter`) and hands `writer.Buffer` + `writer.Length` to the pool-thread job. There is still no `ToArray`. The body of `SaveAccounts` moved into `SaveAccounts(BinaryWriter)`. The stream overload (legacy periodic save, sync shutdown save) wraps it unchanged. The P9-10 ownership rule stays: the game thread touches the writer only while `Saving` is false.
- **`CharacterInfo.WriteFlags`:** with a `BufferBinaryWriter` it clamps the 1999 flags straight into the reserved bytes. Before, it made a stack copy and then a `Write(span)`. Other writers take the old path.

## Verification
- **`Server.Persistence.Tests` 64/64:**
  - New `BufferBinaryWriterTests`:
    - Every overload (non-canonical bools, NaN floats, decimal, Half, char/char[], 7-bit ints) on random values against `BinaryWriter(MemoryStream, AccountSaveEncoding)`.
    - Strings: empty, non-ASCII, surrogate pairs, the 42/43-char in-place boundary, and 1/2/3/4-byte length prefixes (127/128, 16383/16384, 2097151/2097152 bytes), plus 300 random strings up to 40k chars.
    - Invalid surrogates throw the same exception type and write nothing.
    - Reset keeps the array.
  - `AccountSaveOffThreadTests`: legacy = off-thread (twice) = sync, as before. Added: a save that grows the buffer past its file-sized capacity (a 300 KB string), then a smaller save that reuses the grown array. The block flag write is also checked through the new writer.
- **`tools\verify.ps1`** (ServerLibrary, Server, MirDbExport, ScratchWorld, StressClient, WebRelay, all tests): green. WebAdmin 1030, Shared 88×2, Persistence 64, MirDbExport 21, StressClient 32.

## Measurements
- **Data:** a copy of the live `Server.MirADB` (2026-10-22 10:18; 11,812 accounts, 11,730 characters, 5 heroes, 37.59 MB) with the live `Server.MirDB`.
- **Method:** `BufferBinaryWriterTests.Bench_real_accounts_old_vs_new`, Release, idle desktop. Set `MIR2_ACCOUNT_SAVE_BENCH=<folder>`, and optionally `MIR2_ACCOUNT_SAVE_BENCH_ROUNDS`. It times only the game-thread serialisation:
  - old = the P9-10 path (reused `MemoryStream` + `BinaryWriter`);
  - new = `BufferBinaryWriter`.
- **Runs:** 30 rounds after 2 warm-ups. The two paths alternate order each round, so each run starts with the other's 37 MB evicted from cache (closer to a live game thread). There were 3 runs.

| Path | Median ms | Min ms |
|---|---|---|
| Old (P9-10) | 19.0 / 20.7 / 22.1 | 17.4 / 19.1 / 20.6 |
| New | **16.1 / 17.0 / 17.1** | **14.4 / 14.7 / 14.9** |

- **Bytes:** identical: SHA-256 `808AD0BC…1EBD5C` for both, 37,592,849 bytes.
- **Warm-cache best of 8** (new writer only, one-off probe):
  - all accounts 11.8 ms, of which characters take 8.2 ms;
  - the flag blocks alone 3.2 ms;
  - unresolved-friend lookups 0.08 ms (only 4 friends).
- **What is left is memory traffic, not call overhead.** The pass reads the whole object graph (11,730 × 2 KB of flags, 563k inventory slots, 76k items, 115k magics) and writes 37.6 MB. A 37 MB copy alone costs ~4 ms on this machine. Per-call savings therefore top out at ~20-25 % (~1.2-1.4 µs → ~1.0 µs per account, idle).
- **Gate (plan):** this does **not** bring the live-sized save to ≤ ~10 ms. Expect roughly 25-56 ms → 20-45 ms under live load, proportionally. Getting below that needs fewer bytes serialised per save, which is phase 3, the offline account cache.

## Risks / notes
- **The writer must never be disposed while it is reused.** `Envir` never disposes it. A disposed `BinaryWriter` would only stop base-method writes; the overridden ones would keep working.
- **Big-endian hosts** take the `BinaryPrimitives` branch (same bytes); untested, since there are none.
- **The buffer is ~1.125× the file** (42 MB at 37.6 MB), kept for the life of the process. Before, P9-10's `MemoryStream` held up to ~2×.
