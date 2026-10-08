/**
 * Balance section (#section-balance) — MirCraft Admin feature B7.
 * Mirrors BalanceConfigForm: class / hero base stats + caps (with a live per-level example), item stat weights + PvP flags,
 * and the random item stat sets. Backend: /api/admin/balance/* (BalanceRoutes.cs).
 *
 * Also defines window.BsUI, the small helper kit shared with admin-systems.js (requests, error lists, field forms, dirty
 * tracking, tab strips, name datalists).
 */
(function (global) {
  'use strict';

  // =====================================================================================================================
  // BsUI — shared helpers for Balance + Systems
  // =====================================================================================================================

  function esc(s) { return global.OpsUI ? global.OpsUI.esc(s) : String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return '&#' + c.charCodeAt(0) + ';'; }); }

  function toast(msg, kind) { if (typeof global.portalToast === 'function') global.portalToast(msg, kind || 'info'); }

  /** fetch via portalApi -> { ok, status, body } (never throws). */
  async function request(method, url, body) {
    try {
      var opts = { method: method };
      if (body !== undefined) opts.body = body;
      var r = await global.portalApi(url, opts);
      var j = await r.json().catch(function () { return null; });
      return { ok: r.ok && !(j && j.ok === false), status: r.status, body: j || {} };
    } catch (e) {
      return { ok: false, status: 0, body: { message: 'Request failed: ' + ((e && e.message) || e) } };
    }
  }

  async function getJson(url) {
    var r = await request('GET', url);
    if (!r.ok) {
      var err = new Error(r.body.message || ('HTTP ' + r.status));
      err.status = r.status;
      throw err;
    }
    return r.body;
  }

  /** Renders a server validation result ({data:{errors, warnings}}) into box; returns true when there was something to show. */
  function renderIssues(box, body) {
    if (!box) return false;
    var data = (body && body.data) || {};
    var errors = data.errors || [];
    var warnings = data.warnings || [];
    if (!errors.length && !warnings.length) { box.innerHTML = ''; box.classList.add('hidden'); return false; }
    var html = '';
    if (errors.length) {
      html += '<div class="bs-issues-title">Fix these before saving:</div><ul>' + errors.map(function (e) {
        return '<li><span class="mono">' + esc(e.field) + '</span> ' + esc(e.message) + '</li>';
      }).join('') + '</ul>';
    }
    if (warnings.length) {
      html += '<div class="bs-issues-title warn">Warnings:</div><ul>' + warnings.map(function (w) { return '<li>' + esc(w) + '</li>'; }).join('') + '</ul>';
    }
    box.innerHTML = html;
    box.classList.remove('hidden');
    return true;
  }

  function clearIssues(box) { if (box) { box.innerHTML = ''; box.classList.add('hidden'); } }

  /**
   * Interprets a mutation result: toasts, renders validation issues, offers a reload on 409. Returns true on success.
   * onConflict() is called (after the user agrees) to reload fresh data.
   */
  async function handleResult(r, issuesBox, onConflict) {
    if (r.ok) {
      clearIssues(issuesBox);
      toast(r.body.message || 'Saved.', 'ok');
      var warnings = (r.body.data && r.body.data.warnings) || [];
      if (warnings.length) toast(warnings.join('\n'), 'info');
      return true;
    }
    if (r.status === 422) {
      renderIssues(issuesBox, r.body);
      toast(r.body.message || 'Validation failed — nothing was saved.', 'error');
      return false;
    }
    if (r.status === 409 && onConflict) {
      toast(r.body.message || 'Conflict.', 'error');
      var reload = await global.portalConfirm((r.body.message || 'The data changed on the server.') + '\n\nReload the current values now? Your unsaved edits on this tab are discarded.',
        { title: 'Changed elsewhere', confirmLabel: 'Reload', danger: false });
      if (reload) await onConflict();
      return false;
    }
    toast(r.body.message || ('Save failed (HTTP ' + r.status + ').'), 'error');
    return false;
  }

  // ---- dirty tracking (integrates with the shell's leave-section / beforeunload guard via DbSave) -------------------

  var dirtyListeners = [];
  function setDirty(key, on) {
    if (global.DbSave && typeof global.DbSave.setDirty === 'function') global.DbSave.setDirty('bs:' + key, !!on);
    dirtyListeners.forEach(function (fn) { try { fn(key, !!on); } catch (e) { /* listener */ } });
  }
  function isDirty(key) { return !!(global.DbSave && global.DbSave.isDirty && global.DbSave.isDirty('bs:' + key)); }
  function onDirty(fn) { dirtyListeners.push(fn); }

  function confirmDiscard(what) {
    return global.portalConfirm('Discard your unsaved ' + (what || 'changes') + '?', { title: 'Unsaved changes', confirmLabel: 'Discard' });
  }

  // ---- tab strip ------------------------------------------------------------------------------------------------------

  /** Renders tab buttons into host; returns { select(id), markDirty(id, on) }. onSelect(id) may return false to refuse. */
  function tabStrip(host, tabs, onSelect) {
    host.classList.add('gd-tabs', 'bs-tabs');
    host.innerHTML = tabs.map(function (t) {
      return '<button type="button" class="mir-btn" data-bs-tab="' + esc(t.id) + '">' + esc(t.label) + '<span class="bs-tab-dot hidden" title="Unsaved changes">●</span></button>';
    }).join('');
    var current = null;
    function paint() {
      host.querySelectorAll('[data-bs-tab]').forEach(function (b) { b.classList.toggle('primary', b.getAttribute('data-bs-tab') === current); });
    }
    host.addEventListener('click', async function (e) {
      var b = e.target.closest ? e.target.closest('[data-bs-tab]') : null;
      if (!b) return;
      var id = b.getAttribute('data-bs-tab');
      if (id === current) return;
      var ok = await onSelect(id);
      if (ok === false) return;
      current = id;
      paint();
    });
    return {
      select: function (id) { current = id; paint(); },
      current: function () { return current; },
      markDirty: function (id, on) {
        var b = host.querySelector('[data-bs-tab="' + id + '"] .bs-tab-dot');
        if (b) b.classList.toggle('hidden', !on);
      }
    };
  }

  // ---- datalists of item / monster names (Edit database) -------------------------------------------------------------

  var listCache = {};
  function datalist(kind) {
    var id = 'bs-dl-' + kind;
    if (!listCache[kind]) {
      listCache[kind] = getJson('/api/admin/systems/names?kind=' + encodeURIComponent(kind)).then(function (j) {
        var dl = document.getElementById(id);
        if (!dl) { dl = document.createElement('datalist'); dl.id = id; document.body.appendChild(dl); }
        dl.innerHTML = (j.names || []).map(function (n) { return '<option value="' + esc(n) + '"></option>'; }).join('');
        return j;
      }).catch(function () { listCache[kind] = null; return null; });
    }
    return id;
  }

  // ---- generic scalar field form (item weights, system groups) -------------------------------------------------------

  function fieldValue(f, input) {
    if (f.type === 'bool') return !!input.checked;
    if (f.type === 'string') return String(input.value || '').trim();
    var raw = String(input.value || '').trim();
    if (raw === '') return null;
    return Number(raw);
  }

  function fieldError(f, v) {
    if (f.type === 'bool') return null;
    if (f.type === 'string') {
      if (f.min && v.length < f.min) return 'Required.';
      if (f.max && v.length > f.max) return 'At most ' + f.max + ' characters.';
      return null;
    }
    if (v === null || !isFinite(v)) return 'Enter a number.';
    if (f.type !== 'float' && Math.floor(v) !== v) return 'Whole numbers only.';
    if (f.min != null && v < f.min) return 'Minimum ' + f.min + '.';
    if (f.max != null && v > f.max) return 'Maximum ' + f.max + '.';
    return null;
  }

  /**
   * A form over [{key, label, type, min, max, help, lookup, value}] with per-field dirty markers.
   * Returns { changes() -> {key: value} | null when invalid, reset(fields), isDirty() }.
   */
  function fieldForm(host, fields, onChange) {
    var original = {};
    var byKey = {};

    function render(list) {
      original = {};
      byKey = {};
      host.innerHTML = '<table class="cfg-table bs-field-table"><tbody>' + list.map(function (f) {
        original[f.key] = f.value;
        byKey[f.key] = f;
        var input;
        if (f.type === 'bool') {
          input = '<label class="bs-switch"><input type="checkbox" data-bs-field="' + esc(f.key) + '"' + (f.value ? ' checked' : '') + '> <span>' + (f.value ? 'On' : 'Off') + '</span></label>';
        } else if (f.type === 'string') {
          var dl = f.lookup ? ' list="' + esc(datalist(f.lookup)) + '"' : '';
          input = '<input type="text" class="fd-input bs-input-wide" data-bs-field="' + esc(f.key) + '" value="' + esc(f.value) + '"' + dl + ' maxlength="' + esc(f.max || 100) + '" autocomplete="off">';
        } else {
          input = '<input type="number" class="fd-input bs-input-num" data-bs-field="' + esc(f.key) + '" value="' + esc(f.value) + '"' +
            (f.min != null ? ' min="' + esc(f.min) + '"' : '') + (f.max != null ? ' max="' + esc(f.max) + '"' : '') + (f.type === 'float' ? ' step="any"' : ' step="1"') + '>';
        }
        var range = (f.type !== 'bool' && f.type !== 'string' && f.min != null) ? '<span class="mir-muted bs-range">' + esc(f.min) + '–' + esc(f.max) + '</span>' : '';
        return '<tr data-bs-row="' + esc(f.key) + '"><td class="bs-field-label">' + esc(f.label) + '<div class="mir-muted bs-field-key mono">' + esc(f.key) + '</div></td>' +
          '<td class="cfg-val">' + input + range + '<div class="cfg-error" data-bs-err="' + esc(f.key) + '"></div>' +
          (f.help ? '<div class="mir-muted bs-help">' + esc(f.help) + '</div>' : '') + '</td></tr>';
      }).join('') + '</tbody></table>';
    }

    function changes() {
      var out = {};
      var valid = true;
      host.querySelectorAll('[data-bs-field]').forEach(function (input) {
        var key = input.getAttribute('data-bs-field');
        var f = byKey[key];
        var v = fieldValue(f, input);
        var orig = original[key];
        var changed = f.type === 'bool' ? (!!orig !== v) : (f.type === 'string' ? String(orig == null ? '' : orig) !== v : Number(orig) !== v);
        var row = host.querySelector('[data-bs-row="' + key + '"]');
        if (row) row.classList.toggle('bs-changed', changed);
        // Only edited fields block a save (the server validates only the keys it is sent): a stored value that is already
        // outside the catalogue range is shown as a note and left alone, so the rest of the group can still be saved.
        var err = fieldError(f, v);
        var errBox = host.querySelector('[data-bs-err="' + key + '"]');
        if (errBox) errBox.textContent = err ? (changed ? err : 'Stored value is out of range (' + err + ') — left unchanged unless you edit it.') : '';
        input.classList.toggle('cfg-rejected', !!err && changed);
        if (err && changed) { valid = false; return; }
        if (changed) out[key] = v;
      });
      return valid ? out : null;
    }

    host.addEventListener('input', function (e) {
      if (!e.target.hasAttribute || !e.target.hasAttribute('data-bs-field')) return;
      if (e.target.type === 'checkbox') {
        var label = e.target.parentNode && e.target.parentNode.querySelector('span');
        if (label) label.textContent = e.target.checked ? 'On' : 'Off';
      }
      changes();
      if (onChange) onChange();
    });
    render(fields);
    return {
      changes: changes,
      reset: function (list) { render(list); },
      isDirty: function () { var c = changes(); return c === null || Object.keys(c).length > 0; }
    };
  }

  /** A toolbar: [extra] [Reload] [Save] [note input] [status pill]. */
  function toolbarHtml(prefix, extra) {
    return '<div class="db-tools-bar compact bs-toolbar">' + (extra || '') +
      '<span class="db-tools-end">' +
      '<input type="text" class="fd-input bs-note" data-bs="' + prefix + '-note" placeholder="Change note (optional, for the audit trail)" maxlength="200">' +
      '<span class="ops-pill ops-pill-warn hidden" data-bs="' + prefix + '-dirty">unsaved</span>' +
      '<button type="button" class="mir-btn" data-bs="' + prefix + '-reload">Reload</button>' +
      '<button type="button" class="mir-btn primary" data-bs="' + prefix + '-save" disabled>Save</button>' +
      '</span></div>';
  }

  function q(root, name) { return root.querySelector('[data-bs="' + name + '"]'); }

  /**
   * Runs a save with the button disabled and labelled "Saving…"; afterwards the button is enabled only while isDirty()
   * still reports unsaved edits (OpsUI.withBusy would always re-enable it).
   */
  async function saving(btn, work, isDirtyFn) {
    if (!btn || btn.getAttribute('aria-busy') === 'true') return;
    var label = btn.textContent;
    btn.disabled = true;
    btn.setAttribute('aria-busy', 'true');
    btn.textContent = 'Saving…';
    try {
      await work();
    } finally {
      btn.textContent = label;
      btn.removeAttribute('aria-busy');
      btn.disabled = !(isDirtyFn ? isDirtyFn() : true);
    }
  }

  /** The trimmed change note of a toolbar (undefined when empty). */
  function note(root, prefix) {
    var input = q(root, prefix + '-note');
    var v = input ? String(input.value || '').trim() : '';
    return v || undefined;
  }

  function clearNote(root, prefix) {
    var input = q(root, prefix + '-note');
    if (input) input.value = '';
  }

  global.BsUI = {
    esc: esc,
    toast: toast,
    request: request,
    getJson: getJson,
    renderIssues: renderIssues,
    clearIssues: clearIssues,
    handleResult: handleResult,
    setDirty: setDirty,
    isDirty: isDirty,
    onDirty: onDirty,
    confirmDiscard: confirmDiscard,
    tabStrip: tabStrip,
    datalist: datalist,
    fieldForm: fieldForm,
    toolbarHtml: toolbarHtml,
    q: q,
    saving: saving,
    note: note,
    clearNote: clearNote
  };
  var BsUI = global.BsUI;

  // =====================================================================================================================
  // Balance section
  // =====================================================================================================================

  var API = '/api/admin/balance';
  var TABS = [
    { id: 'class', label: 'Class base stats' },
    { id: 'hero', label: 'Hero base stats' },
    { id: 'weights', label: 'Item stat weights' },
    { id: 'random', label: 'Random item stats' }
  ];

  var meta = null;
  var strip = null;
  var panes = {};
  var editors = {};
  var activeTab = 'class';

  function optionList(items, selected) {
    return items.map(function (o) {
      return '<option value="' + esc(o.name) + '"' + (o.name === selected ? ' selected' : '') + '>' + esc(o.name) + '</option>';
    }).join('');
  }

  // ---- base stats editor (Class + Hero tabs) --------------------------------------------------------------------------

  function BaseStatsEditor(pane, hero) {
    var key = hero ? 'hero' : 'class';
    var endpoint = API + (hero ? '/herobasestats/' : '/basestats/');
    var job = meta.classes[0].name;
    var data = null;          // { stats:[...], caps:[...], rev }
    var selected = 0;         // selected stat row (live example)
    var levels = 50;
    var previewTimer = null;
    var previewSeq = 0;

    pane.innerHTML =
      '<div class="mir-card bs-card">' +
      BsUI.toolbarHtml(key, '<label class="bs-inline">' + (hero ? 'Hero class' : 'Class') + ' <select class="fd-input" data-bs="' + key + '-job">' + optionList(meta.classes, job) + '</select></label>') +
      '<p class="mir-muted bs-intro">' + (hero
        ? 'Hero stats per level. Saving writes <span class="mono">Configs\\HeroBaseStats&lt;Class&gt;.ini</span> and refreshes every spawned hero.'
        : 'Player stats per level. Saving writes <span class="mono">Configs\\BaseStats&lt;Class&gt;.ini</span> and online players recalculate their stats at once.') +
      ' Gain rate only applies to the Health and Mana formulas; a cap of 0 means no cap.</p>' +
      '<div class="bs-issues hidden" data-bs="' + key + '-issues"></div>' +
      '<div class="bs-split">' +
      '<div class="bs-split-main">' +
      '<h3 class="bs-h">Base stats</h3>' +
      '<div class="ops-table-scroll"><table class="user-table bs-grid"><thead><tr><th>Stat</th><th>Formula</th><th>Base</th><th>Gain</th><th>Gain rate</th><th>Max</th><th></th></tr></thead>' +
      '<tbody data-bs="' + key + '-stats"></tbody></table></div>' +
      '<button type="button" class="mir-btn bs-add" data-bs="' + key + '-addstat">+ Add stat</button>' +
      '<h3 class="bs-h">Caps</h3>' +
      '<div class="ops-table-scroll"><table class="user-table bs-grid bs-grid-narrow"><thead><tr><th>Stat</th><th>Cap</th><th></th></tr></thead>' +
      '<tbody data-bs="' + key + '-caps"></tbody></table></div>' +
      '<button type="button" class="mir-btn bs-add" data-bs="' + key + '-addcap">+ Add cap</button>' +
      '</div>' +
      '<aside class="bs-split-side">' +
      '<div class="bs-example-head"><h3 class="bs-h" data-bs="' + key + '-extitle">Example</h3>' +
      '<select class="fd-input bs-levels" data-bs="' + key + '-levels"><option value="50">Levels 1-50</option><option value="100">Levels 1-100</option><option value="200">Levels 1-200</option></select></div>' +
      '<div class="bs-example" data-bs="' + key + '-example"><p class="mir-muted">Select a stat row to see its value per level.</p></div>' +
      '</aside></div></div>';

    var statsBody = BsUI.q(pane, key + '-stats');
    var capsBody = BsUI.q(pane, key + '-caps');
    var issues = BsUI.q(pane, key + '-issues');
    var saveBtn = BsUI.q(pane, key + '-save');
    var dirtyPill = BsUI.q(pane, key + '-dirty');
    var jobSelect = BsUI.q(pane, key + '-job');

    function isHealthMana(f) { return f === 'Health' || f === 'Mana'; }

    function renderStats() {
      var counts = {};
      data.stats.forEach(function (r) { counts[r.stat] = (counts[r.stat] || 0) + 1; });
      statsBody.innerHTML = data.stats.map(function (r, i) {
        var dup = counts[r.stat] > 1;
        return '<tr data-i="' + i + '" class="' + (i === selected ? 'bs-selected' : '') + (dup ? ' bs-dup' : '') + '">' +
          '<td><select class="fd-input" data-f="stat">' + optionList(meta.stats, r.stat) + '</select>' + (dup ? '<div class="cfg-error">listed twice</div>' : '') + '</td>' +
          '<td><select class="fd-input" data-f="formula">' + optionList(meta.formulas, r.formula) + '</select></td>' +
          '<td><input type="number" step="1" class="fd-input bs-input-num" data-f="base" value="' + esc(r.base) + '"></td>' +
          '<td><input type="number" step="any" class="fd-input bs-input-num" data-f="gain" value="' + esc(r.gain) + '"></td>' +
          '<td><input type="number" step="any" class="fd-input bs-input-num" data-f="gainRate" value="' + esc(r.gainRate) + '"' + (isHealthMana(r.formula) ? '' : ' disabled title="Only used by the Health and Mana formulas"') + '></td>' +
          '<td><input type="number" step="1" min="0" class="fd-input bs-input-num" data-f="max" value="' + esc(r.max) + '"></td>' +
          '<td><button type="button" class="mir-btn ops-row-btn" data-act="del" title="Remove row">✕</button></td></tr>';
      }).join('') || '<tr><td colspan="7" class="mir-muted">No base stats — every stat is 0 for this class.</td></tr>';
    }

    function renderCaps() {
      var counts = {};
      data.caps.forEach(function (r) { counts[r.stat] = (counts[r.stat] || 0) + 1; });
      capsBody.innerHTML = data.caps.map(function (r, i) {
        var dup = counts[r.stat] > 1;
        return '<tr data-i="' + i + '" class="' + (dup ? 'bs-dup' : '') + '">' +
          '<td><select class="fd-input" data-f="stat">' + optionList(meta.stats, r.stat) + '</select>' + (dup ? '<div class="cfg-error">capped twice</div>' : '') + '</td>' +
          '<td><input type="number" step="1" min="0" class="fd-input bs-input-num" data-f="value" value="' + esc(r.value) + '"></td>' +
          '<td><button type="button" class="mir-btn ops-row-btn" data-act="del" title="Remove cap">✕</button></td></tr>';
      }).join('') || '<tr><td colspan="3" class="mir-muted">No caps.</td></tr>';
    }

    function markDirty(on) {
      editors[key].dirty = on;
      saveBtn.disabled = !on;
      dirtyPill.classList.toggle('hidden', !on);
      BsUI.setDirty('balance-' + key, on);
      if (strip) strip.markDirty(key, on);
    }

    function capFor(stat) {
      for (var i = 0; i < data.caps.length; i++) if (data.caps[i].stat === stat) return Number(data.caps[i].value) || 0;
      return 0;
    }

    function schedulePreview() {
      clearTimeout(previewTimer);
      previewTimer = setTimeout(preview, 250);
    }

    async function preview() {
      var box = BsUI.q(pane, key + '-example');
      var title = BsUI.q(pane, key + '-extitle');
      var r = data && data.stats[selected];
      if (!r) { box.innerHTML = '<p class="mir-muted">Select a stat row to see its value per level.</p>'; title.textContent = 'Example'; return; }
      title.textContent = job + ' — ' + r.stat;
      var nums = [r.base, r.gain, r.gainRate, r.max];
      if (nums.some(function (n) { return n === '' || n == null || !isFinite(Number(n)); })) { box.innerHTML = '<p class="cfg-error">Enter numbers to see the example.</p>'; return; }
      var cap = capFor(r.stat);
      var url = API + '/preview?job=' + encodeURIComponent(job) + '&formula=' + encodeURIComponent(r.formula) +
        '&base=' + encodeURIComponent(Math.trunc(Number(r.base))) + '&gain=' + encodeURIComponent(Number(r.gain)) +
        '&gainRate=' + encodeURIComponent(Number(r.gainRate) || 0) + '&max=' + encodeURIComponent(Math.trunc(Number(r.max)) || 0) +
        '&cap=' + encodeURIComponent(cap) + '&from=1&to=' + levels;
      var seq = ++previewSeq;
      var res = await BsUI.request('GET', url);
      if (seq !== previewSeq) return;
      if (!res.ok) { box.innerHTML = '<p class="cfg-error">' + esc(res.body.message || 'Preview failed') + '</p>'; return; }
      var list = res.body.levels || [];
      var peak = list.reduce(function (m, x) { return Math.max(m, Math.abs(x.value)); }, 1);
      box.innerHTML = '<div class="mir-muted bs-example-note">' + esc(r.formula) + ' formula' + (cap ? ', capped at ' + esc(cap) : '') + (Number(r.max) > 0 ? ', max ' + esc(r.max) : '') + '</div>' +
        '<div class="bs-example-rows">' + list.map(function (x) {
          var w = Math.max(1, Math.round(Math.abs(x.value) / peak * 100));
          return '<div class="bs-example-row"><span class="bs-lv">Lv ' + esc(x.level) + '</span><span class="bs-bar"><span style="width:' + w + '%"></span></span><span class="bs-val mono">' + esc(x.value) + '</span></div>';
        }).join('') + '</div>';
    }

    var loadSeq = 0;

    async function load() {
      var seq = ++loadSeq;
      BsUI.clearIssues(issues);
      statsBody.innerHTML = '<tr><td colspan="7" class="mir-muted">Loading…</td></tr>';
      capsBody.innerHTML = '';
      try {
        var j = await BsUI.getJson(endpoint + encodeURIComponent(job));
        if (seq !== loadSeq) return;
        data = {
          stats: (j.stats || []).map(function (s) { return { stat: s.stat, formula: s.formula, base: s.base, gain: s.gain, gainRate: s.gainRate, max: s.max }; }),
          caps: (j.caps || []).map(function (c) { return { stat: c.stat, value: c.value }; }),
          rev: j.rev
        };
        selected = data.stats.length ? Math.min(selected, data.stats.length - 1) : -1;
        renderStats();
        renderCaps();
        markDirty(false);
        preview();
      } catch (e) {
        if (seq !== loadSeq) return;
        data = null;
        statsBody.innerHTML = '<tr><td colspan="7" class="cfg-error">' + esc(e.message) + '</td></tr>';
      }
    }

    function onGridInput(listName, e) {
      var tr = e.target.closest('tr[data-i]');
      var f = e.target.getAttribute('data-f');
      if (!tr || !f || !data) return;
      var row = data[listName][Number(tr.getAttribute('data-i'))];
      if (!row) return;
      row[f] = e.target.value;
      if (e.target.type === 'number') {
        var n = Number(e.target.value);
        var bad = e.target.value === '' || !isFinite(n) || ((f === 'base' || f === 'max' || f === 'value') && Math.floor(n) !== n) || ((f === 'max' || f === 'value') && n < 0);
        e.target.classList.toggle('cfg-rejected', bad);
      }
      markDirty(true);
      if (listName === 'stats') {
        selected = Number(tr.getAttribute('data-i'));
        if (f === 'stat' || f === 'formula') renderStats();
      } else if (f === 'stat') {
        renderCaps();
      }
      schedulePreview();
    }

    // Number inputs update on 'input' (no re-render, focus stays); selects on 'change' (they re-render their table).
    statsBody.addEventListener('input', function (e) { if (e.target.tagName !== 'SELECT') onGridInput('stats', e); });
    statsBody.addEventListener('change', function (e) { if (e.target.tagName === 'SELECT') onGridInput('stats', e); });
    capsBody.addEventListener('input', function (e) { if (e.target.tagName !== 'SELECT') onGridInput('caps', e); });
    capsBody.addEventListener('change', function (e) { if (e.target.tagName === 'SELECT') onGridInput('caps', e); });

    statsBody.addEventListener('click', function (e) {
      var tr = e.target.closest('tr[data-i]');
      if (!tr || !data) return;
      var i = Number(tr.getAttribute('data-i'));
      if (e.target.closest('[data-act="del"]')) {
        data.stats.splice(i, 1);
        if (selected >= data.stats.length) selected = data.stats.length - 1;
        renderStats();
        markDirty(true);
        schedulePreview();
        return;
      }
      if (selected !== i) {
        selected = i;
        statsBody.querySelectorAll('tr[data-i]').forEach(function (r) { r.classList.toggle('bs-selected', Number(r.getAttribute('data-i')) === i); });
        schedulePreview();
      }
    });

    capsBody.addEventListener('click', function (e) {
      var tr = e.target.closest('tr[data-i]');
      if (!tr || !data || !e.target.closest('[data-act="del"]')) return;
      data.caps.splice(Number(tr.getAttribute('data-i')), 1);
      renderCaps();
      markDirty(true);
      schedulePreview();
    });

    function firstUnused(list) {
      var used = {};
      list.forEach(function (r) { used[r.stat] = true; });
      for (var i = 0; i < meta.stats.length; i++) if (!used[meta.stats[i].name]) return meta.stats[i].name;
      return meta.stats[0].name;
    }

    BsUI.q(pane, key + '-addstat').addEventListener('click', function () {
      if (!data) return;
      data.stats.push({ stat: firstUnused(data.stats), formula: 'Stat', base: 0, gain: 0, gainRate: 0, max: 0 });
      selected = data.stats.length - 1;
      renderStats();
      markDirty(true);
      schedulePreview();
    });

    BsUI.q(pane, key + '-addcap').addEventListener('click', function () {
      if (!data) return;
      data.caps.push({ stat: firstUnused(data.caps), value: 0 });
      renderCaps();
      markDirty(true);
    });

    BsUI.q(pane, key + '-levels').addEventListener('change', function (e) { levels = Number(e.target.value) || 50; preview(); });

    jobSelect.addEventListener('change', async function () {
      var next = jobSelect.value;
      if (editors[key].dirty && !(await BsUI.confirmDiscard(job + ' base stat edits'))) { jobSelect.value = job; return; }
      job = next;
      if (global.portalSetHashParams && activeTab === key) global.portalSetHashParams({ 'class': job }, true);
      load();
    });

    BsUI.q(pane, key + '-reload').addEventListener('click', async function () {
      if (editors[key].dirty && !(await BsUI.confirmDiscard(job + ' base stat edits'))) return;
      load();
    });

    function num(v) { var n = Number(v); return v === '' || v == null || !isFinite(n) ? v : n; }

    saveBtn.addEventListener('click', function () {
      if (!data) return;
      BsUI.saving(saveBtn, async function () {
        var body = {
          stats: data.stats.map(function (r) { return { stat: r.stat, formula: r.formula, base: num(r.base), gain: num(r.gain), gainRate: num(r.gainRate) || 0, max: num(r.max) || 0 }; }),
          caps: data.caps.map(function (c) { return { stat: c.stat, value: num(c.value) }; }),
          rev: data.rev,
          reason: BsUI.note(pane, key)
        };
        var r = await BsUI.request('PUT', endpoint + encodeURIComponent(job), body);
        if (await BsUI.handleResult(r, issues, load)) {
          BsUI.clearNote(pane, key);
          await load();
        }
      }, function () { return editors[key].dirty; });
    });

    return {
      dirty: false,
      load: load,
      setJob: async function (name) {
        if (!name || name === job) return;
        var match = meta.classes.filter(function (c) { return c.name.toLowerCase() === String(name).toLowerCase() || String(c.value) === String(name); })[0];
        if (!match) return;
        if (editors[key].dirty && !(await BsUI.confirmDiscard(job + ' base stat edits'))) return;
        job = match.name;
        jobSelect.value = job;
        load();
      }
    };
  }

  // ---- item stat weights ----------------------------------------------------------------------------------------------

  function WeightsEditor(pane) {
    var rev = null;
    var form = null;

    pane.innerHTML = '<div class="mir-card bs-card">' + BsUI.toolbarHtml('weights', '<span class="db-pane-title">Item stat weights</span>') +
      '<p class="mir-muted bs-intro" data-bs="weights-intro"></p><div class="bs-issues hidden" data-bs="weights-issues"></div>' +
      '<div data-bs="weights-form"><p class="mir-muted">Loading…</p></div></div>';

    var saveBtn = BsUI.q(pane, 'weights-save');
    var pill = BsUI.q(pane, 'weights-dirty');
    var issues = BsUI.q(pane, 'weights-issues');

    function markDirty(on) {
      editors.weights.dirty = on;
      saveBtn.disabled = !on;
      pill.classList.toggle('hidden', !on);
      BsUI.setDirty('balance-weights', on);
      if (strip) strip.markDirty('weights', on);
    }

    async function load() {
      BsUI.clearIssues(issues);
      var host = BsUI.q(pane, 'weights-form');
      try {
        var j = await BsUI.getJson(API + '/itemweights');
        rev = j.rev;
        BsUI.q(pane, 'weights-intro').textContent = (j.note || '') + ' Saved to Configs\\Setup.ini [Items].';
        if (!form) form = BsUI.fieldForm(host, j.fields || [], function () { markDirty(form.isDirty()); });
        else form.reset(j.fields || []);
        markDirty(false);
      } catch (e) {
        host.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    BsUI.q(pane, 'weights-reload').addEventListener('click', async function () {
      if (editors.weights.dirty && !(await BsUI.confirmDiscard('item weight edits'))) return;
      load();
    });

    saveBtn.addEventListener('click', function () {
      if (!form) return;
      var changes = form.changes();
      if (changes === null) { BsUI.toast('Fix the highlighted fields first.', 'error'); return; }
      if (!Object.keys(changes).length) { markDirty(false); return; }
      BsUI.saving(saveBtn, async function () {
        var r = await BsUI.request('PUT', API + '/itemweights', { values: changes, rev: rev, reason: BsUI.note(pane, 'weights') });
        if (await BsUI.handleResult(r, issues, load)) {
          BsUI.clearNote(pane, 'weights');
          await load();
        }
      }, function () { return editors.weights.dirty; });
    });

    return { dirty: false, load: load };
  }

  // ---- random item stats --------------------------------------------------------------------------------------------

  function RandomStatsEditor(pane) {
    var sets = [];
    var current = -1;
    var values = null;
    var groups = meta.randomItemStats.groups;
    var curse = meta.randomItemStats.curse;

    pane.innerHTML = '<div class="mir-card bs-card">' +
      BsUI.toolbarHtml('ris', '<span class="db-pane-title">Random item stats</span>' +
        '<button type="button" class="mir-btn" data-bs="ris-add">+ Add set</button>' +
        '<button type="button" class="mir-btn danger" data-bs="ris-remove">Remove last set</button>') +
      '<p class="mir-muted bs-intro">Items reference a set by its number (Random Stats column of the item database; 255 = none). ' +
      '<b>Chance</b>: the stat is rolled with a 1-in-N chance (0 = never). <b>Stat chance</b>: every point above the first is kept with a 1-in-N chance. ' +
      '<b>Max stat</b>: the most points that can be added (durability adds up to N×1000). Edits apply to new drops immediately; ' +
      'added or removed sets take effect after the next world start.</p>' +
      '<div class="bs-issues hidden" data-bs="ris-issues"></div>' +
      '<div class="bs-split"><aside class="bs-list" data-bs="ris-list"></aside>' +
      '<div class="bs-split-main" data-bs="ris-editor"><p class="mir-muted">Loading…</p></div></div></div>';

    var listEl = BsUI.q(pane, 'ris-list');
    var editorEl = BsUI.q(pane, 'ris-editor');
    var saveBtn = BsUI.q(pane, 'ris-save');
    var pill = BsUI.q(pane, 'ris-dirty');
    var issues = BsUI.q(pane, 'ris-issues');

    function markDirty(on) {
      editors.random.dirty = on;
      saveBtn.disabled = !on;
      pill.classList.toggle('hidden', !on);
      BsUI.setDirty('balance-random', on);
      if (strip) strip.markDirty('random', on);
    }

    function renderList() {
      listEl.innerHTML = sets.length ? sets.map(function (s) {
        return '<button type="button" class="bs-list-item' + (s.id === current ? ' active' : '') + '" data-id="' + esc(s.id) + '">' +
          '<span class="mono">#' + esc(s.id) + '</span>' +
          '<span class="mir-muted">' + (s.usedBy ? esc(s.usedBy) + ' item' + (s.usedBy === 1 ? '' : 's') : 'unused') + '</span></button>';
      }).join('') : '<p class="mir-muted">No sets.</p>';
    }

    function cell(name) {
      return '<td><input type="number" min="0" max="255" step="1" class="fd-input bs-input-num" data-field="' + esc(name) + '" value="' + esc(values[name]) + '"></td>';
    }

    function renderEditor() {
      var set = sets.filter(function (s) { return s.id === current; })[0];
      if (!set || !values) { editorEl.innerHTML = '<p class="mir-muted">Select a set.</p>'; return; }
      editorEl.innerHTML = '<h3 class="bs-h">Set #' + esc(set.id) + '</h3>' +
        (set.usedBy ? '<p class="mir-muted">Used by ' + esc(set.usedBy) + ' item(s): ' + esc((set.usedByItems || []).join(', ')) + (set.usedBy > (set.usedByItems || []).length ? '…' : '') + '</p>' : '<p class="mir-muted">No item uses this set yet.</p>') +
        '<div class="ops-table-scroll"><table class="user-table bs-grid"><thead><tr><th>Stat</th><th>Chance (1 in N)</th><th>Stat chance (1 in N)</th><th>Max stat</th></tr></thead><tbody>' +
        groups.map(function (g) {
          return '<tr><td>' + esc(g.label) + '</td>' + cell(g.chance) + cell(g.statChance) + cell(g.maxStat) + '</tr>';
        }).join('') +
        '<tr><td>Curse chance (%)</td><td><input type="number" min="0" max="255" step="1" class="fd-input bs-input-num" data-field="' + esc(curse) + '" value="' + esc(values[curse]) + '"></td><td colspan="2" class="mir-muted">Cursed when Random(100) ≤ value (0 = never).</td></tr>' +
        '</tbody></table></div>';
    }

    async function select(id) {
      if (editors.random.dirty && current !== id && !(await BsUI.confirmDiscard('random stat edits for set #' + current))) return;
      current = id;
      var set = sets.filter(function (s) { return s.id === id; })[0];
      values = set ? Object.assign({}, set.values) : null;
      markDirty(false);
      BsUI.clearIssues(issues);
      renderList();
      renderEditor();
    }

    var loadSeq = 0;

    async function load(keep) {
      var seq = ++loadSeq;
      try {
        var j = await BsUI.getJson(API + '/randomitemstats');
        if (seq !== loadSeq) return;
        sets = j.sets || [];
        var target = keep != null ? keep : current;
        if (!sets.some(function (s) { return s.id === target; })) target = sets.length ? sets[0].id : -1;
        current = -2;
        editors.random.dirty = false;
        await select(target);
        BsUI.q(pane, 'ris-add').disabled = sets.length >= (j.maxSets || 255);
        BsUI.q(pane, 'ris-remove').disabled = !sets.length;
      } catch (e) {
        editorEl.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    listEl.addEventListener('click', function (e) {
      var b = e.target.closest('[data-id]');
      if (b) select(Number(b.getAttribute('data-id')));
    });

    editorEl.addEventListener('input', function (e) {
      var f = e.target.getAttribute && e.target.getAttribute('data-field');
      if (!f || !values) return;
      values[f] = e.target.value === '' ? '' : Number(e.target.value);
      var bad = e.target.value === '' || !isFinite(Number(e.target.value)) || Number(e.target.value) < 0 || Number(e.target.value) > 255 || Math.floor(Number(e.target.value)) !== Number(e.target.value);
      e.target.classList.toggle('cfg-rejected', bad);
      markDirty(true);
    });

    BsUI.q(pane, 'ris-reload').addEventListener('click', async function () {
      if (editors.random.dirty && !(await BsUI.confirmDiscard('random stat edits'))) return;
      editors.random.dirty = false;
      load(current);
    });

    saveBtn.addEventListener('click', function () {
      if (!values || current < 0) return;
      var set = sets.filter(function (s) { return s.id === current; })[0];
      var changes = {};
      Object.keys(values).forEach(function (k) { if (values[k] !== set.values[k]) changes[k] = values[k]; });
      BsUI.saving(saveBtn, async function () {
        var r = await BsUI.request('PUT', API + '/randomitemstats/' + current, { values: changes, rev: set.rev, reason: BsUI.note(pane, 'ris') });
        if (await BsUI.handleResult(r, issues, function () { editors.random.dirty = false; return load(current); })) {
          BsUI.clearNote(pane, 'ris');
          editors.random.dirty = false;
          await load(current);
        }
      }, function () { return editors.random.dirty; });
    });

    BsUI.q(pane, 'ris-add').addEventListener('click', async function () {
      if (editors.random.dirty && !(await BsUI.confirmDiscard('random stat edits for set #' + current))) return;
      var templates = meta.randomItemStats.templates || ['Nothing'];
      var res = await global.OpsUI.actionDialog({
        title: 'Add random item stat set',
        message: 'The new set gets number #' + sets.length + '. Start from an empty set or copy one of Crystal\'s presets.',
        confirmText: 'Add set',
        danger: false,
        fields: [
          { key: 'template', label: 'Start from', type: 'select', value: 'Nothing', options: templates.map(function (t) { return { value: t, label: t === 'Nothing' ? 'Empty (all zero)' : t + ' preset' }; }) },
          { key: 'reason', label: 'Change note (optional)', type: 'text' }
        ]
      });
      if (!res) return;
      var r = await BsUI.request('POST', API + '/randomitemstats', { template: res.template, reason: res.reason || undefined });
      if (await BsUI.handleResult(r, issues)) {
        editors.random.dirty = false;
        await load(r.body.data && r.body.data.set ? r.body.data.set.id : null);
      }
    });

    BsUI.q(pane, 'ris-remove').addEventListener('click', async function () {
      if (!sets.length) return;
      var last = sets[sets.length - 1];
      var res = await global.OpsUI.actionDialog({
        title: 'Remove set #' + last.id,
        message: 'Only the last set can be removed (items reference sets by number). ' +
          (last.usedBy ? last.usedBy + ' item(s) use set #' + last.id + ' (' + (last.usedByItems || []).join(', ') + ') and lose their random stats after the next world start.' : 'No item uses this set.'),
        confirmText: 'Remove set',
        fields: [{ key: 'reason', label: 'Reason (optional)', type: 'text' }]
      });
      if (!res) return;
      var url = API + '/randomitemstats/' + last.id + '?force=' + (last.usedBy ? '1' : '0') + (res.reason ? '&reason=' + encodeURIComponent(res.reason) : '');
      var r = await BsUI.request('DELETE', url);
      if (await BsUI.handleResult(r, issues)) {
        editors.random.dirty = false;
        await load(null);
      }
    });

    return { dirty: false, load: function () { return load(current >= 0 ? current : null); } };
  }

  // ---- section wiring -------------------------------------------------------------------------------------------------

  function showTab(id) {
    activeTab = id;
    Object.keys(panes).forEach(function (k) { panes[k].classList.toggle('hidden', k !== id); });
    if (strip) strip.select(id);
    var ed = editors[id];
    if (ed && !ed.loaded) { ed.loaded = true; ed.load(); }
  }

  async function load(el, params) {
    el.innerHTML = '<div class="bs-head"><h2 class="gd-group-title">Balance</h2><div data-bs="tabs"></div></div>' +
      TABS.map(function (t) { return '<div class="bs-pane hidden" data-pane="' + t.id + '"></div>'; }).join('');
    meta = await BsUI.getJson(API + '/meta');

    TABS.forEach(function (t) { panes[t.id] = el.querySelector('[data-pane="' + t.id + '"]'); });
    editors['class'] = BaseStatsEditor(panes['class'], false);
    editors.hero = BaseStatsEditor(panes.hero, true);
    editors.weights = WeightsEditor(panes.weights);
    editors.random = RandomStatsEditor(panes.random);

    strip = BsUI.tabStrip(el.querySelector('[data-bs="tabs"]'), TABS, function (id) {
      showTab(id);
      if (global.portalSetHashParams) global.portalSetHashParams({ tab: id, 'class': null }, true);
      return true;
    });

    var tab = params && params.tab && panes[params.tab] ? params.tab : 'class';
    showTab(tab);
  }

  function applyDeepLink(params) {
    if (!params || !strip) return;
    if (params.tab && panes[params.tab] && params.tab !== activeTab) showTab(params.tab);
    if (params['class'] && (activeTab === 'class' || activeTab === 'hero')) editors[activeTab].setJob(params['class']);
  }

  var shownOnce = false;

  global.AdminSections.register('balance', {
    load: load,
    show: function () {
      // Coming back to the section: refresh the visible tab unless it holds unsaved edits (another admin may have changed it).
      if (!shownOnce) { shownOnce = true; return; }
      var ed = editors[activeTab];
      if (ed && ed.loaded && !ed.dirty) ed.load();
    }
  });

  if (typeof global.portalRegisterDeepLink === 'function') global.portalRegisterDeepLink('balance', applyDeepLink);
})(window);
