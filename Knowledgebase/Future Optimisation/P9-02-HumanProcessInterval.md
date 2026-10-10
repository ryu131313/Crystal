# P9-02 — Players and heroes process every 10 ms, not every iteration (2026-10-10)

**Finding (code reading + the loop profiler):**
- `HumanObject.SetOperateTime()` set `OperateTime = Envir.Time`, so every player and hero ran its full `Process()` on every loop iteration: ~750 times a second each, ~375k calls/s at 500 bots.
- The work inside is timer housekeeping with 1 s-scale timers: buffs, regen, poison, torch/durability, rested, mail, fishing, required-group checks, creature timers.
- The only time-critical part is `MapObject.Process`'s `ActionList` (delayed attack damage, spells, NPC actions, map movement).
- Monsters already compute their next due time instead.

**Change:**
- `HumanObject.SetOperateTime()` runs the object at most every `[Optimisation] HumanProcessMs` (default **10**, range 0-50, 0 = old behaviour; also on the web admin settings page).
- It is **never later than the earliest pending delayed action**, so attack, spell and NPC timing stays exact for any delay above 10 ms.
- `OperateTime = 0` (used across `Map.cs`/`HumanObject.cs` to force processing) still works.
- Server-only; no `Shared`, DB or wire change.

**Gameplay effect:** timer housekeeping may run up to 10 ms later than before. A delayed action added with a delay under 10 ms may run up to 10 ms late. No timer in that code is finer than ~100 ms.

**Heroes** (`HeroObject : HumanObject`) get the same interval, and their AI runs inside `Process`. Hero decisions (move, attack, spell) can therefore start up to 10 ms later, comparable to how monsters already schedule themselves. The bots don't use heroes, so this part is untested under load: **watch heroes in the in-game smoke test**, or set `HumanProcessMs=0` if they feel sluggish.

**Measurements:** 500 bots, one map, `tools/run-baseline.ps1 -Bots 500 -NoIdle [-ExtraIni '[Optimisation]','HumanProcessMs=0']`, 60 s windows, bots on the same machine.

| Run | CPU % (1 core) | busy | ms/s conn | ms/s obj | alloc MB/s | max iter ms | bot attacks / kills | decode errors |
|---|---|---|---|---|---|---|---|---|
| HumanProcessMs=0 (old) | 23.4 | 33.1 | 220.7 | 103.1 | 50.27 | 33.924 | 744 / 131 | 0 |
| HumanProcessMs=10 | 24.2 | 30.49 | 253.46 | 44.7 | 29.93 | 33.445 | 737 / 119 | 0 |

**Conclusion:**
- **Proven:** object-phase time down 57 % (103 → 45 ms/s), and allocation down 40 % (50 → 30 MB/s; something in the player `Process` path allocates per call). Bot combat is unaffected: attacks and kills are comparable, so delayed damage still lands. 0 decode errors, 0 isolated errors.
- **Within noise:** overall CPU and `conn`. As with P9-01, quote CPU only after a quiet-machine rerun.

**What is left in `obj`:** the loop still walks the whole object list every iteration, including monsters that mob threads own (skipped by a type check), plus NPCs, items and spells. A separate list of game-thread objects would remove the walk. That changes the threading model, so it needs two reviews; it's a candidate for P9-03.
