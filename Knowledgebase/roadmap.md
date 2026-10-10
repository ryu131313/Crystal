# Crystal Modernisation Roadmap

> **Purpose:** the high-level plan for what we change in Crystal and **in what order**: .NET 10, SQLite, server optimisation, the client upgrade (DX11), plus the work that has to sit around them. This file only sequences the work. The mechanics, traps and evidence for each phase are in [`Future Ideas/Roadmap-Phase-Notes.md`](Future%20Ideas/Roadmap-Phase-Notes.md). When a phase starts it gets its own design note in `Future Ideas/`; when it ships, that note moves to `Archived and Completed/`.
> **Status:** proposed 2026-10-08; rule overrides approved 2026-10-09. Nothing has started. Decisions #3, #5 and #7–#11 in §9 are still open.
> **Basis:** a code survey of `FuckingAround` (`5b6145f2`) and the WoOOLcraft history (its `src` git log, 285 commits from 2026-06-06 to 10-08, plus its Knowledgebase). The plan was checked by a design panel and an adversarial fact-check against both codebases. WoOOLcraft is used as precedent only.
> **Rules still apply:** `AGENTS.md` and the child docs govern every phase. A phase that fixes Known debt still needs your go-ahead when it starts (AGENTS.md directive 5). Every place where this plan overrides a NEVER/ALWAYS rule is marked ⚠ and listed in §8.

---

## 1. The short answer

Your four items are the right four, and nearly in the right order. Here is what changes and what gets added.

1. **Before .NET 10: groundwork, a test harness and two small live-risk fixes.**
   - **Crash-safe saves.** Today a failed account save can leave the server with no `Server.MirADB`, and the next start then quietly creates an empty one.
   - **BinaryFormatter removed from buff saving.** On .NET 9+ it throws, so any stored Archer Concentration buff would stop the world from loading accounts.

   .NET 8 support ends on **2026-11-10**, so this chain runs on a 33-day clock. Only the server moves to .NET 10 then. The client follows in the C5 flag-day (~Jan 2027), because it still uses SlimDX.
2. **A foundations stage sits between .NET 10 and the big work.**
   - **Crash isolation:** today one bad packet stops the world.
   - **Measurement:** a loop profiler, a stress client and a baseline. WoOOLcraft's first optimisation pass was symptom-driven. Its first load-measured pass, months later, found a different set of hotspots.
3. **SQLite covers system data first (`Server.MirDB`), with a single writer.**
   - Accounts, guilds and conquests stay binary until measurements say otherwise. WoOOLcraft never moved its player DB either.
   - Optimisation runs during SQLite's long burn-in.
4. **The client upgrade is a parallel track, not step 4.**
   - The target is **Direct3D 11 via Vortice**, not a D3D9 rebind. Crystal uses D3DX for sprites, lines, shaders and screenshots, and Vortice has no D3DX, so a rebind would need most of the same work anyway.
   - It ends in one **flag-day** release where every player updates once: DX11, no SlimDX, the client on .NET 10, and a real version handshake.
5. **Anything else? Yes, and most of it comes early.**
   - Data safety (P0/P2).
   - Test harnesses (P1, C1).
   - Crash isolation (P5).
   - Security quick wins: a public default GM password that is logged before it is checked, and pre-login allocation bugs (P0/P2, then P8).
   - Build and deploy safety. Today a plain build writes into the live server folder (P0).
   - Launcher integrity and runtime detection (C2).
   - Backup retention: up to 576 `.bak` files are written per day and never pruned (P0/P5).

```
Stage 1 · deadline .NET 8 EOL 2026-11-10
  P0 groundwork ─► P1 harness ─► P2 crash-safe saves ─► P3 BinaryFormatter out (DB v118) ─► P4 server on .NET 10

Stage 2 · foundations
  P5 safety net (isolation, save windows, data defects, tools → net10, CI) ─► P6 measurement (profiler, idle loop, StressClient, baseline)

Stage 3 · the big three (one active at a time; see §4)
  P7 SQLite system data   ──►  burn-in weeks are filled by P9
  P9 measured optimisation
  P8 security hardening   (moves up to right after P5 if the server is public)

Client track · parallel from week 1
  C1 fixes + render goldens ─► C2 launcher release ─► C3 render seam (D3D9) ─► C4 D3D11 behind a toggle ─► C5 flag-day ─► C6 polish
                                                                                                             ▲
                                     C5 also needs: P4 (server net10), P5 (tools net10), P6 (StressClient) ──┘
```

| Phase | What | Size | Exit gate (short) | Target / needs |
|---|---|---|---|---|
| **P0** | Groundwork: operator mitigations, KB in git, safe builds, runbook, hand-off pack | S | GM password changed; off-box snapshot taken; `verify.ps1` builds to scratch and refuses `Build\` | 2026-10-10 |
| **P1** | Harness: DB round trip and oracle, packet tests; read-only security review starts | M | Second save byte-identical; oracle reproducible on a copy of live data | 2026-10-15 |
| **P2** | Crash-safe saves (no format change) | S | Fault injection never loses or empties a DB; 48 h clean live | live 2026-10-17 |
| **P3** | BinaryFormatter out, `Envir.Version` 118, still on .NET 8 | S | Oracle green on live data and older backups; 72 h clean live | live 2026-10-24 |
| **P4** | Server side on .NET 10 (`Shared` stays net8) | S–M | All suites green; every editor opens; 7 clean days | live 2026-10-31 (EOL 11-10, slip limit 12-08) |
| **P5** | Safety net, tools on net10, CI | M | A throwing handler, monster or mob thread no longer stops the world | after P4 |
| **P6** | Measurement: profiler, idle park, load knobs, StressClient, baseline | L | Idle under 5 % of a core; 500 bots for 60 min with 0 world stops | after P5 |
| **P7** | SQLite for system data, single writer | L | Verifier 0 mismatches; ≥1 week shadow; ≥4 weeks burn-in; binary path deleted | after P6 |
| **P8** | Security hardening | M | Criticals and Highs closed; flood test passes; login storm causes no hitch | after P5 (bot-tuned items, hash v2 and the gate's bot checks after P6) |
| **P9** | Measured server optimisation | L | Every change has before/after numbers; target met | after P6 |
| **C1** | Client crash fixes, D3D9 golden images, frame baseline | M | Goldens identical over 3 runs | Oct |
| **C2** | Launcher release (ships the C1 fixes) | M | Old launcher self-updates; add, remove and downgrade proven | Nov |
| **C3** | Render seam on D3D9 | M | Goldens byte-identical; in-game QA | Nov |
| **C4** | D3D11 via Vortice behind a toggle | L (M if WoOOLcraft code is reused) | Golden parity; QA checklist; one fallback cycle | Dec |
| **C5** | Flag-day: SlimDX gone, client and `Shared` on .NET 10, handshake, batched `Shared` fixes | M (+L if TLS) | Old client gets "update required"; clean-VM install works | ~Jan 2027 |
| **C6** | Polish: borderless/DPI, redraw cost, texture streaming, widescreen (after P9) | M–L | Each item gated by goldens or QA | after C5 |

**Sizes** are coding time with agents: **S** is one focused session, **M** a few sessions, **L** about a week. Calendar time is mostly set by watch windows, QA and decisions. **Stage 1 dates** assume a Saturday deploy window; shift them to yours.

---

## 2. Principles (why this order)

1. **Server runtime first.**
   - WoOOLcraft moved to .NET 10 last, because it started on .NET Framework 4.8. It paid for that with a hand-written JavaScriptSerializer shim, and its tuning was measured on a runtime it then left.
   - Crystal is already SDK-style net8. Once BinaryFormatter is gone, the bump is about the size of the 9-file .NET 7→8 change (upstream `acfca12f`, #870).
   - Everything after P4 is written and measured on the final runtime.
2. **Harness before change.** That means the DB harness before any persistence change, packet tests before any `Shared/` change, and goldens before any render change.
   - WoOOLcraft had no server tests for four months. A review one month in found 13 Critical and 24 High issues.
   - Its DX11 rewrite only landed cleanly (about 24 h of commits) once 26 byte-exact goldens gated each phase. An earlier, uncommitted attempt had broken sprite ordering and blending.
3. **Measure before optimising,** in this order: profiler → idle loop → load knobs → StressClient → baseline.
   - Crystal's `WorkLoop` never sleeps, and the mob barrier spins about 10 ms per pass. Idle CPU is about 2 cores, and today's coarse counters can't attribute time to anything.
   - WoOOLcraft's 1,000-bot pass took one map from 88 % to 36 % busy only after these existed.
4. **One writer for game data, decided before more editing is built on it.**
   - WoOOLcraft built admin editing on an editor copy mirrored into the live world. That design caused a live-row re-pointing bug, even after it moved to SQLite.
   - Only a single-writer engine removed the bug: about 25k lines plus 26k lines of tests, and both paths are still maintained.
   - Crystal's `Envir.Edit`/`Envir.Main` split has the same shape. So there are no new MirCraft game-data editing features until P7.
5. **One risk class per deploy.**
   - A DB format change, a runtime change, the SQLite flip and the client flag-day never share a deploy. The flip and the flag-day never share a week.
   - `v118` is a one-way door.
6. **Players take one manual step, once.**
   - Everything that needs a manual player step (a new runtime or prerequisite) goes into C5.
   - Later `Shared` changes (P9 items, C6 `DataRange`) still ship client and server from one commit, as normal launcher patches.
   - The launcher lives inside `Client.exe`, so runtime detection has to reach players one release earlier (C2).
7. **No permanent dual paths.**
   - Binary/SQLite and D3D9/D3D11 each get one burn-in or fallback cycle, then the old path is deleted. Only a standalone importer for old backups survives.
   - WoOOLcraft still carries binary, SQLite, a JSON sidecar, and LiveDb on and off.
8. **The live folder is sacred, and the plan lives in git.**
   - Agents never build into `Build\` or deploy. Deploys are user-run and back up first.
   - `Knowledgebase/`, `AGENTS.md` and `tools/` go into git. One phase is one reviewed commit series on `FuckingAround`, and §11 is updated in the same commit.
   - WoOOLcraft's verification tools and web admin `wwwroot` live outside git with no history. Its .NET 10 port sits uncommitted across 61 files, and its index still claims SlimDX.
9. **Your hours are the bottleneck, not agent throughput.**
   - At most one server phase and one client phase are in active implementation at a time. Agent-only background work doesn't count.
   - Phases that edit `Envir.cs` or `MirConnection.cs` never have implementation in flight at the same time. P9 may fill P7's waits, but P7's remaining steps land between P9 changes, never alongside one.
   - There is one fixed weekly deploy window. Gameplay work can continue, through the same gates.

---

## 3. Starting point: Crystal today vs WoOOLcraft at the start

| | Crystal (2026-10-08) | WoOOLcraft (2026-06-06) | What it means here |
|---|---|---|---|
| Runtime | net8 everywhere, SDK-style | Server on .NET Framework 4.8, client net8, tools net9 | .NET 10 is cheap here, so it goes first |
| Wire assembly | One `Shared.dll` for both sides | Mirrored Library/LibraryCore, which drifted | Keep it single; packet tests are enough |
| Persistence | Hand-written binary, 111 version gates, saves on the game thread, no tests | Zircon DBLibrary (reflective) | The SQLite exporter is more work here. Live edits reaching clients are easier, because Crystal clients hold no game DB |
| Renderer | SlimDX D3D9: 16 files reference SlimDX (8 make raw D3D9 calls), 5 blend recipes, 1 live shader | SlimDX D3D9: ~60 files reference SlimDX, 10 blend recipes, 3 shaders, BC textures | WoOOLcraft's DX11 plan ports almost directly, and the job is about 3–4× smaller |
| Audio | NAudio | DirectSound via SlimDX | No audio work in the SlimDX exit |
| Server loop | Busy spin plus a ~10 ms mob barrier. Only coarse counters (iterations/s, cycle delay) | Similar spin | The idle park is both a fix and what makes measurement possible |
| Robustness | Any handler exception stops the world; a mob-thread exception hangs it; no crash handlers | Same pattern (its review's #1 systemic risk) | Isolation before load testing |
| Load testing | None. Per-IP defences silently stop a local bot fleet | None until day 6 | StressClient and load knobs are new work |
| Admin | MirCraft web admin: process-lifetime, independent of the world | Built later | A headless host is cheap when wanted |
| Security | Plaintext passwords on the wire; PBKDF2-SHA1 with 50 iterations; a shared GM password with a public default, logged before it is checked | Partly similar (PBKDF2-SHA1 with 1,354 iterations, no shared GM password) | Quick wins in P0/P2, the real work in P8/C5 |

---

## 4. Server phases

### Stage 1 — before .NET 8 end of support (P0–P4)

**Hard date:** 2026-11-10, when .NET 8 support ends (also a Patch Tuesday). **Target:** live by 2026-10-31.

**Slip rule:** if a gate is red, stay on net8 and ship once green, before the first unpatched Patch Tuesday (2026-12-08). A few weeks unpatched behind a private server is a smaller risk than an account-DB format bug. Gates are never compressed or bundled to hit the date.

**P0 — Groundwork and operator mitigations** · S · by 2026-10-10
- **Operator actions, today, no code:**
  - Change `GMPassword` from the default `C#Mir 4.0` via the web admin Config page, then restart.
  - Take an off-box snapshot with the world stopped.
  - Check disk space and prune `Back Up\`, but keep the last 48 h until P2 ships.
  - Restart the whole process instead of any in-process world restart (that path leaves guilds stale).
  - Use one editor surface at a time.
- **Repo:**
  - Commit `Knowledgebase/`, `AGENTS.md` and `tools/` after a sensitivity check.
  - Add `tools/verify.ps1`: scratch builds only, and it refuses `Build\`.
  - Add a user-run, manifest-based `deploy-with-backup.ps1` that never touches data.
  - Add `Directory.Build.props` with central TFMs.
  - Write the runbook and do one restore drill.
- **Hand-off pack:**
  - a scratch copy of live data;
  - the client shaders (not in git);
  - your weekly hours;
  - answers to the §9 decisions.
- **Freeze:** no new MirCraft game-data editing until P7, and no new client UI or effects on D3D9 until C4.
- **Gate:**
  - The GM password is changed and the snapshot exists.
  - `verify.ps1` builds Server.Library, Server.exe, Client and LibraryEditor to scratch, and leaves `Build\` untouched.

**P1 — Minimum verification harness** · M · by 2026-10-15
- **On the deadline path:**
  - `Server.Persistence.Tests`: a byte round trip of `Server.MirDB` and `Server.MirADB`, plus a semantic **oracle** (a JSON dump of every collection), both run on a copy of live data.
  - A report of what the first load silently drops.
- **Gates P4 and any `Shared/` change:** `Shared.Tests`, covering:
  - every packet has a factory case;
  - read and write are symmetric;
  - the ID enums are append-only against a manifest;
  - it runs on net8 and net10.
- **Agent background work:**
  - the full oracle (guilds, goods, conquests);
  - the **X1** systematic security and crash review, which includes the MirCraft web admin routes;
  - the packet-mutation audit.
- **Gate:** the second save is byte-identical to the first, the oracle is reproducible, and `Shared.Tests` is green on both TFMs (known gaps are expected failures).

**P2 — Crash-safe saves, no format change** · S · live 2026-10-17 · Known debt
- **Today's failure paths:**
  - `BeginSaveAccounts` moves the live accounts file to `Back Up\` *before* it serialises.
  - A serialisation throw stops the world and hangs shutdown.
  - A failed write is swallowed silently.
  - Either way, a restart without `Server.MirADB` makes `LoadAccounts` write and load an empty one. `SaveDB` also truncates `Server.MirDB` in place.
- **Fix:**
  - Write the new file, then `File.Replace`, so a live file always exists.
  - Reset `Saving` in a `finally`, and log save errors.
  - Make both loads fail closed instead of creating an empty DB.
  - `LoadAccounts` refuses newer versions.
  - Add crash handlers.
  - Stop logging the GM password.
- **Gate:**
  - Fault injection at every save step never loses or empties a DB.
  - A v118 file is refused.
  - 48 h clean live.
- **Rollback:** the previous binaries.

**P3 — BinaryFormatter out, `Envir.Version` 117 → 118, on .NET 8** · S · live 2026-10-24
- **What:**
  - A typed encoding for `Buff.Data`, with a gated legacy branch that never needs BinaryFormatter.
  - It is server-only: the `Shared` helpers stay until C5.
- **Shipped alone on net8,** so a v118 problem can never be confused with a runtime problem. This build is P4's rollback.
- **Gate:**
  - The oracle is equal on live data and on older backups, apart from the documented Buff fields.
  - The P2 build refuses the v118 file.
  - 72 h clean live.
- **Rollback:** restore the pre-deploy snapshot with the P2 binaries. This loses everything written since the deploy.

**P4 — Server side to .NET 10** · S–M · live 2026-10-31; .NET 8 EOL 2026-11-10, slip limit 2026-12-08 · ⚠ retarget
- **What:**
  - Server.Library, Server.exe and the server tests move to `net10.0`/`net10.0-windows7.0`.
  - `Shared` stays `net8.0` so the net8 client needs no patch. `LangVersion` stays 12, and `Crypto.cs` is untouched.
- **Scratch validation:**
  - Every suite passes, and net8 and net10 saves are byte-identical.
  - End to end with the net8 client.
  - Every WinForms form opens and saves. This matters because the main SMain window itself uses the prebuilt .NET Framework `CustomFormControl.dll`, and VisualMapInfo uses VB PowerPacks (a pre-scoped contingency exists).
- **Host:** the .NET 10 runtime is installed side by side, and .NET 8 stays through the rollback window.
- **Gate:** green suites, a signed-off checklist, and 7 clean days.
- **Rollback:** the P3 net8 binaries (same data format).

### Stage 2 — foundations (P5–P6)

**P5 — Safety net, tools to .NET 10, CI** · M · Known debt · ⚠ retarget (tools)
- **World-loop isolation:**
  - Per connection: a bad packet disconnects that client only.
  - Per object and map: the failing object is logged and removed.
  - Mob threads survive an exception instead of hanging the world.
  - ObjectIDs become atomic.
- **Save windows and defects:**
  - Fix the guild resave that deletes every `.mgd` first.
  - Fix stale guilds after an in-process restart.
  - Add backup and log retention.
  - Decide what happens to zero-character accounts and `@LOGIN`.
- **Tools:**
  - The patching and asset tools move to net10; LibraryEditor references `Shared`, so this must happen before C5.
  - Delete the unused legacy HTTP endpoints.
  - Add CI on a Windows runner.
- **Gate:**
  - Fault injection: a throwing handler, monster or mob thread leaves the world ticking.
  - The full oracle is green.
  - 7 clean days.

**P6 — Measurement and load infrastructure** · L · Known debt · ⚠ game-thread wait · ⚠ WoOOLcraft reuse
- **Loop profiler and health endpoint:** per-phase ms, packets, queues, GC and save ms. It is gated, allocation-free and on the dashboard.
- **Idle park:** at most a 1 ms wake-on-work wait, plus a mob-barrier rework.
- **Load knobs** (scratch-only, default off): per-IP exemptions for loopback, with `MaxPacket` kept but raised and per-connection queues capped.
- **StressClient MVP** on `Shared`, plus a baseline matrix and a regression script.
- **Gate:**
  - An idle scratch server uses under 5 % of one core (about 200 % today).
  - The profiler attributes ≥95 % of loop time.
  - 500 bots run for 60 min with 0 world stops.
  - The baseline is committed.

### Stage 3 — the big three (P7–P9)

**Order:** P7 starts first. Its waits (≥1 week of shadow, ≥4 weeks of burn-in) are filled by P9.
- P9 waits on two things only: its save-I/O item waits for the P7 flip, and its `Shared`-touching items wait for C5.
- P8's bot-tuned items, the bot checks in its gate, and hash v2 (gated on a login storm) need the P6 StressClient. Its other items (X1 fixes, length checks, off-thread hashing) need only P5 and X1. The server will face the internet eventually, so **P8 and C5's TLS are the go-public gate**: the server doesn't open to the public until both have shipped. If going public gets closer, P8's non-bot items move to right after P5.

**P7 — SQLite for system data (`Server.MirDB`)** · L · Known debt · ⚠ new-field rule
1. **Exporter.** A read-only exporter plus an **independent** verifier, run as agent work from P4 onward.
2. **Storage seam.** A storage seam extracted from `Envir.cs`, shipped as its own deploy.
3. **Shadow mode.** At least 1 week on live, with the diff run off the game thread.
4. **Switch.** `Microsoft.Data.Sqlite` becomes the store of record behind a flag. One writer thread owns the file, so `Main` no longer saves design data and `SystemDataStale` retires. Backups use `VACUUM INTO`.
5. **Burn-in.** At least 4 weeks, with a rollback drill. Then delete the binary writer, keeping a standalone importer for old backups.

Other details:
- **Scope:** accounts, guilds, goods and conquests stay binary. NPC scripts, drops and quests stay as text.
- **WoOOLcraft lessons built in:** integer enums, loud decode errors, persisted index counters, and refusing to create an empty DB.
- **Gate:** the verifier shows 0 mismatches, the shadow week is clean, the restore drill passes, and the burn-in has 0 decode errors.

**P8 — Security hardening** · M · Known debt · ⚠ `Crypto.cs` · ⚠ server-only `Shared` deploy
- **X1 fixes:** the Critical and High findings.
- **Flood control:** the flood guard counts packets, and stale clients are no longer banned for 24 h on a decode error.
- **Length checks:** `ReadPacket` checks lengths (`C.ClientVersion`, `C.CraftItem`, `C.ReportIssue` all allocate attacker-chosen sizes before login). ⚠ They ship server-only if the server is public (they are wire-compatible); otherwise they go in the C5 batch.
- **Hashing:** every password hash moves off the game thread. **Then** hash v2 (PBKDF2-SHA256, rehash on login and storage unlock). Ship v2 *verification* one deploy before any v2 *write*, so the rollback build can still verify upgraded hashes.
- **Gate:**
  - X1 Criticals and Highs are closed.
  - A bot flood trips the limits while normal bots are never kicked.
  - A 200-login storm shows no tick hitch.
  - Old and new hashes both work.

**P9 — Measured server optimisation** · L · Known debt · ⚠ WoOOLcraft reuse
- **Rules:** rank hotspots from the P6 numbers, one change at a time, with the regression script before every merge and two reviews for every threading change.
- **Candidate backlog:** scheduling, visibility, the network path, logging, the account-save snapshot and the mob-thread/GC model. The list is in the phase notes, and the order comes from measurements.
- **Gate:**
  - The target is met (suggested: 500 bots on one map at ≤50 % of baseline busy, p99 iteration under 50 ms).
  - A 2 h soak runs clean.
  - Only after this gate can `DataRange` or widescreen be raised.

---

## 5. Client track (C1–C6)

C1–C4 need nothing from the server chain except your attention. Schedule your GPU runs and QA inside server watch windows.

**C1 — Client fixes, D3D9 goldens, frame baseline** · M · October · Known defects · ⚠ WoOOLcraft reuse
- **Crash fixes:** the socket-thread exception that exits the process, plus the settings and keybind defects.
- **Goldens:** a `Client.exe --rendertest` golden harness. Goldens can only be recorded while D3D9 is untouched.
- **Frame profiler.**
- **Repo:** the shaders go into git.

**C2 — Launcher release** · M · November. This is the first client release, and it ships the C1 fixes.
- **Per-file SHA-256** as a trailing section, so old launchers still work.
- **A "removed files" list.** Today the launcher removes unlisted files only on a manual "Clean Files", and then it deletes every unlisted file, user inis included.
- **Runtime detection** for .NET 10 and WebView2.
- **An HTTPS host.** The default today is upstream's plain-http `mirfiles.com`, and `Settings.Save()` writes `[Launcher] Host` into every player's ini, so a new default alone reaches no existing install. Saved hosts are migrated at load.
- **AutoPatcherAdmin V1 retired.**

**C3 — Render seam on D3D9** · M · November · Known debt · ⚠ device-lost rule
- **Seam:** raw device use in 8 files moves behind `IRenderBackend`. Gameplay draw calls don't change.
- **Gate:** goldens byte-identical, plus in-game QA. Shipping it as a patch is recommended.

**C4 — D3D11 via Vortice behind a toggle** · L (M with WoOOLcraft code, ⚠) · December
- **Build:**
  - A strict-FIFO sprite batcher.
  - 5 blend states, with their alpha slots set the way D3D9 actually behaved.
  - Flip-model presentation done right: teardown order and tearing flags.
  - Borderless fullscreen.
- **Rollout:** golden parity plus a QA checklist, then opt-in, then default, with D3D9 kept selectable for one ~2-week cycle. The client stays on net8.

**C5 — Flag-day** · M (+L if TLS) · ~January 2027 · Known defects · ⚠ retarget
- **Needs:** C2 one release earlier, C4 the default for one cycle, P4, P5's tool retargets, and the P6 StressClient. Client and server ship from one commit, with no DB format change, and never in P7's flip week.
- **Contents:**
  - The full SlimDX exit, which drops the VC++ 2010 and DirectX June 2010 prerequisites.
  - Client and `Shared` move to net10.
  - A protocol handshake, so stale clients get "update required" instead of a 24 h IP ban.
  - Batched `Shared` fixes: packet factory gaps, Observable leaks, length checks, and the client half of live game-data updates.
  - Optional TLS, only if public.
- **Accepted risk:** the client runs on unsupported .NET 8 for about 2 months after 2026-11-10 (it listens on no port). **Contingency:** if C4 isn't the default by ~2026-12-15, run a SlimDX-on-net10 smoke test.

**C6 — Polish** · M–L · Known debt · ⚠ client threading rule for texture streaming
- Borderless scaled fullscreen and DPI.
- UI and map redraw cost, plus frame pacing.
- Off-thread texture creation (D3D11 only).
- **Widescreen** already exists in the client, capped by the server's `MaxResolution` (1024). Raise it only after P9, together with `Globals.DataRange`. At 1920 the client draws 26 cells each side, but the server sends only 16.

---

## 6. Release rules

| Change type | Examples | Watch | Rollback |
|---|---|---|---|
| Binary-only | P2, P5, P6, P8 (except hash v2), P9 items, the P7 seam | 48 h – 7 days (≥3 autosaves, 1 planned restart, log review) | Previous binaries |
| DB format (one-way) | P3 | 72 h | Pre-deploy snapshot plus the previous build |
| Runtime | P4 | 7 days | P3 net8 binaries (keep .NET 8 on the host) |
| Credential format (one-way per account) | P8 hash v2 | 7 days | The previous build, which already verifies v2 hashes |
| Store of record | P7 flip | ≥4 weeks | `--export-binary`, then the previous build |
| Client release | C2, C3, C4, C6 items | One patch cycle | Previous client patch (C2 proves downgrade works) |
| Flag-day | C5 | One patch cycle | Revert both sides together |

Every deploy runs: manual save → stop → off-box snapshot → `deploy-with-backup.ps1` → watch. Deploys are user-run only, in the weekly window, and at low population if players are live.

---

## 7. Later, and not doing

**Later / optional,** roughly by value:
- A read-only SQLite mirror of `Server.MirADB`, so heavy admin searches leave the game thread.
- Accounts in SQLite, only if P6 shows the save cost matters.
- **LiveDb-lite** (restart-free game-data edits, XL), after P7. Easier than WoOOLcraft's because clients hold no game DB.
- A headless service host, after P2/P3. Easy, because MirCraft already runs independently of the world.
- Retiring the WinForms editors after a capability parity audit (~70 source files, ~48k lines in Server.MirForms).
- Linux hosting.
- Retiring the vendored .NET Framework DLLs.

**Not doing:**
- **Packet "encryption":** the key would ship in the client. TLS only, if public.
- **A web client or any second client:** about 40 % of WoOOLcraft's commits, and every later wire change then had to ship to four places.
- **Premature optimisation:** none before P6, and no .NET 8 server baseline.
- **New features on old paths:** no new MirCraft game-data editing on `Envir.Edit` before P7, and no new client UI or effects on D3D9.
- **Runtime splits:** no player-facing net10 client while SlimDX remains, and no net10-only `Shared` while any consumer is on net8.
- **Shortcuts:** no mixed-risk deploys, no compressed Stage 1 gates, and no `LangVersion` change bundled with a retarget.
- **Live-folder risks:** no CLI `.sln` builds, no builds into `Build\`, no agent deploys, and no live data in the repo or CI.
- **Drive-by debt fixes:** none outside a scheduled phase.

---

## 8. Rule overrides (⚠)

**Approved by the user on 2026-10-09** ("Yes to the rule overrides"). Each override takes effect in the phase listed, and the rule text in AGENTS.md or the child doc is amended in that phase's commit, not before. The Known debt row stays a per-phase go-ahead.

| Rule | Source | Where | Approved change |
|---|---|---|---|
| "Do not add a global.json or retarget" | AGENTS.md Build & Output | P4, P5, C5 | Approve the .NET 10 retarget (your request); update the rule in the P4 commit |
| "New saved fields bump `Envir.Version`" | AGENTS.md (your decision, 2026-10-08) | P7 | Additive SQLite columns for SQLite-backed system data only; accounts keep the rule |
| WoOOLcraft is read "only as design inspiration, and only when the user asks" | AGENTS.md (after the Folder Map) | C1, C4, P6, P9 | Allow copying and adapting its render test runner, sprite batcher, D3D11 backend, StressClient patterns and re-measured P9 fixes, with a note in each commit |
| "Deploy client and server from the same commit after any `Shared/` change" | AGENTS.md | P8 (`ReadPacket` length checks) | A server-only exception for wire-compatible length checks, used if they need to ship before C5 (they must land before the server goes public) |
| NEVER change `Crypto.cs` | server.md §3.4 | P8 | Hash v2, only after off-thread verification is proven |
| NEVER add blocking I/O, `Thread.Sleep` or waits on the game thread | server.md §3.2 | P6 | One bounded (≤1 ms) wake-on-work park; amend §3.2 in the same commit |
| ALWAYS set `DeviceLost` on `Direct3D9Exception` | client.md §3.3 | C3 | The seam's `IsDeviceLost` replaces it; amend §3.3 in the same commit |
| NEVER touch the device or textures off the UI thread | client.md §3.2 | C6 | Off-thread texture creation on D3D11 only |
| Known debt and Known defects are fixed only as an explicitly requested task | AGENTS.md directive 5 | P2, P5, P6, P7, P8, P9, C1, C3, C5, C6 | Approve each phase when it starts |

---

## 9. Decisions needed now (P0–P4, C1)

Later decisions are listed per phase in the phase notes. **Answered 2026-10-09:** #1, #2, #4 (partly) and #6.

| # | Question | Recommendation / answer |
|---|---|---|
| 1 | Is `ryu131313/Crystal` public? May `Knowledgebase/`, `AGENTS.md` and `tools/` be committed? | **Answered:** the repo is private, and the Knowledgebase can be committed |
| 2 | Is the server live with players? Will it face the internet? What concurrency is the target? | **Answered:** not public yet; it will face the internet eventually; target **300 players at peak**. Consequences: <br>• Stage 1 watches protect your data, not players, so they can run at any time of day. <br>• P8 stays after P6, but **P8 and TLS (C5) must be done before the server goes public** (the go-public gate). <br>• P6/P9 load target: 300 bots across maps for the soak, and **500 bots on one map** as headroom. |
| 3 | Move the Server, Client and editor build output off `Build\`? This changes the VS F5 folder | Yes, to a gitignored `out\`; the deploy script copies into `Build\` |
| 4 | Provide a scratch copy of live data and the client `Data\Shaders`? May agents read it unscrubbed? | **Client data answered:** use `C:\Mir2\Build\Client\Debug` for now. Read it only, never modify it; C1 copies `Data\Shaders\*.ps` into the repo. **Still open:** a scratch copy of the live server data. Live data stays outside the repo and `Build\` and is never committed (CI uses synthetic fixtures) |
| 5 | Weekly hours and the deploy window? Off-box backup location? Interim `Multithreaded=false` (needs a restart)? | Saturday window; a daily off-box copy plus one per deploy; `Multithreaded=false` only if load allows |
| 6 | ⚠ The rule overrides in §8 | **Answered:** all approved (2026-10-09). WoOOLcraft code reuse makes C4 an M instead of an L |
| 7 | Accept the client on .NET 8 after 2026-11-10 until C5? | Accept, with the C5 contingency: a SlimDX-on-net10 smoke test if C4 isn't the default by ~2026-12-15 |
| 8 | May agents run `--rendertest` from scratch builds on this machine's GPU? | Yes, scratch builds only |
| 9 | Go-ahead for P2 (Known debt) | Yes. It closes a live account-loss window |
| 10 | DB missing but backups present: refuse to start, or prompt? | Refuse and log loudly; a brand-new server needs an explicit init flag |
| 11 | Legacy Concentration blobs at v118: skip and reset, or decode? | Skip and reset (a 3-second interrupt flag), unless P1's count says otherwise |

---

## 10. Top risks

1. **Loss of the live accounts file (exists today).** It is recoverable only from `Back Up\Accounts` if that hasn't been pruned. Mitigation: the P0 snapshot, keeping 48 h of backups, and P2 shipping first.
2. **v118 is a one-way door.** Mitigation: the P1 oracle on live data, P2's newer-version refusal, shipping alone on net8, and snapshot rollback.
3. **The deadline slips on hand-offs, not code.** Mitigation: the hand-off pack, the Oct 31 target with 10 days of buffer, and the slip rule.
4. **net10 breaks the legacy WinForms DLLs or quietly changes behaviour.** Mitigation: the P4 checklist and contingency, `Shared.Tests` on both TFMs, and the P3 rollback build.
5. **The SQLite cutover quietly loses data.** Mitigation: the independent verifier, live shadow mode, a single writer and `--export-binary`.
6. **Player installs break** (a missing runtime, presentation bugs that escape the goldens). Mitigation: C2 one release ahead, a clean-VM test and the C4 fallback cycle.
7. **Hot-path threading regressions.** Mitigation: the mutation audit, two reviews, bot soaks and the regression script.
8. **Review bandwidth.** Mitigation: WIP limits, the weekly window, and sequential phases for `Envir.cs` and `MirConnection.cs`.

---

## 11. Status

| Phase | Status | Design note | Shipped |
|---|---|---|---|
| P0 Groundwork | Code done 2026-10-10 (operator steps pending) | `Runbook.md`, `Future Ideas/Overnight-Log-2026-10-10.md` | — |
| P1 Harness | Done on synthetic data 2026-10-10 (real-data run pending) | `Future Ideas/Overnight-Log-2026-10-10.md` | — |
| P2 Crash-safe saves | Live 2026-10-10 (shipped with P3-P5, watch windows skipped by the user's decision) | `Future Ideas/Overnight-Log-2026-10-10.md` D43 | 2026-10-10 |
| P3 BinaryFormatter / v118 | Live 2026-10-10; live DB is v118 (rollback = snapshot `D:\Mir2Backups\Server-2026-10-10_17-56-56`) | `Future Ideas/Overnight-Log-2026-10-10.md` D43 | 2026-10-10 |
| P4 Server .NET 10 | Live 2026-10-10 on .NET 10 Desktop Runtime 10.0.11 | `Future Ideas/Overnight-Log-2026-10-10.md` D43 | 2026-10-10 |
| P5 Safety net | Live 2026-10-10 (runtime fault injection still open) | `Future Ideas/Overnight-Log-2026-10-10.md` D43 | 2026-10-10 |
| P6 Measurement | Tools, baseline and 60-min 300-bot soak done 2026-10-10; loop changes live | `Future Optimisation/Baseline-2026-10-10.md`, `Soak-2026-10-10.md`, `tools/run-baseline.ps1`, `tools/run-soak.ps1` | 2026-10-10 |
| P7 SQLite system data | Step 1 (exporter + verifier) done 2026-10-10; seam/shadow/flip need live data | `Future Ideas/P7-SQLite-Export.md` | — |
| P8 Security | Live 2026-10-10 (v2 hashes off by default; X1-17 decision + X1-20 retire decision open) | `Future Ideas/Overnight-Log-2026-10-10.md` D37, D39 | 2026-10-10 |
| P9 Optimisation | Started 2026-10-10; P9-01/02 live | P9-01 encode-once, P9-02 player process interval (`Future Optimisation/P9-0*.md`) | Gate: quiet-machine rerun + 2 h soak |
| C1 Client fixes + goldens | Done 2026-10-10: fixes, shaders, golden harness (15 scenarios, goldens outside git), frame profiler | `Client/D3D11-Parity.md` | — |
| C2 Launcher release | Hashes + removed-files + path checks done 2026-10-10; runtime detection, host, V1 pending | `Future Ideas/Overnight-Log-2026-10-10.md` | — |
| C3 Render seam | Done 2026-10-10: `IRenderBackend`, D3D9 backend byte-identical (15/15) | `Client/D3D11-Parity.md` | not shipped |
| C4 D3D11 | Done 2026-10-10: Vortice backend, golden parity 14/15 identical + 1 waiver, borderless fullscreen, automated QA pass, review fixed; **D3D11 is the default** (D3D9 selectable, automatic fallback). Next: ~2-week default cycle in play, then C5 | `Client/D3D11-Parity.md` | not shipped |
| C5 Flag-day | Not started | — | — |
| C6 Polish | Not started | — | — |

Update this table and the phase's design note in the same commit that finishes a phase. If a phase's findings change the order, revise this file first.

---

## Appendix — WoOOLcraft, in order, and what the order taught

| When (2026) | What happened | Lesson applied here |
|---|---|---|
| Jun 9–11 | The plan was written on Jun 9. DX11 via Vortice went behind a render seam with 26 byte-exact goldens. Phases 0–5 were committed in about 24 h; Phase 5 deleted D3D9 and waived the planned fallback cycle | Goldens first. Three presentation bugs then reached players (unkillable process Jun 16, Win11 60 fps cap Aug 3, fullscreen aspect Sep 12), so C4 keeps one fallback cycle |
| Jun 11 | Symptom-driven server passes for kill spikes. The same evening: the StressClient, a loop profiler and an idle park | The measured pass later found different hotspots, so even cheap fixes wait for P6's numbers |
| Jun 12–13 | Web portal, with admin editing built on an editor copy mirrored into the live world | That design caused the H1 bug (Oct 4) even after SQLite. Only a single writer removed it |
| Jun 19 | SQLite for system data behind a default-off flag, with a shadow diff and the binary kept for rollback | Storing enums by name, plus a swallowed catch, shipped an invisible boss within a week |
| Jul 8 | A systematic review found 13 Critical and 24 High issues. There were no server tests until Oct 5 | Build the harness early, and do a systematic review rather than a quick audit |
| Aug 23–25 | Full SlimDX exit in one 47-file commit, 2.5 months after DX11; its runtime checks were never run | Fold the SlimDX exit into the DX11 work |
| Sep–Oct | Web client, about 40 % of all commits | Every later wire change had to ship to server, desktop, relay and web |
| Oct 3 | A measured perf pass at 240/500/1,000 bots took one map from 88 % to 36 % busy; reviews found real threading bugs | Measure first, and have threading changes reviewed twice |
| Oct 5–6 | LiveDb: single writer, row-level saves, client push. About 25k lines plus 26k lines of tests, off by default | It needs SQLite of record first. Both paths are still maintained |
| Oct 8 | Headless host after a 261-capability parity audit; the server .NET 10 port is still uncommitted | Doing the runtime last cost a shim. TFMs are still mixed (net48/8/9/10), and players need two runtimes |

---

*Last updated: 2026-10-08 (initial version, fact-checked). Detail and evidence: [`Future Ideas/Roadmap-Phase-Notes.md`](Future%20Ideas/Roadmap-Phase-Notes.md).*
