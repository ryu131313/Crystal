/**
 * Maps editor — edit mode (MapEditorEdit), plugged into admin-mapeditor.js through its hooks.
 *
 * Crystal's map objects are a point plus a square half-size, so editing is direct manipulation:
 *   - drag an object to move it (respawns, safe zones, mine zones, NPCs; a movement's source), drag a selected object's handles
 *     to change its Size / Spread, drag a same-map movement's destination ring to move the destination;
 *   - Add tools place a new safe zone, respawn (monster picked first), movement or mine zone with a click;
 *   - Delete removes the selected object (NPCs: use the NPCs grid); the details column edits the selected object's fields.
 * Nothing is sent until "Review & apply": every change on the map becomes ONE LiveDb batch over the map's tables
 * (MirLiveDb.runBatch: dry run, preview with live / restart badges, apply and save). Edit mode needs [LiveDb] Enabled.
 *
 * State: edits (existing rows: changed fields + the row's __rev), added (new rows with temporary negative indexes) and deleted
 * (existing rows). The objects on the map are rebuilt from the overlay with that state applied (MapEditorGeo.objectsFromOverlay
 * on a patched copy), so everything that reads objects (list, details, checks) sees the pending state. Undo / redo keep up to
 * 100 snapshots. Unapplied edits set the DbSave key "mapeditor:edits", so the shell's leave guards cover them.
 */
(function (global) {
  'use strict';

  var Geo = global.MapEditorGeo;

  var KINDS = {
    safezone: { list: 'safeZones', endpoint: 'mapsafezones', pos: ['Location_X', 'Location_Y'], size: 'Size', label: 'safe zone',
      fields: [['Location_X', 'X', 'int'], ['Location_Y', 'Y', 'int'], ['Size', 'Size', 'int'], ['StartPoint', 'Start point', 'bool']],
      create: function () { return { Size: 10, StartPoint: false }; } },
    respawn: { list: 'respawns', endpoint: 'maprespawns', pos: ['Location_X', 'Location_Y'], size: 'Spread', label: 'respawn',
      fields: [['MonsterIndex', 'Monster', 'monster'], ['Location_X', 'X', 'int'], ['Location_Y', 'Y', 'int'], ['Count', 'Count', 'int'],
        ['Spread', 'Spread', 'int'], ['Delay', 'Delay (min)', 'int'], ['RandomDelay', 'Random delay', 'int'], ['Direction', 'Direction (0-7)', 'int']],
      create: function () { return { Count: 1, Spread: 3, Delay: 10, RandomDelay: 0, Direction: 0 }; } },
    mine: { list: 'mineZones', endpoint: 'mapminezones', pos: ['Location_X', 'Location_Y'], size: 'Size', label: 'mine zone',
      fields: [['Mine', 'Mine set', 'int'], ['Location_X', 'X', 'int'], ['Location_Y', 'Y', 'int'], ['Size', 'Size', 'int']],
      create: function () { return { Mine: 1, Size: 10 }; } },
    movement: { list: 'movements', endpoint: 'mapmovements', pos: ['Source_X', 'Source_Y'], size: null, label: 'movement',
      fields: [['Source_X', 'Source X', 'int'], ['Source_Y', 'Source Y', 'int'], ['DestinationMapIndex', 'Destination map', 'map'],
        ['Destination_X', 'Destination X', 'int'], ['Destination_Y', 'Destination Y', 'int'], ['NeedHole', 'Needs a hole', 'bool'],
        ['NeedMove', 'Needs a script move', 'bool'], ['ShowOnBigMap', 'Show on big map', 'bool'], ['Icon', 'Big map icon', 'int']],
      create: function () { return { NeedHole: false, NeedMove: false, ShowOnBigMap: false, Icon: 0 }; } },
    npc: { list: 'npcs', endpoint: 'npcs', pos: ['Location_X', 'Location_Y'], size: null, label: 'NPC', noAdd: true, noDelete: true,
      fields: [['Location_X', 'X', 'int'], ['Location_Y', 'Y', 'int']] }
  };
  var ADD_ORDER = ['safezone', 'respawn', 'movement', 'mine'];
  var MAX_UNDO = 100;
  var HANDLE = 6;   // handle half-size, CSS px

  function num(v, d) { var n = Number(v); return isFinite(n) ? n : (d || 0); }
  function same(a, b) { return String(a == null ? '' : a) === String(b == null ? '' : b); }
  function clone(o) { return JSON.parse(JSON.stringify(o)); }
  function baseKey(key) { return String(key || '').split('~')[0]; }

  function attach(page) {
    var hooks = page.hooks;
    var el = page.el, button = page.button, toast = page.toast;

    var st = {
      on: false, tool: null,
      edits: Object.create(null),     // key -> { kind, index, rev, fields: { f: v } }
      added: [],                      // [{ kind, index (negative), row }]
      deleted: Object.create(null),   // key -> { kind, index, rev }
      undo: [], redo: [], seq: 0,
      drag: null, baseObjs: Object.create(null), monsters: null, maps: null
    };

    // ---- state helpers ----------------------------------------------------------------------------------------------------

    function liveDbOn() { return !!(global.MirLiveDb && MirLiveDb.enabled && MirLiveDb.enabled()); }
    function cur() { return page.current(); }
    function view() { return page.view(); }

    function pendingCount() {
      var n = st.added.length + Object.keys(st.deleted).length;
      Object.keys(st.edits).forEach(function (k) { if (Object.keys(st.edits[k].fields).length) n++; });
      return n;
    }

    function snapshot() { return JSON.stringify({ edits: st.edits, added: st.added, deleted: st.deleted, seq: st.seq }); }
    function restore(s) {
      var o = JSON.parse(s);
      st.edits = Object.assign(Object.create(null), o.edits);
      st.added = o.added;
      st.deleted = Object.assign(Object.create(null), o.deleted);
      st.seq = o.seq;
    }
    function pushUndo(before) {
      st.undo.push(before);
      if (st.undo.length > MAX_UNDO) st.undo.shift();
      st.redo = [];
    }
    function undo() {
      if (!st.undo.length) return;
      st.redo.push(snapshot());
      restore(st.undo.pop());
      refresh();
    }
    function redo() {
      if (!st.redo.length) return;
      st.undo.push(snapshot());
      restore(st.redo.pop());
      refresh();
    }
    function clearAll() {
      st.edits = Object.create(null);
      st.added = [];
      st.deleted = Object.create(null);
      st.undo = []; st.redo = [];
      st.drag = null;
    }

    /** The object's current row value (pending state applied). */
    function rowOf(o) { return o ? o.row : null; }

    function isNew(o) { return !!o && num(o.index) < 0; }

    /** Change fields of an object (no undo push: callers push once per gesture). */
    function setFields(o, fields) {
      if (!o) return;
      var kind = o.kind;
      if (isNew(o)) {
        var a = st.added.filter(function (x) { return x.kind === kind && x.index === o.index; })[0];
        if (a) Object.keys(fields).forEach(function (f) { a.row[f] = fields[f]; });
        return;
      }
      var key = baseKey(o.key);
      var orig = st.baseObjs[key] ? st.baseObjs[key].row : o.row;
      var e = st.edits[key] || (st.edits[key] = { kind: kind, index: o.index, rev: orig.__rev, fields: {} });
      Object.keys(fields).forEach(function (f) {
        if (same(fields[f], orig[f])) delete e.fields[f];
        else e.fields[f] = fields[f];
      });
      if (!Object.keys(e.fields).length) delete st.edits[key];
    }

    // ---- objects with the pending state applied ----------------------------------------------------------------------------

    function patchedOverlay(ov) {
      var out = Object.assign({}, ov);
      Object.keys(KINDS).forEach(function (kind) {
        var k = KINDS[kind];
        var rows = (ov[k.list] || []);
        var counts = Object.create(null);
        rows.forEach(function (r) { counts[r.Index] = (counts[r.Index] || 0) + 1; });
        var list = [];
        rows.forEach(function (r) {
          var key = kind + ':' + r.Index;
          if (counts[r.Index] > 1) { list.push(r); return; }   // duplicate keys are never edited
          if (st.deleted[key]) return;
          var e = st.edits[key];
          list.push(e ? Object.assign({}, r, e.fields, decorate(kind, e.fields, r)) : r);
        });
        st.added.forEach(function (a) {
          if (a.kind !== kind) return;
          list.push(Object.assign({ Index: a.index, MapIndex: ov.index }, a.row, decorate(kind, a.row, a.row)));
        });
        out[k.list] = list;
      });
      return out;
    }

    // Display names for changed monster / destination fields.
    function decorate(kind, fields, row) {
      var d = {};
      if (kind === 'respawn' && fields.MonsterIndex != null && st.monsters) {
        var m = st.monsters.byIndex[String(fields.MonsterIndex)] || st.monsters.byIndex[Number(fields.MonsterIndex)];
        d.__monster = m ? m.label : ('#' + fields.MonsterIndex);
      }
      if (kind === 'movement' && fields.DestinationMapIndex != null) {
        var c = cur();
        if (c && num(fields.DestinationMapIndex) === c.index) d.__destTitle = '';
        else if (st.maps) {
          var mp = st.maps.byIndex[String(fields.DestinationMapIndex)] || st.maps.byIndex[Number(fields.DestinationMapIndex)];
          d.__destTitle = mp ? mp.label : ('map #' + fields.DestinationMapIndex);
        }
      }
      return d;
    }

    function rebuild() {
      var c = cur();
      if (!c || !c.overlay) return;
      c.objects = Geo.objectsFromOverlay(patchedOverlay(c.overlay));
      var v = view();
      v.setObjects(c.objects);
      var pending = [];
      c.objects.forEach(function (o) {
        if (isNew(o)) { pending.push({ obj: o, isNew: true }); return; }
        var was = st.baseObjs[baseKey(o.key)];
        if (was && st.edits[baseKey(o.key)] && (was.x !== o.x || was.y !== o.y || was.r !== o.r)) pending.push({ obj: o, was: was });
      });
      Object.keys(st.deleted).forEach(function (k) { if (st.baseObjs[k]) pending.push({ was: st.baseObjs[k], deleted: true }); });
      v.setPending(pending);
    }

    function refresh() {
      rebuild();
      // An undone / discarded new object was selected: drop the selection (and its deep link).
      var h = global.portalHashParams ? global.portalHashParams() : {};
      if (h.sel && !page.selected()) page.setSelected(null);
      if (global.DbSave && DbSave.setDirty) DbSave.setDirty(page.EDITS_KEY, pendingCount() > 0);
      page.renderObjects();
      page.renderDetails();
      renderPending();
      renderTools();
    }

    // ---- toolbar ------------------------------------------------------------------------------------------------------------

    var ui = {};
    function buildTools() {
      var host = page.ui().edit;
      host.innerHTML = '';
      ui.toggle = button('Edit', 'me-edit-toggle', 'Move, resize, add and delete objects on the map (needs LiveDb)');
      ui.toggle.setAttribute('aria-pressed', 'false');
      ui.toggle.addEventListener('click', function () { setOn(!st.on); });
      host.appendChild(ui.toggle);
      ui.group = el('span', 'me-edit-group hidden');
      ui.adds = {};
      ADD_ORDER.forEach(function (kind) {
        var b = button('+ ' + Geo.KIND[kind].label, 'me-add-btn', 'Click on the map to place a new ' + KINDS[kind].label + ' (Esc cancels)');
        b.addEventListener('click', function () { setTool(st.tool === kind ? null : kind); });
        ui.adds[kind] = b;
        ui.group.appendChild(b);
      });
      ui.del = button('Delete', 'me-del-btn', 'Delete the selected object (Del)');
      ui.del.addEventListener('click', deleteSelected);
      ui.undo = button('↶', 'me-icon-btn', 'Undo (Ctrl+Z)');
      ui.undo.addEventListener('click', undo);
      ui.redo = button('↷', 'me-icon-btn', 'Redo (Ctrl+Y)');
      ui.redo.addEventListener('click', redo);
      [ui.del, ui.undo, ui.redo].forEach(function (b) { ui.group.appendChild(b); });
      ui.group.appendChild(el('span', 'me-edit-hint mir-muted', 'Drag to move · handles resize · Del deletes · Ctrl+Z undo · ? for all keys'));
      // Its own row under the toolbar (the page's editRow), so the toolbar does not wrap in edit mode.
      var row = page.ui().editRow;
      (row || host).appendChild(ui.group);
      renderTools();
    }

    function renderTools() {
      if (!ui.toggle) return;
      var live = liveDbOn();
      ui.toggle.disabled = !live && !st.on;
      ui.toggle.title = live ? 'Move, resize, add and delete objects on the map' :
        'Map editing needs LiveDb ([LiveDb] Enabled=True in Setup.ini). The grids below still work.';
      ui.toggle.classList.toggle('primary', st.on);
      ui.toggle.setAttribute('aria-pressed', st.on ? 'true' : 'false');
      ui.toggle.textContent = st.on ? 'Editing' : 'Edit';
      ui.group.classList.toggle('hidden', !st.on);
      if (page.ui().editRow) page.ui().editRow.classList.toggle('hidden', !st.on);
      ADD_ORDER.forEach(function (k) { ui.adds[k].classList.toggle('primary', st.tool === k); });
      var sel = page.selected();
      ui.del.disabled = !sel || !canDelete(sel);
      ui.undo.disabled = !st.undo.length;
      ui.redo.disabled = !st.redo.length;
      var root = page.ui().canvasHost;
      if (root) root.classList.toggle('me-editing', st.on);
    }

    async function setOn(on) {
      if (on === st.on) return;
      if (on && !liveDbOn()) { toast('Map editing needs LiveDb ([LiveDb] Enabled=True).', 'error'); return; }
      if (!on && pendingCount()) {
        var ok = await global.portalConfirm('Leave edit mode and discard ' + pendingCount() + ' pending change(s)?', { title: 'Discard map edits', confirmLabel: 'Discard' });
        if (!ok) return;
        clearAll();
      }
      st.on = on;
      st.tool = null;
      if (on) loadLookups();
      refresh();
    }

    function setTool(kind) {
      st.tool = kind;
      renderTools();
      if (kind) toast('Click on the map to place the new ' + KINDS[kind].label + '. Esc cancels.', 'info');
    }

    async function loadLookups() {
      if (!global.DbLookups) return;
      try { if (!st.monsters) st.monsters = await DbLookups.fetch('monsters'); } catch (e) { st.monsters = null; }
      try { if (!st.maps) st.maps = await DbLookups.fetch('maps'); } catch (e) { st.maps = null; }
    }

    // ---- gestures -----------------------------------------------------------------------------------------------------------

    function editable(o) {
      if (!o || o.kind === 'inbound' || !KINDS[o.kind]) return false;
      if (o.dupKey) return false;
      return true;
    }
    function canDelete(o) { return editable(o) && !KINDS[o.kind].noDelete; }

    function screen(v, x, y) { return { x: (x - v.vx) * 3 * v.k, y: (y - v.vy) * 2 * v.k }; }

    /** The handles of the selected object in screen px: resize corners / edges and a movement's destination ring. */
    function handles(o) {
      var v = view().view();
      if (!v || !o) return [];
      var out = [];
      var k = KINDS[o.kind];
      if (k && k.size) {
        var a = screen(v, o.x - o.r, o.y - o.r), b = screen(v, o.x + o.r + 1, o.y + o.r + 1);
        var mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
        [[a.x, a.y, 'nwse'], [b.x, a.y, 'nesw'], [a.x, b.y, 'nesw'], [b.x, b.y, 'nwse'],
          [mx, a.y, 'ns'], [mx, b.y, 'ns'], [a.x, my, 'ew'], [b.x, my, 'ew']].forEach(function (h) {
          out.push({ type: 'size', x: h[0], y: h[1], cursor: h[2] + '-resize' });
        });
      }
      if (o.kind === 'movement' && o.dest && o.dest.same) {
        var d = screen(v, o.dest.x + 0.5, o.dest.y + 0.5);
        out.push({ type: 'dest', x: d.x, y: d.y, cursor: 'move' });
      }
      return out;
    }

    function handleAt(o, p) {
      var hs = handles(o);
      for (var i = 0; i < hs.length; i++) {
        var r = hs[i].type === 'dest' ? 9 : HANDLE + 2;
        if (Math.abs(hs[i].x - p.x) <= r && Math.abs(hs[i].y - p.y) <= r) return hs[i];
      }
      return null;
    }

    function hitEditable(c) {
      var c0 = cur();
      if (!c || !c0) return null;
      var hits = Geo.objectsAt(c0.objects, c.x, c.y).filter(function (o) {
        var l = page.layers();
        var layer = { safezone: 'safezones', respawn: 'respawns', movement: 'movements', mine: 'mines', npc: 'npcs' }[o.kind];
        return editable(o) && l[layer] !== false;
      });
      var sel = page.selected();
      if (sel && hits.some(function (h) { return h.key === sel.key; })) return sel;   // keep dragging the selection
      return hits[0] || null;
    }

    function onPointerDown(e, c, p) {
      if (!st.on || e.button !== 0 || e.altKey || e.ctrlKey || e.metaKey) return false;
      if (st.tool) return false;   // the click places the new object (onCellClick)
      var sel = page.selected();
      var h = sel && editable(sel) ? handleAt(sel, p) : null;
      var target = h ? sel : hitEditable(c);
      if (!target) return false;   // empty ground: pan
      if (!sel || sel.key !== target.key) page.setSelected(target.key, { from: 'map' });
      var o = page.selected() || target;
      st.drag = {
        mode: h ? h.type : 'move', key: o.key, before: snapshot(), moved: false,
        startCell: c, start: { x: o.x, y: o.y, r: o.r, dx: o.dest ? o.dest.x : 0, dy: o.dest ? o.dest.y : 0 }
      };
      global.addEventListener('mousemove', onDragMove);
      global.addEventListener('mouseup', onDragEnd);
      e.preventDefault();
      return true;
    }

    function cellOfEvent(e) {
      var v = view();
      var p = v.local(e);
      return v.toCell(p.x, p.y);
    }

    function clampCell(x, y) {
      var s = view().size();
      return { x: Math.max(0, Math.min(s.w - 1, x)), y: Math.max(0, Math.min(s.h - 1, y)) };
    }

    function onDragMove(e) {
      var d = st.drag;
      if (!d) return;
      var c = cellOfEvent(e);
      if (!c) return;
      var o = view().find(d.key);
      if (!o) return;
      var k = KINDS[o.kind];
      var f = {};
      if (d.mode === 'move') {
        var p = clampCell(d.start.x + (c.x - d.startCell.x), d.start.y + (c.y - d.startCell.y));
        f[k.pos[0]] = p.x; f[k.pos[1]] = p.y;
      } else if (d.mode === 'size') {
        f[k.size] = Math.min(250, Math.max(0, Math.max(Math.abs(c.x - d.start.x), Math.abs(c.y - d.start.y))));
      } else if (d.mode === 'dest') {
        var q = clampCell(c.x, c.y);
        f.Destination_X = q.x; f.Destination_Y = q.y;
      }
      var changed = Object.keys(f).some(function (x) { return !same(rowOf(o)[x], f[x]); });
      if (!changed) return;
      d.moved = true;
      setFields(o, f);
      rebuild();
      page.renderDetails();
    }

    function onDragEnd() {
      global.removeEventListener('mousemove', onDragMove);
      global.removeEventListener('mouseup', onDragEnd);
      var d = st.drag;
      st.drag = null;
      if (!d || !d.moved) return;
      var o = view().find(d.key);
      if (o && o.kind === 'npc' && view().walkable(o.x, o.y) === false) {
        restore(d.before);
        refresh();
        toast('That cell is blocked: an NPC must stand on a walkable cell.', 'error');
        return;
      }
      pushUndo(d.before);
      refresh();
    }

    async function onCellClick(e, c) {
      if (!st.on || !st.tool || !c) return false;
      var kind = st.tool;
      var k = KINDS[kind];
      var row = k.create();
      row[k.pos[0]] = c.x;
      row[k.pos[1]] = c.y;
      if (kind === 'respawn') {
        var mon = await pickLookup('monsters', 'Monster for the new respawn', 'Monster');
        if (mon == null) return true;
        row.MonsterIndex = mon;
      }
      if (kind === 'movement') {
        var dest = clampCell(c.x + 3, c.y);
        row.DestinationMapIndex = cur().index;
        row.Destination_X = dest.x;
        row.Destination_Y = dest.y;
      }
      var before = snapshot();
      st.seq -= 1;
      var index = st.seq;
      st.added.push({ kind: kind, index: index, row: row });
      pushUndo(before);
      if (!e.shiftKey) st.tool = null;   // Shift keeps the tool for the next click
      refresh();
      page.setSelected(kind + ':' + index, { from: 'map' });
      if (kind === 'movement') toast('New portal: drag its destination ring, or pick another destination map in the details.', 'info');
      return true;
    }

    function deleteSelected() {
      var o = page.selected();
      if (!o) return;
      if (!canDelete(o)) {
        toast(o.kind === 'npc' ? 'Delete NPCs in the NPCs grid below.' : 'This object cannot be deleted here.', 'info');
        return;
      }
      var before = snapshot();
      if (isNew(o)) {
        st.added = st.added.filter(function (a) { return !(a.kind === o.kind && a.index === o.index); });
      } else {
        var key = baseKey(o.key);
        var orig = st.baseObjs[key];
        delete st.edits[key];
        st.deleted[key] = { kind: o.kind, index: o.index, rev: orig ? orig.row.__rev : o.row.__rev };
      }
      pushUndo(before);
      page.setSelected(null);
      refresh();
    }

    function nudge(dx, dy) {
      var o = page.selected();
      if (!editable(o)) return;
      var k = KINDS[o.kind];
      var p = clampCell(o.x + dx, o.y + dy);
      if (o.kind === 'npc' && view().walkable(p.x, p.y) === false) { toast('That cell is blocked.', 'error'); return; }
      var before = snapshot();
      var f = {};
      f[k.pos[0]] = p.x; f[k.pos[1]] = p.y;
      setFields(o, f);
      pushUndo(before);
      refresh();
    }

    function grow(n) {
      var o = page.selected();
      if (!editable(o) || !KINDS[o.kind].size) return;
      var before = snapshot();
      var f = {};
      f[KINDS[o.kind].size] = Math.max(0, Math.min(250, o.r + n));
      setFields(o, f);
      pushUndo(before);
      refresh();
    }

    // ---- lookup picker ------------------------------------------------------------------------------------------------------

    /** A small modal with a filtered list of a lookup ('monsters' / 'maps') -> the chosen index, or null. */
    function pickLookup(name, title, what, current) {
      return new Promise(function (resolve) {
        (async function () {
          var lk = null;
          try { lk = global.DbLookups ? await DbLookups.fetch(name) : null; } catch (err) { lk = null; }
          if (!lk) { toast('Could not load the ' + name + ' list.', 'error'); resolve(null); return; }
          if (name === 'monsters') st.monsters = lk; else st.maps = lk;
          var backdrop = el('div', 'portal-confirm-backdrop');
          var dlg = el('div', 'portal-confirm-dialog me-pick-dialog');
          dlg.setAttribute('role', 'dialog');
          dlg.setAttribute('aria-modal', 'true');
          dlg.appendChild(el('h3', 'portal-confirm-title', title));
          var input = el('input', 'mir-input me-pick-input');
          input.type = 'search';
          input.placeholder = 'Search ' + what.toLowerCase() + ' by name or #index…';
          dlg.appendChild(input);
          var list = el('div', 'me-pick-list');
          list.setAttribute('role', 'listbox');
          dlg.appendChild(list);
          var foot = el('div', 'portal-confirm-actions');
          var cancel = button('Cancel', '');
          foot.appendChild(cancel);
          dlg.appendChild(foot);
          backdrop.appendChild(dlg);
          document.body.appendChild(backdrop);
          var active = 0, shown = [];
          function done(v) {
            document.removeEventListener('keydown', onKeyPick, true);
            if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
            resolve(v);
          }
          function render() {
            shown = DbLookups.filterOptions(lk.options, input.value).slice(0, 200);
            active = Math.min(active, Math.max(0, shown.length - 1));
            list.innerHTML = '';
            shown.forEach(function (o, i) {
              var b = el('button', 'me-pick-item' + (i === active ? ' me-pick-active' : '') + (same(o.index, current) ? ' me-pick-current' : ''));
              b.type = 'button';
              b.setAttribute('role', 'option');
              b.appendChild(el('span', 'me-pick-idx', '#' + o.index));
              b.appendChild(el('span', null, o.label));
              b.addEventListener('click', function () { done(Number(o.index)); });
              list.appendChild(b);
            });
            if (!shown.length) list.appendChild(el('p', 'mir-muted', 'No match.'));
          }
          function onKeyPick(ev) {
            if (ev.key === 'Escape') { ev.preventDefault(); ev.stopPropagation(); done(null); }
            else if (ev.key === 'ArrowDown') { ev.preventDefault(); active = Math.min(shown.length - 1, active + 1); render(); }
            else if (ev.key === 'ArrowUp') { ev.preventDefault(); active = Math.max(0, active - 1); render(); }
            else if (ev.key === 'Enter') { ev.preventDefault(); ev.stopPropagation(); if (shown[active]) done(Number(shown[active].index)); }
          }
          document.addEventListener('keydown', onKeyPick, true);
          input.addEventListener('input', function () { active = 0; render(); });
          cancel.addEventListener('click', function () { done(null); });
          backdrop.addEventListener('mousedown', function (ev) { if (ev.target === backdrop) done(null); });
          render();
          input.focus();
        })();
      });
    }

    // ---- details form -------------------------------------------------------------------------------------------------------

    function renderDetails(pane, o) {
      if (!st.on || !o) return;
      if (!editable(o)) {
        if (o.dupKey) pane.appendChild(el('p', 'me-warn', 'Not editable here: another row has the same key.'));
        return;
      }
      var k = KINDS[o.kind];
      var form = el('div', 'me-form');
      form.appendChild(el('h4', 'me-form-title', isNew(o) ? 'New ' + k.label + ' (not applied yet)' : 'Edit'));
      k.fields.forEach(function (f) {
        var name = f[0], label = f[1], type = f[2];
        var row = el('label', 'me-form-row');
        row.appendChild(el('span', 'me-form-label', label));
        var val = o.row[name];
        if (type === 'bool') {
          var cb = el('input');
          cb.type = 'checkbox';
          cb.checked = !!val;
          cb.addEventListener('change', function () { change(o, name, cb.checked); });
          row.appendChild(cb);
        } else if (type === 'monster' || type === 'map') {
          var lk = type === 'monster' ? st.monsters : st.maps;
          var opt = lk ? (lk.byIndex[String(val)] || lk.byIndex[Number(val)]) : null;
          var pick = button((opt ? opt.label : '') + ' #' + val, 'me-form-pick', 'Choose');
          pick.addEventListener('click', async function () {
            var v = await pickLookup(type === 'monster' ? 'monsters' : 'maps', 'Choose ' + label.toLowerCase(), label, val);
            if (v != null) change(o, name, v);
          });
          row.appendChild(pick);
          if (type === 'map') {
            var here = button('This map', 'me-form-pick', 'A destination on this map');
            here.addEventListener('click', function () { change(o, name, cur().index); });
            row.appendChild(here);
          }
        } else {
          var inp = el('input', 'mir-input me-form-num');
          inp.type = 'number';
          inp.step = '1';
          inp.value = val == null ? '' : val;
          inp.addEventListener('change', function () {
            var n = Math.round(Number(inp.value));
            if (!isFinite(n) || n < 0) { inp.value = val; toast(label + ' must be 0 or more.', 'error'); return; }
            change(o, name, n);
          });
          row.appendChild(inp);
        }
        form.appendChild(row);
      });
      var acts = el('div', 'me-det-actions');
      if (canDelete(o)) {
        var del = button(isNew(o) ? 'Drop this new ' + k.label : 'Delete ' + k.label, 'danger');
        del.addEventListener('click', deleteSelected);
        acts.appendChild(del);
      }
      if (!isNew(o) && st.edits[baseKey(o.key)]) {
        var rev = button('Revert changes', '', 'Put this object back as it is saved');
        rev.addEventListener('click', function () {
          var before = snapshot();
          delete st.edits[baseKey(o.key)];
          pushUndo(before);
          refresh();
        });
        acts.appendChild(rev);
      }
      form.appendChild(acts);
      pane.appendChild(form);
    }

    function change(o, field, value) {
      if (o.kind === 'npc' && (field === 'Location_X' || field === 'Location_Y')) {
        var x = field === 'Location_X' ? value : o.x, y = field === 'Location_Y' ? value : o.y;
        if (view().walkable(x, y) === false) { toast('That cell is blocked: an NPC must stand on a walkable cell.', 'error'); page.renderDetails(); return; }
      }
      var before = snapshot();
      var f = {};
      f[field] = value;
      setFields(o, f);
      pushUndo(before);
      refresh();
    }

    // ---- pending list + checks ---------------------------------------------------------------------------------------------

    function describe(o) { return Geo.KIND[o.kind].label + ' ' + (isNew(o) ? '(new)' : '#' + o.index) + ' ' + o.name; }

    function pendingLines() {
      var c = cur();
      var lines = [];
      if (!c) return lines;
      c.objects.forEach(function (o) {
        if (isNew(o)) { lines.push({ key: o.key, text: 'New ' + Geo.KIND[o.kind].label.toLowerCase() + ' at ' + o.x + ', ' + o.y + ': ' + o.name }); return; }
        var e = st.edits[baseKey(o.key)];
        if (!e) return;
        var was = st.baseObjs[baseKey(o.key)];
        var parts = Object.keys(e.fields).map(function (f) { return f + ' ' + (was ? was.row[f] : '?') + ' → ' + e.fields[f]; });
        lines.push({ key: o.key, text: describe(o) + ': ' + parts.join(', ') });
      });
      Object.keys(st.deleted).forEach(function (k) {
        var was = st.baseObjs[k];
        lines.push({ key: null, text: 'Delete ' + (was ? describe(was) : k) });
      });
      return lines;
    }

    /** Warnings about the changed / new objects (never blocking: the server's dry run decides). */
    function checks() {
      var c = cur();
      var out = [];
      if (!c) return out;
      var v = view(), walk = v.walk(), s = v.size();
      var touched = c.objects.filter(function (o) { return isNew(o) || st.edits[baseKey(o.key)]; });
      touched.forEach(function (o) {
        if (o.x < 0 || o.y < 0 || o.x >= s.w || o.y >= s.h) out.push({ key: o.key, text: describe(o) + ' is outside the map.' });
        if (o.kind === 'respawn') {
          var n = Geo.walkableIn(o, walk, s.w, s.h);
          if (n === 0) out.push({ key: o.key, text: describe(o) + ': no walkable cell in its area, nothing will spawn.' });
          else if (n > 0 && num(o.row.Count) > n) out.push({ key: o.key, text: describe(o) + ': Count ' + o.row.Count + ' is more than its ' + n + ' walkable cell(s).' });
          c.objects.forEach(function (z) {
            if (z.kind === 'safezone' && Geo.overlaps(o, z)) out.push({ key: o.key, text: describe(o) + ' overlaps safe zone #' + z.index + '.' });
          });
        }
        if (o.kind === 'safezone') {
          c.objects.forEach(function (r) {
            if (r.kind === 'respawn' && Geo.overlaps(o, r)) out.push({ key: o.key, text: describe(o) + ' overlaps respawn ' + r.name + '.' });
          });
        }
        if (o.kind === 'npc' && v.walkable(o.x, o.y) === false) out.push({ key: o.key, text: describe(o) + ' stands on a blocked cell (the server refuses it).' });
        if (o.kind === 'movement') {
          if (v.walkable(o.x, o.y) === false) out.push({ key: o.key, text: describe(o) + ': the source cell is blocked, nobody can step on it.' });
          if (o.dest.same && v.walkable(o.dest.x, o.dest.y) === false) out.push({ key: o.key, text: describe(o) + ': the destination cell is blocked.' });
        }
      });
      return out.slice(0, 50);
    }

    function renderPending() {
      var box = page.ui().pendingBox;
      if (!box) return;
      box.innerHTML = '';
      var n = pendingCount();
      box.classList.toggle('hidden', !st.on && !n);
      if (!st.on && !n) return;
      var head = el('div', 'me-pending-head');
      head.appendChild(el('strong', null, n ? 'Pending: ' + n + ' change(s) on this map' : 'No pending changes'));
      head.appendChild(el('span', 'mir-muted', n ? ' — nothing is sent until you apply.' : ' — drag objects, use + to add, Del to delete.'));
      var acts = el('span', 'me-pending-actions');
      var apply = button('Review & apply (' + n + ')', 'primary');
      apply.disabled = !n;
      apply.addEventListener('click', applyAll);
      var discard = button('Discard', '');
      discard.disabled = !n;
      discard.addEventListener('click', async function () {
        var ok = await global.portalConfirm('Discard ' + n + ' pending change(s) on this map?', { title: 'Discard map edits', confirmLabel: 'Discard' });
        if (!ok) return;
        clearAll();
        refresh();
      });
      acts.appendChild(apply);
      acts.appendChild(discard);
      head.appendChild(acts);
      box.appendChild(head);
      var lines = pendingLines();
      if (lines.length) {
        var ul = el('ul', 'me-pending-list');
        lines.slice(0, 200).forEach(function (l) {
          var li = el('li');
          if (l.key) {
            var a = el('button', 'me-link', l.text);
            a.type = 'button';
            a.addEventListener('click', function () { selectAndShow(l.key); });
            li.appendChild(a);
          } else {
            li.textContent = l.text;
          }
          ul.appendChild(li);
        });
        box.appendChild(ul);
      }
      var cs = checks();
      if (cs.length) {
        var cl = el('ul', 'me-check-list');
        cs.forEach(function (w) {
          var li = el('li', 'me-warn');
          var a = el('button', 'me-link', '⚠ ' + w.text);
          a.type = 'button';
          a.addEventListener('click', function () { selectAndShow(w.key); });
          li.appendChild(a);
          cl.appendChild(li);
        });
        box.appendChild(cl);
      }
    }

    function selectAndShow(key) {
      var o = view().find(key);
      if (!o) return;
      page.setSelected(o.key, { from: 'list' });
      view().showCell(o.x, o.y, false, true);
    }

    // ---- apply ---------------------------------------------------------------------------------------------------------------

    function buildTables() {
      var c = cur();
      var tables = Object.create(null);
      function t(kind) {
        var ep = KINDS[kind].endpoint;
        return tables[ep] || (tables[ep] = { endpoint: ep, rows: [], deletes: [] });
      }
      Object.keys(st.edits).forEach(function (k) {
        var e = st.edits[k];
        if (!Object.keys(e.fields).length) return;
        t(e.kind).rows.push(Object.assign({ Index: e.index, __rev: e.rev }, e.fields));
      });
      st.added.forEach(function (a) {
        var row = { Index: a.index, MapIndex: c.index };
        Object.keys(a.row).forEach(function (f) { if (f.indexOf('__') !== 0) row[f] = a.row[f]; });
        t(a.kind).rows.push(row);
      });
      Object.keys(st.deleted).forEach(function (k) {
        var d = st.deleted[k];
        var tb = t(d.kind);
        tb.deletes.push(d.index);
        if (d.rev) { tb.deleteRevs = tb.deleteRevs || {}; tb.deleteRevs[d.index] = d.rev; }
      });
      return Object.keys(tables).map(function (k) { return tables[k]; });
    }

    async function applyAll() {
      if (!pendingCount()) return;
      if (!(global.MirLiveDb && MirLiveDb.runBatch)) { toast('Game Data is not loaded: cannot apply.', 'error'); return; }
      if (!liveDbOn()) { toast('LiveDb is off on the server: map edits cannot be applied. Discard them, or use the grids.', 'error'); return; }
      var cs = checks();
      if (cs.length) {
        var ok = await global.portalConfirm(cs.length + ' check(s) flagged:\n\n' + cs.slice(0, 12).map(function (w) { return '• ' + w.text; }).join('\n') +
          (cs.length > 12 ? '\n…' : '') + '\n\nContinue to the dry run?', { title: 'Checks before apply', confirmLabel: 'Continue', danger: false });
        if (!ok) return;
      }
      var c = cur();
      var res = await MirLiveDb.runBatch('map #' + c.index + ' ' + ((c.overlay.map && c.overlay.map.Title) || ''), buildTables());
      if (!res) return;   // cancelled / refused: the edits stay
      clearAll();
      if (global.DbSave && DbSave.setDirty) DbSave.setDirty(page.EDITS_KEY, false);
      await page.reloadOverlay(true);
      reloadCleanGrids();
      refresh();
    }

    function reloadCleanGrids() {
      ['safezone', 'respawn', 'movement', 'mine', 'npc'].forEach(function (id) {
        var ed = page.grid(id);
        if (ed && ed.getApi && ed.getApi() && !(ed.isDirty && ed.isDirty())) ed.load();
      });
    }

    // ---- page hooks --------------------------------------------------------------------------------------------------------

    hooks.pendingCount = function () { return pendingCount(); };
    hooks.pendingSummary = function () { var n = pendingCount(); return n ? n + ' map edit(s)' : ''; };
    hooks.discard = function () {
      clearAll();
      st.tool = null;
      if (global.DbSave && DbSave.setDirty) DbSave.setDirty(page.EDITS_KEY, false);
      renderPending();
      renderTools();
    };
    hooks.onOverlay = function (ov) {
      // Fresh rows: the originals by key; pending edits whose row changed meanwhile (other __rev) or vanished are dropped.
      st.baseObjs = Object.create(null);
      Geo.objectsFromOverlay(ov).forEach(function (o) { if (!o.dupKey) st.baseObjs[o.key] = o; });
      var dropped = 0;
      Object.keys(st.edits).forEach(function (k) {
        var b = st.baseObjs[k];
        if (!b || b.row.__rev !== st.edits[k].rev) { delete st.edits[k]; dropped++; }
      });
      Object.keys(st.deleted).forEach(function (k) {
        var b = st.baseObjs[k];
        if (!b || b.row.__rev !== st.deleted[k].rev) { delete st.deleted[k]; dropped++; }
      });
      if (dropped) toast(dropped + ' pending map edit(s) were dropped: those rows changed on the server meanwhile.', 'error');
      if (st.on || pendingCount()) { rebuild(); renderPending(); }
      renderTools();
    };
    hooks.onSelect = function () { renderTools(); };
    hooks.renderDetails = renderDetails;
    hooks.onPointerDown = onPointerDown;
    hooks.onCellClick = function (e, c) {
      if (!st.on || !st.tool) return false;
      onCellClick(e, c);
      return true;
    };
    hooks.cursor = function (c, e) {
      if (!st.on) return '';
      if (st.tool) return 'copy';
      var sel = page.selected();
      if (sel && editable(sel)) {
        var h = handleAt(sel, view().local(e));
        if (h) return h.cursor;
      }
      return hitEditable(c) ? 'move' : '';
    };
    hooks.drawSelection = function (ctx, v, o) {
      if (!st.on || !editable(o)) return;
      handles(o).forEach(function (h) {
        if (h.type === 'dest') {
          ctx.strokeStyle = 'rgba(255,255,255,0.95)';
          ctx.lineWidth = 2;
          ctx.beginPath(); ctx.arc(h.x, h.y, 7, 0, Math.PI * 2); ctx.stroke();
          return;
        }
        ctx.fillStyle = '#ffffff';
        ctx.strokeStyle = 'rgba(0,0,0,0.85)';
        ctx.lineWidth = 1;
        ctx.fillRect(h.x - HANDLE / 2 - 0.5, h.y - HANDLE / 2 - 0.5, HANDLE + 1, HANDLE + 1);
        ctx.strokeRect(h.x - HANDLE / 2 - 0.5, h.y - HANDLE / 2 - 0.5, HANDLE + 1, HANDLE + 1);
      });
      var k = KINDS[o.kind];
      if (k.size) {
        var a = screen(v, o.x - o.r, o.y - o.r);
        var txt = k.size + ' ' + o.r + ' (' + (o.r * 2 + 1) + '×' + (o.r * 2 + 1) + ')';
        ctx.font = '11px Inter, sans-serif';
        var w = ctx.measureText(txt).width + 8;
        ctx.fillStyle = 'rgba(11,13,17,0.85)';
        ctx.fillRect(a.x, a.y - 18, w, 15);
        ctx.fillStyle = '#e5e7eb';
        ctx.textAlign = 'left';
        ctx.fillText(txt, a.x + 4, a.y - 7);
      }
    };
    hooks.onKey = function (e) {
      if (!st.on) return false;
      if (page.isTyping(e)) return false;
      var ctrl = e.ctrlKey || e.metaKey;
      var key = e.key;
      if (ctrl && (key === 'z' || key === 'Z') && !e.shiftKey) { e.preventDefault(); undo(); return true; }
      if (ctrl && (key === 'y' || key === 'Y' || ((key === 'z' || key === 'Z') && e.shiftKey))) { e.preventDefault(); redo(); return true; }
      if (ctrl || e.altKey) return false;
      if (key === 'Escape' && st.tool) { e.preventDefault(); setTool(null); return true; }
      if (key === 'Delete' || key === 'Backspace') { if (page.selected()) { e.preventDefault(); deleteSelected(); return true; } return false; }
      var step = e.shiftKey ? 10 : 1;
      if (key === 'ArrowLeft') { e.preventDefault(); nudge(-step, 0); return true; }
      if (key === 'ArrowRight') { e.preventDefault(); nudge(step, 0); return true; }
      if (key === 'ArrowUp') { e.preventDefault(); nudge(0, -step); return true; }
      if (key === 'ArrowDown') { e.preventDefault(); nudge(0, step); return true; }
      if (key === '[') { e.preventDefault(); grow(-1); return true; }
      if (key === ']') { e.preventDefault(); grow(1); return true; }
      return false;
    };
    hooks.onLiveDb = function () { renderTools(); };

    buildTools();

    global.MapEditorEdit.state = function () {
      return { on: st.on, tool: st.tool, pending: pendingCount(), tables: buildTables(), checks: checks(), undo: st.undo.length, redo: st.redo.length };
    };
    global.MapEditorEdit.setOn = setOn;
    global.MapEditorEdit.applyAll = applyAll;
  }

  global.MapEditorEdit = { attach: attach };
})(window);
