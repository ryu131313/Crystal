/**
 * Schema-driven column definitions for DB grids.
 *
 * Fetches /api/admin/db/schema/{endpoint} (reflection of the MirDB collection,
 * mirroring PortalDbSerializer) and builds typed ag-grid colDefs:
 *   - number  -> numeric editor + number filter, right-aligned; whole numbers / range from the schema type (Int32, Byte ...)
 *   - bool    -> centred checkbox renderer/editor
 *   - enum    -> dropdown (searchable popup for big enums); [Flags] enums get a checkbox-list editor
 *   - reference (FK) -> searchable lookup editor writing the {Prop}Index field, with a "go to" arrow
 *   - datetime/point -> formatted, with parsing valueSetter for Point
 *   - long / multi-line text -> textarea popup editor that keeps the line endings
 *
 * Columns come in the schema's property order (the C# declaration order), each lookup's name column where its index
 * column would be; widths come from the column type (admin-database-review.md section 4) and headers are split into
 * words ("Required Amount") so they can wrap instead of widening the column. Tables with many columns get collapsible
 * column groups.
 *
 * Every consumer falls back to its legacy row-key colDefs when the schema or
 * lookups fail, so a backend mismatch can never blank a grid.
 *
 * window.DbSchema = { fetch(endpoint), typedColDefs(endpoint, rows, opts), headerName, flatColumns, ... }
 */
(function (global) {
  var schemaCache = Object.create(null); // endpoint -> Promise<schema|null>

  function fetchSchema(endpoint) {
    if (schemaCache[endpoint]) return schemaCache[endpoint];
    schemaCache[endpoint] = (async function () {
      try {
        var r = await portalApi('/api/admin/db/schema/' + encodeURIComponent(endpoint));
        if (!r.ok) return null;
        var j = await r.json().catch(function () { return null; });
        // LiveDb: a schema that carries liveDb.active is evidence the editor runs (before the first status poll lands).
        if (j && j.properties && global.LiveDbInfo) global.LiveDbInfo.noteSchema(j);
        return j && j.properties ? j : null;
      } catch (e) {
        return null;
      }
    })();
    return schemaCache[endpoint];
  }

  function formatDateValue(value) {
    if (value == null || value === '') return '';
    if (typeof value === 'string' && /^\/Date\((-?\d+)\)\/$/.test(value)) {
      var ms = parseInt(value.replace(/^\/Date\(|\)\/$/g, ''), 10);
      if (!isNaN(ms)) return new Date(ms).toLocaleString();
    }
    if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value)) {
      // System.Text.Json ISO-8601: DateTime.MinValue -> blank; a zoned value (Z / +hh:mm) -> local time; else as written.
      if (/^0001-01-01/.test(value)) return '';
      if (/(Z|[+-]\d{2}:?\d{2})$/.test(value)) {
        var d = new Date(value);
        if (!isNaN(d.getTime())) {
          var pad = function (n) { return (n < 10 ? '0' : '') + n; };
          return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
        }
      }
      return value.replace('T', ' ').substring(0, 16);
    }
    return value;
  }

  // "RequiredAmount" -> "Required Amount", "NPCName" -> "NPC Name", "MaxDC" -> "Max DC".
  function headerName(field) {
    return String(field || '')
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
      .replace(/_/g, ' ');
  }

  function addCellClass(col, cls) {
    var cur = col.cellClass;
    if (!cur) col.cellClass = cls;
    else col.cellClass = [].concat(cur, cls);
  }

  // Build a references array (DbLookups shape) from schema reference props that
  // have a lookup, skipping any indexField already covered by extraReferences.
  function deriveReferences(schema, rowKeys, extraReferences) {
    var covered = Object.create(null);
    (extraReferences || []).forEach(function (r) { covered[r.indexField] = true; });
    var hasKey = Object.create(null);
    rowKeys.forEach(function (k) { hasKey[k] = true; });

    var display = [];  // refs with a *Name display column -> DbLookups.applyReferenceColumns
    var direct = [];   // refs without one -> lookup editor directly on the index column
    schema.properties.forEach(function (p) {
      if (p.kind !== 'reference' || !p.lookup || p.readOnly) return;
      if (covered[p.name] || !hasKey[p.name]) return;
      var base = p.name.replace(/Index$/, '');
      var displayField = base + 'Name';
      if (hasKey[displayField]) display.push({ displayField: displayField, indexField: p.name, lookup: p.lookup });
      else direct.push({ indexField: p.name, lookup: p.lookup });
    });
    return { display: display, direct: direct };
  }

  // Long or multi-line text (NPCPage.Say, quest texts, descriptions). A single-line <input> drops CR/LF, so these
  // columns get a textarea editor. Server hint `multiline` wins; otherwise any value with a line break or over 80 chars.
  var LONG_TEXT_CHARS = 80;
  function isLongText(p, rows) {
    if (!p || p.kind !== 'string') return false;
    if (p.multiline === true) return true;
    if (p.multiline === false) return false;
    for (var i = 0; rows && i < rows.length; i++) {
      var v = rows[i] ? rows[i][p.name] : null;
      if (typeof v === 'string' && (v.length > LONG_TEXT_CHARS || /[\r\n]/.test(v))) return true;
    }
    return false;
  }

  // A textarea returns LF. Put CRLF back when the stored text used it, so that opening and closing the editor is
  // not a change and the game data keeps its line-ending format.
  function keepLineEndings(oldValue, newValue) {
    var v = newValue == null ? '' : String(newValue);
    return /\r\n/.test(oldValue == null ? '' : String(oldValue)) ? v.replace(/\r?\n/g, '\r\n') : v;
  }

  // Dropdown values with the current value kept: a value that is not a list entry (a [Flags] combination such as
  // "WarWizTao, Berserker", or an undefined number) goes first, so opening the editor never falls back to values[0].
  function valuesWithCurrent(values, current) {
    if (current == null || current === '') return values;
    var s = String(current);
    return values.indexOf(s) >= 0 ? values : [s].concat(values);
  }

  // ---- numbers: whole numbers and the range of the schema type -------------------------------------------------------

  var INT_RANGE = {
    Byte: [0, 255], SByte: [-128, 127], Int16: [-32768, 32767], UInt16: [0, 65535],
    Int32: [-2147483648, 2147483647], UInt32: [0, 4294967295],
    Int64: [-9007199254740991, 9007199254740991], UInt64: [0, 9007199254740991]
  };

  /** -> { ok, value, error } for a number typed into a column of schema type `type`. */
  function parseNumber(type, raw) {
    if (raw === '' || raw == null) return { ok: false, error: 'a number is required' };
    var n = Number(raw);
    if (!isFinite(n)) return { ok: false, error: '"' + raw + '" is not a number' };
    var range = INT_RANGE[type];
    if (range) {
      if (Math.floor(n) !== n) return { ok: false, error: '"' + raw + '" is not a whole number (' + type + ')' };
      if (n < range[0] || n > range[1]) return { ok: false, error: n + ' is outside ' + range[0] + '..' + range[1] + ' (' + type + ')' };
    }
    return { ok: true, value: n };
  }

  // ---- [Flags] enums --------------------------------------------------------------------------------------------------

  function memberList(p) {
    if (Array.isArray(p.members) && p.members.length) return p.members;
    return (p.enumValues || []).map(function (n, i) { return { name: n, value: i }; });
  }

  function isSingleBit(v) { return v > 0 && (v & (v - 1)) === 0; }

  // MirCraft rows carry enums as NUMBERS (System.Text.Json default); the schema publishes the member names (enumValues) and,
  // where given, { name, value } members. These map between the two; the grid / drawer show and pick names and write the
  // number back when the row held a number.
  function enumName(p, value) {
    if (value == null || value === '') return '';
    var n = Number(value);
    var members = memberList(p);
    for (var i = 0; i < members.length; i++) if (Number(members[i].value) === n) return members[i].name;
    return String(value);
  }
  function enumNumber(p, text) {
    if (text == null || text === '') return NaN;
    var t = String(text).trim();
    if (/^-?\d+$/.test(t)) return Number(t);
    var members = memberList(p);
    for (var i = 0; i < members.length; i++) if (members[i].name === t) return Number(members[i].value);
    var lower = t.toLowerCase();
    for (var j = 0; j < members.length; j++) if (String(members[j].name).toLowerCase() === lower) return Number(members[j].value);
    return NaN;
  }
  // Grid column: show names for a numeric enum value, write the number back (a name-valued row keeps names).
  function numericEnumColumn(col, p) {
    var field = p.name;
    col.valueGetter = function (params) {
      var v = params.data ? params.data[field] : null;
      if (typeof v !== 'number') return v;
      return p.flags ? flagsText(p, v) : enumName(p, v);
    };
    col.valueSetter = function (params) {
      if (!params.data) return false;
      var old = params.data[field];
      var nv = params.newValue;
      if (typeof old === 'number') {
        var n = p.flags ? flagsValue(p, nv) : enumNumber(p, nv);
        if (n == null || isNaN(n) || n === old) return false;
        params.data[field] = n;
        return true;
      }
      if (nv === old) return false;
      params.data[field] = nv;
      return true;
    };
  }

  /** "Warrior, Wizard" (or a member name, or a number in text) -> the combined value; NaN for an unknown name. */
  function flagsValue(p, text) {
    if (text == null || text === '') return 0;
    if (/^-?\d+$/.test(String(text).trim())) return Number(text);
    var byName = Object.create(null);
    memberList(p).forEach(function (m) { byName[m.name.toLowerCase()] = m.value; });
    var total = 0;
    var parts = String(text).split(',');
    for (var i = 0; i < parts.length; i++) {
      var name = parts[i].trim().toLowerCase();
      if (!name) continue;
      if (!(name in byName)) return NaN;
      total |= byName[name];
    }
    return total;
  }

  /** The combined value -> its name: a member whose value it is exactly, else the single-bit members joined by ", ". */
  function flagsText(p, value) {
    var members = memberList(p);
    for (var i = 0; i < members.length; i++) if (members[i].value === value) return members[i].name;
    var names = members.filter(function (m) { return isSingleBit(m.value) && (value & m.value) === m.value; }).map(function (m) { return m.name; });
    return names.length ? names.join(', ') : String(value);
  }

  // Checkbox-list popup editor for a [Flags] enum. Returns the original text when the bits did not change, so opening
  // and closing it is not an edit.
  function FlagsCellEditor() {}
  FlagsCellEditor.prototype.init = function (params) {
    var p = params.prop || {};
    this.prop = p;
    this.original = params.value;
    this.bits = flagsValue(p, params.value);
    if (isNaN(this.bits)) this.bits = 0;
    this.startBits = this.bits;
    var self = this;
    var gui = document.createElement('div');
    gui.className = 'db-flags-editor';
    var list = document.createElement('div');
    list.className = 'db-flags-list';
    var boxes = [];
    memberList(p).forEach(function (m) {
      if (!isSingleBit(m.value)) return;
      var lab = document.createElement('label');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = (self.bits & m.value) === m.value;
      cb.addEventListener('change', function () {
        if (cb.checked) self.bits |= m.value; else self.bits &= ~m.value;
        sync();
      });
      boxes.push({ cb: cb, value: m.value });
      lab.appendChild(cb);
      lab.appendChild(document.createTextNode(' ' + m.name));
      list.appendChild(lab);
    });
    var quick = document.createElement('div');
    quick.className = 'db-flags-quick';
    memberList(p).forEach(function (m) {
      if (isSingleBit(m.value)) return;
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'mir-btn';
      b.textContent = m.name;
      b.title = 'Set exactly ' + m.name;
      b.addEventListener('click', function () { self.bits = m.value; sync(); });
      quick.appendChild(b);
    });
    var shown = document.createElement('div');
    shown.className = 'db-flags-value mir-muted';
    function sync() {
      boxes.forEach(function (x) { x.cb.checked = (self.bits & x.value) === x.value; });
      shown.textContent = flagsText(p, self.bits);
    }
    sync();
    gui.appendChild(list);
    if (quick.children && quick.children.length) gui.appendChild(quick);
    gui.appendChild(shown);
    this.gui = gui;
    this.boxes = boxes;
  };
  FlagsCellEditor.prototype.getGui = function () { return this.gui; };
  FlagsCellEditor.prototype.isPopup = function () { return true; };
  FlagsCellEditor.prototype.getValue = function () {
    if (this.bits === this.startBits) return this.original;
    return flagsText(this.prop, this.bits);
  };
  FlagsCellEditor.prototype.afterGuiAttached = function () {
    if (this.boxes[0] && this.boxes[0].cb.focus) this.boxes[0].cb.focus();
  };

  // ---- column rules by type ---------------------------------------------------------------------------------------------

  function applyKind(col, p, rows) {
    if (p.kind === 'string' && isLongText(p, rows)) {
      col.cellEditor = 'agLargeTextCellEditor';
      col.cellEditorPopup = true;
      col.cellEditorParams = { maxLength: 100000, rows: 14, cols: 72 };   // the editor's default maxLength is 200
      col.valueParser = function (params) { return keepLineEndings(params.oldValue, params.newValue); };
      col.tooltipField = p.name;
      col.minWidth = 240;
      col.maxWidth = 320;
    } else if (p.kind === 'string') {
      col.tooltipField = p.name;
      col.minWidth = 80;
      col.maxWidth = 260;
    } else if (p.kind === 'number') {
      col.cellEditor = 'agNumberCellEditor';
      // No precision / min / max on the input: an out-of-range or fractional entry would read back as "no value"
      // (input validity) and the parser could not say why. parseNumber enforces both, with its own message.
      col.cellEditorParams = {};
      col.filter = 'agNumberColumnFilter';
      col.type = 'rightAligned';
      col.minWidth = 56;
      col.maxWidth = 110;
      col.valueParser = function (params) {
        var r = parseNumber(p.type, params.newValue);
        if (r.ok) return r.value;
        if (global.portalToast) global.portalToast(headerName(p.name) + ': ' + r.error + '. The value was not changed.', 'error');
        return params.oldValue;
      };
    } else if (p.kind === 'bool') {
      col.cellRenderer = 'agCheckboxCellRenderer';
      col.cellEditor = 'agCheckboxCellEditor';
      col.minWidth = 44;
      col.maxWidth = 64;
      addCellClass(col, 'db-cell-center');
    } else if (p.kind === 'enum' && p.flags) {
      col.cellEditor = FlagsCellEditor;
      col.cellEditorPopup = true;
      col.cellEditorParams = { prop: p };
      numericEnumColumn(col, p);
      col.minWidth = 70;
      col.maxWidth = 180;
      col.tooltipField = p.name;
    } else if (p.kind === 'enum') {
      var values = p.enumValues || [];
      if (values.length > 25 && global.DbLookups) {
        col.cellEditor = global.DbLookups.SearchableLookupCellEditor;
        col.cellEditorParams = { options: values.map(function (v, i) { return { index: i, label: v }; }) };
        col.singleClickEdit = true;
      } else {
        col.cellEditor = 'agSelectCellEditor';
        col.cellEditorParams = function (params) { return { values: valuesWithCurrent(values, params.value) }; };
      }
      numericEnumColumn(col, p);
      col.minWidth = 70;
      col.maxWidth = 180;
    } else if (p.kind === 'datetime') {
      col.valueFormatter = function (params) { return formatDateValue(params.value); };
      col.maxWidth = 170;
    } else if (p.kind === 'timespan') {
      col.maxWidth = 120;
    } else if (p.kind === 'point') {
      var field = p.name;
      col.valueGetter = function (params) {
        var v = params.data ? params.data[field] : null;
        if (v && typeof v === 'object') return (v.X != null ? v.X : 0) + ', ' + (v.Y != null ? v.Y : 0);
        return v == null ? '' : String(v);
      };
      col.valueSetter = function (params) {
        var m = String(params.newValue == null ? '' : params.newValue).match(/^\s*(-?\d+)\s*[,;\s]\s*(-?\d+)\s*$/);
        if (!m) return false;
        params.data[field] = { X: parseInt(m[1], 10), Y: parseInt(m[2], 10) };
        return true;
      };
      col.maxWidth = 110;
    } else if (p.kind === 'points' || p.kind === 'geometry' || p.kind === 'unsupported') {
      // Point lists travel as text (a Map Region's PointRegion can be 100k+ chars): cap the auto-sized width.
      col.maxWidth = 320;
    }
  }

  // Lookup editor straight on an FK index column (no *Name display column).
  function directLookupCol(col, ref, lookup) {
    var c = Object.assign({}, col, {
      editable: true,
      singleClickEdit: true,
      cellEditor: global.DbLookups.SearchableLookupCellEditor,
      cellEditorParams: global.DbLookups.optionsFor ? global.DbLookups.optionsFor(ref, lookup) : { options: lookup.options },
      valueGetter: function (p) {
        var idx = p.data ? p.data[ref.indexField] : null;
        if (idx != null && lookup.byIndex[idx]) return lookup.byIndex[idx].label;
        return idx == null || idx === 0 ? '' : '#' + idx;
      },
      valueSetter: function (p) {
        var label = p.newValue == null ? '' : String(p.newValue);
        if (!label) { p.data[ref.indexField] = 0; return true; }
        var opt = lookup.byLabel[label];
        if (!opt) return false;
        p.data[ref.indexField] = opt.index;
        return true;
      },
      valueFormatter: function (p) { return p.value == null ? '' : String(p.value); },
      minWidth: 100,
      maxWidth: 220,
      tooltipValueGetter: function (p) { return p.value == null ? '' : String(p.value); }
    });
    addCellClass(c, 'db-cell-lookup');
    // App data lives under colDef.context (ag-Grid warns about unknown colDef keys).
    c.context = Object.assign({}, c.context, { goto: { lookup: ref.lookup, indexField: ref.indexField } });
    return c;
  }

  // ---- column groups for wide tables ------------------------------------------------------------------------------------

  var GROUP_MIN_COLUMNS = 18;
  var GROUP_RULES = [
    { id: 'requirements', label: 'Requirements', test: /^(Required|Need|Min(imum)?Level|MaxLevel|Level$)/ },
    { id: 'appearance', label: 'Appearance', test: /(Image|Shape|Effect|Look|Colou?r|Hue|Light|Icon|Sound|Animation|Frame|Blend|Wings|Offset)/ },
    { id: 'economy', label: 'Economy', test: /(Price|Sell|Cost|Gold|Stack|Weight|Durability|Currency|Experience|Exp$|Rate$|Reward)/ }
  ];

  function groupColumns(cols, propByName) {
    var visible = cols.filter(function (c) { return !c.hide; });
    if (visible.length < GROUP_MIN_COLUMNS) return cols;
    var top = [];
    var groups = { identity: [], requirements: [], appearance: [], economy: [], flags: [], values: [] };
    cols.forEach(function (c) {
      if (c.pinned) { top.push(c); return; }
      var name = c.field || '';
      var p = propByName[name] || (c.context && c.context.refIndexField ? propByName[c.context.refIndexField] : null);
      var kind = p ? p.kind : 'display';
      var id = null;
      for (var i = 0; i < GROUP_RULES.length && !id; i++) if (GROUP_RULES[i].test.test(name)) id = GROUP_RULES[i].id;
      if (!id) id = kind === 'bool' ? 'flags'
        : (kind === 'string' || kind === 'enum' || kind === 'reference' || kind === 'display') ? 'identity' : 'values';
      groups[id].push(c);
    });
    var LABELS = { identity: 'Identity', requirements: 'Requirements', appearance: 'Appearance', economy: 'Economy', flags: 'Flags', values: 'Values' };
    var out = top.slice();
    ['identity', 'requirements', 'appearance', 'economy', 'values', 'flags'].forEach(function (id) {
      var list = groups[id];
      if (!list.length) return;
      var shown = 0;
      out.push({
        headerName: LABELS[id],
        groupId: 'grp_' + id,
        marryChildren: true,
        openByDefault: true,
        headerClass: 'db-col-group',
        children: list.map(function (c) {
          if (c.hide) return c;
          shown++;
          // A collapsed group keeps its first three columns.
          return shown > 3 ? Object.assign({}, c, { columnGroupShow: 'open' }) : c;
        })
      });
    });
    return out;
  }

  /** Column defs flattened (column groups expanded). */
  function flatColumns(defs) {
    var out = [];
    (defs || []).forEach(function (c) {
      if (c && Array.isArray(c.children)) out = out.concat(flatColumns(c.children));
      else if (c) out.push(c);
    });
    return out;
  }

  // The schema's property order, each lookup's name column where its index column is, the other server display
  // columns after the row name, anything else at the end.
  function orderKeys(schema, rowKeys) {
    var present = Object.create(null);
    rowKeys.forEach(function (k) { present[k] = true; });
    var out = [];
    var used = Object.create(null);
    function take(k) { if (present[k] && !used[k]) { used[k] = true; out.push(k); } }
    schema.properties.forEach(function (p) {
      if (p.kind === 'reference') {
        var display = p.name.replace(/Index$/, '') + 'Name';
        take(display);
      }
      take(p.name);
    });
    // Unpaired display columns (SourceMap, DestMap ...): right after the first name column.
    var extras = rowKeys.filter(function (k) { return !used[k]; });
    var at = Math.min(out.length, 2);
    out.splice.apply(out, [at, 0].concat(extras));
    return out;
  }

  // The row's name column: "Name", the first "...Name" text property, else "Description".
  function nameColumn(schema, keys) {
    var strings = schema.properties.filter(function (p) { return p.kind === 'string' && keys.indexOf(p.name) >= 0; });
    var pick = strings.filter(function (p) { return p.name === 'Name'; })[0] ||
      strings.filter(function (p) { return /Name$/.test(p.name); })[0] ||
      strings.filter(function (p) { return p.name === 'Description'; })[0];
    return pick ? pick.name : null;
  }

  // Columns derived from another field of the row: read-only in every grid (M8: a typed Size on a point-list region was
  // accepted and stayed wrong). endpoint -> field -> header tooltip.
  var DERIVED = {
    mapregions: { Size: 'The cell count of the shape: set from PointRegion / BitRegion when the shape is saved (Maps editor > Edit regions).' }
  };

  /**
   * Returns { colDefs, gridOptions } or null (caller falls back to legacy defs).
   * opts: { extraReferences: server-provided references[], readOnly: bool, groups: false (no column groups) }
   */
  async function typedColDefs(endpoint, rows, opts) {
    opts = opts || {};
    var schema = await fetchSchema(endpoint);
    if (!schema) return null;

    // LiveDb (schema.liveDb is present only while the editor runs; absent = the original behaviour below).
    // A restart-only table is read-only as a whole; a column is editable only when the server says it is live.
    var ld = schema.liveDb && typeof schema.liveDb === 'object' ? schema.liveDb : null;
    var tableLive = !ld || ld.live !== false;
    var ro = !!opts.readOnly || !tableLive;

    var propByName = Object.create(null);
    schema.properties.forEach(function (p) { propByName[p.name] = p; });

    // Hide meta columns (e.g. __rev, the optimistic-concurrency token) — they ride
    // along in the row data for save but must never appear as editable grid columns.
    var rowKeys = (rows && rows.length ? Object.keys(rows[0]) : schema.properties.map(function (p) { return p.name; }))
      .filter(function (k) { return k.indexOf('__') !== 0; });
    var keys = orderKeys(schema, rowKeys);
    var nameKey = nameColumn(schema, keys);

    var refs = deriveReferences(schema, keys, opts.extraReferences);
    var allRefs = (opts.extraReferences || []).concat(refs.display);

    var lookups = Object.create(null);
    if (global.DbLookups && (allRefs.length || refs.direct.length)) {
      try {
        lookups = await global.DbLookups.loadForReferences(allRefs.concat(refs.direct));
      } catch (e) {
        return null; // lookups unavailable -> let caller use the legacy path
      }
    }

    var directByField = Object.create(null);
    refs.direct.forEach(function (r) { directByField[r.indexField] = r; });

    var colDefs = keys.map(function (k) {
      var p = propByName[k];
      var editable = global.LiveDbLogic
        ? global.LiveDbLogic.columnEditable({ readOnly: ro, key: k, prop: p, tableLive: tableLive, liveActive: !!ld })
        : (!ro && k !== 'Index' && !!p && !p.readOnly);
      var col = { field: k, headerName: headerName(k), minWidth: 60, editable: editable };
      if (ld && p) decorateHeader(col, p);
      if (k === 'Index') {
        Object.assign(col, { width: 64, minWidth: 56, maxWidth: 76, pinned: 'left', type: 'rightAligned', headerName: '#' });
        addCellClass(col, 'db-cell-index');
        return col;
      }
      if (!p) {
        // Enrichment display column (ItemName, MapName, ...) — read-only unless a
        // reference takes it over below.
        addCellClass(col, 'db-cell-display');
        col.minWidth = 100;
        col.maxWidth = 220;
        col.tooltipField = k;
        if (/date|login|expire/i.test(k)) col.valueFormatter = function (params) { return formatDateValue(params.value); };
        return col;
      }
      if (p.kind === 'reference') {
        var direct = directByField[k];
        if (direct && lookups[direct.lookup] && !ro) {
          var dc = directLookupCol(col, direct, lookups[direct.lookup]);
          if (!editable) dc.editable = false; // LiveDb: a restart-only reference column stays read-only
          return dc;
        }
        if (p.lookup) col.context = Object.assign({}, col.context, { goto: { lookup: p.lookup, indexField: k } });
        return col; // display-mode refs hide this column via applyReferenceColumns
      }
      applyKind(col, p, rows);
      var derived = DERIVED[endpoint] && DERIVED[endpoint][k];
      if (derived) {
        // A value the server derives from another field (MapRegion.Size = the shape's cell count): shown, never typed.
        col.editable = false;
        col.headerTooltip = derived;
        addCellClass(col, 'db-cell-display');
      }
      if (k === nameKey) {
        col.pinned = 'left';
        col.minWidth = Math.max(col.minWidth || 0, 140);
        col.maxWidth = Math.max(Math.min(col.maxWidth || 220, 220), 140);
        addCellClass(col, 'db-cell-name');
      }
      return col;
    });

    if (allRefs.length && global.DbLookups) {
      if (!ro) colDefs = global.DbLookups.applyReferenceColumns(colDefs, allRefs, lookups);
      else {
        // Read-only table: the name column still stands in for its index column (the go-to arrow uses the index).
        var idxFields = Object.create(null);
        allRefs.forEach(function (ref) { idxFields[ref.indexField] = true; });
        colDefs.forEach(function (c) { if (idxFields[c.field]) c.hide = true; });
      }
      colDefs.forEach(function (c) {
        allRefs.forEach(function (ref) {
          if (c.field !== ref.displayField) return;
          c.context = Object.assign({}, c.context, { goto: { lookup: ref.lookup, indexField: ref.indexField }, refIndexField: ref.indexField });
          var ip = propByName[ref.indexField];
          if (ld && ip) {
            // The display column (ItemName ...) stands in for the index column: it inherits that column's
            // live / restart-only state and header badge.
            if (ip.live === false) c.editable = false;
            decorateHeader(c, ip);
          }
        });
      });
    }

    if (opts.groups !== false) colDefs = groupColumns(colDefs, propByName);

    // Lookup columns set singleClickEdit on their own column; a grid-wide singleClickEdit would open (and on blur
    // commit) the editor of every other column on a plain click.
    var gridOptions = {};
    if (allRefs.length || refs.direct.length) gridOptions.stopEditingWhenCellsLoseFocus = true;
    return {
      colDefs: colDefs, gridOptions: gridOptions, schema: schema, lookups: lookups,
      liveDb: ld, tableLive: tableLive, readOnly: ro
    };
  }

  // LiveDb header badge + timing tooltip (the schema's per-property live / timing / evidence / nameKey).
  function decorateHeader(col, p) {
    if (!global.LiveDbLogic) return;
    var tip = global.LiveDbLogic.timingText(p);
    if (tip) col.headerTooltip = tip;
    var cls = global.LiveDbLogic.headerClasses(p);
    if (cls.length) col.headerClass = cls;
    if (p.nameKey) addCellClass(col, 'livedb-namekey');
  }

  global.DbSchema = {
    fetch: fetchSchema,
    typedColDefs: typedColDefs,
    isLongText: isLongText,
    keepLineEndings: keepLineEndings,
    valuesWithCurrent: valuesWithCurrent,
    headerName: headerName,
    parseNumber: parseNumber,
    flagsValue: flagsValue,
    flagsText: flagsText,
    enumName: enumName,
    enumNumber: enumNumber,
    memberList: memberList,
    FlagsCellEditor: FlagsCellEditor,
    flatColumns: flatColumns,
    orderKeys: orderKeys,
    // The schema depends on the LiveDb mode; the status poll calls this when the mode flips.
    clearCache: function () { schemaCache = Object.create(null); }
  };
})(window);
