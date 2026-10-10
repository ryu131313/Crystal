# Threading review of P2–P6 (commits 0d8b56d7, 4f45f1f4, ccba9221, 6c9d590f)

*Independent read-only review, 2026-10-10, branch `FuckingAround` at `6c9d590f`. Nothing was built or run. Line numbers refer to the tip of the branch.*

Scope: crash-safe saves (`BeginSaveAccounts`/`EndSaveAccounts`/`SaveAccounts`, the `Saving` flag), world-loop isolation (`WorldErrors`, `IsolateConnection`/`IsolateObject`, per-monster catch, `MobThread.Dead`, side-file saves and guild refresh counters, the `ReceiveData` race fix), load-test knobs and the retry cap, and `LoopProfiler` / idle park (`_loopSignal`, `WakeLoop`, `TimerResolution`) / `MobThreadOnePass`.

**Summary: 1 High, 3 Medium, 9 Low, plus doc drift.** I found no deadlock, lock-order inversion or lost wakeup in the new code. The main problem is a scheduling regression: with `MobThreadOnePass` and `IdlePark` both on, the game thread's own mob slice (`MobThreads[0]`) can starve.

---

## High

### H1. `MobThreads[0]` (game-thread monsters) starves under `MobThreadOnePass` + `IdlePark`

**Where:** `Server/MirEnvir/Envir.cs` L2358–2371 (thread-0 exit test), L2376 (one-pass stop), L2253–2254 (park).

**What happens:** thread 0 has no time budget and no pass criterion of its own. It walks its list only while some sub-thread is still running, and returns after the first node once every sub-thread is `Stop` or `Dead`:

```csharp
if (Info.Id == 0)
{
    stopping = true;
    for (var x = 1; x < MobThreads.Length; x++)
        if (MobThreads[x].Stop == false && !MobThreads[x].Dead) stopping = false;
    if (!stopping) continue;
    Info.Stop = stopping;
    return;            // after processing at most the one node above
}
```

Before P6, each sub-thread ran until `EndTime = Time + 10`, so thread 0 always got at least ~10 ms per tick and walked its whole list several times. With one-pass, a sub-thread stops as soon as it reaches `Last`. Thread 0 now only gets as much time per tick as the slowest sub-thread's single pass. The idle park then caps the loop at about 1 iteration per 1–2 ms.

**Scenarios:**
1. **A sub-thread is `Dead`** (the P5 path at L2393–2398). Thread 0 sees every other thread as stopped or dead, so it processes **one node per tick**. With the park that is at most about 500–1000 nodes per second. With ~20k monsters, each one on thread-0 maps acts about once every 20–40 s. Effectively the whole world's monsters freeze, not just the dead thread's.
2. **`ThreadLimit = 1` with `Multithreaded = true`.** The web admin catalog allows `min: 1` (`SettingsCatalog.cs` L193–195). `MobThreads.Length == 1`, so `stopping` is true at once and the result is the same 1 node per tick. This was already bad before P6 (about one node per spin), but the park makes it 3–10× worse.
3. **Uneven or empty sub-thread lists.** `Map.Thread = Envir.Random.Next(ThreadLimit)` is random per map. Small scratch worlds and load tests can leave thread 1 with few or no monsters. Thread 0's share of real time becomes about (thread-1 pass time) / (iteration + park). If the busy map (players fighting) is on thread 0, its monsters fall behind their `OperateTime`. Players would notice slower attacks and movement under load.

**Why it is real:** the code path is deterministic, with no timing assumptions. `passComplete` is never consulted for `Id == 0`, and `EndTime` is never set for `MobThreads[0]`.

**Fix (small, local):** give thread 0 the same criterion as the sub-threads, and keep it as part of the barrier:
```csharp
// at ThreadLoop entry for Id == 0:  Info.EndTime = Stopwatch.ElapsedMilliseconds + 10;
if (Info.Id == 0)
{
    var subsDone = true;
    for (var x = 1; x < MobThreads.Length; x++)
        if (!MobThreads[x].Stop && !MobThreads[x].Dead) { subsDone = false; break; }
    var ownDone = Settings.MobThreadOnePass ? (passComplete || Stopwatch.ElapsedMilliseconds > Info.EndTime)
                                            : true;   // legacy: stop when the subs stop
    if (!subsDone || !ownDone) continue;
    Info.Stop = true;
    return;
}
```
Also add a test or a scratch run with `ThreadLimit=1` and with a forced-dead thread 1, and check `LoopProfiler` mob time plus a monster's action rate.

---

## Medium

### M1. When `Disconnect` throws, `IsolateConnection` leaves the account locked to a dead connection, and the account can lock itself out until restart (Plausible: needs `StopGame` to throw)

**Where:** `Envir.cs` L2407–2418 (`IsolateConnection`), `MirConnection.cs` L800–832 (`Disconnect`), `Envir.cs` L4303–4308 (login takeover).

**What happens:** `Disconnect` sets `Connected = false`, removes the connection from `Connections`, then calls `Player.StopGame(reason)` under `AccountLock`. If `StopGame` throws (the case the fallback exists for), the rest never runs:
- `Account.Connection = null`
- the queue nulling
- `_client.Client.Dispose()`

The fallback only calls `Connections.Remove(connection)`, which is a no-op because the connection was already removed. So:
- the socket stays open and is never processed again (ReceiveData returns early on `!Connected`);
- `Account.Connection` still points at the dead connection;
- the `PlayerObject` may still be on its map and in `Envir.Players` (a ghost).

When the player logs in again, `Login` runs `account.Connection?.SendDisconnect(1)`. Because `Connected` is false, that becomes `SoftDisconnect` → `Player.StopGame(reason)` again, which is likely to throw again. The exception escapes the new connection's handler, the per-connection catch isolates the new connection, and `account.Connection = c` is never reached. The account cannot log in until the server restarts. The same applies when `Process()` itself threw from `Disconnect(20/21)`.

**Fix:** make the fallback finish the cleanup without depending on `StopGame`:
```csharp
catch (Exception ex)
{
    WorldErrors.Record(...);
    Connections.Remove(connection);
    connection.ForceCleanup(); // new MirConnection method: under AccountLock clear Account.Connection if it is this,
                               // Player = null; null the queues; dispose the socket; each step in its own try
}
```
Also, in `SoftDisconnect`/`Login`, wrap `StopGame` on the stale connection in a try/catch (Record + continue), so a broken old session cannot block a new login. Optionally remove the ghost player from `Envir.Players`/its map defensively in the same cleanup.

### M2. Sub-threads (and therefore thread 0) process one node per tick when the connection phase takes more than 10 ms (pre-existing; one-pass does not fix it)

**Where:** `Envir.cs` L2130 `Info.EndTime = Time + 10;`, L2376.

`Time` is sampled at the start of the iteration (L2063). Before the barrier, the loop runs `LoopProfiler.Publish`, `AdjustLights` and the whole `lock (Connections)` packet phase. If those take more than 10 ms (a load test with hundreds of clients, or a heavy handler), `Stopwatch.ElapsedMilliseconds > EndTime` is already true when a sub-thread wakes. It processes exactly one node, `passComplete` is false, and it stops. Thread 0 then returns after its own single node (H1). Every threaded monster progresses at about 2 nodes per tick for as long as the packet load lasts. This happens in exactly the load tests P6 is for. Startup has the same effect: on the first tick `EndTime == 0` because the game thread only sets it for threads that are already `Stop`.

**Fix:** set `Info.EndTime = Stopwatch.ElapsedMilliseconds + 10` immediately before `PulseAll` (and the same for thread 0, per H1), and seed `EndTime` when the sub-threads are created.

### M3. A guild refresh aborted mid-loop is never retried, leaving mixed old/new guild indexing on disk (Plausible)

**Where:** `Envir.cs` L2811–2843.

`GuildRefreshNeeded = false` and `forced = true` are set **before** the write loop. Each guild's `NeedSave = false` is cleared before its `new FileStream(i + ".mgdn", FileMode.Create)`. If the loop throws at guild *k*, the exception is caught by the P5 save-step catch at L2221. Possible causes:
- a sharing violation because the previous save's async write of the same `k.mgdn` is still open (two saves close together, e.g. `RequestSaveNow` right after the periodic save);
- a backup tool holding the file;
- a full disk.

Guilds k..N-1 keep their **old-index** files. Nothing forces a rewrite, because the refresh flag is already cleared and their `NeedSave` may be false. On the next load, the file at index *i* (i ≥ k) still holds the guild that used to be at *i*: the deleted guild can come back and the last guild can be missing or duplicated. `_guildRefreshPending` also never reaches 0, so the prune never runs. The prune not running is the safe direction.

This is better than before (previously every `.mgd` was deleted up front), but the retry gap is new to the P5 design.

**Fix:** clear `GuildRefreshNeeded` only after the loop has issued every write. On exception, set it back to `true` so the next save repeats the full refresh. Clear `NeedSave` only after `BeginWrite` succeeds.

---

## Low

### L1. `Saving` is a plain bool written from an IO thread; the 30 s "saving anyway" path will usually fail
`Envir.cs` L141, L2706–2726, L3038–3061. The bool is written on the game thread and the IO completion thread, and read on the game thread, HTTP threads (`AccountRoutes` L252, L909; `CharacterRoutes` L42; `BackupRoutes` L513) and the shutdown thread. Today `Thread.Sleep(1)` in the wait loop prevents hoisting, so this works in practice. Make it `volatile` (the doc's Known debt already lists it).

Separately: if the async write is still open after `AccountSaveWaitLimitMs`, `File.Create(AccountPath + "n")` hits a sharing violation (the async `FileStream` was opened with the default `FileShare.Read`). The *final shutdown* save is then lost, even though the log line says "saving accounts anyway". Use a different temp name for the synchronous path (e.g. `Server.MirADBs`), or keep waiting at shutdown.

### L2. Final async side-file saves are never awaited at shutdown or reboot (pre-existing; P5 says it closes data-loss windows)
`Envir.cs` L2280–2283 (`StopEnvir` → `SaveGoods(true)`, `SaveGuilds(true)`, `SaveConquests(true)`). These are `BeginWrite`s that nothing waits for. A fast process exit after `Stop()` loses the last guild, goods and conquest changes. P2/P5 keep the live file intact, so the data reverts rather than corrupts. On a reboot, `LoadGuilds` could in theory read while a `File.Replace` is in flight (the long `LoadDB` makes this unlikely). Fix: count in-flight side writes (`Interlocked`) and wait for zero with a timeout at the end of the shutdown tail.

### L3. `IsolateObject` → `StopGame` can despawn the captured `next` node (Plausible)
`Envir.cs` L2152–2172 and L2424–2432. `next = current.Next` is captured before `Process()`. If the isolated player's `StopGame` despawns its pet or hero and that object is `next`, the slice then:
- calls `Process()` on a despawned object (may throw, so a second isolated error);
- reads `.Next` on a detached node (null), which ends the slice early.

This is harmless but noisy. Fix: after `IsolateObject`, `if (next != null && next.List == null) next = null;`.

### L4. Quarantining a monster for 10 minutes after a transient exception
`Envir.cs` L2347–2351. Many mob-thread exceptions come from the known cross-thread debt: `List` enumerated while the game thread changes it, or `LinkedList` add/remove from two threads. These used to kill the thread (a hang). Now they freeze that monster (possibly a boss) for 10 minutes, or until poison or a pet level resets `OperateTime`. The world also keeps running on a possibly corrupted `Envir.Objects`/`ObjectsList`. P5 trades a hang for continuing with possible corruption. Consider a short first quarantine (e.g. 5 s) that escalates on repeats.

### L5. `TimerResolution.End()` is not in a `finally`; `Begin()` runs even when `IdlePark` is off
`Envir.cs` L2064, L2275–2277. If the inner catch itself throws (e.g. `SendDisconnect` or `frame.GetFileLineNumber()` on a null frame), control skips to the outer catch and `End()` never runs. The `Interlocked` `_active` guard keeps later Begin/End calls balanced, and process exit releases the resolution, so the only cost is power. Move `End()` into a `finally`, and call `Begin()` only when `Settings.IdlePark`.

### L6. The early wake mostly cannot cut latency; one syscall per receive
`Envir.cs` L2076, L2253; `MirConnection.cs` L189. Connections are processed only when `conTime != Time` (a new millisecond). A wake in the same millisecond as the last iteration runs an idle iteration, which consumes the `AutoResetEvent` and parks for the full 1 ms. Worst-case packet latency is about 1–2 ms. That is fine, but the comment overstates the benefit. `_loopSignal.Set()` on every receive costs a kernel transition even when the loop is not parked; a `volatile bool _parked` check would avoid it.

Plausible: on Windows 11 22H2+ / Server 2025, a minimised or occluded process's `timeBeginPeriod` can be ignored (power throttling). Monster timers would then run at about 15.6 ms granularity while packets still wake the loop. Consider `SetProcessInformation(ProcessPowerThrottling, IGNORE_TIMER_RESOLUTION off)` if the server is deployed there.

### L7. An exception on thread 0 outside the per-object catch still breaks the barrier (pre-existing Known defect, not fixed by P5)
`Envir.cs` L2385–2399. For `Id == 0` the catch logs and returns with sub-threads possibly still running. The game thread then runs the object slice, maps and players at the same time as the mob threads for one tick. The remaining throw sites are very unlikely (`WorldErrors.Record`/`MessageQueue`), but the `server.md` §3.3 table should still say this is open.

### L8. Overlapping guild refreshes share the counters
`Envir.cs` L2819–2821, L2846–2857. A second refresh (`Interlocked.Exchange` of `_guildRefreshPending`/`_guildRefreshFailed`) while the first refresh's completions are in flight mixes the two. Late decrements from refresh 1 count toward refresh 2, and refresh 1's failure flag is wiped. `_guildRefreshKeep` is a plain int read by the IO thread. A prune only deletes index ≥ the current count, so this cannot remove a live guild unless a new guild file is written in that window. Fix: put pending/failed/keep in a per-refresh state object (like `SideFileSave`) and give it to each `SideFileSave`.

### L9. The retry cap drops movement packets silently (Plausible desync)
`MirConnection.cs` L34–39. Beyond 64 queued packets, Walk/Run/Turn/Attack/Magic packets are dropped with no reply. The client's position can then drift from the server's until something sends `UserLocation`. Only a misbehaving client or a long lag burst reaches 64. Consider sending `S.UserLocation` once when dropping, or disconnecting past the cap. The idle park also makes `Process()` run about 10× more often, so retried packets are re-queued about 10× more often. That is cheap, but it means actions now run within about 1 ms of `ActionTime` instead of about 10 ms (see Behaviour notes).

### L10. LoopProfiler cosmetics
- `LoopProfiler.Last` keeps the last snapshot after the world stops, so the `/status` JSON shows a stale `loopProfile`.
- The first `BeginIteration` after a restart counts the whole downtime as `Park`.
- The time spent in `Publish()` is counted as `Connections` (it runs after `BeginIteration` and before the Connections mark).

Clear `Last` in the WorkLoop tail and reset `_mark = 0` on start.

### L11. Unwrapped calls remain in the loop
`AdjustLights()`, `LoopProfiler.Publish()`, the two `Broadcast(...)` timers and `BackupRetention.RunInBackground()` are outside any per-step catch. A throw there still takes the old whole-world stop path (L2257–2273). All are low-risk. Wrap them for consistency with §3.3.

### Doc drift (server.md)
- §2.4 step list: still says "no sleep (`Thread.Sleep(1)` is commented out, one core pegged)". Add the idle park.
- §3.3 table: the "Monster AI throws on a mob thread" row still describes the pre-P5 hang. It should describe the per-monster catch, `Dead`, and the thread-0 caveat (L7).
- §2.4 "Mob threading": add `MobThreadOnePass`, and the H1 caveat until it is fixed.

---

## Behaviour notes (not defects)
- **Tick rate.** Before P6, any world with `Multithreaded` had a ~10 ms barrier floor, so ≤ ~100 ticks/s and connections processed about every 10 ms. With one-pass and the park, ticks run at about 500–1000/s. Players' Walk/Attack/Magic retries now run within about 1 ms of `ActionTime` instead of up to about 10 ms late. That is roughly 1–1.5 % faster effective action cadence on 600 ms actions: not noticeable, but it is a change.
- **Monster scheduling.** `Time` is frozen per tick, so re-walking a sub-thread list in the same tick only re-processes monsters whose `SetOperateTime` left `OperateTime <= Time`. One pass per tick is therefore semantically the same as the old re-walk, apart from H1/M2.

---

## Checked and found correct
- **Monitor barrier, lost wakeups:** the sub-thread sets `Stop = true` outside the lock and then waits in `while (Info.Stop) Monitor.Wait(_locker)`. The game thread writes `Stop = false` before `lock(_locker){PulseAll}`. Every interleaving either sees `false` under the lock or is woken by the pulse and re-checks. Nothing is lost.
- **Idle park, missed wakeups:** `AutoResetEvent` stays set, so a `Set()` while the loop is busy makes the next `WaitOne(1)` return at once. The wait is bounded at 1 ms anyway, and `Stop()` needs no wake (the loop sees `Running == false` within about 1 ms).
- **`Dead` + barrier:** `Dead` is volatile and written before `Stop = true` in the catch. The game thread skips dead threads both when re-arming (L2128) and in thread 0's wait (L2363). The volatile `Dead` read in the spin also stops the JIT hoisting the plain `Stop` read. A restart creates new `MobThread` objects, which clears `Dead`. The game thread cannot hang on a dead thread.
- **Empty lists under one-pass:** the sub-thread sets `passComplete` and stops at once, with no spin and no hang. Thread 0 with an empty list spins only until the sub-threads stop (then see H1).
- **Can the game thread run ahead of the mob threads?** Only via L7. A sub-thread cannot start a new pass before the game thread re-arms it.
- **Reverse `Connections` iteration with isolation:** `Disconnect` removes the connection at index *i* first. The `if (i >= Connections.Count) continue;` guard covers several removals in one handler. Removing an index below *i* can make one connection be processed twice in a tick, which is pre-existing and harmless.
- **Lock order:**
  - `IsolateObject` takes `lock(Connections)`, then `Disconnect` takes `Connections` again (re-entrant) and then `AccountLock`. That matches the documented `Connections → AccountLock`.
  - No new code takes `Connections` while holding `AccountLock`.
  - `_adminQueueLock` and `WorldErrors.Gate` are leaf locks (`WakeLoop` and the logging run outside them).
  - The accept callback takes `Connections` only for `Add`.
- **`LoopProfiler` threads:** `CountPacketIn`/`CountPacketsOut` are called only from `MirConnection.Process()`, which runs only from the WorkLoop's connection phase. `Mark`/`Begin`/`End`/`Publish` run on the game thread. `Last` is published as an immutable snapshot reference, which is safe to read from HTTP threads.
- **Hot-path allocation / LINQ:** nothing per tick was added. `Publish` allocates about 1–2 KB once a second. `WorldErrors` string building is on error paths only. `HasPriorDataEvidence` LINQ runs at load only. `IsLoadTestExempt` short-circuits on a static bool.
- **`Envir.ObjectID`:** `Interlocked.Increment(ref uint)` is correct, and the field is static.
- **`ReceiveData` fix:** working on local copies of `_receiveList`/`_rawData` removes the NRE → 24 h IP-block false positive. If `Disconnect` races in, packets go to an orphaned queue and `_rawData` may be revived on a dead connection; both are harmless. `BeginReceive` afterwards no-ops or is caught.
- **`EndSaveAccounts`:** `Saving` is reset in `finally` on every path. The double `Dispose` is harmless. The callback touches only files, the static failure counter and `Saving`. It is also correct if the APM callback runs synchronously on the game thread.
- **`SaveDB` temp-name collisions:** `Edit.SaveDB` is serialised with Main's save by `GameDataSave.RunFileIo` (it runs on the game thread while Main saves). The WinForms `MonsterTunerForm`/`DragonInfoForm` → `Main.SaveDB()` Known debt can now collide on `Server.MirDBn`, but the collision fails the save instead of corrupting the file.
- **`CompleteSideFileSave`:** reading `FileStream.Name` after `Dispose` is safe on .NET. A failed `File.Replace` leaves the live file and a stale `*n` file, and is logged through `WorldErrors`.
- **`PruneSurplusGuildFiles("*.mgd")`:** believed not to match the in-flight `*.mgdn` temp files. .NET (Core) matches patterns in managed code against long names, without the Win32 8.3 three-character-extension quirk. Worth one quick check on the target runtime because the consequence would be deleting a temp file mid-write.
- **`timeBeginPeriod`/`timeEndPeriod` pairing:** guarded by `Interlocked.Exchange` on `_active`. Begin and End both run on the WorkLoop thread. The calls stay balanced across `Reboot`.
- **Retry cap:** bounds `_retryList` per connection. The re-enqueue loop runs after the receive loop, so it cannot spin forever.
