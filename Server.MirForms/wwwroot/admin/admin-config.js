/**
 * Server Config section (#section-config) — B6. Edits Configs\Setup.ini through GET / PUT /api/admin/config.
 *
 * - Group tabs (General, Network, Web Admin, …) + "All", a filter box (label / key / field / help) and a "Changed only" toggle.
 * - Typed inputs (checkbox, number with the server's min/max, select for Language, datalists for monster / map names),
 *   per-field dirty markers with a per-row revert, inline client validation, "Reset unsaved".
 * - Save sends ONLY the changed fields (with the value each edit started from, so a concurrent edit is reported instead of
 *   overwritten) after one confirmation dialog listing old → new (+ optional reason). Rejected edits stay on screen with
 *   their error; warnings (unknown monster / map names) are shown inline.
 * - Secrets (GMPassword) are never sent to the browser: "Change…" opens a dialog asking for the new value twice.
 * - "Restart required" badges per key and a banner of pending restarts with a Reboot-world shortcut (B1 route).
 * - Unsaved edits register with DbSave (the shell asks before leaving / closing the tab).
 * - Deep link: #section=config&group=<group>&q=<filter>&field=<SettingsField>.
 *
 * window.ConfigLogic holds the pure helpers.
 */
(function (global) {
  'use strict';

  var DIRTY_KEY = 'server-config';
  var ALL = '__all';
  var LS_GROUP = 'mircraft_config_group';

  var esc = function (s) {
    return global.OpsUI ? global.OpsUI.esc(s) : String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  };

  // ---------------------------------------------------------------------------------------------- pure helpers

  /** The text an input holds for an item value (the value the server sent). */
  function displayValue(item) {
    if (item.type === 'bool') return !!item.value;
    if (item.value == null) return '';
    return String(item.value);
  }

  /** Client-side validation of a raw input value. Returns an error string or null. The server validates again. */
  function validate(item, raw) {
    if (item.type === 'bool') return null;
    var s = raw == null ? '' : String(raw);
    if (item.type === 'int' || item.type === 'float') {
      var t = s.trim();
      if (t === '') return 'A value is required.';
      var n = Number(t);
      if (!isFinite(n)) return 'Must be a number.';
      if (item.type === 'int' && Math.floor(n) !== n) return 'Must be a whole number.';
      if (item.min != null && n < item.min) return 'Must be at least ' + fmtNum(item.min) + '.';
      if (item.max != null && n > item.max) return 'Must be at most ' + fmtNum(item.max) + '.';
      return null;
    }
    var v = item.secret ? s : s.trim();
    if (!v.trim()) return 'Cannot be empty (an empty value reverts to the default).';
    if (/[\r\n\u0000-\u001f]/.test(v)) return 'No line breaks or control characters.';
    if (v.indexOf('=') >= 0) return "Cannot contain '='.";
    if (v.length > 512) return 'Too long (max 512 characters).';
    if (item.format === 'ip' && !isIp(v)) return 'Must be an IP address such as 127.0.0.1.';
    if (item.format === 'url-prefix' && !/^https?:\/\/[^\s\/]+\/(\S*\/)?$/i.test(v)) return 'Must look like http://127.0.0.1:5679/ (ending with /).';
    if (item.options && item.options.length && item.options.indexOf(v) < 0) return 'Choose one of: ' + item.options.join(', ') + '.';
    return null;
  }

  function isIp(v) {
    if (/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.test(v)) {
      return v.split('.').every(function (p) { return Number(p) <= 255; });
    }
    return /^[0-9a-f:]+$/i.test(v) && v.indexOf(':') >= 0;
  }

  function fmtNum(n) {
    if (Math.abs(n) >= 1e15) return n.toExponential(2);
    return Number(n).toLocaleString();
  }

  /** True when raw differs from the item's saved value. */
  function isChanged(item, raw) {
    if (item.secret) return false;   // secrets are staged through the dialog, never typed in place
    if (item.type === 'bool') return !!raw !== !!item.value;
    var s = raw == null ? '' : String(raw);
    if (item.type === 'int' || item.type === 'float') {
      var t = s.trim();
      if (t === '') return true;
      var n = Number(t);
      return !isFinite(n) || n !== Number(item.value);
    }
    return s.trim() !== String(item.value == null ? '' : item.value);
  }

  /** The JSON value sent for a raw input value. */
  function payloadValue(item, raw) {
    if (item.type === 'bool') return !!raw;
    if (item.type === 'int' || item.type === 'float') return Number(String(raw).trim());
    return item.secret ? String(raw) : String(raw).trim();
  }

  /** Human text of a value for the confirmation list. */
  function describe(item, v) {
    if (item.secret) return '••••••';
    if (item.type === 'bool') return v ? 'on' : 'off';
    if (v === '' || v == null) return '(empty)';
    return String(v);
  }

  /** Does the item match the filter text (label, key, field, section, help)? */
  function matches(item, q) {
    if (!q) return true;
    q = q.toLowerCase();
    return [item.label, item.key, item.field, item.section, item.help].some(function (s) {
      return s && String(s).toLowerCase().indexOf(q) >= 0;
    });
  }

  /**
   * What a PUT reply means for the page. Returns { applied: [field], rejected: {field: error}, warnings: {field: msg},
   * unchanged: [field], kind, message }.
   */
  function interpretSave(status, j) {
    j = j || {};
    var rejected = {};
    (j.rejected || []).forEach(function (r) { if (r) rejected[r.field || ''] = r.error || 'Rejected'; });
    var warnings = {};
    (j.warnings || []).forEach(function (w) { if (w && w.field) warnings[w.field] = w.message || ''; });
    var applied = Array.isArray(j.applied) ? j.applied : [];
    var unchanged = Array.isArray(j.unchanged) ? j.unchanged : [];
    var nRejected = Object.keys(rejected).length;
    var kind = (status >= 200 && status < 300 && !nRejected && j.persisted !== false) ? 'ok' : (applied.length ? 'warn' : 'error');
    var message = j.message || (status >= 200 && status < 300 ? 'Saved.' : 'Save failed (HTTP ' + status + ').');
    return { applied: applied, rejected: rejected, warnings: warnings, unchanged: unchanged, kind: kind, message: message };
  }

  global.ConfigLogic = {
    validate: validate,
    isChanged: isChanged,
    payloadValue: payloadValue,
    matches: matches,
    interpretSave: interpretSave,
    isIp: isIp
  };

  // ---------------------------------------------------------------------------------------------- state

  var root = null;
  var state = {
    data: null,
    items: Object.create(null),   // field -> item (server values)
    order: [],                    // fields in display order
    secrets: Object.create(null), // field -> staged new secret value
    errors: Object.create(null),  // field -> server rejection
    warnings: Object.create(null),// field -> server warning
    group: ALL,
    filter: '',
    changedOnly: false,
    loading: false
  };

  function lsGet(k) { try { return global.localStorage.getItem(k); } catch (e) { return null; } }
  function lsSet(k, v) { try { global.localStorage.setItem(k, v); } catch (e) { /* private mode */ } }

  function q(sel) { return root ? root.querySelector(sel) : null; }
  function qa(sel) { return root ? Array.prototype.slice.call(root.querySelectorAll(sel)) : []; }

  function inputOf(field) {
    return root ? root.querySelector('[data-cfg-input][data-field="' + cssEsc(field) + '"]') : null;
  }
  function rowOf(field) {
    return root ? root.querySelector('tr[data-cfg-row][data-field="' + cssEsc(field) + '"]') : null;
  }
  function cssEsc(s) {
    return global.CSS && global.CSS.escape ? global.CSS.escape(String(s)) : String(s).replace(/["\\]/g, '\\$&');
  }

  function rawOf(item) {
    var el = inputOf(item.field);
    if (!el) return displayValue(item);
    return el.type === 'checkbox' ? el.checked : el.value;
  }

  /** Fields with an unsaved edit (typed changes + staged secrets). */
  function changedFields() {
    var out = [];
    state.order.forEach(function (f) {
      var item = state.items[f];
      if (item.secret) { if (state.secrets[f] != null) out.push(f); return; }
      if (isChanged(item, rawOf(item))) out.push(f);
    });
    return out;
  }

  // ---------------------------------------------------------------------------------------------- rendering

  function shell() {
    root.innerHTML =
      '<div class="mir-card cfg-card">' +
        '<div class="cfg-head">' +
          '<div><h2 class="cfg-title">Server Config</h2>' +
          '<div class="mir-muted cfg-file" data-cfg-file></div></div>' +
          '<div class="cfg-actions">' +
            '<button type="button" class="mir-btn" data-cfg-act="reload" title="Reload the values from the server">Reload</button>' +
            '<button type="button" class="mir-btn" data-cfg-act="reset" title="Discard every unsaved edit on this page" disabled>Reset unsaved</button>' +
            '<button type="button" class="mir-btn primary" data-cfg-act="save" disabled>Save changes</button>' +
          '</div>' +
        '</div>' +
        '<div data-cfg-banner></div>' +
        '<div class="db-tools-bar compact cfg-toolbar">' +
          '<input type="search" class="fd-input cfg-filter" data-cfg-filter placeholder="Filter settings (name, key, help)…" aria-label="Filter settings" />' +
          '<label class="cfg-check"><input type="checkbox" data-cfg-changed-only /> Changed only</label>' +
          '<span class="mir-muted cfg-count" data-cfg-count></span>' +
        '</div>' +
        '<div class="gd-tabs cfg-tabs" data-cfg-tabs role="tablist"></div>' +
      '</div>' +
      '<div data-cfg-body><p class="mir-muted">Loading…</p></div>' +
      '<datalist id="cfgMonsterNames"></datalist>' +
      '<datalist id="cfgMapNames"></datalist>';
  }

  function badges(item) {
    var html = '';
    if (item.restart === 'world') html += '<span class="cfg-badge restart" title="Applies after the world restarts (Reboot)">restart</span>';
    else if (item.restart === 'process') html += '<span class="cfg-badge restart cfg-badge-process" title="Applies after the server process restarts">server restart</span>';
    if (item.secret) html += '<span class="cfg-badge secret">secret</span>';
    return html;
  }

  function inputHtml(item) {
    var f = esc(item.field);
    var common = ' data-cfg-input data-field="' + f + '" aria-label="' + esc(item.label || item.key) + '"';
    if (item.secret) {
      return '<span class="cfg-secret-state" data-cfg-secret-state data-field="' + f + '">' + (item.isSet ? 'Set (hidden)' : 'Not set') + '</span> ' +
        '<button type="button" class="mir-btn ops-row-btn" data-cfg-secret="' + f + '">Change…</button>';
    }
    if (item.type === 'bool') {
      return '<label class="cfg-switch"><input type="checkbox"' + common + (item.value ? ' checked' : '') + ' /><span data-cfg-bool-label>' + (item.value ? 'On' : 'Off') + '</span></label>';
    }
    if (item.options && item.options.length) {
      var opts = item.options.slice();
      if (opts.indexOf(String(item.value)) < 0) opts.unshift(String(item.value));
      return '<select class="fd-input cfg-select"' + common + '>' + opts.map(function (o) {
        return '<option value="' + esc(o) + '"' + (String(o) === String(item.value) ? ' selected' : '') + '>' + esc(o) + '</option>';
      }).join('') + '</select>';
    }
    if (item.type === 'int' || item.type === 'float') {
      var attrs = ' step="' + (item.type === 'int' ? '1' : 'any') + '"';
      if (item.min != null && Math.abs(item.min) < 1e15) attrs += ' min="' + esc(item.min) + '"';
      if (item.max != null && Math.abs(item.max) < 1e15) attrs += ' max="' + esc(item.max) + '"';
      return '<input type="number" class="cfg-num"' + common + attrs + ' value="' + esc(displayValue(item)) + '" />';
    }
    var list = item.format === 'monster' ? ' list="cfgMonsterNames"' : item.format === 'map' ? ' list="cfgMapNames"' : '';
    return '<input type="text" class="cfg-text" spellcheck="false" autocomplete="off"' + common + list + ' value="' + esc(displayValue(item)) + '" />';
  }

  function rangeHint(item) {
    if (item.type !== 'int' && item.type !== 'float') return '';
    if (item.min == null || item.max == null || Math.abs(item.max) >= 1e15) return '';
    return ' <span class="cfg-range">(' + esc(fmtNum(item.min)) + ' – ' + esc(fmtNum(item.max)) + ')</span>';
  }

  function rowHtml(item) {
    var f = esc(item.field);
    return '<tr data-cfg-row data-field="' + f + '">' +
      '<td class="cfg-key">' +
        '<div class="cfg-label"><span class="cfg-dot" aria-hidden="true"></span>' + esc(item.label || item.key) + badges(item) + '</div>' +
        '<div class="cfg-keyname" title="Settings.' + f + '">[' + esc(item.section) + '] ' + esc(item.key) + '</div>' +
      '</td>' +
      '<td class="cfg-val">' +
        '<div class="cfg-val-line">' + inputHtml(item) +
          '<button type="button" class="mir-btn ops-row-btn cfg-revert" data-cfg-revert="' + f + '" title="Undo this edit" hidden>Undo</button>' +
        '</div>' +
        '<div class="mir-muted cfg-help">' + esc(item.help || '') + rangeHint(item) + '</div>' +
        '<div class="cfg-error" data-cfg-error data-field="' + f + '"></div>' +
        '<div class="cfg-warn" data-cfg-warn data-field="' + f + '"></div>' +
      '</td>' +
    '</tr>';
  }

  function render() {
    var data = state.data;
    var body = q('[data-cfg-body]');
    if (!body || !data) return;

    var fileEl = q('[data-cfg-file]');
    if (fileEl) {
      fileEl.textContent = (data.file || 'Configs\\Setup.ini') +
        (data.lastWriteUtc ? ' · last written ' + (global.OpsUI ? global.OpsUI.fmtDate(data.lastWriteUtc) : data.lastWriteUtc) : '');
    }

    var sections = data.sections || [];
    var html = '';
    sections.forEach(function (sec) {
      html += '<div class="cfg-section" data-cfg-section="' + esc(sec.name) + '">' +
        '<h3 class="cfg-section-title">' + esc(sec.name) + ' <span class="cfg-section-count" data-cfg-section-count></span></h3>' +
        '<table class="cfg-table"><tbody>' + (sec.items || []).map(rowHtml).join('') + '</tbody></table>' +
        '</div>';
    });
    body.innerHTML = html || '<p class="mir-muted">No settings.</p>';

    var lookups = data.lookups || {};
    fillDatalist('cfgMonsterNames', lookups.monsters);
    fillDatalist('cfgMapNames', lookups.maps);

    renderTabs();
    renderBanner();
    Object.keys(state.errors).forEach(function (f) { setRowMessage(f, 'error', state.errors[f]); });
    Object.keys(state.warnings).forEach(function (f) { setRowMessage(f, 'warn', state.warnings[f]); });
    refreshDirty();
    applyView();
  }

  function fillDatalist(id, names) {
    var dl = document.getElementById(id);
    if (!dl) return;
    dl.innerHTML = '';
    (names || []).slice(0, 5000).forEach(function (n) {
      var o = document.createElement('option');
      o.value = String(n);
      dl.appendChild(o);
    });
  }

  function renderTabs() {
    var tabs = q('[data-cfg-tabs]');
    if (!tabs || !state.data) return;
    var names = (state.data.sections || []).map(function (s) { return s.name; });
    if (state.group !== ALL && names.indexOf(state.group) < 0) state.group = ALL;
    var html = '<button type="button" class="mir-btn' + (state.group === ALL ? ' primary' : '') + '" data-cfg-tab="' + ALL + '" role="tab" aria-selected="' + (state.group === ALL) + '">All <span class="cfg-tab-count" data-cfg-tab-count="' + ALL + '"></span></button>';
    names.forEach(function (n) {
      var on = state.group === n;
      html += '<button type="button" class="mir-btn' + (on ? ' primary' : '') + '" data-cfg-tab="' + esc(n) + '" role="tab" aria-selected="' + on + '">' + esc(n) +
        ' <span class="cfg-tab-count" data-cfg-tab-count="' + esc(n) + '"></span></button>';
    });
    tabs.innerHTML = html;
  }

  function pendingOf(data) {
    var p = (data && data.pendingRestart) || {};
    return { world: p.world || [], process: p.process || [] };
  }

  function renderBanner() {
    var el = q('[data-cfg-banner]');
    if (!el) return;
    var p = pendingOf(state.data);
    var html = '';
    if (p.world.length) {
      html += '<div class="cfg-banner cfg-banner-world">' +
        '<span><strong>' + p.world.length + ' setting(s)</strong> apply after the world restarts: ' +
        p.world.map(function (x) { return '<code>' + esc(x.key) + '</code>'; }).join(', ') + '</span>' +
        '<button type="button" class="mir-btn danger" data-cfg-act="reboot">Reboot world…</button>' +
        '</div>';
    }
    if (p.process.length) {
      html += '<div class="cfg-banner cfg-banner-process">' +
        '<span><strong>' + p.process.length + ' setting(s)</strong> apply only after the server program is restarted (close and start Server.exe): ' +
        p.process.map(function (x) { return '<code>' + esc(x.key) + '</code>'; }).join(', ') + '</span>' +
        '</div>';
    }
    el.innerHTML = html;
  }

  function setRowMessage(field, kind, text) {
    var el = root && root.querySelector('[data-cfg-' + kind + '][data-field="' + cssEsc(field) + '"]');
    if (el) el.textContent = text || '';
    var row = rowOf(field);
    if (row && kind === 'error') row.classList.toggle('cfg-row-error', !!text);
  }

  /** Re-evaluates every row's dirty / invalid state, the counters, the buttons and the shell's dirty flag. */
  function refreshDirty() {
    var changed = changedFields();
    var changedSet = Object.create(null);
    changed.forEach(function (f) { changedSet[f] = true; });
    var invalid = 0;

    state.order.forEach(function (f) {
      var item = state.items[f];
      var row = rowOf(f);
      if (!row) return;
      var dirty = !!changedSet[f];
      row.classList.toggle('cfg-dirty', dirty);
      var undo = row.querySelector('[data-cfg-revert]');
      if (undo) undo.hidden = !dirty;
      var err = null;
      if (dirty && !item.secret) err = validate(item, rawOf(item));
      if (err) invalid++;
      var input = inputOf(f);
      if (input) {
        input.classList.toggle('cfg-rejected', !!err || !!state.errors[f]);
        if (input.type === 'checkbox') {
          var lab = row.querySelector('[data-cfg-bool-label]');
          if (lab) lab.textContent = input.checked ? 'On' : 'Off';
        }
      }
      var errEl = row.querySelector('[data-cfg-error]');
      if (errEl) errEl.textContent = err || state.errors[f] || '';
      row.classList.toggle('cfg-row-error', !!(err || state.errors[f]));
      if (item.secret) {
        var st = row.querySelector('[data-cfg-secret-state]');
        if (st) st.textContent = state.secrets[f] != null ? 'New value staged (unsaved)' : (item.isSet ? 'Set (hidden)' : 'Not set');
      }
    });

    // per-group counters
    var perGroup = Object.create(null);
    (state.data && state.data.sections || []).forEach(function (sec) {
      perGroup[sec.name] = (sec.items || []).filter(function (it) { return changedSet[it.field]; }).length;
    });
    qa('[data-cfg-tab-count]').forEach(function (el) {
      var g = el.getAttribute('data-cfg-tab-count');
      var n = g === ALL ? changed.length : (perGroup[g] || 0);
      el.textContent = n ? String(n) : '';
      el.classList.toggle('hidden', !n);
    });
    qa('[data-cfg-section]').forEach(function (sec) {
      var n = perGroup[sec.getAttribute('data-cfg-section')] || 0;
      var c = sec.querySelector('[data-cfg-section-count]');
      if (c) c.textContent = n ? n + ' unsaved' : '';
    });

    var save = q('[data-cfg-act="save"]');
    if (save) {
      save.disabled = !changed.length || state.loading;
      save.textContent = changed.length ? 'Save ' + changed.length + ' change' + (changed.length === 1 ? '' : 's') : 'Save changes';
      save.title = invalid ? invalid + ' value(s) need fixing first' : '';
    }
    var reset = q('[data-cfg-act="reset"]');
    if (reset) reset.disabled = !changed.length;

    if (global.DbSave && global.DbSave.setDirty) {
      var was = global.DbSave.isDirty && global.DbSave.isDirty(DIRTY_KEY);
      if (!!was !== !!changed.length) global.DbSave.setDirty(DIRTY_KEY, changed.length > 0);
    }
    if (state.changedOnly) applyView();
    return { changed: changed, invalid: invalid };
  }

  /** Tab + filter + changed-only visibility. */
  function applyView() {
    var filter = state.filter;
    var changedSet = null;
    if (state.changedOnly) {
      changedSet = Object.create(null);
      changedFields().forEach(function (f) { changedSet[f] = true; });
    }
    var shown = 0;
    qa('[data-cfg-section]').forEach(function (sec) {
      var name = sec.getAttribute('data-cfg-section');
      var groupOk = state.group === ALL || state.group === name || !!filter;   // a filter searches every group
      var any = false;
      Array.prototype.forEach.call(sec.querySelectorAll('tr[data-cfg-row]'), function (tr) {
        var item = state.items[tr.getAttribute('data-field')];
        var vis = groupOk && !!item && matches(item, filter) && (!changedSet || changedSet[item.field]);
        tr.classList.toggle('hidden', !vis);
        if (vis) { any = true; shown++; }
      });
      sec.classList.toggle('hidden', !any);
    });
    qa('[data-cfg-tab]').forEach(function (b) {
      var on = b.getAttribute('data-cfg-tab') === state.group;
      b.classList.toggle('primary', on);
      b.setAttribute('aria-selected', String(on));
    });
    var count = q('[data-cfg-count]');
    if (count) count.textContent = shown + ' of ' + state.order.length + ' settings' + (filter ? ' (filter searches every group)' : '');
    var body = q('[data-cfg-body]');
    if (body) {
      var empty = body.querySelector('.cfg-empty');
      if (!shown && state.order.length) {
        if (!empty) {
          empty = document.createElement('p');
          empty.className = 'mir-muted cfg-empty';
          body.appendChild(empty);
        }
        empty.textContent = state.changedOnly ? 'No unsaved changes match.' : 'No settings match the filter.';
      } else if (empty) {
        empty.parentNode.removeChild(empty);
      }
    }
  }

  // ---------------------------------------------------------------------------------------------- data

  async function load(opts) {
    opts = opts || {};
    if (!opts.force && changedFields().length) {
      var ok = await global.portalConfirm('Discard your unsaved config edits and reload the values from the server?', { title: 'Reload Server Config', confirmLabel: 'Discard & reload' });
      if (!ok) return false;
    }
    state.loading = true;
    var body = q('[data-cfg-body]');
    try {
      var j = await global.OpsUI.fetchJson('/api/admin/config');
      state.data = j || {};
      state.items = Object.create(null);
      state.order = [];
      (state.data.sections || []).forEach(function (sec) {
        (sec.items || []).forEach(function (it) {
          if (Array.isArray(it.enumMembers) && !(it.options && it.options.length)) {
            it.options = it.enumMembers.map(function (m) { return String(m.name); });
          }
          state.items[it.field] = it;
          state.order.push(it.field);
        });
      });
      state.secrets = Object.create(null);
      if (!opts.keepMessages) {
        state.errors = Object.create(null);
        state.warnings = Object.create(null);
      }
      render();
      if (opts.restore) restoreEdits(opts.restore);
      return true;
    } catch (e) {
      if (body && !state.data) body.innerHTML = '<p class="mir-muted">Could not load the configuration: ' + esc(e && e.message || e) + '</p>';
      global.portalToast('Server Config: ' + (e && e.message || e), 'error');
      return false;
    } finally {
      state.loading = false;
      refreshDirty();
    }
  }

  /** Puts edits back on screen after a reload (rejected changes stay editable). restore: {field: rawValue}. */
  function restoreEdits(restore) {
    Object.keys(restore).forEach(function (f) {
      var item = state.items[f];
      if (!item) return;
      if (item.secret) { state.secrets[f] = restore[f]; return; }
      var el = inputOf(f);
      if (!el) return;
      if (el.type === 'checkbox') el.checked = !!restore[f];
      else el.value = restore[f];
    });
  }

  function revert(field) {
    var item = state.items[field];
    if (!item) return;
    if (item.secret) delete state.secrets[field];
    else {
      var el = inputOf(field);
      if (el) {
        if (el.type === 'checkbox') el.checked = !!item.value;
        else el.value = displayValue(item);
      }
    }
    delete state.errors[field];
    delete state.warnings[field];
    setRowMessage(field, 'warn', '');
    refreshDirty();
  }

  async function resetAll() {
    var n = changedFields().length;
    if (!n) return;
    var ok = await global.portalConfirm('Discard ' + n + ' unsaved change(s)?', { title: 'Reset unsaved', confirmLabel: 'Discard' });
    if (!ok) return;
    changedFields().forEach(revert);
    state.errors = Object.create(null);
    refreshDirty();
  }

  async function changeSecret(field) {
    var item = state.items[field];
    if (!item) return;
    // portalDialog trims text/password values, but the server keeps a secret's leading/trailing whitespace: read the
    // untrimmed text straight from the two password inputs (the dialog is built synchronously, see save()).
    var pwInputs = null;
    function rawSecret(x) {
      if (pwInputs && pwInputs.length >= 2) return { value: String(pwInputs[0].value || ''), confirm: String(pwInputs[1].value || '') };
      return { value: x.value || '', confirm: x.confirm || '' };
    }
    var pending = global.portalDialog({
      title: 'Change ' + (item.label || item.key),
      message: 'The current value is never shown. Enter the new value twice; it is saved with the other changes when you press Save. Leading and trailing spaces are kept.',
      confirmLabel: 'Stage change',
      fields: [
        { name: 'value', label: 'New value', type: 'password', required: true },
        { name: 'confirm', label: 'Repeat new value', type: 'password', required: true }
      ],
      validate: function (x) {
        var r = rawSecret(x);
        var err = validate(item, r.value);
        if (err) return err;
        if (r.value !== r.confirm) return 'The two values do not match.';
        return null;
      }
    });
    var dialogs = document.querySelectorAll('.portal-form-dialog');
    if (dialogs.length) pwInputs = dialogs[dialogs.length - 1].querySelectorAll('input[type="password"]');
    var v = await pending;
    if (!v) return;
    var raw = rawSecret(v);
    if (validate(item, raw.value) || raw.value !== raw.confirm) return;
    state.secrets[field] = raw.value;
    delete state.errors[field];
    refreshDirty();
  }

  async function save() {
    var res = refreshDirty();
    if (!res.changed.length) { global.portalToast('No changes to save.', 'info'); return; }
    if (res.invalid) {
      global.portalToast(res.invalid + ' value(s) are invalid: fix the fields marked in red first.', 'error');
      showInvalid();
      return;
    }

    var changes = [];
    var lines = [];
    var restartLines = 0;
    var risky = [];
    res.changed.forEach(function (f) {
      var item = state.items[f];
      var raw = item.secret ? state.secrets[f] : rawOf(item);
      var value = payloadValue(item, raw);
      var change = { field: f, value: value };
      if (!item.secret) change.original = item.value;
      changes.push(change);
      lines.push('[' + item.section + '] ' + item.key + ': ' + describe(item, item.value) + ' → ' + describe(item, value) +
        (item.restart === 'world' ? '  (world restart)' : item.restart === 'process' ? '  (server restart)' : ''));
      if (item.restartRequired) restartLines++;
      if (f === 'WebAdminEnabled' && value === false) risky.push('Web admin will be DISABLED after the next server restart: you will not be able to sign in here.');
      if (f === 'WebAdminPort' || f === 'WebAdminBind') risky.push('The panel address changes after the next server restart.');
      if (f === 'AllowLogin' && value === false) risky.push('Players will not be able to log in.');
      if (f === 'AllowStartGame' && value === false) risky.push('Non-admin accounts will not be able to enter the game.');
    });

    var message = lines.slice(0, 25).join('\n') + (lines.length > 25 ? '\n… and ' + (lines.length - 25) + ' more' : '') +
      (restartLines ? '\n\n' + restartLines + ' change(s) take effect only after a restart.' : '') +
      (risky.length ? '\n\n⚠ ' + risky.join('\n⚠ ') : '');

    var pending = global.portalDialog({
      title: 'Save ' + changes.length + ' change(s) to Setup.ini?',
      message: message,
      confirmLabel: 'Save',
      danger: risky.length > 0,
      fields: [{ name: 'reason', label: 'Reason (optional, audit trail)', type: 'text', placeholder: 'e.g. weekend EXP event' }]
    });
    // The dialog is built synchronously: tag it so the change list keeps its line breaks (admin-config.css).
    var dialogs = document.querySelectorAll('.portal-form-dialog');
    if (dialogs.length) dialogs[dialogs.length - 1].classList.add('cfg-save-dialog');
    var dlg = await pending;
    if (!dlg) return;

    var btn = q('[data-cfg-act="save"]');
    var outcome;
    await (global.OpsUI ? global.OpsUI.withBusy : function (b, fn) { return fn(); })(btn, async function () {
      try {
        var r = await global.portalApi('/api/admin/config', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ changes: changes, reason: dlg.reason || undefined })
        });
        var j = await r.json().catch(function () { return {}; });
        outcome = { status: r.status, body: j, parsed: interpretSave(r.status, j) };
      } catch (e) {
        outcome = { status: 0, body: {}, parsed: { applied: [], rejected: {}, warnings: {}, unchanged: [], kind: 'error', message: 'Request failed: ' + (e && e.message || e) } };
      }
    }, 'Saving…');

    // withBusy skips the work (returns null) while the Save button is disabled, i.e. a save is already in flight.
    if (!outcome) return;
    var p = outcome.parsed;
    global.portalToast(p.message, p.kind === 'warn' ? 'warn' : p.kind);
    if (outcome.status === 0 || (outcome.status >= 500)) return;   // nothing known about the server state: keep the page as is

    // Keep the edits that were not applied on screen (rejected), reload everything else.
    var keep = {};
    res.changed.forEach(function (f) {
      if (p.applied.indexOf(f) >= 0 || p.unchanged.indexOf(f) >= 0) return;
      var item = state.items[f];
      keep[f] = item.secret ? state.secrets[f] : rawOf(item);
    });
    if (outcome.status === 400 || outcome.status === 413) {
      // the request as a whole was refused: nothing changed on the server
      return;
    }
    state.errors = p.rejected;
    state.warnings = p.warnings;
    await load({ force: true, keepMessages: true, restore: keep });
    if (p.applied.length && global.AdminStatus && typeof global.AdminStatus.refresh === 'function') global.AdminStatus.refresh();
  }

  function showInvalid() {
    var bad = state.order.filter(function (f) {
      var item = state.items[f];
      return !item.secret && isChanged(item, rawOf(item)) && validate(item, rawOf(item));
    });
    if (!bad.length) return;
    focusField(bad[0]);
  }

  function focusField(field) {
    var row = rowOf(field);
    if (!row) return;
    var item = state.items[field];
    if (row.classList.contains('hidden') && item) {
      state.filter = '';
      var fb = q('[data-cfg-filter]');
      if (fb) fb.value = '';
      state.changedOnly = false;
      var co = q('[data-cfg-changed-only]');
      if (co) co.checked = false;
      var sec = row.closest('[data-cfg-section]');
      state.group = sec ? sec.getAttribute('data-cfg-section') : ALL;
      applyView();
    }
    row.scrollIntoView({ block: 'center', behavior: 'smooth' });
    row.classList.add('cfg-highlight');
    setTimeout(function () { row.classList.remove('cfg-highlight'); }, 1800);
    var input = inputOf(field) || row.querySelector('button');
    if (input && input.focus) { try { input.focus({ preventScroll: true }); } catch (e) { input.focus(); } }
  }

  async function reboot() {
    var ok = await global.portalConfirm('Reboot the world now? Every player is disconnected while the world stops and starts again. World-restart settings take effect.', { title: 'Reboot world', confirmLabel: 'Reboot' });
    if (!ok) return;
    try {
      var r = await global.portalApi('/api/admin/server/reboot', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
      var j = await r.json().catch(function () { return {}; });
      if (r.status === 404) { global.portalToast('The reboot action is not available in this build. Use Server Control or the server window.', 'error'); return; }
      global.portalToast(j.message || (r.ok ? 'Reboot requested.' : 'Reboot failed (HTTP ' + r.status + ').'), r.ok ? 'ok' : 'error');
      if (r.ok && global.AdminStatus && typeof global.AdminStatus.refresh === 'function') global.AdminStatus.refresh();
    } catch (e) {
      global.portalToast('Reboot failed: ' + (e && e.message || e), 'error');
    }
  }

  // ---------------------------------------------------------------------------------------------- wiring

  function wire() {
    root.addEventListener('click', function (e) {
      var t = e.target && e.target.closest ? e.target : null;
      if (!t) return;
      var act = t.closest('[data-cfg-act]');
      if (act) {
        var a = act.getAttribute('data-cfg-act');
        if (a === 'reload') load();
        else if (a === 'reset') resetAll();
        else if (a === 'save') save();
        else if (a === 'reboot') reboot();
        return;
      }
      var tab = t.closest('[data-cfg-tab]');
      if (tab) {
        state.group = tab.getAttribute('data-cfg-tab');
        lsSet(LS_GROUP, state.group);
        if (global.portalSetHashParams) global.portalSetHashParams({ group: state.group === ALL ? null : state.group }, true);
        applyView();
        return;
      }
      var rv = t.closest('[data-cfg-revert]');
      if (rv) { revert(rv.getAttribute('data-cfg-revert')); return; }
      var sec = t.closest('[data-cfg-secret]');
      if (sec) { changeSecret(sec.getAttribute('data-cfg-secret')); }
    });

    var onEdit = function (e) {
      var el = e.target;
      if (!el || !el.hasAttribute || !el.hasAttribute('data-cfg-input')) return;
      var f = el.getAttribute('data-field');
      if (state.errors[f]) { delete state.errors[f]; }
      if (state.warnings[f]) { delete state.warnings[f]; setRowMessage(f, 'warn', ''); }
      refreshDirty();
    };
    root.addEventListener('input', onEdit);
    root.addEventListener('change', onEdit);

    var filter = q('[data-cfg-filter]');
    if (filter) {
      var timer = null;
      filter.addEventListener('input', function () {
        clearTimeout(timer);
        timer = setTimeout(function () {
          state.filter = filter.value.trim();
          if (global.portalSetHashParams) global.portalSetHashParams({ q: state.filter || null }, true);
          applyView();
        }, 120);
      });
    }
    var co = q('[data-cfg-changed-only]');
    if (co) co.addEventListener('change', function () { state.changedOnly = co.checked; applyView(); });

    root.addEventListener('keydown', function (e) {
      if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
        e.preventDefault();
        save();
      }
    });
  }

  function applyParams(params) {
    params = params || {};
    var changed = false;
    if (params.group != null) {
      var g = String(params.group);
      state.group = g === '' ? ALL : g;
      changed = true;
    }
    if (params.q != null) {
      state.filter = String(params.q);
      var fb = q('[data-cfg-filter]');
      if (fb) fb.value = state.filter;
      changed = true;
    }
    if (changed && state.data) applyView();
    if (params.field && state.data) {
      var item = state.items[params.field] || null;
      if (!item) {
        var lower = String(params.field).toLowerCase();
        state.order.some(function (f) {
          var it = state.items[f];
          if (f.toLowerCase() === lower || String(it.key).toLowerCase() === lower) { item = it; return true; }
          return false;
        });
      }
      if (item) focusField(item.field);
    }
  }

  var justLoaded = false;

  global.AdminSections.register('config', {
    load: function (el) {
      root = el;
      shell();
      wire();
      var saved = lsGet(LS_GROUP);
      if (saved) state.group = saved;
      justLoaded = true;
      return load({ force: true });
    },
    show: function () {
      // First show: load() just fetched. Coming back keeps unsaved edits (the shell asked before leaving); otherwise refresh.
      if (justLoaded) { justLoaded = false; return; }
      if (state.loading) return;
      if (!changedFields().length) return load({ force: true });
    }
  });

  // Runs after load/show on every visit (and for pasted links): group, filter, field focus.
  global.portalRegisterDeepLink('config', function (params) {
    if (!root) return;
    applyParams(params);
  });
})(window);
