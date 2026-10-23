# Quest system redesign: data model, runtime and migration (2026-10-23)

**Status: proposed 2026-10-23, not started. Reviewed 2026-10-23** (one read-only reviewer, fact check against the code + design review; its findings are folded into this text). This is a design written as if we built the quest system from scratch, then fitted to Crystal (SQLite system data since P7, binary `Server.MirADB` for user data, game thread plus mob threads). It replaces `QuestInfo` / `QuestProgressInfo` and the quest packets. No code exists yet. Open decisions are at the end; decide them before phase 1.

## Why

What we have today (all upstream Crystal):

| Area | Today | Where |
|---|---|---|
| Definition | `QuestInfo`: one SQLite row (name, group, file name, level range, one `RequiredQuest`, class, `QuestType`, 4 messages, time limit) plus a quest body with `[@KILLTASKS]`, `[@ITEMTASKS]`, `[@FLAGTASKS]`, `[@CARRYITEMS]`, rewards, descriptions. Since P7 phase 7.4 the bodies are design-text rows in `SystemData.db` (quests kind, `QuestFormat` / `quest_lines`), edited in the web admin Scripts editor and reloaded live by `QuestReload` | `Server/MirDatabase/QuestInfo.cs`, `QuestInfo.LoadInfo`, `Storage/Design/DesignTextFormats.cs`, `WebAdmin/GameData/QuestReload.cs` |
| Tasks | Kill, collect (into the 40-slot quest bag), carry, global flag (number `< FlagIndexCount − 1000`). All tasks at once, no steps. `//TODO: ZoneTasks`, `//TODO: EscortTasks` | `QuestInfo.cs` |
| Targets | Resolved **by name** at load (`Envir.GetItemInfo(name)`). An unknown name returns null and the line is silently dropped. At runtime a kill task matches **every monster whose name starts with the target's name** (`StartsWith`, ignore case: "Wolf" also counts "Wolf2"); item progress counts items by `Info.Name ==`. Rewards: `ParseReward` adds **both** the `(M)` and `(F)` item when the name has gender variants, and `FinishQuest` gives every fixed reward without a gender check (a bug) | `ParseKill`, `ParseItem`, `ParseReward`; `QuestProgressInfo.NeedKill` / `ProcessKill` / `ProcessItem` |
| Givers | NPC script `[QUESTS]` section → `NPCObject.Quests`; positive ids = start, negative = finish. `QuestInfo.NpcIndex` / `FinishNpcIndex` hold the NPC's **runtime `ObjectID`**, not `NPCInfo.Index`; the last script loaded wins, so a quest has one giver. An unknown quest id `return`s and drops the **rest** of that `[QUESTS]` section | `NPCScript.ParseQuests` (~L685-717) |
| Progress (saved) | `CharacterInfo.CurrentQuests` → `QuestProgressInfo.Save`: index, start/end time, lists of (MonsterID, count), (ItemID, count), (flag, state) **matched by target id**, not by task | `QuestProgressInfo.cs`, `CharacterInfo.cs` ~L512 |
| Completion | `CompletedQuests: List<int>`, no dates or counts. Daily = `Envir.ClearDailyQuests` removes the indexes from `ProcessNewDay` (the loop skips the next element after `RemoveAt`, which matters only for back-to-back duplicates). The new day fires when `Envir.Now.Day` changes (`Envir.Process`); `Now` runs on a UTC clock, so the reset is **00:00 UTC**, there is no setting for it, and a server that is down over midnight skips that day's reset. `ProcessNewDay` also invalidates the whole offline account cache. Repeatable = never added | `Envir.cs` ~L3316, ~L6842 |
| Flags | `CharacterInfo.Flags: bool[Globals.FlagIndexCount]`, global and shared with every NPC script | `CharacterInfo.cs` ~L94 |
| Wire | `S.NewQuestInfo` (whole `ClientQuestInfo`), `S.ChangeQuest` (`ClientQuestProgress` with `TaskList: List<string>`, already formatted server-side text), `S.CompleteQuest`, `S.ShareQuest`, `S.GainedQuestItem`, `S.DeleteQuestItem`; `C.AcceptQuest`, `C.FinishQuest`, `C.AbandonQuest`, `C.ShareQuest` | `Shared/Enums.cs` ~L1637 / ~L1847 |
| Runtime | Each kill / pickup walks `CurrentQuests` with LINQ (`CheckNeedQuestKill`, `CheckNeedQuestItem`); kill credit already deferred out of mob passes (`MonsterObject.Die` → `Envir.RunOutsideMobPass`). Only player kills of monsters without a master count (hero kills give no quest credit). A quest drop never lands on the ground: it goes straight into the quest bag of **every** party member in range who needs it | `PlayerObject.cs` ~L12945-13040, `MonsterObject.cs` ~L1004-1011, ~L1157 |
| Live edits | `S.GameDataChanged` pushes changed `ClientQuestInfo` on LiveDb edits; `QuestReload` re-points online characters' progress and refuses edits that change a quest's task shape. GM `SETQUEST` and the web admin character quest routes edit progress directly | `ServerPackets.cs` ~L6903, `QuestReload.cs`, `PlayerObject.cs` ~L4792, `WebAdmin/CharacterRoutes.cs` ~L602 |
| Clients | Desktop classic `QuestDialogs.cs`, modern `QuestsWindow` + `TrackerHud` (5 tracked, progress parsed from strings with a regex, objective bars hidden), web `quests.ts` / `tracker.ts` | `Client/MirScenes/...`, `WebClient/src/ui/modern/...` |

Each feature we want (steps, branches, area/talk/interact tasks, weekly or cooldown repeats, multiple prerequisites, reputation, map markers, progress bars, shared quests) hits one of these limits. Patching each limit one at a time leaves us with four half-models. This design replaces them in one go.

## Goals and non-goals

**Goals**
1. One data model in which quests, steps, objectives, conditions and rewards are typed rows with stable ids, and every target is an id that is checked when the quest is saved.
2. Character state recorded **per objective id**, so a quest can be edited without corrupting the progress of players who are on it.
3. Repeat rules (daily, weekly, cooldown, max completions) worked out from completion history when needed. No bulk reset job.
4. Progress driven by game events through a per-player index: O(1) per event, no LINQ, game thread only.
5. Progress sent to clients as structured data (objective id, value, target). Clients format the text, draw bars and place map markers.
6. One condition system and one reward system, reusable later by NPC dialogue, the GameShop and events.
7. Authoring in the web admin with validation. A file format that is easy to diff, for export and import.

**Non-goals (for this design)**
- Moving user data out of `Server.MirADB` into SQLite. The character-state *records* are designed so a later move is mechanical, but this design does not do it.
- Phasing/instancing (per-player world state). The model leaves room for it (conditions on spawns), but the runtime is not part of this work.
- Voice, cutscenes, camera work.
- Rewriting the NPC script language. Scripts keep working; they gain a few quest checks and actions.

## Design principles

1. **Definition vs state.** Definitions are system data (`SystemData.db`, edited through `Envir.Edit`, published by `GameDataSave`). State is user data (`Server.MirADB`). State never stores anything that can be read from the definition.
2. **Stable ids, never reused.** Quests, steps and objectives each get an integer id from a counter that only goes up, like `QuestIndex` today. Deleting an objective retires its id for good. A string `key` (e.g. `bichon.wolves.1`) exists for authors and export files only, never for saved state.
3. **Ids, not names.** Every target (monster, item, NPC, map) is an id, checked when the quest is saved. Display names come from the target's info.
4. **Declarative first, script as escape hatch.** Conditions, objectives and rewards are data. Per-step script hooks exist for the 5% that data cannot express, and call NPC script pages, not C#.
5. **Text by key.** Quest text lives in one text table with a language column. Definitions hold keys.
6. **The game thread owns all quest state.** Events raised elsewhere are passed to the game thread, as kill credit already is.

## Definition model (system data, `SystemData.db`)

Tables in the existing P7 store (`SqliteWorldCodec` specs + `GameDataRegistry`). `quests` stays a top-level table; the rest are child tables of `quests` (via `parent_position`, as `maprespawns` is of `maps`), except `conditions`, `texts` and `storylines`, which are top-level so other systems can use them.

The SQLite store has no SQL foreign keys today; integrity is checked in code (see Validation). The `→` arrows below are logical references.

**Codec constraints** (`SqliteWorldCodec.cs`, `SqliteWorldFile.cs`), which shape the tables below:
- Every table spec needs a `GameDataRegistry` entry with an entity type, and every registry table needs a spec (checked both ways at start).
- Child tables are one level deep under a top-level parent, keyed `(parent_position, position)`, where `parent_position` is the parent's **list position**, not its `Index`. So steps, objectives, branches, endpoints and rewards are all direct children of `quests` (objectives carry a `Step` id column; they are not children of steps).
- The row key is an INTEGER (`Func<object,int>`), so every table has an int `Index`; string keys such as `texts.Key` are ordinary unique columns.
- **New index counters are not added by the additive migration** (`CounterNames` is a fixed list; Decode refuses a store without a counter). `QuestStepIndex`, `QuestObjectiveIndex`, `QuestConditionIndex`, `QuestGroupIndex`, `QuestTextIndex`, `StorylineIndex` each need a written store migration in phase 1.
- Removing columns (the legacy `quests` columns in phase 6) also needs a written migration: unknown columns fail `CheckColumns` at load.
- `WriteAll` rewrites every table on every save, so per-quest change detection (`Revision`) is done in code (below).

### `quests`
| Column | Type | Notes |
|---|---|---|
| `Index` | int PK | Stable; existing quest indexes are kept |
| `Key` | text unique | Author key for export/import |
| `NameKey`, `SummaryKey` | text → `texts` | |
| `Category` | byte enum `QuestCategory` | Story / Side / Daily / Weekly / Repeatable / Event / Guild / Hidden (replaces `QuestType`; see Enum notes) |
| `StorylineIndex`, `StorylineOrder` | int → `storylines`, int | 0 = none |
| `LevelHint` | int | For "recommended" colouring; not a gate |
| `AcceptCondition` | int → `conditions` | 0 = always. Replaces `RequiredMinLevel/MaxLevel/RequiredQuest/RequiredClass` |
| `VisibleCondition` | int → `conditions` | When the "!" shows, if different from accept (e.g. show at level − 2, grey) |
| `RepeatPolicy` | byte enum | Once / Daily / Weekly / Cooldown / Unlimited |
| `CooldownSeconds` | int | For Cooldown |
| `MaxCompletions` | int | 0 = unlimited (for repeatable policies) |
| `ShareMode` | byte enum | None / Offer (today's share) / AutoParty |
| `CreditMode` | byte enum | Solo / PartyInRange (today) / Contribution (shared quests) |
| `TimeLimitSeconds` | int | 0 = none; counted from accept |
| `FailPolicy` | byte enum | Abandon / Fail (stays in log as failed, retake allowed) / Restart step |
| `AbandonAllowed` | bool | Story quests may forbid it |
| `Revision` | int | Bumped when the quest's **shape hash** changes: on save, `Envir.Edit` hashes the quest's steps, objectives (ids, types, targets, counts) and branches, compares it with `ShapeHash`, and increments `Revision` only on a difference. Text and reward edits do not bump it. Used by state migration |
| `ShapeHash` | long | See `Revision` |
| `PoolIndex` | int → `questpools` | 0 = none. Daily/weekly rotation: only the pool's current picks are available |
| `StartItemConsumed` | bool | An `ItemUse` start endpoint uses up the item on accept |
| `Flags` | int bitmask | AutoAccept, AutoComplete (no turn-in), HiddenInLog, Account-wide (reserved) |

### `queststeps` (child of `quests`)
| Column | Type | Notes |
|---|---|---|
| `Index` | int | Stable step id (global counter `QuestStepIndex`) |
| `Order` | int | Display and default next-step order |
| `TitleKey`, `TextKey` | text → `texts` | Tracker line and log text for this step |
| `CompletionMode` | byte enum | All / Any / NOf |
| `NRequired` | int | For NOf |
| `NextStep` | int | 0 = the next by `Order`; −1 = ready to turn in |
| `TimeLimitSeconds` | int | 0 = none; counted from entering the step |
| `FailCondition` | int → `conditions` | The step fails when it becomes true (left the map, died, escort target dead via a `Counter`); 0 = never. Applies the quest's `FailPolicy` |
| `OnEnterPage`, `OnCompletePage` | text | Optional NPC script page names (the escape hatch) |

### `questobjectives` (child of `quests`, with `Step` column)
| Column | Type | Notes |
|---|---|---|
| `Index` | int | Stable objective id (global counter `QuestObjectiveIndex`), **never reused** |
| `Step` | int → step `Index` | |
| `Order` | int | |
| `Type` | byte enum `QuestObjectiveType` | Designed now: Kill, Collect, Deliver, Talk, ReachArea, UseItem, Craft, Gather (mine/fish), ReachLevel, UseSkill, Counter (script-driven). **Reserved values only** (no runtime, no `Params` schema until a later plan): Interact, Escort, Defend, PvpWin, Equip |
| `PartyCredit` | byte enum | Each (every member in range gets +n, today's behaviour) / Shared (one counter for the party; reserved until shared quests) / Self (killer only) |
| `TargetKind` | byte enum | None, Monster, MonsterGroup, Item, ItemGroup, Npc, MapObject, Map, Spell |
| `TargetId` | int | id in the table that `TargetKind` names |
| `Count` | int | Required amount (1 for binary objectives) |
| `Map`, `X`, `Y`, `Radius` | int | Area for ReachArea / Defend, and the **marker hint** for every type (0 = no marker) |
| `Optional` | bool | Bonus objective; does not block the step |
| `Hidden` | bool | Not shown until it has progress |
| `Condition` | int → `conditions` | Counts only while true (night only, in party, wearing X) |
| `TextKey` | text → `texts` | Optional override; default text is built on the client from type + target + count |
| `DropChance` | int (per 10,000) | Collect objectives: the quest drop chance, replacing quest drops defined in item drop files |
| `Params` | text (JSON) | Reserved for the later types (escort route, defend waves). Empty for every designed type |

### `questgroups` (top-level) and `questgroupmembers` (child)
Named sets of monsters or items ("any wolf", "any ore") for `MonsterGroup` / `ItemGroup` targets. `{ Index, Key, Kind }` + `{ TargetId }`. The importer creates one group per legacy kill task holding every monster whose name starts with the task's name (today's `StartsWith` rule), so imported quests count exactly what they count today.

### `questpools` (top-level) and `questpoolmembers` (child)
`{ Index, Key, Picks, RotatePolicy (Daily | Weekly) }` + `{ Quest, Weight }`. At each reset boundary the server picks `Picks` quests by weight with a seed derived from the pool and the boundary date, so every player sees the same board and nothing needs saving.

### `questbranches` (child of `quests`)
| `Step` | `ChoiceKey` | `TextKey` | `NextStep` | `Outcome` | `Condition` |
|---|---|---|---|---|---|

A step with branches ends with a choice (made at an NPC or in the window). `Outcome` (int, per quest) is recorded in history and can be checked by later conditions ("sided with the miners").

### `questendpoints` (child of `quests`)
| `Role` | `Kind` | `TargetId` | `Map` |
|---|---|---|---|
| Start / Finish | Npc / ItemUse / AreaEnter / Auto (Board reserved) | `NPCInfo.Index` / item id / … | map (for Area) |

This replaces the NPC script `[QUESTS]` section for giving out quests. A quest can have several givers. Npc endpoints store the **`NPCInfo.Index`** (stable), not the runtime `ObjectID` the legacy fields hold. `Auto` = offered when the accept condition becomes true (level-up quests).

### `questrewards` (child of `quests`)
| Column | Notes |
|---|---|
| `Scope` | Quest (on turn-in) / Step (on step completion) / Objective (bonus objective) / Outcome (branch outcome) |
| `ScopeId` | step / objective / outcome id; 0 for Quest |
| `Kind` | Exp, Gold, Credit, Item, Reputation, Title, Spell, Unlock (feature or map flag), Buff, Currency, ScriptPage |
| `TargetId`, `Amount` | e.g. item id + count, faction id + points |
| `Scaling` | Flat / ByLevel (formula id) |
| `ChoiceGroup` | 0 = always given; n = "pick one from group n" |
| `Condition` | → `conditions` (class/gender filters; replaces `(M)`/`(F)` name suffixes). **Deliberate bug fix:** today both gendered items are given; after import each gets a Gender condition, so a player gets only theirs |
| `FirstTimeOnly` | bool: only on the first completion of a repeatable quest |

### `conditions` (top-level, shared)
One table, a small expression tree:

| Column | Notes |
|---|---|
| `Index` | Stable id |
| `Op` | And / Or / Not / Leaf |
| `Parent` | parent condition id (0 = root); children ordered by `Order` |
| `Leaf` | Level, Class, Gender, QuestState (quest, state), QuestCompletedCount, QuestOutcome, ItemCount, Equipped, Reputation, Guild, GuildRank, InParty, PartySize, Flag (legacy global flag), QuestVar, TimeOfDay, DayOfWeek, MapIs, Married, HasHero |
| `Arg1`, `Arg2` | ints (ids, values) |
| `Cmp` | Eq / Ne / Lt / Le / Gt / Ge |

A quest's accept rule is one root id. Each condition row has an `OwnerQuest` column (0 = shared, for use by other systems): the editor deletes a quest's owned conditions with it, and validation forbids a quest referencing a subtree owned by another quest.

### `texts` (top-level, shared)
`{ Index, Key, Body }`, with the existing quest markup (`{text/Colour}`, `[ITEM|MONSTER|NPC:n]` links) kept as is. One language for now (the server's, today's model); a `Lang` column is added only if Open decision 2 chooses per-player language.

### `storylines` (top-level)
`{ Index, Key, NameKey, Order }`: a chapter list for the storyline view.

### Enum notes
- **New** enums (`QuestCategory`, `QuestObjectiveType`, `QuestTargetKind`, `QuestRepeatPolicy`, `QuestRewardKind`, `QuestConditionLeaf`, …) are stored by value in `SystemData.db` and in user data: explicit values, append-only, never renumbered (AGENTS.md persisted-enum rule).
- `QuestType` stays in `Shared/Enums.cs` until the legacy packets are removed (phase 6); `Category` is set from it by the importer.

### Validation (on every save through `Envir.Edit`)
- Every reference resolves (target ids exist for their kind; steps and objectives belong to the same quest; `NextStep` and branch targets exist; no step can be reached from no path; no loop without a branch).
- Condition trees: no cycles, leaves have valid args, `Not` has one child.
- Objective `Type` and `TargetKind` are compatible (Kill → Monster/MonsterGroup, Collect → Item/ItemGroup, Talk → Npc, ReachArea → Map with radius > 0, …).
- Every `*Key` text exists in at least the default language.
- Retired objective ids are not reused (checked against the counter).
- Failures block the save in the web admin with a list of every error (the WinForms editor is not extended; see phase 4).

## Character state (user data, `Server.MirADB`)

Records saved by `CharacterInfo.Save` in a new block, written from `Envir.Version` 121 on (the next free version). The layout is written as tables because that is what it is; in the binary file each "table" is a count followed by records.

```text
QuestState        per active or failed quest
  Quest        int      quest Index
  Status       byte     Active | Ready | Failed
  Step         int      current step id
  Revision     int      definition revision when last migrated
  AcceptedAt   long     DateTime.ToBinary (UTC)
  ExpiresAt    long     0 = no limit
  SharedFrom   int      character index of the sharer, 0 = none
  Branch       int      outcome chosen in this run, 0 = none

ObjectiveState    per objective with progress (no row = 0)
  Quest        int
  Objective    int      objective id (NOT monster or item id)
  Value        int      count, or a bitmask for multi-spot objectives

QuestHistory      per quest ever completed (replaces CompletedQuests: List<int>)
  Quest        int
  Times        int
  FirstAt      long
  LastAt       long
  LastOutcome  int

QuestVar          quest-scoped variables (new; the global Flags[] stays for scripts)
  Quest        int      (Storyline/Global scopes reserved: Global would duplicate Flags[])
  Key          int      variable id from the definition, not a string
  Value        int
```

Tracking stays client-side (`Settings.TrackedQuests`, `QuestTracking.ini`), as today; the server only sends a "track on accept" hint.

**Rules**
- **Availability** = `AcceptCondition` true **and** the repeat policy allows it: `Once` → no history row; `Daily`/`Weekly` → `LastAt` before the last reset boundary; `Cooldown` → `now − LastAt ≥ CooldownSeconds`; `MaxCompletions` → `Times < Max`. Boundaries come from new settings `[Quests] DailyResetHour` (default 0) and `WeeklyResetDay` (default Monday), in UTC like `Envir.Now` (today's reset is a hard-coded 00:00 UTC day change). No bulk clear job, so `Envir.ClearDailyQuests` goes away. Side benefits: a server that was down over the reset no longer skips it, and the new day no longer has to invalidate the whole offline account cache for quests.
- **Quest items stay in the quest bag** (`QuestInventory`, 40 slots, cannot be traded, dropped or sold), with today's party rule: a quest drop goes straight into the bag of every party member in range whose objective needs it (`PartyCredit = Each`). This keeps progress unbuyable through trade, adds no main-inventory pressure and needs no `UserItem` change (a `UserItem` field would be a wire change, because `UserItem.Save` serves packets and the DB). `Collect` counts what is in the quest bag; `Deliver` counts ordinary items in the main inventory, recounted once per tick from a dirty flag set by the inventory-change paths, because inventory slots are assigned in many places with no single hook.
- **Time limits** are stored as `ExpiresAt` (absolute), so they keep counting while offline, like today (`StartDateTime` + limit). On load, an expired quest is applied with its `FailPolicy`.
- **Concurrent cap** stays `Globals.MaxConcurrentQuests` (20), counted over `Active` + `Ready`.

### Definition edits vs saved state
On login, and when `GameDataSave` publishes a quest edit, each `QuestState` whose `Revision` differs from the definition is migrated:
1. Drop `ObjectiveState` rows whose objective no longer exists.
2. If `Step` no longer exists: move to the first remaining step by `Order`; if none remain, set `Ready`.
3. Clamp `Value` to the new `Count`.
4. Re-run step completion (an edit can make a step complete).
5. Set `Revision` and send the client the new state.

Since objective ids are never reused, an id that still exists still means the same task.

## Shared quests (not designed here)

Party, guild and world quests with contribution scoring are a separate plan (phase 7). This design only reserves the enum values (`CreditMode.Contribution`, `PartyCredit.Shared`, `ShareMode.AutoParty`) so that plan needs no renumbering. Expected shape: an Envir-level state (quest, scope, scope id, step, objective values) plus a contribution score per character, with rewards scaled by score above a minimum.

## Live edits

Edits to quests on a running world go through `Envir.Edit` and are published by `GameDataSave`, like today. The V2 replacements of today's live-edit paths:
- `S.GameDataChanged` (today pushes `ClientQuestInfo`) carries the changed quest's `S.QuestDefinition` to connections that were sent it, and clears it from `Connection.SentQuestInfo` otherwise, so a client never keeps a stale definition.
- `QuestReload` (today re-points online progress and refuses shape-changing edits) is replaced by the revision migration below, which handles shape changes instead of refusing them.
- GM `SETQUEST` and the web admin character quest routes (`CharacterRoutes.cs`) are rewritten against `QuestState` / `ObjectiveState` / `QuestHistory` (set step, set objective value, complete, reset history) in phase 3.

## Runtime

### Event pipeline (game thread)
Game code raises typed events on `PlayerObject` through one entry point:

```csharp
// one struct, no allocation per event
internal readonly struct QuestEvent { public readonly QuestObjectiveType Type; public readonly QuestTargetKind Kind;
                                      public readonly int TargetId; public readonly int Amount; public readonly int Map, X, Y; }
player.Quests.Raise(in QuestEvent e);
```

Raise points: monster death (already deferred with `RunOutsideMobPass`; party credit fans out from there), quest drop into the quest bag (`CheckGroupQuestItem`, as today), main-inventory change (sets the Deliver dirty flag), item used, NPC talk (on opening an NPC dialog), map enter and cell change (ReachArea: only when the player crosses into a watched map, then per step through a per-map list of watched areas), craft, mine/fish result, level-up, spell cast, script `QUESTCOUNTER` action.

**Never raise from a mob thread.** A raise point that can run in a mob pass (anything under `MonsterObject.Process`) uses `Envir.RunOutsideMobPass`, like kill credit today.

### Per-player objective index
`PlayerQuests` (new, owned by `PlayerObject`) keeps
`Dictionary<long, List<ObjectiveRef>>` keyed by `(Type, TargetKind, TargetId)` packed into a long, plus a small list for target-less types (ReachLevel, UseSkill without a target). Group targets add one key per group member. The index is rebuilt for one quest when it is accepted, changes step, or is migrated. Events look up one key: no walk over all quests, no LINQ. Lists are reused, no allocation per event.

`Collect` objectives count what the quest bag holds (as `ProcessItem` does today). `Deliver` objectives count the main inventory, recounted once per tick when the player's inventory dirty flag is set. Kill credit keeps today's rule (player kills of monsters without a master; hero kills could be added later as a deliberate change).

### Availability and NPC icons
Which quests an NPC offers depends on conditions (level, items, other quests). Each player caches available-quest results, marks the cache dirty on events that can change a condition leaf (level, class change, quest state, inventory of an item a condition watches, guild, party), and re-evaluates only the dirty quests when the player next opens or sees a giver. `S.QuestAvailability` sends the giver NPC ids with their icon state for the NPCs in view (replacing today's per-NPC `QuestIDs` list plus client-side checks).

### Accept / complete / turn in
- Accept: endpoint check (NPC in range, as today; item use; area; auto), `AcceptCondition`, availability, cap → create state, give `OnAccept` items, rebuild index, send state.
- Step complete: rewards with `Scope = Step`, `OnCompletePage`, branch or `NextStep`, rebuild index, send state.
- Turn in: finish endpoint check, bag space for all rewards (as `CanGainItems` today), take objective items (`Deliver` from the inventory, `Collect` and carry items from the quest bag), give rewards, write history, run `DefaultNPCType.OnFinishQuest` (kept), send state.
- `AutoComplete` quests turn in when they become Ready.
- Step timers and `FailCondition`: checked from a per-player due time (the earliest `ExpiresAt` of any quest or step) and when an event dirties a condition leaf, not every tick.

### Telemetry
Each accept, step complete, fail, abandon and turn-in writes one line to a quest log (log4net logger `Quests`, like the other server logs) with character, quest, step and seconds since accept. The web admin can later show funnels (accepted → step n → done) from it. No per-progress logging.

### NPC script integration
Kept: `CHECKQUEST`, `[QUESTS]` (imported as `Npc` endpoints, then the section is ignored with a warning after phase 6), `OnAcceptQuest` / `OnFinishQuest` default NPC calls, global flags. Added: checks `CHECKQUESTSTEP q s`, `CHECKQUESTVAR`, `CHECKQUESTOUTCOME`, `CHECKCONDITION id`; actions `QUESTCOUNTER q objective n`, `SETQUESTVAR`, `QUESTSTEP q s` (force a step), `OFFERQUEST q` (opens the accept prompt). Script validation in the web admin (`ScriptValidation.cs`) learns the new keywords.

## Wire protocol

New packets, **ids appended at the end** of `ServerPacketIds` / `ClientPacketIds` (all four places plus the Shared.Tests manifest, the TS port and `WebRelay/Fixtures.cs`):

| Packet | Direction | Content |
|---|---|---|
| `S.QuestDefinition` | S→C | One quest's definition in the player's language: steps, objectives (type, target kind/id, count, marker map/x/y/radius, optional, hidden), rewards (filtered by the player's class/gender), endpoints, texts. Sent once per connection, like `NewQuestInfo` (`Connection.SentQuestInfo`). The targets' `ItemInfo`/`MonsterInfo`/`NPC` names follow the existing on-demand rules |
| `S.QuestStates` | S→C | Batch of `(Quest, Status, Step, ExpiresAt, StepExpiresAt, TrackHint)` on login and on every change; `TrackHint` only on accept (the client decides, as today) |
| `S.QuestProgress` | S→C | Batch of `(Quest, Objective, Value)`; coalesced per tick per player, so a 10-kill AoE sends one packet |
| `S.QuestHistory` | S→C | `(Quest, Times, LastAt)` for the log's Completed tab and repeat timers |
| `S.QuestAvailability` | S→C | Giver NPC ids + icon state for the NPCs in view |
| `S.QuestOffer` | S→C | Accept prompt from a non-NPC endpoint (item, area, auto, share) |
| `C.QuestAccept` / `C.QuestTurnIn` / `C.QuestAbandon` / `C.QuestShare` / `C.QuestChoose` | C→S | quest id (+ reward choice group picks, + branch `ChoiceKey`) |

All ≤ 65,535 bytes: `S.QuestDefinition` for a very large quest is checked by validation (limit the text size per quest). Packets are private (`Observable => false`).

The legacy packets stay until both clients ship with V2 (phase 6), behind `[Quests] Protocol=Legacy|V2` like `[Optimisation] MoveBatch`: with `Legacy` the server builds `ClientQuestInfo` / `ClientQuestProgress.TaskList` from the new model (single-step view: current step's objectives, server-formatted lines), so an unpatched client still works.

## Clients (desktop modern + web)

- Text built on the client from `(Type, Target, Count, Value)` with `ClientTextKeys` (e.g. `QuestObjKill` = "Kill {0}: {1}/{2}"), unless the objective has a `TextKey`.
- Objective bars and counts in `QuestsWindow` and `TrackerHud` (the "objective bars hidden" note goes away); step list with done/current/future; failed quests in the log.
- Map markers: big map and minimap draw the marker hint of every objective of each tracked quest's current step (area circles for ReachArea), plus the turn-in NPC when Ready. The Map button centres on the next marker.
- Available tab driven by `S.QuestAvailability` (no client-side copy of `CanAccept`).
- Storyline view: chapters → quests with done/current/locked.
- Repeat timers on daily/weekly/cooldown quests in the Completed tab.
- Progress toasts (a short bar on progress, a completion banner).
- Classic UI: keeps working through the legacy single-step view during phases 3-5; gets V2 data in phase 5 (no new classic features).

## Authoring

- Web admin **Quests** editor (new page, `Server/WebAdmin` + `wwwroot/admin`): quest list with filters; quest form; step/objective tree with pickers for monsters/items/NPCs/maps (existing lookup endpoints); condition builder; reward table; text per language; the full validation list on save; "where used" (which quests reference this monster/item/NPC). Edits go through `Envir.Edit` under `EditLock` and publish with `GameDataSave`, like other game data.
- **Export/import:** one JSON file per quest (by `Key`, references written as keys/names *and* ids, ids authoritative), for diffs and bulk work. Import validates and shows a diff before applying.
- After the phase 1 import, the legacy quest bodies (the design-text quests kind in `SystemData.db`, edited through the Scripts editor) are read-only in the web admin, and `QuestReload` stops applying them. The WinForms quest editor is not extended; it is disabled for V2 quests (Known debt: WinForms editors touch `Envir.Edit` directly; not changed here).

## Migration plan

Lean phases, one short smoke check each (see `lean-plans-testing`). Phases 1-2 do not change behaviour for players.

| Phase | Work | Smoke check |
|---|---|---|
| **0. Inventory (read-only)** | Tool run over a **copy** of the live `SystemData.db` + `Server.MirADB`: count quests, steps-to-be, every legacy line that fails to parse today (silently dropped), `[QUESTS]` sections cut short by an unknown id, quests with more than one would-be giver (last script wins today), quests no NPC gives, kill tasks whose `StartsWith` matches more than one monster, quests whose rewards have `(M)`/`(F)` pairs, quests in progress per type, quest bag contents, flags used by quest `[@FLAGTASKS]`. Written into this doc. | Report exists; numbers match the web admin quest count |
| **1. Definition model + importer** | New tables, registry entries, `Envir` lists; **written store migration for the new counters** (the additive migration cannot add counters); validation; importer from legacy `QuestInfo` + quest bodies: each quest → 1 step; kill tasks → `Kill` objectives on a `MonsterGroup` of every `StartsWith` match; item tasks → `Collect`; flag tasks → `Counter` tied to the flag; carry items → `OnAccept` quest-bag items; rewards → `questrewards` (`(M)`/`(F)` pairs → one reward each with a Gender condition: deliberate fix); requirements → a condition tree; `[QUESTS]` → `Npc` endpoints with the `ObjectID` mapped to `NPCInfo.Index` (positive = start, negative = finish; every script's entries kept, so a quest may now have several givers); `QuestType` → `Category` + `RepeatPolicy`. Old `QuestInfo` still loaded and used at runtime. Additive SQLite tables: **no `Envir.Version` bump** (P7 rule). | Import on the live copy: 0 validation errors or a listed set of real data errors (from phase 0); round trip SQLite → export JSON → import gives identical rows; `Server.Persistence.Tests` |
| **2. Runtime engine (dark)** | `PlayerQuests`, event pipeline, index, conditions, rewards, availability, accept/turn-in, revision migration, written against the new model. Behind `[Quests] Engine=Legacy|V2` (default Legacy, read at world start). With V2 the server sends the **legacy packets** built from the new model. State held in memory from the legacy save fields during this phase (converted on login, written back to the legacy fields on save), so no format change yet. **Single-step quests only in this phase**: the legacy fields cannot hold step, branch, revision or history data, so the editor refuses multi-step quests until phase 3, and the offline account cache (P9-12) stays valid because the saved bytes keep the legacy layout. | Scratch server (`tools/start-scratch-server.ps1`), V2 engine: StressClient quest bots accept/kill/turn in the imported quests; results match a Legacy run on the same seed (completions, rewards, gold/exp totals, except the gender-reward fix); `alone`/tick lateness unchanged in `[Diag] LogLoopProfile` |
| **3. Save format (Version 121)** | `CharacterInfo` writes the new state block; load for `< 121` converts: `CurrentQuests` → `QuestState` + `ObjectiveState` (matched by quest + monster/item id → objective id, using the phase-1 mapping), `CompletedQuests` → `QuestHistory` (`Times = 1`, dates unknown → `DateTime.MinValue`, so a daily quest is takeable at once, which is today's behaviour after the next reset anyway; Repeatable quests were never recorded, so their history starts at 0 and `MaxCompletions` counts from the migration). The quest bag is unchanged (no item migration, no `UserItem` change, so **no wire change in this phase**). GM `SETQUEST` and the web admin character quest routes move to the new state. `Engine=V2` becomes the default. A pre-121 build refuses a 121 file (`Envir.Version` check), so take the `Server.MirADB` backup before the first start (Runbook). | `Server.Persistence.Tests`: round trip at 121; legacy fixtures (synthetic + `MIR2_PERSISTENCE_DATA` copy) load → save → load with the same quests, counts and history; spot check of 3 characters with quests in progress on the live copy |
| **4. Protocol V2 + clients** | New packets (manifest regen), Shared models, desktop modern UI + web modern UI (bars, step list, client-built text, failed state, timers), TS port + fixtures, `[Quests] Protocol=V2` once clients are patched (in-game patcher, Runbook §9). Web admin Quests editor and JSON import/export. | `Shared.Tests` + `codec:test`; `tools\uitest.ps1` screenshots of the window and tracker with the fixture quests; one manual accept → kill → turn in on scratch with each client |
| **5. New content features** | Objective types beyond the imported ones (ReachArea, Talk, Interact, UseItem, Craft, Gather, ReachLevel, UseSkill, PvpWin), steps/branches/outcomes in the editor, weekly/cooldown policies, reputation reward kind (needs the faction model: Open decision 4), map markers and storyline view in both clients, NPC script keywords. Classic UI gets V2 data. | One authored test chain on scratch covering each objective type, a branch and a weekly reset (server clock moved); markers visible on big map/minimap |
| **6. Remove legacy** | After one release with no `Legacy` use: drop legacy packets (ids stay reserved, never reused), `QuestInfo` body parsing and the quests design-text kind, the legacy `quests` columns (written store migration: unknown columns fail `CheckColumns`), `QuestType`, `ClearDailyQuests`, `QuestReload`, `[QUESTS]` parsing (warning only), the `Engine` and `Protocol` flags. The quest bag stays. | All builds + tests (`tools\verify.ps1`); scratch smoke as in phase 4 |
| **7. Shared quests (separate project)** | Party/guild/world quests and contribution on the fixed model; event UI. | Its own plan |

Each phase ends with `tools\verify.ps1`. Deploys are user-run (`tools\deploy-with-backup.ps1`). Rollback: phases 1-2 roll back by flag; from phase 3 on, rollback needs the pre-121 `Server.MirADB` backup (characters' quest progress since the deploy is lost), which is why phase 3 ships alone.

### What the importer cannot do automatically
- Quests whose real logic lives in NPC scripts (flags set by scripts, `CHECKQUEST` gates, `[@_DIE]` hooks) import as `Counter` objectives tied to the flag and keep working, but become proper objectives only by hand. Phase 0 lists them.
- Quest drops: today quest items drop through the normal drop files and `CheckGroupQuestItem`. The importer keeps that path (an item drop that matches a `Collect` objective goes to the player who needs it), so `DropChance` stays 0 for imported quests until an author moves them.
- Lines dropped silently today (unknown names), and `[QUESTS]` sections cut short by an unknown id, are reported, not guessed.
- NPCs that exist only at runtime with no `NPCInfo` row (if phase 0 finds any) cannot become endpoints; they are reported.

## Threading, performance and rules this must respect
- Game thread owns `PlayerQuests` and all quest state. Raise points under a mob pass defer with `Envir.RunOutsideMobPass`. No locks, no LINQ, no per-event allocation in `Raise` (server.md §3.2, §3.1).
- Web admin edits: `Envir.Edit` under `EditLock`; live state migration happens on the game thread when `GameDataSave` publishes (server.md §2.7, §3.7).
- Packet rule: four places + manifest + TS port in one commit; private packets; ≤ 65,535 bytes.
- New enums: explicit values, append-only.
- Version bump 121 with `LoadVersion` gates; `CustomVersion` stays 0.
- Client: UI thread only; text through `ClientTextKeys` in `Client/Localization/*.json`; server-side messages through `ServerTextKeys`.

## Open decisions (before phase 1)
1. **Is the user DB wiped at launch?** (The HUD belt decision of 2026-10-16 assumes a wipe.) If yes, phase 3's character conversion shrinks to "start empty" and phase 2's in-memory conversion layer can be dropped.
2. **Quest text language:** send the server's language only (today's model), or the player's client language (needs a client → server language field)?
3. **Reset times:** `DailyResetHour` / `WeeklyResetDay` defaults (today: 00:00 UTC daily, no weekly), and whether the Daily NPC call moves to the same hour.
4. **Reputation/factions:** a separate small model (`factions` table + per-character values) needed for the Reputation reward and condition leaf. In scope of phase 5 or its own plan?
5. **Account-wide quests/history** (alts sharing storyline progress): keep the flag reserved, or design it now?
6. **Phasing:** out of scope here; confirm.

## Never/Always rules (for whoever implements this)
From AGENTS.md: packet rule, persisted-enum rule, Build & Output rules (no solution build; scratch `-o`; never `Build\`), compile every consumer after a `Shared/` change, `Shared.Tests` / `Server.Persistence.Tests` / `Server.WebAdmin.Tests` as listed above, user-data fields bump `Envir.Version`, system-data fields are additive SQLite columns. server.md §3 and client.md §3 apply to their phases; check their Known debt sections before touching `PlayerObject` quest code (LINQ per event and `FinishNPC`'s `Envir.NPCs.Single` are Known debt replaced here, not drive-by fixes).
