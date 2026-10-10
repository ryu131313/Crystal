# Overnight run log — 2026-10-10

> Autonomous run through `roadmap.md` while the user slept. The user's instructions:
> - work through the suggested order;
> - document important decisions;
> - keep testing efficient;
> - skip and document anything stuck, or anything with 3+ test failures on the same piece of work.
>
> **Nothing here was deployed.** Agents never touch `Build\`. Every "live" gate in the roadmap (watch windows, deploys, operator actions) is listed under **Pending you**.

## Pending you (operator actions and decisions)
- P0 operator mitigations: the GM password, an off-box copy, pruning `Back Up\` (keep 48 h). Checklist in `Knowledgebase/Runbook.md` §5.
- A scratch copy of the live server data (decision #4). The harness has been run on synthetic data only. See P1.
- Every deploy and watch window: P2, P3, P4 in order, one per deploy (roadmap §6).
- Decisions still open: #3 (move build output off `Build\`), #5 (deploy window and backup location), #7, #8, #10, #11. Where I needed an answer to keep going, I used the roadmap's recommendation and recorded it below.

## Decisions made
| # | Decision | Why |
|---|---|---|
| D1 | `Directory.Build.props` defines `ServerTfm`, `ServerWinTfm`, `SharedTfm`, `ClientTfm`, `ToolsTfm`, plus `LangVersion 12`. Every `.csproj` uses them. | Roadmap P0. Verified that every project's `OutputPath` is unchanged. |
| D2 | `tools/verify.ps1` builds each project to `%TEMP%\mir2-verify\<key>`. | Full build of 8 projects plus tests takes ~21 s. Build and Components were confirmed untouched afterwards. |
| D3 | `tools/deploy-with-backup.ps1` deletes only files listed in the previous deploy's manifest, skips suspicious entries, and protects data paths. | Tested on fake folders in scratch, never against `Build\`. |
| D4 | Decision #3 (move build output off `Build\`) **not done**. It is still open and changes your VS F5 folder. | Your call. Until then, `verify.ps1` is the safe path. |

## Progress by phase
### P0 — done (code parts)
- `Directory.Build.props`, `tools/verify.ps1`, `tools/deploy-with-backup.ps1`, `Knowledgebase/Runbook.md`.
- The AGENTS.md build rules were updated.
- The operator parts are pending you (see above).
