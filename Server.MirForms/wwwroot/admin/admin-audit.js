/**
 * Audit Trail section (#section-auditlog) — B11.
 *
 * Browses the web admin audit log (GET /api/admin/audit, cursor-paged with `before`), with filters (action group / exact action,
 * admin, free text, time window), an expandable details row (field before -> after diff when the details carry one, plus the
 * pretty-printed JSON), a summary of action groups (GET /api/admin/audit/actions), CSV export of the loaded rows or of
 * everything matching (GET /api/admin/audit/export) and deep links:
 *   #section=auditlog&admin=<name>&action=<prefix or action>&q=<text>&days=<n>
 * Read-only: this section never mutates anything. All dynamic text is escaped.
 */
(function (global) {
  'use strict';

  var PAGE = 200;
  var DAY_CHOICES = [
    { value: 1, label: 'Last 24 hours' }, { value: 7, label: 'Last 7 days' }, { value: 30, label: 'Last 30 days' },
    { value: 90, label: 'Last 90 days' }, { value: 365, label: 'Last year' }, { value: 0, label: 'Everything' }
  ];

  var root = null;
  var state = { entries: [], next: null, loading: false, expanded: Object.create(null), filters: { action: '', admin: '', q: '', days: 7 } };
  var summary = null;
  var seq = 0;
  var freshLoad = false;

  function esc(s) { return OpsUI.esc(s); }
  function $(sel) { return root ? root.querySelector(sel) : null; }

  // ---- pure helpers (also used by the Backups history tab through window.AuditView) -------------------------------------

  function pick(obj, keys) {
    if (!obj || typeof obj !== 'object') return undefined;
    for (var i = 0; i < keys.length; i++) {
      if (Object.prototype.hasOwnProperty.call(obj, keys[i])) return obj[keys[i]];
      var lower = keys[i].toLowerCase();
      for (var k in obj) {
        if (Object.prototype.hasOwnProperty.call(obj, k) && k.toLowerCase() === lower) return obj[k];
      }
    }
    return undefined;
  }
  function has(obj, keys) { return pick(obj, keys) !== undefined; }

  var BEFORE = ['before', 'old', 'oldValue', 'from', 'was'];
  var AFTER = ['after', 'new', 'newValue', 'to', 'value'];
  var FIELD = ['field', 'name', 'column', 'key', 'property'];

  /** Field changes found in an audit details object: [{field, before, after, hasBefore}] or null when it carries none. */
  function changesOf(d) {
    if (!d || typeof d !== 'object' || Array.isArray(d)) return null;
    var out = [];
    var lists = ['changes', 'fields', 'diff', 'changed'];
    for (var i = 0; i < lists.length; i++) {
      var c = pick(d, [lists[i]]);
      if (Array.isArray(c)) {
        c.forEach(function (el) {
          if (!el || typeof el !== 'object') return;
          var f = pick(el, FIELD);
          if (f == null || f === '__rev') return;
          out.push({ field: String(f), before: pick(el, BEFORE), after: pick(el, AFTER), hasBefore: true });
        });
      } else if (c && typeof c === 'object') {
        Object.keys(c).forEach(function (k) {
          if (k === '__rev') return;
          var v = c[k];
          if (v && typeof v === 'object' && !Array.isArray(v) && (has(v, BEFORE) || has(v, AFTER))) out.push({ field: k, before: pick(v, BEFORE), after: pick(v, AFTER), hasBefore: true });
          else if (Array.isArray(v) && v.length === 2) out.push({ field: k, before: v[0], after: v[1], hasBefore: true });
          else out.push({ field: k, before: undefined, after: v, hasBefore: false });
        });
      }
      if (out.length) return out;
    }
    var b = pick(d, BEFORE), a = pick(d, AFTER);
    var single = pick(d, FIELD);
    if (single != null && (b !== undefined || a !== undefined) && !(b && typeof b === 'object') && !(a && typeof a === 'object')) {
      return [{ field: String(single), before: b, after: a, hasBefore: true }];
    }
    var bo = b && typeof b === 'object' && !Array.isArray(b) ? b : null;
    var ao = a && typeof a === 'object' && !Array.isArray(a) ? a : null;
    if (bo || ao) {
      var names = [];
      [bo, ao].forEach(function (o) { if (o) Object.keys(o).forEach(function (k) { if (names.indexOf(k) < 0) names.push(k); }); });
      names.forEach(function (k) {
        if (k === '__rev') return;
        var bv = bo ? bo[k] : undefined, av = ao ? ao[k] : undefined;
        if (bo && ao && JSON.stringify(bv) === JSON.stringify(av)) return;
        out.push({ field: k, before: bv, after: av, hasBefore: !!bo });
      });
      return out.length ? out : null;
    }
    return null;
  }

  function fmtValue(v) {
    if (v === undefined) return '<span class="mir-muted">(not recorded)</span>';
    if (v === null) return '<span class="mir-muted">(empty)</span>';
    if (typeof v === 'object') return '<code>' + esc(JSON.stringify(v)) + '</code>';
    var s = String(v);
    if (s.length > 160) s = s.slice(0, 157) + '…';
    return esc(s).replace(/\r?\n/g, ' ↵ ');
  }

  /** Before -> after table for a change list (escaped HTML). */
  function changesTable(changes) {
    if (!changes || !changes.length) return '';
    return '<table class="user-table audit-diff"><thead><tr><th>Field</th><th>Before</th><th>After</th></tr></thead><tbody>' +
      changes.map(function (c) {
        return '<tr><td class="mono">' + esc(c.field) + '</td><td class="audit-before">' + fmtValue(c.before) + '</td><td class="audit-after">' + fmtValue(c.after) + '</td></tr>';
      }).join('') + '</tbody></table>';
  }

  function prettyJson(v) {
    try { return JSON.stringify(v, null, 2); } catch (e) { return String(v); }
  }

  function groupOf(action) {
    var a = String(action || '');
    var i = a.indexOf('.');
    return i > 0 ? a.slice(0, i) : a;
  }

  /** Pill colour by verb: destructive red, warnings amber, the rest neutral. */
  function actionClass(action) {
    var a = String(action || '').toLowerCase();
    if (/(^|\.)(ban|delete|remove|kill|kick|restore|prune|shutdown|stop|clear|revoke|wipe|restore-failed)\b/.test(a) || /failed|refused/.test(a)) return 'ops-pill ops-pill-danger';
    if (/(^|\.)(reboot|update|edit|set|save|create|start|chatban|maintenance|admin|password)/.test(a)) return 'ops-pill ops-pill-warn';
    return 'ops-pill audit-pill';
  }

  function query(f, before, path) {
    var p = [];
    if (path !== '/api/admin/audit/export') p.push('limit=' + PAGE);
    if (f.action) p.push('action=' + encodeURIComponent(f.action));
    if (f.admin) p.push('admin=' + encodeURIComponent(f.admin));
    if (f.q) p.push('q=' + encodeURIComponent(f.q));
    p.push('days=' + (Number(f.days) >= 0 ? Number(f.days) : 7));
    if (before) p.push('before=' + encodeURIComponent(before));
    return (path || '/api/admin/audit') + '?' + p.join('&');
  }

  // ---- rendering -------------------------------------------------------------------------------------------------------

  function shell() {
    root.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Audit Trail</h2>' +
      '    <div class="audit-toolbar-actions">' +
      '      <span class="mir-muted" data-role="stamp"></span>' +
      '      <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '      <button type="button" class="mir-btn" data-role="csv" title="Download the loaded rows as CSV">CSV (loaded)</button>' +
      '      <a class="mir-btn" data-role="export" href="#" title="Server-side export of every matching entry (up to 10,000)">Export all</a>' +
      '    </div>' +
      '  </div>' +
      '  <p class="mir-muted audit-intro">Every change made through this panel is recorded here: who, from which IP, what, and the reason given. ' +
      'Older days are read from <code>Logs\\WebAdmin\\audit-*.jsonl</code>.</p>' +
      '  <form class="audit-filters" data-role="filters" autocomplete="off">' +
      '    <label><span class="mir-muted">Action</span><input class="fd-input" name="action" list="auditActionList" placeholder="e.g. gm or account.ban"></label>' +
      '    <datalist id="auditActionList"></datalist>' +
      '    <label><span class="mir-muted">Admin</span><input class="fd-input" name="admin" list="auditAdminList" placeholder="any"></label>' +
      '    <datalist id="auditAdminList"></datalist>' +
      '    <label class="audit-filter-q"><span class="mir-muted">Search</span><input class="fd-input" name="q" placeholder="target, reason, IP, details…"></label>' +
      '    <label><span class="mir-muted">Window</span><select class="fd-input" name="days">' +
      DAY_CHOICES.map(function (d) { return '<option value="' + d.value + '">' + esc(d.label) + '</option>'; }).join('') +
      '    </select></label>' +
      '    <div class="audit-filter-buttons"><button type="submit" class="mir-btn primary">Search</button>' +
      '      <button type="button" class="mir-btn" data-role="clear">Clear</button></div>' +
      '  </form>' +
      '  <div class="audit-groups" data-role="groups"></div>' +
      '  <div class="ops-table-scroll"><table class="user-table audit-table">' +
      '    <thead><tr><th></th><th>Time</th><th>Admin</th><th>Action</th><th>Target</th><th>Reason</th><th>IP</th></tr></thead>' +
      '    <tbody data-role="rows"><tr><td colspan="7" class="mir-muted">Loading…</td></tr></tbody>' +
      '  </table></div>' +
      '  <div class="audit-footer"><span class="mir-muted" data-role="count"></span>' +
      '    <button type="button" class="mir-btn hidden" data-role="more">Load older</button></div>' +
      '</div>';

    var form = $('[data-role="filters"]');
    form.addEventListener('submit', function (e) { e.preventDefault(); applyForm(true); });
    form.elements.days.addEventListener('change', function () { applyForm(true); });
    $('[data-role="clear"]').addEventListener('click', function () {
      setForm({ action: '', admin: '', q: '', days: 7 });
      applyForm(true);
    });
    $('[data-role="refresh"]').addEventListener('click', function (e) {
      OpsUI.withBusy(e.currentTarget, function () { return Promise.all([search(false), loadSummary()]); });
    });
    $('[data-role="more"]').addEventListener('click', function (e) {
      OpsUI.withBusy(e.currentTarget, function () { return search(true); });
    });
    $('[data-role="csv"]').addEventListener('click', exportLoaded);
    $('[data-role="export"]').addEventListener('click', function (e) {
      e.preventDefault();
      var a = document.createElement('a');
      a.href = query(state.filters, null, '/api/admin/audit/export');
      a.download = '';
      document.body.appendChild(a);
      a.click();
      setTimeout(function () { if (a.parentNode) a.parentNode.removeChild(a); }, 0);
    });
    $('[data-role="rows"]').addEventListener('click', onRowsClick);
    $('[data-role="groups"]').addEventListener('click', function (e) {
      var b = e.target.closest('[data-group]');
      if (!b) return;
      var g = b.getAttribute('data-group');
      setForm(Object.assign({}, state.filters, { action: state.filters.action === g ? '' : g }));
      applyForm(true);
    });
  }

  function setForm(f) {
    var form = $('[data-role="filters"]');
    if (!form) return;
    form.elements.action.value = f.action || '';
    form.elements.admin.value = f.admin || '';
    form.elements.q.value = f.q || '';
    var days = String(f.days == null || f.days === '' ? 7 : f.days);
    if (!DAY_CHOICES.some(function (d) { return String(d.value) === days; })) {
      var opt = document.createElement('option');
      opt.value = days;
      opt.textContent = 'Last ' + days + ' days';
      form.elements.days.appendChild(opt);
    }
    form.elements.days.value = days;
  }

  function readForm() {
    var form = $('[data-role="filters"]');
    return {
      action: form.elements.action.value.trim(),
      admin: form.elements.admin.value.trim(),
      q: form.elements.q.value.trim(),
      days: Number(form.elements.days.value)
    };
  }

  function applyForm(writeHash) {
    state.filters = readForm();
    if (writeHash && global.portalSetHashParams) {
      global.portalSetHashParams({
        action: state.filters.action || null,
        admin: state.filters.admin || null,
        q: state.filters.q || null,
        name: null, // a player chip's deep link maps name -> q; drop it so a cleared search stays cleared
        days: state.filters.days === 7 ? null : String(state.filters.days)
      }, true);
    }
    renderGroups();
    return search(false);
  }

  async function search(more) {
    var my = ++seq;
    if (!more) {
      state.entries = [];
      state.next = null;
      state.expanded = Object.create(null);
    }
    state.loading = true;
    try {
      var j = await OpsUI.fetchJson(query(state.filters, more ? state.next : null));
      if (my !== seq) return;
      j = j && j.entries ? j : (OpsUI.unwrap(j) || {});
      state.entries = state.entries.concat(j.entries || []);
      state.next = j.next || null;
      render();
      var stamp = $('[data-role="stamp"]');
      if (stamp) stamp.textContent = 'Updated ' + new Date().toLocaleTimeString();
    } catch (e) {
      if (my !== seq) return;
      var body = $('[data-role="rows"]');
      if (body) body.innerHTML = '<tr><td colspan="7" class="ops-pill-danger">' + esc('Could not load the audit log: ' + ((e && e.message) || e)) + '</td></tr>';
      portalToast('Audit log: ' + ((e && e.message) || e), 'error');
    } finally {
      if (my === seq) state.loading = false;
    }
  }

  function rowHtml(e, i) {
    var open = !!state.expanded[e.id || i];
    var hasDetails = e.details != null && !(typeof e.details === 'object' && !Array.isArray(e.details) && !Object.keys(e.details).length);
    var main = '<tr class="audit-row' + (open ? ' open' : '') + '" data-i="' + i + '">' +
      '<td><button type="button" class="mir-btn ops-row-btn audit-toggle" data-toggle="' + i + '" aria-expanded="' + open + '" title="Details">' + (open ? '▾' : '▸') + '</button></td>' +
      '<td title="' + esc(OpsUI.fmtDate(e.ts)) + '"><span class="audit-time">' + esc(OpsUI.fmtDate(e.ts)) + '</span><br><span class="mir-muted">' + esc(OpsUI.ago(e.ts)) + '</span></td>' +
      '<td><a href="#" class="ops-link" data-filter-admin="' + esc(e.admin || '') + '">' + esc(e.admin || '') + '</a></td>' +
      '<td><a href="#" data-filter-action="' + esc(e.action || '') + '"><span class="' + actionClass(e.action) + '">' + esc(e.action || '') + '</span></a></td>' +
      '<td class="audit-target">' + esc(e.target || '') + (hasDetails && !open ? ' <span class="mir-muted">…</span>' : '') + '</td>' +
      '<td class="audit-reason">' + esc(e.reason || '') + '</td>' +
      '<td class="mono">' + esc(e.ip || '') + '</td>' +
      '</tr>';
    if (!open) return main;
    var changes = changesOf(e.details);
    var detail = '<div class="audit-detail">' +
      '<dl class="kv-list"><dt>When (UTC)</dt><dd class="mono">' + esc(e.ts || '') + '</dd>' +
      '<dt>Entry id</dt><dd class="mono">' + esc(e.id || '') + '</dd>' +
      (e.reason ? '<dt>Reason</dt><dd>' + esc(e.reason) + '</dd>' : '') + '</dl>' +
      (changes && changes.length ? '<h4>Changes</h4>' + changesTable(changes) : '') +
      (hasDetails ? '<details' + (changes && changes.length ? '' : ' open') + '><summary>Details JSON</summary><pre class="audit-json">' + esc(prettyJson(e.details)) + '</pre></details>'
        : '<p class="mir-muted">No details recorded.</p>') +
      '<div class="audit-detail-actions">' +
      (groupOf(e.action) === 'db' ? '<button type="button" class="mir-btn" data-open-history="' + i + '">Open in game-data history</button>' : '') +
      '<button type="button" class="mir-btn" data-copy="' + i + '">Copy JSON</button></div>' +
      '</div>';
    return main + '<tr class="audit-detail-row"><td></td><td colspan="6">' + detail + '</td></tr>';
  }

  function render() {
    var body = $('[data-role="rows"]');
    if (!body) return;
    if (!state.entries.length) {
      body.innerHTML = '<tr><td colspan="7" class="mir-muted">No audit entries match these filters.</td></tr>';
    } else {
      body.innerHTML = state.entries.map(rowHtml).join('');
    }
    var count = $('[data-role="count"]');
    if (count) count.textContent = state.entries.length + ' entr' + (state.entries.length === 1 ? 'y' : 'ies') + ' loaded' + (state.next ? ' (more available)' : '');
    var more = $('[data-role="more"]');
    if (more) more.classList.toggle('hidden', !state.next);
  }

  function onRowsClick(ev) {
    var t = ev.target;
    var tog = t.closest('[data-toggle]');
    if (tog) {
      var i = Number(tog.getAttribute('data-toggle'));
      var e = state.entries[i];
      if (!e) return;
      var key = e.id || i;
      if (state.expanded[key]) delete state.expanded[key]; else state.expanded[key] = true;
      render();
      return;
    }
    var fa = t.closest('[data-filter-admin]');
    if (fa) {
      ev.preventDefault();
      setForm(Object.assign({}, state.filters, { admin: fa.getAttribute('data-filter-admin') }));
      applyForm(true);
      return;
    }
    var fc = t.closest('[data-filter-action]');
    if (fc) {
      ev.preventDefault();
      setForm(Object.assign({}, state.filters, { action: fc.getAttribute('data-filter-action') }));
      applyForm(true);
      return;
    }
    var cp = t.closest('[data-copy]');
    if (cp) {
      var entry = state.entries[Number(cp.getAttribute('data-copy'))];
      var text = prettyJson(entry);
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { portalToast('Copied.', 'ok'); }, function () { OpsUI.download('audit-entry.json', text, 'application/json'); });
      } else {
        OpsUI.download('audit-entry.json', text, 'application/json');
      }
      return;
    }
    var oh = t.closest('[data-open-history]');
    if (oh) {
      var en = state.entries[Number(oh.getAttribute('data-open-history'))];
      var target = String((en && en.target) || '');
      var m = /^\s*(?:db[.:/])?([A-Za-z][A-Za-z0-9_-]*)\s*(?:[#/:\[\s]\s*#?(-?\d+))?/.exec(target);
      var d = (en && en.details) || {};
      var ep = pick(d, ['endpoint', 'table', 'collection']) || (m ? m[1] : '');
      var idx = pick(d, ['index', 'rowIndex']);
      if (idx == null && m && m[2]) idx = m[2];
      global.portalOpenDeepLink('backups', { tab: 'history', endpoint: ep || null, index: idx == null ? null : String(idx) });
    }
  }

  function exportLoaded() {
    if (!state.entries.length) { portalToast('Nothing loaded to export.', 'info'); return; }
    var rows = state.entries.map(function (e) {
      return [e.ts, e.admin, e.ip, e.action, e.target, e.reason, e.details == null ? '' : JSON.stringify(e.details), e.id];
    });
    var csv = OpsUI.toCsv(['ts', 'admin', 'ip', 'action', 'target', 'reason', 'details', 'id'], rows.map(function (r) {
      return r.map(function (v) {
        var s = v == null ? '' : String(v);
        return /^[=+\-@\t\r]/.test(s) ? "'" + s : s;   // spreadsheet formula injection
      });
    }));
    OpsUI.download('mircraft-audit-' + new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-') + '.csv', '﻿' + csv, 'text/csv;charset=utf-8');
  }

  async function loadSummary() {
    try {
      var j = await OpsUI.fetchJson('/api/admin/audit/actions?days=90');
      summary = j && j.actions ? j : OpsUI.unwrap(j);
    } catch (e) {
      summary = null;
    }
    var al = document.getElementById('auditActionList');
    var ad = document.getElementById('auditAdminList');
    if (summary && al) {
      var opts = (summary.groups || []).map(function (g) { return g.group + '.'; })
        .concat((summary.actions || []).map(function (a) { return a.action; }));
      al.innerHTML = opts.map(function (o) { return '<option value="' + esc(o) + '"></option>'; }).join('');
    }
    if (summary && ad) {
      ad.innerHTML = (summary.admins || []).map(function (a) { return '<option value="' + esc(a.admin) + '"></option>'; }).join('');
    }
    renderGroups();
  }

  function renderGroups() {
    var el = $('[data-role="groups"]');
    if (!el) return;
    if (!summary || !(summary.groups || []).length) { el.innerHTML = ''; return; }
    var cur = String(state.filters.action || '').replace(/\.?\*?$/, '').toLowerCase();
    el.innerHTML = '<span class="mir-muted">Last 90 days:</span> ' + summary.groups.map(function (g) {
      var on = cur === String(g.group).toLowerCase();
      return '<button type="button" class="mir-btn ops-row-btn audit-group' + (on ? ' primary' : '') + '" data-group="' + esc(g.group) + '">' +
        esc(g.group) + ' <span class="audit-group-count">' + esc(OpsUI.formatNumber(g.count)) + '</span></button>';
    }).join(' ');
  }

  // ---- registration ----------------------------------------------------------------------------------------------------

  function fromParams(p) {
    p = p || {};
    return {
      action: p.action || '',
      admin: p.admin || '',
      q: p.q || p.name || '',
      days: p.days != null && p.days !== '' && !isNaN(Number(p.days)) ? Number(p.days) : 7
    };
  }

  function paramsDiffer(f) {
    var s = state.filters;
    return s.action !== f.action || s.admin !== f.admin || s.q !== f.q || Number(s.days) !== Number(f.days);
  }

  global.AdminSections.register('auditlog', {
    load: function (el, params) {
      root = el;
      shell();
      state.filters = fromParams(params);
      setForm(state.filters);
      loadSummary();
      freshLoad = true;
      return search(false);
    },
    show: function () {
      // Deep links (below) re-run the search when their parameters change; a plain revisit refreshes the first page
      // (unless older pages were loaded, which a refresh would throw away).
      if (freshLoad) { freshLoad = false; return null; }
      if (!state.loading && state.entries.length <= PAGE) return search(false);
      return null;
    }
  });

  if (global.portalRegisterDeepLink) {
    global.portalRegisterDeepLink('auditlog', function (params) {
      if (!root) return;
      // Plain sidebar navigation also dispatches here with only {section}: keep the admin's filters (show() refreshes).
      var p = params || {};
      if (['action', 'admin', 'q', 'name', 'days'].every(function (k) { return p[k] == null || p[k] === ''; })) return;
      var f = fromParams(params);
      if (!paramsDiffer(f)) return;
      setForm(f);
      state.filters = f;
      renderGroups();
      search(false);
    });
  }

  global.AuditView = { changesOf: changesOf, changesTable: changesTable, fmtValue: fmtValue, actionClass: actionClass, query: query };
})(window);
