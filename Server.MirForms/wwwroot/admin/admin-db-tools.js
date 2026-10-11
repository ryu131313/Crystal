/**
 * Shared DB-grid tools (Tier 2-4): quick search, add row, delete selected, bulk field edit,
 * JSON/CSV export, JSON import, Paste TSV / Copy TSV (§6 step 5, the desktop's Ctrl+V / Ctrl+C spreadsheet workflow).
 * Mounts a toolbar above any ag-Grid and operates on its api +
 * endpoint. Persistence reuses DbSave.persist (PUT rows -> POST save) and the audited delete
 * endpoint. All dynamic HTML is escaped.
 */
(function (global) {
  var newRowSeq = -1; // tentative negative Index for not-yet-saved rows (server creates on save)

  function esc(v) {
    if (v == null) return '';
    return String(v).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function rowsFromApi(api, selectedOnly) {
    var rows = [];
    if (selectedOnly) return api.getSelectedRows();
    api.forEachNode(function (n) { rows.push(n.data); });
    return rows;
  }

  // Columns bulk edit can set: editable, visible, not meta / Index. A display column (ItemName ...) only counts when
  // a lookup editor owns it (its valueSetter writes the FK); a read-only display column is left out.
  function flatDefs(defs) {
    var out = [];
    (defs || []).forEach(function (c) {
      if (c && Array.isArray(c.children)) out = out.concat(flatDefs(c.children));
      else if (c) out.push(c);
    });
    return out;
  }

  /** The row-selection checkbox column (app data under colDef.context: ag-Grid warns about unknown colDef keys). */
  function isSelectCol(d) { return !!(d && d.context && d.context.select); }

  function editableColumns(api) {
    var cols = [];
    flatDefs(api.getColumnDefs() || []).forEach(function (c) {
      if (!c.field || isSelectCol(c) || c.hide) return;
      if (c.field.indexOf('__') === 0 || c.field === 'Index') return;
      if (c.editable !== true && typeof c.editable !== 'function') {
        if (c.editable === false) return;
        var dce = api.getGridOption ? api.getGridOption('defaultColDef') : null;
        if (dce && dce.editable === false) return;
      }
      cols.push(c);
    });
    return cols;
  }

  function setDirty(ctx, on) {
    if (global.DbSave && DbSave.setDirty) DbSave.setDirty(ctx.dirtyKey || ctx.endpoint, on);
    else global.__gridDirty = on;
  }

  // The bulk-edit input that matches the column: lookup options, enum values, a checkbox, a number, or text.
  function bulkInput(col) {
    var p = col.cellEditorParams;
    if (typeof p === 'function') { try { p = p({ value: null }); } catch (e) { p = null; } }
    var options = null;
    if (p && Array.isArray(p.options)) options = p.options.map(function (o) { return o && o.label != null ? String(o.label) : String(o); });
    else if (p && Array.isArray(p.values)) options = p.values.map(String);
    var input;
    if (options) {
      input = document.createElement('select');
      options.forEach(function (v) { var o = document.createElement('option'); o.value = v; o.textContent = v || '(none)'; input.appendChild(o); });
      return { el: input, read: function () { return input.value; } };
    }
    if (col.cellEditor === 'agCheckboxCellEditor') {
      input = document.createElement('select');
      ['true', 'false'].forEach(function (v) { var o = document.createElement('option'); o.value = v; o.textContent = v; input.appendChild(o); });
      return { el: input, read: function () { return input.value === 'true'; } };
    }
    input = document.createElement(col.cellEditor === 'agLargeTextCellEditor' ? 'textarea' : 'input');
    if (input.tagName === 'INPUT') input.type = col.cellEditor === 'agNumberCellEditor' ? 'number' : 'text';
    else input.rows = 2;
    input.placeholder = 'new value';
    input.style.width = '180px';
    var numeric = col.cellEditor === 'agNumberCellEditor';
    return {
      el: input,
      read: function () {
        if (!numeric) return input.value;
        var n = Number(input.value);
        if (input.value === '' || !isFinite(n)) throw new Error('"' + input.value + '" is not a number');
        return n;
      }
    };
  }

  function download(name, text, mime) {
    var blob = new Blob([text], { type: mime });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url; a.download = name;
    document.body.appendChild(a); a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }

  function toCsv(rows) {
    if (!rows.length) return '';
    var keys = Object.keys(rows[0]).filter(function (k) { return k.indexOf('__') !== 0; });
    function cell(v) {
      if (v == null) return '';
      if (typeof v === 'object') v = JSON.stringify(v);
      v = String(v);
      return /[",\n]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v;
    }
    var out = [keys.join(',')];
    rows.forEach(function (r) { out.push(keys.map(function (k) { return cell(r[k]); }).join(',')); });
    // UTF-8 BOM first: without it Excel reads the file as ANSI and garbles "—" and other non-ASCII text.
    return '﻿' + out.join('\r\n');
  }

  function stripMeta(rows) {
    return rows.map(function (r) {
      var o = {};
      Object.keys(r).forEach(function (k) { if (k.indexOf('__') !== 0) o[k] = r[k]; });
      return o;
    });
  }

  // Build a default row from the endpoint schema so a new row starts usable
  // (typed defaults) instead of blank. Falls back to {} when no schema is available.
  async function schemaDefaults(endpoint) {
    var out = {};
    if (!global.DbSchema || !DbSchema.fetch) return out;
    var schema = null;
    try { schema = await DbSchema.fetch(endpoint); } catch (e) { schema = null; }
    if (!schema || !schema.properties) return out;
    schema.properties.forEach(function (p) {
      if (!p || p.name === 'Index' || p.readOnly) return;
      if (schema.liveDb && p.live === false) return;   // LiveDb: restart-only columns are not part of a live insert
      // The server publishes the values it gives a new row (e.g. RandomStatsId 255, monster speeds): use them, never 0 / '' / false.
      if (schema.defaults && Object.prototype.hasOwnProperty.call(schema.defaults, p.name)) {
        var d = schema.defaults[p.name];
        out[p.name] = d && typeof d === 'object' ? JSON.parse(JSON.stringify(d)) : d;
        return;
      }
      switch (p.kind) {
        case 'number':
        case 'reference': out[p.name] = 0; break;
        case 'bool': out[p.name] = false; break;
        case 'string': out[p.name] = ''; break;
        case 'point': out[p.name] = { X: 0, Y: 0 }; break;
        // enum / datetime / timespan: leave unset so the server's OnCreated/CLR default
        // applies. (Picking enumValues[0] would mis-default flags enums like RequiredGender,
        // whose first member is Male, not the conventional None.)
      }
    });
    return out;
  }

  // ---- popovers (Columns menu, Changes list) ---------------------------------------------------------------------------

  var openPopover = null;
  function closePopover() {
    if (openPopover && openPopover.parentNode) openPopover.parentNode.removeChild(openPopover);
    openPopover = null;
  }

  function popover(anchor, content) {
    closePopover();
    var pop = document.createElement('div');
    pop.className = 'db-popover';
    pop.appendChild(content);
    var bar = anchor.parentNode;
    while (bar && !(bar.classList && bar.classList.contains('db-tools-bar'))) bar = bar.parentNode;
    (bar || anchor.parentNode).appendChild(pop);
    if (anchor.offsetLeft != null) pop.style.left = Math.max(0, anchor.offsetLeft) + 'px';
    openPopover = pop;
    setTimeout(function () {
      if (typeof document === 'undefined' || !document.addEventListener) return;
      var onDoc = function (e) {
        if (!openPopover) { document.removeEventListener('mousedown', onDoc); return; }
        if (pop.contains && pop.contains(e.target)) return;
        if (anchor.contains && anchor.contains(e.target)) return;
        closePopover();
        document.removeEventListener('mousedown', onDoc);
      };
      document.addEventListener('mousedown', onDoc);
    }, 0);
    return pop;
  }

  // Columns menu: show / hide columns, show all, and reset the saved layout (widths, order, visibility).
  function columnsMenu(anchor, ctx) {
    var api = ctx.getApi();
    if (!api || !api.getColumns) return;
    var box = document.createElement('div');
    box.className = 'db-columns-menu';
    var filter = document.createElement('input');
    filter.type = 'search';
    filter.placeholder = 'Find a column…';
    box.appendChild(filter);
    var list = document.createElement('div');
    list.className = 'db-columns-list';
    box.appendChild(list);
    var cols = (api.getColumns() || []).filter(function (c) {
      var d = c.getColDef ? c.getColDef() : {};
      return !isSelectCol(d) && !(d.field && d.field.indexOf('__') === 0 && d.colId !== '__stats');
    });
    function draw() {
      var q = filter.value.trim().toLowerCase();
      list.innerHTML = '';
      cols.forEach(function (c) {
        var d = c.getColDef();
        var name = d.headerName || d.field || c.getColId();
        if (q && name.toLowerCase().indexOf(q) < 0 && String(d.field || '').toLowerCase().indexOf(q) < 0) return;
        var lab = document.createElement('label');
        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = c.isVisible();
        cb.addEventListener('change', function () { api.setColumnsVisible([c.getColId()], cb.checked); });
        lab.appendChild(cb);
        lab.appendChild(document.createTextNode(' ' + name));
        list.appendChild(lab);
      });
    }
    filter.addEventListener('input', draw);
    draw();
    var actions = document.createElement('div');
    actions.className = 'db-columns-actions';
    var all = btnEl('Show all');
    all.onclick = function () { api.setColumnsVisible(cols.map(function (c) { return c.getColId(); }), true); draw(); };
    var reset = btnEl('Reset layout', 'Forget the saved widths, order and hidden columns of this table');
    reset.onclick = function () {
      if (ctx.resetLayout) ctx.resetLayout();
      else {
        if (api.resetColumnState) api.resetColumnState();
        if (global.DbGridLayout) DbGridLayout.reset(ctx.layoutKey || ctx.endpoint);
      }
      closePopover();
    };
    actions.appendChild(all);
    actions.appendChild(reset);
    box.appendChild(actions);
    popover(anchor, box);
    filter.focus();
  }

  function fmtShort(v) {
    if (v == null) return '(empty)';
    var t = typeof v === 'object' ? JSON.stringify(v) : String(v);
    t = t.replace(/\r?\n/g, ' \u21b5 ');
    return t.length > 80 ? t.slice(0, 77) + '...' : t;
  }

  // Pending changes of this grid (row, field, before -> after) before saving; a row opens it in the grid.
  function changesPanel(anchor, ctx) {
    var list = ctx.pending ? ctx.pending() : [];
    var box = document.createElement('div');
    box.className = 'db-changes-panel';
    if (!list.length) {
      box.appendChild(document.createTextNode('No unsaved changes.'));
      popover(anchor, box);
      return;
    }
    var head = document.createElement('div');
    head.className = 'db-changes-head';
    head.textContent = list.length + ' changed row(s), not saved yet';
    box.appendChild(head);
    var ul = document.createElement('ul');
    list.slice(0, 200).forEach(function (e) {
      var li = document.createElement('li');
      var title = document.createElement('a');
      title.href = '#';
      title.className = 'db-changes-row';
      title.textContent = (e.added ? 'new row ' : '#') + e.index + (e.label ? ' ' + e.label : '');
      title.onclick = function (ev) { ev.preventDefault(); closePopover(); if (ctx.focusRow) ctx.focusRow(e.index); };
      li.appendChild(title);
      Object.keys(e.fields || {}).forEach(function (f) {
        var d = document.createElement('div');
        d.className = 'db-changes-field';
        d.textContent = f + ': ' + fmtShort(e.fields[f].before) + ' \u2192 ' + fmtShort(e.fields[f].after);
        li.appendChild(d);
      });
      ul.appendChild(li);
    });
    box.appendChild(ul);
    if (list.length > 200) box.appendChild(document.createTextNode('... and ' + (list.length - 200) + ' more.'));
    if (ctx.discard) {
      var discard = btnEl('Discard all (reload)');
      discard.onclick = function () { closePopover(); ctx.discard(); };
      box.appendChild(discard);
    }
    popover(anchor, box);
  }

  function btnEl(label, title) {
    var b = document.createElement('button');
    b.type = 'button'; b.className = 'mir-btn'; b.textContent = label; if (title) b.title = title;
    return b;
  }

  // ctx: { getApi, endpoint, reload, readOnly, liveDb, dirtyKey?, layoutKey?,
  //        compact?, leading?: [el], trailing?: [el], pending?(), focusRow?(index), discard?(), onChangesButton?(btn) }
  // Sets ctx.clearSearch so the owner can clear the quick search (e.g. to show a row it was asked to focus).
  function mountToolbar(gridEl, ctx) {
    if (!gridEl || gridEl.__dbToolsMounted) return;
    gridEl.__dbToolsMounted = true;

    var bar = document.createElement('div');
    bar.className = 'db-tools-bar' + (ctx.compact ? ' compact' : '');
    bar.style.cssText = 'display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:0 0 8px;position:relative';
    (ctx.leading || []).forEach(function (e) { bar.appendChild(e); });

    function btn(label, title) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'mir-btn'; b.textContent = label; if (title) b.title = title;
      return b;
    }

    var search = document.createElement('input');
    search.type = 'search'; search.placeholder = 'Quick search…'; search.style.cssText = 'min-width:200px';
    search.addEventListener('input', function () {
      var api = ctx.getApi(); if (api) api.setGridOption('quickFilterText', search.value);
    });
    bar.appendChild(search);
    ctx.clearSearch = function () {
      search.value = '';
      var api = ctx.getApi(); if (api) api.setGridOption('quickFilterText', '');
    };

    var colsBtn = btn('Columns', 'Show or hide columns; reset the saved layout');
    colsBtn.onclick = function () { columnsMenu(colsBtn, ctx); };
    bar.appendChild(colsBtn);

    var density = btn('', 'Row height and text size of the database grids');
    var setDensityLabel = function () {
      var d = global.DbGridLayout ? DbGridLayout.density() : 'compact';
      density.textContent = d === 'comfortable' ? 'Comfortable' : 'Compact';
    };
    density.onclick = function () {
      if (!global.DbGridLayout) return;
      DbGridLayout.density(DbGridLayout.density() === 'comfortable' ? 'compact' : 'comfortable');
      setDensityLabel();
      var api = ctx.getApi(); if (api && api.resetRowHeights) api.resetRowHeights();
    };
    setDensityLabel();
    bar.appendChild(density);

    if (ctx.pending) {
      var changesBtn = btn('Changes', 'The rows and fields changed in this grid and not saved yet');
      changesBtn.disabled = true;
      changesBtn.onclick = function () { changesPanel(changesBtn, ctx); };
      bar.appendChild(changesBtn);
      if (ctx.onChangesButton) ctx.onChangesButton(changesBtn);
    }

    var expJson = btn('Export JSON');
    expJson.onclick = function () {
      var api = ctx.getApi(); if (!api) return;
      download(ctx.endpoint + '.json', JSON.stringify(stripMeta(rowsFromApi(api, false)), null, 2), 'application/json');
    };
    bar.appendChild(expJson);

    var expCsv = btn('Export CSV');
    expCsv.onclick = function () {
      var api = ctx.getApi(); if (!api) return;
      download(ctx.endpoint + '.csv', toCsv(rowsFromApi(api, false)), 'text/csv');
    };
    bar.appendChild(expCsv);

    var copyBtn = btn('Copy TSV', 'Copy the selected rows (or every filtered row) as tab-separated text, to paste into a spreadsheet');
    copyBtn.onclick = function () { copyTsv(ctx); };
    bar.appendChild(copyBtn);

    if (!ctx.readOnly) {
      var addBtn = btn('Add row', 'Adds a new row pre-filled with default values; edit it and Save to create it');
      addBtn.onclick = async function () {
        var api = ctx.getApi(); if (!api) return;
        var defaults = await schemaDefaults(ctx.endpoint);
        var extra = typeof ctx.newRowDefaults === 'function' ? ctx.newRowDefaults() : (ctx.newRowDefaults || {});
        var row = Object.assign(defaults, extra, { Index: newRowSeq-- });
        api.applyTransaction({ add: [row], addIndex: 0 });
        setDirty(ctx, true);
        if (global.DbSave && DbSave.noteEdit) DbSave.noteEdit(ctx.dirtyKey || ctx.endpoint, row.Index, []);
        portalToast('New row added (Index ' + row.Index + ', temporary) with default values. Edit it and Save to create it.', 'info');
      };
      bar.appendChild(addBtn);

      var dupBtn = btn('Duplicate selected', 'Clones the selected row(s) as new rows; edit and Save to create them');
      dupBtn.onclick = function () {
        var api = ctx.getApi(); if (!api) return;
        var sel = api.getSelectedRows();
        if (!sel.length) { portalToast('Select one or more rows to duplicate (use the checkboxes).', 'info'); return; }
        var clones = sel.map(function (r) {
          var c = {};
          Object.keys(r).forEach(function (k) {
            if (k.indexOf('__') === 0) return;   // drop __rev (and any meta) so each clone saves as a new row
            var v = r[k];
            if (v && typeof v === 'object') v = JSON.parse(JSON.stringify(v)); // deep-copy Point etc.
            c[k] = v;
          });
          c.Index = newRowSeq--;                 // fresh temp Index -> server creates a new row
          return c;
        });
        api.applyTransaction({ add: clones, addIndex: 0 });
        setDirty(ctx, true);
        portalToast('Duplicated ' + clones.length + ' row(s) as new (temporary Index). Edit and Save to create.', 'ok');
      };
      bar.appendChild(dupBtn);

      var delBtn = btn('Delete selected');
      delBtn.onclick = function () { ctx.delBtn = delBtn; deleteSelected(ctx); };
      bar.appendChild(delBtn);

      var bulkBtn = btn('Bulk edit selected');
      bulkBtn.onclick = function () { bulkEdit(ctx, bar); };
      bar.appendChild(bulkBtn);

      var importBtn = btn('Import JSON', 'Import an array of rows (validated like a normal save)');
      var file = document.createElement('input');
      file.type = 'file'; file.accept = '.json,application/json'; file.style.display = 'none';
      file.addEventListener('change', function () {
        var f = file.files && file.files[0]; if (!f) return;
        var reader = new FileReader();
        reader.onload = function () { importJson(ctx, reader.result); file.value = ''; };
        reader.readAsText(f);
      });
      importBtn.onclick = function () { file.click(); };
      bar.appendChild(importBtn);
      bar.appendChild(file);

      var pasteBtn = btn('Paste TSV…', 'Paste rows copied from a spreadsheet: the first line names the columns; lookup columns take the display name');
      pasteBtn.onclick = function () { pasteTsv(ctx); };
      bar.appendChild(pasteBtn);

      // LiveDb: the server says whether new rows / deletes are possible for this table (schema.liveDb).
      var ts = (global.LiveDbLogic && ctx.liveDb) ? global.LiveDbLogic.tabState(ctx.liveDb) : null;
      if (ts && !ts.insertable) {
        addBtn.disabled = true; dupBtn.disabled = true;
        addBtn.title = dupBtn.title = ts.note || 'New rows of this table cannot be created yet (connected clients cannot receive them).';
      }
      if (ts && !ts.deletable) {
        delBtn.disabled = true;
        delBtn.title = ts.deleteNote || 'Delete is not available for this table.';
      }
    }

    if (ctx.trailing && ctx.trailing.length) {
      var end = document.createElement('span');
      end.className = 'db-tools-end';
      ctx.trailing.forEach(function (e) { end.appendChild(e); });
      bar.appendChild(end);
    }

    gridEl.parentNode.insertBefore(bar, gridEl);
  }

  async function deleteSelected(ctx) {
    var api = ctx.getApi(); if (!api) return;
    var sel = api.getSelectedRows();
    if (!sel.length) { portalToast('Select one or more rows first (use the checkboxes).', 'info'); return; }

    var newRows = sel.filter(function (r) { return r.Index <= 0; });
    var realIdx = sel.filter(function (r) { return r.Index > 0; }).map(function (r) { return r.Index; });

    if (newRows.length) api.applyTransaction({ remove: newRows });
    if (!realIdx.length) { portalToast('Removed ' + newRows.length + ' unsaved row(s).', 'ok'); return; }

    // LiveDb: dry run first; the dialog shows the guard's reasons verbatim; no database/save afterwards.
    if (global.LiveDbEditor && await LiveDbEditor.useLiveDb()) {
      var del = await LiveDbEditor.deleteRows({ endpoint: ctx.endpoint, busyEls: ctx.delBtn ? [ctx.delBtn] : [] }, sel.filter(function (r) { return r.Index > 0; }));
      if (del.needsReload && ctx.reload) ctx.reload();
      return;
    }

    // MirCraft LiveDb ([LiveDb] Enabled): the deletes go in one batch (saved at once; live where the table allows it).
    if (global.MirLiveDb && MirLiveDb.enabled && MirLiveDb.enabled() && MirLiveDb.deleteRows) {
      var applied = await MirLiveDb.deleteRows(ctx.endpoint, realIdx);
      if (applied && ctx.reload) ctx.reload();
      return;
    }

    var ok = await portalConfirm('Delete ' + realIdx.length + ' saved row(s) from ' + ctx.endpoint +
      '? The database is backed up before it is saved. The live game drops them on the next world restart.');
    if (!ok) return;

    var r = await portalApi('/api/admin/db/' + encodeURIComponent(ctx.endpoint) + '/delete', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ indexes: realIdx })
    });
    var j = await r.json().catch(function () { return {}; });
    var rejects = j.rejects || [];
    if (rejects.length) {
      portalToast('Not deleted: ' + rejects.slice(0, 4).map(function (x) { return '#' + x.index + ' ' + (x.message || x.code || ''); }).join('; ') +
        (rejects.length > 4 ? ' …' : ''), 'warn');
    }
    if (!r.ok || j.ok === false || !(j.deleted > 0)) {
      if (!rejects.length) portalToast(j.message || j.error || 'Delete failed.', 'error');
      if (ctx.reload && j.deleted > 0) ctx.reload();
      return;
    }

    var save = await portalApi('/api/admin/database/save', { method: 'POST' });
    var js = await save.json().catch(function () { return {}; });
    if (!save.ok || js.ok === false) { portalToast((js.message || js.error || 'Deleted in the editor copy but the disk save failed'), 'error'); }
    else portalToast('Deleted ' + j.deleted + ' row(s) and saved. Restart the world to drop them from the live game.', 'ok');
    if (global.AdminStatus && AdminStatus.refresh) AdminStatus.refresh();
    if (ctx.reload) ctx.reload();
  }

  function bulkEdit(ctx, bar) {
    var api = ctx.getApi(); if (!api) return;
    if (!api.getSelectedRows().length) { portalToast('Select rows to bulk-edit first.', 'info'); return; }
    if (bar.querySelector('.db-bulk-panel')) return;

    var panel = document.createElement('span');
    panel.className = 'db-bulk-panel';
    panel.style.cssText = 'display:inline-flex;gap:6px;align-items:center;margin-left:8px';

    var cols = editableColumns(api);
    if (!cols.length) { portalToast('This grid has no column that bulk edit can set.', 'info'); return; }
    var sel = document.createElement('select');
    cols.forEach(function (c) {
      var o = document.createElement('option'); o.value = c.field; o.textContent = c.headerName || c.field; sel.appendChild(o);
    });
    var slot = document.createElement('span');
    var current = null;
    function pickColumn() {
      var col = cols.filter(function (c) { return c.field === sel.value; })[0];
      current = { col: col, input: bulkInput(col) };
      slot.innerHTML = '';
      slot.appendChild(current.input.el);
    }
    sel.onchange = pickColumn;
    pickColumn();
    var apply = document.createElement('button'); apply.type = 'button'; apply.className = 'mir-btn primary'; apply.textContent = 'Apply';
    var cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'mir-btn'; cancel.textContent = 'Cancel';

    apply.onclick = function () {
      var col = current.col, field = col.field, value;
      try { value = current.input.read(); } catch (e) { portalToast(e.message, 'error'); return; }
      // setDataValue runs the column's valueSetter (a lookup column writes its FK) and fires cellValueChanged;
      // the valueParser an editor would run is applied first.
      var nodes = api.getSelectedNodes();
      var changed = 0, refused = 0;
      nodes.forEach(function (n) {
        var v = value;
        if (typeof col.valueParser === 'function') {
          v = col.valueParser({ oldValue: api.getCellValue ? api.getCellValue({ rowNode: n, colKey: field }) : n.data[field], newValue: value, data: n.data, node: n, colDef: col, api: api });
        }
        if (n.setDataValue(field, v, 'bulkEdit')) changed++; else refused++;
      });
      if (changed) setDirty(ctx, true);
      portalToast('Set ' + field + ' = ' + String(value) + ' on ' + changed + ' row(s)' + (refused ? ' (' + refused + ' unchanged or refused)' : '') + '. Save to persist.', refused ? 'info' : 'ok');
      panel.remove();
    };
    cancel.onclick = function () { panel.remove(); };

    panel.appendChild(sel); panel.appendChild(slot); panel.appendChild(apply); panel.appendChild(cancel);
    bar.appendChild(panel);
    if (current.input.el.focus) current.input.el.focus();
  }


  // ---- §6 step 5: the spreadsheet workflow (the desktop grids' Ctrl+V / Ctrl+C, A-28 / A-41 / D-05 / E-05) ---------------
  // Paste TSV: the first line names the columns (a field such as MonsterIndex, or the header the grid shows, such as Monster);
  // a row whose Index is an existing row updates it, a row without one becomes a new row. Every value goes through the grid's
  // own setters (a lookup column takes the display name and writes the FK), so the normal save (dry run, rev, validation) checks it.

  /** Pure: TSV text (Excel's clipboard format: tabs, CRLF, "quoted" cells with "" escapes and embedded newlines) to rows of cells. */
  function parseTsv(text) {
    var rows = [], row = [], cell = '', quoted = false, i = 0, s = String(text || '');
    while (i < s.length) {
      var ch = s[i];
      if (quoted) {
        if (ch === '"' && s[i + 1] === '"') { cell += '"'; i += 2; continue; }
        if (ch === '"') { quoted = false; i++; continue; }
        cell += ch; i++; continue;
      }
      if (ch === '"' && cell === '') { quoted = true; i++; continue; }
      if (ch === '\t') { row.push(cell); cell = ''; i++; continue; }
      if (ch === '\r' || ch === '\n') {
        row.push(cell); cell = '';
        rows.push(row); row = [];
        if (ch === '\r' && s[i + 1] === '\n') i++;
        i++; continue;
      }
      cell += ch; i++;
    }
    if (cell !== '' || row.length) { row.push(cell); rows.push(row); }
    return rows.filter(function (r) { return r.some(function (c) { return String(c).trim() !== ''; }); });
  }

  function normName(v) { return String(v == null ? '' : v).replace(/\s+/g, '').toLowerCase(); }

  /** Pure: header cells -> column fields (by field, then by header name; null when unknown). */
  function mapHeaders(headers, columns) {
    var byField = {}, byHeader = {}, byAlias = {};
    (columns || []).forEach(function (c) {
      if (!c || !c.field) return;
      byField[normName(c.field)] = c.field;
      if (c.headerName) byHeader[normName(c.headerName)] = c.field;
      // "Monster" for the MonsterName lookup column (the desktop grids showed the link without the suffix).
      if (/Name$/.test(c.field) && c.field.length > 4) byAlias[normName(c.field.slice(0, -4))] = c.field;
    });
    var fields = [], unknown = [];
    (headers || []).forEach(function (h) {
      var k = normName(h);
      var f = byField[k] || byHeader[k] || byAlias[k] || null;
      fields.push(f);
      if (!f && k) unknown.push(String(h).trim());
    });
    return { fields: fields, unknown: unknown };
  }

  /** Pure: a pasted cell for a column whose current value has this type ('boolean' | 'number' | other). Returns { ok, value }. */
  function convertCell(text, kind) {
    var v = String(text == null ? '' : text).trim();
    if (kind === 'boolean') {
      if (/^(true|1|yes|y|checked|x|✓)$/i.test(v)) return { ok: true, value: true };
      if (/^(false|0|no|n|unchecked|)$/i.test(v)) return { ok: true, value: false };
      return { ok: false, value: v };
    }
    if (kind === 'number') {
      var t = v.replace(/,/g, '').replace(/%$/, '');
      if (t === '') return { ok: true, value: 0 };
      var n = Number(t);
      return isFinite(n) ? { ok: true, value: n } : { ok: false, value: v };
    }
    return { ok: true, value: v };
  }

  /** Pure: rows -> TSV with a header line (cells with tab / newline / quote are quoted, like Excel). */
  function toTsv(rows, columns) {
    function cell(v) {
      if (v == null) return '';
      if (typeof v === 'object') v = JSON.stringify(v);
      v = String(v);
      return /[\t\r\n"]/.test(v) ? '"' + v.replace(/"/g, '""') + '"' : v;
    }
    var lines = [columns.map(function (c) { return cell(c.field); }).join('\t')];
    (rows || []).forEach(function (r) { lines.push(columns.map(function (c) { return cell(typeof c.get === 'function' ? c.get(r) : r[c.field]); }).join('\t')); });
    return lines.join('\r\n');
  }

  function lookupOptions(col, node) {
    var p = col && col.cellEditorParams;
    if (typeof p === 'function') { try { p = p({ data: node && node.data, value: null }); } catch (e) { p = null; } }
    return p && Array.isArray(p.options) ? p.options : null;
  }

  /** Pure: the option label a pasted lookup value means: an exact label, "#12" / "12", or the one "N — name" label; else null. */
  function resolveLookupLabel(options, text) {
    var v = String(text == null ? '' : text).trim();
    if (!options || !v) return null;
    var lower = v.toLowerCase();
    var m = /^#?(\d+)$/.exec(v);
    var hits = options.filter(function (o) {
      if (!o || o.label == null) return false;
      var l = String(o.label);
      if (l.toLowerCase() === lower) return true;
      if (m && Number(o.index) === Number(m[1])) return true;
      var dash = l.indexOf(' — ');
      return dash > 0 && l.substring(dash + 3).toLowerCase() === lower;
    });
    return hits.length === 1 ? String(hits[0].label) : null;
  }

  function lookupLabelFor(col, node, value) {
    var opts = lookupOptions(col, node);
    return opts ? resolveLookupLabel(opts, value) : null;
  }

  function kindOf(col, sample) {
    if (col && col.cellEditor === 'agCheckboxCellEditor') return 'boolean';
    if (col && (col.cellEditor === 'agNumberCellEditor' || col.filter === 'agNumberColumnFilter')) return 'number';
    if (typeof sample === 'boolean') return 'boolean';
    if (typeof sample === 'number') return 'number';
    return 'text';
  }

  async function pasteTsv(ctx) {
    var api = ctx.getApi(); if (!api) return;
    var v = await portalDialog({
      title: 'Paste TSV into ' + ctx.endpoint,
      message: 'Paste cells copied from a spreadsheet. The first line names the columns (a field like MonsterIndex, or the header shown in the grid). '
        + 'A row whose Index matches a row here updates it; a row without an Index is added as a new row. Lookup columns take the name shown in the grid. '
        + 'Nothing is saved until you apply the changes.',
      confirmLabel: 'Check',
      danger: false,
      fields: [{ name: 'tsv', label: 'Cells (tab-separated)', type: 'textarea', value: '' }],
      validate: function (x) { return !x.tsv || !String(x.tsv).trim() ? 'Paste some cells.' : null; }
    });
    if (!v) return;
    var table = parseTsv(v.tsv);
    if (table.length < 2) { portalToast('Expected a header line and at least one row.', 'error'); return; }

    var columns = editableColumns(api);
    var indexCol = flatDefs(api.getColumnDefs() || []).filter(function (c) { return c.field === 'Index'; })[0] || { field: 'Index', headerName: 'Index' };
    var map = mapHeaders(table[0], [indexCol].concat(columns));
    var indexAt = map.fields.indexOf('Index');
    var colByField = {};
    columns.forEach(function (c) { colByField[c.field] = c; });
    if (!map.fields.some(function (f) { return f && f !== 'Index'; })) {
      portalToast('No pasted column matches an editable column of this grid' + (map.unknown.length ? ' (unknown: ' + map.unknown.join(', ') + ')' : '') + '.', 'error');
      return;
    }

    var ts = (global.LiveDbLogic && ctx.liveDb) ? global.LiveDbLogic.tabState(ctx.liveDb) : null;
    var canInsert = !ctx.noInsert && !(ts && !ts.insertable);
    var updates = [], inserts = [], missing = [];
    table.slice(1).forEach(function (cells, r) {
      var idx = indexAt >= 0 ? Number(String(cells[indexAt] || '').trim()) : 0;
      if (idx > 0) {
        var node = api.getRowNode(String(idx));
        if (node) updates.push({ line: r + 2, node: node, cells: cells });
        else missing.push(idx);
      } else inserts.push({ line: r + 2, cells: cells });
    });
    if (inserts.length && !canInsert) { portalToast('This table does not accept new rows here: give every pasted row the Index of an existing row.', 'error'); return; }

    var summary = updates.length + ' row(s) to update, ' + inserts.length + ' new row(s)';
    if (missing.length) summary += '; ' + missing.length + ' Index value(s) not in this grid are skipped (' + missing.slice(0, 8).join(', ') + (missing.length > 8 ? ', …' : '') + ')';
    if (map.unknown.length) summary += '; unknown columns ignored: ' + map.unknown.join(', ');
    if (!(await portalConfirm(summary + '.', { title: 'Paste into ' + ctx.endpoint + '?', confirmLabel: 'Paste', danger: false }))) return;

    var refused = [], changed = 0;
    function applyCells(node, cells, line) {
      map.fields.forEach(function (field, c) {
        if (!field || field === 'Index' || c >= cells.length) return;
        var col = colByField[field];
        var conv = convertCell(cells[c], kindOf(col, node.data[field]));
        if (!conv.ok) { refused.push('line ' + line + ' ' + field + ' "' + conv.value + '"'); return; }
        var value = conv.value;
        if (col && typeof col.valueParser === 'function' && typeof value === 'string') {
          value = col.valueParser({ oldValue: node.data[field], newValue: value, data: node.data, node: node, colDef: col, api: api });
        }
        var before = api.getCellValue ? api.getCellValue({ rowNode: node, colKey: field }) : node.data[field];
        if (String(before == null ? '' : before) === String(value == null ? '' : value)) return;
        if (node.setDataValue(field, value, 'pasteTsv')) { changed++; return; }
        // A lookup column: a name several rows share is labelled "12 — Name"; accept the plain name when one option has it,
        // or "#12" / "12" for the row index.
        var alt = lookupLabelFor(col, node, value);
        if (alt != null && alt !== value && node.setDataValue(field, alt, 'pasteTsv')) changed++;
        else refused.push('line ' + line + ' ' + field + ' "' + cells[c] + '"' + (alt === null && lookupOptions(col, node) ? ' (no such name, or several rows have it: use #index)' : ''));
      });
    }
    updates.forEach(function (u) { applyCells(u.node, u.cells, u.line); });

    if (inserts.length) {
      var defaults = await schemaDefaults(ctx.endpoint);
      var extra = typeof ctx.newRowDefaults === 'function' ? ctx.newRowDefaults() : (ctx.newRowDefaults || {});
      var rows = inserts.map(function () { return Object.assign({}, JSON.parse(JSON.stringify(defaults)), extra, { Index: newRowSeq-- }); });
      api.applyTransaction({ add: rows, addIndex: 0 });
      rows.forEach(function (row, k) {
        var node = api.getRowNode(String(row.Index));
        if (global.DbSave && DbSave.noteEdit) DbSave.noteEdit(ctx.dirtyKey || ctx.endpoint, row.Index, []);
        if (node) applyCells(node, inserts[k].cells, inserts[k].line);
      });
    }

    if (changed || inserts.length) setDirty(ctx, true);
    portalToast('Pasted: ' + changed + ' cell(s) changed' + (inserts.length ? ', ' + inserts.length + ' new row(s)' : '') +
      (refused.length ? '; ' + refused.length + ' refused (' + refused.slice(0, 5).join('; ') + (refused.length > 5 ? '; …' : '') + ')' : '') +
      '. Review, then apply the changes to save.', refused.length ? 'warn' : 'ok');
  }

  async function copyTsv(ctx) {
    var api = ctx.getApi(); if (!api) return;
    var sel = api.getSelectedRows();
    var rows = [];
    if (sel.length) rows = sel;
    else api.forEachNodeAfterFilterAndSort(function (n) { rows.push(n.data); });
    var cols = (api.getAllDisplayedColumns ? api.getAllDisplayedColumns() : []).map(function (c) { return c.getColDef(); })
      .filter(function (d) { return d && d.field && !isSelectCol(d) && d.field.indexOf('__') !== 0; })
      .map(function (d) { return { field: d.field, get: function (r) { return typeof d.valueGetter === 'function' ? d.valueGetter({ data: r, colDef: d }) : r[d.field]; } }; });
    var text = toTsv(rows, cols);
    try {
      await navigator.clipboard.writeText(text);
      portalToast('Copied ' + rows.length + ' row(s) × ' + cols.length + ' column(s) as TSV' + (sel.length ? ' (the selection)' : ' (the filtered rows)') + '.', 'ok');
    } catch (e) {
      download(ctx.endpoint + '.tsv', text, 'text/tab-separated-values');
      portalToast('The clipboard was not available: downloaded ' + ctx.endpoint + '.tsv instead.', 'info');
    }
  }

  async function importJson(ctx, text) {
    var rows;
    try { rows = JSON.parse(text); } catch (e) { portalToast('Invalid JSON file.', 'error'); return; }
    if (!Array.isArray(rows) || !rows.length) { portalToast('Expected a non-empty JSON array of rows.', 'error'); return; }
    var live = !!(global.LiveDbEditor && await LiveDbEditor.useLiveDb());
    var withRev = rows.filter(function (r) { return r && r.__rev != null; }).length;
    var ok = await portalConfirm('Import ' + rows.length + ' row(s) into ' + ctx.endpoint +
      '? Existing Index values update those rows; new/unknown Index values create rows. All go through validation, ' +
      'then the rows are applied and the database is saved right away (there is no preview).' +
      (withRev ? '\n\n' + withRev + ' row(s) carry a revision (__rev) from the export: rows that changed since then are skipped as conflicts.' : '') +
      (live ? '\n\nLiveDb: rows without a revision token overwrite the live values without the usual stale-edit check.' : ''));
    if (!ok) return;
    // noRev: only here (after the confirm above) may rows without __rev update existing rows.
    var res = await DbSave.persist(ctx.endpoint, rows, live ? { noRev: true, reason: 'import ' + ctx.endpoint } : { reason: 'import ' + ctx.endpoint });
    if (res.ok && ctx.reload) ctx.reload();
  }

  // Sets multi-row selection + a checkbox column so Add/Delete/Bulk have a selection model.
  function enrichGrid(gridOpts, opts) {
    opts = opts || {};
    gridOpts.rowSelection = 'multiple';
    gridOpts.suppressRowClickSelection = true;
    if (!opts.readOnly && Array.isArray(gridOpts.columnDefs) && !gridOpts.columnDefs.some(isSelectCol)) {
      // concat (not unshift) so a schema-cached colDefs array is never mutated.
      gridOpts.columnDefs = [{
        context: { select: true }, headerName: '', width: 42, pinned: 'left',
        checkboxSelection: true, headerCheckboxSelection: true, headerCheckboxSelectionFilteredOnly: true,
        editable: false, sortable: false, filter: false, resizable: false, suppressMenu: true
      }].concat(gridOpts.columnDefs);
    }
    return gridOpts;
  }

  global.DbGridTools = {
    mountToolbar: mountToolbar, enrichGrid: enrichGrid, closePopover: closePopover,
    parseTsv: parseTsv, mapHeaders: mapHeaders, convertCell: convertCell, toTsv: toTsv, pasteTsv: pasteTsv, resolveLookupLabel: resolveLookupLabel
  };
})(window);
