# P9-10 — Account save off the game thread (2026-10-21)

**Status: implemented, one read-only review (persistence + threading) with no Critical or High findings. Its Medium (test gaps) and the diag Low are fixed. Measured on scratch servers with 2000 bots.** Roadmap P9 backlog item "Account save: snapshot on the game thread, then serialise and write off it".
- **No DB change:** Server.MirADB is byte-identical (Envir.Version stays 119).

## What the save cost
`[Diag] Save` (new, one Debug line per save with `[Diag] LogLoopProfile`) split the save block at 2000 bot accounts (5.7 MB):

| Step | ms (game thread) |
|---|---|
| accounts (FoldPlayTime + serialise + file open) | **38.95** |
| db (SystemData.db runtime counter post) | 0.59 |
| guilds / goods / conquests | 0.89 / 0.39 / 0.59 |

So the spike is the account save alone. An offline bench on a copy of that file showed where the time goes. Serialising into a pre-sized, reused buffer took **~5 ms**. The rest of the 39 ms is MemoryStream growth (copies up to 8 MB), the 5.7 MB `ToArray` copy and the LOH/GC that both cause. Of the 5 ms, 2 ms was the 1999 quest flags per character, written as 1999 `Write(bool)` calls. They are ~70 % of the file's bytes.

## Design
- **The snapshot is the serialised bytes.** A deep copy of the accounts (UserItems, Stats dictionaries, mail, quests) would allocate more and take longer than serialising them. And nothing cheaper is consistent:
  - **Dirty-only caching** saves nothing with bots: all 2000 accounts are online and change every save. Offline accounts would also need every offline-mutation path tracked (mail, friend removal, marriage/mentor ends, CleanUp, web admin edits).
  - **Spreading the serialisation over several ticks** breaks cross-account atomicity. A trade, a mail, a guild-storage move or a drop/pickup inside the window can dupe or lose an item in the file.
- **`[Optimisation] AccountSaveOffThread` (default True; web admin Settings › Optimisation):**
  - **Game thread:** `BeginSaveAccountsOffThread` serialises into one `MemoryStream` kept across saves (`_accountSaveBuffer`). That means no growth once it has reached the file size and no `ToArray`.
  - **Pool thread:** one `ThreadPool.UnsafeQueueUserWorkItem` (`WriteAccountsFile`) gets the buffer and its length, full paths, and the backup name. The name is taken from the game clock, as before.
  - **File steps:** the pool thread does every file step in the old order: live-file check, backup dir, `Server.MirADBn`, `File.Replace` with the `Back Up\Accounts` .bak. Failures are counted (`AccountSaveFailures`) and logged. `Saving` is cleared in `finally`.
- **Ownership:**
  - The game thread touches the buffer only while `Saving` is false. Every save already starts with `if (Saving) return;`.
  - The pool thread only reads the buffer, then clears the volatile `Saving`.
  - Shutdown `SaveAccounts()` still waits for `Saving` (30 s bound, unchanged).
- **Off = the old path, kept verbatim** (`BeginSaveAccountsLegacy`: a new stream, `ToArray`, the file opened on the game thread, an APM write, `EndSaveAccounts`).
- **`CharacterInfo.WriteFlags` (both paths):** writes the flags as one block. A vector `Min(byte, 1)` clamp gives the same bytes as `Write(bool)` for any bool bit pattern.
- **`SaveAccounts(stream, leaveOpen)`** uses BinaryWriter's default encoding explicitly (UTF-8, no BOM, throws on invalid surrogates).

## Verification
- **`Server.Persistence.Tests` (58/58):**
  - `AccountSaveOffThreadTests` checks that the legacy periodic save, the off-thread save (twice, so the reused buffer is reset and not appended to) and the sync save are byte-identical on the synthetic world with random flags.
  - It also checks four failure and edge paths:
    - an off-thread write failure: counted, `Saving` cleared, live file unchanged, recovers on the next save;
    - the shutdown save waits for an in-flight write;
    - a periodic save is skipped while a write is in flight;
    - the block flag write matches `Write(bool)` for lengths 0-5000 with non-canonical bools.
  - The committed v117/v118/v119 fixtures still re-save byte-identically.
- **Real-shaped data:** the 2000-account Server.MirADB written by the old code, loaded and re-saved by both new paths, is byte-identical.
- **`tools\verify.ps1`:** every build passes, and so do all tests: WebAdmin 1019, Shared 76×2, Persistence 58, MirDbExport 21, StressClient 32.

## Measurements
- **Setup:** 2000 bots (`--ramp 50`, 4 synthetic maps), `SaveDelay=1`, bots on the same desktop. The server is saturated here: 10-20 iterations/s, with most of the time in the connection/send phase.
- **Bench:** `BeginSaveAccounts` on a copy of the 2000-account file, 20 saves on an idle machine.

| Run | Game-thread save ms | Save-second max vs neighbours |
|---|---|---|
| Original code, heavy load (first run) | 38.95 | — |
| Original code (`cf7d8dc7`), 7 saves, lighter load | 21.3-25.9 | **48.1 vs 36.6-44.1; 61.7 vs 39.9-49.3**: the spike shows |
| P9-10 on, 5 steady saves, heavy load | **9.95-11.0** (first 19.96, JIT) | **76.8 vs 77.8-98.4; 99.2 vs 84.0-101.0**: no spike |
| Bench, legacy path + block flags | med 9.4-9.8 (min 4.6) | — |
| Bench, P9-10 on | **med 2.9-3.2 (min 2.7)** | — |

- **After run, per-second worst iteration** (17:28-17:34): median 85 ms, p99 118 ms. The save seconds sit inside that range.
- **What stays on the game thread:** pure serialisation, about 1.4 µs per account idle and ~5 µs under this load (CPU shared with 2000 bots). It grows linearly with accounts, offline ones included.

## Known limits / follow-ups
- **The serialisation itself stays on the game thread** (consistency, see Design). A real server with tens of thousands of mostly offline accounts would need an offline-account blob cache. That needs the offline-mutation audit first.
- **Buffer size:** the buffer keeps its grown capacity, up to about 2× the file size, for the life of the process (intended).
- **After the 30 s shutdown bound** the sync save can still race the pool write on `Server.MirADBn`. This is unchanged from the legacy path.
- **`[Diag] Save` fields:** `accounts=` includes `FoldPlayTime`. A save skipped because the previous write is still running is tagged `skipped`.
