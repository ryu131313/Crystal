/**
 * LiveDb History section (#section-livedb) — LiveDb Batch D.
 *
 *   Status line   GET /api/admin/livedb/status (on show and every 10 s while visible): enabled, world, last batch, live outcome,
 *                 sweeper queue, diverged parents, open WinForms editors, unsaved legacy edits.
 *   History       GET /api/admin/livedb/history?limit=&offset=&table=&admin=&from=&to= (newest first, paged). Each batch expands to
 *                 GET /api/admin/livedb/history/{id}: per row the before -> after of every field with its live / restart outcome,
 *                 full row images for inserts / deletes, the live counters (fields, rows added / removed / refused, item holders
 *                 refreshed, client push), and the undo state.
 *   Undo          POST /api/admin/livedb/undo/{id}?dryRun=1 -> preview (what reverts, what stays until a restart, conflicts) ->
 *                 confirm -> POST /api/admin/livedb/undo/{id} {reason}. The server derives the undo batch id from the target, so a
 *                 repeated click returns the first outcome. Conflicts (a row changed since) are shown inline under the batch.
 * Deep link: #section=livedb[&batch=<id>][&table=<endpoint>].
 * Every request goes through portalApi (CSRF on the POST); all dynamic text is set with textContent.
 */
(function (global) {
  'use strict';

  var ENDPOINTS = ['items', 'monsters', 'magic', 'npcs', 'quests', 'maps', 'mapsafezones', 'maprespawns', 'mapmovements', 'mapminezones',
    'gameshop', 'conquests', 'conquestguards', 'conquestgates', 'conquestwalls', 'conquestsieges', 'conquestflags', 'conquestcontrolpoints', 'dragon'];
  var PAGE = 50;
  var LIVE_TEXT = {
    applied: 'Live', partial: 'Partly live', none: 'Nothing live', 'world-stopped': 'Saved only (world stopped)',
    'rolled-back': 'Saved only (live step rolled back)', timeout: 'Saved only (game thread timeout)', 'dry-run': 'Dry run'
  };

  var root = null;
  var ui = {};
  var state = { offset: 0, total: 0, items: [], filters: { table: '', admin: '', from: '', to: '' }, open: Object.create(null), seq: 0 };
  var details = Object.create(null);   // batch id -> last detail payload
  var conflicts = Object.create(null); // batch id -> last undo reply that was refused / rejected
  var busy = Object.create(null);      // batch id -> true while an undo request is in flight
  var pendingOpen = null;              // deep-linked batch id to expand once the list is loaded

  // ---- DOM helpers (textContent only) --------------------------------------------------------------------------------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = String(text);
    return e;
  }
  function button(label, cls, title) {
    var b = el('button', 'mir-btn' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    if (title) b.title = title;
    return b;
  }
  function pill(text, kind, title) {
    var p = el('span', 'ops-pill ldb-pill' + (kind ? ' ops-pill-' + kind : ''), text);
    if (title) p.title = title;
    return p;
  }
  function clear(node) { while (node && node.firstChild) node.removeChild(node.firstChild); }
  function fmtDate(v) { return v ? OpsUI.fmtDate(v) : '—'; }
  function fmtValue(v) {
    if (v === undefined) return '';
    if (v === null || v === '') return '(empty)';
    var t = typeof v === 'object' ? JSON.stringify(v) : String(v);
    t = t.replace(/\r?\n/g, ' ↵ ');
    return t.length > 120 ? t.slice(0, 117) + '…' : t;
  }
  function plural(n, what) { n = Number(n) || 0; return n + ' ' + what + (n === 1 ? '' : 's'); }
  function liveKind(outcome) {
    if (outcome === 'applied') return 'ok';
    if (outcome === 'partial' || outcome === 'none') return 'warn';
    return 'danger';
  }

  async function getJson(url) {
    try {
      var r = await portalApi(url, { method: 'GET' });
      var j = await r.json().catch(function () { return null; });
      return { ok: r.ok, status: r.status, body: j };
    } catch (e) {
      return { ok: false, status: 0, body: null };
    }
  }

  /** POST /api/admin/livedb/undo/{id}[?dryRun=1]. Resolves { http, body } (error replies too), or null after a transport error. */
  async function postUndo(id, dryRun, reason) {
    var url = '/api/admin/livedb/undo/' + encodeURIComponent(id) + (dryRun ? '?dryRun=1' : '');
    try {
      var r = await portalApi(url, { method: 'POST', body: { reason: reason || null } });
      var j = await r.json().catch(function () { return {}; });
      return { http: r.status, body: j && typeof j === 'object' ? j : {} };
    } catch (e) {
      portalToast('Request failed: ' + ((e && e.message) || e) + '. Undo again to retry (it is not applied twice).', 'error');
      return null;
    }
  }

  // ---- shell ---------------------------------------------------------------------------------------------------------

  function shell() {
    clear(root);
    var head = el('div', 'ldb-head');
    head.appendChild(el('h2', 'ldb-title', 'LiveDb History'));
    head.appendChild(el('p', 'mir-muted ldb-intro',
      'Every game-data batch saved through LiveDb: what changed, what reached the running world, and Undo. Undo reverts edits and removes ' +
      'added rows through the same all-or-nothing pipeline; deleted rows cannot be undone (restore a database backup with the world stopped).'));
    root.appendChild(head);

    ui.status = el('div', 'ldb-status');
    root.appendChild(ui.status);

    var bar = el('div', 'ldb-filters');
    function field(labelText, input) {
      var l = el('label', 'ldb-filter');
      l.appendChild(el('span', 'mir-muted', labelText));
      l.appendChild(input);
      bar.appendChild(l);
      return input;
    }
    ui.table = document.createElement('select');
    ui.table.className = 'fd-input';
    var any = el('option', null, 'All tables');
    any.value = '';
    ui.table.appendChild(any);
    ENDPOINTS.forEach(function (ep) {
      var o = el('option', null, (global.DbRegistry && global.DbRegistry.label) ? global.DbRegistry.label(ep) : ep);
      o.value = ep;
      ui.table.appendChild(o);
    });
    field('Table', ui.table);
    ui.admin = el('input', 'fd-input');
    ui.admin.type = 'text';
    ui.admin.placeholder = 'admin account';
    ui.admin.maxLength = 64;
    field('Admin', ui.admin);
    ui.from = el('input', 'fd-input');
    ui.from.type = 'date';
    field('From', ui.from);
    ui.to = el('input', 'fd-input');
    ui.to.type = 'date';
    field('To', ui.to);
    var actions = el('div', 'ldb-filter-buttons');
    var apply = button('Apply', 'primary');
    var reset = button('Reset');
    var refresh = button('Refresh', null, 'Reload the status and the list');
    actions.appendChild(apply);
    actions.appendChild(reset);
    actions.appendChild(refresh);
    bar.appendChild(actions);
    root.appendChild(bar);

    apply.addEventListener('click', function () { readFilters(); state.offset = 0; loadList(); });
    reset.addEventListener('click', function () {
      state.filters = { table: '', admin: '', from: '', to: '' };
      writeFilters();
      state.offset = 0;
      loadList();
    });
    refresh.addEventListener('click', function () { loadStatus(); loadList(); });
    ui.admin.addEventListener('keydown', function (e) { if (e.key === 'Enter') { readFilters(); state.offset = 0; loadList(); } });

    ui.summary = el('p', 'mir-muted ldb-summary');
    root.appendChild(ui.summary);
    ui.list = el('div', 'ldb-list');
    root.appendChild(ui.list);

    var pager = el('div', 'ldb-pager');
    ui.prev = button('← Newer');
    ui.next = button('Older →');
    ui.page = el('span', 'mir-muted');
    pager.appendChild(ui.prev);
    pager.appendChild(ui.page);
    pager.appendChild(ui.next);
    root.appendChild(pager);
    ui.prev.addEventListener('click', function () { state.offset = Math.max(0, state.offset - PAGE); loadList(); });
    ui.next.addEventListener('click', function () { state.offset += PAGE; loadList(); });
  }

  function readFilters() {
    state.filters = {
      table: ui.table.value || '',
      admin: String(ui.admin.value || '').trim(),
      from: ui.from.value || '',
      to: ui.to.value || ''
    };
  }
  function writeFilters() {
    ui.table.value = state.filters.table || '';
    ui.admin.value = state.filters.admin || '';
    ui.from.value = state.filters.from || '';
    ui.to.value = state.filters.to || '';
  }

  // ---- status line -----------------------------------------------------------------------------------------------------

  async function loadStatus() {
    var res = await getJson('/api/admin/livedb/status');
    if (!root) return;
    clear(ui.status);
    var s = res.ok ? res.body : null;
    if (!s || typeof s !== 'object') {
      ui.status.appendChild(pill('Status unavailable', 'danger'));
      return;
    }
    ui.status.appendChild(pill(s.enabled ? 'LiveDb on' : 'LiveDb off', s.enabled ? 'ok' : 'danger',
      s.enabled ? 'Grid edits save and apply live' : '[LiveDb] Enabled=False: grids use Apply + Save DB; undo is unavailable'));
    ui.status.appendChild(pill('World ' + (s.world || 'unknown'), s.world === 'running' ? 'ok' : (s.world === 'stopped' ? 'danger' : 'warn')));
    var last = s.lastBatchId
      ? 'Last batch ' + s.lastBatchId + ' · ' + fmtDate(s.lastBatchUtc) + ' · ' + (LIVE_TEXT[s.lastLiveOutcome] || s.lastLiveOutcome || '—') +
        (s.lastSaved === false ? ' · not saved' : ' · saved')
      : 'No batch since the server started';
    ui.status.appendChild(el('span', 'ldb-status-item', last));
    ui.status.appendChild(el('span', 'ldb-status-item mir-muted', plural(s.batches, 'batch') + ', ' + plural(s.undos, 'undo') + ', ' +
      plural(s.rejected, 'rejected') + ', ' + plural(s.liveFailures, 'live failure')));
    var sw = s.sweeper || {};
    if (sw.active || sw.pendingKinds) ui.status.appendChild(pill('Sweeper: ' + plural(sw.pendingKinds, 'monster kind') + ' queued', 'warn', 'Spawned monsters still being refreshed'));
    var div = Array.isArray(s.diverged) ? s.diverged : [];
    if (div.length) ui.status.appendChild(pill(plural(div.length, 'diverged parent'), 'warn', 'Saved only until a restart: ' + div.slice(0, 20).join(', ')));
    if (s.editorFormsOpen > 0) ui.status.appendChild(pill('WinForms editor open', 'danger', 'Batches and undo are refused while a server DB editor window is open'));
    if (s.unsavedChanges > 0) ui.status.appendChild(pill(plural(s.unsavedChanges, 'unsaved legacy edit'), 'warn', 'Batches and undo are refused until these are saved or discarded'));
  }

  // ---- history list ----------------------------------------------------------------------------------------------------

  function listUrl() {
    var q = ['limit=' + PAGE, 'offset=' + state.offset];
    var f = state.filters;
    if (f.table) q.push('table=' + encodeURIComponent(f.table));
    if (f.admin) q.push('admin=' + encodeURIComponent(f.admin));
    if (f.from) q.push('from=' + encodeURIComponent(f.from));
    if (f.to) q.push('to=' + encodeURIComponent(f.to));
    if (!f.from) q.push('days=14');
    return '/api/admin/livedb/history' + '?' + q.join('&');
  }

  async function loadList() {
    var seq = ++state.seq;
    ui.summary.textContent = 'Loading…';
    var res = await getJson(listUrl());
    if (!root || seq !== state.seq) return;
    if (!res.ok || !res.body) {
      ui.summary.textContent = 'Could not load the history (HTTP ' + res.status + ').';
      return;
    }
    var b = res.body.data && res.body.data.items ? res.body.data : res.body;
    state.items = Array.isArray(b.items) ? b.items : [];
    state.total = Number(b.total) || 0;
    ui.summary.textContent = state.total
      ? 'Showing ' + (state.offset + 1) + '–' + (state.offset + state.items.length) + ' of ' + state.total + (b.truncated ? ' (only the newest ' + b.scanned + ' batches are searched)' : '')
      : 'No batches match.';
    ui.prev.disabled = state.offset <= 0;
    ui.next.disabled = state.offset + state.items.length >= state.total;
    ui.page.textContent = state.total ? 'page ' + (Math.floor(state.offset / PAGE) + 1) + ' / ' + Math.max(1, Math.ceil(state.total / PAGE)) : '';
    renderList();
    if (pendingOpen) {
      var id = pendingOpen;
      pendingOpen = null;
      openBatch(id, true);
    }
  }

  function renderList() {
    clear(ui.list);
    state.items.forEach(function (it) { ui.list.appendChild(renderBatch(it)); });
  }

  function renderBatch(it) {
    var card = el('div', 'ldb-batch');
    card.setAttribute('data-batch', it.id);
    var head = el('div', 'ldb-batch-head');
    var toggle = button(state.open[it.id] ? '▾' : '▸', 'ldb-toggle', 'Show the rows');
    head.appendChild(toggle);
    var when = el('span', 'ldb-when', fmtDate(it.utc));
    when.title = it.utc ? OpsUI.ago(it.utc) : '';
    head.appendChild(when);
    head.appendChild(el('span', 'ldb-admin', it.admin || '—'));
    head.appendChild(el('code', 'ldb-id', it.id));
    if (it.source && it.source !== 'livedb') head.appendChild(pill(it.source, null));
    head.appendChild(el('span', 'ldb-what', plural(it.applied, 'row') + (it.deleted ? ', ' + it.deleted + ' deleted' : '') +
      (it.tables && it.tables.length ? ' · ' + it.tables.join(', ') : '')));
    head.appendChild(pill(LIVE_TEXT[it.live] || it.live || '—', liveKind(it.live)));
    if (!it.saved) head.appendChild(pill('not saved', 'danger'));
    if (it.inverseOf) head.appendChild(pill('undo of ' + it.inverseOf, null, 'This batch reverses ' + it.inverseOf));
    if (it.undoneBy) head.appendChild(pill('undone by ' + it.undoneBy, 'warn'));
    var spacer = el('span', 'ldb-spacer');
    head.appendChild(spacer);
    var undo = button('Undo', 'danger', it.undoneBy ? 'Already undone' : (!it.undoable ? 'Batches with deleted rows cannot be undone' : 'Preview the undo first'));
    undo.disabled = !!it.undoneBy || !it.undoable || !!busy[it.id];
    head.appendChild(undo);
    card.appendChild(head);

    if (it.reason || it.message) {
      var sub = el('div', 'ldb-batch-sub mir-muted', (it.reason ? 'Reason: ' + it.reason + ' · ' : '') + (it.message || ''));
      card.appendChild(sub);
    }

    var body = el('div', 'ldb-batch-body');
    body.hidden = !state.open[it.id];
    card.appendChild(body);
    if (state.open[it.id]) renderDetailInto(body, it.id);

    toggle.addEventListener('click', function () {
      state.open[it.id] = !state.open[it.id];
      toggle.textContent = state.open[it.id] ? '▾' : '▸';
      body.hidden = !state.open[it.id];
      if (state.open[it.id]) renderDetailInto(body, it.id, true);
    });
    undo.addEventListener('click', function () { startUndo(it.id, undo); });
    return card;
  }

  function openBatch(id, scroll) {
    state.open[id] = true;
    var card = ui.list.querySelector('[data-batch="' + (global.CSS && CSS.escape ? CSS.escape(id) : id) + '"]');
    if (!card) {
      // not on this page: show it on its own above the list
      var holder = el('div', 'ldb-batch');
      holder.setAttribute('data-batch', id);
      holder.appendChild(el('div', 'ldb-batch-head', 'Batch ' + id));
      var b = el('div', 'ldb-batch-body');
      holder.appendChild(b);
      ui.list.insertBefore(holder, ui.list.firstChild);
      renderDetailInto(b, id, true);
      card = holder;
    } else {
      var body = card.querySelector('.ldb-batch-body');
      var t = card.querySelector('.ldb-toggle');
      if (t) t.textContent = '▾';
      if (body) { body.hidden = false; renderDetailInto(body, id, true); }
    }
    if (scroll && card.scrollIntoView) card.scrollIntoView({ block: 'nearest' });
  }

  // ---- batch detail ----------------------------------------------------------------------------------------------------

  async function renderDetailInto(body, id, reload) {
    if (!reload && details[id]) { drawDetail(body, details[id], id); return; }
    clear(body);
    body.appendChild(el('p', 'mir-muted', 'Loading…'));
    var res = await getJson('/api/admin/livedb/history/' + encodeURIComponent(id));
    if (!root) return;
    if (!res.ok || !res.body || !res.body.rows) {
      clear(body);
      body.appendChild(el('p', 'ldb-bad', (res.body && res.body.message) || ('Could not load batch ' + id + ' (HTTP ' + res.status + ').')));
      return;
    }
    details[id] = res.body;
    drawDetail(body, res.body, id);
  }

  function drawDetail(body, d, id) {
    clear(body);
    var live = d.live || {};
    var counters = [
      plural(live.appliedFields, 'field') + ' live',
      plural(live.restartFields, 'field') + ' after restart'
    ];
    if (live.insertedLive) counters.push(plural(live.insertedLive, 'row') + ' added live');
    if (live.deletedLive) counters.push(plural(live.deletedLive, 'row') + ' removed live');
    if (live.refusedLive) counters.push(plural(live.refusedLive, 'change') + ' refused by the running world');
    if (live.monstersQueued) counters.push(plural(live.monstersQueued, 'monster kind') + ' refreshed');
    if (live.dropsReloaded) counters.push(plural(live.dropsReloaded, 'drop list') + ' reloaded');
    if (live.magicSynced) counters.push(plural(live.magicSynced, 'spell') + ' synced');
    if (live.itemHoldersRefreshed) counters.push(plural(live.itemHoldersRefreshed, 'item holder') + ' refreshed');
    if (live.pushConnections || live.pushPackets) counters.push('pushed to ' + plural(live.pushConnections, 'client') + ' (' + plural(live.pushPackets, 'packet') + ', ' + plural(live.pushRows, 'row') + ')');
    if (live.pushSkipped) counters.push('client push skipped (MaxPushRows)');
    if (live.pushFailed) counters.push('client push failed');
    body.appendChild(el('div', 'ldb-counters', counters.join(' · ')));
    if (d.backupFile) body.appendChild(el('div', 'mir-muted ldb-small', 'Previous Server.MirDB: Back Up\\Database\\' + d.backupFile));
    if (d.flushedAreas && d.flushedAreas.length) body.appendChild(el('div', 'mir-muted ldb-small', 'Also saved other editor changes: ' + d.flushedAreas.join(', ')));
    (d.notes || []).forEach(function (n) { body.appendChild(el('div', 'ldb-note', n)); });

    (d.rows || []).forEach(function (r) { body.appendChild(drawRow(r)); });

    var u = d.undo || {};
    var undoBox = el('div', 'ldb-undo-state');
    if (u.undoneBy) undoBox.appendChild(el('div', 'ldb-note', 'Undone by ' + u.undoneBy + '.'));
    else if (u.undoable) undoBox.appendChild(el('div', 'mir-muted ldb-small', 'Undo would revert ' + plural(u.updates, 'edited row') + (u.deletes ? ' and remove ' + plural(u.deletes, 'added row') : '') + ' (batch id ' + u.undoId + ').'));
    (u.refused || []).forEach(function (x) {
      undoBox.appendChild(el('div', 'ldb-bad', 'Cannot undo' + (x.endpoint ? ' ' + x.endpoint + ' #' + x.index : '') + ': ' + x.message));
    });
    (u.notes || []).forEach(function (n) { if ((d.notes || []).indexOf(n) < 0) undoBox.appendChild(el('div', 'ldb-note', n)); });
    body.appendChild(undoBox);

    var c = conflicts[id];
    if (c) body.appendChild(drawIssues(c, 'Last undo attempt'));
  }

  function drawRow(r) {
    var box = el('div', 'ldb-row');
    var title = el('div', 'ldb-row-head');
    title.appendChild(pill(r.kind || 'update', r.kind === 'delete' ? 'danger' : (r.kind === 'insert' ? 'ok' : null)));
    title.appendChild(el('strong', null, (r.endpoint || '') + ' #' + r.index + (r.name ? ' ' + r.name : '')));
    if (r.rowLive === true) title.appendChild(pill('live', 'ok'));
    else if (r.rowLive === false) title.appendChild(pill('after restart', 'warn', r.rowReason || ''));
    box.appendChild(title);
    if (r.rowReason) box.appendChild(el('div', 'mir-muted ldb-small', r.rowReason));

    var changes = Array.isArray(r.changes) ? r.changes : [];
    if (r.kind === 'update' && changes.length) {
      var t = el('table', 'ops-table ldb-diff');
      var thead = el('thead');
      var hr = el('tr');
      ['Field', 'Before', 'After', 'Running world'].forEach(function (h) { hr.appendChild(el('th', null, h)); });
      thead.appendChild(hr);
      t.appendChild(thead);
      var tb = el('tbody');
      changes.forEach(function (c) {
        var tr = el('tr');
        tr.appendChild(el('td', 'ldb-field', c.field));
        tr.appendChild(el('td', 'ldb-before', fmtValue(c.before)));
        tr.appendChild(el('td', 'ldb-after', fmtValue(c.after)));
        var outcome = el('td');
        outcome.appendChild(pill(c.live ? 'live' : 'after restart', c.live ? 'ok' : 'warn'));
        if (!c.live && c.reason) outcome.appendChild(el('span', 'mir-muted ldb-small', ' ' + c.reason));
        tr.appendChild(outcome);
        tb.appendChild(tr);
      });
      t.appendChild(tb);
      box.appendChild(t);
    } else if (r.image && typeof r.image === 'object') {
      var keys = Object.keys(r.image).filter(function (k) { return k.indexOf('__') !== 0; });
      var det = el('details', 'ldb-image');
      det.appendChild(el('summary', null, (r.kind === 'delete' ? 'Deleted row' : 'New row') + ' (' + keys.length + ' fields)'));
      var dl = el('div', 'ldb-image-grid');
      keys.forEach(function (k) {
        dl.appendChild(el('span', 'ldb-field', k));
        dl.appendChild(el('span', null, fmtValue(r.image[k])));
      });
      det.appendChild(dl);
      box.appendChild(det);
    }
    return box;
  }

  /** Conflicts / validation / refusals of an undo reply, shown inline under the batch and in the preview. */
  function drawIssues(reply, heading) {
    var box = el('div', 'ldb-issues');
    box.appendChild(el('div', 'ldb-issues-head', heading + ': ' + (reply.message || 'refused')));
    var list = el('ul', 'ldb-issue-list');
    (reply.conflicts || []).forEach(function (c) {
      list.appendChild(el('li', null, (c.endpoint || '') + ' #' + c.index + ': ' + (c.message || 'changed since')));
    });
    (reply.validation || []).forEach(function (v) {
      list.appendChild(el('li', null, (v.endpoint || '') + ' #' + v.index + (v.field ? ' ' + v.field : '') + ': ' + (v.message || '')));
    });
    var data = reply.data || {};
    (data.refused || []).forEach(function (x) {
      list.appendChild(el('li', null, (x.endpoint ? x.endpoint + ' #' + x.index + ': ' : '') + (x.message || '')));
    });
    if (list.firstChild) box.appendChild(list);
    if ((reply.conflicts || []).length)
      box.appendChild(el('p', 'mir-muted ldb-small', 'These rows changed after the batch; undo would overwrite newer edits, so nothing was changed. Edit the rows directly instead.'));
    return box;
  }

  // ---- undo --------------------------------------------------------------------------------------------------------------

  async function startUndo(id, btn) {
    if (busy[id]) return;
    busy[id] = true;
    if (btn) btn.disabled = true;
    try {
      var preview = await postUndo(id, true);
      if (!preview) return;
      var p = preview.body;
      if (p.alreadyUndone) {
        portalToast(p.message || ('Batch ' + id + ' is already undone.'), 'info');
        await reloadAfter(id);
        return;
      }
      var refused = preview.http >= 400 || p.ok === false || p.status === 'rejected';
      if (refused) {
        conflicts[id] = p;
        portalToast(p.message || ('Undo of ' + id + ' is not possible (HTTP ' + preview.http + ').'), 'error');
        redrawDetail(id);
        return;
      }
      delete conflicts[id];
      var choice = await previewDialog(id, p);
      if (!choice) return;
      var res = await postUndo(id, false, choice.reason);
      if (!res) return;
      var b = res.body;
      if (res.http >= 400 || b.ok === false || b.status === 'rejected') {
        conflicts[id] = b;
        portalToast(b.message || ('Undo failed (HTTP ' + res.http + ').'), 'error');
        redrawDetail(id);
        return;
      }
      portalToast(b.message || ('Batch ' + id + ' undone.'), b.live && (b.live.outcome === 'applied' || b.live.outcome === 'none') ? 'ok' : 'warn', 9000);
      if (global.MirLiveDb && global.MirLiveDb.refresh) global.MirLiveDb.refresh();
      await reloadAfter(id);
    } finally {
      busy[id] = false;
      if (btn && root && btn.isConnected) btn.disabled = false;
    }
  }

  async function reloadAfter(id) {
    delete details[id];
    state.open[id] = true;
    await Promise.all([loadStatus(), loadList()]);
  }

  function redrawDetail(id) {
    state.open[id] = true;
    openBatch(id, true);
  }

  /** The dry-run preview: what the undo reverts, what reaches the running world, what stays until a restart. Resolves {reason} or null. */
  function previewDialog(id, p) {
    return new Promise(function (resolve) {
      var backdrop = el('div', 'portal-confirm-backdrop');
      var dialog = el('div', 'portal-confirm-dialog ldb-dialog');
      dialog.setAttribute('role', 'dialog');
      dialog.setAttribute('aria-modal', 'true');
      dialog.appendChild(el('h3', null, 'Undo batch ' + id + '?'));
      var body = el('div', 'ldb-dialog-body');
      body.appendChild(el('p', null, p.message || 'Dry run.'));
      (p.undoNotes || []).forEach(function (n) { body.appendChild(el('div', 'ldb-note', n)); });
      (p.rows || []).forEach(function (r) {
        var box = el('div', 'ldb-row');
        var head = el('div', 'ldb-row-head');
        head.appendChild(pill(r.kind === 'delete' ? 'remove' : 'revert', r.kind === 'delete' ? 'danger' : null));
        head.appendChild(el('strong', null, (r.endpoint || '') + ' #' + (r.newIndex || r.index) + (r.name ? ' ' + r.name : '')));
        if (r.kind === 'delete') head.appendChild(pill(r.rowLive ? 'removed live' : 'saved only until a restart', r.rowLive ? 'ok' : 'warn', r.rowReason || ''));
        box.appendChild(head);
        (r.changes || []).forEach(function (c) {
          var line = el('div', 'ldb-small');
          line.appendChild(el('span', 'ldb-field', c.field + ': '));
          line.appendChild(el('span', 'ldb-before', fmtValue(c.before)));
          line.appendChild(el('span', null, ' → '));
          line.appendChild(el('span', 'ldb-after', fmtValue(c.after)));
          line.appendChild(el('span', 'mir-muted', c.live ? '  (live)' : '  (after restart' + (c.reason ? ': ' + c.reason : '') + ')'));
          box.appendChild(line);
        });
        body.appendChild(box);
      });
      dialog.appendChild(body);
      var wrap = el('label', 'ldb-dialog-reason');
      wrap.appendChild(el('span', 'mir-muted', 'Reason (optional, kept in the audit trail)'));
      var reason = el('input', 'fd-input');
      reason.type = 'text';
      reason.maxLength = 300;
      wrap.appendChild(reason);
      dialog.appendChild(wrap);
      var actions = el('div', 'portal-confirm-actions');
      var cancel = button('Cancel');
      var confirm = button('Undo now', 'danger');
      actions.appendChild(cancel);
      actions.appendChild(confirm);
      dialog.appendChild(actions);
      backdrop.appendChild(dialog);
      var done = false;
      function finish(v) {
        if (done) return;
        done = true;
        document.removeEventListener('keydown', onKey, true);
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        resolve(v);
      }
      function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(null); } }
      cancel.addEventListener('click', function () { finish(null); });
      confirm.addEventListener('click', function () { finish({ reason: String(reason.value || '').trim() }); });
      backdrop.addEventListener('click', function (e) { if (e.target === backdrop) finish(null); });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(backdrop);
      reason.focus();
    });
  }

  // ---- registration ------------------------------------------------------------------------------------------------------

  function applyParams(p) {
    p = p || {};
    if (p.table && ENDPOINTS.indexOf(String(p.table)) >= 0) state.filters.table = String(p.table);
    if (p.batch) pendingOpen = String(p.batch);
  }

  global.AdminSections.register('livedb', {
    load: function (el0, params) {
      root = el0;
      shell();
      applyParams(params);
      writeFilters();
      global.AdminSections.poll('livedb', loadStatus, 10000);
      return Promise.all([loadStatus(), loadList()]);
    },
    show: function () {
      loadStatus();
      return loadList();
    }
  });

  if (global.portalRegisterDeepLink) {
    global.portalRegisterDeepLink('livedb', function (params) {
      if (!root) return;
      var before = JSON.stringify(state.filters);
      applyParams(params);
      writeFilters();
      if (JSON.stringify(state.filters) !== before) { state.offset = 0; loadList(); }
      else if (pendingOpen) { var id = pendingOpen; pendingOpen = null; openBatch(id, true); }
    });
  }
})(window);
