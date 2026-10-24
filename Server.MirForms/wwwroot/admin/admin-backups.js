/**
 * Backups & History section (#section-backups) — B11.
 *
 * Tabs:
 *   Database / Accounts — Back Up\Database\*.bak, Back Up\Accounts\*.bak: list (origin, date, size, file version), Create backup
 *     (VACUUM INTO copies of the live SystemData.db / UserData.db), Download, Restore (world STOPPED only; typed confirmation; the server backs up the
 *     current file first) and Prune (dry run first, then one confirmation).
 *   Scripts — Back Up\WebAdmin\Scripts\** (written by the Scripts & Files editor): list, filter, Download, open the file in Scripts, Prune.
 *   History — game-data edits from the audit log (GET /api/admin/db/history): per-field before -> after with Revert (GET the current row,
 *     set the recorded "before" values, PUT with its __rev, then save — via DbSave.persist). Inserts / deletes are not revertible.
 * Deep links: #section=backups&tab=database|accounts|scripts|history[&endpoint=<table>&index=<n>].
 * All mutations go through portalApi (CSRF) and are audited server-side. All dynamic text is escaped.
 */
(function (global) {
  'use strict';

  var TABS = [
    { id: 'database', label: 'Database' },
    { id: 'accounts', label: 'Accounts' },
    { id: 'scripts', label: 'Scripts' },
    { id: 'history', label: 'Game-data history' }
  ];
  var ENDPOINTS = ['items', 'monsters', 'magic', 'npcs', 'quests', 'maps', 'mapsafezones', 'maprespawns', 'mapmovements', 'mapminezones',
    'gameshop', 'conquests', 'conquestguards', 'conquestgates', 'conquestwalls', 'conquestsieges', 'conquestflags', 'conquestcontrolpoints', 'dragon'];
  var HISTORY_PAGE = 200;

  var root = null;
  var current = 'database';
  var kindData = Object.create(null);     // kind -> last GET /api/admin/backups?kind= payload
  var summary = null;
  var lastWorld = null;
  var scriptFilter = '';
  var hist = { items: [], next: null, filters: { endpoint: '', index: '', admin: '', days: 30 }, seq: 0 };
  var loadSeq = 0;

  function esc(s) { return OpsUI.esc(s); }
  function $(sel) { return root ? root.querySelector(sel) : null; }

  function fmtSize(bytes) {
    var n = Number(bytes) || 0;
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    if (n < 1024 * 1024 * 1024) return (n / (1024 * 1024)).toFixed(1) + ' MB';
    return (n / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
  }

  function when(utc) {
    if (!utc) return '<span class="mir-muted">—</span>';
    return '<span title="' + esc(OpsUI.fmtDate(utc)) + '">' + esc(OpsUI.fmtDate(utc)) + '</span><br><span class="mir-muted">' + esc(OpsUI.ago(utc)) + '</span>';
  }

  function worldPill(world) {
    var w = String(world || 'unknown');
    var cls = w === 'running' ? 'ops-pill ops-pill-ok' : w === 'stopped' ? 'ops-pill ops-pill-danger' : 'ops-pill ops-pill-warn';
    return '<span class="' + cls + '">World ' + esc(w) + '</span>';
  }

  var ORIGIN_LABEL = { manual: 'Manual', 'pre-restore': 'Pre-restore', auto: 'Automatic', script: 'Script', other: 'Other' };
  function originPill(o) {
    var cls = o === 'manual' ? 'ops-pill ops-pill-ok' : o === 'pre-restore' ? 'ops-pill ops-pill-warn' : 'ops-pill backups-pill';
    return '<span class="' + cls + '">' + esc(ORIGIN_LABEL[o] || o || '') + '</span>';
  }

  function downloadHref(kind, name) {
    return '/api/admin/backups/download?kind=' + encodeURIComponent(kind) + '&name=' + encodeURIComponent(name);
  }

  async function postJson(url, body, quiet) {
    return OpsUI.post(url, body, { quiet: !!quiet });
  }

  // ---- shell -----------------------------------------------------------------------------------------------------------

  function shell() {
    root.innerHTML =
      '<div class="admin-status-grid backups-tiles" data-role="tiles"></div>' +
      '<div class="toolbar backups-tabs-bar">' +
      '  <div class="sheet-tabs backups-tabs" role="tablist">' +
      TABS.map(function (t) { return '<button type="button" class="mir-btn" role="tab" data-tab="' + t.id + '">' + esc(t.label) + '</button>'; }).join('') +
      '  </div>' +
      '  <span data-role="world"></span>' +
      '</div>' +
      '<div data-role="panel"></div>';

    $('.backups-tabs').addEventListener('click', function (e) {
      var b = e.target.closest('[data-tab]');
      if (!b) return;
      selectTab(b.getAttribute('data-tab'), true);
    });
    $('[data-role="tiles"]').addEventListener('click', function (e) {
      var t = e.target.closest('[data-tile]');
      if (t) selectTab(t.getAttribute('data-tile'), true);
    });
    $('[data-role="panel"]').addEventListener('click', onPanelClick);
  }

  function selectTab(id, writeHash) {
    if (!TABS.some(function (t) { return t.id === id; })) id = 'database';
    current = id;
    root.querySelectorAll('[data-tab]').forEach(function (b) {
      var on = b.getAttribute('data-tab') === id;
      b.classList.toggle('primary', on);
      b.setAttribute('aria-selected', String(on));
    });
    if (writeHash && global.portalSetHashParams) {
      global.portalSetHashParams({ tab: id === 'database' ? null : id, endpoint: null, index: null }, true);
      lastDeepKey = deepKey({ tab: id === 'database' ? null : id });
    }
    return loadCurrent();
  }

  function loadCurrent() {
    return current === 'history' ? renderHistoryShell() : loadKind(current);
  }

  async function loadSummary() {
    try {
      var j = await OpsUI.fetchJson('/api/admin/backups');
      summary = j && j.kinds ? j : OpsUI.unwrap(j);
    } catch (e) {
      summary = null;
    }
    renderTiles();
  }

  function renderTiles() {
    var el = $('[data-role="tiles"]');
    if (!el) return;
    if (!summary || !summary.kinds) { el.innerHTML = ''; return; }
    lastWorld = summary.world || lastWorld;
    el.innerHTML = summary.kinds.map(function (k) {
      return '<button type="button" class="admin-status-tile backups-tile" data-tile="' + esc(k.kind) + '">' +
        '<span class="k">' + esc(k.label) + '</span>' +
        '<span class="v">' + esc(OpsUI.formatNumber(k.count)) + ' <small>file' + (k.count === 1 ? '' : 's') + '</small></span>' +
        '<span class="mir-muted backups-tile-sub">' + esc(fmtSize(k.totalSize)) + (k.newestUtc ? ' · newest ' + esc(OpsUI.ago(k.newestUtc)) : '') + '</span>' +
        '</button>';
    }).join('');
    renderWorld(summary.world);
  }

  function renderWorld(world) {
    var el = $('[data-role="world"]');
    if (el && world) el.innerHTML = worldPill(world);
  }

  // ---- database / accounts / scripts -------------------------------------------------------------------------------------

  async function loadKind(kind) {
    var my = ++loadSeq;
    var panel = $('[data-role="panel"]');
    if (!kindData[kind]) panel.innerHTML = '<div class="mir-card"><p class="mir-muted">Loading…</p></div>';
    try {
      var j = await OpsUI.fetchJson('/api/admin/backups?kind=' + encodeURIComponent(kind));
      if (my !== loadSeq || current !== kind) return;
      kindData[kind] = j && j.files ? j : OpsUI.unwrap(j);
      lastWorld = kindData[kind].world || lastWorld;
      renderWorld(lastWorld);
      renderKind(kind);
    } catch (e) {
      if (my !== loadSeq) return;
      panel.innerHTML = '<div class="mir-card"><p class="ops-pill-danger">' + esc('Could not list backups: ' + ((e && e.message) || e)) + '</p></div>';
    }
  }

  function versionCell(f, d) {
    if (f.version == null) return '<span class="mir-muted">—</span>';
    var sv = d.supportedVersions;
    var ok = !sv || (f.version >= sv.min && f.version <= sv.max);
    return '<span class="' + (ok ? '' : 'ops-pill ops-pill-danger') + '" title="' + (ok ? 'Supported by this server' : 'Not loadable by this server (supports ' + esc(sv.min) + '–' + esc(sv.max) + ')') + '">' +
      esc(f.version) + (f.customVersion ? '.' + esc(f.customVersion) : '') + '</span>';
  }

  function renderKind(kind) {
    var d = kindData[kind];
    var panel = $('[data-role="panel"]');
    if (!d || !panel) return;

    if (kind === 'scripts') { renderScripts(d); return; }

    var live = d.live || {};
    var canRestore = !!d.canRestore;
    var restoreTitle = canRestore ? 'Replace the live file with this backup' : 'Stop the world first (Server Control) — restores only run while it is stopped';
    var files = d.files || [];

    var rows = files.length ? files.map(function (f, i) {
      return '<tr>' +
        '<td class="backups-name">' + esc(f.name) + '</td>' +
        '<td>' + originPill(f.origin) + '</td>' +
        '<td>' + when(f.modifiedUtc) + '</td>' +
        '<td class="backups-num">' + esc(fmtSize(f.size)) + '</td>' +
        '<td>' + versionCell(f, d) + '</td>' +
        '<td class="backups-actions">' +
        '<a class="mir-btn ops-row-btn" href="' + esc(downloadHref(kind, f.name)) + '" download>Download</a>' +
        (d.restorable ? '<button type="button" class="mir-btn ops-row-btn danger" data-restore="' + i + '"' + (canRestore ? '' : ' disabled') + ' title="' + esc(restoreTitle) + '">Restore…</button>' : '') +
        '</td></tr>';
    }).join('') : '<tr><td colspan="6" class="mir-muted">No backups yet.</td></tr>';

    panel.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>' + esc(d.label || kind) + '</h2>' +
      '    <div class="backups-toolbar-actions">' +
      (d.creatable ? '<button type="button" class="mir-btn primary" data-act="create">Create backup</button>' : '') +
      '      <button type="button" class="mir-btn" data-act="prune">Prune…</button>' +
      '      <button type="button" class="mir-btn" data-act="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <dl class="kv-list backups-live">' +
      '    <dt>Live file</dt><dd class="mono">' + esc(live.name || '') + (live.exists === false ? ' <span class="ops-pill ops-pill-warn">missing</span>' : '') + '</dd>' +
      (live.exists ? '<dt>Size</dt><dd>' + esc(fmtSize(live.size)) + '</dd><dt>Last written</dt><dd>' + esc(OpsUI.fmtDate(live.modifiedUtc)) + ' (' + esc(OpsUI.ago(live.modifiedUtc)) + ')</dd>' +
        '<dt>File version</dt><dd>' + esc(live.version == null ? '—' : live.version) + (d.supportedVersions ? ' <span class="mir-muted">(server supports ' + esc(d.supportedVersions.min) + '–' + esc(d.supportedVersions.max) + ')</span>' : '') + '</dd>' : '') +
      '    <dt>Folder</dt><dd class="mono">' + esc(d.directory || '') + '</dd>' +
      '    <dt>Backups</dt><dd>' + esc(OpsUI.formatNumber(d.count || files.length)) + ' · ' + esc(fmtSize(d.totalSize)) + '</dd>' +
      '  </dl>' +
      (d.restorable ? '<p class="backups-note ' + (canRestore ? 'ok' : 'warn') + '">' +
        (canRestore
          ? 'The world is stopped: backups can be restored. The current file is backed up first as a "Pre-restore" copy.'
          : 'Restoring requires the world to be stopped. <button type="button" class="mir-btn ops-row-btn" data-act="server">Open Server Control</button>') +
        '</p>' : '') +
      (kind === 'database' ? '<p class="mir-muted backups-hint">The server also writes an automatic "Database …" copy every time the game data is saved.</p>' : '') +
      (kind === 'accounts' ? '<p class="mir-muted backups-hint">The server writes an automatic "Accounts …" copy at every periodic save, so this folder grows quickly — prune it now and then. Account backups contain password hashes: keep downloads private.</p>' : '') +
      '  <div class="ops-table-scroll"><table class="user-table backups-table">' +
      '    <thead><tr><th>Backup</th><th>Origin</th><th>Modified</th><th>Size</th><th>Version</th><th></th></tr></thead>' +
      '    <tbody>' + rows + '</tbody></table></div>' +
      '</div>';
  }

  function renderScripts(d) {
    var panel = $('[data-role="panel"]');
    var files = d.files || [];
    var f = scriptFilter.toLowerCase();
    var shown = f ? files.filter(function (x) { return String(x.name).toLowerCase().indexOf(f) >= 0; }) : files;

    var rows = shown.length ? shown.map(function (x) {
      var idx = files.indexOf(x);
      var label = x.scriptRoot ? '<span class="ops-pill backups-pill">' + esc(x.scriptRoot) + '</span> <span class="mono">' + esc(x.scriptPath) + '</span>'
        : '<span class="mono">' + esc(x.name) + '</span>';
      return '<tr>' +
        '<td class="backups-name">' + label + '</td>' +
        '<td>' + when(x.modifiedUtc) + '</td>' +
        '<td class="backups-num">' + esc(fmtSize(x.size)) + '</td>' +
        '<td class="backups-actions">' +
        '<a class="mir-btn ops-row-btn" href="' + esc(downloadHref('scripts', x.name)) + '" download>Download</a>' +
        (x.scriptRoot ? '<button type="button" class="mir-btn ops-row-btn" data-open-script="' + idx + '" title="Open the file in Scripts &amp; Files (its Backups list restores it)">Open in Scripts</button>' : '') +
        '</td></tr>';
    }).join('') : '<tr><td colspan="4" class="mir-muted">' + (files.length ? 'No backup matches the filter.' : 'No script backups yet — the Scripts & Files editor writes one every time it saves or deletes a file.') + '</td></tr>';

    panel.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>' + esc(d.label || 'Script file backups') + '</h2>' +
      '    <div class="backups-toolbar-actions">' +
      '      <input class="fd-input backups-filter" data-role="script-filter" placeholder="Filter by file…" value="' + esc(scriptFilter) + '">' +
      '      <button type="button" class="mir-btn" data-act="prune">Prune…</button>' +
      '      <button type="button" class="mir-btn" data-act="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <p class="mir-muted backups-hint">Folder <span class="mono">' + esc(d.directory || '') + '</span> · ' + esc(OpsUI.formatNumber(d.count || files.length)) + ' file(s), ' + esc(fmtSize(d.totalSize)) +
      '. Restore a script version from the Scripts & Files editor (Backups list of that file).</p>' +
      '  <div class="ops-table-scroll"><table class="user-table backups-table">' +
      '    <thead><tr><th>File</th><th>Saved</th><th>Size</th><th></th></tr></thead>' +
      '    <tbody>' + rows + '</tbody></table></div>' +
      '</div>';

    var input = $('[data-role="script-filter"]');
    if (input) {
      input.addEventListener('input', function () {
        scriptFilter = input.value;
        var pos = input.selectionStart;
        renderScripts(d);
        var again = $('[data-role="script-filter"]');
        if (again) { again.focus(); try { again.setSelectionRange(pos, pos); } catch (e) { /* ignore */ } }
      });
    }
  }

  async function createBackup(btn) {
    var kind = current;
    await OpsUI.withBusy(btn, async function () {
      var j = await postJson('/api/admin/backups/create', { kind: kind });
      if (j.__ok) { await Promise.all([loadKind(kind), loadSummary()]); }
    }, 'Creating…');
  }

  async function restoreBackup(file) {
    var kind = current;
    var d = kindData[kind] || {};
    var liveName = (d.live && d.live.name) || (kind === 'database' ? 'SystemData.db' : 'UserData.db');
    var sv = d.supportedVersions;
    if (file.version != null && sv && (file.version < sv.min || file.version > sv.max)) {
      portalToast('This backup has file version ' + file.version + '; the server only loads ' + sv.min + '–' + sv.max + '.', 'error');
      return;
    }
    var message = 'Replaces ' + liveName + ' with "' + file.name + '" (' + fmtSize(file.size) + ', ' + OpsUI.fmtDate(file.modifiedUtc) + '). ' +
      'The current file is first copied to a "Pre-restore" backup. ' +
      (kind === 'database'
        ? 'The panel\'s game-data editor copy is reloaded from the restored file — unsaved Game Data edits are discarded. '
        : 'Accounts already loaded by the panel are reloaded from the restored file, so later account or character edits build on it. ') +
      'Type the backup name to confirm.';
    var v = await portalDialog({
      title: 'Restore ' + (kind === 'database' ? 'database' : 'accounts') + ' backup',
      message: message,
      confirmLabel: 'Restore',
      danger: true,
      fields: [
        { name: 'confirm', label: 'Backup name', type: 'text', placeholder: file.name },
        { name: 'reason', label: 'Reason (recorded in the audit trail)', type: 'textarea' }
      ],
      validate: function (vals) { return vals.confirm === file.name ? null : 'Type the backup name exactly: ' + file.name; }
    });
    if (!v) return;
    var j = await postJson('/api/admin/backups/restore', { kind: kind, name: file.name, confirm: v.confirm, reason: v.reason || null });
    if (j.__status === 409) loadKind(kind);
    if (j.__ok) {
      await Promise.all([loadKind(kind), loadSummary()]);
      if (global.AdminStatus && typeof global.AdminStatus.refresh === 'function') global.AdminStatus.refresh();
    }
  }

  async function prune() {
    var kind = current;
    var v = await portalDialog({
      title: 'Prune ' + kind + ' backups',
      message: 'Deletes backups older than the given age. The newest ones are always kept. You will see the list before anything is deleted.',
      confirmLabel: 'Preview',
      fields: [
        { name: 'olderThanDays', label: 'Older than (days)', type: 'number', value: kind === 'accounts' ? 14 : 30, min: 1, max: 3650, required: true },
        { name: 'keepNewest', label: 'Always keep the newest', type: 'number', value: 10, min: 1, max: 1000, required: true }
      ],
      validate: function (vals) {
        if (!(vals.olderThanDays >= 1)) return 'Age must be at least 1 day.';
        if (!(vals.keepNewest >= 1)) return 'Keep at least 1 backup.';
        return null;
      }
    });
    if (!v) return;
    var body = { kind: kind, olderThanDays: Math.floor(v.olderThanDays), keepNewest: Math.floor(v.keepNewest), dryRun: true };
    var dry = await postJson('/api/admin/backups/prune', body, true);
    if (!dry.__ok) return;
    var plan = dry.data || {};
    if (!plan.count) { portalToast('Nothing to prune: no backup is older than ' + body.olderThanDays + ' day(s) outside the newest ' + body.keepNewest + '.', 'info'); return; }
    var names = (plan.files || []).slice(0, 8).map(function (f) { return f.name; }).join(', ') + (plan.count > 8 ? ', … (+' + (plan.count - 8) + ' more)' : '');
    var ok = await portalConfirm('Permanently delete ' + plan.count + ' backup(s), ' + fmtSize(plan.bytes) + ' in total: ' + names + '. This cannot be undone.',
      { title: 'Delete old backups', confirmLabel: 'Delete ' + plan.count, danger: true });
    if (!ok) return;
    body.dryRun = false;
    var res = await postJson('/api/admin/backups/prune', body);
    if (res.__ok) await Promise.all([loadKind(kind), loadSummary()]);
  }

  // ---- game-data history -------------------------------------------------------------------------------------------------

  function renderHistoryShell() {
    var panel = $('[data-role="panel"]');
    var f = hist.filters;
    panel.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Game-data history</h2>' +
      '    <div class="backups-toolbar-actions"><span class="mir-muted" data-role="hist-stamp"></span>' +
      '      <button type="button" class="mir-btn" data-act="hist-refresh">Refresh</button></div>' +
      '  </div>' +
      '  <p class="mir-muted backups-hint">Edits made to the game data (items, monsters, maps, …) through this panel, from the audit trail. ' +
      'Revert puts the recorded "before" values back on the current row and saves the database; like every game-data change it takes effect after the next world restart. ' +
      'Inserted or deleted rows cannot be reverted here — restore a database backup instead. Safe zones, movements, mine zones and conquest ' +
      'parts are numbered by their position in the map / conquest, so their numbers can move to another row after a delete; those edits are not revertible here.</p>' +
      '  <form class="backups-hist-filters" data-role="hist-filters" autocomplete="off">' +
      '    <label><span class="mir-muted">Table</span><input class="fd-input" name="endpoint" list="backupsEndpointList" placeholder="all" value="' + esc(f.endpoint) + '"></label>' +
      '    <datalist id="backupsEndpointList">' + ENDPOINTS.map(function (e) { return '<option value="' + esc(e) + '"></option>'; }).join('') + '</datalist>' +
      '    <label><span class="mir-muted">Row index</span><input class="fd-input" name="index" type="number" placeholder="any" value="' + esc(f.index) + '"></label>' +
      '    <label><span class="mir-muted">Admin</span><input class="fd-input" name="admin" placeholder="any" value="' + esc(f.admin) + '"></label>' +
      '    <label><span class="mir-muted">Window</span><select class="fd-input" name="days">' +
      [[7, 'Last 7 days'], [30, 'Last 30 days'], [90, 'Last 90 days'], [365, 'Last year'], [0, 'Everything']].map(function (o) {
        return '<option value="' + o[0] + '"' + (Number(f.days) === o[0] ? ' selected' : '') + '>' + esc(o[1]) + '</option>';
      }).join('') + '</select></label>' +
      '    <div class="audit-filter-buttons"><button type="submit" class="mir-btn primary">Search</button>' +
      '      <button type="button" class="mir-btn" data-act="hist-clear">Clear</button></div>' +
      '  </form>' +
      '  <div class="ops-table-scroll"><table class="user-table backups-hist-table">' +
      '    <thead><tr><th>Time</th><th>Admin</th><th>Row</th><th>Changes</th><th>Reason</th><th></th></tr></thead>' +
      '    <tbody data-role="hist-rows"><tr><td colspan="6" class="mir-muted">Loading…</td></tr></tbody></table></div>' +
      '  <div class="audit-footer"><span class="mir-muted" data-role="hist-count"></span>' +
      '    <button type="button" class="mir-btn hidden" data-act="hist-more">Load older</button></div>' +
      '</div>';

    var form = $('[data-role="hist-filters"]');
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      readHistFilters();
      writeHistHash();
      loadHistory(false);
    });
    form.elements.days.addEventListener('change', function () { readHistFilters(); loadHistory(false); });
    return loadHistory(false);
  }

  function readHistFilters() {
    var form = $('[data-role="hist-filters"]');
    if (!form) return;
    hist.filters = {
      endpoint: form.elements.endpoint.value.trim(),
      index: form.elements.index.value.trim(),
      admin: form.elements.admin.value.trim(),
      days: Number(form.elements.days.value)
    };
  }

  function writeHistHash() {
    if (!global.portalSetHashParams) return;
    global.portalSetHashParams({ tab: 'history', endpoint: hist.filters.endpoint || null, index: hist.filters.index || null }, true);
    lastDeepKey = deepKey({ tab: 'history', endpoint: hist.filters.endpoint || null, index: hist.filters.index || null });
  }

  async function loadHistory(more) {
    var my = ++hist.seq;
    if (!more) { hist.items = []; hist.next = null; }
    var f = hist.filters;
    var p = ['limit=' + HISTORY_PAGE, 'days=' + (Number(f.days) >= 0 ? Number(f.days) : 30)];
    if (f.endpoint) p.push('endpoint=' + encodeURIComponent(f.endpoint));
    if (f.index !== '' && f.index != null) p.push('index=' + encodeURIComponent(f.index));
    if (f.admin) p.push('admin=' + encodeURIComponent(f.admin));
    if (more && hist.next) p.push('before=' + encodeURIComponent(hist.next));
    try {
      var j = await OpsUI.fetchJson('/api/admin/db/history?' + p.join('&'));
      if (my !== hist.seq) return;
      j = j && j.items ? j : (OpsUI.unwrap(j) || {});
      hist.items = hist.items.concat(j.items || []);
      hist.next = j.next || null;
      renderHistoryRows();
      var st = $('[data-role="hist-stamp"]');
      if (st) st.textContent = 'Updated ' + new Date().toLocaleTimeString();
    } catch (e) {
      if (my !== hist.seq) return;
      var body = $('[data-role="hist-rows"]');
      if (body) body.innerHTML = '<tr><td colspan="6" class="ops-pill-danger">' + esc('Could not load the history: ' + ((e && e.message) || e)) + '</td></tr>';
    }
  }

  function kindPill(kind) {
    var cls = kind === 'delete' ? 'ops-pill ops-pill-danger' : kind === 'insert' ? 'ops-pill ops-pill-ok' : kind === 'update' ? 'ops-pill ops-pill-warn' : 'ops-pill backups-pill';
    return '<span class="' + cls + '">' + esc(kind || 'other') + '</span>';
  }

  function changeLines(item) {
    var ch = item.changes || [];
    if (!ch.length) return '<span class="mir-muted">' + esc(item.action || '') + (item.target ? ' · ' + esc(item.target) : '') + '</span>';
    var fv = global.AuditView ? global.AuditView.fmtValue : function (v) { return esc(v == null ? '' : typeof v === 'object' ? JSON.stringify(v) : v); };
    var lines = ch.slice(0, 8).map(function (c) {
      var b = Object.prototype.hasOwnProperty.call(c, 'before') ? c.before : undefined;
      return '<div class="db-hist-change"><b>' + esc(c.field) + '</b> ' +
        (item.kind === 'insert' ? '' : '<span class="b">' + fv(b) + '</span> → ') +
        '<span class="a">' + fv(Object.prototype.hasOwnProperty.call(c, 'after') ? c.after : undefined) + '</span></div>';
    }).join('');
    if (ch.length > 8) lines += '<div class="mir-muted">+' + (ch.length - 8) + ' more field(s)</div>';
    return lines;
  }

  function renderHistoryRows() {
    var body = $('[data-role="hist-rows"]');
    if (!body) return;
    if (!hist.items.length) {
      body.innerHTML = '<tr><td colspan="6" class="mir-muted">No game-data edits recorded for these filters.</td></tr>';
    } else {
      body.innerHTML = hist.items.map(function (it, i) {
        var row = it.endpoint
          ? '<a href="#" class="ops-link" data-open-row="' + i + '">' + esc(it.endpoint) + (it.index != null ? ' #' + esc(it.index) : '') + '</a>'
          : '<span class="mir-muted">—</span>';
        var actions = it.revertible
          ? '<button type="button" class="mir-btn ops-row-btn danger" data-revert="' + i + '">Revert…</button>'
          : '<button type="button" class="mir-btn ops-row-btn" disabled title="' + esc(it.positionKeyed
            ? 'Rows of this table are numbered by their position in the parent list, so #' + it.index + ' may be a different row now: edit it in Game Data instead'
            : it.kind === 'insert' || it.kind === 'delete'
            ? 'Inserted / deleted rows cannot be reverted here'
            : 'The audit entry does not record the previous values') + '">Revert…</button>';
        return '<tr>' +
          '<td>' + when(it.ts) + '</td>' +
          '<td>' + esc(it.admin || '') + '</td>' +
          '<td>' + kindPill(it.kind) + '<br>' + row + '</td>' +
          '<td class="backups-changes">' + changeLines(it) + '</td>' +
          '<td class="audit-reason">' + esc(it.reason || '') + '</td>' +
          '<td class="backups-actions">' + actions + '</td>' +
          '</tr>';
      }).join('');
    }
    var count = $('[data-role="hist-count"]');
    if (count) count.textContent = hist.items.length + ' change(s) loaded' + (hist.next ? ' (more available)' : '');
    var more = $('[data-act="hist-more"]');
    if (more) more.classList.toggle('hidden', !hist.next);
  }

  /** Loose equality for "has the field changed since the audited edit?" (numbers vs numeric strings, null vs ''). */
  function sameValue(a, b) {
    if (a === b) return true;
    if (a == null && b == null) return true;
    if (a == null || b == null) return String(a == null ? '' : a) === String(b == null ? '' : b);
    if (typeof a === 'object' || typeof b === 'object') return JSON.stringify(a) === JSON.stringify(b);
    return String(a) === String(b);
  }

  async function revert(item) {
    if (!item || !item.revertible || item.positionKeyed) return;
    if (global.AdminInspect.confirmReplace && !(await global.AdminInspect.confirmReplace('backups'))) return;
    var rows;
    try {
      var j = await OpsUI.fetchJson('/api/admin/db/' + encodeURIComponent(item.endpoint));
      rows = (j && j.rows) || ((OpsUI.unwrap(j) || {}).rows) || [];
    } catch (e) {
      portalToast('Could not read ' + item.endpoint + ': ' + ((e && e.message) || e), 'error');
      return;
    }
    var row = null;
    for (var r = 0; r < rows.length; r++) {
      if (Number(rows[r].Index) === Number(item.index)) { row = rows[r]; break; }
    }
    if (!row) { portalToast(item.endpoint + ' #' + item.index + ' no longer exists — nothing to revert.', 'error'); return; }

    var lines = (item.changes || []).map(function (c) {
      var present = Object.prototype.hasOwnProperty.call(row, c.field);
      var before = Object.prototype.hasOwnProperty.call(c, 'before') ? c.before : null;
      var after = Object.prototype.hasOwnProperty.call(c, 'after') ? c.after : undefined;
      return {
        field: c.field, present: present, current: present ? row[c.field] : undefined, before: before, after: after,
        drift: present && after !== undefined && !sameValue(row[c.field], after),
        noop: present && sameValue(row[c.field], before)
      };
    });
    var usable = lines.filter(function (l) { return l.present && !l.noop; });
    if (!usable.length) {
      portalToast(lines.some(function (l) { return l.present; })
        ? 'The row already has the recorded "before" values — nothing to revert.'
        : 'None of the recorded fields exist on the current row.', 'info');
      return;
    }

    var fv = global.AuditView ? global.AuditView.fmtValue : function (v) { return esc(v == null ? '' : JSON.stringify(v)); };
    var node = document.createElement('div');
    node.className = 'backups-revert';
    node.innerHTML =
      '<p>Revert <b>' + esc(item.endpoint) + ' #' + esc(item.index) + '</b> to the values before the edit by <b>' + esc(item.admin || '') + '</b> on ' + esc(OpsUI.fmtDate(item.ts)) + '.</p>' +
      (lines.some(function (l) { return l.drift; }) ? '<p class="backups-note warn">Some fields changed again since that edit (marked below); reverting overwrites those newer values too.</p>' : '') +
      '<table class="user-table audit-diff"><thead><tr><th>Field</th><th>Current</th><th>Revert to</th></tr></thead><tbody>' +
      lines.map(function (l) {
        var note = !l.present ? ' <span class="ops-pill ops-pill-warn">not in table</span>' : l.noop ? ' <span class="mir-muted">(already)</span>' : l.drift ? ' <span class="ops-pill ops-pill-warn">changed since</span>' : '';
        return '<tr><td class="mono">' + esc(l.field) + note + '</td><td class="audit-before">' + fv(l.current) + '</td><td class="audit-after">' + fv(l.before) + '</td></tr>';
      }).join('') + '</tbody></table>' +
      '<label class="portal-form-field backups-revert-reason"><span class="mir-muted">Reason (recorded in the audit trail)</span>' +
      '<input class="fd-input" data-role="revert-reason"></label>' +
      '<div class="portal-confirm-actions"><button type="button" class="mir-btn" data-role="revert-cancel">Cancel</button>' +
      '<button type="button" class="mir-btn danger" data-role="revert-go">Revert &amp; save database</button></div>';
    node.querySelector('[data-role="revert-reason"]').value = 'Revert of ' + (item.action || 'edit') + ' ' + (item.id || '');

    global.AdminInspect.open({ title: 'Revert ' + item.endpoint + ' #' + item.index, node: node });
    node.querySelector('[data-role="revert-cancel"]').addEventListener('click', function () { global.AdminInspect.close(); });
    var go = node.querySelector('[data-role="revert-go"]');
    go.addEventListener('click', function () {
      OpsUI.withBusy(go, async function () {
        var patch = { Index: row.Index };
        if (row.__rev != null) patch.__rev = row.__rev;
        usable.forEach(function (l) { patch[l.field] = l.before; });
        var reason = node.querySelector('[data-role="revert-reason"]').value.trim();
        var res = global.DbSave && typeof global.DbSave.persist === 'function'
          ? await global.DbSave.persist(item.endpoint, [patch], { reason: reason || null })
          : { ok: false };
        if (res && res.ok) {
          global.AdminInspect.close();
          if (global.DbSave.notifySaved) global.DbSave.notifySaved(item.endpoint);
          loadHistory(false);
        }
      }, 'Reverting…');
    });
  }

  // ---- events ----------------------------------------------------------------------------------------------------------

  function onPanelClick(e) {
    var t = e.target;
    var act = t.closest('[data-act]');
    if (act) {
      var a = act.getAttribute('data-act');
      if (a === 'create') { createBackup(act); return; }
      if (a === 'prune') { prune(); return; }
      if (a === 'refresh') { OpsUI.withBusy(act, function () { return Promise.all([loadKind(current), loadSummary()]); }); return; }
      if (a === 'server') { global.portalShowSection('server'); return; }
      if (a === 'hist-refresh') { OpsUI.withBusy(act, function () { return loadHistory(false); }); return; }
      if (a === 'hist-more') { OpsUI.withBusy(act, function () { return loadHistory(true); }); return; }
      if (a === 'hist-clear') {
        hist.filters = { endpoint: '', index: '', admin: '', days: 30 };
        writeHistHash();
        renderHistoryShell();
        return;
      }
    }
    var rs = t.closest('[data-restore]');
    if (rs && !rs.disabled) {
      var d = kindData[current];
      var file = d && d.files ? d.files[Number(rs.getAttribute('data-restore'))] : null;
      if (file) restoreBackup(file);
      return;
    }
    var os = t.closest('[data-open-script]');
    if (os) {
      var sd = kindData.scripts;
      var sf = sd && sd.files ? sd.files[Number(os.getAttribute('data-open-script'))] : null;
      if (sf) global.portalOpenDeepLink('scripts', { root: sf.scriptRoot, path: sf.scriptPath });
      return;
    }
    var rv = t.closest('[data-revert]');
    if (rv) {
      var item = hist.items[Number(rv.getAttribute('data-revert'))];
      OpsUI.withBusy(rv, function () { return revert(item); });
      return;
    }
    var or = t.closest('[data-open-row]');
    if (or) {
      e.preventDefault();
      var it = hist.items[Number(or.getAttribute('data-open-row'))];
      if (it && it.endpoint) global.portalOpenDeepLink('gamedata', { table: it.endpoint, index: it.index == null ? null : String(it.index) });
    }
  }

  function onStatus(st) {
    if (!st || !root) return;
    var w = st.world || st.worldState;
    if (!w || w === lastWorld) return;
    lastWorld = w;
    renderWorld(w);
    if (OpsUI.sectionVisible('backups') && (current === 'database' || current === 'accounts')) loadKind(current);
  }

  // ---- registration ----------------------------------------------------------------------------------------------------

  function applyParams(p) {
    p = p || {};
    var tab = p.tab && TABS.some(function (t) { return t.id === p.tab; }) ? p.tab : null;
    if (p.endpoint != null || p.index != null) tab = 'history';
    if (tab === 'history') {
      hist.filters = {
        endpoint: p.endpoint || '',
        index: p.index != null ? String(p.index) : '',
        admin: p.admin || '',
        days: p.days != null && p.days !== '' && !isNaN(Number(p.days)) ? Number(p.days) : (hist.filters.days || 30)
      };
    }
    return tab;
  }

  var deepTab = null;
  var lastDeepKey = null;

  function deepKey(p) {
    p = p || {};
    return [p.tab || '', p.endpoint || '', p.index == null ? '' : String(p.index), p.admin || '', p.days == null ? '' : String(p.days)].join('|');
  }

  global.AdminSections.register('backups', {
    load: function (el, params) {
      root = el;
      shell();
      if (global.AdminStatus && global.AdminStatus.onChange) global.AdminStatus.onChange(onStatus);
      lastDeepKey = deepKey(params);
      deepTab = applyParams(params) || 'database';
      loadSummary();
      return selectTab(deepTab, false);
    },
    show: function () {
      if (deepTab) { deepTab = null; return null; }   // load() just rendered it
      loadSummary();
      return loadCurrent();
    }
  });

  if (global.portalRegisterDeepLink) {
    global.portalRegisterDeepLink('backups', function (params) {
      if (!root) return;
      var p = params || {};
      var key = deepKey(p);
      if (key === lastDeepKey) return;   // already applied (load, or the hash this section wrote itself)
      lastDeepKey = key;
      var tab = applyParams(p);
      if (!tab) return;
      if (tab !== current || tab === 'history') selectTab(tab, false);
    });
  }
})(window);
