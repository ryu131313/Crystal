# NPCSegment regexes built once (2026-10-22)

**Change:** `Server/MirObjects/NPC/NPCSegment.cs` built a `new Regex(...)` on every call of `ParseArguments`, `AddVariable`, `FindVariable`, `ParseCheck`, `ParseAct` and both `ReplaceValue` overloads, and used static `Regex.Match(x, pattern, IgnoreCase)` in `ParseAct`. Every pattern is now a `private static readonly Regex` with the same pattern and options. The runtime ones (`ReplaceValue`, `FindVariable`, `AddVariable`) add `RegexOptions.Compiled` (`Compiled` changes speed, not matching). The parse-time ones (`ParseArguments`, quote, flag, `[A-Z][0-9]` IgnoreCase) stay interpreted, since they run only at script load. `ReplaceValue` reuses its first `varMatch`/`twoValMatch`/`oneValMatch` instead of matching each pattern a second time in the `if` chain. Match order and results are unchanged.

**Why:** an allocation trace of 200 bots using merchants and quests (2026-10-22) put `ReplaceValue` (from `ParseSay`/checks/acts) at ~15% of all server allocation. `ParseSay` calls `ReplaceValue` once per **word** of every say line, and the old code built 4 regexes (pattern parse, RegexNode tree, FindOptimizations) per word.

## Per call (microbenchmark, .NET 10 Release, `GC.GetAllocatedBytesForCurrentThread`)

The `ReplaceValue` regex work only, without the player lookups. The old and new versions give identical results for each input.

| Word | Old B/call | New B/call | Old µs | New µs |
|---|---|---|---|---|
| `Welcome,` (plain word, the common case) | 11,360 | 0 | 10.4 | 0.04 |
| `<$USERNAME>` | 15,603 | 464 | 23.7 | 0.46 |
| `<$OUTPUT(D0)>` | 16,467 | 1,424 | 7.8 | 0.68 |
| `<$MONSTERCOUNT(0)>` | 16,504 | 1,216 | 16.3 | 1.12 |

Times are noisy (another build was running), but the allocation numbers are exact. The scratch merchant's `[@MAIN]` page has about 10 words, so each open allocated about 115 KB before and 0 B now. Real scripts with longer pages allocated several hundred KB per click.

## Whole server (tools/run-baseline.ps1, 200 bots, 90 s, synthetic map, scratch only)

| Run | alloc MB/s | CPU % (1 core) | busy | NPC calls / pages | Result |
|---|---|---|---|---|---|
| Before (`%TEMP%\mir2-regex-before`) | 1.08 | 6.8 | 4.72 | 129 / 86 | PASS |
| After (`%TEMP%\mir2-regex-after`) | 1.26 | 4.7 | 3.99 | 120 / 80 | PASS |

The scratch bots open only about one NPC page per second, so this change is lost in run-to-run noise here: the after run sent twice the packets (pkts out/s 18.5k → 37.1k, more quest hand-ins). The whole-server rows only show that nothing broke (0 errors, 0 decode errors, PASS). The per-call table is the measurement. To see the ~15% share disappear, repeat the allocation trace on the live-data bot run that produced it.
