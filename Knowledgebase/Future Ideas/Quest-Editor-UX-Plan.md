# Quest Editor UX Plan (web admin › Game Data › Quests)

**Status:** proposed 2026-10-25; phases A, B, C, D and E implemented 2026-10-25 (backend meta / refs / runtime warnings / name sync /
`levelHint`, editor clean-up, the wizard, the Test and Texts tabs, hints + item icons + map place picker + area hunts + templates /
region / follow-up / save & test, docs). The manual smoke steps in B12, C, D and E still need a live server and a logged-in character.
**Goal:** make creating a new quest as quick as possible: a wizard for common quests, duplicate, fewer fields on screen, choices that
actually work at runtime, validation you can click, and testing a quest on an online character without leaving the page.
**Audience:** written for an implementing agent that has **not** read the code. Follow it literally. Where it says "exactly", copy the
shape. Line numbers are approximate (2026-10-25); find code by the function name given.

---

## 0. Read this first

### 0.1 Files you will touch (and nothing else)

| File | Phase | What |
|---|---|---|
| `Server/WebAdmin/Quests/QuestEditorDtos.cs` | A | 2 fields on `QuestProblemDto` |
| `Server/WebAdmin/Quests/QuestEditorService.cs` | A | Meta reserved flags, problem refs, runtime warnings, name sync, `levelHint` in the list |
| `Server.WebAdmin.Tests/QuestEditorTests.cs` | A | new tests |
| `Server.MirForms/wwwroot/admin/admin-quests.js` | B, C, D | editor changes |
| `Server.MirForms/wwwroot/admin/admin-quests.css` | B, C, D | styles (every selector starts with `#section-quests`) |
| `Server.MirForms/wwwroot/admin/admin-quests-wizard.js` | C | **new file**: the quick-create wizard |
| `Server.MirForms/wwwroot/admin/index.html` | C | one `<script>` line |
| `Server.WebAdmin.Tests/QuestEditorFrontendTests.cs` | C, D | update and extend the static checks |
| `Server/WebAdmin/README.md` | D | update the **Quests** bullet (~L229) |

No `Shared/` change, no packet change, no `Envir.Version` bump, no `SystemData.db` schema change, no runtime (`Server/MirObjects`,
`Server/MirEnvir`) change.

### 0.2 Rules (from `AGENTS.md` and `Knowledgebase/Server/server.md` §3.7). Breaking any of them is a failed phase

Frontend (`wwwroot/admin/*.js`):
- **NEVER** use `innerHTML`, `outerHTML` or `insertAdjacentHTML`. Build nodes with the file's `h(tag, props, ...children)` helper (it uses
  `textContent`). `QuestEditorFrontendTests` checks this.
- **NEVER** use `localStorage` / `sessionStorage` in `admin-quests.js` or the wizard. The test bans `localStorage` in the quests file.
  Keep page state in module variables.
- **NEVER** call `fetch(` directly. GETs use `OpsUI.get(url)`, which returns the parsed JSON or `null`. Mutations use
  `OpsUI.post(url, body, opts)`, which returns `{__ok, data, message, ...}`; it adds the CSRF header and shows a toast. Every URL must
  start with `/api/`.
- **ALWAYS** confirm destructive actions with `await portalConfirm(text, { title, confirmLabel, danger: true })`.
- **ALWAYS** report errors with `portalToast(text, 'error' | 'warn' | 'info')`.
- Keep the file's style: an IIFE `(function (global) { 'use strict'; ... })(window);`, `var`, `function` expressions, `async`/`await`
  allowed, **no** arrow functions, `let`/`const`, template literals, classes or ES modules. No new libraries, no npm, no CDN.
- CSS: every selector in `admin-quests.css` must start with `#section-quests` (a test checks this). Use the existing CSS variables
  (`--border`, `--border-dim`, `--border-bright`, `--well`) and give any new variable a fallback, e.g. `var(--warn, #d9a441)`.

Backend (C#):
- `QuestEditorService` runs on a web admin worker under `EditLock` (through `QuestEditorGate`). Do not touch `Envir.Main` from it.
- No new routes in phases A to C. Phase D only calls routes that already exist.
- **NEVER** return exception text to clients.
- Keep the UTF-8 BOM and the line endings of every file you edit.

Code style (all files):
- Comments only for *why* (a constraint, a runtime fact). **NEVER** restate the code. **NEVER** add `///` doc comments to private
  members. **NEVER** add banner or divider comments (`// ---- x ----`); the existing ones in `admin-quests.js` are known debt, so leave
  them alone but don't add new ones. Put new functions next to related code instead. **NEVER** leave commented-out code, and no
  change-log comments.

Build and test (**never** build the `.sln`, **never** write to `Build\`):
```
dotnet build C:\Mir2\Server\Server.Library.csproj
dotnet test C:\Mir2\Server.WebAdmin.Tests\Server.WebAdmin.Tests.csproj
node --check C:\Mir2\Server.MirForms\wwwroot\admin\admin-quests.js
node --check C:\Mir2\Server.MirForms\wwwroot\admin\admin-quests-wizard.js
```
Do **not** build `Server.MirForms\Server.csproj` (if you must, only with `-o <scratch dir>`). `wwwroot` is plain static files, so a JS
change needs no build.

After each phase, your reply must include a **"Never/Always Rules Followed"** section that lists each rule above as followed or not
applicable. Do not commit unless the user asks. If they ask, commit on `FuckingAround` with an imperative message, e.g.
"Add the quest wizard and duplicate to the web admin Quests editor".

### 0.3 Runtime facts the UI must respect (verified in code 2026-10-25)

| Thing | Works at runtime | Has **no** runtime (never fires / never given) |
|---|---|---|
| Objective types (`QuestObjectiveType`) | Kill, Collect, Deliver, Talk, ReachArea, UseItem, Craft, Gather, ReachLevel, UseSkill, Counter, Flag | Interact 12, Escort 13, Defend 14, PvpWin 15, Equip 16 (validation already **errors** on them) |
| Endpoint kinds (`QuestEndpointKind`) | Npc 0, Auto 3 | ItemUse 1, AreaEnter 2 (validation passes, nothing triggers them), Board 4 (validation errors) |
| Reward kinds (`QuestRewardKind`) | Exp 0, Gold 1, Credit 2, Item 3 | Reputation 4, Title 5, Spell 6, Unlock 7, Buff 8, Currency 9, ScriptPage 10 (silently skipped) |
| Reward scopes (`QuestRewardScope`) | Quest 0, Step 1, Outcome 3, Accept 4 (items only) | Objective 2 (never given) |
| Party credit (`QuestPartyCredit`) | Each 0, Self 2 | Shared 1 (enum comment: reserved) |
| Quest flags (`QuestFlags`) | AutoAccept 1, AutoComplete 2, HiddenInLog 4 | AccountWide 8 (reserved) |
| Target kinds (`QuestTargetKind`) | everything except MapObject | MapObject 6 (no objective type uses it) |

Other facts:
- **With no Finish endpoint, the Start NPCs also take the quest back** (`QuestCatalog.cs` ~L171). So "turn in to the giver" means
  *no Finish endpoint*.
- `ReachLevel`: `Count` is the required level. `Counter`: `Count` is the target count (raised by the `QUESTCOUNTER` script keyword).
  `Talk` / `ReachArea`: Count stays 1. `ReachArea`: `TargetId` = map, `X`/`Y`/`Radius` = the area (Radius > 0 is required).
- A step whose `NextStep` is 0 goes to the next step by `Order`. After the last step, the run is ready to turn in.
- `QuestInfo.Name` is set from the name text **only when the quest is created** (`QuestEditorService.Save`, ~L692). Pickers and the
  "quests" lookup show `QuestInfo.Name`, so a rename never shows up there. Phase A fixes this.
- Validation messages (`QuestDefinitions.Validate`, `QuestEditorService.Save`) are plain strings. Messages about a row start with
  `step N`, `objective N`, `condition N`, `variable N` / `variable id N`. Rewards contain ` reward `, endpoints contain `endpoint`,
  branches contain `branch`. In structural errors N is the id the page sent (new rows: a temporary id ≤ -1001). In `Validate` errors it
  is the counter id given out during the save, which the page does not know for new rows. Phase A maps those ids back.

---

## Phase A — Backend support (small, C# only)

### A1. Mark reserved enum members in `GET /api/admin/quests/meta`

In `QuestEditorService.cs`:

1. Add a field to the nested `EnumOption` class (~L267):
   ```csharp
   internal sealed class EnumOption
   {
       public int value;
       public string label;
       public bool reserved;
   }
   ```
2. Replace `EnumList<T>()` (~L264) with an overload that takes the reserved members:
   ```csharp
   private static List<EnumOption> EnumList<T>(params T[] reserved) where T : struct, Enum =>
       EnumListRaw<T>().Select(x => new EnumOption
       {
           value = x.value, label = x.label,
           reserved = reserved.Any(r => Convert.ToInt32(r, CultureInfo.InvariantCulture) == x.value),
       }).ToList();
   ```
3. In `Meta()` (~L218), change only these entries (leave the rest as they are):
   ```csharp
   ["flags"] = EnumList(QuestFlags.AccountWide).Where(e => e.value != 0).ToList(),
   ["objectiveType"] = EnumList(QuestObjectiveType.Interact, QuestObjectiveType.Escort, QuestObjectiveType.Defend, QuestObjectiveType.PvpWin, QuestObjectiveType.Equip),
   ["partyCredit"] = EnumList(QuestPartyCredit.Shared),
   ["targetKind"] = EnumList(QuestTargetKind.MapObject),
   ["endpointKind"] = EnumList(QuestEndpointKind.ItemUse, QuestEndpointKind.AreaEnter, QuestEndpointKind.Board),
   ["rewardScope"] = EnumList(QuestRewardScope.Objective),
   ["rewardKind"] = EnumList(QuestRewardKind.Reputation, QuestRewardKind.Title, QuestRewardKind.Spell, QuestRewardKind.Unlock,
       QuestRewardKind.Buff, QuestRewardKind.Currency, QuestRewardKind.ScriptPage),
   ```
   The other entries keep calling `EnumList<X>()` with no arguments (this compiles, because `params` can be empty).

### A2. Tell the page which row each problem is about

1. In `QuestEditorDtos.cs`, add to `QuestProblemDto`:
   ```csharp
   public string RefKind;            // "quest" | "step" | "objective" | "condition" | "variable" | "endpoint" | "reward" | "branch"
   public int RefId;                 // the row id as the page knows it (a temporary id for a row that is not saved yet); 0 = none
   ```
2. In `QuestEditorService.cs`, add a static helper next to `ToDto(QuestValidationProblem p)` (~L746):
   ```csharp
   private static readonly System.Text.RegularExpressions.Regex RowRef =
       new System.Text.RegularExpressions.Regex(@"^(step|objective|condition|variable)(?: id)? (-?\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

   // Validate names rows by the counter ids a save hands out; the page only knows the temporary ids of rows it has not saved yet.
   private static void Annotate(List<QuestProblemDto> problems, Dictionary<string, Dictionary<int, int>> realToTemp)
   {
       foreach (var p in problems)
       {
           if (p.RefKind != null) continue;
           var msg = p.Message ?? string.Empty;
           var m = RowRef.Match(msg);
           if (m.Success)
           {
               p.RefKind = m.Groups[1].Value;
               p.RefId = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
               if (realToTemp != null && realToTemp.TryGetValue(p.RefKind, out var map) && map.TryGetValue(p.RefId, out var temp)) p.RefId = temp;
           }
           else if (msg.Contains("endpoint", StringComparison.Ordinal)) p.RefKind = "endpoint";
           else if (msg.Contains(" reward ", StringComparison.Ordinal) || msg.StartsWith("Objective reward", StringComparison.Ordinal)) p.RefKind = "reward";
           else if (msg.Contains("branch", StringComparison.Ordinal)) p.RefKind = "branch";
           else p.RefKind = "quest";
       }
   }
   ```
   Add `using System.Text.RegularExpressions;` at the top instead of the full names if you prefer; check that nothing collides.
3. Call it in `Save` at three places:
   - Before the first `throw Refused(...)` (~L478, the structural errors). The ids there are the page's ids, so pass `null`:
     ```csharp
     if (errors.Count > 0)
     {
         var structural = errors.Select(m => new QuestProblemDto { Quest = questIndex, Message = m }).ToList();
         Annotate(structural, null);
         throw Refused(structural);
     }
     ```
   - The same at the second refusal (~L657-661, after `Restore(snap, true);`), also with `null`.
   - Right after `mine = problems.Where(p => p.Quest == questIndex).Select(ToDto).ToList();` (~L710), with reverse maps. `stepMap`,
     `objectiveMap`, `conditionMap` and `varIds` are in scope there:
     ```csharp
     var varRealToTemp = new Dictionary<int, int>();
     for (var i = 0; i < dto.Vars.Count; i++)
         if (dto.Vars[i].Index < 0) varRealToTemp[varIds[i]] = dto.Vars[i].Index;
     mine.AddRange(RuntimeWarnings(def, questIndex));          // A3
     Annotate(mine, new Dictionary<string, Dictionary<int, int>>
     {
         ["step"] = stepMap.ToDictionary(kv => kv.Value, kv => kv.Key),
         ["objective"] = objectiveMap.ToDictionary(kv => kv.Value, kv => kv.Key),
         ["condition"] = conditionMap.ToDictionary(kv => kv.Value, kv => kv.Key),
         ["variable"] = varRealToTemp,
     });
     ```
   - `List()` does not need it. In `Import` (~L1061), the inserted warnings may stay without a ref (the page treats a missing
     `refKind` as "quest").

### A3. Warn about choices that have no runtime (warnings never block a save)

Add next to `Annotate`:
```csharp
// Editor-only warnings for rows that save fine but do nothing in game (Validate stays the runtime's contract).
private static IEnumerable<QuestProblemDto> RuntimeWarnings(QuestDefinition def, int quest)
{
    QuestProblemDto W(string kind, int id, string message) => new QuestProblemDto { Quest = quest, Warning = true, RefKind = kind, RefId = id, Message = message };

    if (!def.Endpoints.Any(e => e != null && e.Role == QuestEndpointRole.Start && (e.Kind == QuestEndpointKind.Npc || e.Kind == QuestEndpointKind.Auto)))
        yield return W("endpoint", 0, "has no Start NPC (or Auto) endpoint: players cannot pick it up, only a script or a GM can start it");
    foreach (var e in def.Endpoints)
        if (e != null && (e.Kind == QuestEndpointKind.ItemUse || e.Kind == QuestEndpointKind.AreaEnter))
            yield return W("endpoint", 0, e.Role + " endpoint " + e.Kind + " has no runtime yet: use an Npc or Auto endpoint");
    foreach (var r in def.Rewards)
    {
        if (r == null) continue;
        if (r.Scope == QuestRewardScope.Objective)
            yield return W("reward", 0, "Objective reward " + r.Kind + " has no runtime yet: it is never given (use a Step reward)");
        else if (r.Kind != QuestRewardKind.Exp && r.Kind != QuestRewardKind.Gold && r.Kind != QuestRewardKind.Credit && r.Kind != QuestRewardKind.Item)
            yield return W("reward", 0, r.Scope + " reward " + r.Kind + " has no runtime yet: it is never given");
    }
    foreach (var o in def.Objectives)
        if (o != null && o.PartyCredit == QuestPartyCredit.Shared)
            yield return W(null, 0, "objective " + N(o.Index) + " party credit Shared is reserved (no runtime yet)");
}
```
The objective warning is created with `RefKind = null` on purpose: its message names the real (counter) id, and `Annotate` (which
only fills problems whose `RefKind` is null) parses it and maps it back to the page's temporary id.

### A4. Keep `QuestInfo.Name` / `Group` in step with the editor

In `Save`, just after `q.Definition = def;` (~L697):
```csharp
if (!isNew)
{
    var renamed = dto.Texts.TryGetValue(def.NameKey, out var nb2) ? FirstLine(nb2) : string.Empty;
    if (renamed.Length > 0) q.Name = renamed;
    q.Group = def.GroupName;
}
```
A dry run and a refusal roll back through `Restore`, which only restores `Definition` today. So also:
- Add `public string Name; public string Group;` to the nested `Snapshot` class (~L280).
- In `Take(q)` set `Name = q?.Name, Group = q?.Group`.
- In `Restore`, change `if (s.Quest != null) s.Quest.Definition = s.Definition;` to
  `if (s.Quest != null) { s.Quest.Definition = s.Definition; s.Quest.Name = s.Name; s.Quest.Group = s.Group; }`.

### A5. `levelHint` in the list

In `List()` (~L48), add `levelHint = q.Definition.LevelHint,` after `category = ...`.

### A6. Tests (`Server.WebAdmin.Tests/QuestEditorTests.cs`)

Use the existing `World()`, `FullQuest()`, `Editor(w)` and `T` helpers. Add:

1. `Meta_marks_reserved_enum_members`: serialise `svc.Meta()` with `WebAdminJson.Options`, parse it with `JsonDocument`, and assert
   that in `enums.objectiveType` the entry with `label == "Escort"` has `reserved == true` and `"Kill"` has `reserved == false`. Do the
   same check for `rewardKind` `Title` and `endpointKind` `ItemUse`.
2. `Dry_run_problems_name_new_rows_by_their_temporary_ids`: `var q = FullQuest(); q.Objectives[0].Count = 0;` then
   `svc.Save(q, true)`. A dry run with an error does **not** throw (only a real save throws). Look at the code: in the dry-run path it
   returns `Problems = mine`. Assert a problem with `RefKind == "objective"` and `RefId == T - 20`.
3. `Structural_errors_carry_the_page_ids`: `q.Steps[0].CompletionMode = 99;` → `Save(q, false)` throws 422. Parse with an updated
   `ProblemsOf` that also reads `refKind` / `refId` (extend the existing helper with
   `RefKind = p.TryGetProperty("refKind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null` and the same for
   `refId`). Assert `RefKind == "step"`, `RefId == T - 10`.
4. `Runtime_warnings_flag_objective_rewards_and_missing_givers`: save `FullQuest()` (it has an Objective-scope reward) and assert
   `result.Problems` contains a warning starting with `"Objective reward"`. Then `read = svc.Read(1); read.Endpoints.Clear();` save,
   and assert a warning containing `"has no Start NPC"`.
5. `Renaming_the_name_text_renames_QuestInfo`: save `FullQuest()`, read it, set `read.Texts["bichon.wolves.name"] = "Pack of Bichon"`,
   save, assert `w.QuestInfoList[0].Name == "Pack of Bichon"`. Then change it again in a **dry run** and assert the name is unchanged.

Existing tests: the new warnings may change warning counts. If a test asserts an **exact** warning count or an exact `Problems` list,
update only that number or list and add a short comment saying why. Do not weaken error assertions.

**Phase A done when:** `dotnet build C:\Mir2\Server\Server.Library.csproj` and `dotnet test ...Server.WebAdmin.Tests...` both pass.

---

## Phase B — Editor clean-up (`admin-quests.js` + `.css`)

Do the steps in order and run `node --check` after each.

### B1. Shared helpers

1. **`enumValue(name, label)`** replaces `kindValue`, `scopeValue` and `kindOp`:
   ```js
   function enumValue(name, label) {
     var hit = enumList(name).filter(function (o) { return o.label === label; })[0];
     return hit ? hit.value : 0;
   }
   ```
   Then delete `kindValue`, `scopeValue` and `kindOp` and update every call: `kindValue(x)` → `enumValue('targetKind', x)`,
   `scopeValue(x)` → `enumValue('rewardScope', x)`, `kindOp(x)` → `enumValue('conditionOp', x)`. Note: `scopeValue` returned -1 when
   nothing was found. The only caller (Remove step) compares with real scopes, so 0 is fine there; check it reads correctly afterwards.
2. **`enumChoices(name, current)`** hides reserved members unless the row already uses one:
   ```js
   function enumChoices(name, current) {
     return enumList(name).filter(function (o) { return !o.reserved || o.value === Number(current); })
       .map(function (o) { return o.reserved ? { value: o.value, label: o.label + ' (no runtime yet)' } : o; });
   }
   ```
   Change `enumSelect` to `return select(value, enumChoices(name, value), onChange, rerender);`.
   In `renderQuestForm`, build the flag checkboxes from
   `enumList('flags').filter(function (f) { return !f.reserved || (q.flags & f.value) !== 0; })`.
3. **Replace the magic `cmp: 5`** with `enumValue('compare', 'Ge')` in the two places that create a Leaf condition (in
   `renderConditionNode` "+ Part" and in `renderConditions` "+ Single check").
4. **Lookup details.** In `ensureLookups`, keep each option's `detail` and show it in the datalist. A datalist option **displays
   its `label` instead of its `value`** when both are set, so the label must repeat the name (a bare `label: o.detail` hides the name:
   the NPC dropdown showed only map names):
   ```js
   lookups[n].detailByIndex = {};
   (lookups[n].options || []).forEach(function (o) {
     if (!o.index) return;
     lookups[n].detailByIndex[o.index] = o.detail || '';
     dl.appendChild(h('option', { value: o.label + ' #' + o.index, label: o.detail ? o.label + ' · ' + o.detail : null }));
   });
   ```
   Add `function lookupDetail(name, index) { var l = lookups[name]; return (l && l.detailByIndex && l.detailByIndex[index]) || ''; }`.
   In `picker`, show the detail in the status span when the id resolves:
   `status.textContent = idx && !label ? 'not found' : (idx ? lookupDetail(name, idx) : '');`, and set the same text once at creation
   for the initial value. For NPCs the detail is the NPC's map name, which tells two "Guard"s apart.
5. **`jumpTo(tabId, ref)`** plus the focus flash. Add a module variable `var pendingFocus = null;` and:
   ```js
   function jumpTo(tabId, ref) {
     tab = tabId;
     pendingFocus = ref || null;
     renderEditor();
   }
   ```
   At the **end** of `renderEditor()` (after `host.scrollTop = scroll;`):
   ```js
   if (pendingFocus) {
     var target = host.querySelector('[data-ref="' + pendingFocus + '"]');
     pendingFocus = null;
     if (target) {
       target.scrollIntoView({ block: 'center' });
       target.classList.add('qe-flash');
       setTimeout(function () { target.classList.remove('qe-flash'); }, 1500);
     }
   }
   ```
   Add `data-ref` attributes (the `h()` helper passes unknown props to `setAttribute`):
   - step card in `renderSteps`: `h('div', { cls: 'qe-step', 'data-ref': 'step:' + s.index })`
   - objective row in `renderObjective`: `'data-ref': 'objective:' + o.index`
   - condition node in `renderConditionNode`: `'data-ref': 'condition:' + c.index`
   - variable row in `renderVars`: `'data-ref': 'variable:' + v.index`
   - in the Quest tab: the author key field `'data-ref': 'quest:authorKey'`, the Name text block `'quest:name'`, and the giver strip
     (B4) `'quest:giver'`. `field()` and `textField()` return the wrapper, so pass the attribute by wrapping or by
     `wrapper.setAttribute('data-ref', ...)` after creating it.

   CSS:
   ```css
   #section-quests .qe-flash { outline: 2px solid var(--warn, #d9a441); outline-offset: 2px; border-radius: 4px; }
   ```
6. **Create a condition from the field that uses it.** Replace `conditionSelect` (it needs `jumpTo` from step 5):
   ```js
   function conditionSelect(value, onChange) {
     var opts = conditionOptions().concat([{ value: 'new', label: '+ New condition…' }]);
     return select(value || 0, opts, function (v) {
       if (v !== 'new') { onChange(v); return; }
       var c = { index: newTemp(), op: enumValue('conditionOp', 'Leaf'), parent: 0, order: 0, leaf: enumValue('conditionLeaf', 'Level'), arg1: 1, arg2: 0, cmp: enumValue('compare', 'Ge') };
       quest.conditions.push(c);
       onChange(c.index);
       jumpTo('conditions', 'condition:' + c.index);
     });
   }
   ```
   `select()` passes `'new'` through as a string (`Number('new')` is NaN), so the check works.
7. **Readable condition labels.** In `conditionSummary(c, all)`, show compare symbols and names instead of raw numbers:
   - Symbols: `{ Eq: '=', Ne: '≠', Lt: '<', Le: '≤', Gt: '>', Ge: '≥' }[enumLabel('compare', c.cmp)]`.
   - Leaf text by `enumLabel('conditionLeaf', c.leaf)`: `ItemCount` → `'Has ' + sym + ' ' + c.arg2 + ' × ' + (lookupLabel('items', c.arg1) || '#' + c.arg1)`;
     `Equipped` → `'Wears ' + item name`; `MapIs` → `'On map ' + map name`; `QuestStatus` → `'Quest ' + quest name + ' is ' + enumLabel('runStatus', c.arg2)`;
     `QuestCompletedCount` → `'Completed ' + quest name + ' ' + sym + ' ' + c.arg2 + ' times'`; `Class` → `'Class ' + sym + ' ' + enumLabel('mirClass', c.arg1)`;
     `Gender` → the same with `mirGender`; `QuestVar` → var key (from `quest.vars`) + `' ' + sym + ' ' + c.arg2`; anything else →
     `leafName + ' ' + sym + ' ' + c.arg1 + (c.arg2 ? ' / ' + c.arg2 : '')`.
   - And / Or / Not: `'All of: '` / `'Any of: '` / `'Not: '` followed by the children's summaries joined with `'; '` (at most 3, then
     `' …'`, and recurse at most 3 levels deep). Children are `all.filter(function (k) { return k.parent === c.index; })`, sorted by
     `order`.

### B2. Durations as text ("90", "90s", "5m", "2h", "1d", "1h 30m")

Add:
```js
var DURATION_UNITS = { d: 86400, h: 3600, m: 60, s: 1 };

function parseDuration(text) {
  var s = String(text == null ? '' : text).trim().toLowerCase();
  if (!s) return 0;
  if (/^\d+$/.test(s)) return Number(s);
  var re = /(\d+)\s*([dhms])/g, m, total = 0, matched = '';
  while ((m = re.exec(s))) { total += Number(m[1]) * DURATION_UNITS[m[2]]; matched += m[0]; }
  return matched.replace(/\s+/g, '') === s.replace(/\s+/g, '') ? total : NaN;
}

function formatDuration(sec) {
  sec = Number(sec) || 0;
  if (sec <= 0) return '';
  var parts = [];
  ['d', 'h', 'm', 's'].forEach(function (u) {
    var n = Math.floor(sec / DURATION_UNITS[u]);
    if (n) { parts.push(n + u); sec -= n * DURATION_UNITS[u]; }
  });
  return parts.join(' ');
}

function durationInput(seconds, onChange, placeholder) {
  var e = h('input', { type: 'text', value: formatDuration(seconds), placeholder: placeholder || '0 = none', cls: 'qe-duration' });
  var status = h('span', { cls: 'qe-picker-name' });
  e.addEventListener('input', function () {
    var n = parseDuration(e.value);
    var bad = isNaN(n);
    status.textContent = bad ? 'use 90, 90s, 5m, 2h or 1d' : (n ? n + ' s' : '');
    status.className = 'qe-picker-name' + (bad ? ' qe-bad' : '');
    if (!bad) { onChange(n); markDirty(); }
  });
  e.addEventListener('change', function () { var n = parseDuration(e.value); if (!isNaN(n)) e.value = formatDuration(n); });
  return h('span', { cls: 'qe-picker-wrap' }, e, status);
}
```
Use it for: quest `cooldownSeconds` ("Cooldown"), quest `timeLimitSeconds` ("Time limit"), and step `timeLimitSeconds`. Drop the
`(s)` from those labels.

### B3. Basic / advanced quest form

Add a module variable `var showAdvanced = false;` (it stays for the page session; no storage). Rewrite `renderQuestForm(host)` so it
renders in this order:

1. The **quick setup strip** (B4).
2. **Basic grid** (`qe-grid`):
   - Author key: `input(...)` plus a `Suggest` button (B6), with hint "Unique; used by export files and scripts". `data-ref quest:authorKey`.
   - Category (`enumSelect('category', ...)`, rerender **true**, because B6's suggestion uses it).
   - Group (log).
   - Level hint.
   - Repeat (`enumSelect('repeatPolicy', ..., true)`, so the cooldown appears and disappears).
   - Cooldown (`durationInput`): **only** when `enumLabel('repeatPolicy', q.repeatPolicy) === 'Cooldown'` **or** `q.cooldownSeconds > 0`.
   - "Abandon allowed" checkbox.
3. **Texts** heading plus the three `textField`s (Name, Summary, Turn-in text), unchanged.
4. A toggle button:
   `button(showAdvanced ? 'Hide advanced settings' : 'Show advanced settings' + (n ? ' (' + n + ' set)' : ''), function () { showAdvanced = !showAdvanced; renderEditor(); })`,
   where `n = advancedCount(q)`.
5. When `showAdvanced`, a second `qe-grid`: Storyline, Storyline order, Accept condition, Visible condition, Max completions, Share,
   Credit, Time limit (`durationInput`), Fail policy, Pool, "Start item consumed", and Flags (the checkbox row).

```js
// Defaults as in blankQuest(): a value that differs means "set".
function advancedCount(q) {
  var n = 0;
  if (q.storylineIndex) n++; if (q.storylineOrder) n++; if (q.acceptCondition) n++; if (q.visibleCondition) n++;
  if (q.maxCompletions) n++; if (q.shareMode !== 1) n++; if (q.creditMode !== 1) n++; if (q.timeLimitSeconds) n++;
  if (q.failPolicy) n++; if (q.poolIndex) n++; if (q.startItemConsumed) n++; if (q.flags) n++;
  return n;
}
```
(Put each `if` on its own line if the file's style needs it; the file normally has one statement per line.)

### B4. Quick setup strip (giver, turn-in, exp, gold) on the Quest tab

A box `h('div', { cls: 'qe-essentials', 'data-ref': 'quest:giver' }, ...)` with a `qe-grid` inside:

```js
function endpointOf(roleName) {
  var role = enumValue('endpointRole', roleName), npc = enumValue('endpointKind', 'Npc');
  return quest.endpoints.filter(function (e) { return e.role === role && e.kind === npc; })[0] || null;
}

function setEndpointNpc(roleName, npcIndex) {
  var ep = endpointOf(roleName);
  if (!npcIndex) { if (ep) quest.endpoints.splice(quest.endpoints.indexOf(ep), 1); return; }
  if (ep) ep.targetId = npcIndex;
  else quest.endpoints.push({ role: enumValue('endpointRole', roleName), kind: enumValue('endpointKind', 'Npc'), targetId: npcIndex, map: 0 });
}

// The plain "always given" reward of a kind: quest scope, no choice, no condition, flat, every time.
function simpleReward(kindName) {
  var kind = enumValue('rewardKind', kindName), scope = enumValue('rewardScope', 'Quest');
  return quest.rewards.filter(function (r) {
    return r.kind === kind && r.scope === scope && !r.scopeId && !r.choiceGroup && !r.condition && !r.firstTimeOnly && !r.scaling;
  })[0] || null;
}

function setSimpleReward(kindName, amount) {
  var r = simpleReward(kindName);
  if (!amount) { if (r) quest.rewards.splice(quest.rewards.indexOf(r), 1); return; }
  if (r) r.amount = amount;
  else quest.rewards.push({ scope: enumValue('rewardScope', 'Quest'), scopeId: 0, kind: enumValue('rewardKind', kindName), targetId: 0, amount: amount, scaling: 0, choiceGroup: 0, condition: 0, firstTimeOnly: false });
}
```
Fields:
- **Giver NPC**: `picker('npcs', (endpointOf('Start') || {}).targetId || 0, function (v) { setEndpointNpc('Start', v); })`.
- **Turn in to**: `picker('npcs', (endpointOf('Finish') || {}).targetId || 0, function (v) { setEndpointNpc('Finish', v); })` with hint
  "Empty = back to the giver".
- **Exp** and **Gold**: `numberInput(simpleReward('Exp') ? simpleReward('Exp').amount : 0, function (v) { setSimpleReward('Exp', v); }, { min: 0 })`,
  and the same for Gold.
- A line: `quest.rewards.length + ' reward(s) in all'` with a button `Edit rewards` → `jumpTo('rewards')`.
- If there is more than one Start or Finish endpoint, add a muted note
  `'More givers on the Givers tab (' + quest.endpoints.length + ' endpoints)'` with a button → `jumpTo('endpoints')`.

`picker` already calls `markDirty()`. `setEndpointNpc` / `setSimpleReward` change the model only, which is enough.

CSS:
```css
#section-quests .qe-essentials { border: 1px solid var(--border-bright); border-radius: 6px; padding: 10px 12px; background: var(--well); margin-bottom: 12px; }
```

### B5. Objectives: show only what the type needs

Rewrite the field list in `renderObjective(o, siblings, sfx)`:

**Always visible** (one `qe-grid`):
- Type: `enumSelect('objectiveType', ..., true)` (unchanged handler).
- Target kind: **only when** `allowed.length > 1`.
- Target: `targetControl(...)`, **not** for `ReachLevel` / `Counter` (their target kind is None).
- Count, with the label and visibility from this table:

  | Type | Count label | Shown |
  |---|---|---|
  | Kill | How many | yes |
  | Collect, Deliver, UseItem, Craft, Gather | How many | yes |
  | ReachLevel | Level | yes |
  | Counter | Target count | yes |
  | UseSkill | Times | yes |
  | Talk, ReachArea, Flag | — | no (keep the value; new rows already use 1) |
- For `ReachArea` only: X, Y, Radius (hint "needed (> 0)") inline.
- For `Collect` only: Drop chance (unchanged).

**Collapsed** in `h('details', { cls: 'qe-more', open: anyMore || null }, h('summary', { text: 'More options' }), grid)`:
- Party credit, Condition, Optional, Hidden, and the Text override `textField`.
- For types other than ReachArea: Marker map, X, Y, Radius (hint "optional: shows the place on the tracker").
- `anyMore` = `o.partyCredit || o.condition || o.optional || o.hidden || o.textKey || (type !== 'ReachArea' && (o.map || o.x || o.y || o.radius))`.

The `h()` helper skips a prop whose value is `null` or `false`, so `open: anyMore ? true : null` works (true becomes the empty
attribute).

CSS:
```css
#section-quests .qe-more summary { cursor: pointer; font-size: 12px; color: var(--muted, #9aa); margin: 2px 0; }
#section-quests .qe-more[open] summary { margin-bottom: 6px; }
```

### B6. Author key: suggest it, and re-key a new quest's texts

```js
function slug(s) { return String(s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').substring(0, 40); }

function suggestAuthorKey(q) {
  var base = enumLabel('category', q.category).toLowerCase() + '.' + (slug(q.texts[q.nameKey]) || 'quest');
  var taken = {};
  list.forEach(function (x) { if (x.index !== q.index && x.authorKey) taken[x.authorKey.toLowerCase()] = true; });
  var k = base;
  for (var n = 2; taken[k.toLowerCase()]; n++) k = base + '-' + n;
  return k;
}

/* Renames every text key of the quest that starts with oldPrefix + '.' (texts move with it). Returns how many moved. */
function rekeyTexts(oldPrefix, newPrefix) {
  if (!oldPrefix || !newPrefix || oldPrefix === newPrefix) return 0;
  var moved = 0;
  function re(k) {
    if (!k || k.indexOf(oldPrefix + '.') !== 0) return k;
    var nk = newPrefix + k.substring(oldPrefix.length);
    if (quest.texts[nk] != null && nk !== k) return k;
    if (quest.texts[k] != null) { quest.texts[nk] = quest.texts[k]; delete quest.texts[k]; }
    moved++;
    return nk;
  }
  quest.nameKey = re(quest.nameKey); quest.summaryKey = re(quest.summaryKey); quest.completeKey = re(quest.completeKey);
  quest.steps.forEach(function (s) { s.titleKey = re(s.titleKey); s.textKey = re(s.textKey); s.readyTextKey = re(s.readyTextKey); });
  quest.objectives.forEach(function (o) { o.textKey = re(o.textKey); });
  quest.branches.forEach(function (b) { b.choiceKey = re(b.choiceKey); b.textKey = re(b.textKey); });
  return moved;
}
```
Wiring (all in `renderQuestForm`):
- The author key input: keep `input(...)`. Also remember `var prevKey = q.authorKey || 'quest';` when rendering, and add a `change`
  listener: if `quest.index === 0`, call `rekeyTexts(prevKey, quest.authorKey)`, then `renderEditor()`. The `'quest'` fallback matters
  because `textField` builds default keys from `(quest.authorKey || 'quest')`.
- `Suggest` button: `var old = q.authorKey || 'quest'; q.authorKey = suggestAuthorKey(q); if (!q.index) rekeyTexts(old, q.authorKey); markDirty(); renderEditor();`
- Auto-suggest: in `textField`, the Name field only (pass a 6th argument `onCommit`, or check `suffix === 'name'`). On the body's
  `change` event: if `quest.index === 0 && !quest.authorKey`, set `quest.authorKey = suggestAuthorKey(quest)`, then
  `rekeyTexts('quest', quest.authorKey)` and `renderEditor()`.
- **Saved quests (`index > 0`) are never re-keyed.** Branch `ChoiceKey` is what `C.QuestChoose` sends, so renaming keys of a live quest
  is not worth the risk.

### B7. "Ready to play" checklist under the editor head

```js
function readiness() {
  var out = [];
  var nameOk = !!(quest.nameKey && String(quest.texts[quest.nameKey] || '').trim());
  out.push({ ok: nameOk, label: nameOk ? 'Name' : 'Needs a name', tab: 'quest', ref: 'quest:name' });
  out.push({ ok: !!quest.authorKey, label: quest.authorKey ? 'Author key' : 'Needs an author key', tab: 'quest', ref: 'quest:authorKey' });
  var start = quest.endpoints.some(function (e) {
    return e.role === enumValue('endpointRole', 'Start') &&
      ((e.kind === enumValue('endpointKind', 'Npc') && e.targetId > 0) || e.kind === enumValue('endpointKind', 'Auto'));
  });
  out.push({ ok: start, warn: !start, label: start ? 'Giver' : 'No giver NPC', tab: 'quest', ref: 'quest:giver' });
  var noObj = sortedSteps().filter(function (s) { return !quest.objectives.some(function (o) { return o.step === s.index; }) && !quest.branches.some(function (b) { return b.step === s.index; }); })[0];
  out.push({ ok: !noObj, label: noObj ? stepLabel(noObj.index) + ' has no objective' : 'Objectives', tab: 'steps', ref: noObj ? 'step:' + noObj.index : null });
  var noTarget = quest.objectives.filter(function (o) {
    var t = enumLabel('objectiveType', o.type);
    return t !== 'ReachLevel' && t !== 'Counter' && t !== 'Flag' && !(o.targetId > 0);
  })[0];
  out.push({ ok: !noTarget, label: noTarget ? 'An objective has no target' : 'Targets', tab: 'steps', ref: noTarget ? 'objective:' + noTarget.index : null });
  out.push({ ok: quest.rewards.length > 0, warn: true, label: quest.rewards.length ? 'Rewards' : 'No rewards', tab: 'rewards' });
  return out;
}
```
In `renderEditor`, after the head (`qe-head`) and before the tabs:
```js
host.appendChild(h('div', { cls: 'qe-ready' }, readiness().map(function (r) {
  return h('button', { type: 'button', cls: 'qe-ready-item ' + (r.ok ? 'ok' : r.warn ? 'warn' : 'bad'),
    text: (r.ok ? '✓ ' : '• ') + r.label, onclick: function () { jumpTo(r.tab, r.ref); } });
})));
```
This is computed on each render only. It does not update while typing, which is fine.

CSS:
```css
#section-quests .qe-ready { display: flex; flex-wrap: wrap; gap: 6px; margin: 6px 0 8px; }
#section-quests .qe-ready-item { font-size: 12px; padding: 2px 8px; border-radius: 10px; border: 1px solid var(--border); background: transparent; cursor: pointer; }
#section-quests .qe-ready-item.ok { color: var(--ok, #6c6); }
#section-quests .qe-ready-item.warn { color: var(--warn, #d9a441); }
#section-quests .qe-ready-item.bad { color: var(--danger, #e66); border-color: var(--danger, #e66); }
```

### B8. Clickable validation problems

Change `problemList(items)`:
```js
var REF_TABS = { quest: 'quest', step: 'steps', objective: 'steps', condition: 'conditions', variable: 'vars', endpoint: 'endpoints', reward: 'rewards', branch: 'branches' };

function problemList(items) {
  if (!items || !items.length) return h('p', { cls: 'mir-muted', text: 'No problems.' });
  return h('ul', { cls: 'qe-problems' }, items.map(function (p) {
    var tabId = REF_TABS[p.refKind || 'quest'] || 'quest';
    var ref = p.refId && (p.refKind === 'step' || p.refKind === 'objective' || p.refKind === 'condition' || p.refKind === 'variable') ? p.refKind + ':' + p.refId : null;
    return h('li', { cls: p.warning ? 'qe-warn' : 'qe-bad' }, h('strong', { text: p.warning ? 'warning' : 'error' }), ' ' + p.message + ' ',
      quest ? h('button', { type: 'button', cls: 'qe-link', text: 'Show', onclick: function () { jumpTo(tabId, ref); } }) : null);
  }));
}
```
`problemList` is also used by the import preview. There `quest` may be a different quest, so pass a second argument
`problemList(items, linkable)` and only show `Show` when `linkable` is true (true from the Validation tab, false from `renderImport`).

### B9. Keyboard: Ctrl+S saves, Ctrl+Enter checks

1. In `renderEditor`, give the Check and Save buttons `data-role` values. `button()` does not take one, so set it after creating:
   `var checkBtn = button('Check', ...); checkBtn.setAttribute('data-role', 'check-btn');`, and the same for `save-btn`.
2. In `render(sectionEl)`, once:
   ```js
   sectionEl.addEventListener('keydown', function (e) {
     if (!quest || !(e.ctrlKey || e.metaKey)) return;
     var key = e.key.toLowerCase();
     if (key !== 's' && key !== 'enter') return;
     e.preventDefault();
     // Pickers commit on 'change': blur first so the focused field's value is in the model.
     if (document.activeElement && document.activeElement.blur) document.activeElement.blur();
     var dry = key === 'enter';
     save(dry, el(dry ? 'check-btn' : 'save-btn'));
   });
   ```
3. Add the shortcut to the buttons' `title`: Save "Save (Ctrl+S)", Check "Validate without saving (Ctrl+Enter)".

### B10. Duplicate

A `Duplicate` button in the editor head, shown when `quest.index > 0` (place it before `Export`):
```js
async function duplicateQuest() {
  if (isDirty()) { portalToast('Save or revert your changes first.', 'warn'); return; }
  var copy = duplicateGraph(quest);
  quest = normalise(copy);
  loadedJson = '';
  problems = [];
  tab = 'quest';
  markDirty();
  renderEditor();
  renderList();
  portalToast('Copy ready: check the texts, then Save to create it.', 'info');
}

/* A deep copy with every owned row on a new temporary id and new text keys, so saving it never touches the source quest's rows or texts. */
function duplicateGraph(src) {
  var q = JSON.parse(JSON.stringify(src));
  var steps = {}, objectives = {}, conditions = {}, vars = {};
  q.steps.forEach(function (s) { steps[s.index] = newTemp(); s.index = steps[s.index]; });
  q.objectives.forEach(function (o) { objectives[o.index] = newTemp(); o.index = objectives[o.index]; });
  q.conditions.forEach(function (c) { conditions[c.index] = newTemp(); c.index = conditions[c.index]; });
  q.vars.forEach(function (v) { vars[v.index] = newTemp(); v.index = vars[v.index]; });
  function st(id) { return id > 0 && steps[id] ? steps[id] : id; }
  function cd(id) { return conditions[id] || id; }          // shared conditions (not owned) keep their id
  q.steps.forEach(function (s) { s.nextStep = st(s.nextStep); s.failCondition = cd(s.failCondition); });
  q.objectives.forEach(function (o) { o.step = st(o.step); o.condition = cd(o.condition); });
  q.branches.forEach(function (b) { b.step = st(b.step); b.nextStep = st(b.nextStep); b.condition = cd(b.condition); });
  q.rewards.forEach(function (r) {
    if (r.scope === enumValue('rewardScope', 'Step')) r.scopeId = st(r.scopeId);
    else if (r.scope === enumValue('rewardScope', 'Objective')) r.scopeId = objectives[r.scopeId] || r.scopeId;
    r.condition = cd(r.condition);
  });
  q.conditions.forEach(function (c) {
    if (c.parent) c.parent = cd(c.parent);
    if (enumLabel('conditionLeaf', c.leaf) === 'QuestVar' && vars[c.arg1]) c.arg1 = vars[c.arg1];
  });
  q.acceptCondition = cd(q.acceptCondition);
  q.visibleCondition = cd(q.visibleCondition);

  var oldAk = src.authorKey || 'quest';
  var taken = {};
  list.forEach(function (x) { if (x.authorKey) taken[x.authorKey.toLowerCase()] = true; });
  var ak = oldAk + '-copy';
  for (var n = 2; taken[ak.toLowerCase()]; n++) ak = oldAk + '-copy-' + n;
  var texts = {};
  function key(k) {
    if (!k) return k;
    var nk = k.indexOf(oldAk + '.') === 0 ? ak + k.substring(oldAk.length) : k + '.copy';
    if (src.texts[k] != null) texts[nk] = src.texts[k];
    return nk;
  }
  q.nameKey = key(q.nameKey); q.summaryKey = key(q.summaryKey); q.completeKey = key(q.completeKey);
  q.steps.forEach(function (s) { s.titleKey = key(s.titleKey); s.textKey = key(s.textKey); s.readyTextKey = key(s.readyTextKey); });
  q.objectives.forEach(function (o) { o.textKey = key(o.textKey); });
  q.branches.forEach(function (b) { b.choiceKey = key(b.choiceKey); b.textKey = key(b.textKey); });
  if (q.nameKey) texts[q.nameKey] = (texts[q.nameKey] || '') + ' (copy)';
  q.texts = texts;
  q.authorKey = ak;
  q.index = 0; q.rev = null; q.name = ''; q.revision = 0; q.shapeHash = '0';
  return q;
}
```
Why the text keys **must** change: the server treats a key that another quest also uses as shared, so editing the copy's text would
also change the source quest's text (`KeysUsedOutside` in `Save`).

### B11. List: level and sort

- In `renderList`, change the key line to
  `(x.authorKey || '') + ' · ' + enumLabel('category', x.category) + (x.levelHint ? ' · Lv ' + x.levelHint : '')`.
- Add `sort: 'index'` to `filters`. Add a select in `render()` after the filters with options
  `[{value:'index',label:'By id'},{value:'name',label:'By name'},{value:'problems',label:'Problems first'}]` and a `change` handler
  that sets `filters.sort` and calls `renderList()`.
- In `filtered()`, sort the result before returning: `name` → `localeCompare` on `x.name || ''`; `problems` →
  `(b.errors - a.errors) || (b.warnings - a.warnings) || (a.index - b.index)`; `index` → `a.index - b.index`.

### B12. Phase B checks

`node --check` passes. `dotnet test ...Server.WebAdmin.Tests...` passes (the frontend tests read the JS and CSS). Then a **manual smoke**
for the user (you cannot run the live server; list these steps in your reply):
1. Open a quest. The checklist shows, and clicking a missing item scrolls to the field and flashes it.
2. Reserved options are gone from the dropdowns (e.g. objective type shows no Escort).
3. Repeat = Cooldown shows the Cooldown field; typing `2h` shows `7200 s`.
4. Giver / Exp / Gold in the strip show up correctly on the Givers / Rewards tabs.
5. Duplicate, then Save, creates a new quest; the source quest's texts are unchanged.
6. Ctrl+S saves; a failed save opens Validation, and **Show** jumps to the row.

---

## Phase C — Quick-create wizard (new file `admin-quests-wizard.js`)

### C1. Expose a kit from `admin-quests.js`

Replace the last line `global.QuestsPage = { blankQuest: blankQuest, isDirty: isDirty };` with:
```js
global.QuestsPage = {
  blankQuest: blankQuest,
  isDirty: isDirty,
  openDraft: openDraft,
  check: function () { return save(true, null); },
  meta: function () { return meta; },
  authorKeys: function () { return list.map(function (x) { return x.authorKey || ''; }); },
  kit: {
    h: h, clear: clear, field: field, input: input, numberInput: numberInput, checkbox: checkbox, select: select,
    enumSelect: enumSelect, enumChoices: enumChoices, enumList: enumList, enumLabel: enumLabel, enumValue: enumValue,
    button: button, picker: picker, targetControl: targetControl, lookupLabel: lookupLabel, durationInput: durationInput,
    newTemp: newTemp, idPart: idPart, slug: slug
  }
};
```
And add `openDraft`:
```js
async function openDraft(q) {
  if (!await confirmDiscard()) return false;
  quest = normalise(q);
  loadedJson = '';
  problems = [];
  tab = 'quest';
  markDirty();
  renderEditor();
  renderList();
  return true;
}
```
`save(true, null)` works: `OpsUI.withBusy(null, work)` just runs `work`.

Note: `input()`, `checkbox()`, `select()` and `picker()` call `markDirty()`. That is harmless while the wizard is open, because
`quest` is `null` and `isDirty()` returns false.

### C2. Open the wizard from "New"

Change `newQuest()`:
```js
async function newQuest() {
  if (!await confirmDiscard()) return;
  quest = null;
  loadedJson = '';
  problems = [];
  markDirty();
  renderList();
  if (global.QuestsWizard) { QuestsWizard.open(el('editor')); return; }
  await openDraft(blankQuest());
}
```
In `renderEditor`'s empty state (`if (!quest)`), add a button `New quest (wizard)` → `newQuest()` next to "Where used" / "Groups".

### C3. `index.html`

Add `<script src="admin-quests-wizard.js"></script>` on the line **after** `<script src="admin-quests.js"></script>` (~L194). It must stay
before `app.js`.

### C4. The wizard file

Skeleton (style as in 0.2):
```js
/*
 * Quests › New quest wizard: builds a whole quest graph for the common shapes (kill / collect / deliver / talk / go to / reach level,
 * one step per objective) and opens it as an unsaved draft in the Quests editor (QuestsPage.openDraft). It never calls the API itself.
 */
(function (global) {
  'use strict';

  var state = null;

  function k() { return global.QuestsPage.kit; }

  function fresh() {
    var K = k();
    return {
      name: '', summary: '', complete: '',
      category: K.enumValue('category', 'Side'), levelHint: 0, repeat: K.enumValue('repeatPolicy', 'Once'), cooldown: 0,
      giver: 0, finisher: 0, noGiver: false,
      steps: [blankStep()],
      exp: 0, gold: 0, items: [],
      errors: []
    };
  }

  function blankStep() { return { type: 'Kill', group: false, targetId: 0, count: 10, x: 0, y: 0, radius: 5, giveOnAccept: false, title: '' }; }

  function open(host) { state = fresh(); render(host); }

  global.QuestsWizard = { open: open };
})(window);
```

**Form layout** (render into `host`: `K.clear(host)`, then one `div.qe-wizard`). Each section is an `h4` plus a `qe-grid`:

1. **Basics:** Name (`input`, required), Category (`select` over `enumChoices('category')`), Level hint (`numberInput`), Repeat
   (`select` over `enumChoices('repeatPolicy')`, **re-render** on change), Cooldown (`durationInput`, only when the repeat label is
   `Cooldown`).
2. **Who:** Giver NPC (`picker('npcs', state.giver, ...)`), Turn in to (`picker('npcs', state.finisher, ...)`, hint
   "Empty = back to the giver"), and a checkbox "No giver (started by a script or a GM)" (`state.noGiver`).
3. **Steps:** one card (`div.qe-step`) per `state.steps[i]`:
   - Type: a `select` with exactly these options (value = the objective type label):
     `Kill` "Kill monsters", `Collect` "Collect items", `Deliver` "Deliver an item", `Talk` "Talk to an NPC",
     `ReachArea` "Go to a place", `ReachLevel` "Reach a level". Re-render on change, and reset `targetId` to 0.
   - Target, by type:
     - Kill: a checkbox "Any monster of a group" (`group`). Off: `picker('monsters', ...)`. On:
       `targetControl('MonsterGroup', ...)`.
     - Collect / Deliver: `picker('items', ...)`.
     - Talk: `picker('npcs', ...)`.
     - ReachArea: `picker('maps', ...)`, plus `X`, `Y`, `Radius` number inputs.
     - ReachLevel: none.
   - Count: Kill / Collect / Deliver "How many" (min 1); ReachLevel "Level" (min 1); Talk / ReachArea not shown (built as 1).
   - Deliver only: checkbox "Give the item(s) when the quest is accepted" (`giveOnAccept`).
   - Tracker title: `input` with placeholder = `defaultTitle(step)` (below); empty means the default is used.
   - Buttons: `↑`, `↓`, `Remove` (hidden when there is only one step).
   - Below the cards: `+ Add step` → `state.steps.push(blankStep())`, then re-render.
4. **Rewards:** Exp, Gold (`numberInput` min 0); items as rows of `picker('items')` + count + `×` remove; `+ Item reward`.
5. **Texts:** Summary (`textarea`, rows 3), Turn-in text (`textarea`, rows 3). Build each `textarea` with `K.h('textarea', ...)`, set
   `.value` and add an `input` listener.
6. **Errors** list (`ul.qe-problems` of `li.qe-bad` from `state.errors`), when not empty.
7. **Actions:** `Create draft` (primary), `Blank quest (advanced)` → `QuestsPage.openDraft(QuestsPage.blankQuest())`, and `Cancel` →
   `K.clear(host)` then `host.appendChild(K.h('p', { cls: 'mir-muted', text: 'Pick a quest on the left, or start a new one.' }))`.

Every input handler only writes `state.*`. Call `render(host)` again only where re-rendering is stated above (type, repeat, group
toggle, add/remove/move step, add/remove item). Re-rendering throws away the focus; that is acceptable for those clicks.

**Default titles:**
```js
function defaultTitle(s) {
  var K = k();
  var mon = s.group ? 'monsters' : (K.lookupLabel('monsters', s.targetId) || 'monsters');
  switch (s.type) {
    case 'Kill': return 'Kill ' + s.count + ' ' + mon;
    case 'Collect': return 'Collect ' + s.count + ' ' + (K.lookupLabel('items', s.targetId) || 'items');
    case 'Deliver': return 'Deliver ' + (K.lookupLabel('items', s.targetId) || 'the item') + ' to ' + (K.lookupLabel('npcs', state.finisher || state.giver) || 'the NPC');
    case 'Talk': return 'Talk to ' + (K.lookupLabel('npcs', s.targetId) || 'the NPC');
    case 'ReachArea': return 'Go to ' + (K.lookupLabel('maps', s.targetId) || 'the place');
    case 'ReachLevel': return 'Reach level ' + s.count;
  }
  return '';
}
```
(Group names come from `QuestsPage.meta().groups`, `{index, authorKey, kind, members}`. For a group, use its `authorKey` instead of
"monsters".)

**Checks before building** (`state.errors = []`, push a message for each; if any, re-render and stop):
- Name is empty → "Give the quest a name."
- No giver and `noGiver` is off → "Pick a giver NPC, or tick 'No giver'."
- For each step i (1-based): a type other than ReachLevel with `targetId <= 0` → "Step i: pick a target." ReachArea with
  `radius <= 0` → "Step i: the radius must be above 0." Count < 1 where it is shown → "Step i: the count must be at least 1."
- Deliver with no `finisher` → "Deliver: pick the NPC to turn in to (Turn in to)." (Delivering to the giver is allowed but is usually a
  mistake, so make this a hard check.)
- Each item reward row with no item → "Item reward n: pick an item."

**Build the graph** (`build()` returns the graph, then `QuestsPage.openDraft(graph).then(function (ok) { if (ok) QuestsPage.check(); })`):
```js
function build() {
  var K = k(), P = global.QuestsPage;
  var q = P.blankQuest();                 // one step, temp ids
  q.steps = [];
  q.category = state.category;
  q.levelHint = state.levelHint;
  q.repeatPolicy = state.repeat;
  q.cooldownSeconds = K.enumLabel('repeatPolicy', state.repeat) === 'Cooldown' ? state.cooldown : 0;

  var base = K.enumLabel('category', state.category).toLowerCase() + '.' + (K.slug(state.name) || 'quest');
  var taken = {};
  P.authorKeys().forEach(function (a) { if (a) taken[a.toLowerCase()] = true; });
  var ak = base;
  for (var n = 2; taken[ak.toLowerCase()]; n++) ak = base + '-' + n;
  q.authorKey = ak;

  function text(fieldKey, body) { if (!String(body || '').trim()) return ''; q.texts[fieldKey] = body; return fieldKey; }
  q.nameKey = text(ak + '.name', state.name.trim());
  q.summaryKey = text(ak + '.summary', state.summary);
  q.completeKey = text(ak + '.complete', state.complete);

  var targetKinds = { Kill: 'Monster', Collect: 'Item', Deliver: 'Item', Talk: 'Npc', ReachArea: 'Map', ReachLevel: 'None' };
  state.steps.forEach(function (s, i) {
    var stepId = K.newTemp();
    var sfx = ak + '.s' + K.idPart(stepId);
    q.steps.push({ index: stepId, order: i + 1, titleKey: text(sfx + '.title', s.title.trim() || defaultTitle(s)), textKey: '', readyTextKey: '',
      completionMode: 0, nRequired: 0, nextStep: 0, timeLimitSeconds: 0, failCondition: 0, onEnterPage: '', onCompletePage: '' });
    var kind = s.type === 'Kill' && s.group ? 'MonsterGroup' : targetKinds[s.type];
    var counted = s.type === 'Kill' || s.type === 'Collect' || s.type === 'Deliver' || s.type === 'ReachLevel';
    q.objectives.push({ index: K.newTemp(), step: stepId, order: 1, type: K.enumValue('objectiveType', s.type), partyCredit: 0,
      targetKind: K.enumValue('targetKind', kind), targetId: s.type === 'ReachLevel' ? 0 : s.targetId, count: counted ? s.count : 1,
      map: 0, x: s.type === 'ReachArea' ? s.x : 0, y: s.type === 'ReachArea' ? s.y : 0, radius: s.type === 'ReachArea' ? s.radius : 0,
      optional: false, hidden: false, condition: 0, textKey: '', dropChance: 0, params: '' });
    if (s.type === 'Deliver' && s.giveOnAccept)
      q.rewards.push(reward('Accept', 'Item', s.targetId, s.count));
  });

  var npc = K.enumValue('endpointKind', 'Npc');
  if (!state.noGiver && state.giver) q.endpoints.push({ role: K.enumValue('endpointRole', 'Start'), kind: npc, targetId: state.giver, map: 0 });
  if (state.finisher && state.finisher !== state.giver) q.endpoints.push({ role: K.enumValue('endpointRole', 'Finish'), kind: npc, targetId: state.finisher, map: 0 });
  if (state.exp > 0) q.rewards.push(reward('Quest', 'Exp', 0, state.exp));
  if (state.gold > 0) q.rewards.push(reward('Quest', 'Gold', 0, state.gold));
  state.items.forEach(function (it) { q.rewards.push(reward('Quest', 'Item', it.id, it.count || 1)); });
  return q;

  function reward(scopeName, kindName, targetId, amount) {
    return { scope: K.enumValue('rewardScope', scopeName), scopeId: 0, kind: K.enumValue('rewardKind', kindName), targetId: targetId,
      amount: amount, scaling: 0, choiceGroup: 0, condition: 0, firstTimeOnly: false };
  }
}
```
Notes:
- `blankQuest()` has already used one temporary id for its default step. Dropping that step (`q.steps = []`) only skips that number,
  which is harmless.
- A Kill step with a group uses `targetControl('MonsterGroup', ...)`, whose value is the group index.
- For `ReachArea`, the "Go to" target **is** the map (`targetId`). `map` (the marker map) stays 0.
- The draft is unsaved, so the user still clicks Save in the editor. The automatic `QuestsPage.check()` shows problems straight away
  (for example a missing text).

**CSS** (in `admin-quests.css`):
```css
#section-quests .qe-wizard { display: flex; flex-direction: column; gap: 10px; }
#section-quests .qe-wizard h4 { margin: 8px 0 0; }
#section-quests .qe-wizard textarea { width: 100%; }
#section-quests .qe-wizard-actions { display: flex; gap: 8px; margin-top: 8px; }
```

### C5. Frontend test (`QuestEditorFrontendTests.cs`)

Add:
```csharp
[Fact]
public void Wizard_is_loaded_after_the_page_and_never_calls_the_api()
{
    var index = File.ReadAllText(Path.Combine(AdminDir(), "index.html"));
    var page = index.IndexOf("<script src=\"admin-quests.js\"></script>", StringComparison.Ordinal);
    var wizard = index.IndexOf("<script src=\"admin-quests-wizard.js\"></script>", StringComparison.Ordinal);
    var app = index.IndexOf("app.js", StringComparison.Ordinal);
    Assert.True(page >= 0 && wizard > page && wizard < app);

    var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-quests-wizard.js"));
    Assert.DoesNotContain("/api/", js);
    Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
    Assert.DoesNotContain("innerHTML", js);
    Assert.DoesNotContain("insertAdjacentHTML", js);
    Assert.DoesNotContain("localStorage", js);
    Assert.Contains("QuestsPage.openDraft", js);
}
```

**Phase C done when** the tests and `node --check` pass. Manual smoke for the user: New → wizard → "Kill 10 Wolf" with a giver and 500 exp
→ Create draft → the editor opens with one step, one Kill objective, a Start endpoint and an Exp reward, and Check reports no errors →
Save creates it.

---

## Phase D — Test on character, texts table, docs

### D1. "Test" tab (saved quests only)

Add the two routes to the endpoint list in the comment at the top of `admin-quests.js`. Add `{ id: 'test', label: 'Test' }` to `TABS`
after `validation`. In `renderEditor`'s tab loop, skip it when `!quest.index`. In the
switch: `case 'test': renderTest(pane2); break;`.

Module state: `var testState = { players: null, character: 0, step: 0 };`

```js
async function loadPlayers() {
  var j = await OpsUI.get('/api/admin/players-online');
  testState.players = (j && j.rows) || [];
  testState.players.sort(function (a, b) { return (b.isGM ? 1 : 0) - (a.isGM ? 1 : 0) || String(a.name).localeCompare(String(b.name)); });
  if (!testState.players.some(function (p) { return p.characterIndex === testState.character; }))
    testState.character = testState.players.length ? testState.players[0].characterIndex : 0;
}

async function questAction(action, extra, btn) {
  if (!testState.character) { portalToast('Pick an online character first.', 'warn'); return; }
  var url = '/api/admin/characters/' + testState.character + (action === 'reset' ? '/quests/reset' : '/quests/edit');
  var body = Object.assign({ questIndex: quest.index }, action === 'reset' ? {} : { action: action }, extra || {});
  await OpsUI.withBusy(btn, function () { return OpsUI.post(url, body); }, '…');
}
```
Response shapes (existing routes, `Server/WebAdmin/Routes/PlayerRoutes.cs`, `CharacterRoutes.cs` ~L586 / ~L662):
- `GET /api/admin/players-online` → `{ rows: [{ characterIndex, name, level, className, mapTitle, isGM, ... }], total, world }`. The list
  is empty while the world is stopped.
- `POST /api/admin/characters/{index}/quests/edit` `{ questIndex, action: 'start' | 'complete' | 'step', step? }`. Online it uses the
  same calls as GM SETQUEST. `complete` records a completion **without rewards**. `step: -1` means ready to turn in.
- `POST /api/admin/characters/{index}/quests/reset` `{ questIndex }` removes the run and the history row.
- All three are audited on the server. `OpsUI.post` shows the server's message as a toast.

`renderTest(host)`:
1. If `testState.players === null`: show "Loading…", call `loadPlayers().then(function () { renderEditor(); })`, and return.
2. If `isDirty()`: a warning paragraph (`qe-warn`): "Unsaved changes: the test uses the saved version. Save first."
3. If no players: "No one is online. Log in a test character, then Refresh." plus a `Refresh` button
   (`testState.players = null; renderEditor();`).
4. Otherwise a `qe-grid`:
   - Character: `select(testState.character, players.map(function (p) { return { value: p.characterIndex, label: p.name + ' (Lv ' + p.level + ', ' + (p.mapTitle || '?') + ')' + (p.isGM ? ' [GM]' : '') }; }), function (v) { testState.character = v; })`
     plus a `Refresh` button.
   - Buttons in `qe-actions`:
     - `Start` → `questAction('start', null, btn)`.
     - A step select (`sortedSteps()` with `index > 0`, labels from `stepLabel`, plus `{ value: -1, label: 'Ready to turn in' }`) and
       `Jump to step` → `questAction('step', { step: testState.step }, btn)`. Default `testState.step` to the first step's index.
     - `Complete (no rewards)` → `if (await portalConfirm('Mark quest #' + quest.index + ' completed on this character? No rewards are given.', { title: 'Complete quest', confirmLabel: 'Complete' })) questAction('complete', null, btn)`.
     - `Reset` (class `qe-danger`) → `portalConfirm('Remove this quest's progress and history from the character?', { title: 'Reset quest', confirmLabel: 'Reset', danger: true })`,
       then `questAction('reset', null, btn)`.
   - A muted note: "Start skips the accept checks. To test the real offer, talk to the giver in game."
   - A link to the character drawer. First find how other pages link to it:
     `Grep "#section=characters" Server.MirForms/wwwroot/admin --glob "*.js"`, and copy that pattern (the hash is
     `#section=characters&index=<characterIndex>`).

### D2. "Texts" tab (proofreading every text of the quest in one table)

Add `{ id: 'texts', label: 'Texts' }` to `TABS` after `vars`. `renderTexts(host)` lists every text field:
```js
function textFields() {
  var out = [];
  function add(label, key, setKey, suffix, multiline) { out.push({ label: label, key: key, setKey: setKey, suffix: suffix, multiline: multiline }); }
  add('Quest name', quest.nameKey, function (k) { quest.nameKey = k; }, 'name', false);
  add('Summary', quest.summaryKey, function (k) { quest.summaryKey = k; }, 'summary', true);
  add('Turn-in text', quest.completeKey, function (k) { quest.completeKey = k; }, 'complete', true);
  sortedSteps().forEach(function (s) {
    var sfx = 's' + idPart(s.index), n = stepLabel(s.index);
    add(n + ' title', s.titleKey, function (k) { s.titleKey = k; }, sfx + '.title', false);
    add(n + ' log text', s.textKey, function (k) { s.textKey = k; }, sfx + '.text', true);
    add(n + ' ready text', s.readyTextKey, function (k) { s.readyTextKey = k; }, sfx + '.ready', true);
  });
  quest.objectives.forEach(function (o) { add('Objective ' + (o.index > 0 ? '#' + o.index : '(new)') + ' text', o.textKey, function (k) { o.textKey = k; }, 'o' + idPart(o.index), false); });
  quest.branches.forEach(function (b, i) {
    add('Branch ' + (i + 1) + ' choice', b.choiceKey, function (k) { b.choiceKey = k; }, 's' + idPart(b.step) + '.choice', false);
    add('Branch ' + (i + 1) + ' text', b.textKey, function (k) { b.textKey = k; }, 's' + idPart(b.step) + '.branch', true);
  });
  return out;
}
```
Render one `textField(f.label, f.key, f.setKey, f.suffix, f.multiline)` per entry. It reuses the existing component, so key handling
stays the same. Add a checkbox at the top, "Only empty texts" (module variable `textsOnlyEmpty`), that filters to entries where
`!f.key || !String(quest.texts[f.key] || '').trim()`.

### D3. Update the frontend API test

In `Api_calls_stay_within_the_quest_editor_routes`, change the expected list to a set comparison:
```csharp
Assert.Equal(new[] { "/api/admin/characters/", "/api/admin/players-online", "/api/admin/quests" }, urls.OrderBy(u => u, StringComparer.Ordinal).ToArray());
```
and update the test's summary line to say the page also calls the characters' quest edit routes for the Test tab. The regex captures
the literal `'/api/admin/characters/'`. Keep the URL built as `'/api/admin/characters/' + index + '/quests/edit'` so the literal
matches.

### D4. Docs

In `Server/WebAdmin/README.md`, extend the **Quests** bullet (~L229-237) with one or two sentences: the New wizard (common quest
shapes, opens an unsaved draft), Duplicate, the quick setup strip, basic/advanced settings, reserved choices hidden (`reserved` in
meta), clickable validation (`refKind` / `refId`), and the Test tab (start / jump to step / complete / reset on an online character
through the characters' quest routes). Don't add a changelog line.

Then set **Status** at the top of this file to the phases that are done.

**Phase D done when** the tests and `node --check` pass. Manual smoke for the user: log in a character, open a saved quest, Test →
Start → the quest appears in the client's log → Jump to step "Ready to turn in" → the giver offers the turn-in → Reset removes it.

---

## Phase E — Maps, area hunts, hints, suggestions, templates, fewer clicks

**Goal:** a person who doesn't know the data tables can make a quest by clicking. Places are picked on the map, hunts can target
"whatever spawns here", every picker says where things are, rewards and the level hint are suggested, and common shapes are one click.
Phases A to D must be done first (they are, as of 2026-10-25).

### E0. Read first

**Files.** Same rules as §0.2. Phase E adds or touches:

| File | Part | What |
|---|---|---|
| `Server/WebAdmin/Quests/QuestHints.cs` | E1 | **new**: hint data (spawns, drops, NPC place, map respawns) |
| `Server/WebAdmin/Quests/QuestEditorRoutes.cs` | E1 | 2 GET routes |
| `Server/WebAdmin/MapArt/MapArtService.cs` | E1 | `ItemIconPng` (next to `MiniMapPng`) |
| `Server.WebAdmin.Tests/QuestHintsTests.cs` | E1 | **new** tests |
| `Server.MirForms/wwwroot/admin/admin-mapeditor-picker.js` + `admin-mapeditor-view.js` | E2 | optional radius square in `MapPicker.pick` |
| `Server.MirForms/wwwroot/admin/admin-quests-assist.js` | E3 | **new**: `QuestsAssist`, shared by the editor and the wizard |
| `Server.MirForms/wwwroot/admin/admin-quests.js` | E4, E9 | kit additions, hints under pickers, editor upgrades |
| `Server.MirForms/wwwroot/admin/admin-quests-wizard.js` | E5-E8 | wizard upgrades |
| `Server.MirForms/wwwroot/admin/admin-quests.css` | all | styles (scoped `#section-quests`; the map picker keeps its own `me-` classes in `admin-mapeditor.css`) |
| `Server.MirForms/wwwroot/admin/index.html` | E3 | one `<script>` line |
| `Server.WebAdmin.Tests/QuestEditorFrontendTests.cs` | E10 | static checks for the new file |
| `Server/WebAdmin/README.md` | E10 | Quests bullet |

**More runtime facts (verified 2026-10-25):**
- **The ReachArea area is a square, not a circle:** `PlayerQuests.cs` ~L748 skips the area when `|px - X| > Radius || |py - Y| > Radius`.
  Draw it as a square and call it "area" in the UI, never "circle".
- **An objective's condition gates its progress:** `PlayerQuests.cs` ~L352 and ~L655 skip the objective when
  `o.Condition != 0 && !EvaluateCondition(o.Condition)`. The `MapIs` leaf compares the player's map with `Arg1` using `Cmp`
  (`PlayerQuests.Conditions.cs` ~L84). So "kills only count on this map" = an owned Leaf condition `{ leaf: MapIs, arg1: map, cmp: Eq }`
  set as the Kill objective's `condition`.
- `QuestStatus` leaf: `arg1` = quest index, `arg2` = a `QuestRunStatus` (Completed = 4), `cmp` Eq. This is used for follow-up quests.
- **Respawns:** `MapInfo.Respawns` is a `List<RespawnInfo>` with `MonsterIndex`, `Location` (Point), `Count`, `Spread`.
  `MonsterInfo` has `Level`, `Experience`, `IsBoss`, `DropPath`. `NPCInfo` has `MapIndex`, `Location`, `Name`. `ItemInfo` has `Price`,
  `Image`, `RequiredClass`. All of these are in `QuestWorld` (`MapInfoList`, `MonsterInfoList`, `NPCInfoList`, `ItemInfoList`), so the
  hints are testable on a detached `QuestWorld`, as `QuestEditorTests` does.
- **Drops** live in `DropTables` (`Server/MirEnvir/Storage/Design/DropTables.cs`): `DropTables.Lists()` lists every list,
  `DropTables.ForMonster(index, name, dropPath)` gives a monster's list, and `DropRow` has `Kind` (`DropKind.Item` / `Include` / `Group` /
  …), `Chance` (the N of "1/N"), `ItemIndex`, `Parent` (≥ 0 inside a group) and `IncludeListId`. `DropRoutes` reads these on HTTP
  threads without a lock. Copy that, and only when `DesignStore.Active` is true.
- `MapPicker.pick(opts)` (`admin-mapeditor-picker.js`) is a modal: a map list plus `MapEditorView`. It resolves `{ map, x, y }` or
  `null`. The view draws the picked cell with `drawPin` (`admin-mapeditor-view.js` ~L743) and has `rectPx(v, {x, y, r})` for squares.
- GM teleport (existing, audited): `POST /api/admin/gm/action` with
  `{ action: 'teleport', character: <name>, characterIndex: <index>, map: '#' + mapIndex, x, y, reason }` (see `teleportOne` in
  `admin-mapeditor.js` ~L1080).
- Minimap image: `GET /api/admin/mapeditor/maps/{index}/minimap` (404 when `[WebAdmin] ClientDataPath` is unset or the map has no
  minimap). The terrain image `.../terrain` always works when the map file exists. Both are plain `<img>` GETs (cookie auth, no CSRF).
- There is **no** item icon endpoint today (`admin-lookups.js` L8). E1 adds one for the Quests page only.

**Where API calls may live.** The wizard stays API-free (the test `Wizard_..._never_calls_the_api` stays). Every new call goes in the
new `admin-quests-assist.js` (E3). The `admin-quests.js` URL test stays as it is.

---

### E1. Backend: hints and item icons

**E1.1 `Server/WebAdmin/Quests/QuestHints.cs` (new, `internal static class QuestHints`, namespace `Server.WebAdmin.Quests`).** Start the
file with a UTF-8 BOM like its neighbours. One `<summary>` on the class: read-only, HTTP worker, the world parts read under `EditLock`
through `QuestEditorGate.Read`, and drops outside the lock (as `DropRoutes`).

```csharp
public sealed class SpawnHint { public int map; public string mapTitle; public int respawns; public int count; }

private static string MapTitle(MapInfo m) => string.IsNullOrWhiteSpace(m.Title) ? m.FileName : m.Title;

public static object Monster(QuestWorld w, int id)
{
    var m = w.MonsterInfoList.FirstOrDefault(x => x != null && x.Index == id);
    if (m == null) return null;
    var spawns = new List<SpawnHint>();
    foreach (var map in w.MapInfoList)
    {
        if (map == null) continue;
        var rows = map.Respawns.Where(r => r != null && r.MonsterIndex == id).ToList();
        if (rows.Count == 0) continue;
        spawns.Add(new SpawnHint { map = map.Index, mapTitle = MapTitle(map), respawns = rows.Count, count = rows.Sum(r => (int)r.Count) });
    }
    return new
    {
        kind = "monster", id, name = m.Name, level = (int)m.Level, experience = (long)m.Experience, boss = m.IsBoss,
        spawns = spawns.OrderByDescending(s => s.count).Take(10).ToList(),
    };
}
```
Keep the lowercase field names shown in every hint shape (they are what the page reads).

```csharp
public static object Npc(QuestWorld w, int id)       // { kind:"npc", id, name, map, mapTitle, x, y } or null
public static object Item(QuestWorld w, int id)      // { kind:"item", id, name, price, image, requiredClass (the enum name), droppedBy: [] }  (droppedBy filled by the route)
public static object Map(QuestWorld w, int id)       // see below, or null
```
`Map` returns
`{ kind: "map", id, title, fileName, miniMap, npcs: [{ index, name, x, y }], respawns: [{ monster, name, level, boss, x, y, spread, count }] }`,
with `name`/`level`/`boss` looked up in `MonsterInfoList` (unknown monster: name `"#" + index`, level 0). The route adds `width` and
`height`.

Drops (testable without the store):
```csharp
public sealed class DropHint { public int monster; public string name; public int chance; public bool grouped; }

// Which monsters drop an item: each monster's own list plus the lists it includes (followed once each, cycles ignored). The best
// (lowest N) chance per monster wins.
public static List<DropHint> DroppedBy(long itemIndex, IEnumerable<(int Index, string Name, string DropPath)> monsters,
    Func<int, string, string, DropList> listOf, Func<long, DropList> byId)
{
    var best = new Dictionary<int, DropHint>();
    foreach (var (index, name, dropPath) in monsters)
    {
        var root = listOf(index, name, dropPath);
        if (root == null) continue;
        var seen = new HashSet<long>();
        var stack = new Stack<DropList>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var l = stack.Pop();
            if (l == null || !seen.Add(l.Id)) continue;
            foreach (var r in l.Rows)
            {
                if (r.Kind == DropKind.Include && r.IncludeListId is long inc) stack.Push(byId(inc));
                else if (r.Kind == DropKind.Item && r.ItemIndex == itemIndex && r.Chance > 0
                         && (!best.TryGetValue(index, out var h) || r.Chance < h.chance))
                    best[index] = new DropHint { monster = index, name = name, chance = r.Chance, grouped = r.Parent >= 0 };
            }
        }
    }
    return best.Values.OrderBy(h => h.chance).ThenBy(h => h.name, StringComparer.OrdinalIgnoreCase).Take(15).ToList();
}
```
Check that `DropList`, `DropRow` and `DropKind` are visible from `Server.WebAdmin` (they are `internal` in the same assembly; `DropRoutes`
uses them) and add `using Server.MirEnvir.Storage;` if needed (see `DropRoutes.cs`'s usings).

**E1.2 Routes in `QuestEditorRoutes.Register`:**
```csharp
router.Get("/api/admin/quests/hints", GetHints);
router.Get("/api/admin/quests/item-icon/{index:int}", GetItemIcon);
```
`GetHints`: `kind` = `monster | item | npc | map` (anything else → 400 "'kind' must be one of: monster, item, npc, map"), `id` > 0 (else
400). Then:
1. `var hint = QuestEditorGate.Read(s => s.Hint(kind, id));`. Add a thin `Hint` method to `QuestEditorService` that switches to the
   `QuestHints` functions with `_w`. This keeps the world access behind the same gate as the rest of the page.
2. `null` → 404 "No <kind> #<id>".
3. `item`: when `DesignStore.Active`, take a monster snapshot under `EditLock` exactly like `DropRoutes.Snapshot()` (Index, Name, DropPath),
   then call `QuestHints.DroppedBy(id, monsters, (i, n, p) => DropTables.ForMonster(i, n, p), DropTables.Get)` **outside** the lock. Return
   the item object plus `droppedBy` and `dropsKnown = DesignStore.Active`. The cleanest way is to have `Item(...)` return a small class
   with a settable `droppedBy` list instead of an anonymous object.
4. `map`: `var walk = MapEditorService.Walkability(fileName);` **outside** the gate (file IO, as `MapEditorRoutes.GetOverlay` does), and
   add `width = walk?.Width ?? 0, height = walk?.Height ?? 0`. Again: return a class with settable fields.
5. `req.Json(hint)`.

`GetItemIcon`: look up `ItemInfo.Image` for the item index via `QuestEditorGate.Read`. 404 when the item doesn't exist. Then
`MapArtService.ItemIconPng(image)` → 404 "No item icon (is [WebAdmin] ClientDataPath set?)" when null. Otherwise
`req.SetHeader("Cache-Control", "private, max-age=300"); return req.Bytes(png, "image/png");` (copy `GetMiniMap` in `MapEditorRoutes.cs`).

**E1.3 `MapArtService.ItemIconPng(int image)`:** copy `MiniMapLib()` + `MiniMapPng()` exactly as `ItemsLib()` + `ItemIconPng()`, with the
library `Path.Combine(root, "Items.Lib")`, its own cache field `_items` and its own `ConcurrentDictionary<int, byte[]> ItemIconPngs`
(look at how `MiniMapPngs` is declared and copy it). `image < 0` → null. The client's item icons are `Data\Items.Lib`, and `Root` is the
client Data folder (the minimap reads `Root\MMap.Lib`).

**E1.4 Tests (`Server.WebAdmin.Tests/QuestHintsTests.cs`).** Build a `QuestWorld` as `QuestEditorTests.World()` does, plus
`w.MapInfoList[0].Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = new Point(20, 20), Count = 5, Spread = 3 })` and a second
row for monster 2 at (60, 60):
1. `Monster_hint_lists_where_it_spawns`: `QuestHints.Monster(w, 1)` serialised with `WebAdminJson.Options` has `level`, one spawn on map 1
   with `count == 5`.
2. `Npc_hint_gives_map_and_cell`: NPC 1 → map 1, x 10, y 10, `mapTitle == "Bichon"`.
3. `Map_hint_lists_respawns_with_monster_names`: 2 respawns, the first `name == "Wolf"`.
4. `DroppedBy_follows_includes_once_and_keeps_the_best_chance`: build three `DropList`s in the test (A: Item 2 at chance 20, plus an
   Include of B; B: Item 2 at chance 5, plus an Include of A, which is a cycle; C: no item 2). Monster 1 → A, monster 3 → C. Expect
   one hint: monster 1, chance 5. Construct `DropList { Id = 1, Rows = new[] { new DropRow { Kind = DropKind.Item, ItemIndex = 2, Chance = 20 }, ... } }`
   and pass lambdas for `listOf` / `byId`.

**E1 done when** Server.Library builds and the WebAdmin tests pass.

---

### E2. Map picker: an optional area square

In `admin-mapeditor-picker.js` `pick(opts)`:
1. A new option `opts.radius` (number). Only when `opts.radius != null`, add to the footer, before the `note` span:
   `el('span', 'mir-muted', 'Area')`, a `−` button, a number input `ri` (`min 0`, `max 50`, class `mir-input me-picker-xy`) and a `+`
   button. `state.radius = Math.max(0, num(opts.radius, 0))`. The buttons and the input set `state.radius` (clamp 0..50), then
   `view.setPinRadius(state.radius)`.
2. `confirm()` resolves `{ map, x, y, radius: state.radius }` when `opts.radius != null`, else `{ map, x, y }` as today. That keeps
   every existing caller unchanged.
3. In `check()`, when `opts.radius != null` and the overlay is loaded, add how many monster spawns touch the area:
   `var n = (state.overlay.respawns || []).filter(function (r) { var sp = num(r.Spread, 0); return Math.abs(num(r.Location_X) - state.x) <= state.radius + sp && Math.abs(num(r.Location_Y) - state.y) <= state.radius + sp; }).length;`
   and, when the cell is fine, show `note.textContent = n + ' monster spawn(s) in this area';` (only if `bad` is empty).
4. After `view.pin(...)` / `view.showCell(...)` (in `setCell`) and after `loadMap`, call `view.setPinRadius(state.radius || 0)`.

In `admin-mapeditor-view.js`:
- Add `st.pinRadius = 0` next to where `st.pin` is reset (~L168), and `setPinRadius: function (r) { st.pinRadius = Math.max(0, Number(r) || 0); queueDraw(); }`
  to the returned object (~L906).
- In `drawPin(ctx, v, p)`, before the two arcs:
  ```js
  if (st.pinRadius > 0) {
    var sq = rectPx(v, { x: p.x, y: p.y, r: st.pinRadius });
    ctx.fillStyle = 'rgba(244, 114, 182, 0.12)';
    ctx.fillRect(sq.x, sq.y, sq.w, sq.h);
    ctx.strokeStyle = 'rgba(244, 114, 182, 0.95)';
    ctx.lineWidth = 1.5;
    ctx.strokeRect(sq.x, sq.y, sq.w, sq.h);
  }
  ```
  (`drawPin` is declared inside `create`, so `st` and `rectPx` are in scope. Check this; if not, pass the radius in as an argument.)

The map editor never passes `radius`, so it is unchanged. Note in the picker's header comment: `radius` → an area square and a Radius
control, resolved as `radius`.

---

### E3. `admin-quests-assist.js` (new): `QuestsAssist`

Load it in `index.html` **between** `admin-quests.js` and `admin-quests-wizard.js`. IIFE, same style. Header comment: what it is, and the
URLs it uses. Contents:

```js
var API = '/api/admin/quests';
var cache = {};                     // 'kind:id' -> Promise<hint|null>, for the page session

function hints(kind, id) {
  id = Number(id) || 0;
  if (!id) return Promise.resolve(null);
  var key = kind + ':' + id;
  if (!cache[key]) cache[key] = OpsUI.get(API + '/hints?kind=' + encodeURIComponent(kind) + '&id=' + id);
  return cache[key];
}
function clearHints() { cache = {}; }
function itemIconUrl(id) { return API + '/item-icon/' + Number(id); }
```

**`hintLine(kind, id)`** returns a `span.qe-hint-line` filled asynchronously (`hints(kind, id).then(...)`; null → leave it empty):
- `monster`: `'Lv ' + level + ' · ' + experience + ' exp' + (boss ? ' · boss' : '')`, then `' · spawns on '` + the first 3 spawns as
  `mapTitle + ' (×' + count + ')'`, joined with `', '`, plus `' …'` if there are more. No spawns → `' · no spawns'` with class `qe-warn`.
- `item`: `droppedBy.length` → `'Dropped by '` + the first 4 as `name + ' 1/' + chance + (grouped ? '*' : '')` (and a `title` saying "*
  inside a drop group"). Empty and `dropsKnown` → `'No monster drops it (shop, craft or script only?)'` with class `qe-warn`. Not
  `dropsKnown` → nothing.
- `npc`: `mapTitle + ' (' + x + ', ' + y + ')'`, then two small link buttons: `Map` (sets `location.hash` to
  `'#section=mapeditor&map=' + map + '&x=' + x + '&y=' + y`; check how other pages deep-link before choosing `location.hash`) and
  `Teleport…` → `teleportPrompt(map, x, y)`.

**`teleportPrompt(map, x, y)`**: `GET /api/admin/players-online`. No rows → toast "No one is online.". Otherwise
`OpsUI.actionDialog(...)` with a select of the online characters (GMs first). Read `actionDialog`'s signature in `admin-ops-common.js`
and copy a caller such as the map editor's `teleportOne` dialog (~L1088). Then
`OpsUI.post('/api/admin/gm/action', { action: 'teleport', character: p.name, characterIndex: p.characterIndex, map: '#' + map, x: x, y: y, reason: 'Quest editor' })`.

**`placeLine(place, onChange, opts)`**, where `place = { map, x, y, radius }`. It returns a `div.qe-place` with:
- A **thumbnail** (`thumbnail(place)`, below).
- A text line: map title (from `hints('map', place.map)`, else `'Pick a place'`) + `' (' + x + ', ' + y + ')'` + (radius != null ?
  `' · area ' + (2 * radius + 1) + '×' + (2 * radius + 1)` : '').
- A button `Pick on map…`:
  `MapPicker.pick({ map: place.map || -1, x: place.x, y: place.y, radius: opts.radius ? (place.radius || opts.defaultRadius || 5) : null, walkableOnly: true, title: opts.title || 'Pick the place' })`.
  On a result, set `place.map/x/y` (and `radius` when given), call `onChange(place)`, then re-render the line in place: replace the old
  `div` with a new `placeLine(...)`. Guard `if (!global.MapPicker)` → toast "The map picker isn't loaded.".
- A toggle `Numbers` that shows Map / X / Y / Radius number inputs (for typing coordinates). They write into `place` and call `onChange`.

**`thumbnail(place)`** returns a `canvas.qe-thumb` (160 × 120), drawn when both arrive: the image (`img.src =
'/api/admin/mapeditor/maps/' + map + '/minimap'`, and on `error` try `.../terrain` once, then give up, leaving a grey box) and
`hints('map', map)` for `width`/`height`. Scale the image to fit the canvas while keeping its aspect ratio. Draw the marker at
`(x / width, y / height)` of the drawn image: a 3 px dot, plus a square of half-size `radius / width × drawnWidth` (at least 3 px) when
`radius > 0`. Colours: `rgba(244, 114, 182, …)` as in the picker. No map → only the grey box.

**`areaMonsters(place)`** → `Promise<[{ monster, name, level, boss, count, respawns }]>` from `hints('map', place.map)`. Keep the respawns
whose square touches the area (`|rx - x| <= radius + spread && |ry - y| <= radius + spread`), grouped by `monster` (summing `count`, counting
`respawns`), sorted by `count` descending.

**`suggest(input)`**, the reward and level suggestion. It is pure apart from `hints`, and **all tuning sits in one object at the top of
the file**:
```js
// First guesses for authors, not balance rules: tune these numbers to the server.
var TUNING = { killExpFactor: 1.5, stepExpBase: 100, stepExpPerLevelSq: 20, goldPerLevelPerStep: 50, itemPriceFactor: 0.5 };
```
`input = { steps: [{ type, monsters: [ids], count, item, level }], levelHint }`. Returns `Promise<{ level, exp, gold }>`:
- `level`: the average `level` of every monster in Kill steps (one value per step: the mean of its monsters), and for ReachLevel the
  step's `count`. The result is the max of those, rounded. Nothing → 0.
- `exp`: Kill steps add `mean monster experience × count × killExpFactor`. Every other step adds
  `stepExpBase + stepExpPerLevelSq × L²`, where `L = level || levelHint || 1`. Round to a whole number.
- `gold`: `goldPerLevelPerStep × L × steps`, plus Collect / Deliver `item price × count × itemPriceFactor`. Round.

**`saveGroup(members, keyBase)`**: reuse a group with exactly the same member set (sorted compare) from `QuestsPage.meta().groups` with
kind Monster (0). Otherwise POST `API + '/groups/save'` with `{ group: { index: 0, authorKey: key, kind: 0, members: members } }`, where
`key` = `keyBase`, or `keyBase + '-2'`, `-3`, … until no group has it (case-insensitive). On `__ok`, call `await QuestsPage.reloadMeta()`
(E4) and return `j.data.index`. Otherwise return 0 (the post already showed a toast).

**`players()`**: GET `/api/admin/players-online`, rows sorted GMs first, then by name. This is shared by `teleportPrompt` and the
wizard's "Save & test".

Export:
`global.QuestsAssist = { hints: hints, clearHints: clearHints, hintLine: hintLine, placeLine: placeLine, thumbnail: thumbnail, areaMonsters: areaMonsters, suggest: suggest, saveGroup: saveGroup, players: players, teleportPrompt: teleportPrompt, itemIconUrl: itemIconUrl, TUNING: TUNING };`

CSS:
```css
#section-quests .qe-hint-line { display: block; font-size: 11px; color: var(--muted, #9aa); margin-top: 2px; }
#section-quests .qe-hint-line.qe-warn { color: var(--warn, #d9a441); }
#section-quests .qe-place { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
#section-quests .qe-thumb { width: 160px; height: 120px; border: 1px solid var(--border); border-radius: 4px; background: var(--well); }
#section-quests .qe-icon { width: 24px; height: 24px; image-rendering: pixelated; vertical-align: middle; margin-right: 6px; }
```

---

### E4. `admin-quests.js`: hooks for the assist and the wizard

1. **Hints under pickers:** in `picker(name, value, onChange)`, when `global.QuestsAssist` and `name` is `monsters` / `items` / `npcs`, add
   `var hintHost = h('span');` after `status`, fill it with `QuestsAssist.hintLine(kind, value)` (kind = `monster` / `item` / `npc`), and
   in `sync()` replace its child with `hintLine(kind, idx)` (or clear it when `idx` is 0). This automatically covers the editor and the
   wizard.
2. **Item icons:** in the item picker only, put `h('img', { cls: 'qe-icon', alt: '', src: QuestsAssist.itemIconUrl(value) })` in front
   of the input when `value > 0`. Hide it on `error` (`img.style.display = 'none'`) and update `src` in `sync()`.
3. **`picker` option `opts.list`:** a 4th argument `opts`. When `opts.list` is set, use it as the datalist id instead of `'qe-dl-' + name`
   (the region datalists, E7.3).
4. **`save()` returns the result:** `return r;` at the end of the success path (dry run too), `return null` on every other exit. Expose
   `saveNow: function () { return save(false, el('save-btn')); }`.
5. **`reloadMeta`:** expose `reloadMeta: function () { return loadMeta(); }` (`loadMeta` is async). After a group save, the group pickers
   rebuild their options from `meta.groups` on the next render.
6. **`startOn(characterIndex)`:** refactor `questAction(action, extra, btn)` to take the character as a parameter
   (`questAction(action, extra, btn, character)`, defaulting to `testState.character`) and expose
   `startOn: function (ch) { return questAction('start', null, null, ch); }`. Make `questAction` `return` the `OpsUI.post` result.
7. **`wizard(prefill)`:** expose `wizard: function (prefill) { ... }`, which does what `newQuest()` does but calls
   `QuestsWizard.open(el('editor'), prefill)` (E7.2 / E7.4). `QuestsWizard.open(host, prefill)` must accept a missing `prefill`.
8. Extend `kit` with `picker` (already there; it now takes `opts`), `jumpTo`, and `textArea` if you move the wizard's `textArea` into the
   page (optional).

---

### E5. Wizard: places on the map, and "monsters in an area"

Change `blankStep()` to
`{ type: 'Kill', killMode: 'one', targetId: 0, count: 10, place: { map: 0, x: 0, y: 0, radius: 5 }, area: null, picked: [], onlyHere: true, giveOnAccept: false, title: '' }`.
Replace the old `group` boolean with `killMode` everywhere (`'one'`, `'group'`, `'area'`), and replace `s.x/s.y/s.radius` with
`s.place`.

**Go to a place** (`ReachArea`): instead of the Map / X / Y / Radius fields, use
`QuestsAssist.placeLine(s.place, function (p) { s.targetId = p.map; refreshTitle(); }, { radius: true, defaultRadius: 5, title: 'Where must the player go?' })`.
`build()` uses `targetId = s.place.map`, `x/y/radius = s.place.*`. `checkState`: `!s.place.map` → "Step i: pick the place on the map."

**Kill:** a `Target` select with `one` "One monster", `group` "Any monster of a group" and `area` "Monsters in an area" (re-render on
change):
- `one` / `group`: as today.
- `area`:
  1. `QuestsAssist.placeLine(s.place, onPlace, { radius: true, defaultRadius: 20, title: 'Where do they hunt?' })`. `onPlace` calls
     `QuestsAssist.areaMonsters(s.place)`, then stores the list in `s.area`, sets `s.picked` to every non-boss monster id, and re-renders.
  2. When `s.area` is set: one checkbox per monster, `name + ' (Lv ' + level + ', ×' + count + (boss ? ', boss' : '') + ')'`, ticked when
     its id is in `s.picked`. An empty area → muted "No monster spawns in this area: make it bigger or move it.".
  3. A checkbox "Only kills on this map count" (`s.onlyHere`, default **on**). Its hint: "Kills anywhere count otherwise; the area is
     shown on the tracker".
- `checkState` for `area`: `!s.place.map` → "Step i: pick the hunting area."; `!s.picked.length` → "Step i: tick at least one monster.".

**Building an area step** (this makes `create()` async; keep the API calls in the assist):
```js
async function resolveAreas() {
  for (var i = 0; i < state.steps.length; i++) {
    var s = state.steps[i];
    if (s.type !== 'Kill' || s.killMode !== 'area') continue;
    if (s.picked.length === 1) { s.resolvedTarget = { kind: 'Monster', id: s.picked[0] }; continue; }
    var map = await QuestsAssist.hints('map', s.place.map);
    var base = 'area.' + K.slug((map && map.title) || ('map-' + s.place.map)) + '.' + K.slug(K.lookupLabel('monsters', s.picked[0]) || 'monsters');
    var gid = await QuestsAssist.saveGroup(s.picked.slice().sort(function (a, b) { return a - b; }), base);
    if (!gid) return false;
    s.resolvedTarget = { kind: 'MonsterGroup', id: gid };
  }
  return true;
}
```
In `create()`: after `checkState()` passes, `if (!await resolveAreas()) return;`, then build. Tell the author in the wizard (a muted line
under the area list) that a group named `area.…` will be created when they click Create (it is shared and reused). In `build()`, for an
area step: `targetKind` / `targetId` from `s.resolvedTarget`; `map/x/y/radius` = `s.place.*` (the tracker marker); and when `s.onlyHere`:
```js
var condId = K.newTemp();
q.conditions.push({ index: condId, op: K.enumValue('conditionOp', 'Leaf'), parent: 0, order: 0, leaf: K.enumValue('conditionLeaf', 'MapIs'), arg1: s.place.map, arg2: 0, cmp: K.enumValue('compare', 'Eq') });
// objective.condition = condId
```
For `one` / `group` Kill steps, optionally also allow a marker: an "Add a map marker" link that shows a `placeLine` with
`{ radius: true, defaultRadius: 10 }` and fills `map/x/y/radius`.

**Default title for area:** `'Kill ' + count + ' ' + (picked.length === 1 ? monster name : 'monsters') + ' near ' + mapTitle`. The map
title comes from the cached `hints('map', ...)`; while it's loading, use `'the area'`.

---

### E6. Wizard: hints, auto level, suggested rewards, item rewards

1. **Hints under pickers** come from E4.1. Nothing more to do.
2. **NPCs on different maps:** in the Who section, after both pickers, add a `span.qe-hint-line` that, once both `hints('npc', …)`
   resolve, says "The turn-in NPC is on <map>, the giver on <map>." with class `qe-warn` when the maps differ. Empty otherwise.
3. **Auto level hint:** add `state.levelTouched = false`. The Level hint input's handler sets it to true. Under the field, show
   `Suggested: N` with a `Use` button. Get N from `QuestsAssist.suggest(toSuggestInput())`. In `create()`, if `!state.levelTouched` and
   the suggestion is > 0, use it.
   ```js
   function toSuggestInput() {
     return { levelHint: state.levelHint, steps: state.steps.map(function (s) {
       var monsters = s.type !== 'Kill' ? [] : s.killMode === 'area' ? s.picked : s.killMode === 'group' ? groupMembers(s.targetId) : (s.targetId ? [s.targetId] : []);
       return { type: s.type, monsters: monsters, count: s.count, item: (s.type === 'Collect' || s.type === 'Deliver') ? s.targetId : 0 };
     }) };
   }
   ```
   `groupMembers(id)` = the `members` of the meta group with that index.
   Refresh the suggestion text only when the steps change (a re-render), not on every keystroke.
4. **Suggested exp and gold:** a `Suggest` button next to Exp and Gold (one button fills both): `suggest(...)` → set `state.exp` /
   `state.gold`, re-render. Under the reward grid add a muted line: "Server exp / gold rates are applied on top.".
5. **Item rewards:** each row shows the item icon (E4.2 is automatic in the picker) and its `requiredClass` from `hints('item', id)` as a
   small tag when it isn't `None`. Above the rows, a checkbox "The player picks one of these" (`state.pickOne`). In `build()`, the item
   rewards then get `choiceGroup: 1`. With fewer than 2 items the checkbox is disabled.

---

### E7. Wizard: templates, region, follow-ups

1. **Templates.** Above Basics, a row `Start from:` with buttons. Each button replaces `state.steps` (asking first with `portalConfirm`
   when steps already have targets) and sets the fields shown:

   | Button | Sets |
   |---|---|
   | Blank | one Kill step (today's default) |
   | Hunt | one Kill step, `killMode 'area'`, count 10 |
   | Hunt then report | Kill (`area`) + Talk (target = `state.giver` if set) |
   | Fetch and deliver | Collect (count 5) + Deliver (same item: `targetId` copied when the Collect target is picked; leave it 0 here) |
   | Travel chain | ReachArea + Talk + ReachArea |
   | Daily bounty | Kill (`area`), count 20, `category = Daily`, `repeat = Daily` |

   Note: the turn-in already goes back to the giver (or the Turn in NPC), so no template ends with "talk to the giver" except
   "Hunt then report", where the report is a separate step on purpose (it can carry Step rewards).
2. **Start from an existing quest** (the "save as template" idea): add `QuestsWizard.open(host, prefill)` support. `prefill` may be
   `{ fromGraph: graph }`. `fromGraph` → `stateFromGraph(graph)`:
   - It refuses (toast "This quest has parts the wizard can't show; use Duplicate instead." and opens nothing) when the graph has
     branches, more than one objective in a step, an objective type outside the wizard's six, conditions other than one `MapIs` per
     Kill objective, or Step / Outcome / Objective rewards.
   - Otherwise it maps: category, level, repeat, cooldown, giver / finisher from the endpoints, one wizard step per step (Kill with a
     MonsterGroup target → `killMode 'group'`; a marker becomes `place`), exp / gold / items from the Quest-scope rewards, the Accept item
     reward → `giveOnAccept`. **Not** copied: the name (empty, so the author types one), summary and turn-in texts, tracker titles.
   - In the editor head, a `Use as template` button (saved quests only, when not dirty) → `QuestsPage.wizard({ fromGraph: quest })`.
3. **Region.** A `Region` field at the top of the wizard: `picker('maps', state.region, ...)`, empty by default. When it's set, fetch
   `hints('map', region)` and build two datalists inside the wizard box: `qe-dl-region-npcs` (its `npcs`) and `qe-dl-region-monsters`
   (the distinct monsters of its `respawns`). Option values must be `label + ' #' + index`, the same format `picker` parses. Then pass
   `{ list: 'qe-dl-region-npcs' }` / `{ list: 'qe-dl-region-monsters' }` to the NPC and monster pickers, and default every new `place.map`
   to the region. A small `Show all` checkbox (`state.regionAll`) switches the pickers back to the global lists.
4. **Follow-up quest.** In the editor head (saved quests only), a `Follow-up quest` button →
   `QuestsPage.wizard({ after: { index: quest.index, name: quest.name }, giver: <the quest's first Start NPC>, category: quest.category, levelHint: quest.levelHint, storylineIndex: quest.storylineIndex, storylineOrder: quest.storylineOrder })`.
   `fresh(prefill)` copies these into the state. The wizard shows a line "Unlocks after #N Name (×)" (the × clears `state.after`). In
   `build()`, when `state.after`:
   ```js
   var c = K.newTemp();
   q.conditions.push({ index: c, op: K.enumValue('conditionOp', 'Leaf'), parent: 0, order: 0, leaf: K.enumValue('conditionLeaf', 'QuestStatus'), arg1: state.after.index, arg2: K.enumValue('runStatus', 'Completed'), cmp: K.enumValue('compare', 'Eq') });
   q.acceptCondition = c;
   q.storylineIndex = state.storylineIndex || 0;
   q.storylineOrder = state.storylineIndex ? (state.storylineOrder || 0) + 1 : 0;
   ```

---

### E8. Wizard: texts, preview, fewer clicks

1. **Live tracker title placeholder:** in `stepCard`, keep a reference to the title input and add
   `function refreshTitle() { titleInput.placeholder = defaultTitle(s); }`. Call it from every target, count, place and area callback of
   that step, so the placeholder follows the picks without a re-render.
2. **Write the texts for me:** a button next to the Texts heading. It fills Summary and Turn-in text **only when they are empty**
   (otherwise it asks with `portalConfirm` before overwriting):
   - Summary: one sentence per step, joined with a space:
     Kill `'Kill ' + count + ' ' + target + (place ? ' near ' + mapTitle : '') + '.'`; Collect `'Bring me ' + count + ' ' + item + '.'`;
     Deliver `'Take the ' + item + ' to ' + finisherName + '.'`; Talk `'Speak with ' + npc + '.'`; ReachArea `'Travel to ' + mapTitle + '.'`;
     ReachLevel `'Come back when you reach level ' + count + '.'`. Then, unless the last step is a Talk to the turn-in NPC,
     `' Then return to ' + (finisherName || giverName) + '.'`.
   - Turn-in: `'Well done. Here is your reward.'` (with exp / gold: `'Well done. Take this for your trouble.'`).
   - Use plain words only (no `%name%`-style tokens: the client doesn't substitute them).
3. **Player preview:** on wide screens (`@media (min-width: 1400px)`) the wizard becomes two columns (`.qe-wizard-cols`): the form on the
   left and `div.qe-preview` (sticky) on the right. Re-render the preview on each `render(host)` and also, cheaply, from text inputs (a
   `renderPreview()` that only rebuilds the preview node). It shows:
   - **Offer** box: the name, the summary, then "Rewards:" with exp / gold / item names × count (items `(choose one)` when `pickOne`).
   - **Tracker** box: per step, the title (or `defaultTitle`), then the objective line `target + ' 0/' + count` (Talk / ReachArea:
     `target + ' 0/1'`).
   - A muted note: "Approximate: the game client draws the real dialog."
4. **Smart defaults:**
   - Picking a Talk step's NPC while `state.finisher` is empty shows a link under it, `Finish at this NPC`, which sets
     `state.finisher` (no automatic change).
   - `+ Add step` copies the previous step's `type`, `killMode`, `place` (a copy) and `count`, with an empty target.
   - A second quick button next to it: `+ Then talk to…` adds a Talk step.
5. **Drag to reorder:** step cards get `draggable="true"`, and the head shows a `⠿` handle. `dragstart` stores the index in
   `e.dataTransfer.setData('text/plain', i)`, `dragover` calls `preventDefault()`, and `drop` moves the step to the drop target's index,
   then re-renders. Keep ↑ / ↓ for keyboard users. Do the same in the editor's `renderSteps` (call `renumberSteps` after a drop).
6. **Validate as you go:** change `checkState()` to return `[{ key, message }]`, with keys `name`, `giver`, `finisher`, `step<i>.target`,
   `step<i>.place`, `step<i>.count`, `step<i>.picked`, `item<i>`. Keep `state.tried` false until the first Create. After that, every
   render runs `checkState()` and adds class `qe-invalid` to the field wrapper whose key has an error: give the wrappers
   `data-key="..."` and loop over them. The bottom list stays.
   ```css
   #section-quests .qe-invalid input, #section-quests .qe-invalid select { border-color: var(--danger, #e66); }
   ```
7. **Save & test:** next to `Create draft`, a select of online characters (filled once from `QuestsAssist.players()`; "(no one online)"
   disables it) and a button `Create, save & start`. It runs the same steps as Create, then `await QuestsPage.openDraft(graph)` →
   `var r = await QuestsPage.saveNow();` → if `r && r.index`, `await QuestsPage.startOn(characterIndex)`. A checkbox "and teleport them
   to the giver" (on when the character is a GM) then POSTs the GM teleport (put this in the assist: `teleport(character, map, x, y)`,
   sharing code with `teleportPrompt`) to the giver's `hints('npc', giver)` cell. If the save fails, the editor already shows Validation;
   stop there.

---

### E9. Editor (`admin-quests.js`) gets the same helpers

- **ReachArea objectives** (`renderObjective`): replace the inline X / Y / Radius with `QuestsAssist.placeLine({ map: o.targetId, x: o.x, y: o.y, radius: o.radius }, function (p) { o.targetId = p.map; o.x = p.x; o.y = p.y; o.radius = p.radius; markDirty(); }, { radius: true, defaultRadius: 5 })`.
- **"More options" marker** of other types: `placeLine({ map: o.map, x: o.x, y: o.y, radius: o.radius }, ..., { radius: true, defaultRadius: 10 })`.
  Keep the raw number fields behind its `Numbers` toggle (they're part of `placeLine`).
- **Quest tab:** a `Suggest` button next to Level hint, and one next to Exp / Gold in the quick setup strip. Both use
  `QuestsAssist.suggest(...)` built from the graph: Kill objectives with Monster / MonsterGroup targets, Collect / Deliver items,
  ReachLevel counts.
- **Write the texts for me** on the Quest tab's Texts heading. It uses the same sentence builder as E8.2: move `summarySentences(steps)`
  into the assist so both share it. The editor version reads the graph's objectives in step order.
- **Head buttons:** `Use as template` (E7.2) and `Follow-up quest` (E7.4).

---

### E10. Tests and docs

`QuestEditorFrontendTests.cs`:
```csharp
[Fact]
public void Assist_is_loaded_between_the_page_and_the_wizard_and_uses_only_known_routes()
{
    var index = File.ReadAllText(Path.Combine(AdminDir(), "index.html"));
    int At(string s) => index.IndexOf(s, StringComparison.Ordinal);
    Assert.True(At("admin-quests.js\"") < At("admin-quests-assist.js\"") && At("admin-quests-assist.js\"") < At("admin-quests-wizard.js\"") && At("admin-quests-wizard.js\"") < At("app.js"));

    var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-quests-assist.js"));
    var urls = Regex.Matches(js, @"'(/api/[^']*)'").Select(m => m.Groups[1].Value).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToArray();
    Assert.Equal(new[] { "/api/admin/gm/action", "/api/admin/mapeditor/maps/", "/api/admin/players-online", "/api/admin/quests" }, urls);
    Assert.Empty(Regex.Matches(js, @"(?<![\w.$])fetch\s*\("));
    Assert.DoesNotContain("innerHTML", js);
    Assert.DoesNotContain("insertAdjacentHTML", js);
    Assert.DoesNotContain("localStorage", js);
}
```
Build the minimap URL as `'/api/admin/mapeditor/maps/' + map + '/minimap'` so the literal matches. The existing wizard test (no
`/api/`) must still pass. Keep every call in the assist.

`Server/WebAdmin/README.md`: add one or two sentences to the Quests bullet covering the map place picker (area square), monsters in an
area (creates or reuses an `area.*` group, optional "this map only" condition), hints under pickers
(`GET /api/admin/quests/hints`, `GET /api/admin/quests/item-icon/{index}`), suggestions (`QuestsAssist.TUNING`), templates, region,
follow-up quests, and Save & test.

**Phase E done when** `dotnet build C:\Mir2\Server\Server.Library.csproj`, the WebAdmin tests, and `node --check` on
`admin-quests.js`, `admin-quests-assist.js`, `admin-quests-wizard.js`, `admin-mapeditor-picker.js` and `admin-mapeditor-view.js` all pass.

**Manual smoke for the user:**
1. Wizard → Hunt → Pick on map. The area square shows, ± resizes it, and the footer counts the spawns.
2. Tick two monsters → Create → a group `area.…` appears under Groups, and the draft has a Kill objective on it plus a MapIs condition.
3. The picker hints show spawn maps for a monster and "Dropped by" for an item. An NPC shows its map and cell, and Map opens the map
   editor there.
4. Suggest fills exp / gold / level.
5. Follow-up quest from a saved quest → the draft's accept condition is "QuestStatus #N = Completed".
6. Create, save & start on a GM → the quest is in the client's log, and the GM is next to the giver.
7. The map editor's own "Pick on map…" (row drawer) still works with no area control.

---

## Not in this plan (ideas for later)

- A step graph view (Then / branch arrows) and storyline neighbours.
- Named shared conditions, and a condition library.
- Item icons in every Game Data lookup (E1 adds the endpoint for the Quests page only; `admin-lookups.js` `ICON_URL` could point at
  it later).
- Showing the runtime warnings (A3) in the list counts. That would mean moving them into `QuestDefinitions.Validate`, which is runtime
  code.

## Open questions for the user (defaults the plan uses)

1. Deliver to the giver: the wizard refuses it. Allow it?
2. Should the wizard default to Category **Side** and Repeat **Once**?
3. Is the Test tab OK being offered to every admin (it uses existing audited routes)?
4. Phase E: "Only kills on this map count" defaults to **on** for area hunts. Keep it?
5. Phase E: the reward suggestion numbers (`QuestsAssist.TUNING`) are placeholders. Do you have target exp / gold per level to use?
