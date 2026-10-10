# X1-17 — guild rank authority: options for a decision (2026-10-10)

> Status: **waiting on your decision.** Each option below is a one-line guard change in `Server/MirObjects/GuildObject.cs` `ChangeRank` (~L264), with no DB or wire change. Every option is gameplay-visible for guild officers.

## What the code does today
Rank index 0 is the leader rank; higher index = less authority. The caller is `self` (rank `self.MyGuildRank.Index`), the target member is in `MemberRank`, and the new rank is `rankIndex`.

```csharp
if ((MemberRank.Index >= self.MyGuildRank.Index) && (self.MyGuildRank.Index != 0)) return false;
```

- A **leader** (index 0) may change anyone, to any rank. That is fine.
- An **officer** with `CanChangeRank` (index ≥ 1):
  - is **refused** for members at their own rank or below (higher index), which are exactly the people they should manage;
  - is **allowed** for members *above* them, including leaders, subject only to the "≥ 3 leaders and one other leader online" rule;
  - may move anyone **to any rank**, including rank 0 (promote a friend to leader), because `rankIndex` is never compared with the caller's rank.

Players can use this today: an officer can demote a leader, or make a friend a leader.

## Options

| | Rule for officers (leaders unchanged) | Guard | Effect |
|---|---|---|---|
| **A (recommended)** | Manage only members **below** you, and only move them to ranks **below** you | `if (self.MyGuildRank.Index != 0 && (MemberRank.Index <= self.MyGuildRank.Index \|\| rankIndex <= self.MyGuildRank.Index)) return false;` | The usual MMO rule. Officers can no longer touch leaders, peers or promote to their own rank. |
| B | Manage members below you, may promote **up to your own** rank | as A but `rankIndex < self.MyGuildRank.Index` | Officers can create more officers of their level. |
| C | Only leaders change ranks (ignore `CanChangeRank` for officers) | `if (self.MyGuildRank.Index != 0) return false;` | Simplest. Makes the `CanChangeRank` option useless for non-leaders. |
| D | Keep today's behaviour | — | Leaves the exploit. Not recommended before going public. |

## Side issue found while reading (same file)
`ChangeRankOption` (~L370) clears an option with `^=` (toggle) instead of `&= ~`. Disabling an option that is already off turns it **on**. The fix is the same one-liner whichever option you pick: `Options &= ~(GuildRankOptions)(1 << Option)`.

## After you decide
Tell me A/B/C/D. The change is about five lines plus a `// X1-17` comment, and I'll test it on the scratch server.
