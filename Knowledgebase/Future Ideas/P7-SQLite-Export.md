# P7 step 1: SQLite export and independent verifier

Written 2026-10-10. Roadmap §4 Stage 3, P7 step 1 ("Exporter"). Phase notes: `Roadmap-Phase-Notes.md`, section P7.

**Status:** built, not deployed anywhere. It's an offline CLI. It only reads `Server.MirDB` and never touches a running server. Nothing in `Server/`, `Server.MirForms/` or `Shared/` changed.

| Path | What it is |
|---|---|
| `tools/MirDbExport/` | Console tool `MirDbExport.exe` (net10.0 via `$(ServerTfm)`, `Microsoft.Data.Sqlite` 9.0.0). Server.Library already grants it `InternalsVisibleTo`. |
| `tools/MirDbExport/WorldLoader.cs` | Offline load. It refuses a missing or 0-byte file, copies `Server.MirDB` to `%TEMP%\mirdbexport\<guid>`, runs `Envir.Edit.LoadDB()` there and checks that the copy's SHA-256 didn't change. |
| `tools/MirDbExport/Exporter.cs` | Export, using `GameDataRegistry.Tables` → `Rows(ctx)` / `Columns` / `Get`. |
| `tools/MirDbExport/Verifier.cs` | Independent verifier. |
| `tools/MirDbExport/Completeness.cs` | `gaps` report. |
| `tools/MirDbExport.Tests/` | xUnit tests: 16 tests, under 1 s. They copy the v118 fixture and never write to it. |

## Usage

```
dotnet build C:\Mir2\tools\MirDbExport\MirDbExport.csproj
C:\Mir2\tools\MirDbExport\bin\Debug\net10.0\MirDbExport.exe export <folder-with-Server.MirDB> <out.db>
C:\Mir2\tools\MirDbExport\bin\Debug\net10.0\MirDbExport.exe verify <folder-with-Server.MirDB> <out.db>
C:\Mir2\tools\MirDbExport\bin\Debug\net10.0\MirDbExport.exe gaps
dotnet test  C:\Mir2\tools\MirDbExport.Tests\MirDbExport.Tests.csproj
```

**Exit codes:**
- `0`: success, or verified with 0 mismatches.
- `1`: export errors (no file is written) or verify mismatches. Up to 200 are listed with table, key and column.
- `2`: usage or setup error, such as a missing or 0-byte file, or `LoadDB` refusing the version.

**Safety:**
- Point it at a **copy** of live data, never at `Build\`.
- It only reads the source. Even so, a file that is being rewritten mid-copy would export a torn read. `SaveDB` writes a temp file and then swaps it in (P2), so in practice copying the file is safe.
- The export is written to `<out>.tmp`, checked with `PRAGMA integrity_check`, and only then moved into place with `File.Move(…, overwrite: true)`. On any error the `.tmp` is deleted and no `.db` appears.
- Connections use `Pooling=False`, so no handle stays open.

### Results on `Server.Persistence.Tests/Fixtures/v118/Server.MirDB` (synthetic)

- **Export:** 19 tables, 124 rows.

  | Table | Rows |
  |---|---|
  | `items` | 4 |
  | `monsters` | 2 |
  | `magic` | 109 (the file's spells plus `FillMagicInfoList` defaults) |
  | `npcs` | 1 |
  | `quests` | 1 |
  | `maps` | 2 |
  | `mapsafezones` | 1 |
  | `maprespawns` | 1 |
  | `mapmovements` | 1 |
  | `gameshop` | 1 |
  | `dragon` | 1 |
  | `mapminezones`, `conquests` and the 6 conquest child tables | 0 |

- **Verify:** 3,118 cells compared, **0 mismatches**, exit 0.
- **Corruption check:** raising `items.Price` by 1 in a copy gives `items key 1 column Price: export has 101 (INTEGER), entity has 100 (INTEGER)`, exit 1.
- **Tests:** they add a conquest with one of every child, a mine zone, a negative stat, a null ToolTip and a non-BMP name in memory. With those, all 19 tables carry rows and verify with 0 mismatches. 10 kinds of corruption are each caught: an int column, a stat column, an enum, a float, a DateTime, a string set to NULL, `parent_index`, a deleted row, an index counter, and the meta SHA.

## Schema (schema_version 1)

**Tables:**
- **One STRICT table per registry endpoint, named after it:** `items`, `monsters`, `magic`, `npcs`, `quests`, `maps`, `mapsafezones`, `maprespawns`, `mapmovements`, `mapminezones`, `gameshop`, `conquests`, `conquestguards`, `conquestgates`, `conquestwalls`, `conquestsieges`, `conquestflags`, `conquestcontrolpoints`, `dragon`.
  - `key INTEGER PRIMARY KEY` is `GameDataRow.Index`:
    - Top-level tables: the record's own Index.
    - `magic`: `(int)Spell`.
    - `gameshop`: `GIndex`.
    - `maprespawns`: `RespawnIndex`.
    - Other map and conquest child tables: `parent*10000 + position`.
    - `dragon`: 1.
  - `parent_index INTEGER NOT NULL` is `GameDataRow.ParentIndex`: the MapIndex or ConquestIndex for child tables, 0 for the rest.
  - `position INTEGER NOT NULL` is the 0-based position in the owning list (the top-level list, or the parent's child list). Positional child rows are identified by `(parent_index, position)`, as the phase notes require.
  - Then one column per `GameDataColumn`, in registry order, quoted, with the registry's name. That includes the parent column (`MapIndex` / `ConquestIndex`, which repeats `parent_index`), `Stat_<Name>`, `<Point>_X/_Y` and `Exps_1..12`. Columns added to the registry by a new public model field (e.g. `gameshop` `DealGoldPrice`/`DealCreditPrice`, DB v119) follow `meta.envir_version` and do **not** bump `schema_version`; it is bumped only when this layout rule or an encoding rule changes.
  - A duplicate key, or a column-name collision (case-insensitive, or with a reserved name), is an export error.
- **`meta (name TEXT PRIMARY KEY, value ANY) STRICT`:** `schema_version`, `envir_version` (= `Envir.LoadVersion`), `envir_custom_version`, `code_envir_version` (`Envir.Version` of the exporting build), `exported_utc` (ISO 8601), `source_file`, `source_size`, `source_sha256`, `tool`.
- **`index_counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL) STRICT`:** `MapIndex, ItemIndex, MonsterIndex, NPCIndex, QuestIndex, GameshopIndex, ConquestIndex, RespawnIndex`, as loaded and never recomputed. `Envir.ScriptIndex` isn't persisted, so it isn't exported. The per-conquest child counters (`GuardIndex` …) are columns of `conquests`.

## Encoding rules

The rules are applied by the exporter and re-implemented separately by the verifier.

| CLR type | SQLite | Notes |
|---|---|---|
| enum (any underlying type) | INTEGER | Numeric value, never the name. A `ulong` above `long.MaxValue` is an error. |
| bool | INTEGER | 0 / 1 |
| sbyte, byte, short, ushort, int, uint, long, char | INTEGER | exact |
| ulong | INTEGER | Error if > `long.MaxValue`. No registry column uses ulong today. |
| float, double | REAL | Floats are widened exactly to double. NaN is an error because SQLite stores it as NULL. |
| string | TEXT | null → NULL (kept distinct from `''`). An unpaired surrogate is an error. |
| DateTime | INTEGER | `DateTime.ToBinary()`, the same as the binary `GameShopItem.Save`. Local-kind values carry the same timezone caveat as the binary format. |
| `List<int>` | TEXT | JSON array without spaces, e.g. `[1,2,3]`. null → `[]`. The binary format writes a count, so null and empty were already the same. |
| decimal | TEXT | Invariant `ToString()`. No registry column uses decimal today. |
| anything else | export error | none today |

**Loud loss checks:** these also fail the export, and the verifier checks them independently.
- **Stats keys without a column:** a `Stats` entry whose key has no `Stat_` column and whose value isn't 0. That covers an undefined `Stat` byte from an old file and `Stat.Unknown`. The registry exports only the defined Stat members.
- **Array length mismatch:** an array whose length differs from its `Name_N` column count (`DragonInfo.Exps`).

**Zero-valued Stats entries:** these aren't data loss. `Stats.Equals` treats 0 and absent alike, but the binary loader can hold explicit `key=0` entries, so a later byte-identical round trip has to normalise them (see "Next steps").

## Independent verifier: what "independent" means here

- **Rows:** rows are enumerated **directly from the `Envir.Edit` lists** in `Verifier.Specs`. The verifier never calls `GameDataTable.Rows`. Keys, parent and position are re-derived there.
- **Columns:** each exported column is mapped to an entity member by the verifier's own naming rules:
  - the parent column → the parent's `Index`;
  - renames `DestinationMapIndex → MovementInfo.MapIndex` and `LocalIndex → Index`;
  - a same-named public field or property;
  - `X_X` / `X_Y` → a Point;
  - `Stat_<Name>` → the single Stats member;
  - `X_N` → array element N-1.

  The value is read by reflection. The verifier never calls `GameDataColumn.Get`.
- **Encoding:** `Verifier.Expect` is a separate implementation of the table above. Cells are compared by storage class and value.
- **What the registry is still used for:** only its **names**, to prove the export has exactly the registry's endpoints and columns. A dropped or extra table or column is a mismatch, and so is a registry table the verifier doesn't know.
- **Other checks:** row counts and missing or extra keys per table, `index_counters` against the `Envir.Edit` fields (read by reflection), and `meta` (`schema_version`, `envir_version`, `source_size`, `source_sha256`). The SHA check catches verifying an export against the wrong source.
- **Shared code:** the verifier shares `WorldLoader`, which is the real `Envir.Edit.LoadDB` load, and the table names in `Schema.cs`. It shares no encoding.

## Completeness report

`MirDbExport gaps` lists the public instance fields and properties of each persisted model that no exported column or key carries. Source: the verifier's column → member mapping over the registry's columns. The "Saved in Server.MirDB?" column was checked by hand against each `Save(BinaryWriter)`.

| Model | Member | Type | Saved in Server.MirDB? | Lost if SQLite were the store of record? |
|---|---|---|---|---|
| MapInfo | SafeZones / Movements / Respawns / MineZones | List | yes | **No**: rows in `mapsafezones` / `mapmovements` / `maprespawns` / `mapminezones` |
| MapInfo | NPCs | List<NPCInfo> | no (built by `CreateMap`) | no |
| MapInfo | ActiveCoords | List<Point> | no (runtime) | no |
| ItemInfo | RandomStats | RandomItemStat | no (bound at load from `RandomStatsId` + `Settings.RandomItemStatsList`) | no, but the SQLite loader must redo the binding |
| ItemInfo | IsConsumable, IsFishingRod, FriendlyName | get-only | no (computed) | no |
| MonsterInfo | Drops | List<DropInfo> | no (drop files) | no |
| MonsterInfo | HasSpawnScript, HasDieScript | bool | no (set from scripts) | no |
| MonsterInfo | GameName, ClientInformation | get-only | no (computed) | no |
| NPCInfo | Colour | Color | no (set at runtime from the conquest guild's flag colour) | no |
| NPCInfo | Sabuk | bool | read only by old versions (`< 66`), never written | no (the old-backup importer handles it) |
| NPCInfo | GameName, ClientInformation | get-only | no | no |
| QuestInfo | Description, TaskDescription, ReturnDescription, CompletionDescription, CarryItems, KillTasks, ItemTasks, FlagTasks, GoldReward, ExpReward, CreditReward, FixedRewards, SelectRewards | various | no (quest `.txt`, `LoadInfo`) | no |
| QuestInfo | NpcIndex, NpcInfo, FinishNpcIndex, FinishNPC | runtime | no | no |
| GameShopItem | Info | ItemInfo | no (`BindGameShop` at load) | no, but the loader must redo the binding |
| ConquestInfo | ConquestGuards / Gates / Walls / Sieges / Flags / ControlPoints | List | yes | **No**: rows in the 6 conquest child tables |
| SafeZoneInfo | Info | MapInfo | no (back-reference to the parent map) | no; restore it from `parent_index` |
| DragonInfo | Drops | List<DropInfo>[] | no (`DragonItem.txt`) | no |
| DragonInfo | Level, Experience | byte / long | no (runtime) | no |
| ConquestArcher/Gate/Wall/Siege/FlagInfo, RespawnInfo, MovementInfo, MineZone, MagicInfo | — | — | — | every public member is exported |

No member-level gap would lose saved data. **But the registry isn't the whole of `Server.MirDB`.** These parts of the file are outside the registry and would be lost:

| Data in Server.MirDB | Exported? | Needed for |
|---|---|---|
| **`Envir.RespawnTick` (`RespawnTimer`)**: `BaseSpawnRate` (byte), `CurrentTickcounter` (ulong), the `Respawn` list of `RespawnTickOption {UserCount int, DelayLoss double}` | **no** | **Loss if not added.** `CurrentTickcounter` is runtime state, and the phase notes' `runtime_state` table is the place for it. `BaseSpawnRate` and the option list are design data that no grid exposes. Being a ulong, `CurrentTickcounter` needs the > `long.MaxValue` rule. |
| GTMap block (`GTMapList`) | no | Written but never read by `LoadDB`. Dropping it is already planned. |
| `MagicInfo` records with `Spell == 0` (`Spell.None`) | no (the registry filters `Index > 0`) | Saved and reloaded today. Decide whether to drop them or give them a table (a zero key would collide with nothing). |
| Header `Version` / `CustomVersion` | as `meta.envir_version` / `envir_custom_version` | — |

**Load-time behaviour the export inherits:** the export shows what `Envir.Edit` holds *after* `LoadDB`, not the raw file.
- **Game shop drops:** game-shop items whose `ItemIndex` doesn't bind are dropped by `LoadDB`, so the next binary save already loses them.
- **Duplicate spells:** duplicate `Spell` records are dropped (`MagicExists`).
- **Default spells:** `FillMagicInfoList` adds every missing default spell, which is why `magic` has 109 rows for the fixture.
- **Old files:** `UpdateMagicInfo` runs on files with version ≤ 70.

The verifier compares against the same loaded state, so it can't see these drops. A raw-file record counter, which would mean parsing the format outside `Envir`, is a possible follow-up if they matter.

## Deviations from the brief, and what was skipped

- **Round trip "binary → SQLite → load → SQLite byte-identical":** not done, because there is no SQLite loader yet. That belongs to the storage-seam / `SqliteWorldStore` steps. The test that exists is that two exports of the same file are identical apart from `exported_utc` (`Export_is_deterministic_apart_from_the_timestamp`).
- **The exporter's own loss checks** (Stats keys and array lengths) go beyond the brief. They exist because the registry's column set can't carry those values, and a silent drop is exactly the WoOOLcraft failure.
- **`parent_index` is 0 for top-level rows**, not NULL. That matches `GameDataRow.ParentIndex`.
- **NuGet warning NU1903:** `Microsoft.Data.Sqlite` 9.0.0 pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.10, which carries a high-severity advisory (GHSA-2m69-gcr7-jv3q). That's fine for an offline tool on trusted input. Before the store-of-record step, pin a fixed `SQLitePCLRaw.bundle_e_sqlite3`, or use a newer `Microsoft.Data.Sqlite`.

## What the next P7 steps need (from this work)

1. **Storage seam (`IWorldStore`):**
   - Post-load bindings must stay outside the store and run for both backends: `BindGameShop` (it drops unbound items today; keep that or log it loudly), `ItemInfo.RandomStats`, `SafeZoneInfo.Info`, `Settings.LinkGuildCreationItems`, `FillMagicInfoList` / `UpdateMagicInfo`, and the quest / drop / dragon `.txt` loads.
   - Move `RespawnTick` load and save into the seam explicitly, because it isn't game data.
2. **Schema for the store of record:**
   - Add a `runtime_state` table (the phase notes plan one) for `RespawnTimer.CurrentTickcounter`.
   - Add a design table for `BaseSpawnRate` plus the `RespawnTickOption` list. Both are missing from the registry and from this export.
   - Decide what to do with `Spell == 0` magic rows.
   - Keep `index_counters` as loaded.
   - The `key` for position-keyed children (`parent*10000+pos`) isn't stable across deletes. A store of record should key them `(parent_index, position)` or give them a real id. The column is already there.
3. **Byte-identical round trip:**
   - Normalise or preserve `Stats` zero entries and order: `SortedDictionary` order is fine, but explicit `0` entries are written today.
   - `List<int>` null vs empty is already the same in binary.
   - `DateTime.ToBinary` keeps Kind.
   - The `ToolTip` null flag (`writer.Write(ToolTip != null)`) is carried by TEXT NULL.
4. **Shadow mode:** run this exporter's encoding (or the store's) off the game thread on a snapshot, then this verifier's comparison. Its output already names table, key and column per mismatch.
5. **Gate tooling:** exit codes are CI-friendly. Point `verify` at a copy of the live `Server.MirDB` (never `Build\` in place) to get the P7 step 1 gate number on real data.
