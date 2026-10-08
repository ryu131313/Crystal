/**
 * Row form drawer for DB grids.
 *
 * Double-click a grid row -> side drawer with grouped, typed fields built from
 * the /api/admin/db/schema/{endpoint} schema: searchable FK selects, enum
 * dropdowns, checkboxes, Point editors, live image thumbnails, and a visual
 * frame picker for image-index fields. Apply writes back into the grid row
 * (sets __gridDirty); persistence stays on the grid's normal Save button.
 *
 * Below the fields, related-record panels: the row's child tables (FormDrawer.CHILDREN, or DbRegistry.children when a
 * registry is loaded) as small editable grids with their own Save, read-only panels of the rows that point at it
 * (FormDrawer.REFERENCED_BY) and links to single parent rows (FormDrawer.LINKS). Each panel loads when it is opened,
 * through the generic GET /api/admin/db/{endpoint}?{Field}=N filter. The tables start EMPTY for MirCraft; the Game Data
 * module fills them (e.g. FormDrawer.REFERENCED_BY.items = [...]) without editing this file.
 *
 * window.FormDrawer.open({ endpoint, entity, rowNode, dirtyKey?, readOnly? })
 */
(function (global) {
  var current = null;   // { endpoint, entity, rowNode, schema, lookups, inputs, row }
  var dirty = false;

  // Image fields that get a thumbnail. The thumbnail and the Browse… picker both come from the one server-side resolver
  // (§7.1: ImagePreview.resolveUrl, LibraryPicker in admin-library.js); this file no longer has its own library tables
  // (the old PICKER_FIELDS showed 60 raw values a page, empty frames included, with a Next that never ended).
  var THUMB_FIELDS = { Image: 1, StateImage: 1, Shape: 1, ItemIndex: 1, Icon: 1, BuffIcon: 1 };

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function firstHump(name) {
    var m = String(name).match(/^[A-Z][a-z0-9]+/);
    return m ? m[0] : name;
  }

  // Group schema properties: General (ids/strings/enums/refs), Appearance
  // (image-ish), prefix groups for numeric stat blocks, Flags (bools), Stats.
  function groupProps(props) {
    var humpCount = Object.create(null);
    props.forEach(function (p) {
      if (p.kind === 'number' && p.name !== 'Index') humpCount[firstHump(p.name)] = (humpCount[firstHump(p.name)] || 0) + 1;
    });

    var order = ['General', 'Appearance'];
    var groups = { General: [], Appearance: [] };
    function add(name, p) {
      if (!groups[name]) { groups[name] = []; order.push(name); }
      groups[name].push(p);
    }

    props.forEach(function (p) {
      if (THUMB_FIELDS[p.name] && p.kind !== 'reference') { add('Appearance', p); return; }
      if (p.kind === 'bool') { add('Flags', p); return; }
      if (p.kind === 'reference' || p.kind === 'enum' || p.kind === 'string' || p.name === 'Index') { add('General', p); return; }
      if (p.kind === 'number') {
        var hump = firstHump(p.name);
        if (humpCount[hump] >= 3 && hump.length >= 3) { add(hump, p); return; }
        add('Stats', p);
        return;
      }
      add('General', p); // datetime, timespan, point
    });

    // Flags + Stats at the end.
    ['Stats', 'Flags'].forEach(function (g) {
      var i = order.indexOf(g);
      if (i >= 0) { order.splice(i, 1); order.push(g); }
    });
    return order.filter(function (g) { return groups[g] && groups[g].length; })
      .map(function (g) { return { name: g, props: groups[g] }; });
  }

  // Same rule as the grid (DbSchema.isLongText), for the one row the drawer shows.
  function isLongText(p, value) {
    if (p.multiline === true) return true;
    if (p.multiline === false) return false;
    return typeof value === 'string' && (value.length > 80 || /[\r\n]/.test(value));
  }

  function markDirty() {
    dirty = true;
    var st = document.getElementById('formDrawerStatus');
    if (st) st.textContent = 'Unapplied changes';
  }

  // ---------- field inputs ----------

  function searchableSelect(options, value, onPick) {
    // Mini searchable dropdown (same look as the grid lookup editor).
    var wrap = el('div', 'fd-lookup');
    var input = el('input', 'fd-input');
    input.type = 'text';
    input.value = value || '';
    input.autocomplete = 'off';
    var list = el('div', 'fd-lookup-list hidden');
    wrap.appendChild(input);
    wrap.appendChild(list);

    function render() {
      var q = input.value.trim().toLowerCase();
      list.innerHTML = '';
      var shown = 0;
      for (var i = 0; i < options.length && shown < 100; i++) {
        var label = options[i].label != null ? options[i].label : String(options[i]);
        if (q && label.toLowerCase().indexOf(q) < 0) continue;
        (function (label) {
          var btn = el('button', 'fd-lookup-option', label || '(none)');
          btn.type = 'button';
          btn.addEventListener('mousedown', function (e) {
            e.preventDefault();
            input.value = label;
            list.classList.add('hidden');
            onPick(label);
          });
          list.appendChild(btn);
        })(label);
        shown++;
      }
      if (!shown) list.appendChild(el('div', 'fd-lookup-empty', 'No matches'));
    }

    input.addEventListener('focus', function () { render(); list.classList.remove('hidden'); });
    input.addEventListener('input', function () { render(); list.classList.remove('hidden'); onPick(input.value); });
    input.addEventListener('blur', function () { setTimeout(function () { list.classList.add('hidden'); }, 150); });
    return { wrap: wrap, input: input };
  }

  async function updateThumb(thumb, entity, field, value, row) {
    if (!thumb || !global.ImagePreview || !ImagePreview.resolveUrl) return;
    try {
      var url = await ImagePreview.resolveUrl(entity, field, value, row);
      if (url) {
        thumb.src = url;
        thumb.style.display = '';
      } else {
        thumb.style.display = 'none';
      }
    } catch (e) { thumb.style.display = 'none'; }
  }

  // LiveDb (schema.liveDb present only while the editor runs): restart-only tables / columns and the kinds
  // the grid cannot edit (point lists, geometry, unsupported) are shown read-only.
  function liveLocked(p, ctx) {
    var ld = ctx && ctx.schema && ctx.schema.liveDb;
    if (!ld || typeof ld !== 'object') return false;
    if (ld.live === false || p.live === false) return true;
    var bad = global.LiveDbLogic ? global.LiveDbLogic.NON_EDITABLE_KINDS : { points: 1, geometry: 1, unsupported: 1 };
    return !!bad[p.kind];
  }

  function buildField(p, row, ctx) {
    var fieldWrap = el('div', 'fd-field');
    var label = el('label', 'fd-label', p.name);
    fieldWrap.appendChild(label);
    if (ctx && ctx.schema && ctx.schema.liveDb && global.LiveDbLogic) {
      var tip = global.LiveDbLogic.timingText(p);
      if (tip) label.title = tip;
      var cls = global.LiveDbLogic.headerClasses(p);
      cls.forEach(function (c) { label.classList.add(c); });
      // Only the exception is marked: a small badge on restart-only fields.
      if (cls.indexOf('livedb-restart') >= 0 && p.name !== 'Index' && !p.readOnly) label.appendChild(el('span', 'fd-badge', 'restart-only'));
    }
    var value = row[p.name];
    // MirCraft rows hold enums as numbers: the field shows and picks member names, Apply writes the number back.
    var numericEnum = p.kind === 'enum' && typeof value === 'number' && !!(global.DbSchema && DbSchema.enumName);
    if (numericEnum) value = p.flags ? DbSchema.flagsText(p, value) : DbSchema.enumName(p, value);
    var input = null;
    var get = null; // () => coerced value or throws { msg }
    // Apply writes back only the fields the user changed: an untouched field never round-trips through its input
    // (a single-line input drops line breaks, a <select> without the current value picks its first entry, ...).
    var touched = false;
    function touch() { touched = true; markDirty(); }

    if (p.name === 'Index' || p.readOnly || liveLocked(p, ctx)) {
      input = el('input', 'fd-input');
      input.value = value == null ? '' : (typeof value === 'object' ? JSON.stringify(value) : String(value));
      input.readOnly = true;
      input.classList.add('fd-readonly');
      fieldWrap.appendChild(input);
      return { el: fieldWrap, apply: null };
    }

    if (p.kind === 'reference') {
      var lookup = ctx.lookups[p.lookup];
      if (lookup) {
        var idx = value == null ? 0 : Number(value);
        var initial = idx && lookup.byIndex[idx] ? lookup.byIndex[idx].label : '';
        var picked = initial;
        // A map-region picker on a row with a map offers that map's regions (the grid cell does the same).
        var choices = lookup.options;
        if (global.DbLookups && DbLookups.optionsFor) {
          var of = DbLookups.optionsFor({ indexField: p.name }, lookup);
          if (typeof of === 'function') choices = of({ data: row }).options;
        }
        var ss = searchableSelect(choices, initial, function (label) { picked = label; touch(); });
        fieldWrap.appendChild(ss.wrap);
        get = function () {
          var label = (picked || '').trim();
          if (!label) return 0;
          var opt = lookup.byLabel[label];
          if (!opt) throw { msg: p.name + ': "' + label + '" is not a known ' + p.lookup + ' entry' };
          return opt.index;
        };
        return {
          el: fieldWrap,
          apply: function (out) {
            if (!touched) return;
            var v = get();
            out[p.name] = v;
            // Keep the paired display column in sync for the grid.
            var displayField = p.name.replace(/Index$/, '') + 'Name';
            if (displayField in row) out[displayField] = v && lookup.byIndex[v] ? lookup.byIndex[v].label : null;
          }
        };
      }
      // No lookup -> plain number input for the raw index.
      p = { name: p.name, kind: 'number' };
    }

    if (p.kind === 'bool') {
      input = el('input');
      input.type = 'checkbox';
      input.checked = value === true || value === 'true';
      input.addEventListener('change', touch);
      fieldWrap.classList.add('fd-field-bool');
      fieldWrap.appendChild(input);
      get = function () { return input.checked; };
    } else if (p.kind === 'enum' && p.flags && global.DbSchema && DbSchema.flagsValue) {
      // [Flags] enum ("Warrior, Wizard"): a checkbox per single-bit member, quick buttons for the named combinations.
      var bits = DbSchema.flagsValue(p, value);
      if (isNaN(bits)) bits = 0;
      var startBits = bits;
      var members = Array.isArray(p.members) ? p.members : [];
      var box = el('div', 'fd-flags');
      var shownText = el('div', 'fd-flags-value mir-muted');
      var checks = [];
      var syncFlags = function () {
        checks.forEach(function (c) { c.cb.checked = (bits & c.value) === c.value; });
        shownText.textContent = DbSchema.flagsText(p, bits);
      };
      members.forEach(function (m) {
        if (!(m.value > 0 && (m.value & (m.value - 1)) === 0)) return;
        var lab = el('label', 'fd-flag');
        var cb = el('input');
        cb.type = 'checkbox';
        cb.addEventListener('change', function () { if (cb.checked) bits |= m.value; else bits &= ~m.value; touch(); syncFlags(); });
        checks.push({ cb: cb, value: m.value });
        lab.appendChild(cb);
        lab.appendChild(document.createTextNode(' ' + m.name));
        box.appendChild(lab);
      });
      var quickRow = el('div', 'fd-flags-quick');
      members.forEach(function (m) {
        if (m.value > 0 && (m.value & (m.value - 1)) === 0) return;
        var qb = el('button', 'mir-btn', m.name);
        qb.type = 'button';
        qb.onclick = function () { bits = m.value; touch(); syncFlags(); };
        quickRow.appendChild(qb);
      });
      fieldWrap.classList.add('fd-field-wide');
      fieldWrap.appendChild(box);
      if (quickRow.children.length) fieldWrap.appendChild(quickRow);
      fieldWrap.appendChild(shownText);
      syncFlags();
      input = box;
      get = function () { return bits === startBits ? value : DbSchema.flagsText(p, bits); };
    } else if (p.kind === 'enum') {
      var values = p.enumValues || [];
      if (values.length > 25) {
        var pickedEnum = value == null ? '' : String(value);
        var ssE = searchableSelect(values.map(function (v) { return { label: v }; }), pickedEnum, function (label) { pickedEnum = label; touch(); });
        fieldWrap.appendChild(ssE.wrap);
        input = ssE.input;
        get = function () {
          var v = (pickedEnum || '').trim();
          if (!v) v = values[0] || '';
          if (values.indexOf(v) < 0) throw { msg: p.name + ': "' + v + '" is not a valid ' + p.type };
          return v;
        };
      } else {
        input = el('select', 'fd-input');
        // A value that is not a list entry (a [Flags] combination such as "WarWizTao, Berserker") is kept as the
        // first, selected option; without it the browser would show (and Apply would write) the first entry.
        var shownValues = (value != null && value !== '' && values.indexOf(String(value)) < 0) ? [String(value)].concat(values) : values;
        shownValues.forEach(function (v) {
          var o = el('option', null, v);
          o.value = v;
          if (String(value) === v) o.selected = true;
          input.appendChild(o);
        });
        input.addEventListener('change', touch);
        fieldWrap.appendChild(input);
        get = function () { return input.value; };
      }
    } else if (p.kind === 'point') {
      var pv = value && typeof value === 'object' ? value : { X: 0, Y: 0 };
      var xi = el('input', 'fd-input fd-point');
      xi.type = 'number'; xi.value = pv.X != null ? pv.X : 0;
      var yi = el('input', 'fd-input fd-point');
      yi.type = 'number'; yi.value = pv.Y != null ? pv.Y : 0;
      [xi, yi].forEach(function (i) { i.addEventListener('input', touch); });
      var pw = el('div', 'fd-point-wrap');
      pw.appendChild(el('span', 'mir-muted', 'X'));
      pw.appendChild(xi);
      pw.appendChild(el('span', 'mir-muted', 'Y'));
      pw.appendChild(yi);
      fieldWrap.appendChild(pw);
      get = function () {
        var x = parseInt(xi.value, 10), y = parseInt(yi.value, 10);
        if (isNaN(x) || isNaN(y)) throw { msg: p.name + ': X and Y must be integers' };
        return { X: x, Y: y };
      };
    } else if (p.kind === 'number') {
      input = el('input', 'fd-input');
      input.type = 'number';
      input.step = 'any';
      input.value = value == null ? '' : value;
      input.addEventListener('input', touch);
      fieldWrap.appendChild(input);
      get = function () {
        if (input.value === '') return 0;
        var n = Number(input.value);
        if (!isFinite(n)) throw { msg: p.name + ': not a number' };
        return n;
      };
    } else if (p.kind === 'string' && isLongText(p, value)) {
      // Long or multi-line text (NPCPage.Say ...): a textarea across both columns that grows with its content.
      // A textarea reads back LF; CRLF is restored when the stored text used it.
      input = el('textarea', 'fd-input fd-textarea');
      input.rows = 3;
      input.value = value == null ? '' : String(value);
      var grow = function () { input.style.height = 'auto'; input.style.height = Math.min(480, input.scrollHeight + 2) + 'px'; };
      input.addEventListener('input', function () { touch(); grow(); });
      fieldWrap.classList.add('fd-field-wide');
      fieldWrap.appendChild(input);
      setTimeout(grow, 0);
      get = function () {
        return global.DbSchema && DbSchema.keepLineEndings ? DbSchema.keepLineEndings(value, input.value) : input.value;
      };
    } else { // string, datetime, timespan
      input = el('input', 'fd-input');
      input.type = 'text';
      if (p.kind === 'datetime') input.placeholder = 'yyyy-MM-dd HH:mm';
      if (p.kind === 'timespan') input.placeholder = 'hh:mm:ss';
      input.value = value == null ? '' : String(value);
      input.addEventListener('input', touch);
      fieldWrap.appendChild(input);
      get = function () { return input.value; };
    }

    // Image thumbnail + optional frame picker for appearance fields.
    if (ctx.entity && THUMB_FIELDS[p.name] && (p.kind === 'number' || p.kind === 'enum')) {
      var thumb = el('img', 'fd-thumb');
      thumb.style.display = 'none';
      fieldWrap.appendChild(thumb);
      var refresh = function () {
        var v;
        try { v = get(); } catch (e) { return; }
        updateThumb(thumb, ctx.entity, p.name, v, row);
      };
      (input || fieldWrap).addEventListener('input', refresh);
      (input || fieldWrap).addEventListener('change', refresh);
      refresh();

      var canPick = global.LibraryPicker && global.ImagePreview && ImagePreview.pickable && ImagePreview.pickable(ctx.entity, p.name);
      if (canPick && (p.kind === 'number' || p.kind === 'enum') && input) {
        var btn = el('button', 'mir-btn fd-browse', 'Browse…');
        btn.type = 'button';
        btn.title = 'Pick the art in the Library Viewer';
        btn.onclick = async function () {
          var cur;
          try { cur = get(); } catch (e) { cur = input.value; }
          var picked = await LibraryPicker.pick({ entity: ctx.entity, field: p.name, value: cur, row: row, title: p.name });
          if (picked == null) return;
          if (p.kind === 'enum' && p.enumValues && p.enumValues.length) {
            // A MonsterImage cell holds the member name; a number without a name is kept as the number.
            var named = await enumNameOf(p, picked);
            input.value = named || String(picked);
            if (typeof pickedEnum !== 'undefined') pickedEnum = input.value;
          } else {
            input.value = picked;
          }
          touch();
          refresh();
        };
        fieldWrap.appendChild(btn);
      }
    }

    if (numericEnum && get) {
      var getName = get;
      get = function () {
        var v = getName();
        var n = p.flags ? DbSchema.flagsValue(p, v) : DbSchema.enumNumber(p, v);
        if (n == null || isNaN(n)) throw { msg: p.name + ': "' + v + '" is not a valid ' + p.type };
        return n;
      };
    }

    return {
      el: fieldWrap,
      apply: get ? function (out) { if (touched) out[p.name] = get(); } : null
    };
  }

  // A picked number -> its enum member name (null when it has none).
  async function enumNameOf(p, value) {
    if (!global.DbSchema || !DbSchema.enumName) return null;
    var name = DbSchema.enumName(p, value);
    return name && name !== String(value) ? name : null;
  }

  // The image picker modal is the Library Viewer's (LibraryPicker, admin-library.js).
  function closeImagePicker() {
    if (global.LibraryPicker && LibraryPicker.close) LibraryPicker.close();
  }

  // ---------- open / apply / close ----------

  async function open(opts) {
    if (!global.DbSchema) return;
    if (dirty && !window.confirm('Discard unapplied drawer changes?')) return;
    // Opening another row throws the related-record panels away: ask first when one has unsaved edits.
    if (panelsDirty() && !window.confirm('A related-records panel has unsaved edits. Open this row and lose them?')) return;
    dirty = false;

    var schema = await DbSchema.fetch(opts.endpoint);
    if (!schema) {
      portalToast('No schema available for ' + opts.endpoint + '.', 'error');
      return;
    }
    var row = opts.rowNode.data;

    // Load lookups for all reference props up front.
    var lookups = Object.create(null);
    if (global.DbLookups) {
      var refs = schema.properties.filter(function (p) { return p.kind === 'reference' && p.lookup; })
        .map(function (p) { return { lookup: p.lookup }; });
      try { lookups = await DbLookups.loadForReferences(refs); } catch (e) { }
    }

    closePanels();
    current = { endpoint: opts.endpoint, dirtyKey: opts.dirtyKey || opts.endpoint, entity: opts.entity || opts.endpoint, rowNode: opts.rowNode, schema: schema, lookups: lookups, appliers: [], row: row, panels: [], readOnly: !!opts.readOnly };

    var titleEl = document.getElementById('formDrawerTitle');
    var nameProp = row.ItemName || row.MonsterName || row.Name || row.NPCName || row.QuestName || ('#' + row.Index);
    if (titleEl) titleEl.textContent = schema.type + ' — ' + nameProp;
    var st = document.getElementById('formDrawerStatus');
    if (st) st.textContent = '';

    var body = document.getElementById('formDrawerBody');
    if (!body) return;
    body.innerHTML = '';

    groupProps(schema.properties).forEach(function (group) {
      var det = document.createElement('details');
      det.open = group.name === 'General' || group.name === 'Appearance';
      var sum = document.createElement('summary');
      sum.textContent = group.name + ' (' + group.props.length + ')';
      det.appendChild(sum);
      var grid = el('div', 'fd-group-grid');
      group.props.forEach(function (p) {
        var f = buildField(p, row, current);
        grid.appendChild(f.el);
        if (f.apply) current.appliers.push(f.apply);
      });
      det.appendChild(grid);
      body.appendChild(det);
    });

    if (row.Index > 0) buildRelated(body, opts.endpoint, row);

    var drawerApply = document.getElementById('formDrawerApply');
    if (drawerApply) drawerApply.disabled = !!opts.readOnly || (!!schema.liveDb && !current.appliers.length); // restart-only table: nothing to apply

    document.getElementById('formDrawer').classList.remove('hidden');
  }

  function applyChanges() {
    if (!current) return;
    var updated = Object.assign({}, current.row);
    try {
      current.appliers.forEach(function (fn) { fn(updated); });
    } catch (e) {
      portalToast(e.msg || String(e), 'error');
      return;
    }
    var before = current.row;
    var changes = Object.keys(updated).filter(function (k) {
      return k.indexOf('__') !== 0 && JSON.stringify(updated[k]) !== JSON.stringify(before[k]);
    }).map(function (k) { return { field: k, before: before[k], after: updated[k] }; });
    current.rowNode.setData(updated);
    current.row = updated;
    if (!changes.length) {
      dirty = false;
      var st0 = document.getElementById('formDrawerStatus');
      if (st0) st0.textContent = 'Nothing changed.';
      return;
    }
    if (global.DbSave && DbSave.noteEdit) DbSave.noteEdit(current.dirtyKey, updated.Index, changes);
    else if (global.DbSave && DbSave.setDirty) DbSave.setDirty(current.dirtyKey, true);
    else global.__gridDirty = true;
    dirty = false;
    var st = document.getElementById('formDrawerStatus');
    if (st) st.textContent = 'Applied to grid — use the grid’s Save button to persist.';
    portalToast('Row updated in grid. Save the grid to persist.', 'ok');
  }

  function close() {
    if (dirty && !window.confirm('Discard unapplied drawer changes?')) return;
    if (panelsDirty() && !window.confirm('A related-records panel has unsaved edits. Close the drawer and lose them?')) return;
    dirty = false;
    closePanels();
    current = null;
    var d = document.getElementById('formDrawer');
    if (d) d.classList.add('hidden');
    closeImagePicker();
  }

  // ---------- related records (5.1) ----------

  // Rows of other tables that point at this row: endpoint -> [{ title, endpoint, field }] (field = the FK on the other
  // table). Read-only panels. Empty for MirCraft until the Game Data module fills it.
  var REFERENCED_BY = {};

  // Child tables edited in place: endpoint -> [{ title, endpoint, field }] (used when no DbRegistry is loaded).
  var CHILDREN = {};

  // Links to a single parent / look row: endpoint -> [{ title, field (on this row), endpoint (it points at) }].
  var LINKS = {};

  var panelSeq = 0;

  // The panels' grids are destroyed (their listeners, registry entries and dirty state go with them).
  function closePanels() {
    if (!current || !current.panels) return;
    current.panels.forEach(function (pn) {
      if (pn.editor && pn.editor.destroy) pn.editor.destroy();
      else if (global.DbSave && DbSave.setDirty) DbSave.setDirty(pn.dirtyKey, false);
    });
    current.panels = [];
  }

  function panelsDirty() {
    if (!current || !current.panels || !global.DbSave || !DbSave.isDirty) return false;
    return current.panels.some(function (pn) { return DbSave.isDirty(pn.dirtyKey); });
  }

  function regLabel(endpoint) {
    if (global.DbRegistry && DbRegistry.label) return DbRegistry.label(endpoint);
    return global.__dbTabLabel ? global.__dbTabLabel(endpoint) : endpoint;
  }

  // Go to one row of a table: the registry's navigator when loaded, else the Game Data deep link (dbTab, row).
  function gotoRow(endpoint, index) {
    if (global.DbNav && DbNav.goto) { DbNav.goto(endpoint, index); return; }
    if (global.portalOpenDeepLink) global.portalOpenDeepLink('gamedata', { dbTab: endpoint, row: index });
  }

  // Open a table filtered to the children of one row (Game Data deep link: dbTab plus <Field>=<index>).
  function openChildTable(endpoint, field, index, label) {
    if (global.DbNav && DbNav.openChild) { DbNav.openChild(endpoint, field, index, label); return; }
    if (global.portalOpenDeepLink) {
      var prm = { dbTab: endpoint };
      prm[field] = index;
      global.portalOpenDeepLink('gamedata', prm);
    }
  }

  function rowName(row) {
    return row.ItemName || row.MonsterName || row.Name || row.NPCName || row.QuestName || row.SetName || row.Description || ('#' + row.Index);
  }

  function buildRelated(body, endpoint, row) {
    var R = global.DbRegistry;
    var sections = [];
    (LINKS[endpoint] || []).forEach(function (l) {
      var idx = Number(row[l.field]);
      if (idx > 0) sections.push({ kind: 'link', title: l.title, endpoint: l.endpoint, index: idx });
    });
    var children = R && R.children
      ? R.children(endpoint).map(function (c) { return { title: c.label, endpoint: c.endpoint, field: c.field }; })
      : (CHILDREN[endpoint] || []);
    children.forEach(function (c) {
      sections.push({ kind: 'grid', title: c.title, endpoint: c.endpoint, field: c.field, editable: !current.readOnly });
    });
    (REFERENCED_BY[endpoint] || []).forEach(function (r) {
      sections.push({ kind: 'grid', title: r.title, endpoint: r.endpoint, field: r.field, editable: false });
    });
    if (!sections.length) return;

    var wrap = el('div', 'fd-related');
    wrap.appendChild(el('div', 'fd-related-title', 'Related records'));
    sections.forEach(function (sec) {
      if (sec.kind === 'action') {
        var act = el('a', 'fd-related-link', sec.title);
        act.href = '#';
        act.onclick = function (e) { e.preventDefault(); sec.run(); };
        wrap.appendChild(act);
        return;
      }
      if (sec.kind === 'link') {
        var line = el('div', 'fd-related-link');
        line.appendChild(el('span', 'mir-muted', sec.title + ': '));
        var a = el('a', null, regLabel(sec.endpoint) + ' #' + sec.index);
        a.href = '#';
        a.onclick = function (e) { e.preventDefault(); close(); if (!current) gotoRow(sec.endpoint, sec.index); };
        line.appendChild(a);
        wrap.appendChild(line);
        return;
      }
      var det = document.createElement('details');
      det.className = 'fd-related-panel';
      var sum = document.createElement('summary');
      sum.textContent = sec.title;
      det.appendChild(sum);
      var loaded = false;
      det.addEventListener('toggle', function () {
        if (!det.open || loaded) return;
        loaded = true;
        buildGridPanel(det, sec, row);
      });
      wrap.appendChild(det);
    });
    body.appendChild(wrap);
  }

  function buildGridPanel(det, sec, row) {
    var bar = el('div', 'fd-related-bar');
    var openAll = el('a', null, 'Open in ' + regLabel(sec.endpoint) + ' \u2197');
    openAll.href = '#';
    openAll.onclick = function (e) {
      e.preventDefault();
      var label = regLabel(current ? current.endpoint : '') + ' = ' + rowName(row);
      close();
      if (!current) openChildTable(sec.endpoint, sec.field, row.Index, label);
    };
    bar.appendChild(openAll);
    det.appendChild(bar);
    var grid = el('div', 'ag-theme-quartz-dark grid-panel fd-mini-grid');
    grid.id = 'fdPanel' + (++panelSeq);
    det.appendChild(grid);
    if (!global.makeDbGridEditor) return;
    var dirtyKey = 'drawer:' + sec.endpoint + ':' + row.Index;
    var defaults = {};
    defaults[sec.field] = row.Index;
    var ed = makeDbGridEditor({
      endpoint: sec.endpoint,
      entity: sec.endpoint,
      gridId: grid.id,
      title: sec.title,
      query: encodeURIComponent(sec.field) + '=' + encodeURIComponent(row.Index),
      compact: true,
      wireDom: false,
      formDrawer: false,
      register: false,
      readOnly: !sec.editable,
      dirtyKey: dirtyKey,
      layoutKey: 'drawer_' + sec.endpoint,
      newRowDefaults: defaults,
      height: '220px'
    });
    if (current) current.panels.push({ dirtyKey: dirtyKey, editor: ed });
    ed.load();
  }

  // ---------- drawer width (drag the left edge; remembered per viewer) ----------

  function initResize() {
    var d = document.getElementById('formDrawer');
    if (!d || d.__resizeReady) return;
    d.__resizeReady = true;
    try { var w = global.localStorage && Number(global.localStorage.getItem('mircraft_drawer_width')); if (w > 300) d.style.width = w + 'px'; } catch (e) { /* storage off */ }
    var handle = el('div', 'fd-resize-handle');
    handle.title = 'Drag to resize';
    d.insertBefore(handle, d.firstChild);
    handle.addEventListener('mousedown', function (e) {
      e.preventDefault();
      var move = function (ev) {
        var w = Math.max(360, Math.min(window.innerWidth - 40, window.innerWidth - ev.clientX));
        d.style.width = w + 'px';
      };
      var up = function () {
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
        try { if (global.localStorage) global.localStorage.setItem('mircraft_drawer_width', String(parseInt(d.style.width, 10) || 0)); } catch (e2) { /* storage off */ }
      };
      document.addEventListener('mousemove', move);
      document.addEventListener('mouseup', up);
    });
  }

  global.FormDrawer = {
    open: open,
    close: close,
    REFERENCED_BY: REFERENCED_BY,
    CHILDREN: CHILDREN,
    LINKS: LINKS
  };

  document.addEventListener('DOMContentLoaded', function () {
    initResize();
    var closeBtn = document.getElementById('formDrawerClose');
    if (closeBtn) closeBtn.onclick = close;
    var applyBtn = document.getElementById('formDrawerApply');
    if (applyBtn) applyBtn.onclick = applyChanges;
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape') return;
      var pm = document.getElementById('imgPickerModal');
      if (pm && !pm.classList.contains('hidden')) return;   // the picker closes itself first
      var d = document.getElementById('formDrawer');
      if (d && !d.classList.contains('hidden')) close();
    });
  });
})(window);
