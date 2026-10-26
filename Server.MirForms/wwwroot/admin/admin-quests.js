/*
 * Quests (#section-quests): the quest V2 editor (UserData-and-Quests-Plan.md, Part B, "Authoring").
 *
 *   GET  /api/admin/quests                      quest list (+ validation counts)
 *   GET  /api/admin/quests/meta                 enum names, target groups, shared conditions, storylines, pools
 *   GET  /api/admin/quests/{index}              one quest graph (with rev)
 *   POST /api/admin/quests/save                 {quest, dryRun?}: 422 + data.problems when validation fails (nothing saved)
 *   POST /api/admin/quests/{index}/delete       {rev}
 *   GET  /api/admin/quests/where-used           ?kind=&id=
 *   GET  /api/admin/quests/{index}/export       crystal-quest/1 file
 *   POST /api/admin/quests/import               {file} preview (diff + token), then {file, apply:true, token}
 *   POST /api/admin/quests/groups/save          {group}; POST /api/admin/quests/groups/{index}/delete
 *   GET  /api/admin/players-online              the online characters the Test tab offers
 *   POST /api/admin/characters/{index}/quests/edit    {questIndex, action, step?} (Test tab; GM SETQUEST)
 *   POST /api/admin/characters/{index}/quests/reset   {questIndex} (Test tab)
 *
 * Pickers use DbLookups.fetch (GET /api/admin/lookups/{name}). New rows carry temporary ids <= -1000 (the server replaces them with
 * counter ids). Every node is built with the DOM API and textContent: no HTML strings are ever parsed. Mutations go through
 * OpsUI.post / portalApi (CSRF header).
 */
(function (global) {
  'use strict';

  var DIRTY_KEY = 'quests-editor';
  var API = '/api/admin/quests';
  var LOOKUPS = { monsters: 'monsters', items: 'items', npcs: 'npcs', maps: 'maps', quests: 'quests', magics: 'magics' };

  var root = null;
  var meta = null;           // GET meta
  var list = [];             // GET list
  var lookups = {};          // name -> DbLookups result
  var quest = null;          // the graph being edited
  var loadedJson = '';       // JSON of the graph as loaded (dirty check)
  var problems = [];         // last save / check result
  var tab = 'quest';
  var tempId = -1000;
  var filters = { q: '', category: '', group: '', storyline: '', errors: false, sort: 'index' };
  var pendingFocus = null;   // a data-ref to scroll to and flash after the next render
  var showAdvanced = false;  // the Quest tab's advanced grid, for this page session
  var textsOnlyEmpty = false;
  var testState = { players: null, character: 0, step: 0 };

  // ---- small DOM helpers ------------------------------------------------------------------------------------------

  function h(tag, props) {
    var e = document.createElement(tag);
    if (props) {
      Object.keys(props).forEach(function (k) {
        var v = props[k];
        if (v == null || v === false) return;
        if (k === 'text') e.textContent = String(v);
        else if (k === 'cls') e.className = v;
        else if (k.indexOf('on') === 0 && typeof v === 'function') e.addEventListener(k.substring(2), v);
        else if (k === 'value') e.value = v;
        else if (k === 'checked') e.checked = !!v;
        else e.setAttribute(k, v === true ? '' : String(v));
      });
    }
    for (var i = 2; i < arguments.length; i++) append(e, arguments[i]);
    return e;
  }

  function append(parent, child) {
    if (child == null || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { append(parent, c); }); return; }
    parent.appendChild(typeof child === 'string' || typeof child === 'number' ? document.createTextNode(String(child)) : child);
  }

  function clear(e) { while (e && e.firstChild) e.removeChild(e.firstChild); }
  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }
  function newTemp() { tempId -= 1; return tempId; }
  function enumList(name) { return (meta && meta.enums && meta.enums[name]) || []; }
  function enumLabel(name, v) {
    var hit = enumList(name).filter(function (o) { return o.value === Number(v); })[0];
    return hit ? hit.label : String(v);
  }

  /* The value of an enum member by its label, 0 when the meta list does not have it. */
  function enumValue(name, label) {
    var hit = enumList(name).filter(function (o) { return o.label === label; })[0];
    return hit ? hit.value : 0;
  }

  /* Dropdown options: a reserved member is hidden unless the row already uses it. */
  function enumChoices(name, current) {
    return enumList(name).filter(function (o) { return !o.reserved || o.value === Number(current); })
      .map(function (o) { return o.reserved ? { value: o.value, label: o.label + ' (no runtime yet)' } : o; });
  }

  function num(v, fallback) { var n = Number(v); return isNaN(n) ? (fallback || 0) : Math.trunc(n); }

  function field(label, control, hint) {
    return h('label', { cls: 'qe-field' }, h('span', { cls: 'qe-label', text: label }), control, hint ? h('span', { cls: 'qe-hint', text: hint }) : null);
  }

  function input(value, onChange, opts) {
    opts = opts || {};
    var e = h('input', { type: opts.type || 'text', value: value == null ? '' : value, placeholder: opts.placeholder, cls: opts.cls, min: opts.min, max: opts.max });
    e.addEventListener('input', function () { onChange(opts.type === 'number' ? num(e.value) : e.value); markDirty(); });
    return e;
  }

  function numberInput(value, onChange, opts) { return input(value, onChange, Object.assign({ type: 'number' }, opts || {})); }

  var DURATION_UNITS = { d: 86400, h: 3600, m: 60, s: 1 };

  /* "90", "90s", "5m", "2h", "1d", "1h 30m" → seconds; NaN when the text is not a duration. */
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

  function checkbox(value, onChange, label) {
    var e = h('input', { type: 'checkbox', checked: !!value });
    e.addEventListener('change', function () { onChange(e.checked); markDirty(); });
    return h('label', { cls: 'qe-check' }, e, ' ' + label);
  }

  /* options: [{value, label}] */
  function select(value, options, onChange, rerender) {
    var e = h('select');
    options.forEach(function (o) {
      var opt = h('option', { value: String(o.value), text: o.label });
      if (String(o.value) === String(value)) opt.selected = true;
      e.appendChild(opt);
    });
    e.addEventListener('change', function () {
      var v = e.value;
      var asNum = Number(v);
      onChange(v !== '' && !isNaN(asNum) ? asNum : v);
      markDirty();
      if (rerender) renderEditor();
    });
    return e;
  }

  function enumSelect(name, value, onChange, rerender) { return select(value, enumChoices(name, value), onChange, rerender); }

  function button(text, onClick, cls, title) {
    return h('button', { type: 'button', cls: 'mir-btn' + (cls ? ' ' + cls : ''), text: text, title: title, onclick: onClick });
  }

  // ---- lookups / pickers -------------------------------------------------------------------------------------------

  async function ensureLookups() {
    var names = Object.keys(LOOKUPS);
    for (var i = 0; i < names.length; i++) {
      if (lookups[names[i]]) continue;
      try { lookups[names[i]] = await DbLookups.fetch(LOOKUPS[names[i]]); } catch (e) { lookups[names[i]] = { options: [], byIndex: {} }; }
    }
    // One datalist per lookup, shared by every picker of that kind.
    var host = el('datalists');
    clear(host);
    names.forEach(function (n) {
      var dl = h('datalist', { id: 'qe-dl-' + n });
      lookups[n].detailByIndex = {};
      (lookups[n].options || []).forEach(function (o) {
        if (!o.index) return;
        lookups[n].detailByIndex[o.index] = o.detail || '';
        // A datalist option displays its label instead of its value when both are set, so the label repeats the name and the id.
        dl.appendChild(h('option', { value: o.label + ' #' + o.index, label: o.detail ? o.label + ' · ' + o.detail + ' #' + o.index : null }));
      });
      host.appendChild(dl);
    });
  }

  function lookupLabel(name, index) {
    var l = lookups[name];
    var o = l && l.byIndex ? l.byIndex[index] : null;
    return o ? o.label : '';
  }

  /* The extra column of a lookup row (an NPC's map, a monster's level), to tell same-named rows apart. */
  function lookupDetail(name, index) {
    var l = lookups[name];
    return (l && l.detailByIndex && l.detailByIndex[index]) || '';
  }

  /* A searchable picker: shows "Label #index"; accepts a picked entry, "#n" or a bare number. opts.list picks another datalist. */
  function picker(name, value, onChange, opts) {
    opts = opts || {};
    var listId = opts.list || ('qe-dl-' + name);
    var hintKind = name === 'monsters' ? 'monster' : name === 'items' ? 'item' : name === 'npcs' ? 'npc' : null;
    var shown = value ? (lookupLabel(name, value) || '?') + ' #' + value : '';
    var img = name === 'items' && global.QuestsAssist ? h('img', { cls: 'qe-icon', alt: '' }) : null;
    if (img) {
      img.style.display = value > 0 ? '' : 'none';
      if (value > 0) img.src = global.QuestsAssist.itemIconUrl(value);
      img.addEventListener('error', function () { img.style.display = 'none'; });
    }
    var e = h('input', { type: 'text', value: shown, list: listId, placeholder: 'Search ' + name + '…', cls: 'qe-picker' });
    var status = h('span', { cls: 'qe-picker-name', text: value ? lookupDetail(name, value) : '' });
    var hintHost = hintKind && global.QuestsAssist ? h('span') : null;
    if (hintHost) hintHost.appendChild(global.QuestsAssist.hintLine(hintKind, value));
    function sync() {
      var m = /#\s*(\d+)\s*$/.exec(e.value) || /^\s*(\d+)\s*$/.exec(e.value);
      var idx = m ? Number(m[1]) : 0;
      if (!e.value.trim()) idx = 0;
      var label = idx ? lookupLabel(name, idx) : '';
      status.textContent = idx && !label ? 'not found' : (idx ? lookupDetail(name, idx) : '');
      status.className = 'qe-picker-name' + (idx && !label ? ' qe-bad' : '');
      if (img) {
        if (idx > 0) { img.style.display = ''; img.src = global.QuestsAssist.itemIconUrl(idx); }
        else img.style.display = 'none';
      }
      if (hintHost) { clear(hintHost); hintHost.appendChild(global.QuestsAssist.hintLine(hintKind, idx)); }
      onChange(idx);
      markDirty();
    }
    e.addEventListener('change', sync);
    return h('span', { cls: 'qe-picker-wrap' }, img, e, status, hintHost);
  }

  /* The target control for an objective / endpoint / reward by kind. */
  function targetControl(kind, value, onChange) {
    switch (kind) {
      case 'Monster': return picker('monsters', value, onChange);
      case 'Item': return picker('items', value, onChange);
      case 'Npc': return picker('npcs', value, onChange);
      case 'Map': return picker('maps', value, onChange);
      case 'Spell': return picker('magics', value, onChange);
      case 'Quest': return picker('quests', value, onChange);
      case 'MonsterGroup':
      case 'ItemGroup': {
        var gk = kind === 'MonsterGroup' ? 0 : 1;
        var opts = [{ value: 0, label: '(choose a group)' }].concat((meta.groups || []).filter(function (g) { return g.kind === gk; })
          .map(function (g) { return { value: g.index, label: g.authorKey + ' #' + g.index + ' (' + g.members.length + ')' }; }));
        return select(value, opts, onChange);
      }
      case 'None': return h('span', { cls: 'mir-muted', text: 'no target' });
      default: return numberInput(value, onChange, { min: 0 });
    }
  }

  // ---- model helpers -----------------------------------------------------------------------------------------------

  function blankQuest() {
    var stepId = newTemp();
    return {
      index: 0, rev: null, name: '', revision: 0, shapeHash: '0',
      authorKey: '', nameKey: '', summaryKey: '', completeKey: '',
      category: 1, storylineIndex: 0, storylineOrder: 0, levelHint: 0, acceptCondition: 0, visibleCondition: 0, repeatPolicy: 0,
      cooldownSeconds: 0, maxCompletions: 0, shareMode: 1, creditMode: 1, timeLimitSeconds: 0, failPolicy: 0, abandonAllowed: true,
      poolIndex: 0, startItemConsumed: false, flags: 0, groupName: '',
      steps: [{ index: stepId, order: 1, titleKey: '', textKey: '', readyTextKey: '', completionMode: 0, nRequired: 0, nextStep: 0, timeLimitSeconds: 0, failCondition: 0, onEnterPage: '', onCompletePage: '' }],
      objectives: [], branches: [], endpoints: [], rewards: [], vars: [], conditions: [], texts: {}
    };
  }

  function normalise(q) {
    ['steps', 'objectives', 'branches', 'endpoints', 'rewards', 'vars', 'conditions'].forEach(function (k) { q[k] = q[k] || []; });
    q.texts = q.texts || {};
    return q;
  }

  function sortedSteps() { return quest.steps.slice().sort(function (a, b) { return a.order - b.order || a.index - b.index; }); }

  function stepLabel(id) {
    var steps = sortedSteps();
    for (var i = 0; i < steps.length; i++) if (steps[i].index === id) return 'Step ' + (i + 1) + (id > 0 ? ' (#' + id + ')' : ' (new)');
    return 'step ' + id;
  }

  function isDirty() { return quest != null && JSON.stringify(quest) !== loadedJson; }

  function markDirty() {
    if (global.DbSave && typeof DbSave.setDirty === 'function') DbSave.setDirty(DIRTY_KEY, isDirty());
    var s = el('dirty');
    if (s) s.textContent = isDirty() ? 'Unsaved changes' : '';
  }

  /* Switch tab and scroll the row a problem or the readiness checklist points at into view. */
  function jumpTo(tabId, ref) {
    tab = tabId;
    pendingFocus = ref || null;
    renderEditor();
  }

  function conditionSummary(c, all, depth) {
    var op = enumLabel('conditionOp', c.op);
    if (op !== 'Leaf') {
      var kids = all.filter(function (k) { return k.parent === c.index; }).sort(function (a, b) { return a.order - b.order; });
      if ((depth || 0) >= 3) return op + ' (' + kids.length + ' part' + (kids.length === 1 ? '' : 's') + ')';
      if (!kids.length) return op + ' (no parts)';
      var head = op === 'And' ? 'All of: ' : op === 'Or' ? 'Any of: ' : op === 'Not' ? 'Not: ' : op + ' of: ';
      var parts = kids.slice(0, 3).map(function (k) { return conditionSummary(k, all, (depth || 0) + 1); });
      return head + parts.join('; ') + (kids.length > 3 ? ' …' : '');
    }
    var sym = compareSymbol(c.cmp);
    switch (enumLabel('conditionLeaf', c.leaf)) {
      case 'ItemCount': return 'Has ' + sym + ' ' + c.arg2 + ' × ' + lookupOr('items', c.arg1);
      case 'Equipped': return 'Wears ' + lookupOr('items', c.arg1);
      case 'MapIs': return 'On map ' + lookupOr('maps', c.arg1);
      case 'QuestStatus': return 'Quest ' + lookupOr('quests', c.arg1) + ' is ' + enumLabel('runStatus', c.arg2);
      case 'QuestCompletedCount': return 'Completed ' + lookupOr('quests', c.arg1) + ' ' + sym + ' ' + c.arg2 + ' times';
      case 'Class': return 'Class ' + sym + ' ' + enumLabel('mirClass', c.arg1);
      case 'Gender': return 'Gender ' + sym + ' ' + enumLabel('mirGender', c.arg1);
      case 'QuestVar': return varKey(c.arg1) + ' ' + sym + ' ' + c.arg2;
      default: return enumLabel('conditionLeaf', c.leaf) + ' ' + sym + ' ' + c.arg1 + (c.arg2 ? ' / ' + c.arg2 : '');
    }
  }

  var COMPARE_SYMBOLS = { Eq: '=', Ne: '≠', Lt: '<', Le: '≤', Gt: '>', Ge: '≥' };

  function compareSymbol(cmp) {
    var name = enumLabel('compare', cmp);
    return COMPARE_SYMBOLS[name] || name;
  }

  function lookupOr(name, index) { return lookupLabel(name, index) || '#' + index; }

  function varKey(index) {
    var hit = (quest ? quest.vars : []).filter(function (v) { return v.index === index; })[0];
    return hit && hit.authorKey ? hit.authorKey : 'variable #' + index;
  }

  /* Options for a condition reference: none, this quest's root conditions, shared conditions. */
  function conditionOptions() {
    var opts = [{ value: 0, label: '(none)' }];
    quest.conditions.filter(function (c) { return !c.parent; }).forEach(function (c) {
      opts.push({ value: c.index, label: (c.index > 0 ? '#' + c.index : 'new') + ': ' + conditionSummary(c, quest.conditions) });
    });
    var shared = (meta.sharedConditions || []);
    shared.filter(function (c) { return !c.parent; }).forEach(function (c) {
      opts.push({ value: c.index, label: 'shared #' + c.index + ': ' + conditionSummary(c, shared) });
    });
    return opts;
  }

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

  function stepOptions(withNext) {
    var opts = withNext ? [{ value: 0, label: 'Next by order' }, { value: -1, label: 'Ready to turn in' }] : [{ value: 0, label: '(choose)' }];
    sortedSteps().forEach(function (s) { opts.push({ value: s.index, label: stepLabel(s.index) }); });
    return opts;
  }

  /* The plain Npc endpoint of a role (what the quick setup strip edits). */
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

  /* The plain "always given" reward of a kind: quest scope, no choice, no condition, flat, every time. */
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

  /* How many advanced settings differ from blankQuest()'s defaults. */
  function advancedCount(q) {
    var n = 0;
    if (q.storylineIndex) n++;
    if (q.storylineOrder) n++;
    if (q.acceptCondition) n++;
    if (q.visibleCondition) n++;
    if (q.maxCompletions) n++;
    if (q.shareMode !== 1) n++;
    if (q.creditMode !== 1) n++;
    if (q.timeLimitSeconds) n++;
    if (q.failPolicy) n++;
    if (q.poolIndex) n++;
    if (q.startItemConsumed) n++;
    if (q.flags) n++;
    return n;
  }

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

  /* The "ready to play" checklist: what a quest still needs before a player can see it through. */
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
    var noObj = sortedSteps().filter(function (s) {
      return !quest.objectives.some(function (o) { return o.step === s.index; }) && !quest.branches.some(function (b) { return b.step === s.index; });
    })[0];
    out.push({ ok: !noObj, label: noObj ? stepLabel(noObj.index) + ' has no objective' : 'Objectives', tab: 'steps', ref: noObj ? 'step:' + noObj.index : null });
    var noTarget = quest.objectives.filter(function (o) {
      var t = enumLabel('objectiveType', o.type);
      return t !== 'ReachLevel' && t !== 'Counter' && t !== 'Flag' && !(o.targetId > 0);
    })[0];
    out.push({ ok: !noTarget, label: noTarget ? 'An objective has no target' : 'Targets', tab: 'steps', ref: noTarget ? 'objective:' + noTarget.index : null });
    out.push({ ok: quest.rewards.length > 0, warn: true, label: quest.rewards.length ? 'Rewards' : 'No rewards', tab: 'rewards' });
    return out;
  }

  // ---- text fields (one box per text field, server language) -------------------------------------------------------

  /* A row id as a key part: real ids as they are, temporary (negative) ids as nN (review C1). */
  function idPart(id) { return id < 0 ? 'n' + (-id) : String(id); }

  /* Every text key the quest's fields use (quest row, steps, objectives, branches). */
  function usedKeys() {
    var keys = {};
    function add(k) { if (k) keys[k] = true; }
    add(quest.nameKey); add(quest.summaryKey); add(quest.completeKey);
    (quest.steps || []).forEach(function (s) { add(s.titleKey); add(s.textKey); add(s.readyTextKey); });
    (quest.objectives || []).forEach(function (o) { add(o.textKey); });
    (quest.branches || []).forEach(function (b) { add(b.choiceKey); add(b.textKey); });
    return keys;
  }

  /* A default key that no text and no key field of this quest uses yet: base, then base-2, base-3, ... (review C1). */
  function freeKey(base) {
    var used = usedKeys();
    var k = base;
    for (var n = 2; used[k] || quest.texts[k] != null; n++) k = base + '-' + n;
    return k;
  }

  /* key: current key; setKey(k) stores it on the row; suffix builds a default key from the author key and the row's own id.
     onCommit (optional) runs on the body's change event and re-renders when it returns true. */
  function textField(label, key, setKey, suffix, multiline, onCommit) {
    var keyInput = h('input', { type: 'text', value: key || '', cls: 'qe-key', placeholder: 'text key', title: 'Text key (texts table, server language)' });
    var body = h(multiline ? 'textarea' : 'input', { cls: multiline ? 'qe-body' : 'qe-body qe-body-line', placeholder: label + ' text', rows: multiline ? 3 : null, type: multiline ? null : 'text' });
    body.value = key && quest.texts[key] != null ? quest.texts[key] : '';
    keyInput.addEventListener('change', function () {
      var old = key;
      var k = keyInput.value.trim();
      keyInput.value = k;
      if (old && k && old !== k && quest.texts[k] == null && quest.texts[old] != null) quest.texts[k] = quest.texts[old];
      key = k;
      setKey(k);
      body.value = k && quest.texts[k] != null ? quest.texts[k] : body.value;
      markDirty();
    });
    body.addEventListener('input', function () {
      if (!key) {
        key = freeKey((quest.authorKey || 'quest') + '.' + suffix);
        keyInput.value = key;
        setKey(key);
      }
      quest.texts[key] = body.value;
      markDirty();
    });
    if (onCommit) body.addEventListener('change', function () { if (onCommit()) renderEditor(); });
    return h('div', { cls: 'qe-text' }, h('span', { cls: 'qe-label', text: label }), h('div', { cls: 'qe-text-row' }, keyInput, body));
  }

  function withRef(e, ref) { e.setAttribute('data-ref', ref); return e; }

  // ---- list --------------------------------------------------------------------------------------------------------

  async function loadMeta() {
    var j = await OpsUI.get(API + '/meta');
    if (j) meta = j;
  }

  async function loadList() {
    var j = await OpsUI.get(API);
    list = (j && j.quests) || [];
    renderFilters();
    renderList();
  }

  function renderFilters() {
    var cat = el('f-category');
    var grp = el('f-group');
    var sl = el('f-storyline');
    if (!cat || !meta) return;
    function fill(sel, opts, current) {
      clear(sel);
      opts.forEach(function (o) {
        var opt = h('option', { value: String(o.value), text: o.label });
        if (String(o.value) === String(current)) opt.selected = true;
        sel.appendChild(opt);
      });
    }
    fill(cat, [{ value: '', label: 'All categories' }].concat(enumList('category')), filters.category);
    var groups = [];
    list.forEach(function (q) { if (q.groupName && groups.indexOf(q.groupName) < 0) groups.push(q.groupName); });
    groups.sort();
    fill(grp, [{ value: '', label: 'All groups' }].concat(groups.map(function (g) { return { value: g, label: g }; })), filters.group);
    fill(sl, [{ value: '', label: 'All storylines' }, { value: '0', label: '(no storyline)' }].concat((meta.storylines || []).map(function (s) {
      return { value: String(s.index), label: s.name || s.authorKey };
    })), filters.storyline);
  }

  function filtered() {
    var q = filters.q.trim().toLowerCase();
    var rows = list.filter(function (x) {
      if (filters.category !== '' && String(x.category) !== String(filters.category)) return false;
      if (filters.group && x.groupName !== filters.group) return false;
      if (filters.storyline !== '' && String(x.storylineIndex) !== String(filters.storyline)) return false;
      if (filters.errors && !x.errors) return false;
      if (q && (String(x.name || '') + ' ' + (x.authorKey || '') + ' #' + x.index).toLowerCase().indexOf(q) < 0) return false;
      return true;
    });
    if (filters.sort === 'name') rows.sort(function (a, b) { return String(a.name || '').localeCompare(String(b.name || '')); });
    else if (filters.sort === 'problems') rows.sort(function (a, b) { return (b.errors - a.errors) || (b.warnings - a.warnings) || (a.index - b.index); });
    else rows.sort(function (a, b) { return a.index - b.index; });
    return rows;
  }

  function renderList() {
    var host = el('list');
    if (!host) return;
    clear(host);
    var rows = filtered();
    el('count').textContent = rows.length + ' of ' + list.length + ' quest' + (list.length === 1 ? '' : 's');
    rows.slice(0, 1000).forEach(function (x) {
      var badges = h('span', { cls: 'qe-badges' },
        x.errors ? h('span', { cls: 'ops-pill ops-pill-danger', text: x.errors + ' err' }) : null,
        x.warnings ? h('span', { cls: 'ops-pill ops-pill-warn', text: x.warnings + ' warn' }) : null,
        !x.hasDefinition ? h('span', { cls: 'ops-pill', title: 'No steps yet: the quest does nothing until it has a step.', text: 'no steps' }) : null);
      var row = h('button', { type: 'button', cls: 'qe-row' + (quest && quest.index === x.index ? ' active' : ''), onclick: function () { openQuest(x.index); } },
        h('span', { cls: 'qe-row-index', text: '#' + x.index }),
        h('span', { cls: 'qe-row-name', text: x.name || '(unnamed)' }),
        h('span', { cls: 'qe-row-key', text: (x.authorKey || '') + ' · ' + enumLabel('category', x.category) + (x.levelHint ? ' · Lv ' + x.levelHint : '') }),
        badges);
      host.appendChild(row);
    });
    if (rows.length > 1000) host.appendChild(h('p', { cls: 'mir-muted', text: 'Showing the first 1000; narrow the search.' }));
  }

  // ---- open / new / save -------------------------------------------------------------------------------------------

  async function confirmDiscard() {
    if (!isDirty()) return true;
    return await portalConfirm('Discard your unsaved quest changes?', { title: 'Unsaved changes', confirmLabel: 'Discard', danger: true });
  }

  async function openQuest(index) {
    if (!await confirmDiscard()) return;
    var j = await OpsUI.get(API + '/' + encodeURIComponent(index));
    if (!j) { portalToast('Could not load quest ' + index + '.', 'error'); return; }
    setQuest(normalise(j));
    problems = [];
    renderList();
  }

  function setQuest(q) {
    quest = q;
    loadedJson = q ? JSON.stringify(q) : '';
    markDirty();
    renderEditor();
  }

  async function newQuest() { return wizard(); }

  /* Open the wizard (or a blank draft when it is not loaded) with an optional prefill: { fromGraph } | { after, giver, category, … }. */
  async function wizard(prefill) {
    if (global.QuestsWizard && global.QuestsWizard.canOpen && !global.QuestsWizard.canOpen(prefill)) return;
    if (!await confirmDiscard()) return;
    quest = null;
    loadedJson = '';
    problems = [];
    markDirty();
    renderList();
    if (global.QuestsWizard) { global.QuestsWizard.open(el('editor'), prefill); return; }
    await openDraft(blankQuest());
  }

  /* Open a graph built elsewhere (the wizard) as an unsaved draft in the editor. */
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

  function errorProblems(j) {
    return (j && j.data && j.data.problems) || [];
  }

  async function save(dryRun, btn) {
    if (!quest) return null;
    var body = { quest: quest, dryRun: !!dryRun };
    var j = await OpsUI.withBusy(btn, function () { return OpsUI.post(API + '/save', body, { quiet: false }); }, dryRun ? 'Checking…' : 'Saving…');
    if (!j) return null;
    if (!j.__ok) {
      problems = errorProblems(j);
      tab = problems.length ? 'validation' : tab;
      renderEditor();
      return null;
    }
    var r = j.data || {};
    problems = r.problems || [];
    if (dryRun) { tab = 'validation'; renderEditor(); return r; }
    loadedJson = '';
    await loadMeta();
    await loadList();
    var fresh = await OpsUI.get(API + '/' + encodeURIComponent(r.index));
    if (fresh) { quest = normalise(fresh); loadedJson = JSON.stringify(quest); }
    markDirty();
    renderEditor();
    renderList();
    return r;
  }

  async function del(btn) {
    if (!quest || !quest.index) return;
    if (!await portalConfirm('Delete quest #' + quest.index + ' (' + (quest.authorKey || '') + ') with its conditions and the texts only it uses? Its id is retired.',
      { title: 'Delete quest', confirmLabel: 'Delete', danger: true })) return;
    var j = await OpsUI.withBusy(btn, function () { return OpsUI.post(API + '/' + quest.index + '/delete', { rev: quest.rev }); }, 'Deleting…');
    if (!j || !j.__ok) {
      var uses = j && j.data && j.data.uses;
      if (uses && uses.length) { showUses(uses, 'Still used by'); tab = 'whereused'; renderEditor(); }
      return;
    }
    quest = null;
    loadedJson = '';
    markDirty();
    await loadList();
    renderEditor();
  }

  async function exportQuest() {
    if (!quest || !quest.index) return;
    var r = await portalApi(API + '/' + quest.index + '/export', { method: 'GET' });
    if (!r.ok) { portalToast('Export failed (HTTP ' + r.status + ').', 'error'); return; }
    var text = await r.text();
    OpsUI.download((quest.authorKey || ('quest-' + quest.index)).replace(/[^A-Za-z0-9._-]/g, '_') + '.quest.json', text, 'application/json');
  }

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

  // ---- import ------------------------------------------------------------------------------------------------------

  var importText = null;
  var importPreview = null;

  function pickImportFile() {
    var f = h('input', { type: 'file', accept: '.json,application/json' });
    f.addEventListener('change', function () {
      var file = f.files && f.files[0];
      if (!file) return;
      var reader = new FileReader();
      reader.onload = function () { importText = String(reader.result || ''); previewImport(); };
      reader.readAsText(file);
    });
    f.click();
  }

  async function previewImport() {
    var j = await OpsUI.post(API + '/import', { file: importText }, { quiet: true });
    importPreview = j && j.__ok ? j.data : null;
    if (!importPreview && j) importPreview = { failed: true, problems: errorProblems(j), message: j.message };
    renderImport();
  }

  async function applyImport(btn) {
    if (!importPreview || !importPreview.token) return;
    if (!await confirmDiscard()) return; // review C5: the import opens the imported quest, which drops unsaved edits
    var j = await OpsUI.withBusy(btn, function () { return OpsUI.post(API + '/import', { file: importText, apply: true, token: importPreview.token }); }, 'Importing…');
    if (!j || !j.__ok) { importPreview = { failed: true, problems: errorProblems(j), message: j && j.message }; renderImport(); return; }
    var idx = j.data.index;
    importText = null;
    importPreview = null;
    renderImport();
    await loadMeta();
    await loadList();
    loadedJson = JSON.stringify(quest);
    await openQuest(idx);
  }

  function renderImport() {
    var host = el('import');
    clear(host);
    if (!importPreview) { host.classList.add('hidden'); return; }
    host.classList.remove('hidden');
    var p = importPreview;
    host.appendChild(h('h3', { text: p.failed ? 'Import refused' : 'Import preview (' + p.mode + (p.index ? ' quest #' + p.index : '') + ')' }));
    if (p.message && p.failed) host.appendChild(h('p', { cls: 'qe-bad', text: p.message }));
    if (p.diff && p.diff.length) host.appendChild(h('ul', { cls: 'qe-diff' }, p.diff.map(function (d) { return h('li', { text: d }); })));
    host.appendChild(problemList(p.problems || [], false));
    var errs = (p.problems || []).filter(function (x) { return !x.warning; }).length;
    host.appendChild(h('div', { cls: 'qe-actions' },
      !p.failed && !errs ? button('Apply import', function (e) { applyImport(e.currentTarget); }, 'primary') : null,
      button('Close', function () { importPreview = null; importText = null; renderImport(); })));
  }

  // ---- where used --------------------------------------------------------------------------------------------------

  var usesState = { kind: 'monster', id: 0, uses: null, title: '' };

  async function whereUsed(kind, id) {
    if (!id) { portalToast('Pick something first.', 'warn'); return; }
    var j = await OpsUI.get(API + '/where-used?kind=' + encodeURIComponent(kind) + '&id=' + encodeURIComponent(id));
    showUses((j && j.uses) || [], 'Used by');
    usesState.kind = kind;
    usesState.id = id;
    tab = 'whereused';
    renderEditor();
  }

  function showUses(uses, title) { usesState.uses = uses; usesState.title = title; }

  function renderWhereUsed(host) {
    var kinds = ['monster', 'item', 'npc', 'map', 'group', 'condition', 'quest', 'spell'];
    var idHost = h('span');
    function idControl() {
      clear(idHost);
      var k = usesState.kind;
      var lookup = { monster: 'Monster', item: 'Item', npc: 'Npc', map: 'Map', quest: 'Quest', spell: 'Spell' }[k];
      if (lookup) idHost.appendChild(targetControl(lookup, usesState.id, function (v) { usesState.id = v; }));
      else idHost.appendChild(numberInput(usesState.id, function (v) { usesState.id = v; }, { min: 0 }));
    }
    var kindSel = h('select');
    kinds.forEach(function (k) { var o = h('option', { value: k, text: k }); if (k === usesState.kind) o.selected = true; kindSel.appendChild(o); });
    kindSel.addEventListener('change', function () { usesState.kind = kindSel.value; usesState.id = 0; idControl(); });
    idControl();
    host.appendChild(h('div', { cls: 'qe-inline' }, field('Kind', kindSel), field('Which', idHost),
      button('Find', function () { whereUsed(usesState.kind, usesState.id); }, 'primary')));
    if (quest && quest.index) host.appendChild(h('p', null, button('Where is this quest used?', function () { whereUsed('quest', quest.index); })));
    if (usesState.uses) {
      host.appendChild(h('h4', { text: usesState.title + ' (' + usesState.uses.length + ')' }));
      if (!usesState.uses.length) host.appendChild(h('p', { cls: 'mir-muted', text: 'Nothing references it.' }));
      host.appendChild(h('ul', { cls: 'qe-uses' }, usesState.uses.map(function (u) {
        return h('li', null,
          u.quest ? h('button', { type: 'button', cls: 'qe-link', text: '#' + u.quest + ' ' + (u.questLabel || ''), onclick: function () { openQuest(u.quest); } }) : null,
          h('span', { text: (u.quest ? ': ' : '') + u.where }));
      })));
    }
  }

  // ---- validation list ---------------------------------------------------------------------------------------------

  var REF_TABS = { quest: 'quest', step: 'steps', objective: 'steps', condition: 'conditions', variable: 'vars', endpoint: 'endpoints', reward: 'rewards', branch: 'branches' };

  function problemList(items, linkable) {
    if (!items || !items.length) return h('p', { cls: 'mir-muted', text: 'No problems.' });
    return h('ul', { cls: 'qe-problems' }, items.map(function (p) {
      var tabId = REF_TABS[p.refKind || 'quest'] || 'quest';
      var ref = p.refId && (p.refKind === 'step' || p.refKind === 'objective' || p.refKind === 'condition' || p.refKind === 'variable') ? p.refKind + ':' + p.refId : null;
      return h('li', { cls: p.warning ? 'qe-warn' : 'qe-bad' }, h('strong', { text: p.warning ? 'warning' : 'error' }), ' ' + p.message + ' ',
        linkable && quest ? h('button', { type: 'button', cls: 'qe-link', text: 'Show', onclick: function () { jumpTo(tabId, ref); } }) : null);
    }));
  }

  // ---- editor tabs -------------------------------------------------------------------------------------------------

  var TABS = [
    { id: 'quest', label: 'Quest' }, { id: 'steps', label: 'Steps & objectives' }, { id: 'branches', label: 'Branches' },
    { id: 'endpoints', label: 'Givers' }, { id: 'rewards', label: 'Rewards' }, { id: 'conditions', label: 'Conditions' },
    { id: 'vars', label: 'Variables' }, { id: 'texts', label: 'Texts' }, { id: 'validation', label: 'Validation' }, { id: 'test', label: 'Test' },
    { id: 'whereused', label: 'Where used' }, { id: 'groups', label: 'Groups' }
  ];

  function renderEditor() {
    var host = el('editor');
    if (!host) return;
    var scroll = host.scrollTop;
    clear(host);
    if (!quest) {
      host.appendChild(h('div', { cls: 'qe-empty' },
        h('p', { text: 'Pick a quest on the left, or start a new one.' }),
        h('div', { cls: 'qe-tabs' },
          button('New quest (wizard)', newQuest, 'primary'),
          button('Where used', function () { tab = 'whereused'; renderEmptyTab(); }),
          button('Groups', function () { tab = 'groups'; renderEmptyTab(); }))));
      var pane = h('div', { cls: 'qe-pane', 'data-role': 'empty-pane' });
      host.appendChild(pane);
      if (tab === 'whereused') renderWhereUsed(pane);
      if (tab === 'groups') renderGroups(pane);
      return;
    }

    var errs = problems.filter(function (p) { return !p.warning; }).length;
    var checkBtn = button('Check', function (e) { save(true, e.currentTarget); }, null, 'Validate without saving (Ctrl+Enter)');
    checkBtn.setAttribute('data-role', 'check-btn');
    var saveBtn = button('Save', function (e) { save(false, e.currentTarget); }, 'primary', 'Save (Ctrl+S)');
    saveBtn.setAttribute('data-role', 'save-btn');
    host.appendChild(h('div', { cls: 'qe-head' },
      h('h3', { text: quest.index ? 'Quest #' + quest.index + ' ' + (quest.authorKey || '') : 'New quest' }),
      quest.index ? h('span', { cls: 'mir-muted', text: 'revision ' + quest.revision }) : null,
      h('span', { cls: 'qe-dirty', 'data-role': 'dirty' }),
      h('span', { cls: 'qe-spacer' }),
      checkBtn,
      saveBtn,
      quest.index ? button('Duplicate', duplicateQuest) : null,
      quest.index && !isDirty() ? button('Use as template', function () { wizard({ fromGraph: quest }); }, null, 'Start a new quest from this one') : null,
      quest.index ? button('Follow-up quest', followUpQuest, null, 'Start a quest that unlocks after this one') : null,
      quest.index ? button('Export', exportQuest) : null,
      quest.index ? button('Delete', function (e) { del(e.currentTarget); }, 'qe-danger') : null,
      button('Revert', async function () {
        if (!await confirmDiscard()) return;
        if (quest.index) { loadedJson = JSON.stringify(quest); openQuest(quest.index); } else { quest = null; loadedJson = ''; markDirty(); renderEditor(); }
      })));

    host.appendChild(h('div', { cls: 'qe-ready' }, readiness().map(function (r) {
      return h('button', { type: 'button', cls: 'qe-ready-item ' + (r.ok ? 'ok' : r.warn ? 'warn' : 'bad'),
        text: (r.ok ? '✓ ' : '• ') + r.label, onclick: function () { jumpTo(r.tab, r.ref); } });
    })));

    var tabs = h('div', { cls: 'qe-tabs' });
    TABS.forEach(function (t) {
      if (t.id === 'test' && !quest.index) return;
      var label = t.id === 'validation' && problems.length ? t.label + ' (' + errs + '/' + (problems.length - errs) + ')' : t.label;
      tabs.appendChild(button(label, function () { tab = t.id; renderEditor(); }, tab === t.id ? 'active' : null));
    });
    host.appendChild(tabs);

    var pane2 = h('div', { cls: 'qe-pane' });
    host.appendChild(pane2);
    switch (tab) {
      case 'quest': renderQuestForm(pane2); break;
      case 'steps': renderSteps(pane2); break;
      case 'branches': renderBranches(pane2); break;
      case 'endpoints': renderEndpoints(pane2); break;
      case 'rewards': renderRewards(pane2); break;
      case 'conditions': renderConditions(pane2); break;
      case 'vars': renderVars(pane2); break;
      case 'texts': renderTexts(pane2); break;
      case 'test': renderTest(pane2); break;
      case 'validation': pane2.appendChild(problemList(problems, true)); if (!problems.length) pane2.appendChild(h('p', { cls: 'mir-muted', text: 'Run Check or Save to validate.' })); break;
      case 'whereused': renderWhereUsed(pane2); break;
      case 'groups': renderGroups(pane2); break;
    }
    markDirty();
    host.scrollTop = scroll;
    if (pendingFocus) {
      var target = host.querySelector('[data-ref="' + pendingFocus + '"]');
      pendingFocus = null;
      if (target) {
        target.scrollIntoView({ block: 'center' });
        target.classList.add('qe-flash');
        setTimeout(function () { target.classList.remove('qe-flash'); }, 1500);
      }
    }
  }

  function renderEmptyTab() {
    var pane = el('empty-pane');
    clear(pane);
    if (tab === 'whereused') renderWhereUsed(pane);
    if (tab === 'groups') renderGroups(pane);
  }

  // -- quest form --

  function groupMembers(id) {
    var g = (meta.groups || []).filter(function (x) { return x.index === id; })[0];
    return g ? (g.members || []) : [];
  }

  function groupLabel(id) {
    var g = (meta.groups || []).filter(function (x) { return x.index === id; })[0];
    return g ? g.authorKey : 'monsters';
  }

  /* The graph as the assist's reward suggestion sees it (Kill monsters, items, level targets). */
  function graphSuggestInput() {
    var steps = sortedSteps().map(function (s) {
      var objs = quest.objectives.filter(function (o) { return o.step === s.index; });
      var type = objs.length ? enumLabel('objectiveType', objs[0].type) : 'Kill';
      var step = { type: type, monsters: [], count: 0, item: 0 };
      objs.forEach(function (o) {
        var kind = enumLabel('objectiveType', o.type);
        if (kind === 'Kill') {
          var tk = enumLabel('targetKind', o.targetKind);
          if (tk === 'Monster') step.monsters.push(o.targetId);
          else if (tk === 'MonsterGroup') step.monsters = step.monsters.concat(groupMembers(o.targetId));
        } else if (kind === 'Collect' || kind === 'Deliver') { step.item = o.targetId; step.count = o.count; }
        else if (kind === 'ReachLevel') step.count = o.count;
      });
      if (type === 'Kill') step.count = objs.reduce(function (a, o) { return Math.max(a, o.count); }, 0) || 1;
      return step;
    });
    return { steps: steps, levelHint: quest.levelHint };
  }

  function suggestLevel() {
    if (!global.QuestsAssist) return;
    var q = quest;
    global.QuestsAssist.suggest(graphSuggestInput()).then(function (r) {
      if (!r || !r.level || quest !== q) return;
      quest.levelHint = r.level;
      markDirty();
      renderEditor();
    });
  }

  function suggestRewards() {
    if (!global.QuestsAssist) return;
    var q = quest;
    global.QuestsAssist.suggest(graphSuggestInput()).then(function (r) {
      if (!r || quest !== q) return;
      if (r.exp) setSimpleReward('Exp', r.exp);
      if (r.gold) setSimpleReward('Gold', r.gold);
      markDirty();
      renderEditor();
    });
  }

  function graphTextSteps() {
    return sortedSteps().map(function (s) {
      var o = quest.objectives.filter(function (x) { return x.step === s.index; })[0];
      var type = o ? enumLabel('objectiveType', o.type) : '';
      var target = '';
      if (o && type === 'Kill') target = enumLabel('targetKind', o.targetKind) === 'MonsterGroup' ? groupLabel(o.targetId) : (lookupLabel('monsters', o.targetId) || 'monsters');
      return {
        type: type, count: o ? o.count : 1, target: target,
        item: o && (type === 'Collect' || type === 'Deliver') ? (lookupLabel('items', o.targetId) || 'the item') : '',
        npc: type === 'Deliver'
          ? (lookupLabel('npcs', (endpointOf('Finish') || {}).targetId || (endpointOf('Start') || {}).targetId || 0) || 'the NPC')
          : (lookupLabel('npcs', o ? o.targetId : 0) || 'the NPC'),
        mapTitle: o && type === 'ReachArea' ? (lookupLabel('maps', o.targetId) || '') : (o && o.map ? (lookupLabel('maps', o.map) || '') : ''),
        place: o && !!o.map ? true : false,
      };
    });
  }

  async function writeTexts() {
    if (!global.QuestsAssist) return;
    var turnInNpc = (endpointOf('Finish') || endpointOf('Start') || {}).targetId || 0;
    var giver = lookupLabel('npcs', (endpointOf('Start') || {}).targetId || 0) || '';
    var finisher = lookupLabel('npcs', (endpointOf('Finish') || {}).targetId || 0) || giver;
    var ordered = sortedSteps();
    var last = ordered[ordered.length - 1];
    var lastTalk = false;
    if (last) {
      var lo = quest.objectives.filter(function (o) { return o.step === last.index; })[0];
      lastTalk = !!lo && enumLabel('objectiveType', lo.type) === 'Talk' && lo.targetId === turnInNpc;
    }
    var summary = global.QuestsAssist.summarySentences(graphTextSteps(), { giverName: giver, finisherName: finisher, lastTalkToTurnIn: lastTalk }).join(' ');
    var hasMoney = quest.rewards.some(function (r) { return r.kind === enumValue('rewardKind', 'Exp') || r.kind === enumValue('rewardKind', 'Gold'); });
    var turnIn = hasMoney ? 'Well done. Take this for your trouble.' : 'Well done. Here is your reward.';

    var hasSummary = quest.summaryKey && String(quest.texts[quest.summaryKey] || '').trim();
    var hasTurnIn = quest.completeKey && String(quest.texts[quest.completeKey] || '').trim();
    if ((hasSummary || hasTurnIn) && !await portalConfirm('Overwrite the summary and turn-in text?', { title: 'Write the texts', confirmLabel: 'Overwrite', danger: true })) return;

    if (!quest.summaryKey) quest.summaryKey = freeKey((quest.authorKey || 'quest') + '.summary');
    if (!quest.completeKey) quest.completeKey = freeKey((quest.authorKey || 'quest') + '.complete');
    quest.texts[quest.summaryKey] = summary;
    quest.texts[quest.completeKey] = turnIn;
    markDirty();
    renderEditor();
  }

  function followUpQuest() {
    if (!quest || !quest.index) return;
    var start = (quest.endpoints || []).filter(function (e) { return e.role === enumValue('endpointRole', 'Start'); })[0];
    wizard({
      after: { index: quest.index, name: quest.name || ('#' + quest.index) },
      giver: start ? start.targetId : 0,
      category: quest.category,
      levelHint: quest.levelHint,
      storylineIndex: quest.storylineIndex,
      storylineOrder: quest.storylineOrder,
    });
  }

  function renderQuestForm(host) {
    var q = quest;
    host.appendChild(renderEssentials());

    var prevKey = q.authorKey || 'quest';
    var akInput = input(q.authorKey, function (v) { q.authorKey = v.trim(); }, { placeholder: 'e.g. bichon.wolves.1' });
    akInput.addEventListener('change', function () {
      if (q.index) return;   // a live quest keeps its text keys: a branch ChoiceKey is what C.QuestChoose sends
      rekeyTexts(prevKey, q.authorKey);
      renderEditor();
    });

    var cooldown = enumLabel('repeatPolicy', q.repeatPolicy) === 'Cooldown' || q.cooldownSeconds > 0;
    host.appendChild(h('div', { cls: 'qe-grid' },
      withRef(field('Author key', h('span', { cls: 'qe-inline' }, akInput,
        button('Suggest', function () {
          var old = q.authorKey || 'quest';
          q.authorKey = suggestAuthorKey(q);
          if (!q.index) rekeyTexts(old, q.authorKey);
          markDirty();
          renderEditor();
        })), 'Unique; used by export files and scripts'), 'quest:authorKey'),
      field('Category', enumSelect('category', q.category, function (v) { q.category = v; }, true)),
      field('Group (log)', input(q.groupName, function (v) { q.groupName = v; })),
      field('Level hint', h('span', { cls: 'qe-inline' }, numberInput(q.levelHint, function (v) { q.levelHint = v; }, { min: 0 }),
        button('Suggest', suggestLevel, null, 'Suggest a level from the objectives'))),
      field('Repeat', enumSelect('repeatPolicy', q.repeatPolicy, function (v) { q.repeatPolicy = v; }, true)),
      cooldown ? field('Cooldown', durationInput(q.cooldownSeconds, function (v) { q.cooldownSeconds = v; })) : null,
      h('div', { cls: 'qe-field' }, checkbox(q.abandonAllowed, function (v) { q.abandonAllowed = v; }, 'Abandon allowed'))));

    host.appendChild(h('div', { cls: 'qe-inline' }, h('h4', { text: 'Texts' }), button('Write the texts for me', writeTexts, null, 'Fill the summary and turn-in text from the steps')));
    host.appendChild(withRef(textField('Name', q.nameKey, function (k) { q.nameKey = k; }, 'name', false, function () {
      if (q.index || q.authorKey) return false;
      q.authorKey = suggestAuthorKey(q);
      rekeyTexts('quest', q.authorKey);
      return true;
    }), 'quest:name'));
    host.appendChild(textField('Summary', q.summaryKey, function (k) { q.summaryKey = k; }, 'summary', true));
    host.appendChild(textField('Turn-in text', q.completeKey, function (k) { q.completeKey = k; }, 'complete', true));

    var n = advancedCount(q);
    host.appendChild(button(showAdvanced ? 'Hide advanced settings' : 'Show advanced settings' + (n ? ' (' + n + ' set)' : ''),
      function () { showAdvanced = !showAdvanced; renderEditor(); }));
    if (!showAdvanced) return;

    var flags = h('div', { cls: 'qe-flags' }, enumList('flags').filter(function (f) { return !f.reserved || (q.flags & f.value) !== 0; })
      .map(function (f) {
        return checkbox((q.flags & f.value) !== 0, function (on) { q.flags = on ? (q.flags | f.value) : (q.flags & ~f.value); }, f.label + (f.reserved ? ' (no runtime yet)' : ''));
      }));
    host.appendChild(h('div', { cls: 'qe-grid' },
      field('Storyline', select(q.storylineIndex, [{ value: 0, label: '(none)' }].concat((meta.storylines || []).map(function (s) { return { value: s.index, label: s.name || s.authorKey }; })), function (v) { q.storylineIndex = v; })),
      field('Storyline order', numberInput(q.storylineOrder, function (v) { q.storylineOrder = v; }, { min: 0 })),
      field('Accept condition', conditionSelect(q.acceptCondition, function (v) { q.acceptCondition = v; })),
      field('Visible condition', conditionSelect(q.visibleCondition, function (v) { q.visibleCondition = v; })),
      field('Max completions', numberInput(q.maxCompletions, function (v) { q.maxCompletions = v; }, { min: 0 }), '0 = unlimited'),
      field('Share', enumSelect('shareMode', q.shareMode, function (v) { q.shareMode = v; })),
      field('Credit', enumSelect('creditMode', q.creditMode, function (v) { q.creditMode = v; })),
      field('Time limit', durationInput(q.timeLimitSeconds, function (v) { q.timeLimitSeconds = v; })),
      field('Fail policy', enumSelect('failPolicy', q.failPolicy, function (v) { q.failPolicy = v; })),
      field('Pool', select(q.poolIndex, [{ value: 0, label: '(none)' }].concat((meta.pools || []).map(function (p) { return { value: p.index, label: p.authorKey + ' #' + p.index }; })), function (v) { q.poolIndex = v; })),
      h('div', { cls: 'qe-field' }, checkbox(q.startItemConsumed, function (v) { q.startItemConsumed = v; }, 'Start item consumed'))));
    host.appendChild(field('Flags', flags));
  }

  /* The four settings almost every quest needs, so the common case never opens the Givers / Rewards tabs. */
  function renderEssentials() {
    var box = h('div', { cls: 'qe-essentials', 'data-ref': 'quest:giver' });
    var start = endpointOf('Start'), finish = endpointOf('Finish');
    var starts = quest.endpoints.filter(function (e) { return e.role === enumValue('endpointRole', 'Start'); }).length;
    var finishes = quest.endpoints.filter(function (e) { return e.role === enumValue('endpointRole', 'Finish'); }).length;
    var exp = simpleReward('Exp'), gold = simpleReward('Gold');
    box.appendChild(h('div', { cls: 'qe-grid' },
      field('Giver NPC', picker('npcs', start ? start.targetId : 0, function (v) { setEndpointNpc('Start', v); })),
      field('Turn in to', picker('npcs', finish ? finish.targetId : 0, function (v) { setEndpointNpc('Finish', v); }), 'Empty = back to the giver'),
      field('Exp', numberInput(exp ? exp.amount : 0, function (v) { setSimpleReward('Exp', v); }, { min: 0 })),
      field('Gold', numberInput(gold ? gold.amount : 0, function (v) { setSimpleReward('Gold', v); }, { min: 0 }))));
    box.appendChild(h('div', { cls: 'qe-actions' },
      h('span', { cls: 'mir-muted', text: quest.rewards.length + ' reward(s) in all' }),
      button('Suggest exp / gold', suggestRewards, null, 'Suggest from the objectives'),
      button('Edit rewards', function () { jumpTo('rewards'); }),
      starts > 1 || finishes > 1 ? h('span', { cls: 'mir-muted', text: 'More givers on the Givers tab (' + quest.endpoints.length + ' endpoints)' }) : null,
      starts > 1 || finishes > 1 ? button('Edit givers', function () { jumpTo('endpoints'); }) : null));
    return box;
  }

  // -- steps & objectives --

  function move(arr, item, delta) {
    var i = arr.indexOf(item);
    var j = i + delta;
    if (i < 0 || j < 0 || j >= arr.length) return;
    arr.splice(i, 1);
    arr.splice(j, 0, item);
  }

  function renumberSteps(ordered) { ordered.forEach(function (s, i) { s.order = i + 1; }); }

  /* The target kinds an objective type accepts (the first is the default). */
  var TYPE_TARGETS = {
    Kill: ['Monster', 'MonsterGroup'], Collect: ['Item', 'ItemGroup'], Deliver: ['Item', 'ItemGroup'], UseItem: ['Item', 'ItemGroup'],
    Craft: ['Item', 'ItemGroup'], Gather: ['Item', 'ItemGroup'], Talk: ['Npc'], ReachArea: ['Map'], ReachLevel: ['None'], Counter: ['None'],
    UseSkill: ['Spell'], Flag: ['Flag']
  };

  function renderSteps(host) {
    var steps = sortedSteps();
    steps.forEach(function (s, si) {
      var card = h('div', { cls: 'qe-step', 'data-ref': 'step:' + s.index });
      var handle = h('span', { cls: 'qe-drag', text: '⠿', draggable: 'true', title: 'Drag to reorder' });
      handle.addEventListener('dragstart', function (e) { e.dataTransfer.setData('text/x-quest-step', String(si)); e.dataTransfer.effectAllowed = 'move'; });
      card.addEventListener('dragover', function (e) { e.preventDefault(); });
      card.addEventListener('drop', function (e) {
        var raw = e.dataTransfer.getData('text/x-quest-step');
        if (!raw) return;
        e.preventDefault();
        var from = Number(raw);
        if (isNaN(from) || from === si) return;
        var list = sortedSteps();
        var moved = list.splice(from, 1)[0];
        list.splice(si, 0, moved);
        renumberSteps(list);
        markDirty();
        renderEditor();
      });
      card.appendChild(h('div', { cls: 'qe-step-head' },
        handle,
        h('strong', { text: stepLabel(s.index) }),
        h('span', { cls: 'qe-spacer' }),
        button('↑', function () { move(steps, s, -1); renumberSteps(steps); markDirty(); renderEditor(); }, null, 'Move up'),
        button('↓', function () { move(steps, s, 1); renumberSteps(steps); markDirty(); renderEditor(); }, null, 'Move down'),
        button('Remove step', function () {
          // Review C6: nothing may keep pointing at the removed step or its objectives.
          var gone = {};
          quest.objectives.forEach(function (o) { if (o.step === s.index) gone[o.index] = true; });
          var objectives = Object.keys(gone).length;
          var rewardsBefore = quest.rewards.length, branchesBefore = quest.branches.length, relinked = 0;
          quest.steps.splice(quest.steps.indexOf(s), 1);
          quest.objectives = quest.objectives.filter(function (o) { return o.step !== s.index; });
          quest.branches = quest.branches.filter(function (b) { return b.step !== s.index; });
          quest.branches.forEach(function (b) { if (b.nextStep === s.index) { b.nextStep = 0; relinked++; } });
          quest.steps.forEach(function (x) { if (x.nextStep === s.index) { x.nextStep = 0; relinked++; } });
          var stepScope = enumValue('rewardScope', 'Step'), objectiveScope = enumValue('rewardScope', 'Objective');
          quest.rewards = quest.rewards.filter(function (r) {
            return !((r.scope === stepScope && r.scopeId === s.index) || (r.scope === objectiveScope && gone[r.scopeId]));
          });
          renumberSteps(sortedSteps());
          markDirty(); renderEditor();
          portalToast('Step removed with ' + objectives + ' objective(s), ' + (branchesBefore - quest.branches.length) + ' branch(es) and ' +
            (rewardsBefore - quest.rewards.length) + ' reward(s); ' + relinked + ' link(s) to it now go to the next step by order.', 'info');
        }, 'qe-danger')));
      card.appendChild(h('div', { cls: 'qe-grid' },
        field('Completion', enumSelect('completionMode', s.completionMode, function (v) { s.completionMode = v; }, true)),
        enumLabel('completionMode', s.completionMode) === 'NOf' ? field('N required', numberInput(s.nRequired, function (v) { s.nRequired = v; }, { min: 1 })) : null,
        field('Then', select(s.nextStep, stepOptions(true).filter(function (o) { return o.value !== s.index; }), function (v) { s.nextStep = v; })),
        field('Time limit', durationInput(s.timeLimitSeconds, function (v) { s.timeLimitSeconds = v; })),
        field('Fail condition', conditionSelect(s.failCondition, function (v) { s.failCondition = v; })),
        field('On enter page', input(s.onEnterPage, function (v) { s.onEnterPage = v; }, { placeholder: 'NPC script page' })),
        field('On complete page', input(s.onCompletePage, function (v) { s.onCompletePage = v; }, { placeholder: 'NPC script page' }))));
      var sfx = 's' + idPart(s.index);
      card.appendChild(textField('Title (tracker)', s.titleKey, function (k) { s.titleKey = k; }, sfx + '.title', false));
      card.appendChild(textField('Log text', s.textKey, function (k) { s.textKey = k; }, sfx + '.text', true));
      card.appendChild(textField('Ready text', s.readyTextKey, function (k) { s.readyTextKey = k; }, sfx + '.ready', true));

      var objs = quest.objectives.filter(function (o) { return o.step === s.index; }).sort(function (a, b) { return a.order - b.order; });
      var oh = h('div', { cls: 'qe-objectives' }, h('h4', { text: 'Objectives' }));
      objs.forEach(function (o) { oh.appendChild(renderObjective(o, objs, 'o' + idPart(o.index))); });
      oh.appendChild(button('+ Objective', function () {
        quest.objectives.push({
          index: newTemp(), step: s.index, order: objs.length + 1, type: 0, partyCredit: 0, targetKind: enumValue('targetKind', 'Monster'), targetId: 0, count: 1,
          map: 0, x: 0, y: 0, radius: 0, optional: false, hidden: false, condition: 0, textKey: '', dropChance: 0, params: ''
        });
        markDirty(); renderEditor();
      }));
      card.appendChild(oh);
      host.appendChild(card);
    });
    host.appendChild(button('+ Step', function () {
      var s = sortedSteps();
      quest.steps.push({ index: newTemp(), order: s.length + 1, titleKey: '', textKey: '', readyTextKey: '', completionMode: 0, nRequired: 0, nextStep: 0, timeLimitSeconds: 0, failCondition: 0, onEnterPage: '', onCompletePage: '' });
      markDirty(); renderEditor();
    }, 'primary'));
  }

  var COUNT_LABELS = { Kill: 'How many', Collect: 'How many', Deliver: 'How many', UseItem: 'How many', Craft: 'How many', Gather: 'How many', ReachLevel: 'Level', Counter: 'Target count', UseSkill: 'Times' };

  function renderObjective(o, siblings, sfx) {
    var type = enumLabel('objectiveType', o.type);
    var allowed = TYPE_TARGETS[type] || [enumLabel('targetKind', o.targetKind)];
    var kindName = enumLabel('targetKind', o.targetKind);
    var hasTarget = type !== 'ReachLevel' && type !== 'Counter';
    var row = h('div', { cls: 'qe-objective', 'data-ref': 'objective:' + o.index });
    row.appendChild(h('div', { cls: 'qe-grid' },
      field('Type', enumSelect('objectiveType', o.type, function (v) {
        o.type = v;
        var a = TYPE_TARGETS[enumLabel('objectiveType', v)];
        if (a && a.indexOf(enumLabel('targetKind', o.targetKind)) < 0) { o.targetKind = enumValue('targetKind', a[0]); o.targetId = 0; }
      }, true)),
      allowed.length > 1 ? field('Target kind', select(o.targetKind, allowed.map(function (k) { return { value: enumValue('targetKind', k), label: k }; }), function (v) { o.targetKind = v; o.targetId = 0; }, true)) : null,
      hasTarget && type !== 'ReachArea' ? field('Target', targetControl(kindName, o.targetId, function (v) { o.targetId = v; })) : null,
      COUNT_LABELS[type] ? field(COUNT_LABELS[type], numberInput(o.count, function (v) { o.count = v; }, { min: 1 })) : null,
      type === 'Collect' ? field('Drop chance', numberInput(o.dropChance, function (v) { o.dropChance = v; }, { min: 0, max: 10000 }), 'per 10,000') : null));
    if (type === 'ReachArea' && global.QuestsAssist) {
      row.appendChild(field('Place', global.QuestsAssist.placeLine({ map: o.targetId, x: o.x, y: o.y, radius: o.radius },
        function (p) { o.targetId = p.map; o.x = p.x; o.y = p.y; o.radius = p.radius; markDirty(); },
        { radius: true, defaultRadius: 5 }), 'the area a player must reach'));
    }

    var anyMore = o.partyCredit || o.condition || o.optional || o.hidden || o.textKey ||
      (type !== 'ReachArea' && (o.map || o.x || o.y || o.radius));
    var more = h('div', { cls: 'qe-grid' },
      field('Party credit', enumSelect('partyCredit', o.partyCredit, function (v) { o.partyCredit = v; })),
      field('Condition', conditionSelect(o.condition, function (v) { o.condition = v; })),
      h('div', { cls: 'qe-field' }, checkbox(o.optional, function (v) { o.optional = v; }, 'Optional'), checkbox(o.hidden, function (v) { o.hidden = v; }, 'Hidden')));
    if (type !== 'ReachArea' && global.QuestsAssist) {
      more.appendChild(field('Marker', global.QuestsAssist.placeLine({ map: o.map, x: o.x, y: o.y, radius: o.radius },
        function (p) { o.map = p.map; o.x = p.x; o.y = p.y; o.radius = p.radius; markDirty(); },
        { radius: true, defaultRadius: 10 }), 'optional: shows the place on the tracker'));
    }
    more.appendChild(textField('Text override', o.textKey, function (k) { o.textKey = k; }, sfx, false));
    row.appendChild(h('details', { cls: 'qe-more', open: anyMore ? true : null }, h('summary', { text: 'More options' }), more));

    row.appendChild(h('div', { cls: 'qe-actions' },
      h('span', { cls: 'mir-muted', text: o.index > 0 ? 'objective #' + o.index : 'new objective' }),
      button('↑', function () { move(siblings, o, -1); siblings.forEach(function (x, i) { x.order = i + 1; }); markDirty(); renderEditor(); }),
      button('↓', function () { move(siblings, o, 1); siblings.forEach(function (x, i) { x.order = i + 1; }); markDirty(); renderEditor(); }),
      button('Remove', function () { quest.objectives.splice(quest.objectives.indexOf(o), 1); markDirty(); renderEditor(); }, 'qe-danger')));
    return row;
  }

  // -- branches --

  function renderBranches(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'A step with branches ends with a choice; each branch picks the next step and records an outcome.' }));
    quest.branches.forEach(function (b, i) {
      var card = h('div', { cls: 'qe-step' });
      card.appendChild(h('div', { cls: 'qe-grid' },
        field('Step', select(b.step, stepOptions(false), function (v) { b.step = v; })),
        field('Then', select(b.nextStep, stepOptions(true), function (v) { b.nextStep = v; })),
        field('Outcome', numberInput(b.outcome, function (v) { b.outcome = v; })),
        field('Condition', conditionSelect(b.condition, function (v) { b.condition = v; }))));
      // Branches have no id: the step's id plus freeKey's counter keeps every default key (and ChoiceKey, which C.QuestChoose sends) unique.
      card.appendChild(textField('Choice', b.choiceKey, function (k) { b.choiceKey = k; }, 's' + idPart(b.step) + '.choice', false));
      card.appendChild(textField('Text', b.textKey, function (k) { b.textKey = k; }, 's' + idPart(b.step) + '.branch', true));
      card.appendChild(button('Remove branch', function () { quest.branches.splice(i, 1); markDirty(); renderEditor(); }, 'qe-danger'));
      host.appendChild(card);
    });
    host.appendChild(button('+ Branch', function () {
      var first = sortedSteps()[0];
      quest.branches.push({ step: first ? first.index : 0, choiceKey: '', textKey: '', nextStep: 0, outcome: quest.branches.length + 1, condition: 0 });
      markDirty(); renderEditor();
    }, 'primary'));
  }

  // -- endpoints --

  function renderEndpoints(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Who gives the quest (Start) and takes it back (Finish). NPCs are stored by NPCInfo index.' }));
    var table = h('div', { cls: 'qe-table' });
    quest.endpoints.forEach(function (ep, i) {
      var kind = enumLabel('endpointKind', ep.kind);
      table.appendChild(h('div', { cls: 'qe-trow' },
        field('Role', enumSelect('endpointRole', ep.role, function (v) { ep.role = v; })),
        field('Kind', enumSelect('endpointKind', ep.kind, function (v) { ep.kind = v; ep.targetId = 0; }, true)),
        kind === 'Npc' ? field('NPC', picker('npcs', ep.targetId, function (v) { ep.targetId = v; })) :
          kind === 'ItemUse' ? field('Item', picker('items', ep.targetId, function (v) { ep.targetId = v; })) : null,
        kind === 'AreaEnter' ? field('Map', picker('maps', ep.map, function (v) { ep.map = v; })) : null,
        button('Remove', function () { quest.endpoints.splice(i, 1); markDirty(); renderEditor(); }, 'qe-danger')));
    });
    host.appendChild(table);
    host.appendChild(h('div', { cls: 'qe-actions' },
      button('+ Start NPC', function () { quest.endpoints.push({ role: 0, kind: 0, targetId: 0, map: 0 }); markDirty(); renderEditor(); }, 'primary'),
      button('+ Finish NPC', function () { quest.endpoints.push({ role: 1, kind: 0, targetId: 0, map: 0 }); markDirty(); renderEditor(); })));
  }

  // -- rewards --

  function renderRewards(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Choice group 0 = always given; n = the player picks one reward of group n. Exp and gold keep the server rates.' }));
    var table = h('div', { cls: 'qe-table' });
    quest.rewards.forEach(function (r, i) {
      var scope = enumLabel('rewardScope', r.scope);
      var kind = enumLabel('rewardKind', r.kind);
      var scopeCtl = null;
      if (scope === 'Step') scopeCtl = field('Step', select(r.scopeId, stepOptions(false), function (v) { r.scopeId = v; }));
      else if (scope === 'Objective') {
        var opts = [{ value: 0, label: '(choose)' }].concat(quest.objectives.map(function (o) {
          return { value: o.index, label: (o.index > 0 ? '#' + o.index : 'new') + ' ' + enumLabel('objectiveType', o.type) + ' ' + enumLabel('targetKind', o.targetKind) + ' ' + o.targetId };
        }));
        scopeCtl = field('Objective', select(r.scopeId, opts, function (v) { r.scopeId = v; }));
      } else if (scope === 'Outcome') scopeCtl = field('Outcome', numberInput(r.scopeId, function (v) { r.scopeId = v; }));
      var target = kind === 'Item' ? field('Item', picker('items', r.targetId, function (v) { r.targetId = v; })) :
        kind === 'Spell' ? field('Spell', picker('magics', r.targetId, function (v) { r.targetId = v; })) :
          (kind === 'Exp' || kind === 'Gold' || kind === 'Credit') ? null : field('Target id', numberInput(r.targetId, function (v) { r.targetId = v; }));
      table.appendChild(h('div', { cls: 'qe-trow' },
        field('Scope', enumSelect('rewardScope', r.scope, function (v) { r.scope = v; r.scopeId = 0; }, true)),
        scopeCtl,
        field('Kind', enumSelect('rewardKind', r.kind, function (v) { r.kind = v; r.targetId = 0; }, true)),
        target,
        field('Amount', numberInput(r.amount, function (v) { r.amount = v; }, { min: 0 })),
        field('Scaling', enumSelect('rewardScaling', r.scaling, function (v) { r.scaling = v; })),
        field('Choice group', numberInput(r.choiceGroup, function (v) { r.choiceGroup = v; }, { min: 0 })),
        field('Condition', conditionSelect(r.condition, function (v) { r.condition = v; })),
        h('div', { cls: 'qe-field' }, checkbox(r.firstTimeOnly, function (v) { r.firstTimeOnly = v; }, 'First time only')),
        button('Remove', function () { quest.rewards.splice(i, 1); markDirty(); renderEditor(); }, 'qe-danger')));
    });
    host.appendChild(table);
    host.appendChild(button('+ Reward', function () {
      quest.rewards.push({ scope: 0, scopeId: 0, kind: 0, targetId: 0, amount: 0, scaling: 0, choiceGroup: 0, condition: 0, firstTimeOnly: false });
      markDirty(); renderEditor();
    }, 'primary'));
  }

  // -- conditions (And / Or / Not / Leaf tree; owned by the quest) --

  /* The argument control of a leaf: a picker for ids, a select for enums, a number otherwise. */
  function leafArg(c) {
    var leaf = enumLabel('conditionLeaf', c.leaf);
    var set1 = function (v) { c.arg1 = v; };
    var set2 = function (v) { c.arg2 = v; };
    switch (leaf) {
      case 'ItemCount': return [field('Item', picker('items', c.arg1, set1)), field('Count', numberInput(c.arg2, set2, { min: 0 }))];
      case 'Equipped': return [field('Item', picker('items', c.arg1, set1))];
      case 'MapIs': return [field('Map', picker('maps', c.arg1, set1))];
      case 'QuestStatus': return [field('Quest', picker('quests', c.arg1, set1)), field('Status', enumSelect('runStatus', c.arg2, set2))];
      case 'QuestCompletedCount': return [field('Quest', picker('quests', c.arg1, set1)), field('Times', numberInput(c.arg2, set2, { min: 0 }))];
      case 'QuestOutcome': return [field('Quest', picker('quests', c.arg1, set1)), field('Outcome', numberInput(c.arg2, set2))];
      case 'Class': return [field('Class', enumSelect('mirClass', c.arg1, set1))];
      case 'Gender': return [field('Gender', enumSelect('mirGender', c.arg1, set1))];
      case 'QuestVar': return [field('Variable', select(c.arg1, [{ value: 0, label: '(choose)' }].concat(quest.vars.map(function (v) {
        return { value: v.index, label: v.authorKey + (v.index > 0 ? ' #' + v.index : ' (new)') };
      })), set1)), field('Value', numberInput(c.arg2, set2))];
      case 'Flag': return [field('Flag number', numberInput(c.arg1, set1, { min: 0 }))];
      case 'TimeOfDay': return [field('Hour (0-23)', numberInput(c.arg1, set1, { min: 0, max: 23 }))];
      case 'DayOfWeek': return [field('Day (0 = Sunday)', numberInput(c.arg1, set1, { min: 0, max: 6 }))];
      default: return [field('Value', numberInput(c.arg1, set1)), field('Arg 2', numberInput(c.arg2, set2))];
    }
  }

  function removeCondition(c) {
    var doomed = [c.index];
    for (var changed = true; changed;) {
      changed = false;
      quest.conditions.forEach(function (x) { if (doomed.indexOf(x.parent) >= 0 && doomed.indexOf(x.index) < 0) { doomed.push(x.index); changed = true; } });
    }
    quest.conditions = quest.conditions.filter(function (x) { return doomed.indexOf(x.index) < 0; });
  }

  function renderConditionNode(c, depth) {
    var op = enumLabel('conditionOp', c.op);
    var kids = quest.conditions.filter(function (x) { return x.parent === c.index; }).sort(function (a, b) { return a.order - b.order; });
    var node = h('div', { cls: 'qe-cond', style: 'margin-left:' + Math.min(depth, 8) * 18 + 'px', 'data-ref': 'condition:' + c.index });
    node.appendChild(h('div', { cls: 'qe-inline' },
      h('span', { cls: 'qe-cond-id', text: c.index > 0 ? '#' + c.index : 'new' }),
      field('Op', enumSelect('conditionOp', c.op, function (v) { c.op = v; if (enumLabel('conditionOp', v) === 'Leaf' && !c.leaf) c.leaf = 1; }, true)),
      op === 'Leaf' ? field('Check', enumSelect('conditionLeaf', c.leaf, function (v) { c.leaf = v; c.arg1 = 0; c.arg2 = 0; }, true)) : null,
      op === 'Leaf' ? field('Compare', enumSelect('compare', c.cmp, function (v) { c.cmp = v; })) : null,
      op === 'Leaf' ? leafArg(c) : null,
      op !== 'Leaf' && !(op === 'Not' && kids.length >= 1) ? button('+ Part', function () {
        quest.conditions.push({ index: newTemp(), op: enumValue('conditionOp', 'Leaf'), parent: c.index, order: kids.length + 1, leaf: 1, arg1: 0, arg2: 0, cmp: enumValue('compare', 'Ge') });
        markDirty(); renderEditor();
      }) : null,
      button('Remove', function () { removeCondition(c); markDirty(); renderEditor(); }, 'qe-danger')));
    var box = h('div', null, node);
    kids.forEach(function (k) { box.appendChild(renderConditionNode(k, depth + 1)); });
    return box;
  }

  function renderConditions(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Conditions this quest owns (deleted with it). A root condition can be picked as the accept / visible rule, a step\'s fail rule, or an objective, branch or reward condition. Shared conditions (owner 0) are listed there too.' }));
    var roots = quest.conditions.filter(function (c) { return !c.parent; }).sort(function (a, b) { return a.index - b.index; });
    roots.forEach(function (c) { host.appendChild(h('div', { cls: 'qe-step' }, renderConditionNode(c, 0))); });
    host.appendChild(h('div', { cls: 'qe-actions' },
      button('+ All of (And)', function () { quest.conditions.push({ index: newTemp(), op: enumValue('conditionOp', 'And'), parent: 0, order: 0, leaf: 0, arg1: 0, arg2: 0, cmp: 0 }); markDirty(); renderEditor(); }, 'primary'),
      button('+ Any of (Or)', function () { quest.conditions.push({ index: newTemp(), op: enumValue('conditionOp', 'Or'), parent: 0, order: 0, leaf: 0, arg1: 0, arg2: 0, cmp: 0 }); markDirty(); renderEditor(); }),
      button('+ Single check', function () { quest.conditions.push({ index: newTemp(), op: enumValue('conditionOp', 'Leaf'), parent: 0, order: 0, leaf: 1, arg1: 0, arg2: 0, cmp: enumValue('compare', 'Ge') }); markDirty(); renderEditor(); })));
    var shared = meta.sharedConditions || [];
    if (shared.length) {
      host.appendChild(h('h4', { text: 'Shared conditions (read only here)' }));
      host.appendChild(h('ul', { cls: 'qe-uses' }, shared.map(function (c) {
        return h('li', { text: '#' + c.index + (c.parent ? ' (part of #' + c.parent + ')' : '') + ': ' + conditionSummary(c, shared) });
      })));
    }
  }

  // -- variables --

  function renderVars(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Quest variables for the QuestVar condition and the SETQUESTVAR / CHECKQUESTVAR script keywords.' }));
    var table = h('div', { cls: 'qe-table' });
    quest.vars.forEach(function (v, i) {
      table.appendChild(h('div', { cls: 'qe-trow', 'data-ref': 'variable:' + v.index },
        h('span', { cls: 'qe-cond-id', text: v.index > 0 ? '#' + v.index : 'new' }),
        field('Key', input(v.authorKey, function (x) { v.authorKey = x.trim(); })),
        field('Default', numberInput(v.default, function (x) { v.default = x; })),
        button('Remove', function () { quest.vars.splice(i, 1); markDirty(); renderEditor(); }, 'qe-danger')));
    });
    host.appendChild(table);
    host.appendChild(button('+ Variable', function () { quest.vars.push({ index: newTemp(), authorKey: '', default: 0 }); markDirty(); renderEditor(); }, 'primary'));
  }

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

  function renderTexts(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Every text of the quest in one table, for proofreading. The keys are the same ones the other tabs edit.' }));
    host.appendChild(h('div', { cls: 'qe-actions' }, checkbox(textsOnlyEmpty, function (v) { textsOnlyEmpty = v; renderEditor(); }, 'Only empty texts')));
    var fields = textFields().filter(function (f) {
      return !textsOnlyEmpty || !f.key || !String(quest.texts[f.key] || '').trim();
    });
    if (!fields.length) { host.appendChild(h('p', { cls: 'mir-muted', text: 'No texts.' })); return; }
    fields.forEach(function (f) { host.appendChild(textField(f.label, f.key, f.setKey, f.suffix, f.multiline)); });
  }

  async function loadPlayers() {
    var j = await OpsUI.get('/api/admin/players-online');
    testState.players = (j && j.rows) || [];
    testState.players.sort(function (a, b) { return (b.isGM ? 1 : 0) - (a.isGM ? 1 : 0) || String(a.name).localeCompare(String(b.name)); });
    if (!testState.players.some(function (p) { return p.characterIndex === testState.character; }))
      testState.character = testState.players.length ? testState.players[0].characterIndex : 0;
  }

  async function questAction(action, extra, btn, character) {
    var ch = character || testState.character;
    if (!ch) { portalToast('Pick an online character first.', 'warn'); return null; }
    var url = '/api/admin/characters/' + ch + (action === 'reset' ? '/quests/reset' : '/quests/edit');
    var body = Object.assign({ questIndex: quest.index }, action === 'reset' ? {} : { action: action }, extra || {});
    return OpsUI.withBusy(btn, function () { return OpsUI.post(url, body); }, '…');
  }

  function renderTest(host) {
    if (!quest.index) { host.appendChild(h('p', { cls: 'mir-muted', text: 'Save the quest first: the test works on the saved version.' })); return; }
    if (testState.players === null) {
      host.appendChild(h('p', { cls: 'mir-muted', text: 'Loading…' }));
      loadPlayers().then(function () { renderEditor(); });
      return;
    }
    if (isDirty()) host.appendChild(h('p', { cls: 'qe-warn', text: 'Unsaved changes: the test uses the saved version. Save first.' }));
    if (!testState.players.length) {
      host.appendChild(h('p', { cls: 'mir-muted', text: 'No one is online. Log in a test character, then Refresh.' }));
      host.appendChild(button('Refresh', function () { testState.players = null; renderEditor(); }));
      return;
    }

    var players = testState.players;
    var steps = sortedSteps().filter(function (s) { return s.index > 0; });
    if (!testState.step) testState.step = steps.length ? steps[0].index : -1;
    var chars = players.map(function (p) {
      return { value: p.characterIndex, label: p.name + ' (Lv ' + p.level + ', ' + (p.mapTitle || '?') + ')' + (p.isGM ? ' [GM]' : '') };
    });
    var stepOpts = steps.map(function (s) { return { value: s.index, label: stepLabel(s.index) }; }).concat([{ value: -1, label: 'Ready to turn in' }]);
    host.appendChild(h('div', { cls: 'qe-grid' },
      field('Character', select(testState.character, chars, function (v) { testState.character = v; })),
      field('Step', select(testState.step, stepOpts, function (v) { testState.step = v; }))));
    host.appendChild(h('div', { cls: 'qe-actions' },
      button('Start', function (e) { questAction('start', null, e.currentTarget); }, 'primary'),
      button('Jump to step', function (e) { questAction('step', { step: testState.step }, e.currentTarget); }),
      button('Complete (no rewards)', async function (e) {
        if (!await portalConfirm('Mark quest #' + quest.index + ' completed on this character? No rewards are given.', { title: 'Complete quest', confirmLabel: 'Complete' })) return;
        questAction('complete', null, e.currentTarget);
      }),
      button('Reset', async function (e) {
        if (!await portalConfirm('Remove this quest\'s progress and history from the character?', { title: 'Reset quest', confirmLabel: 'Reset', danger: true })) return;
        questAction('reset', null, e.currentTarget);
      }, 'qe-danger'),
      button('Refresh', function () { testState.players = null; renderEditor(); })));
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Start skips the accept checks. To test the real offer, talk to the giver in game.' }));
    var picked = players.filter(function (p) { return p.characterIndex === testState.character; })[0];
    if (picked) {
      host.appendChild(h('p', null, button('Open ' + picked.name + ' in Characters', function () {
        if (global.portalOpenDeepLink) global.portalOpenDeepLink('characters', { q: picked.name, character: String(picked.characterIndex) });
      })));
    }
  }

  // -- target groups --

  var groupEdit = null;

  function renderGroups(host) {
    host.appendChild(h('p', { cls: 'mir-muted', text: 'Named sets of monsters or items for MonsterGroup / ItemGroup objectives ("any wolf"). Shared by every quest.' }));
    var groups = (meta && meta.groups) || [];
    host.appendChild(h('ul', { cls: 'qe-uses' }, groups.map(function (g) {
      return h('li', null,
        h('button', { type: 'button', cls: 'qe-link', text: g.authorKey + ' #' + g.index, onclick: function () { groupEdit = JSON.parse(JSON.stringify(g)); rerenderGroups(); } }),
        h('span', { text: ' ' + enumLabel('groupKind', g.kind) + ', ' + g.members.length + ' member(s)' }),
        ' ', button('Where used', function () { whereUsed('group', g.index); }));
    })));
    host.appendChild(button('+ Group', function () { groupEdit = { index: 0, authorKey: '', kind: 0, members: [] }; rerenderGroups(); }, 'primary'));
    if (!groupEdit) return;
    var g = groupEdit;
    var lookup = g.kind === 1 ? 'items' : 'monsters';
    var addId = 0;
    var card = h('div', { cls: 'qe-step' });
    card.appendChild(h('h4', { text: g.index ? 'Group #' + g.index : 'New group' }));
    card.appendChild(h('div', { cls: 'qe-grid' },
      field('Key', input(g.authorKey, function (v) { g.authorKey = v.trim(); })),
      field('Kind', select(g.kind, enumList('groupKind'), function (v) { g.kind = v; g.members = []; rerenderGroups(); }))));
    card.appendChild(h('div', { cls: 'qe-chips' }, g.members.map(function (m, i) {
      return h('span', { cls: 'qe-chip' }, (lookupLabel(lookup, m) || '?') + ' #' + m, button('×', function () { g.members.splice(i, 1); rerenderGroups(); }));
    })));
    card.appendChild(h('div', { cls: 'qe-inline' }, field('Add', picker(lookup, 0, function (v) { addId = v; })),
      button('Add', function () { if (addId && g.members.indexOf(addId) < 0) g.members.push(addId); rerenderGroups(); })));
    card.appendChild(h('div', { cls: 'qe-actions' },
      button('Save group', async function (e) {
        var j = await OpsUI.withBusy(e.currentTarget, function () { return OpsUI.post(API + '/groups/save', { group: g }); }, 'Saving…');
        if (j && j.__ok) { groupEdit = null; await loadMeta(); rerenderGroups(); }
        else if (j) { problems = errorProblems(j); portalToast((problems[0] && problems[0].message) || j.message || 'Not saved', 'error'); }
      }, 'primary'),
      g.index ? button('Delete group', async function (e) {
        if (!await portalConfirm('Delete group ' + g.authorKey + '?', { title: 'Delete group', confirmLabel: 'Delete', danger: true })) return;
        var j = await OpsUI.withBusy(e.currentTarget, function () { return OpsUI.post(API + '/groups/' + g.index + '/delete', {}); }, 'Deleting…');
        if (j && j.__ok) { groupEdit = null; await loadMeta(); rerenderGroups(); }
        else if (j && j.data && j.data.uses) { showUses(j.data.uses, 'Group still used by'); tab = 'whereused'; renderEditor(); }
      }, 'qe-danger') : null,
      button('Close', function () { groupEdit = null; rerenderGroups(); })));
    host.appendChild(card);
  }

  function rerenderGroups() {
    if (quest) { tab = 'groups'; renderEditor(); return; }
    tab = 'groups';
    renderEmptyTab();
  }

  // ---- layout ------------------------------------------------------------------------------------------------------

  function render(sectionEl) {
    clear(sectionEl);
    var search = h('input', { type: 'search', placeholder: 'Search name, key or #index', cls: 'qe-search' });
    search.addEventListener('input', function () { filters.q = search.value; renderList(); });
    function filterSel(role, key) {
      var s = h('select', { 'data-role': role });
      s.addEventListener('change', function () { filters[key] = s.value; renderList(); });
      return s;
    }
    var errOnly = h('input', { type: 'checkbox' });
    errOnly.addEventListener('change', function () { filters.errors = errOnly.checked; renderList(); });
    var sortSel = h('select', { 'data-role': 'f-sort' });
    [{ value: 'index', label: 'By id' }, { value: 'name', label: 'By name' }, { value: 'problems', label: 'Problems first' }].forEach(function (o) {
      var opt = h('option', { value: o.value, text: o.label });
      if (o.value === filters.sort) opt.selected = true;
      sortSel.appendChild(opt);
    });
    sortSel.addEventListener('change', function () { filters.sort = sortSel.value; renderList(); });

    sectionEl.appendChild(h('div', { cls: 'mir-card qe-layout' },
      h('div', { cls: 'qe-side' },
        h('div', { cls: 'qe-side-head' },
          h('h3', { text: 'Quests' }),
          button('New', newQuest, 'primary'),
          button('Import', pickImportFile, null, 'Import a .quest.json export file (preview first)'),
          button('Refresh', function (e) { OpsUI.withBusy(e.currentTarget, async function () { if (global.QuestsAssist) global.QuestsAssist.clearHints(); lookups = {}; DbLookups.clearCache(); await loadMeta(); await ensureLookups(); await loadList(); }); })),
        search,
        h('div', { cls: 'qe-filters' }, filterSel('f-category', 'category'), filterSel('f-group', 'group'), filterSel('f-storyline', 'storyline'),
          h('label', { cls: 'qe-check' }, errOnly, ' has errors')),
        sortSel,
        h('div', { cls: 'mir-muted qe-count', 'data-role': 'count' }),
        h('div', { cls: 'qe-list', 'data-role': 'list' })),
      h('div', { cls: 'qe-main' },
        h('div', { cls: 'qe-import hidden', 'data-role': 'import' }),
        h('div', { cls: 'qe-editor', 'data-role': 'editor' })),
      h('div', { cls: 'hidden', 'data-role': 'datalists' })));

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
  }

  global.AdminSections.register('quests', {
    load: async function (sectionEl) {
      root = sectionEl;
      render(sectionEl);
      await loadMeta();
      await ensureLookups();
      await loadList();
      renderEditor();
    },
    show: function () {
      if (!isDirty()) loadList();
    },
    hide: function () {}
  });

  portalRegisterDeepLink('quests', function (p) {
    if (!root || !p || !p.quest) return;
    var idx = Number(p.quest);
    if (idx > 0) openQuest(idx);
  });

  global.QuestsPage = {
    blankQuest: blankQuest,
    isDirty: isDirty,
    openDraft: openDraft,
    check: function () { return save(true, null); },
    saveNow: function () { return save(false, el('save-btn')); },
    reloadMeta: function () { return loadMeta(); },
    startOn: function (ch) { return questAction('start', null, null, ch); },
    wizard: wizard,
    jumpTo: jumpTo,
    meta: function () { return meta; },
    authorKeys: function () { return list.map(function (x) { return x.authorKey || ''; }); },
    kit: {
      h: h, clear: clear, field: field, input: input, numberInput: numberInput, checkbox: checkbox, select: select,
      enumSelect: enumSelect, enumChoices: enumChoices, enumList: enumList, enumLabel: enumLabel, enumValue: enumValue,
      button: button, picker: picker, targetControl: targetControl, lookupLabel: lookupLabel, durationInput: durationInput,
      newTemp: newTemp, idPart: idPart, slug: slug, jumpTo: jumpTo
    }
  };
})(window);
