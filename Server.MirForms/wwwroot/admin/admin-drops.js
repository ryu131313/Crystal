/**
 * Drops editor (Game Data tab "Drops", drops plan phase 2): the drop lists of SystemData.db as an ag-Grid of rows.
 *
 * Left: the list picker (search over list names and the monsters that use a list, kind filter with counts, "New list…").
 * Right: the selected list: header (name, kind, users, a "shared list" note), toolbar, grid. Rows come in pre-order (a group's
 * members follow it, indented by depth). note / include / disabled rows are greyed and kept as they are; notes are hidden
 * unless "Show notes" is ticked, but every row is sent back on save, in place. A disabled row is fixed by picking an item.
 *
 * API (all under /api/admin/drops, { ok, message, data }): GET lists, GET monster?index=, GET list?id=; POST save / copy /
 * delete-disabled (dry run first, then the real call; 400 = validation with data.errors[{ row, field, message }], 409 = the
 * list changed since it was loaded) and POST create. "Class drops…" calls /api/admin/scripts/drops/class-toggle (all lists).
 * Writes go through portalApi (CSRF header); every server text reaches the DOM through textContent.
 *
 * Wired by admin-gamedata.js: TABS entry { id: 'drops', kind: 'drops' } -> DropsEditor.mount(pane) / show() / openList(id) /
 * openMonster(index); hash #section=gamedata&table=drops&list=<id> or &monster=<index>. Dirty key 'drops' (DbSave).
 *
 * Global: DropsEditor.
 */
(function (global) {
  'use strict';

  var API = '/api/admin/drops';
  var DIRTY_KEY = 'drops';
  var LS_NOTES = 'mircraft_drops_shownotes';
  var LIST_LIMIT = 400;
  var KINDS = ['monster', 'named', 'fishing', 'awakening', 'strongbox', 'blackstone', 'dragon'];
  var KIND_LABEL = { monster: 'Monster', named: 'Named', fishing: 'Fishing', awakening: 'Awakening', strongbox: 'Strongbox', blackstone: 'Blackstone', dragon: 'Dragon' };
  var ROW_LABEL = { item: 'Item', gold: 'Gold', group: 'Group', include: 'Include', note: 'Note', disabled: 'Disabled' };
  var MODE_LABEL = { '': 'normal', '*': 'random (*)', '^': 'first (^)' };
  var TYPED = { item: true, gold: true, group: true };
  var MAX_DRAGON_LEVEL = 13;

  var ui = {};
  var mounted = false;
  var lists = [];            // GET lists
  var listsLoaded = false;
  var cur = null;            // { id, name, kind, monsterIndex, fishingType, dragon, rev, users, rows }
  var emptyMonster = null;   // { monster, listId: null } while a monster without drops is shown
  var dirty = false;
  var showNotes = false;
  var api = null;            // ag-Grid api
  var itemLookup = null;
  var nextNewId = -1;
  var issues = [];           // [{ rowId, field, message }]

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function button(label, cls, title) {
    var b = el('button', 'mir-btn' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    if (title) b.title = title;
    return b;
  }
  function toast(msg, kind) { if (global.portalToast) global.portalToast(msg, kind || 'info'); }
  function lsGet(k) { try { return global.localStorage ? global.localStorage.getItem(k) : null; } catch (e) { return null; } }
  function lsSet(k, v) { try { if (global.localStorage) global.localStorage.setItem(k, v); } catch (e) { /* storage off */ } }
  function unwrap(j) { return global.OpsUI ? global.OpsUI.unwrap(j) : (j && j.data); }

  // ---- server calls ---------------------------------------------------------------------------------------------------

  /** GET -> data, or null after an error (toasted unless quiet). */
  async function getData(url, quiet) {
    var res = await global.OpsUI.fetchResult(url);
    if (!res.ok) {
      if (!quiet) toast(res.error || ('Request failed (HTTP ' + res.status + ').'), 'error');
      return null;
    }
    var d = unwrap(res.data);
    return d && typeof d === 'object' ? d : null;
  }

  /** POST through portalApi (CSRF) -> { status, ok, message, data } or null after a transport error (toasted). */
  async function post(url, body) {
    var r, j;
    try {
      r = await global.portalApi(url, { method: 'POST', body: body });
      j = await r.json().catch(function () { return {}; });
    } catch (e) {
      toast('Request failed: ' + ((e && e.message) || e), 'error');
      return null;
    }
    j = j && typeof j === 'object' ? j : {};
    return { status: r.status, ok: r.ok && j.ok !== false, message: j.message || j.error || '', data: j.data && typeof j.data === 'object' ? j.data : {} };
  }

  // ---- dirty state ----------------------------------------------------------------------------------------------------

  function setDirty(on) {
    dirty = !!on;
    if (global.DbSave && global.DbSave.setDirty) global.DbSave.setDirty(DIRTY_KEY, dirty);
    if (ui.save) ui.save.classList.toggle('db-has-pending', dirty);
    if (ui.dirtyPill) ui.dirtyPill.classList.toggle('hidden', !dirty);
  }

  /** Resolves true when there are no unsaved edits or the admin agrees to drop them. */
  async function confirmDiscard(what) {
    if (!dirty || !cur) return true;
    var ok = await global.portalConfirm('The drop list "' + cur.name + '" has unsaved changes. ' + (what || 'Discard them?'),
      { title: 'Unsaved drop changes', confirmLabel: 'Discard changes', danger: true });
    if (ok) setDirty(false);
    return !!ok;
  }

  // ---- list picker ----------------------------------------------------------------------------------------------------

  async function loadLists(quiet) {
    var d = await getData(API + '/lists', quiet);
    if (!d) return false;
    lists = Array.isArray(d.lists) ? d.lists : [];
    listsLoaded = true;
    renderKindFilter();
    renderPicker();
    return true;
  }

  function usersText(users) {
    return (users || []).map(function (u) { return u && u.name ? u.name : ('#' + (u && u.index)); }).join(', ');
  }

  function kindText(l) {
    var k = KIND_LABEL[l.kind] || l.kind || '?';
    return l.kind === 'fishing' && l.fishingType != null ? k + ' ' + l.fishingType : k;
  }

  function renderKindFilter() {
    var sel = ui.kind;
    var keep = sel.value;
    sel.textContent = '';
    var counts = Object.create(null);
    lists.forEach(function (l) { counts[l.kind] = (counts[l.kind] || 0) + 1; });
    var all = el('option', null, 'All kinds (' + lists.length + ')');
    all.value = '';
    sel.appendChild(all);
    KINDS.forEach(function (k) {
      var o = el('option', null, KIND_LABEL[k] + ' (' + (counts[k] || 0) + ')');
      o.value = k;
      sel.appendChild(o);
    });
    sel.value = keep || '';
  }

  function matches(l, q, kind) {
    if (kind && l.kind !== kind) return false;
    if (!q) return true;
    if (/^#\d+$/.test(q)) return String(l.id) === q.slice(1);
    if (String(l.name || '').toLowerCase().indexOf(q) >= 0) return true;
    return (l.users || []).some(function (u) { return u && String(u.name || '').toLowerCase().indexOf(q) >= 0; });
  }

  function renderPicker() {
    var q = String(ui.search.value || '').trim().toLowerCase();
    var kind = ui.kind.value;
    var found = lists.filter(function (l) { return matches(l, q, kind); });
    ui.pickList.textContent = '';
    found.slice(0, LIST_LIMIT).forEach(function (l) {
      var b = el('button', 'gd-drops-pick' + (cur && cur.id === l.id ? ' gd-drops-pick-active' : ''));
      b.type = 'button';
      b.setAttribute('data-id', String(l.id));
      b.appendChild(el('span', 'gd-drops-pick-name', l.name || ('#' + l.id)));
      var meta = kindText(l) + ' · ' + (Number(l.typed) || 0) + ' row(s)' + (Number(l.disabled) ? ' · ' + l.disabled + ' disabled' : '');
      b.appendChild(el('span', 'gd-drops-pick-meta mir-muted', meta));
      var u = usersText(l.users);
      if (u) b.appendChild(el('span', 'gd-drops-pick-users mir-muted', 'Used by: ' + u));
      b.addEventListener('click', function () { openList(l.id, true); });
      ui.pickList.appendChild(b);
    });
    ui.pickCount.textContent = found.length + ' of ' + lists.length + ' list(s)' + (found.length > LIST_LIMIT ? ' (showing the first ' + LIST_LIMIT + ': refine the search)' : '');
  }

  function markPicked() {
    Array.prototype.forEach.call(ui.pickList.querySelectorAll('[data-id]'), function (b) {
      b.classList.toggle('gd-drops-pick-active', !!cur && b.getAttribute('data-id') === String(cur.id));
    });
  }

  // ---- open a list / a monster ----------------------------------------------------------------------------------------

  /** Load list `id` into the grid. fromPicker: write the hash (list=). Resolves true when shown. */
  async function openList(id, fromPicker) {
    id = Number(id);
    if (!isFinite(id)) return false;
    if (cur && cur.id === id && !emptyMonster && dirty) return true;   // already open with edits: keep them
    if (!(await confirmDiscard('Discard them and open another list?'))) return false;
    var d = await getData(API + '/list?id=' + encodeURIComponent(id));
    if (!d) return false;
    emptyMonster = null;
    applyList(d);
    if (fromPicker && global.portalSetHashParams) global.portalSetHashParams({ table: 'drops', list: id, monster: null, index: null }, true);
    return true;
  }

  /** Resolve a monster's list (GET monster?index=) and open it, or show "No drops yet" with Create list. */
  async function openMonster(index) {
    index = Number(index);
    if (!(index > 0)) return false;
    if (!(await confirmDiscard('Discard them and open another list?'))) return false;
    var d = await getData(API + '/monster?index=' + encodeURIComponent(index));
    if (!d) return false;
    if (d.listId != null) {
      var ok = await openList(d.listId, false);
      if (ok && d.shared && cur) cur.sharedNote = sharedText(d);
      if (ok) renderHead();
      return ok;
    }
    showEmptyMonster(d);
    return true;
  }

  function sharedText(d) {
    var m = d && d.monster;
    var users = usersText(d && d.users);
    if (m && m.dropPath) return 'This monster uses the shared list "' + (d.listName || m.dropPath) + '" (DropPath)' + (users ? ', used by: ' + users : '') + '. Changes affect all of them.';
    return users ? 'Shared list: changes affect every monster that uses it (' + users + ').' : '';
  }

  function applyList(d) {
    cur = {
      id: Number(d.id), name: d.name || ('#' + d.id), kind: d.kind || '', monsterIndex: d.monsterIndex, fishingType: d.fishingType,
      dragon: !!d.dragon || d.kind === 'dragon', rev: d.rev, users: Array.isArray(d.users) ? d.users : [], rows: [], sharedNote: ''
    };
    setRows(Array.isArray(d.rows) ? d.rows : []);
    issues = [];
    renderIssues();
    setDirty(false);
    ui.empty.classList.add('hidden');
    ui.body.classList.remove('hidden');
    ui.hint.classList.add('hidden');
    renderHead();
    markPicked();
    syncGrid(true);
    syncTools();
  }

  function setRows(rows) {
    cur.rows = rows.map(function (r) {
      return {
        id: Number(r.id), parentId: r.parentId == null ? null : Number(r.parentId), depth: Number(r.depth) || 0, kind: r.kind || 'note',
        chance: r.chance == null ? null : Number(r.chance), itemIndex: r.itemIndex == null ? null : Number(r.itemIndex),
        itemName: r.itemName || '', quest: !!r.quest, gold: r.gold == null ? null : Number(r.gold),
        groupMode: r.groupMode == null ? '' : String(r.groupMode), dragonLevel: r.dragonLevel == null ? null : Number(r.dragonLevel),
        includeListId: r.includeListId == null ? null : Number(r.includeListId), text: r.text || '', warning: r.warning || ''
      };
    });
    fixDepths();
  }

  function showEmptyMonster(d) {
    cur = null;
    emptyMonster = d;
    setDirty(false);
    issues = [];
    renderIssues();
    ui.body.classList.add('hidden');
    ui.empty.classList.remove('hidden');
    ui.hint.classList.add('hidden');
    ui.emptyText.textContent = '';
    var m = d.monster || {};
    ui.emptyText.appendChild(el('strong', null, (m.name || ('Monster #' + m.index)) + ': no drops yet.'));
    ui.emptyText.appendChild(el('span', 'mir-muted', m.dropPath
      ? ' Its DropPath is "' + m.dropPath + '"; no list of that name exists. Create list makes it.'
      : ' Create list makes an empty drop list for this monster.'));
    markPicked();
  }

  async function createForMonster() {
    var m = emptyMonster && emptyMonster.monster;
    if (!m) return;
    var res = await post(API + '/create', { monsterIndex: Number(m.index) });
    if (!res) return;
    if (!res.ok) { toast(res.message || ('Create failed (HTTP ' + res.status + ').'), 'error'); return; }
    toast(res.message || 'Drop list created.', 'ok');
    await loadLists(true);
    if (res.data.id != null) await openList(res.data.id, true);
  }

  async function createNamed() {
    if (!(await confirmDiscard('Discard them and create a new list?'))) return;
    var v = await global.portalDialog({
      title: 'New drop list',
      message: 'A named list is not tied to one monster: a DropPath shared by several monsters, an NPC DROP list or an #INSERT target. ' +
        'Monster lists are created from the monster (Monsters tab → Drops).',
      fields: [{ name: 'name', label: 'Name (e.g. Shared/Wolves.txt)', type: 'text', value: '', required: true }],
      confirmLabel: 'Create',
      validate: function (x) { return String(x.name || '').trim() ? null : 'Enter a name.'; }
    });
    if (!v) return;
    var res = await post(API + '/create', { name: String(v.name).trim() });
    if (!res) return;
    if (!res.ok) { toast(res.message || ('Create failed (HTTP ' + res.status + ').'), 'error'); return; }
    toast(res.message || 'Drop list created.', 'ok');
    await loadLists(true);
    if (res.data.id != null) await openList(res.data.id, true);
  }

  // ---- header + toolbar state -----------------------------------------------------------------------------------------

  function renderHead() {
    if (!cur) return;
    ui.title.textContent = cur.name;
    ui.kindPill.textContent = kindText(cur);
    var u = usersText(cur.users);
    ui.users.textContent = u ? 'Used by: ' + u : (cur.kind === 'monster' ? '' : 'Not used by any monster');
    var shared = cur.sharedNote || '';
    if (!shared && (cur.users.length > 1 || (cur.kind === 'named' && cur.users.length > 0))) {
      shared = 'Shared list' + (cur.kind === 'named' ? ' (DropPath)' : '') + ': changes affect every monster listed above.';
    }
    ui.shared.textContent = shared;
    ui.shared.classList.toggle('hidden', !shared);
    var counts = Object.create(null);
    cur.rows.forEach(function (r) { counts[r.kind] = (counts[r.kind] || 0) + 1; });
    var parts = [];
    ['item', 'gold', 'group', 'include', 'disabled', 'note'].forEach(function (k) { if (counts[k]) parts.push(counts[k] + ' ' + ROW_LABEL[k].toLowerCase()); });
    ui.meta.textContent = '#' + cur.id + (parts.length ? ' · ' + parts.join(', ') : ' · empty');
  }

  function selectedRows() { return api ? api.getSelectedRows() : []; }

  function syncTools() {
    var has = !!cur;
    var sel = has ? selectedRows() : [];
    var one = sel.length === 1 ? sel[0] : null;
    [ui.addItem, ui.addGold, ui.addGroup, ui.save, ui.revert, ui.copy, ui.delDisabled].forEach(function (b) { b.disabled = !has; });
    ui.addToGroup.disabled = !(one && one.kind === 'group');
    ui.del.disabled = !sel.length;
    ui.up.disabled = !one;
    ui.down.disabled = !one;
  }

  // ---- row structure helpers ------------------------------------------------------------------------------------------

  function rowIndexOf(id) {
    for (var i = 0; i < cur.rows.length; i++) if (cur.rows[i].id === id) return i;
    return -1;
  }

  /** Depth from parentId (rows are in pre-order: a parent always comes before its members). */
  function fixDepths() {
    var depth = Object.create(null);
    cur.rows.forEach(function (r) {
      var d = r.parentId != null && depth[r.parentId] != null ? depth[r.parentId] + 1 : 0;
      if (r.parentId != null && depth[r.parentId] == null) r.parentId = null;   // never point at a row that is not before us
      r.depth = d;
      depth[r.id] = d;
    });
  }

  /** The index after the last row of row i's subtree (members follow their group with a greater depth). */
  function subtreeEnd(i) {
    var d = cur.rows[i].depth;
    var j = i + 1;
    while (j < cur.rows.length && cur.rows[j].depth > d) j++;
    return j;
  }

  function newRow(kind, parent) {
    return {
      id: nextNewId--, parentId: parent ? parent.id : null, depth: parent ? parent.depth + 1 : 0, kind: kind,
      chance: 10, itemIndex: null, itemName: '', quest: false, gold: kind === 'gold' ? 0 : null,
      groupMode: '', dragonLevel: cur.dragon && kind !== 'group' ? 1 : null, includeListId: null, text: '', warning: ''
    };
  }

  function insertTopLevel(kind) {
    if (!cur) return;
    var row = newRow(kind, null);
    var sel = selectedRows();
    var at = cur.rows.length;
    if (sel.length) {
      var i = rowIndexOf(sel[sel.length - 1].id);
      while (i > 0 && cur.rows[i].parentId != null) i = rowIndexOf(cur.rows[i].parentId);   // its top-level ancestor
      if (i >= 0) at = subtreeEnd(i);
    }
    cur.rows.splice(at, 0, row);
    afterStructure(row, kind === 'gold' ? 'gold' : kind === 'group' ? 'chance' : 'item');
  }

  async function addToGroup() {
    var sel = selectedRows();
    var group = sel.length === 1 && sel[0].kind === 'group' ? sel[0] : null;
    if (!group) { toast('Select one group row first.', 'info'); return; }
    var v = await global.portalDialog({
      title: 'Add to group',
      fields: [{ name: 'kind', label: 'Row', type: 'select', value: 'item', options: [{ value: 'item', label: 'Item' }, { value: 'gold', label: 'Gold' }, { value: 'group', label: 'Group (nested)' }] }],
      confirmLabel: 'Add'
    });
    if (!v) return;
    var i = rowIndexOf(group.id);
    if (i < 0) return;
    var row = newRow(v.kind, group);
    cur.rows.splice(subtreeEnd(i), 0, row);
    afterStructure(row, v.kind === 'gold' ? 'gold' : v.kind === 'group' ? 'chance' : 'item');
  }

  async function deleteSelected() {
    var sel = selectedRows();
    if (!sel.length) return;
    var gone = Object.create(null);
    var n = 0, members = 0;
    sel.forEach(function (r) {
      var i = rowIndexOf(r.id);
      if (i < 0 || gone[r.id]) return;
      var end = subtreeEnd(i);
      for (var k = i; k < end; k++) {
        if (!gone[cur.rows[k].id]) { gone[cur.rows[k].id] = true; n++; if (k > i) members++; }
      }
    });
    if (members) {
      var ok = await global.portalConfirm('Delete ' + n + ' row(s)? ' + members + ' of them are members of a selected group and go with it. ' +
        'Nothing is saved until you press Save.', { title: 'Delete drop rows', confirmLabel: 'Delete', danger: true });
      if (!ok) return;
    }
    cur.rows = cur.rows.filter(function (r) { return !gone[r.id]; });
    afterStructure(null);
  }

  function hiddenRow(r) { return !showNotes && r.kind === 'note'; }

  /** Move the selected row (with its members) past the previous / next visible sibling under the same parent. */
  function move(dir) {
    var sel = selectedRows();
    if (sel.length !== 1) { toast('Select one row to move.', 'info'); return; }
    var row = sel[0];
    var i = rowIndexOf(row.id);
    if (i < 0) return;
    var end = subtreeEnd(i);
    var block = cur.rows.slice(i, end);
    var sib = -1;
    if (dir < 0) {
      for (var k = i - 1; k >= 0; k--) {
        var r = cur.rows[k];
        if (r.depth < row.depth) break;
        if (r.parentId === row.parentId && !hiddenRow(r)) { sib = k; break; }
      }
      if (sib < 0) { toast('Already the first row of its ' + (row.parentId == null ? 'list' : 'group') + '.', 'info'); return; }
      cur.rows.splice(i, block.length);
      cur.rows.splice.apply(cur.rows, [sib, 0].concat(block));
    } else {
      var k2 = end;
      while (k2 < cur.rows.length) {
        var r2 = cur.rows[k2];
        if (r2.depth < row.depth) break;
        if (r2.parentId === row.parentId && !hiddenRow(r2)) { sib = k2; break; }
        k2++;
      }
      if (sib < 0) { toast('Already the last row of its ' + (row.parentId == null ? 'list' : 'group') + '.', 'info'); return; }
      var after = subtreeEnd(sib);
      var rest = cur.rows.slice(end, after);
      cur.rows.splice.apply(cur.rows, [i, after - i].concat(rest).concat(block));
    }
    afterStructure(row);
  }

  function afterStructure(focusRow, editCol) {
    fixDepths();
    setDirty(true);
    renderHead();
    syncGrid(false);
    if (focusRow && api) {
      setTimeout(function () {
        var node = api.getRowNode(String(focusRow.id));
        if (!node) return;
        api.deselectAll();
        node.setSelected(true);
        api.ensureNodeVisible(node, 'middle');
        if (editCol && node.rowIndex != null) api.startEditingCell({ rowIndex: node.rowIndex, colKey: editCol });
        syncTools();
      }, 0);
    } else {
      syncTools();
    }
  }

  // ---- grid -----------------------------------------------------------------------------------------------------------

  function editable(kind, col) {
    switch (col) {
      case 'chance': return !!TYPED[kind];
      case 'item': return kind === 'item' || kind === 'disabled';
      case 'gold': return kind === 'gold';
      case 'groupMode': return kind === 'group';
      case 'dragonLevel': return !!(cur && cur.dragon) && (kind === 'item' || kind === 'gold');
      default: return false;
    }
  }

  function positiveInt(v, min, max) {
    var s = String(v == null ? '' : v).trim();
    if (!/^\d+$/.test(s)) return null;
    var n = Number(s);
    if (n < min || (max != null && n > max)) return null;
    return n;
  }

  function changed(p) {
    setDirty(true);
    clearIssue(p.data.id);
    // Redraw, not refresh: the row class follows the kind (a fixed disabled row) and the cleared issue.
    if (p.api && p.node) p.api.redrawRows({ rowNodes: [p.node] });
  }

  function itemLabel(r) {
    if (r.itemIndex != null && itemLookup && itemLookup.byIndex[r.itemIndex]) return itemLookup.byIndex[r.itemIndex].label;
    return r.itemName || '';
  }

  function questRenderer(p) {
    if (!p.data || p.data.kind !== 'item') return '';
    var cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!p.data.quest;
    cb.title = 'Quest drop: only drops for a player on a quest that needs it';
    cb.addEventListener('change', function () {
      p.data.quest = cb.checked;
      setDirty(true);
      clearIssue(p.data.id);
    });
    return cb;
  }

  function columnDefs() {
    var lookupEditor = global.DbLookups ? global.DbLookups.SearchableLookupCellEditor : 'agTextCellEditor';
    return [
      {
        colId: 'sel', headerName: '', width: 42, pinned: 'left', checkboxSelection: true, headerCheckboxSelection: true,
        headerCheckboxSelectionFilteredOnly: true, editable: false, resizable: false, suppressHeaderMenuButton: true
      },
      {
        colId: 'kind', headerName: 'Kind', width: 130, editable: false,
        valueGetter: function (p) { return p.data ? (ROW_LABEL[p.data.kind] || p.data.kind) : ''; },
        valueFormatter: function (p) { return p.data && p.data.depth > 0 ? '↳ ' + p.value : p.value; },
        cellStyle: function (p) { return { paddingLeft: (10 + 18 * (p.data ? p.data.depth : 0)) + 'px' }; }
      },
      {
        colId: 'chance', headerName: 'Chance', width: 100,
        headerTooltip: 'Drop chance 1/N: edit N (a whole number, 1 or more). The game ignores any other numerator.',
        editable: function (p) { return editable(p.data.kind, 'chance'); },
        valueGetter: function (p) { return p.data && TYPED[p.data.kind] && p.data.chance != null ? '1/' + p.data.chance : ''; },
        valueSetter: function (p) {
          var m = /^\s*(?:(\d+)\s*\/\s*)?(\d+)\s*$/.exec(String(p.newValue == null ? '' : p.newValue));
          if (!m || (m[1] != null && m[1] !== '1')) { toast('Chance is 1/N: type N (a whole number, 1 or more) or 1/N.', 'error'); return false; }
          var n = positiveInt(m[2], 1, 2147483647);
          if (n == null) { toast('Chance must be 1/N with N a whole number of at least 1.', 'error'); return false; }
          if (n === p.data.chance) return false;
          p.data.chance = n;
          return true;
        }
      },
      {
        colId: 'item', headerName: 'Item', width: 220,
        editable: function (p) { return editable(p.data.kind, 'item'); },
        singleClickEdit: true,
        cellEditor: lookupEditor,
        cellEditorParams: function () { return { options: itemLookup ? itemLookup.options : [] }; },
        valueGetter: function (p) { return p.data && (p.data.kind === 'item' || p.data.kind === 'disabled') ? itemLabel(p.data) : ''; },
        valueSetter: function (p) {
          var label = p.newValue == null ? '' : String(p.newValue);
          var opt = itemLookup && label ? itemLookup.byLabel[label] : null;
          if (!opt) return false;
          if (p.data.kind === 'item' && p.data.itemIndex === opt.index) return false;
          p.data.itemIndex = opt.index;
          p.data.itemName = opt.label;
          if (p.data.kind === 'disabled') {
            // A disabled row (a line naming a missing item) becomes a real item row once an item is picked.
            p.data.kind = 'item';
            p.data.warning = '';
            if (p.data.chance == null) p.data.chance = 10;
          }
          return true;
        }
      },
      { colId: 'quest', headerName: 'Quest', width: 80, editable: false, cellRenderer: questRenderer },
      {
        colId: 'gold', headerName: 'Gold', width: 110,
        editable: function (p) { return editable(p.data.kind, 'gold'); },
        valueGetter: function (p) { return p.data && p.data.kind === 'gold' ? p.data.gold : ''; },
        valueSetter: function (p) {
          var n = positiveInt(p.newValue, 1, 2147483647);
          if (n == null) { toast('Gold must be a whole number of at least 1.', 'error'); return false; }
          if (n === p.data.gold) return false;
          p.data.gold = n;
          return true;
        }
      },
      {
        colId: 'groupMode', headerName: 'Group mode', width: 130,
        editable: function (p) { return editable(p.data.kind, 'groupMode'); },
        cellEditor: 'agSelectCellEditor',
        cellEditorParams: { values: ['', '*', '^'] },
        valueGetter: function (p) { return p.data && p.data.kind === 'group' ? (p.data.groupMode || '') : null; },
        valueFormatter: function (p) { return p.value == null ? '' : (MODE_LABEL[p.value] || p.value); },
        valueSetter: function (p) {
          var v = p.newValue == null ? '' : String(p.newValue);
          if (!(v in MODE_LABEL) || v === p.data.groupMode) return false;
          p.data.groupMode = v;
          return true;
        }
      },
      {
        colId: 'dragonLevel', headerName: 'Dragon level', width: 120, hide: true,
        headerTooltip: 'Dragon level 1..' + MAX_DRAGON_LEVEL + ' the row belongs to',
        editable: function (p) { return editable(p.data.kind, 'dragonLevel'); },
        valueGetter: function (p) { return p.data && TYPED[p.data.kind] ? p.data.dragonLevel : ''; },
        valueSetter: function (p) {
          var n = positiveInt(p.newValue, 1, MAX_DRAGON_LEVEL);
          if (n == null) { toast('Dragon level must be a whole number from 1 to ' + MAX_DRAGON_LEVEL + '.', 'error'); return false; }
          if (n === p.data.dragonLevel) return false;
          p.data.dragonLevel = n;
          return true;
        }
      },
      {
        colId: 'text', headerName: 'Text', flex: 1, minWidth: 180, editable: false,
        valueGetter: function (p) { return p.data && !TYPED[p.data.kind] ? p.data.text : ''; },
        tooltipValueGetter: function (p) { return p.value || ''; }
      },
      {
        colId: 'warning', headerName: 'Warning', width: 220, editable: false,
        valueGetter: function (p) {
          if (!p.data) return '';
          var bad = issueFor(p.data.id);
          return bad ? bad.message : (p.data.warning || '');
        },
        tooltipValueGetter: function (p) { return p.value || ''; },
        cellClass: function (p) { return p.value ? 'gd-drops-warn' : ''; }
      }
    ];
  }

  async function ensureGrid() {
    if (api) return true;
    if (!global.agGrid || typeof global.agGrid.createGrid !== 'function') {
      ui.gridHost.appendChild(el('p', 'mir-muted', 'The grid library did not load (check the connection to cdn.jsdelivr.net), so drops cannot be shown.'));
      return false;
    }
    if (!itemLookup && global.DbLookups) {
      try { itemLookup = await global.DbLookups.fetch('items'); } catch (e) { toast('Could not load the item list: ' + ((e && e.message) || e), 'error'); }
    }
    if (api) return true;
    api = global.agGrid.createGrid(ui.gridHost, {
      columnDefs: columnDefs(),
      defaultColDef: { sortable: false, filter: false, resizable: true, suppressHeaderMenuButton: true, wrapHeaderText: true, autoHeaderHeight: true },
      rowData: [],
      getRowId: function (p) { return String(p.data.id); },
      rowSelection: 'multiple',
      suppressRowClickSelection: false,
      stopEditingWhenCellsLoseFocus: true,
      tooltipShowDelay: 400,
      getRowClass: function (p) {
        if (!p.data) return '';
        var c = [];
        if (!TYPED[p.data.kind]) c.push('gd-drops-row-muted');
        if (p.data.kind === 'group') c.push('gd-drops-row-group');
        if (issueFor(p.data.id)) c.push('gd-drops-row-bad');
        return c.join(' ');
      },
      isExternalFilterPresent: function () { return !showNotes; },
      doesExternalFilterPass: function (node) { return !node.data || node.data.kind !== 'note'; },
      onCellValueChanged: changed,
      onSelectionChanged: syncTools,
      overlayNoRowsTemplate: '<span class="mir-muted">No rows. Use Add item / Add gold / Add group.</span>'
    });
    return true;
  }

  function syncGrid(fresh) {
    if (!api || !cur) return;
    // A fresh list replaces the grid's rows outright (no id-based delta update that could keep a stale pending row).
    if (fresh) api.setGridOption('rowData', []);
    api.setGridOption('rowData', cur.rows.slice());
    api.setColumnsVisible(['dragonLevel'], !!cur.dragon);
    api.onFilterChanged();
    api.redrawRows();
    if (fresh) api.deselectAll();
  }

  // ---- issues (validation errors from a save) -------------------------------------------------------------------------

  function issueFor(id) {
    for (var i = 0; i < issues.length; i++) if (issues[i].rowId === id) return issues[i];
    return null;
  }

  function clearIssue(id) {
    var before = issues.length;
    issues = issues.filter(function (x) { return x.rowId !== id; });
    if (issues.length !== before) renderIssues();
  }

  function renderIssues() {
    ui.issues.textContent = '';
    ui.issues.classList.toggle('hidden', !issues.length);
    if (!issues.length) return;
    ui.issues.appendChild(el('strong', 'gd-bad', 'Not saved: ' + issues.length + ' problem(s)'));
    var ul = el('ul', 'gd-issue-list');
    issues.slice(0, 100).forEach(function (x) {
      var li = el('li');
      var b = el('button', 'gd-drops-issue-link', (x.rowId != null ? 'Row ' + (x.pos + 1) : 'List') + (x.field ? ' · ' + x.field : ''));
      b.type = 'button';
      b.addEventListener('click', function () { focusRow(x.rowId); });
      li.appendChild(b);
      li.appendChild(document.createTextNode(': ' + (x.message || '')));
      ul.appendChild(li);
    });
    if (issues.length > 100) ul.appendChild(el('li', 'mir-muted', '… and ' + (issues.length - 100) + ' more'));
    ui.issues.appendChild(ul);
  }

  function focusRow(id) {
    if (!api || id == null) return;
    var node = api.getRowNode(String(id));
    if (!node) return;
    if (!node.displayed && !showNotes) { setShowNotes(true); node = api.getRowNode(String(id)); }
    api.deselectAll();
    node.setSelected(true);
    api.ensureNodeVisible(node, 'middle');
  }

  /** Map data.errors[{ row (array position), field, message }] of a 400 reply onto the rows that were sent. */
  function takeErrors(res, sent) {
    var errs = Array.isArray(res.data.errors) ? res.data.errors : [];
    issues = errs.map(function (e) {
      var pos = Number(e && e.row);
      var row = isFinite(pos) && sent && sent[pos] ? sent[pos] : null;
      return { rowId: row ? row.id : null, pos: isFinite(pos) ? pos : -1, field: e && e.field ? String(e.field) : '', message: e && e.message ? String(e.message) : '' };
    });
    renderIssues();
    if (api) { api.redrawRows(); api.refreshCells({ force: true }); }
    toast(res.message || 'The list was not saved: fix the problems listed above the grid.', 'error');
  }

  // ---- save / copy / delete disabled ----------------------------------------------------------------------------------

  function payloadRows() {
    return cur.rows.map(function (r) {
      return {
        id: r.id, parentId: r.parentId, kind: r.kind, chance: r.chance, itemIndex: r.itemIndex, quest: !!r.quest,
        gold: r.gold, groupMode: r.groupMode || '', dragonLevel: r.dragonLevel
      };
    });
  }

  function summaryNodes(body, d) {
    var s = d.summary;
    if (Array.isArray(s)) {
      var ul = el('ul', 'gd-change-list');
      s.slice(0, 200).forEach(function (x) { ul.appendChild(el('li', null, typeof x === 'object' ? JSON.stringify(x) : String(x))); });
      if (s.length > 200) ul.appendChild(el('li', 'mir-muted', '… and ' + (s.length - 200) + ' more'));
      body.appendChild(ul);
    } else if (s && typeof s === 'object') {
      var ul2 = el('ul', 'gd-change-list');
      Object.keys(s).forEach(function (k) { ul2.appendChild(el('li', null, k + ': ' + (typeof s[k] === 'object' ? JSON.stringify(s[k]) : String(s[k])))); });
      body.appendChild(ul2);
    } else if (s != null && s !== '') {
      body.appendChild(el('p', 'gd-drops-summary', String(s)));
    }
  }

  async function conflict(res) {
    var ok = await global.portalConfirm((res.message || 'This drop list was changed on the server since you loaded it.') +
      ' Reload it now? Your unsaved edits are lost.', { title: 'Drop list changed', confirmLabel: 'Reload', danger: true });
    if (ok) { setDirty(false); await openList(cur.id, false); }
  }

  /**
   * One write in two steps: the dry run (shown with the server's summary) and, after Confirm, the real call. The reply's rows
   * replace the grid. spec: { url, body (without dryRun / reason), title, confirmLabel, sent (rows, for error positions), danger }
   */
  async function twoStep(spec) {
    var dry = await post(spec.url, Object.assign({}, spec.body, { dryRun: true }));
    if (!dry) return false;
    if (dry.status === 409) { await conflict(dry); return false; }
    if (!dry.ok) {
      if (dry.status === 400 && Array.isArray(dry.data.errors) && dry.data.errors.length) takeErrors(dry, spec.sent);
      else toast(dry.message || ('Dry run failed (HTTP ' + dry.status + ').'), 'error');
      return false;
    }
    issues = [];
    renderIssues();
    if (api) api.redrawRows();
    var d = dry.data;
    if (Number(d.changed) === 0) {
      toast(dry.message || 'Nothing to change.', 'info');
      if (spec.clearOnNoChange) setDirty(false);
      return false;
    }
    var choice = await global.GameData.modal({
      title: spec.title,
      confirmLabel: spec.confirmLabel,
      danger: !!spec.danger,
      reason: true,
      build: function (body) {
        // The server's summary once (its message says the same when there is no summary), then one line of our own.
        if (d.summary != null && d.summary !== '') summaryNodes(body, d);
        else if (dry.message) body.appendChild(el('p', null, dry.message));
        else if (d.changed != null) body.appendChild(el('p', null, d.changed + ' row change(s).'));
        if (ui.shared && !ui.shared.classList.contains('hidden')) body.appendChild(el('p', 'gd-live-note', ui.shared.textContent));
        body.appendChild(el('p', 'mir-muted', 'Nothing has been changed yet. Confirm saves to SystemData.db at once and reloads the drops of the running world.'));
      }
    });
    if (!choice) return false;
    var real = await post(spec.url, Object.assign({}, spec.body, { dryRun: false, reason: choice.reason || '' }));
    if (!real) return false;
    if (real.status === 409) { await conflict(real); return false; }
    if (!real.ok) {
      if (real.status === 400 && Array.isArray(real.data.errors) && real.data.errors.length) takeErrors(real, spec.sent);
      else toast(real.message || ('Failed (HTTP ' + real.status + ').'), 'error');
      return false;
    }
    var r = real.data;
    // The server's message already says what was reloaded in the running world (data.reloaded): show it as is.
    toast(real.message || r.reloaded || 'Saved.', 'ok');
    // Re-render the whole list from the server (replaces every row, including the pending new ones, and resets the dirty
    // state); the reply's rows are the fallback when the reload fails. Then refresh the picker's counts.
    setDirty(false);
    issues = [];
    renderIssues();
    var id = cur.id;
    var reloaded = await openList(id, false);
    if (!reloaded && Array.isArray(r.rows) && cur && cur.id === id) {
      cur.rev = r.rev != null ? r.rev : cur.rev;
      setRows(r.rows);
      renderHead();
      syncGrid(true);
      syncTools();
    }
    await loadLists(true);
    markPicked();
    return true;
  }

  async function save() {
    if (!cur) return;
    if (api) api.stopEditing();
    var sent = payloadRows();
    await twoStep({
      url: API + '/save', body: { id: cur.id, rev: cur.rev, rows: sent }, sent: sent,
      title: 'Save drops: ' + cur.name, confirmLabel: 'Save', clearOnNoChange: true
    });
  }

  async function revert() {
    if (!cur) return;
    if (dirty && !(await confirmDiscard('Reload the list from the server and discard them?'))) return;
    setDirty(false);
    await openList(cur.id, false);
  }

  async function copyFrom() {
    if (!cur) return;
    var monsters = null;
    try { monsters = global.DbLookups ? await global.DbLookups.fetch('monsters') : null; } catch (e) { monsters = null; }
    var v = await global.portalDialog({
      title: 'Copy drops into "' + cur.name + '"',
      message: 'Replaces every row of this list with a copy of another list\'s rows. Pick a monster (its list is used) or a list by name. ' +
        'The other list is not changed.',
      fields: [
        { name: 'monster', label: 'From monster', type: 'text', value: '', suggestions: monsters ? monsters.labels.filter(Boolean) : [] },
        { name: 'list', label: 'or from list', type: 'text', value: '', suggestions: lists.map(function (l) { return l.name; }).filter(Boolean) }
      ],
      confirmLabel: 'Preview',
      validate: function (x) {
        var a = String(x.monster || '').trim(), b = String(x.list || '').trim();
        if (!a && !b) return 'Pick a monster or a list.';
        if (a && b) return 'Pick a monster or a list, not both.';
        return null;
      }
    });
    if (!v) return;
    var fromId = null, fromLabel = '';
    var mName = String(v.monster || '').trim();
    if (mName) {
      var opt = monsters && monsters.byLabel[mName];
      if (!opt) { toast('No monster called "' + mName + '".', 'error'); return; }
      var md = await getData(API + '/monster?index=' + encodeURIComponent(opt.index));
      if (!md) return;
      if (md.listId == null) { toast('"' + mName + '" has no drop list to copy.', 'info'); return; }
      fromId = Number(md.listId);
      fromLabel = (md.listName || mName);
    } else {
      var lName = String(v.list || '').trim().toLowerCase();
      var found = lists.filter(function (l) { return String(l.name || '').toLowerCase() === lName; })[0];
      if (!found) { toast('No drop list called "' + v.list + '".', 'error'); return; }
      fromId = Number(found.id);
      fromLabel = found.name;
    }
    if (fromId === cur.id) { toast('That is this list.', 'info'); return; }
    if (!(await confirmDiscard('Copying replaces the list on the server: discard your unsaved edits?'))) return;
    await twoStep({
      url: API + '/copy', body: { fromId: fromId, toId: cur.id, rev: cur.rev },
      title: 'Copy "' + fromLabel + '" into "' + cur.name + '"', confirmLabel: 'Replace rows', danger: true
    });
  }

  async function deleteDisabled() {
    if (!cur) return;
    if (!(await confirmDiscard('This action works on the saved list: discard your unsaved edits first?'))) return;
    await twoStep({
      url: API + '/delete-disabled', body: { id: cur.id, rev: cur.rev },
      title: 'Delete the disabled rows of "' + cur.name + '"', confirmLabel: 'Delete disabled rows', danger: true
    });
  }

  /** Remove / re-add every Assassin or Archer drop in ALL lists (the server's class toggle), then reload this list. */
  async function classDrops() {
    if (!(await confirmDiscard('The class tool changes the saved lists: discard your unsaved edits first?'))) return;
    var v = await global.portalDialog({
      title: 'Class drops',
      message: 'Removes or re-adds the drops of every item that requires the class, in ALL drop lists (like the Remove / Re-add ' +
        'Assassin and Archer drops buttons of the server Config window). A preview comes first.',
      fields: [
        { name: 'cls', label: 'Class', type: 'select', value: 'Assassin', options: [{ value: 'Assassin', label: 'Assassin' }, { value: 'Archer', label: 'Archer' }] },
        { name: 'mode', label: 'Action', type: 'select', value: 'remove', options: [{ value: 'remove', label: 'Remove their drops' }, { value: 'add', label: 'Re-add their drops' }] }
      ],
      confirmLabel: 'Preview'
    });
    if (!v) return;
    var enable = v.mode === 'add';
    var url = '/api/admin/scripts/drops/class-toggle';
    var dry = await post(url, { 'class': v.cls, enable: enable, dryRun: true });
    if (!dry) return;
    if (!dry.ok) { toast(dry.message || ('Preview failed (HTTP ' + dry.status + ').'), 'error'); return; }
    var d = dry.data;
    var count = firstNumber(d, ['linesChanged', 'rowsChanged', 'changed']);
    if (count === 0) { toast(dry.message || ('Nothing to change: no ' + v.cls + ' drops found.'), 'info'); return; }
    var choice = await global.GameData.modal({
      title: (enable ? 'Re-add ' : 'Remove ') + v.cls + ' drops',
      confirmLabel: 'Apply to all lists',
      danger: !enable,
      reason: true,
      build: function (body) {
        if (dry.message) body.appendChild(el('p', null, dry.message));
        countLines(body, d);
        var where = Array.isArray(d.files) ? d.files : (Array.isArray(d.lists) ? d.lists : []);
        if (where.length) {
          var ul = el('ul', 'gd-file-list');
          where.slice(0, 40).forEach(function (f) {
            var n = Array.isArray(f.lines) ? f.lines.length : (Array.isArray(f.rows) ? f.rows.length : f.count);
            ul.appendChild(el('li', null, String(f.path || f.name || f.list || f.id || '') + (n != null ? ' (' + n + ')' : '')));
          });
          if (where.length > 40) ul.appendChild(el('li', 'mir-muted', '… and ' + (where.length - 40) + ' more'));
          body.appendChild(ul);
        }
      }
    });
    if (!choice) return;
    var res = await post(url, { 'class': v.cls, enable: enable, dryRun: false, reason: choice.reason || '' });
    if (!res) return;
    if (!res.ok) { toast(res.message || ('Failed (HTTP ' + res.status + ').'), 'error'); return; }
    var failed = Array.isArray(res.data.failed) ? res.data.failed.length : 0;
    toast((res.message || 'Done.') + (failed ? ' ' + failed + ' list(s) could not be written.' : ''), failed ? 'warn' : 'ok');
    await loadLists(true);
    if (cur) { setDirty(false); await openList(cur.id, false); }
  }

  function firstNumber(d, keys) {
    for (var i = 0; i < keys.length; i++) if (typeof d[keys[i]] === 'number') return d[keys[i]];
    return null;
  }

  function countLines(body, d) {
    var parts = [];
    [['linesChanged', 'row(s)'], ['rowsChanged', 'row(s)'], ['filesChanged', 'list(s)'], ['listsChanged', 'list(s)']].forEach(function (p) {
      if (typeof d[p[0]] === 'number') parts.push(d[p[0]] + ' ' + p[1]);
    });
    if (parts.length) body.appendChild(el('p', null, 'Changes: ' + parts.join(', ') + '.'));
  }

  // ---- notes toggle ---------------------------------------------------------------------------------------------------

  function setShowNotes(on) {
    showNotes = !!on;
    if (ui.notes) ui.notes.checked = showNotes;
    lsSet(LS_NOTES, showNotes ? '1' : '0');
    if (api) api.onFilterChanged();
  }

  // ---- build ----------------------------------------------------------------------------------------------------------

  function mount(pane) {
    if (mounted) return;
    mounted = true;
    showNotes = lsGet(LS_NOTES) === '1';
    var wrap = el('div', 'gd-drops');

    var side = el('aside', 'gd-drops-side mir-card');
    ui.search = el('input', 'fd-input gd-drops-search');
    ui.search.type = 'search';
    ui.search.placeholder = 'Search lists or monsters…';
    ui.search.setAttribute('aria-label', 'Search drop lists by name or by monster');
    ui.search.addEventListener('input', renderPicker);
    side.appendChild(ui.search);
    var row = el('div', 'gd-drops-side-row');
    ui.kind = el('select', 'fd-input gd-drops-kind');
    ui.kind.setAttribute('aria-label', 'Kind of drop list');
    ui.kind.addEventListener('change', renderPicker);
    row.appendChild(ui.kind);
    var newBtn = button('New list…', '', 'Create a named drop list (DropPath, NPC DROP or #INSERT target)');
    newBtn.addEventListener('click', createNamed);
    row.appendChild(newBtn);
    side.appendChild(row);
    ui.pickCount = el('div', 'gd-drops-count mir-muted', 'Loading…');
    side.appendChild(ui.pickCount);
    ui.pickList = el('div', 'gd-drops-picklist');
    side.appendChild(ui.pickList);
    wrap.appendChild(side);

    var main = el('section', 'gd-drops-main');
    ui.empty = el('div', 'gd-drops-empty mir-card hidden');
    ui.emptyText = el('p', 'gd-drops-empty-text');
    ui.empty.appendChild(ui.emptyText);
    var create = button('Create list', 'primary');
    create.addEventListener('click', function () { global.OpsUI.withBusy(create, createForMonster, 'Creating…'); });
    ui.empty.appendChild(create);
    main.appendChild(ui.empty);

    ui.body = el('div', 'gd-drops-body hidden');
    var head = el('div', 'gd-drops-head');
    ui.title = el('h3', 'db-pane-title gd-drops-title', '');
    head.appendChild(ui.title);
    ui.kindPill = el('span', 'ops-pill', '');
    head.appendChild(ui.kindPill);
    ui.dirtyPill = el('span', 'ops-pill ops-pill-warn hidden', 'unsaved');
    head.appendChild(ui.dirtyPill);
    ui.meta = el('span', 'mir-muted gd-drops-meta', '');
    head.appendChild(ui.meta);
    ui.body.appendChild(head);
    ui.users = el('div', 'gd-drops-users mir-muted', '');
    ui.body.appendChild(ui.users);
    ui.shared = el('div', 'gd-drops-shared hidden', '');
    ui.body.appendChild(ui.shared);

    var tools = el('div', 'gd-drops-tools');
    function tool(key, label, title, fn, cls) {
      var b = button(label, cls, title);
      // withBusy re-enables the button when done: syncTools() afterwards puts back the selection-based state.
      b.addEventListener('click', function () { Promise.resolve(global.OpsUI.withBusy(b, fn)).then(syncTools, syncTools); });
      ui[key] = b;
      tools.appendChild(b);
      return b;
    }
    tool('addItem', 'Add item', 'Add an item row at the top level, after the selected row (or at the end)', function () { insertTopLevel('item'); });
    tool('addGold', 'Add gold', 'Add a gold row at the top level, after the selected row (or at the end)', function () { insertTopLevel('gold'); });
    tool('addGroup', 'Add group', 'Add a group at the top level, after the selected row (or at the end)', function () { insertTopLevel('group'); });
    tool('addToGroup', 'Add to group', 'Add a row as the last member of the selected group', addToGroup);
    tool('del', 'Delete selected', 'Delete the selected rows (a group takes its members with it)', deleteSelected);
    tool('up', 'Move up', 'Move the selected row (and its members) up within its group', function () { move(-1); });
    tool('down', 'Move down', 'Move the selected row (and its members) down within its group', function () { move(1); });
    tools.appendChild(el('span', 'gd-drops-sep'));
    var notesLab = el('label', 'gd-drops-notes');
    ui.notes = el('input');
    ui.notes.type = 'checkbox';
    ui.notes.checked = showNotes;
    ui.notes.addEventListener('change', function () { setShowNotes(ui.notes.checked); });
    notesLab.appendChild(ui.notes);
    notesLab.appendChild(document.createTextNode(' Show notes'));
    notesLab.title = 'Comment lines of the old drop files. Hidden or not, they stay in place when you save.';
    tools.appendChild(notesLab);
    var end = el('span', 'gd-drops-tools-end');
    tools.appendChild(end);
    function endTool(key, label, title, fn, cls) {
      var b = tool(key, label, title, fn, cls);
      end.appendChild(b);
    }
    endTool('copy', 'Copy drops from…', 'Replace this list with a copy of another monster\'s or list\'s drops (preview first)', copyFrom);
    endTool('delDisabled', 'Delete all disabled', 'Delete the disabled rows (lines naming items that do not exist) of this list (preview first)', deleteDisabled);
    endTool('classTool', 'Class drops…', 'Remove or re-add Assassin / Archer drops in every drop list (preview first)', classDrops);
    endTool('revert', 'Revert', 'Reload this list from the server, dropping unsaved edits', revert);
    endTool('save', 'Save', 'Check the list on the server (dry run), then save it', save, 'primary');
    ui.body.appendChild(tools);

    ui.issues = el('div', 'gd-drops-issues hidden');
    ui.issues.setAttribute('role', 'alert');
    ui.body.appendChild(ui.issues);
    ui.gridHost = el('div', 'ag-theme-quartz-dark grid-panel gd-drops-grid');
    ui.body.appendChild(ui.gridHost);
    main.appendChild(ui.body);

    ui.hint = el('p', 'gd-drops-hint mir-muted', 'Pick a drop list on the left, or open a monster\'s drops from the Monsters tab (Drops button).');
    main.appendChild(ui.hint);
    wrap.appendChild(main);
    pane.appendChild(wrap);
    syncTools();
  }

  async function show() {
    if (!mounted) return;
    if (!listsLoaded) await loadLists(false);
    await ensureGrid();
    ui.hint.classList.toggle('hidden', !!(cur || emptyMonster));
  }

  async function openListPublic(id) {
    await show();
    var ok = await openList(id, false);
    ui.hint.classList.toggle('hidden', !!(cur || emptyMonster));
    return ok;
  }

  async function openMonsterPublic(index) {
    await show();
    var ok = await openMonster(index);
    ui.hint.classList.toggle('hidden', !!(cur || emptyMonster));
    return ok;
  }

  global.DropsEditor = {
    mount: mount,
    show: show,
    openList: openListPublic,
    openMonster: openMonsterPublic,
    isDirty: function () { return dirty; },
    pendingCount: function () { return dirty ? 1 : 0; },
    /** Resolves true when leaving is fine (no edits, or the admin discards them). */
    confirmLeave: function () { return confirmDiscard('Discard them and leave the Drops tab?'); },
    current: function () { return cur ? { id: cur.id, name: cur.name, kind: cur.kind } : null; }
  };
})(window);
