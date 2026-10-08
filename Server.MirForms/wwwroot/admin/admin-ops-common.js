/*
 * Shared helpers for the MirCraft admin section modules (OpsUI).
 *
 * Everything here is deliberately small and dependency-free: the admin site has no build step,
 * so these are plain functions on a single global rather than a module.
 */
(function (global) {
  'use strict';

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function val(id) {
    var el = document.getElementById(id);
    if (!el) return '';
    return el.type === 'checkbox' ? el.checked : (el.value || '').trim();
  }

  function num(id, fallback) {
    var raw = val(id);
    if (raw === '' || raw === null) return fallback;
    var n = Number(raw);
    return isNaN(n) ? fallback : n;
  }

  function setText(id, text) {
    var el = document.getElementById(id);
    if (el) el.textContent = text == null ? '' : String(text);
  }

  function setHtml(id, html) {
    var el = document.getElementById(id);
    if (el) el.innerHTML = html;
  }

  /*
   * POST JSON and surface the result as a toast. Resolves with the parsed body (or {}), plus __ok (bool) and __status.
   * opts: { method ('POST' default; 'PUT' / 'DELETE' allowed), quiet (no toast on success) }.
   */
  async function post(url, body, opts) {
    opts = opts || {};
    try {
      var req = { method: opts.method || 'POST', headers: { 'Content-Type': 'application/json' } };
      if (body !== undefined) req.body = JSON.stringify(body);
      var r = await portalApi(url, req);
      var j = await r.json().catch(function () { return {}; });
      if (!j || typeof j !== 'object') j = { value: j };
      var ok = r.ok && j.ok !== false;
      if (!ok || !opts.quiet) portalToast(j.message || j.error || (ok ? 'Done.' : ('Failed (HTTP ' + r.status + ').')), ok ? 'ok' : 'error');
      j.__status = r.status;
      j.__ok = ok;
      return j;
    } catch (e) {
      portalToast('Request failed: ' + e, 'error');
      return { __ok: false };
    }
  }

  /* Silent GET returning the parsed body, or null on failure. */
  async function get(url) {
    try {
      var r = await portalApi(url, { method: 'GET' });
      if (!r.ok) return null;
      return await r.json().catch(function () { return null; });
    } catch (e) {
      return null;
    }
  }

  async function withBusy(button, work, busyText) {
    if (!button) return await work();
    if (button.disabled) return null;
    var oldText = button.textContent;
    button.disabled = true;
    button.setAttribute('aria-busy', 'true');
    if (busyText) button.textContent = busyText;
    try { return await work(); }
    finally {
      button.disabled = false;
      button.removeAttribute('aria-busy');
      if (busyText) button.textContent = oldText;
    }
  }

  /*
   * A server timestamp -> Date (or null). Accepts ISO-8601 strings (System.Text.Json DateTime), Date objects, unix seconds
   * (< 1e11) and unix milliseconds. An ISO string without a zone is read as local time by the browser; one ending in Z as UTC.
   */
  function toDate(v) {
    if (v == null || v === '') return null;
    if (v instanceof Date) return isNaN(v.getTime()) ? null : v;
    if (typeof v === 'number') return new Date(v < 1e11 ? v * 1000 : v);
    var s = String(v);
    if (/^\d+$/.test(s)) { var n = Number(s); return new Date(n < 1e11 ? n * 1000 : n); }
    if (/^0001-01-01/.test(s)) return null;   // DateTime.MinValue
    var d = new Date(s);
    return isNaN(d.getTime()) ? null : d;
  }

  /* A server timestamp as local date + time text ('' for none). */
  function fmtDate(v) {
    var d = toDate(v);
    return d ? d.toLocaleString() : '';
  }

  /* Relative "3m ago" for a server timestamp (ISO string, Date, unix seconds or ms); "in 5m" for a future one. */
  function ago(when) {
    var dt = toDate(when);
    if (!dt) return '';
    var diff = Math.floor((Date.now() - dt.getTime()) / 1000);
    if (diff < -5) return 'in ' + span(-diff);
    return span(Math.max(0, diff)) + ' ago';
  }
  function span(d) {
    if (d < 60) return d + 's';
    if (d < 3600) return Math.floor(d / 60) + 'm';
    if (d < 86400) return Math.floor(d / 3600) + 'h';
    return Math.floor(d / 86400) + 'd';
  }

  /* Seconds -> "2d 3h 4m" (uptime and the like). */
  function duration(seconds) {
    var s = Math.max(0, Math.floor(Number(seconds) || 0));
    var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
    if (d) return d + 'd ' + h + 'h ' + m + 'm';
    if (h) return h + 'h ' + m + 'm';
    if (m) return m + 'm ' + (s % 60) + 's';
    return (s % 60) + 's';
  }

  /* The payload of an { ok, message, data } envelope (or the body itself when it is not one). */
  function unwrap(j) {
    if (j && typeof j === 'object' && 'ok' in j && 'data' in j) return j.data;
    return j;
  }

  /* Legacy relative time from unix seconds (kept for copied modules). */
  function agoSeconds(unixSeconds) {
    if (!unixSeconds) return '';
    var d = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (d < 60) return d + 's ago';
    if (d < 3600) return Math.floor(d / 60) + 'm ago';
    if (d < 86400) return Math.floor(d / 3600) + 'h ago';
    return Math.floor(d / 86400) + 'd ago';
  }

  function formatNumber(n) {
    if (n == null || isNaN(n)) return '0';
    return Number(n).toLocaleString();
  }

  /*
   * Renders a list of OpsLog entries into a scrollable log block. `extra` may return
   * additional HTML (e.g. per-row action buttons) appended to each row.
   */
  function renderLog(containerId, entries, extra, opts) {
    var el = document.getElementById(containerId);
    if (!el) return;
    opts = opts || {};

    if (!entries || !entries.length) {
      el.innerHTML = '<p class="mir-muted">' + esc(opts.empty || 'Nothing to show.') + '</p>';
      return;
    }

    var html = '';
    for (var i = 0; i < entries.length; i++) {
      var e = entries[i];
      // Names become links through the shared player chip (§5); accounts and admins stay text.
      var who = e.character
        ? playerLink(e.character, { account: e.account })
        : esc(e.account || e.actor || '');
      var data = '';
      if (opts.showData && e.data && typeof e.data === 'object' && Object.keys(e.data).length) {
        data = '<details class="ops-row-data"><summary>data</summary><pre>' + esc(JSON.stringify(e.data, null, 2)) + '</pre></details>';
      }
      html += '<div class="ops-row">' +
        '<span class="ops-row-time" title="' + esc(e.time || fmtDate(e.ts || e.t)) + '">' + esc(ago(e.t || e.ts || e.time)) + '</span>' +
        '<span class="ops-row-tag">' + esc(e.action || '') + '</span>' +
        '<span class="ops-row-who">' + who + '</span>' +
        '<span class="ops-row-text">' + esc(e.detail || '') + '</span>' +
        (extra ? extra(e) : '') + data +
        '</div>';
    }
    el.innerHTML = html;
  }

  /*
   * GET -> the parsed JSON body. Throws an Error (with .status and .body) on a network failure or a non-2xx status; the
   * message is the server's { message } (or error) when it sent one.
   */
  async function fetchJson(url) {
    var r = await portalApi(url, { method: 'GET' });
    var j = await r.json().catch(function () { return null; });
    if (!r.ok) {
      var err = new Error((j && (j.message || j.error)) || ('HTTP ' + r.status));
      err.status = r.status;
      err.body = j;
      throw err;
    }
    return j;
  }

  /*
   * GET that tells a failure apart from "nothing" and never throws: resolves { ok, status, data, error }.
   * data is the parsed body (or null); error is the server's message or the HTTP status.
   */
  async function fetchResult(url) {
    try {
      var r = await portalApi(url, { method: 'GET' });
      var j = await r.json().catch(function () { return null; });
      if (!r.ok) return { ok: false, status: r.status, data: j, error: (j && (j.message || j.error)) || ('HTTP ' + r.status) };
      return { ok: true, status: r.status, data: j, error: null };
    } catch (e) {
      return { ok: false, status: 0, data: null, error: String(e && e.message || e) };
    }
  }

  /*
   * A player name as a link (§5 "names become links"): the shared player chip when WS2's PlayerChip is loaded,
   * otherwise the escaped name. opts: { account, online, characterIndex }.
   */
  function playerLink(name, opts) {
    if (name == null || name === '') return '';
    if (global.PlayerChip && typeof global.PlayerChip.html === 'function') {
      try { return global.PlayerChip.html(String(name), opts || {}); } catch (e) { /* fall back to text */ }
    }
    return esc(name);
  }

  /*
   * B7: one dialog per action — fields (reason, duration presets, ...) and an explicit Confirm. Cancel, Escape or a
   * click outside resolves null, and the caller must then do nothing. Enter never confirms (B62): only a click on
   * Confirm (or Space/Enter while Confirm itself has focus, which the browser turns into a click) does.
   *
   * spec: { title, message, confirmText, danger, fields: [{ key, label, type: 'text'|'number'|'textarea'|'select'|
   *         'checkbox', value, options: [{ value, label }], presets: [{ label, value }], required, min, max,
   *         placeholder, hint }] }
   * Resolves { key: value, ... } (numbers for type 'number', booleans for 'checkbox') or null.
   */
  function actionDialog(spec) {
    spec = spec || {};
    return new Promise(function (resolve) {
      var doc = global.document;
      var backdrop = doc.createElement('div');
      backdrop.className = 'portal-confirm-backdrop';
      var dialog = doc.createElement('div');
      dialog.className = 'portal-confirm-dialog ops-dialog';
      dialog.setAttribute('role', 'dialog');
      dialog.setAttribute('aria-modal', 'true');

      var title = doc.createElement('h3');
      title.textContent = spec.title || 'Confirm action';
      dialog.appendChild(title);
      if (spec.message) {
        var copy = doc.createElement('p');
        copy.textContent = spec.message;
        dialog.appendChild(copy);
      }

      var inputs = {};
      (spec.fields || []).forEach(function (f) {
        var wrap = doc.createElement('label');
        wrap.className = 'ops-dialog-field';
        var cap = doc.createElement('span');
        cap.textContent = f.label || f.key;
        wrap.appendChild(cap);

        var input;
        if (f.type === 'select') {
          input = doc.createElement('select');
          (f.options || []).forEach(function (o) {
            var opt = doc.createElement('option');
            opt.value = String(o.value);
            opt.textContent = o.label == null ? String(o.value) : o.label;
            input.appendChild(opt);
          });
          if (f.value != null) input.value = String(f.value);
        } else if (f.type === 'textarea') {
          input = doc.createElement('textarea');
          input.rows = 3;
          input.value = f.value == null ? '' : String(f.value);
        } else {
          input = doc.createElement('input');
          input.type = f.type === 'number' ? 'number' : f.type === 'checkbox' ? 'checkbox' : 'text';
          if (f.type === 'checkbox') input.checked = !!f.value;
          else input.value = f.value == null ? '' : String(f.value);
          if (f.min != null) input.min = f.min;
          if (f.max != null) input.max = f.max;
        }
        input.className = 'fd-input';
        if (f.placeholder) input.placeholder = f.placeholder;
        wrap.appendChild(input);
        inputs[f.key] = { field: f, el: input };

        if (f.presets && f.presets.length) {
          var row = doc.createElement('div');
          row.className = 'ops-dialog-presets';
          f.presets.forEach(function (p) {
            var b = doc.createElement('button');
            b.type = 'button';
            b.className = 'mir-btn ops-row-btn';
            b.textContent = p.label;
            b.addEventListener('click', function () { input.value = String(p.value); });
            row.appendChild(b);
          });
          wrap.appendChild(row);
        }
        if (f.hint) {
          var hint = doc.createElement('span');
          hint.className = 'mir-muted';
          hint.textContent = f.hint;
          wrap.appendChild(hint);
        }
        dialog.appendChild(wrap);
      });

      var error = doc.createElement('p');
      error.className = 'ops-dialog-error';
      dialog.appendChild(error);

      var actions = doc.createElement('div');
      actions.className = 'portal-confirm-actions';
      var cancel = doc.createElement('button');
      cancel.type = 'button';
      cancel.className = 'mir-btn';
      cancel.textContent = 'Cancel';
      var confirm = doc.createElement('button');
      confirm.type = 'button';
      confirm.className = 'mir-btn ' + (spec.danger === false ? 'primary' : 'danger');
      confirm.textContent = spec.confirmText || 'Confirm';
      actions.appendChild(cancel);
      actions.appendChild(confirm);
      dialog.appendChild(actions);
      backdrop.appendChild(dialog);

      function finish(value) {
        doc.removeEventListener('keydown', onKey);
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        resolve(value);
      }
      function onKey(e) {
        if (e.key === 'Escape') finish(null);
      }
      function collect() {
        var out = {};
        var keys = Object.keys(inputs);
        for (var i = 0; i < keys.length; i++) {
          var f = inputs[keys[i]].field, el = inputs[keys[i]].el;
          var v;
          if (f.type === 'checkbox') v = !!el.checked;
          else {
            v = String(el.value == null ? '' : el.value).trim();
            if (f.required && v === '') return { error: (f.label || f.key) + ' is required.' };
            if (f.type === 'number' && v !== '') {
              var n = Number(v);
              if (!isFinite(n)) return { error: (f.label || f.key) + ' must be a number.' };
              if (f.min != null && n < f.min) return { error: (f.label || f.key) + ' must be at least ' + f.min + '.' };
              if (f.max != null && n > f.max) return { error: (f.label || f.key) + ' must be at most ' + f.max + '.' };
              v = n;
            }
          }
          out[keys[i]] = v;
        }
        return { values: out };
      }

      cancel.addEventListener('click', function () { finish(null); });
      confirm.addEventListener('click', function () {
        var r = collect();
        if (r.error) { error.textContent = r.error; return; }
        finish(r.values);
      });
      backdrop.addEventListener('click', function (e) { if (e.target === backdrop) finish(null); });
      doc.addEventListener('keydown', onKey);
      doc.body.appendChild(backdrop);
      var first = (spec.fields || []).length ? inputs[spec.fields[0].key].el : cancel;
      if (first && first.focus) first.focus();
    });
  }

  /* Duration presets shared by the GM dialogs (minutes). 'indefinite' / 'permanent' are explicit choices. */
  var MUTE_PRESETS = [
    { value: '15', label: '15 minutes' }, { value: '60', label: '1 hour' }, { value: '1440', label: '1 day' },
    { value: '10080', label: '7 days' }, { value: 'indefinite', label: 'Indefinitely' }
  ];
  var BAN_PRESETS = [
    { value: '60', label: '1 hour' }, { value: '1440', label: '1 day' }, { value: '10080', label: '7 days' },
    { value: '43200', label: '30 days' }, { value: 'permanent', label: 'Permanently' }
  ];

  /*
   * Turns a duration choice into the body the GM action expects (pure; tested). kind 'mute' -> {minutes} or
   * {indefinite:true}; kind 'ban' -> {minutes} or {permanent:true}. Returns null for anything else (no default).
   */
  function durationBody(kind, choice) {
    var c = String(choice == null ? '' : choice).trim().toLowerCase();
    if (kind === 'mute' && c === 'indefinite') return { indefinite: true, minutes: 0 };
    if (kind === 'ban' && c === 'permanent') return { permanent: true, minutes: 0 };
    var n = Number(c);
    if (c === '' || !isFinite(n) || n < 1 || Math.floor(n) !== n) return null;
    return { minutes: n };
  }

  /* Downloads text as a file (logs, chat log export, CSV). */
  function download(filename, text, type) {
    try {
      var blob = new Blob([text], { type: type || 'text/plain;charset=utf-8' });
      var url = URL.createObjectURL(blob);
      var a = global.document.createElement('a');
      a.href = url;
      a.download = filename;
      global.document.body.appendChild(a);
      a.click();
      setTimeout(function () { URL.revokeObjectURL(url); if (a.parentNode) a.parentNode.removeChild(a); }, 0);
    } catch (e) {
      portalToast('Download failed: ' + e, 'error');
    }
  }

  /* CSV with every cell quoted (pure). */
  function toCsv(header, rows) {
    function cell(v) { return '"' + String(v == null ? '' : v).replace(/"/g, '""') + '"'; }
    var out = [header.map(cell).join(',')];
    rows.forEach(function (r) { out.push(r.map(cell).join(',')); });
    return out.join('\r\n');
  }

  /* True while the section is shown and the tab is in the foreground (polls stop otherwise, §5 / 8.2). */
  function sectionVisible(id) {
    var doc = global.document;
    if (doc.hidden) return false;
    var sec = doc.getElementById('section-' + id);
    return !!sec && !sec.classList.contains('hidden');
  }

  global.OpsUI = {
    esc: esc,
    val: val,
    num: num,
    setText: setText,
    setHtml: setHtml,
    post: post,
    get: get,
    fetchJson: fetchJson,
    fetchResult: fetchResult,
    withBusy: withBusy,
    ago: ago,
    agoSeconds: agoSeconds,
    toDate: toDate,
    fmtDate: fmtDate,
    duration: duration,
    unwrap: unwrap,
    formatNumber: formatNumber,
    renderLog: renderLog,
    playerLink: playerLink,
    actionDialog: actionDialog,
    durationBody: durationBody,
    MUTE_PRESETS: MUTE_PRESETS,
    BAN_PRESETS: BAN_PRESETS,
    download: download,
    toCsv: toCsv,
    sectionVisible: sectionVisible
  };
})(window);
