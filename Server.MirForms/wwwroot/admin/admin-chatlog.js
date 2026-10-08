/*
 * Chat Log (#section-chatlog).
 *
 * Live mode polls GET /api/admin/chatlog?after=<seq> every 2 s while the section and the tab are visible, appending only
 * new lines (no redraw, scroll position kept unless the reader is at the bottom with auto-scroll on). History mode reads a
 * day's log4net chat file through GET /api/admin/chatlog/history?date=yyyy-MM-dd.
 *
 * Filters: channel chips, name (sender or target), text. Each line has inline actions (Chat ban…, Kick…, Filter, Character)
 * that reuse the GM Console's dialogs (GmConsole.runFor). Deep link: #section=chatlog&name=<character> (player= also works).
 */
(function (global) {
  'use strict';

  var esc = function (s) { return OpsUI.esc(s); };
  var POLL_MS = 2000;
  var MAX_ROWS = 2000;
  var CHANNELS = ['normal', 'shout', 'shout-map', 'shout-server', 'whisper', 'group', 'guild', 'mentor', 'relationship', 'announcement', 'broadcast', 'admin'];
  var HISTORY_CHANNELS = ['command', 'system'];

  var root = null;
  var mode = 'live';
  var selected = Object.create(null);   // channel -> true (empty = all)
  var lines = [];                       // shown lines, oldest first
  var lastSeq = 0;
  var loadedKey = null;                 // filter key the live lines belong to
  var paused = false;
  var stopPoll = null;
  var busy = false;
  var gen = 0;                          // bumped by every live reload / mode change; stale responses are dropped

  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }

  function selectedChannels() { return Object.keys(selected); }

  function filterParams() {
    var p = [];
    var ch = selectedChannels();
    if (ch.length) p.push('channel=' + encodeURIComponent(ch.join(',')));
    var name = el('name').value.trim();
    var q = el('q').value.trim();
    if (name) p.push('name=' + encodeURIComponent(name));
    if (q) p.push('q=' + encodeURIComponent(q));
    return p;
  }

  function filterKey() { return mode + '|' + filterParams().join('&'); }

  function timeText(utc) {
    var d = OpsUI.toDate(utc);
    if (!d) return '';
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  function isPlayerName(name) {
    return !!name && name.charAt(0) !== '[' && !/\s/.test(name);
  }

  function rowHtml(l) {
    var actions = '';
    if (isPlayerName(l.sender)) {
      actions =
        '<span class="cl-actions">' +
        '<button type="button" class="mir-btn ops-row-btn" data-cl="chatban" data-name="' + esc(l.sender) + '" title="Chat ban ' + esc(l.sender) + '">Chat ban…</button>' +
        '<button type="button" class="mir-btn danger ops-row-btn" data-cl="kick" data-name="' + esc(l.sender) + '" title="Kick ' + esc(l.sender) + '">Kick…</button>' +
        '<button type="button" class="ops-link" data-cl="filter" data-name="' + esc(l.sender) + '" title="Show only this player">filter</button>' +
        '<button type="button" class="ops-link" data-cl="inspect" data-name="' + esc(l.sender) + '" title="Open the character">character</button>' +
        '</span>';
    }
    var who = isPlayerName(l.sender) ? OpsUI.playerLink(l.sender) : '<span class="mir-muted">' + esc(l.sender || '') + '</span>';
    if (l.target) who += ' <span class="mir-muted">→</span> ' + (isPlayerName(l.target) ? OpsUI.playerLink(l.target) : esc(l.target));
    return '<div class="ops-row cl-row" data-seq="' + esc(l.seq) + '">' +
      '<span class="ops-row-time" title="' + esc(OpsUI.fmtDate(l.utc)) + '">' + esc(timeText(l.utc)) + '</span>' +
      '<span class="ops-row-tag cl-ch cl-ch-' + esc(String(l.channel || '').replace(/[^a-z-]/gi, '')) + '">' + esc(l.channel) + '</span>' +
      '<span class="ops-row-who">' + who + '</span>' +
      '<span class="ops-row-text">' + esc(l.message) + '</span>' +
      actions +
      '</div>';
  }

  function atBottom(feed) {
    return feed.scrollHeight - feed.scrollTop - feed.clientHeight < 40;
  }

  function renderAll(note) {
    var feed = el('feed');
    if (!lines.length) {
      feed.innerHTML = '<p class="mir-muted cl-empty">' + esc(note || (mode === 'live' ? 'No chat yet. New lines appear here as players talk.' : 'No chat matched.')) + '</p>';
    } else {
      feed.innerHTML = lines.map(rowHtml).join('');
      if (mode === 'history' || el('autoscroll').checked) feed.scrollTop = feed.scrollHeight;
    }
    updateStatus();
  }

  function appendLines(fresh) {
    if (!fresh.length) return;
    var feed = el('feed');
    var stick = el('autoscroll').checked && atBottom(feed);
    if (!lines.length) feed.innerHTML = '';

    lines = lines.concat(fresh);
    var holder = document.createElement('div');
    holder.innerHTML = fresh.map(rowHtml).join('');
    while (holder.firstChild) feed.appendChild(holder.firstChild);

    if (lines.length > MAX_ROWS) {
      var drop = lines.length - MAX_ROWS;
      lines = lines.slice(drop);
      var keepTop = feed.scrollTop, before = feed.scrollHeight;
      for (var i = 0; i < drop && feed.firstChild; i++) feed.removeChild(feed.firstChild);
      if (!stick) feed.scrollTop = Math.max(0, keepTop - (before - feed.scrollHeight));
    }
    if (stick) feed.scrollTop = feed.scrollHeight;
    updateStatus();
  }

  function updateStatus(extra) {
    var s = el('status');
    if (!s) return;
    var parts = [lines.length + ' line' + (lines.length === 1 ? '' : 's')];
    if (mode === 'live') parts.push(paused ? 'paused' : 'live · updated ' + new Date().toLocaleTimeString());
    if (extra) parts.push(extra);
    s.textContent = parts.join(' · ');
  }

  // ---- live ---------------------------------------------------------------------------------------------------------

  async function reloadLive() {
    var my = ++gen;
    var key = filterKey();
    var r = await OpsUI.fetchResult('/api/admin/chatlog?' + filterParams().concat(['limit=500']).join('&'));
    if (my !== gen) return;               // a newer reload (or a mode change) superseded this one
    if (!r.ok) { updateStatus('load failed: ' + r.error); return; }
    if (mode !== 'live') return;
    lines = (r.data.lines || []).slice();
    lastSeq = Number(r.data.last) || 0;
    loadedKey = key;
    renderAll();
  }

  async function pollLive() {
    if (mode !== 'live' || paused || busy) return;
    busy = true;
    try {
      if (loadedKey !== filterKey()) { await reloadLive(); return; }
      var my = gen;
      var r = await OpsUI.fetchResult('/api/admin/chatlog?' + filterParams().concat(['after=' + lastSeq, 'limit=500']).join('&'));
      if (!r.ok) { updateStatus('refresh failed: ' + r.error); return; }
      if (my !== gen || mode !== 'live' || loadedKey !== filterKey()) return;   // a reload ran meanwhile: drop
      var j = r.data || {};
      if (j.reset) { await reloadLive(); return; }
      appendLines(j.lines || []);
      lastSeq = Number(j.last) || lastSeq;
      if (j.gap) updateStatus('some older lines were dropped from the server buffer');
      if (j.more) setTimeout(pollLive, 50);
    } finally {
      busy = false;
    }
  }

  // ---- history ------------------------------------------------------------------------------------------------------

  async function loadDates() {
    var j = await OpsUI.get('/api/admin/chatlog/dates');
    var sel = el('date');
    if (!sel) return;
    var keep = sel.value;
    var dates = (j && j.dates) || [];
    var today = new Date();
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    var todayText = today.getFullYear() + '-' + pad(today.getMonth() + 1) + '-' + pad(today.getDate());
    if (!dates.some(function (d) { return d.date === todayText; })) dates.unshift({ date: todayText, size: 0 });
    sel.innerHTML = dates.map(function (d) {
      return '<option value="' + esc(d.date) + '">' + esc(d.date) + (d.size ? ' (' + esc(Math.max(1, Math.round(d.size / 1024))) + ' KB)' : '') + '</option>';
    }).join('');
    if (keep && dates.some(function (d) { return d.date === keep; })) sel.value = keep;
  }

  async function loadHistory(button) {
    var date = el('date').value;
    if (!date) return;
    var url = '/api/admin/chatlog/history?date=' + encodeURIComponent(date) + '&limit=5000' + (filterParams().length ? '&' + filterParams().join('&') : '');
    await OpsUI.withBusy(button, async function () {
      var r = await OpsUI.fetchResult(url);
      if (mode !== 'history') return;
      if (!r.ok) { lines = []; renderAll('Could not read that day: ' + r.error); return; }
      var j = r.data || {};
      lines = j.lines || [];
      loadedKey = filterKey() + '|' + date;
      renderAll(j.exists === false ? 'There is no chat log file for ' + date + '.' : 'No chat matched.');
      if (j.truncated) updateStatus('showing the newest ' + lines.length + ' of ' + j.total + ' matching lines');
    }, 'Loading…');
  }

  // ---- modes / filters ----------------------------------------------------------------------------------------------

  function setMode(m) {
    mode = m;
    root.querySelectorAll('[data-mode]').forEach(function (b) {
      b.classList.toggle('active', b.getAttribute('data-mode') === m);
      b.setAttribute('aria-pressed', b.getAttribute('data-mode') === m ? 'true' : 'false');
    });
    el('live-tools').classList.toggle('hidden', m !== 'live');
    el('history-tools').classList.toggle('hidden', m !== 'history');
    renderChips();
    lines = [];
    loadedKey = null;
    gen++;
    if (m === 'live') { renderAll('Loading…'); reloadLive(); }
    else { renderAll('Pick a day and press Load.'); loadDates(); }
  }

  function renderChips() {
    var host = el('chips');
    var list = mode === 'history' ? CHANNELS.filter(function (c) { return c !== 'broadcast' && c !== 'admin' && c !== 'shout-map' && c !== 'shout-server'; }).concat(HISTORY_CHANNELS) : CHANNELS;
    Object.keys(selected).forEach(function (k) { if (list.indexOf(k) < 0) delete selected[k]; });
    var none = !selectedChannels().length;
    host.innerHTML = '<button type="button" class="cl-chip' + (none ? ' active' : '') + '" data-chip="" aria-pressed="' + none + '">All</button>' +
      list.map(function (c) {
        var on = !!selected[c];
        return '<button type="button" class="cl-chip cl-ch-' + esc(c) + (on ? ' active' : '') + '" data-chip="' + esc(c) + '" aria-pressed="' + on + '">' + esc(c) + '</button>';
      }).join('');
  }

  function filtersChanged() {
    if (mode === 'live') reloadLive();
    else if (lines.length) loadHistory(el('load'));
  }

  var filterTimer = null;
  function scheduleFilter() {
    if (filterTimer) clearTimeout(filterTimer);
    filterTimer = setTimeout(function () {
      portalSetHashParams({ name: el('name').value.trim() || null, player: null }, true);
      filtersChanged();
    }, 350);
  }

  function exportCsv() {
    if (!lines.length) { portalToast('Nothing to export.', 'error'); return; }
    var rows = lines.map(function (l) { return [OpsUI.fmtDate(l.utc), l.channel, l.sender, l.target || '', l.message]; });
    var stamp = mode === 'history' ? el('date').value : new Date().toISOString().slice(0, 10);
    OpsUI.download('chatlog-' + stamp + '.csv', OpsUI.toCsv(['time', 'channel', 'sender', 'target', 'message'], rows), 'text/csv;charset=utf-8');
  }

  async function lineAction(kind, name, button) {
    if (!name) return;
    switch (kind) {
      case 'filter':
        el('name').value = name;
        portalSetHashParams({ name: name, player: null }, true);
        filtersChanged();
        return;
      case 'inspect':
        portalOpenDeepLink('characters', { q: name });
        return;
      case 'chatban':
      case 'kick':
        if (!global.GmConsole) { portalToast('The GM console is not loaded.', 'error'); return; }
        await GmConsole.runFor(kind, { character: name }, {}, button);
        return;
    }
  }

  // ---- section ------------------------------------------------------------------------------------------------------

  function render(sectionEl) {
    sectionEl.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Chat Log</h2>' +
      '    <div class="cl-modes" role="group" aria-label="Mode">' +
      '      <button type="button" class="mir-btn active" data-mode="live" aria-pressed="true">Live</button>' +
      '      <button type="button" class="mir-btn" data-mode="history" aria-pressed="false">History</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="cl-chips" data-role="chips" aria-label="Channels"></div>' +
      '  <div class="ops-form">' +
      '    <label>Name (sender or target)<input type="text" class="fd-input" data-role="name" placeholder="Character" autocomplete="off" /></label>' +
      '    <label>Text<input type="text" class="fd-input" data-role="q" placeholder="Contains…" autocomplete="off" /></label>' +
      '    <span data-role="live-tools" class="cl-tools">' +
      '      <label class="mir-toggle"><input type="checkbox" data-role="pause" /> Pause</label>' +
      '      <label class="mir-toggle"><input type="checkbox" data-role="autoscroll" checked /> Auto-scroll</label>' +
      '      <button type="button" class="mir-btn" data-role="clear-view">Clear view</button>' +
      '    </span>' +
      '    <span data-role="history-tools" class="cl-tools hidden">' +
      '      <label>Day<select class="fd-input" data-role="date"></select></label>' +
      '      <button type="button" class="mir-btn primary" data-role="load">Load</button>' +
      '    </span>' +
      '    <button type="button" class="mir-btn" data-role="export">Export CSV</button>' +
      '  </div>' +
      '  <p class="mir-muted cl-status" data-role="status"></p>' +
      '  <div class="ops-log cl-feed" data-role="feed" aria-live="off"></div>' +
      '</div>';
  }

  function wire(sectionEl) {
    sectionEl.addEventListener('click', function (e) {
      var t = e.target;
      var m = t.closest ? t.closest('[data-mode]') : null;
      if (m) { if (m.getAttribute('data-mode') !== mode) setMode(m.getAttribute('data-mode')); return; }

      var chip = t.closest ? t.closest('[data-chip]') : null;
      if (chip) {
        var c = chip.getAttribute('data-chip');
        if (!c) selected = Object.create(null);
        else if (selected[c]) delete selected[c];
        else selected[c] = true;
        renderChips();
        filtersChanged();
        return;
      }

      var act = t.closest ? t.closest('[data-cl]') : null;
      if (act) lineAction(act.getAttribute('data-cl'), act.getAttribute('data-name'), act);
    });

    el('name').addEventListener('input', scheduleFilter);
    el('q').addEventListener('input', scheduleFilter);
    el('pause').addEventListener('change', function () {
      paused = !!el('pause').checked;
      updateStatus();
      if (!paused) pollLive();
    });
    el('clear-view').addEventListener('click', function () { lines = []; renderAll('View cleared. New lines appear here.'); });
    el('export').addEventListener('click', exportCsv);
    el('load').addEventListener('click', function (e) { loadHistory(e.currentTarget); });
    el('date').addEventListener('change', function () { loadHistory(el('load')); });
  }

  function applyDeepLink(p) {
    if (!root || !p) return;
    var name = p.name || p.player;
    if (name == null) return;
    var input = el('name');
    if (input.value.trim() === String(name)) return;
    input.value = String(name);
    filtersChanged();
  }

  global.AdminSections.register('chatlog', {
    load: function (sectionEl, params) {
      root = sectionEl;
      render(sectionEl);
      wire(sectionEl);
      renderChips();
      var name = params && (params.name || params.player);
      if (name) el('name').value = String(name);
      renderAll('Loading…');
    },
    show: function () {
      if (mode === 'live') reloadLive();
      if (!stopPoll) stopPoll = global.AdminSections.poll('chatlog', pollLive, POLL_MS);
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
    }
  });

  portalRegisterDeepLink('chatlog', applyDeepLink);

  global.ChatLogPage = {
    show: function (name) { portalOpenDeepLink('chatlog', { name: name || '' }); },
    lines: function () { return lines.slice(); }
  };
})(window);
