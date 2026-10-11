/**
 * Shared ag-grid editor factory for DB-backed admin panels.
 *
 * makeDbGridEditor(opts) -> { load, show, save, applyFilter, getApi, getRows, setParentFilter, focusRow,
 *                              pendingEdits, pendingCount, endpoint }
 *
 * opts: endpoint, gridId, title, titleId?, reloadId?, saveId?, filterId?, readOnly?, entity?, typed?, formDrawer?,
 *       wireDom? (false: no DOM wiring at DOMContentLoaded), columnPriority? (legacy fallback only),
 *       compact? (one merged toolbar row above the grid: title, filter chip, tools, Reload, Apply changes (N)),
 *       dirtyKey? (DbSave dirty key; default endpoint), layoutKey? (saved column layout key; default endpoint),
 *       stats? ({ child, field }: a read-only "Stats" summary column from the child stat rows),
 *       query? (a fixed extra query, e.g. "MonsterIndex=12" for a drawer panel), register? (false: not a go-to target),
 *       height? (CSS height of the grid element),
 *       url? (string or function: the GET url instead of /api/admin/db/<endpoint>, e.g. a map's sub-route; empty: no load),
 *       saveUrl? (string or function: the LiveDb PUT url instead of /api/admin/db/<endpoint>),
 *       persistLegacy?(rows) (LiveDb off: the caller's own save instead of DbSave.persist; -> Promise {ok, clean}),
 *       columnsHook?(colDefs) -> colDefs, onRowClicked?(event)
 *
 * Loads are single-flight (two overlapping loads used to build two grids in one panel), show() keeps the rows of a
 * grid that has unsaved edits, Reload asks before it throws edits away, and every edit is logged per row and field
 * (before -> after) so the save button can say "Apply changes (3)" and the Changes list can show them.
 */
(function (global) {
  function defaultColDef() {
    return {
      sortable: true, filter: true, resizable: true, editable: true, floatingFilter: true, minWidth: 56,
      // Headers wrap onto two short lines instead of widening the column (auto-size skips the header).
      wrapHeaderText: true, autoHeaderHeight: true
    };
  }

  function displayFields() {
    return global.DbRegistry ? global.DbRegistry.DISPLAY_FIELDS : ['ItemName', 'MonsterName', 'MapName', 'RegionName', 'NPCName'];
  }

  function formatGridDate(value) {
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

  // Legacy fallback (no schema): row keys, Index and the display columns first.
  function colDefsFromRows(rows, priority) {
    var display = displayFields();
    priority = (priority || []).concat(['Index', 'Name']).concat(display);
    if (!rows.length) return [{ field: 'Index' }];
    var keys = Object.keys(rows[0]).filter(function (k) { return k.indexOf('__') !== 0; });
    keys.sort(function (a, b) {
      var ia = priority.indexOf(a), ib = priority.indexOf(b);
      if (ia < 0) ia = 999;
      if (ib < 0) ib = 999;
      if (ia !== ib) return ia - ib;
      return a.localeCompare(b);
    });
    return keys.map(function (k) {
      var ro = display.indexOf(k) >= 0;
      var col = { field: k, editable: k !== 'Index' && !ro };
      if (global.DbSchema && DbSchema.headerName) col.headerName = DbSchema.headerName(k);
      if (/date|login|expire/i.test(k)) col.valueFormatter = function (p) { return formatGridDate(p.value); };
      if (ro) col.cellClass = 'db-cell-display';
      return col;
    });
  }

  // ---- saved column layout + density (per viewer, localStorage; never required) -----------------------------------

  var Layout = global.DbGridLayout = global.DbGridLayout || {
    key: function (k) { return 'mircraft_grid_cols_' + k; },
    load: function (k) {
      try { var s = global.localStorage && global.localStorage.getItem(Layout.key(k)); return s ? JSON.parse(s) : null; } catch (e) { return null; }
    },
    save: function (k, state) {
      try { if (global.localStorage) global.localStorage.setItem(Layout.key(k), JSON.stringify(state)); } catch (e) { /* storage off */ }
    },
    reset: function (k) {
      try { if (global.localStorage) global.localStorage.removeItem(Layout.key(k)); } catch (e) { /* storage off */ }
    },
    density: function (value) {
      var key = 'mircraft_grid_density';
      if (value) {
        try { if (global.localStorage) global.localStorage.setItem(key, value); } catch (e) { /* storage off */ }
      } else {
        try { value = (global.localStorage && global.localStorage.getItem(key)) || 'compact'; } catch (e) { value = 'compact'; }
      }
      if (typeof document !== 'undefined' && document.body && document.body.classList) {
        document.body.classList.toggle('db-density-comfortable', value === 'comfortable');
      }
      return value;
    }
  };

  // Every grid editor by endpoint, so a go-to link can focus a row in a special editor section (Magic, NPCs, ...).
  var Editors = global.DbGridEditors = global.DbGridEditors || Object.create(null);

  // ---- "go to" arrow on lookup cells ----------------------------------------------------------------------------------

  function gotoTarget(colDef, data) {
    var g = colDef && colDef.context && colDef.context.goto;
    if (!g || !data) return null;
    var idx = Number(data[g.indexField]);
    if (!(idx > 0)) return null;
    var endpoint = global.DbRegistry ? DbRegistry.homeOfLookup(g.lookup) : g.lookup;
    return endpoint ? { endpoint: endpoint, index: idx } : null;
  }

  function gotoRenderer(params) {
    var wrap = document.createElement('span');
    wrap.className = 'db-goto-cell';
    var text = document.createElement('span');
    text.className = 'db-goto-text';
    text.textContent = params.valueFormatted != null ? params.valueFormatted : (params.value == null ? '' : String(params.value));
    wrap.appendChild(text);
    var t = gotoTarget(params.colDef, params.data);
    if (t && global.DbNav) {
      var a = document.createElement('a');
      a.className = 'db-goto';
      a.href = '#';
      a.textContent = '↗';
      a.title = 'Open #' + t.index + ' in ' + (global.DbRegistry ? DbRegistry.label(t.endpoint) : t.endpoint) + ' (or Ctrl+click the cell)';
      var stop = function (e) { e.stopPropagation(); if (e.type === 'click') { e.preventDefault(); DbNav.goto(t.endpoint, t.index); } };
      a.addEventListener('mousedown', stop);
      a.addEventListener('click', stop);
      wrap.appendChild(a);
      // A region link also gets "show on the map": the Maps editor selects the region's map and highlights it (4.3, WS4).
      if (t.endpoint === 'mapregions' && global.MapEditorPage && global.MapEditorPage.selectRegion) {
        var m = document.createElement('a');
        m.className = 'db-goto db-goto-map';
        m.href = '#';
        m.textContent = '⌖';
        m.title = 'Show region #' + t.index + ' on its map (Maps editor)';
        var stopMap = function (e) {
          e.stopPropagation();
          if (e.type !== 'click') return;
          e.preventDefault();
          if (portalShowSection('map-editor') === false) return;
          global.MapEditorPage.selectRegion(t.index);
        };
        m.addEventListener('mousedown', stopMap);
        m.addEventListener('click', stopMap);
        wrap.appendChild(m);
      }
    }
    return wrap;
  }

  function addGotoRenderers(defs) {
    (defs || []).forEach(function (c) {
      if (c && Array.isArray(c.children)) addGotoRenderers(c.children);
      else if (c && c.context && c.context.goto && !c.cellRenderer && !c.hide) c.cellRenderer = gotoRenderer;
    });
  }

  // ---- the "Stats" summary column ---------------------------------------------------------------------------------------

  async function loadStatsSummary(stats) {
    var by = Object.create(null);
    try {
      var r = await portalApi('/api/admin/db/' + encodeURIComponent(stats.child));
      if (!r.ok) return by;
      var j = await r.json().catch(function () { return {}; });
      (j.rows || []).forEach(function (s) {
        var k = String(s[stats.field]);
        var amount = Number(s.Amount);
        var txt = (s.Stat != null ? s.Stat : '?') + (isFinite(amount) ? ' ' + (amount >= 0 ? '+' : '') + amount : '');
        (by[k] = by[k] || []).push(txt);
      });
    } catch (e) { /* a summary is optional */ }
    return by;
  }

  function statsColumn(byParent) {
    return {
      colId: '__stats', headerName: 'Stats', editable: false, sortable: false, filter: true, minWidth: 120, maxWidth: 260,
      cellClass: 'db-cell-display',
      valueGetter: function (p) { var l = p.data ? byParent[String(p.data.Index)] : null; return l ? l.join(' · ') : ''; },
      tooltipValueGetter: function (p) { var l = p.data ? byParent[String(p.data.Index)] : null; return l ? l.join('\n') : ''; }
    };
  }

  // ---- LiveDb (MirCraft): header badges from the schema's liveClass / liveTiming / liveNote ----------------------------
  // Only while the server's [LiveDb] Enabled is on (global.MirLiveDb, set by admin-gamedata.js from /api/admin/livedb/status).
  var LIVE_TIMING = {
    now: 'Live: takes effect in the running world as soon as it is applied.',
    hook: 'Live: applied to the running world, then a refresh runs (for example spawned monsters are updated over the next ticks).',
    spawn: 'Live: used from the next spawn / next map entry.',
    clients: 'Live: sent to connected clients.',
    restart: 'Restart-only: saved to the database now; the running world uses it after the next world restart.'
  };

  function liveTip(p) {
    if (!p || !p.liveClass) return '';
    var t = LIVE_TIMING[p.liveTiming] || (p.liveClass === 'RestartOnly' ? LIVE_TIMING.restart : LIVE_TIMING.now);
    return p.liveNote ? t + ' (' + p.liveNote + ')' : t;
  }

  function liveActive() { return !!(global.MirLiveDb && global.MirLiveDb.enabled && global.MirLiveDb.enabled()); }

  /** Adds gd-h-live / gd-h-restart + a timing tooltip to every column the schema classifies (recursing into groups). */
  function decorateLiveHeaders(defs, schema) {
    if (!schema || !Array.isArray(schema.properties)) return defs;
    var props = Object.create(null);
    schema.properties.forEach(function (p) { props[p.name] = p; });
    (function walk(list) {
      (list || []).forEach(function (c) {
        if (!c) return;
        if (Array.isArray(c.children)) { walk(c.children); return; }
        var key = (c.context && c.context.refIndexField) || c.field;
        var p = key ? props[key] : null;
        if (!p || !p.liveClass || p.readOnly) return;
        var cls = p.liveClass === 'RestartOnly' ? 'gd-h-restart' : 'gd-h-live';
        c.headerClass = [].concat(c.headerClass || [], cls);
        var tip = liveTip(p);
        c.headerTooltip = c.headerTooltip ? c.headerTooltip + '\n' + tip : tip;
      });
    })(defs);
    return defs;
  }

  global.LiveDbText = { tip: liveTip, timing: LIVE_TIMING, decorate: decorateLiveHeaders, active: liveActive };

  function sameValue(a, b) {
    if (a === b) return true;
    if (a == null || b == null) return (a == null || a === '') && (b == null || b === '');
    if (typeof a === 'object' || typeof b === 'object') return JSON.stringify(a) === JSON.stringify(b);
    return String(a) === String(b);
  }

  global.makeDbGridEditor = function (opts) {
    var api = null;
    var allRows = [];
    var ro = !!opts.readOnly;     // effective: also true for a restart-only table while LiveDb runs
    var lastQuery = null;
    var typedInfo = null;
    var dirtyKey = opts.dirtyKey || opts.endpoint;
    var layoutKey = opts.layoutKey || opts.endpoint;
    var parentFilter = null;      // { field, value, label }
    var toolCtx = null;
    var ui = {};                  // compact toolbar elements
    // LiveDb changed-rows baseline for THIS grid (inert while LiveDb is off).
    var tracker = (global.DbSave && global.DbSave.track) ? global.DbSave.track() : null;
    // Pending edits: Index -> { label, fields: { field: { before, after } } }.
    var edits = Object.create(null);

    function setDirty(on) {
      if (global.DbSave && global.DbSave.setDirty) global.DbSave.setDirty(dirtyKey, on);
      else global.__gridDirty = on;
    }
    function isDirty() { return !!(global.DbSave && global.DbSave.isDirty && global.DbSave.isDirty(dirtyKey)); }

    function rowLabel(data) {
      if (!data) return '';
      return data.ItemName || data.MonsterName || data.Name || data.NPCName || data.QuestName || data.Description || data.SetName || '';
    }

    function noteEdit(data, field, before, after) {
      if (!data || !field) return;
      var k = String(data.Index);
      var e = edits[k] || (edits[k] = { index: data.Index, label: rowLabel(data), fields: Object.create(null) });
      var f = e.fields[field];
      if (!f) e.fields[field] = { before: before, after: after };
      else f.after = after;
      if (e.fields[field] && sameValue(e.fields[field].before, e.fields[field].after)) delete e.fields[field];
      if (!Object.keys(e.fields).length) delete edits[k];
      refreshCount();
    }

    function pendingEdits() {
      var out = Object.keys(edits).map(function (k) { return edits[k]; });
      if (api) api.forEachNode(function (n) {
        if (n.data && !(Number(n.data.Index) > 0)) out.push({ index: n.data.Index, label: rowLabel(n.data), added: true, fields: {} });
      });
      return out;
    }

    function pendingCount() { return pendingEdits().length; }

    function saveLabelBase() {
      var live = global.LiveDbInfo && global.LiveDbInfo.isActive && global.LiveDbInfo.isActive();
      return live ? 'Apply changes (live)' : 'Apply rows + Save DB';
    }

    function refreshCount() {
      if (destroyed) return;
      var n = api ? pendingCount() : 0;
      var btn = ui.save || (opts.saveId ? document.getElementById(opts.saveId) : null);
      if (btn && !btn.__busy) {
        // The compact toolbar's button is ours; a section's own button ("Apply crafts (live)") keeps its label and
        // only gets the count appended.
        var base = ui.save ? saveLabelBase() : String(btn.textContent || '').replace(/ \(\d+\)$/, '');
        btn.textContent = base + (n ? ' (' + n + ')' : '');
        if (btn.classList) btn.classList.toggle('db-has-pending', n > 0);
      }
      if (ui.meta) ui.meta.textContent = allRows.length + ' rows' + (n ? ' · ' + n + ' changed' : '');
      if (ui.changes) { ui.changes.textContent = 'Changes' + (n ? ' (' + n + ')' : ''); ui.changes.disabled = !n; }
    }

    function setTitle(text) {
      var title = opts.titleId ? document.getElementById(opts.titleId) : null;
      if (title) title.textContent = text;
      if (ui.title) ui.title.textContent = opts.title || opts.endpoint;
    }

    function applyFilter() {
      if (!api) return;
      var fid = opts.filterId;
      var f = (fid && document.getElementById(fid)) ? document.getElementById(fid).value : '';
      f = String(f || '').trim().toLowerCase();
      if (!f) {
        api.setGridOption('rowData', allRows);
        return;
      }
      api.setGridOption('rowData', allRows.filter(function (r) {
        return Object.keys(r).some(function (k) {
          var v = r[k];
          return v != null && String(v).toLowerCase().indexOf(f) >= 0;
        });
      }));
    }

    function renderChip() {
      if (!ui.chip) return;
      ui.chip.innerHTML = '';
      ui.chip.style.display = parentFilter ? '' : 'none';
      if (!parentFilter) return;
      var text = document.createElement('span');
      text.textContent = parentFilter.label || (parentFilter.field + ' = ' + parentFilter.value);
      var x = document.createElement('button');
      x.type = 'button';
      x.className = 'db-chip-x';
      x.textContent = '×';
      x.title = 'Show all rows';
      x.onclick = function () { setParentFilter(null); };
      ui.chip.appendChild(text);
      ui.chip.appendChild(x);
    }

    /** Show only the rows whose `field` equals `value` (a child table opened from its parent); null shows all. */
    function setParentFilter(f) {
      parentFilter = f && f.field ? { field: f.field, value: Number(f.value), label: f.label } : null;
      renderChip();
      if (api && api.onFilterChanged) api.onFilterChanged();
    }

    function focusRow(index) {
      if (!api) return false;
      var node = api.getRowNode(String(index));
      if (!node) { portalToast('Row #' + index + ' is not in ' + (opts.title || opts.endpoint) + '.', 'info'); return false; }
      if (node.displayed === false) {
        if (parentFilter && Number(node.data[parentFilter.field]) !== parentFilter.value) setParentFilter(null);
        if (toolCtx && toolCtx.clearSearch) toolCtx.clearSearch();
      }
      if (api.ensureNodeVisible) api.ensureNodeVisible(node, 'middle');
      if (node.setSelected) node.setSelected(true, true);
      if (api.flashCells) api.flashCells({ rowNodes: [node] });
      if (api.setFocusedCell && node.rowIndex != null) api.setFocusedCell(node.rowIndex, 'Index');
      return true;
    }

    var layoutPaused = false;
    function saveLayout() {
      if (!api || !api.getColumnState || layoutPaused) return;
      clearTimeout(saveLayout.t);
      saveLayout.t = setTimeout(function () { Layout.save(layoutKey, api.getColumnState()); }, 300);
    }

    // Single-flight: overlapping loads share one request and never build a second grid.
    var inflight = null;
    function load(extraQuery, keepEdits) {
      // A reload that has edits to put back (LiveDb save with rows hidden by the quick filter) must not be merged into a
      // plain load already in flight: it runs after it.
      if (inflight && keepEdits && keepEdits.length) {
        return inflight.then(function () { return load(extraQuery, keepEdits); }, function () { return load(extraQuery, keepEdits); });
      }
      if (!inflight) {
        inflight = loadCore(extraQuery, keepEdits).then(
          function (v) { inflight = null; return v; },
          function (e) { inflight = null; throw e; });
      }
      return inflight;
    }

    /** Show the grid: build it the first time; keep its rows while it has unsaved edits; otherwise refresh them. */
    function show() {
      if (api && isDirty()) { refreshCount(); return Promise.resolve(); }
      return load(lastQuery);
    }

    async function loadCore(extraQuery, keepEdits) {
      var el = document.getElementById(opts.gridId);
      if (!el || typeof agGrid === 'undefined') return;
      if (opts.height) el.style.height = opts.height;
      setTitle(opts.title + ' (loading…)');
      var url = opts.url ? (typeof opts.url === 'function' ? opts.url() : opts.url) : '/api/admin/db/' + opts.endpoint;
      if (!url) { setTitle(opts.title); return; }
      var q = [opts.query, extraQuery].filter(Boolean).join('&');
      if (q) url += (url.indexOf('?') >= 0 ? '&' : '?') + q;
      var r = await portalApi(url);
      var j = await r.json().catch(function () { return {}; });
      if (!r.ok) {
        setTitle(opts.title);
        portalToast(j.message || j.error || r.statusText || ('HTTP ' + r.status), 'error');
        return;
      }
      allRows = j.rows || [];
      lastQuery = extraQuery || null;
      setTitle(opts.title + ' — ' + allRows.length + ' rows');
      if (api) {
        if (opts.stats) await refreshStats();
        if (tracker) tracker.reset(allRows);
        var kept = Object.create(null);
        (keepEdits || []).forEach(function (row) { kept[String(row.Index)] = true; });
        Object.keys(edits).forEach(function (k) { if (!kept[k]) delete edits[k]; });
        if (!(keepEdits && keepEdits.length)) setDirty(false);
        // LiveDb: edits of rows the quick filter hid during a save are put back on the fresh rows (baseline = server data,
        // so they stay "dirty" and the next Save sends them).
        if (keepEdits && keepEdits.length && global.LiveDbLogic) {
          var back = global.LiveDbLogic.reapplyEdits(allRows, keepEdits);
          if (back.dropped.length) portalToast('Row(s) ' + back.dropped.map(function (i) { return '#' + i; }).join(', ') + ' were changed on the server meanwhile; your hidden edit of them was dropped.', 'error');
        }
        applyFilter();
        refreshCount();
        return;
      }
      var typed = null;
      if (opts.typed !== false && global.DbSchema) {
        try { typed = await DbSchema.typedColDefs(opts.endpoint, allRows, { readOnly: !!opts.readOnly, extraReferences: j.references || [] }); } catch (e) { typed = null; }
      }
      if (api) { applyFilter(); return; }   // safety net: a grid appeared meanwhile
      typedInfo = typed;
      // LiveDb: a restart-only table is read-only as a whole (no Save/Add/Delete) with a banner saying why.
      ro = !!opts.readOnly || !!(typed && typed.tableLive === false);
      var columnDefs = typed ? typed.colDefs : colDefsFromRows(allRows, opts.columnPriority);
      if (typed && typed.schema && liveActive()) columnDefs = decorateLiveHeaders(columnDefs, typed.schema);
      if (opts.stats) {
        var byParent = statsByParent;
        var fresh = await loadStatsSummary(opts.stats);
        Object.keys(fresh).forEach(function (k) { byParent[k] = fresh[k]; });
        columnDefs = columnDefs.slice();
        // after the pinned columns
        var at = 0;
        while (at < columnDefs.length && columnDefs[at].pinned) at++;
        columnDefs.splice(at, 0, statsColumn(byParent));
      }
      if (typeof opts.columnsHook === 'function') columnDefs = opts.columnsHook(columnDefs.slice()) || columnDefs;
      addGotoRenderers(columnDefs);
      var saved = Layout.load(layoutKey);
      var gridOpts = {
        columnDefs: columnDefs,
        defaultColDef: defaultColDef(),
        rowData: allRows,
        animateRows: true,
        tooltipShowDelay: 400,
        enableBrowserTooltips: false,
        getRowId: function (p) { return String(p.data.Index); },
        onCellValueChanged: function (e) {
          setDirty(true);
          noteEdit(e.data, e.colDef && (e.colDef.field || e.colDef.colId), e.oldValue, e.newValue);
        },
        onCellClicked: function (e) {
          // Alt+click on an Image / StateImage / Shape / Icon / BuffIcon cell picks the art in the Library Viewer (WS3, §7.1).
          if (e.event && e.event.altKey && !ro && global.LibraryPicker && LibraryPicker.handleCell(opts.entity || opts.endpoint, e)) return;
          // Ctrl/Cmd+click on a lookup cell opens the row it points at.
          if (!(e.event && (e.event.ctrlKey || e.event.metaKey))) return;
          var t = gotoTarget(e.colDef, e.data);
          if (!t || !global.DbNav) return;
          if (e.api && e.api.stopEditing) e.api.stopEditing(true);
          DbNav.goto(t.endpoint, t.index);
        },
        onRowClicked: function (e) { if (typeof opts.onRowClicked === 'function') opts.onRowClicked(e); },
        isExternalFilterPresent: function () { return !!parentFilter; },
        doesExternalFilterPass: function (node) { return !parentFilter || Number(node.data[parentFilter.field]) === parentFilter.value; },
        onColumnResized: function (e) { if (e.finished && e.source !== 'autosizeColumns' && e.source !== 'api') saveLayout(); },
        onColumnMoved: function (e) { if (e.finished) saveLayout(); },
        onColumnVisible: saveLayout,
        onColumnPinned: saveLayout
      };
      // A saved layout wins over auto-size; auto-size skips the header (it wraps instead).
      if (!saved) gridOpts.autoSizeStrategy = { type: 'fitCellContents', skipHeader: true };
      if (typed && typed.gridOptions) Object.assign(gridOpts, typed.gridOptions);
      if (global.ImagePreview) {
        var hoverEntity = opts.entity || opts.endpoint;
        gridOpts.onCellMouseOver = function (e) {
          if (e.colDef) ImagePreview.onCell(hoverEntity, e.colDef.field, e.value, e.event, e.data);
        };
        gridOpts.onCellMouseOut = function () { ImagePreview.hide(); };
      }
      if (opts.formDrawer !== false && global.FormDrawer) {
        gridOpts.onRowDoubleClicked = function (e) {
          FormDrawer.open({ endpoint: opts.endpoint, entity: opts.entity || opts.endpoint, rowNode: e.node, dirtyKey: dirtyKey, readOnly: ro });
        };
      }
      if (global.DbGridTools) DbGridTools.enrichGrid(gridOpts, { readOnly: ro });
      if (el.querySelector && el.querySelector('.ag-root-wrapper')) { while (el.firstChild) el.removeChild(el.firstChild); }
      api = agGrid.createGrid(el, gridOpts);
      if (saved && api.applyColumnState) api.applyColumnState({ state: saved, applyOrder: true });
      if (ro && api.setGridOption) {
        api.setGridOption('defaultColDef', Object.assign({}, defaultColDef(), { editable: false }));
      }
      if (tracker) tracker.reset(allRows);
      if (global.DbSave && global.DbSave.registerGrid) global.DbSave.registerGrid(dirtyKey, api, { tracker: tracker });
      if (global.DbSave && global.DbSave.onEdit) {
        // The row drawer reports the fields it changed (rowNode.setData fires no cellValueChanged).
        global.DbSave.onEdit(dirtyKey, function (index, changes) {
          var node = api.getRowNode(String(index));
          (changes || []).forEach(function (c) { noteEdit(node ? node.data : { Index: index }, c.field, c.before, c.after); });
        });
      }
      if (global.DbGridTools) {
        var ld = typed ? typed.liveDb : null;
        toolCtx = {
          getApi: function () { return api; },
          endpoint: opts.endpoint,
          dirtyKey: dirtyKey,
          layoutKey: layoutKey,
          reload: function () { return load(lastQuery); },
          readOnly: ro,
          liveDb: ld,
          // Fixed defaults (drawer panels, page editor) plus the parent link of the active filter chip, so a row added
          // while "Items = Sword" is shown belongs to that item instead of vanishing with ItemIndex 0.
          newRowDefaults: function () {
            var d = Object.assign({}, typeof opts.newRowDefaults === 'function' ? opts.newRowDefaults() : (opts.newRowDefaults || {}));
            if (parentFilter) d[parentFilter.field] = parentFilter.value;
            return d;
          },
          pending: pendingEdits,
          focusRow: focusRow,
          discard: reloadAsk,
          resetLayout: function () {
            layoutPaused = true;
            clearTimeout(saveLayout.t);
            if (api.resetColumnState) api.resetColumnState();
            if (api.autoSizeAllColumns) api.autoSizeAllColumns(true);
            Layout.reset(layoutKey);
            setTimeout(function () { layoutPaused = false; }, 500);
          }
        };
        if (opts.compact) buildCompactToolbar(toolCtx, ld);
        DbGridTools.mountToolbar(el, toolCtx);
      }
      if (typed && typed.liveDb && global.LiveDbEditor) {
        LiveDbEditor.applyTabState({
          saveEl: ui.save || (opts.saveId ? document.getElementById(opts.saveId) : null),
          bannerId: 'liveDbBanner_' + opts.gridId,
          before: (el.previousElementSibling && el.previousElementSibling.classList && el.previousElementSibling.classList.contains('db-tools-bar')) ? el.previousElementSibling : el
        }, LiveDbLogic.tabState(typed.liveDb));
      }
      if (global.LiveDbInfo && global.LiveDbInfo.onChange) global.LiveDbInfo.onChange(refreshCount);
      if (opts.stats && global.DbSave && global.DbSave.onSaved) global.DbSave.onSaved(onTableSaved);
      renderChip();
      refreshCount();
    }

    // ---- the Stats summary column (opts.stats) ----
    var statsByParent = Object.create(null);
    async function refreshStats() {
      var fresh = await loadStatsSummary(opts.stats);
      Object.keys(statsByParent).forEach(function (k) { delete statsByParent[k]; });
      Object.keys(fresh).forEach(function (k) { statsByParent[k] = fresh[k]; });
      if (api && api.refreshCells) api.refreshCells({ columns: ['__stats'], force: true });
    }
    function onTableSaved(endpoint) {
      if (destroyed || !api || !opts.stats || endpoint !== opts.stats.child) return;
      refreshStats();
    }

    /** Throw the grid away (drawer panels, page-editor grids): its listeners, registry entries and dirty state go too. */
    var destroyed = false;
    function destroy() {
      if (destroyed) return;
      destroyed = true;
      if (global.LiveDbInfo && global.LiveDbInfo.offChange) global.LiveDbInfo.offChange(refreshCount);
      if (global.DbSave) {
        if (global.DbSave.offSaved) global.DbSave.offSaved(onTableSaved);
        if (global.DbSave.onEdit) global.DbSave.onEdit(dirtyKey, null);
        if (global.DbSave.unregisterGrid) global.DbSave.unregisterGrid(dirtyKey, api);
        if (global.DbSave.setDirty) global.DbSave.setDirty(dirtyKey, false);
      }
      if (Editors[opts.endpoint] === editor) delete Editors[opts.endpoint];
      if (api && api.destroy) { try { api.destroy(); } catch (e) { /* already gone */ } }
      api = null;
      allRows = [];
      edits = Object.create(null);
    }

    // Title + row count on the left, Reload / Apply changes (N) on the right of the one toolbar row.
    function buildCompactToolbar(ctx, ld) {
      var head = document.createElement('div');
      head.className = 'db-pane-head';
      ui.title = document.createElement('h3');
      ui.title.className = 'db-pane-title';
      ui.title.textContent = opts.title || opts.endpoint;
      ui.meta = document.createElement('span');
      ui.meta.className = 'db-pane-meta mir-muted';
      head.appendChild(ui.title);
      head.appendChild(ui.meta);
      ui.chip = document.createElement('span');
      ui.chip.className = 'db-chip';
      ui.chip.style.display = 'none';
      var trailing = [];
      if (ld && global.LiveDbLogic) {
        var legend = document.createElement('span');
        legend.className = 'db-legend mir-muted';
        legend.innerHTML = '<span class="db-legend-swatch"></span>restart-only column';
        legend.title = 'Columns underlined in amber take effect after a server restart; edit them with SysDbEdit while the server is stopped. Hover a header for when its change takes effect.';
        trailing.push(legend);
      }
      var reload = document.createElement('button');
      reload.type = 'button';
      reload.className = 'mir-btn';
      reload.textContent = 'Reload';
      reload.onclick = reloadAsk;
      trailing.push(reload);
      if (!ro) {
        ui.save = document.createElement('button');
        ui.save.type = 'button';
        ui.save.className = 'mir-btn primary db-save-btn';
        ui.save.onclick = save;
        trailing.push(ui.save);
      }
      ctx.leading = [head, ui.chip];
      ctx.trailing = trailing;
      ctx.compact = true;
      ctx.onChangesButton = function (b) { ui.changes = b; };
    }

    // Reload discards this grid's unsaved edits: ask first.
    async function reloadAsk() {
      if (isDirty() || pendingCount()) {
        var ok = await portalConfirm('Reload ' + (opts.title || opts.endpoint) + ' from the server? Your unsaved edits in this grid will be lost.');
        if (!ok) return;
      }
      return load(lastQuery);
    }

    async function save() {
      if (!api) return;
      if (ro) {
        portalToast(opts.readOnly ? 'Read-only grid.' : 'Restart-only table: edit it with SysDbEdit while the server is stopped.', 'error');
        return;
      }
      if (typeof api.stopEditing === 'function') api.stopEditing();   // commit a cell editor that is still open
      var btn = ui.save || (opts.saveId ? document.getElementById(opts.saveId) : null);
      if (global.LiveDbEditor && await LiveDbEditor.useLiveDb()) {
        var lres = await LiveDbEditor.saveGrid({
          endpoint: opts.endpoint,
          url: opts.saveUrl ? (typeof opts.saveUrl === 'function' ? opts.saveUrl() : opts.saveUrl) : undefined,
          dirtyKey: dirtyKey,
          label: opts.title,
          tracker: tracker,
          getRows: function () { var rs = []; api.forEachNode(function (n) { rs.push(n.data); }); return rs; },
          getAllRows: function () { return allRows; },
          busyEls: btn ? [btn] : [],
          reload: function (keep) { return load(lastQuery, keep); }
        });
        if (lres && lres.ok && !lres.noChanges && global.DbSave && global.DbSave.notifySaved) global.DbSave.notifySaved(opts.endpoint);
        refreshCount();
        return;
      }
      var rows = [];
      api.forEachNode(function (n) { rows.push(n.data); });
      var hadNew = rows.some(function (r) { return Number(r.Index) <= 0; });
      var res = typeof opts.persistLegacy === 'function' ? await opts.persistLegacy(rows) : await DbSave.persist(opts.endpoint, rows);
      if (res.ok) {
        setDirty(false); edits = Object.create(null); refreshCount();
        if (global.DbSave && global.DbSave.notifySaved) global.DbSave.notifySaved(opts.endpoint);
      }
      if (res.ok && (res.clean === false || hadNew)) load(lastQuery);
    }

    function getApi() { return api; }
    function getRows() { return allRows; }

    if (opts.wireDom !== false) {
      document.addEventListener('DOMContentLoaded', function () {
        var rl = opts.reloadId && document.getElementById(opts.reloadId);
        if (rl) rl.onclick = reloadAsk;
        var sv = opts.saveId && document.getElementById(opts.saveId);
        if (sv) sv.onclick = save;
        var ff = opts.filterId && document.getElementById(opts.filterId);
        if (ff) ff.addEventListener('input', applyFilter);
      });
    }

    var editor = {
      endpoint: opts.endpoint,
      load: load, show: show, save: save, reload: reloadAsk, applyFilter: applyFilter, getApi: getApi, getRows: getRows,
      setParentFilter: setParentFilter, getParentFilter: function () { return parentFilter; },
      focusRow: focusRow, pendingEdits: pendingEdits, pendingCount: pendingCount,
      isDirty: function () { return isDirty() || pendingCount() > 0; },
      typed: function () { return typedInfo; },
      destroy: destroy
    };
    // A go-to target from the start: a link may arrive before the first load has finished.
    if (opts.register !== false) Editors[opts.endpoint] = editor;
    return editor;
  };
})(window);
