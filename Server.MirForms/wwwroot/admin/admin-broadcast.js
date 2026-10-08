/*
 * Broadcast (#section-broadcast): server-wide messages and the login notice.
 *
 *   POST /api/admin/broadcast {message, type}      announcement | shout | system | hint | linemessage (one chat line per text line)
 *   GET  /api/admin/broadcast/recent               recent broadcasts (audit trail) with Resend
 *   GET/PUT /api/admin/broadcast/notice            Envir\Notice.txt (etag guarded; optional push to online players)
 *
 * The notice editor is dirty-guarded through DbSave.setDirty('broadcast-notice') (leave-section and close-tab prompts).
 */
(function (global) {
  'use strict';

  var esc = function (s) { return OpsUI.esc(s); };
  var DIRTY_KEY = 'broadcast-notice';
  var TYPES = [
    { value: 'announcement', label: 'Announcement', hint: 'Shown in the announcement colour to everyone.' },
    { value: 'shout', label: 'Server shout', hint: 'Looks like a server-wide shout.' },
    { value: 'system', label: 'System', hint: 'System message line.' },
    { value: 'hint', label: 'Hint', hint: 'Hint line (help colour).' },
    { value: 'linemessage', label: 'Line message', hint: 'Like the rotating line messages.' }
  ];
  var MAX_LINES = 10;
  var MAX_LEN = 500;

  var root = null;
  var notice = null;          // last GET /notice payload
  var noticeDirty = false;
  var stopPoll = null;

  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }

  function setDirty(on) {
    noticeDirty = !!on;
    if (global.DbSave && typeof DbSave.setDirty === 'function') DbSave.setDirty(DIRTY_KEY, noticeDirty);
    var s = el('notice-state');
    if (s) s.textContent = noticeDirty ? 'Unsaved changes' : (notice && notice.lastWriteUtc ? 'Saved ' + OpsUI.fmtDate(notice.lastWriteUtc) : '');
    var save = el('notice-save');
    if (save) save.disabled = !noticeDirty;
    var revert = el('notice-revert');
    if (revert) revert.disabled = !noticeDirty;
  }

  /* Lines that will be sent (mirrors the server's SplitLines: trimmed, blanks dropped). Pure. */
  function linesOf(text) {
    return String(text || '').replace(/\r\n?/g, '\n').split('\n')
      .map(function (l) { return l.replace(/[\u0000-\u001f\u007f]/g, ' ').trim(); })
      .filter(function (l) { return l.length > 0; });
  }

  function typeLabel(v) {
    for (var i = 0; i < TYPES.length; i++) if (TYPES[i].value === v) return TYPES[i].label;
    return v || '';
  }

  // ---- broadcast ----------------------------------------------------------------------------------------------------

  function renderPreview() {
    var box = el('message');
    var type = el('type').value;
    var lines = linesOf(box.value);
    var prev = el('preview');
    var count = el('count');
    var tooLong = lines.some(function (l) { return l.length > MAX_LEN; });

    count.textContent = lines.length + ' line' + (lines.length === 1 ? '' : 's') + ' · ' + box.value.length + ' characters' +
      (lines.length > MAX_LINES ? ' · too many lines (max ' + MAX_LINES + ')' : '') + (tooLong ? ' · a line is longer than ' + MAX_LEN : '');
    count.className = 'mir-muted' + (lines.length > MAX_LINES || tooLong ? ' bc-warn' : '');

    var hint = TYPES.filter(function (t) { return t.value === type; })[0];
    el('type-hint').textContent = hint ? hint.hint : '';

    prev.innerHTML = lines.length
      ? lines.map(function (l) { return '<div class="bc-line bc-type-' + esc(type) + '">' + esc(l) + '</div>'; }).join('')
      : '<div class="mir-muted">The preview appears here.</div>';
    el('send').disabled = !lines.length || lines.length > MAX_LINES;
  }

  function onlineCount() {
    var st = global.AdminStatus && AdminStatus.last();
    return st && typeof st.players === 'number' ? st.players : null;
  }

  async function send(message, type, button) {
    var lines = linesOf(message);
    if (!lines.length) { portalToast('Type a message first.', 'error'); return null; }
    var n = onlineCount();
    var who = n == null ? 'every online player' : (n + ' online player' + (n === 1 ? '' : 's'));
    var ok = await portalConfirm('Send this ' + typeLabel(type).toLowerCase() + ' (' + lines.length + ' line' + (lines.length === 1 ? '' : 's') + ') to ' + who + '?\n\n' +
      lines.slice(0, 4).join('\n') + (lines.length > 4 ? '\n…' : ''), { title: 'Broadcast', confirmLabel: 'Send' });
    if (!ok) return null;
    var j = await OpsUI.withBusy(button, function () {
      return OpsUI.post('/api/admin/broadcast', { message: lines.join('\n'), type: type });
    }, 'Sending…');
    if (j && j.__ok) loadRecent();
    return j;
  }

  async function loadRecent() {
    var host = el('recent');
    if (!host) return;
    var j = await OpsUI.get('/api/admin/broadcast/recent?limit=25');
    var entries = (j && j.entries) || [];
    if (!entries.length) { host.innerHTML = '<p class="mir-muted bc-empty">No broadcasts in the last 30 days.</p>'; return; }
    host.innerHTML = entries.map(function (e, i) {
      return '<div class="ops-row">' +
        '<span class="ops-row-time" title="' + esc(OpsUI.fmtDate(e.ts)) + '">' + esc(OpsUI.ago(e.ts)) + '</span>' +
        '<span class="ops-row-tag">' + esc(typeLabel(e.type)) + '</span>' +
        '<span class="ops-row-text bc-recent-text">' + esc(e.message) + '</span>' +
        '<span class="mir-muted">' + esc(e.admin || '') + (e.recipients >= 0 ? ' · ' + esc(e.recipients) + ' online' : '') + '</span>' +
        '<button type="button" class="mir-btn ops-row-btn" data-bc-edit="' + i + '" title="Copy into the editor">Edit</button>' +
        '<button type="button" class="mir-btn primary ops-row-btn" data-bc-resend="' + i + '">Resend</button>' +
        '</div>';
    }).join('');
    host._entries = entries;
  }

  // ---- notice -------------------------------------------------------------------------------------------------------

  /* Approximates Settings.LoadNotice: a "TITLE=..." line becomes the title, the other lines the message. Pure. */
  function parseNotice(text) {
    var title = '', body = [];
    String(text || '').replace(/\r\n?/g, '\n').split('\n').forEach(function (line) {
      var eq = line.indexOf('=');
      if (eq > 0 && /^title/i.test(line.slice(0, eq).trim())) { title = line.slice(eq + 1); return; }
      body.push(line);
    });
    while (body.length && body[body.length - 1] === '') body.pop();
    return { title: title, message: body.join('\n') };
  }

  function renderNoticePreview() {
    var p = parseNotice(el('notice').value);
    el('notice-preview').innerHTML =
      '<div class="bc-notice-title">' + (p.title ? esc(p.title) : '<span class="mir-muted">(no TITLE= line)</span>') + '</div>' +
      '<pre class="bc-notice-body">' + (p.message ? esc(p.message) : '<span class="mir-muted">(empty: players get no notice)</span>') + '</pre>';
  }

  async function loadNotice(force) {
    if (noticeDirty && !force) return;
    var r = await OpsUI.fetchResult('/api/admin/broadcast/notice');
    if (!r.ok) { el('notice-meta').textContent = 'Could not load the notice: ' + r.error; return; }
    notice = r.data;
    el('notice').value = notice.content || '';
    el('notice-meta').textContent = (notice.fullPath || notice.path || '') + (notice.exists ? '' : ' (not created yet)');
    renderNoticePreview();
    setDirty(false);
  }

  async function saveNotice(button) {
    if (!notice) return;
    var push = !!el('notice-push').checked;
    var ok = await portalConfirm('Save the login notice' + (push ? ' and show it to every player online now' : '') + '?\n\nPlayers see a changed notice at their next login.',
      { title: 'Save notice', confirmLabel: 'Save' });
    if (!ok) return;
    var j = await OpsUI.withBusy(button, function () {
      return OpsUI.post('/api/admin/broadcast/notice', { content: el('notice').value, etag: notice.etag, pushOnline: push }, { method: 'PUT' });
    }, 'Saving…');
    if (j && j.__ok && j.data) {
      notice = j.data;
      el('notice').value = notice.content || '';
      renderNoticePreview();
      setDirty(false);
    } else if (j && j.__status === 409) {
      portalToast('Someone changed the notice on disk. Reload it (your text is still in the editor; copy it first).', 'error');
    }
  }

  // ---- section ------------------------------------------------------------------------------------------------------

  function render(sectionEl) {
    sectionEl.innerHTML =
      '<div class="bc-columns">' +
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Broadcast</h2><span class="mir-muted" data-role="online"></span></div>' +
      '  <div class="ops-form">' +
      '    <label>Type<select class="fd-input" data-role="type">' + TYPES.map(function (t) { return '<option value="' + esc(t.value) + '">' + esc(t.label) + '</option>'; }).join('') + '</select></label>' +
      '    <span class="mir-muted" data-role="type-hint"></span>' +
      '  </div>' +
      '  <textarea class="fd-input ops-textarea bc-message" data-role="message" rows="4" maxlength="5000" placeholder="Message to every online player (one chat line per line, up to 10 lines)"></textarea>' +
      '  <div class="bc-meta"><span data-role="count" class="mir-muted"></span></div>' +
      '  <div class="bc-preview" data-role="preview" aria-label="Preview"></div>' +
      '  <div class="bc-actions"><button type="button" class="mir-btn primary" data-role="send" disabled>Send…</button>' +
      '    <button type="button" class="mir-btn" data-role="clear">Clear</button></div>' +
      '  <div class="ops-group"><div class="toolbar"><h3>Recent broadcasts</h3><button type="button" class="mir-btn" data-role="refresh">Refresh</button></div>' +
      '    <div class="ops-log" data-role="recent"></div></div>' +
      '</div>' +
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Login notice</h2><span class="mir-muted" data-role="notice-state"></span></div>' +
      '  <p class="mir-muted">Shown to each player at login when it changed since their last logout. A line <code>TITLE=…</code> sets the title.</p>' +
      '  <p class="mir-muted mono bc-path" data-role="notice-meta"></p>' +
      '  <textarea class="fd-input ops-textarea mono bc-notice" data-role="notice" rows="12" spellcheck="false"></textarea>' +
      '  <div class="ops-form"><label class="mir-toggle"><input type="checkbox" data-role="notice-push" /> Also show it to players online now</label></div>' +
      '  <div class="bc-actions">' +
      '    <button type="button" class="mir-btn primary" data-role="notice-save" disabled>Save notice…</button>' +
      '    <button type="button" class="mir-btn" data-role="notice-revert" disabled>Discard changes</button>' +
      '    <button type="button" class="mir-btn" data-role="notice-reload">Reload from disk</button>' +
      '  </div>' +
      '  <h3 class="bc-subhead">Preview</h3>' +
      '  <div class="bc-notice-preview" data-role="notice-preview"></div>' +
      '</div>' +
      '</div>';
  }

  function wire(sectionEl) {
    el('message').addEventListener('input', renderPreview);
    el('type').addEventListener('change', renderPreview);
    el('send').addEventListener('click', async function (e) {
      var j = await send(el('message').value, el('type').value, e.currentTarget);
      if (j && j.__ok) { el('message').value = ''; renderPreview(); }
    });
    el('clear').addEventListener('click', function () { el('message').value = ''; renderPreview(); });
    el('refresh').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, loadRecent); });

    el('recent').addEventListener('click', function (e) {
      var t = e.target;
      var host = el('recent');
      var entries = host._entries || [];
      var resend = t.closest ? t.closest('[data-bc-resend]') : null;
      if (resend) {
        var en = entries[Number(resend.getAttribute('data-bc-resend'))];
        if (en) send(en.message, en.type || 'announcement', resend);
        return;
      }
      var edit = t.closest ? t.closest('[data-bc-edit]') : null;
      if (edit) {
        var ed = entries[Number(edit.getAttribute('data-bc-edit'))];
        if (ed) {
          el('message').value = ed.message || '';
          if (ed.type) el('type').value = ed.type;
          renderPreview();
          el('message').focus();
        }
      }
    });

    el('notice').addEventListener('input', function () {
      setDirty(!notice || el('notice').value !== (notice.content || ''));
      renderNoticePreview();
    });
    el('notice-save').addEventListener('click', function (e) { saveNotice(e.currentTarget); });
    el('notice-revert').addEventListener('click', async function () {
      if (!await portalConfirm('Discard your unsaved notice changes?', { title: 'Discard', confirmLabel: 'Discard', danger: true })) return;
      el('notice').value = notice ? notice.content || '' : '';
      renderNoticePreview();
      setDirty(false);
    });
    el('notice-reload').addEventListener('click', async function (e) {
      if (noticeDirty && !await portalConfirm('Reload the notice from disk and lose your unsaved changes?', { title: 'Reload', confirmLabel: 'Reload', danger: true })) return;
      OpsUI.withBusy(e.currentTarget, function () { return loadNotice(true); });
    });
  }

  function updateOnline() {
    var n = onlineCount();
    var o = el('online');
    if (o) o.textContent = n == null ? '' : n + ' player' + (n === 1 ? '' : 's') + ' online';
  }

  global.AdminSections.register('broadcast', {
    load: function (sectionEl) {
      root = sectionEl;
      render(sectionEl);
      wire(sectionEl);
      renderPreview();
      if (global.AdminStatus) AdminStatus.onChange(updateOnline);
      return loadNotice(true);
    },
    show: function () {
      updateOnline();
      loadRecent();
      if (!noticeDirty) loadNotice();
      if (!stopPoll) stopPoll = global.AdminSections.poll('broadcast', loadRecent, 30000);
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
    }
  });

  portalRegisterDeepLink('broadcast', function (p) {
    if (!root || !p || !p.message) return;
    var box = el('message');
    if (box && !box.value) { box.value = String(p.message); renderPreview(); }
  });

  global.BroadcastPage = { linesOf: linesOf, parseNotice: parseNotice, send: send };
})(window);
